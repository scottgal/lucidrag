using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mostlylucid.DataSummarizer.Models;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Stores and retrieves data profiles with content hashing for deduplication and drift detection.
/// Profiles are stored as JSON files with computed hashes for:
/// - Content hash: Exact file content (skip re-profiling if unchanged)
/// - Schema hash: Column names + types (detect schema changes)
/// - Statistical signature: Normalized stats for similarity matching
/// </summary>
public class ProfileStore
{
    private readonly string _storePath;
    private readonly ProfileIndex _index;
    private readonly string _indexPath;
    private readonly JsonSerializerOptions _jsonOptions;

    public ProfileStore(string? storePath = null)
    {
        _storePath = storePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataSummarizer", "profiles");
        
        Directory.CreateDirectory(_storePath);
        _indexPath = Path.Combine(_storePath, "index.json");
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        
        _index = LoadIndex();
    }

    /// <summary>
    /// Store a profile with computed hashes
    /// </summary>
    public StoredProfileInfo Store(DataProfile profile, string? contentHash = null, SegmentInfo? segment = null)
    {
        // Compute content hash and size (works for both files and databases)
        var (hash, size) = contentHash != null 
            ? (contentHash, File.Exists(profile.SourcePath) ? new FileInfo(profile.SourcePath).Length : profile.RowCount)
            : ComputeSourceHash(profile);
            
        var info = new StoredProfileInfo
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            SourcePath = profile.SourcePath,
            FileName = Path.GetFileName(profile.SourcePath),
            StoredAt = DateTime.UtcNow,
            RowCount = profile.RowCount,
            ColumnCount = profile.ColumnCount,
            ContentHash = hash,
            FileSize = size,
            SchemaHash = ComputeSchemaHash(profile),
            StatisticalSignature = ComputeStatisticalSignature(profile),
            ProfileTime = profile.ProfileTime,
            SegmentName = segment?.Name,
            SegmentFilter = segment?.Filter,
            SegmentGroup = segment?.Group
        };

        // Compute centroid vector for similarity matching
        var segmentProfiler = new SegmentProfiler();
        var centroid = segmentProfiler.ComputeCentroid(profile, segment?.Name);
        info.CentroidVector = centroid.Vector;

        // Save full profile
        var profilePath = Path.Combine(_storePath, $"{info.Id}.json");
        var json = JsonSerializer.Serialize(profile, _jsonOptions);
        File.WriteAllText(profilePath, json);
        info.ProfilePath = profilePath;

        // Update index
        _index.Profiles[info.Id] = info;
        _index.ByContentHash[info.ContentHash] = info.Id;
        _index.BySchemaHash.TryAdd(info.SchemaHash, []);
        _index.BySchemaHash[info.SchemaHash].Add(info.Id);
        
        // Index by segment group if provided
        if (!string.IsNullOrEmpty(segment?.Group))
        {
            _index.BySegmentGroup.TryAdd(segment.Group, []);
            _index.BySegmentGroup[segment.Group].Add(info.Id);
        }
        
        SaveIndex();
        
        return info;
    }
    
    /// <summary>
    /// Store a segment profile (filtered subset of data)
    /// </summary>
    public StoredProfileInfo StoreSegment(
        DataProfile profile, 
        string segmentName, 
        string? filter = null,
        string? groupId = null)
    {
        var segment = new SegmentInfo
        {
            Name = segmentName,
            Filter = filter,
            Group = groupId ?? $"{Path.GetFileNameWithoutExtension(profile.SourcePath)}_segments"
        };
        return Store(profile, segment: segment);
    }
    
    /// <summary>
    /// Get all segments in a group
    /// </summary>
    public List<StoredProfileInfo> GetSegmentGroup(string groupId)
    {
        if (!_index.BySegmentGroup.TryGetValue(groupId, out var ids))
            return [];
            
        return ids
            .Where(id => _index.Profiles.ContainsKey(id))
            .Select(id => _index.Profiles[id])
            .OrderBy(p => p.SegmentName)
            .ToList();
    }
    
    /// <summary>
    /// Find the most similar profile using centroid distance
    /// </summary>
    public (StoredProfileInfo? Profile, double Distance) FindMostSimilarByCentroid(DataProfile profile)
    {
        var segmentProfiler = new SegmentProfiler();
        var centroid = segmentProfiler.ComputeCentroid(profile);
        
        StoredProfileInfo? bestMatch = null;
        double bestDistance = double.MaxValue;
        
        foreach (var (id, info) in _index.Profiles)
        {
            if (info.CentroidVector == null || info.CentroidVector.Length == 0)
                continue;
                
            var distance = ComputeEuclideanDistance(centroid.Vector, info.CentroidVector);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestMatch = info;
            }
        }
        
        return (bestMatch, bestDistance);
    }
    
    /// <summary>
    /// Find profiles within a distance threshold of the given profile
    /// </summary>
    public List<(StoredProfileInfo Profile, double Distance)> FindWithinDistance(
        DataProfile profile, 
        double maxDistance = 0.5)
    {
        var segmentProfiler = new SegmentProfiler();
        var centroid = segmentProfiler.ComputeCentroid(profile);
        var results = new List<(StoredProfileInfo, double)>();
        
        foreach (var (id, info) in _index.Profiles)
        {
            if (info.CentroidVector == null || info.CentroidVector.Length == 0)
                continue;
                
            var distance = ComputeEuclideanDistance(centroid.Vector, info.CentroidVector);
            if (distance <= maxDistance)
            {
                results.Add((info, distance));
            }
        }
        
        return results.OrderBy(r => r.Item2).ToList();
    }
    
    private static double ComputeEuclideanDistance(double[] a, double[] b)
    {
        if (a.Length != b.Length)
        {
            // Pad shorter vector with zeros
            var maxLen = Math.Max(a.Length, b.Length);
            var aPadded = new double[maxLen];
            var bPadded = new double[maxLen];
            Array.Copy(a, aPadded, a.Length);
            Array.Copy(b, bPadded, b.Length);
            a = aPadded;
            b = bPadded;
        }
        
        var sumSquares = a.Zip(b, (x, y) => (x - y) * (x - y)).Sum();
        return Math.Sqrt(sumSquares) / Math.Sqrt(a.Length); // Normalized by dimension
    }

    /// <summary>
    /// Check if we have a profile for this exact file content (by xxHash64).
    /// Very fast - use this to skip re-profiling unchanged files.
    /// </summary>
    public StoredProfileInfo? FindByContentHash(string filePath)
    {
        var hash = ComputeFileHash(filePath);
        if (_index.ByContentHash.TryGetValue(hash, out var id))
        {
            return _index.Profiles.GetValueOrDefault(id);
        }
        return null;
    }
    
    /// <summary>
    /// Quick check if file might have a cached profile (by size first, then hash).
    /// Returns null immediately if file size doesn't match any stored profile.
    /// This is the fastest path for detecting unchanged files.
    /// </summary>
    public StoredProfileInfo? QuickFindExisting(string filePath)
    {
        if (!File.Exists(filePath))
            return null;
            
        var fileInfo = new FileInfo(filePath);
        var fileSize = fileInfo.Length;
        
        // Quick pre-check: any profiles with matching file size?
        var candidates = _index.Profiles.Values
            .Where(p => p.FileSize == fileSize)
            .ToList();
            
        if (candidates.Count == 0)
            return null; // No size match, definitely new file
            
        // Size matches - compute hash to confirm (streaming for large files)
        var (hash, _) = ComputeStreamingHash(filePath);
        
        if (_index.ByContentHash.TryGetValue(hash, out var id))
        {
            return _index.Profiles.GetValueOrDefault(id);
        }
        
        return null;
    }
    
    /// <summary>
    /// Check if file is already profiled and return the existing profile if so.
    /// This is the fast path - uses xxHash64 to detect unchanged files.
    /// Returns (isExact, profile, baseline) where:
    /// - isExact: true if exact content match found (skip profiling)
    /// - profile: existing profile if found, null otherwise
    /// - baseline: baseline profile for drift comparison if schema matches
    /// </summary>
    public (bool IsExactMatch, DataProfile? ExistingProfile, DataProfile? Baseline) CheckFile(string filePath)
    {
        // Fast path: exact content match via xxHash64
        var existingInfo = FindByContentHash(filePath);
        if (existingInfo != null)
        {
            var existingProfile = LoadProfile(existingInfo.Id);
            if (existingProfile != null)
            {
                // Return existing profile and baseline (if different from existing)
                var baseline = LoadBaseline(existingProfile);
                var isBaseline = baseline != null && existingInfo.Id == GetBaselineId(existingProfile);
                return (true, existingProfile, isBaseline ? null : baseline);
            }
        }
        
        return (false, null, null);
    }
    
    /// <summary>
    /// Get the baseline profile ID for a given profile (oldest with same schema)
    /// </summary>
    private string? GetBaselineId(DataProfile profile)
    {
        var schemaHash = ComputeSchemaHash(profile);
        if (!_index.BySchemaHash.TryGetValue(schemaHash, out var ids) || ids.Count == 0)
            return null;
            
        return ids
            .Select(id => _index.Profiles[id])
            .OrderBy(p => p.StoredAt)
            .First().Id;
    }

    /// <summary>
    /// Find profiles with the same schema (column names + types)
    /// </summary>
    public List<StoredProfileInfo> FindBySchema(DataProfile profile)
    {
        var schemaHash = ComputeSchemaHash(profile);
        if (_index.BySchemaHash.TryGetValue(schemaHash, out var ids))
        {
            return ids.Select(id => _index.Profiles[id]).ToList();
        }
        return [];
    }

    /// <summary>
    /// Find similar profiles based on statistical signature (fuzzy matching)
    /// </summary>
    public List<SimilarProfile> FindSimilar(DataProfile profile, double minSimilarity = 0.7)
    {
        var signature = ComputeStatisticalSignature(profile);
        var results = new List<SimilarProfile>();

        foreach (var (id, info) in _index.Profiles)
        {
            var similarity = ComputeSignatureSimilarity(signature, info.StatisticalSignature);
            if (similarity >= minSimilarity)
            {
                results.Add(new SimilarProfile
                {
                    Info = info,
                    Similarity = similarity,
                    MatchType = DetermineMatchType(profile, info, similarity)
                });
            }
        }

        return results.OrderByDescending(r => r.Similarity).ToList();
    }

    /// <summary>
    /// Get the most recent profile for a file path pattern
    /// </summary>
    public StoredProfileInfo? GetLatestForPath(string pathPattern)
    {
        var pattern = pathPattern.ToLowerInvariant();
        return _index.Profiles.Values
            .Where(p => p.SourcePath.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                       p.FileName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.StoredAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// Get profile history for a logical dataset (same schema)
    /// </summary>
    public List<StoredProfileInfo> GetHistory(string schemaHash, int limit = 10)
    {
        if (!_index.BySchemaHash.TryGetValue(schemaHash, out var ids))
            return [];

        return ids
            .Select(id => _index.Profiles[id])
            .OrderByDescending(p => p.StoredAt)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Load a stored profile by ID
    /// </summary>
    public DataProfile? LoadProfile(string id)
    {
        if (!_index.Profiles.TryGetValue(id, out var info))
            return null;

        if (!File.Exists(info.ProfilePath))
            return null;

        var json = File.ReadAllText(info.ProfilePath);
        return JsonSerializer.Deserialize<DataProfile>(json, _jsonOptions);
    }

    /// <summary>
    /// Load the baseline profile for drift comparison
    /// Returns the pinned baseline if one exists, otherwise the oldest profile with matching schema
    /// Profiles marked as ExcludeFromBaseline are skipped
    /// </summary>
    public DataProfile? LoadBaseline(DataProfile current)
    {
        var schemaHash = ComputeSchemaHash(current);
        if (!_index.BySchemaHash.TryGetValue(schemaHash, out var ids) || ids.Count == 0)
            return null;

        var candidates = ids
            .Select(id => _index.Profiles[id])
            .Where(p => !p.ExcludeFromBaseline)
            .ToList();

        if (candidates.Count == 0)
            return null;

        // Check for pinned baseline first
        var pinned = candidates.FirstOrDefault(p => p.IsPinnedBaseline);
        if (pinned != null)
            return LoadProfile(pinned.Id);

        // Fall back to oldest profile with this schema
        var baselineId = candidates
            .OrderBy(p => p.StoredAt)
            .First().Id;

        return LoadProfile(baselineId);
    }

    /// <summary>
    /// List all stored profiles
    /// </summary>
    public List<StoredProfileInfo> ListAll(int limit = 100)
    {
        return _index.Profiles.Values
            .OrderByDescending(p => p.StoredAt)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Delete a stored profile
    /// </summary>
    public bool Delete(string id)
    {
        if (!_index.Profiles.TryGetValue(id, out var info))
            return false;

        // Remove from index
        _index.Profiles.Remove(id);
        _index.ByContentHash.Remove(info.ContentHash);
        if (_index.BySchemaHash.TryGetValue(info.SchemaHash, out var ids))
        {
            ids.Remove(id);
            if (ids.Count == 0)
                _index.BySchemaHash.Remove(info.SchemaHash);
        }

        // Delete file
        if (File.Exists(info.ProfilePath))
            File.Delete(info.ProfilePath);

        SaveIndex();
        return true;
    }

    /// <summary>
    /// Prune old profiles, keeping only the most recent N per schema
    /// </summary>
    public int Prune(int keepPerSchema = 5)
    {
        var pruned = 0;
        
        foreach (var (schemaHash, ids) in _index.BySchemaHash.ToList())
        {
            if (ids.Count <= keepPerSchema) continue;

            var toDelete = ids
                .Select(id => _index.Profiles[id])
                .Where(p => !p.IsPinnedBaseline) // Never prune pinned baselines
                .OrderByDescending(p => p.StoredAt)
                .Skip(keepPerSchema)
                .Select(p => p.Id)
                .ToList();

            foreach (var id in toDelete)
            {
                Delete(id);
                pruned++;
            }
        }

        return pruned;
    }
    
    /// <summary>
    /// Alias for Prune() for backward compatibility
    /// </summary>
    public int PruneOldProfiles(int keepPerSchema = 5) => Prune(keepPerSchema);
    
    /// <summary>
    /// Clear all stored profiles (for testing or fresh start)
    /// </summary>
    public int ClearAll()
    {
        var count = _index.Profiles.Count;
        
        // Delete all profile files
        foreach (var info in _index.Profiles.Values)
        {
            if (File.Exists(info.ProfilePath))
            {
                try { File.Delete(info.ProfilePath); } catch { /* ignore */ }
            }
        }
        
        // Clear indexes
        _index.Profiles.Clear();
        _index.ByContentHash.Clear();
        _index.BySchemaHash.Clear();
        _index.BySegmentGroup.Clear();
        
        SaveIndex();
        return count;
    }
    
    /// <summary>
    /// Get store statistics
    /// </summary>
    public StoreStats GetStats()
    {
        var profiles = _index.Profiles.Values.ToList();
        return new StoreStats
        {
            TotalProfiles = profiles.Count,
            TotalSizeBytes = profiles.Sum(p => p.FileSize),
            UniqueSchemas = _index.BySchemaHash.Count,
            SegmentGroups = _index.BySegmentGroup.Count,
            OldestProfile = profiles.MinBy(p => p.StoredAt)?.StoredAt,
            NewestProfile = profiles.MaxBy(p => p.StoredAt)?.StoredAt,
            StorePath = _storePath
        };
    }
    
    /// <summary>
    /// Update metadata for a stored profile (tags, notes, flags)
    /// </summary>
    public void UpdateMetadata(StoredProfileInfo info)
    {
        if (!_index.Profiles.ContainsKey(info.Id))
            throw new ArgumentException($"Profile {info.Id} not found");
            
        // Update the profile in the index
        _index.Profiles[info.Id] = info;
        
        // If pinning this as baseline, unpin any other baselines with same schema
        if (info.IsPinnedBaseline && _index.BySchemaHash.TryGetValue(info.SchemaHash, out var ids))
        {
            foreach (var id in ids.Where(id => id != info.Id))
            {
                if (_index.Profiles.TryGetValue(id, out var other) && other.IsPinnedBaseline)
                {
                    other.IsPinnedBaseline = false;
                }
            }
        }
        
        SaveIndex();
    }
    
    /// <summary>
    /// Get detailed store statistics for interactive menu
    /// </summary>
    public StoreStatistics GetStatistics()
    {
        var profiles = _index.Profiles.Values.ToList();
        var totalDiskUsage = 0.0;
        
        foreach (var profile in profiles)
        {
            if (File.Exists(profile.ProfilePath))
            {
                totalDiskUsage += new FileInfo(profile.ProfilePath).Length;
            }
        }
        
        return new StoreStatistics
        {
            TotalProfiles = profiles.Count,
            UniqueSchemas = _index.BySchemaHash.Count,
            TotalRowsProfiled = profiles.Sum(p => p.RowCount),
            TotalDiskUsageMB = totalDiskUsage / (1024.0 * 1024.0),
            OldestProfile = profiles.MinBy(p => p.StoredAt)?.StoredAt,
            NewestProfile = profiles.MaxBy(p => p.StoredAt)?.StoredAt
        };
    }

    #region Hash Computation

    /// <summary>
    /// Buffer size for streaming hash (1MB chunks for efficient large file handling)
    /// </summary>
    private const int HashBufferSize = 1024 * 1024;

    /// <summary>
    /// Compute xxHash64 of file content using streaming (handles multi-GB files efficiently).
    /// Very fast - ~3GB/s on modern hardware.
    /// Used to skip re-profiling if file hasn't changed.
    /// </summary>
    public static string ComputeFileHash(string filePath)
    {
        if (!File.Exists(filePath))
            return "unknown";

        return ComputeStreamingHash(filePath).Hash;
    }
    
    /// <summary>
    /// Compute xxHash64 of file content with file size for quick validation.
    /// Uses streaming for memory-efficient hashing of large files.
    /// Returns (hash, fileSize) tuple.
    /// </summary>
    public static (string Hash, long Size) ComputeFileHashWithSize(string filePath)
    {
        if (!File.Exists(filePath))
            return ("unknown", 0);

        return ComputeStreamingHash(filePath);
    }
    
    /// <summary>
    /// Streaming xxHash64 computation - handles files of any size with constant memory.
    /// Reads file in 1MB chunks, processes incrementally.
    /// </summary>
    private static (string Hash, long Size) ComputeStreamingHash(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var size = fileInfo.Length;
        
        // Use incremental hashing with buffered reads (works for any size)
        var hasher = new XxHash64();
        var buffer = new byte[HashBufferSize];
        
        using var stream = new FileStream(
            filePath, 
            FileMode.Open, 
            FileAccess.Read, 
            FileShare.Read,
            bufferSize: HashBufferSize,
            useAsync: false); // Sync is faster for sequential reads
        
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hasher.Append(buffer.AsSpan(0, bytesRead));
        }
        
        var hashValue = hasher.GetCurrentHashAsUInt64();
        var hashHex = hashValue.ToString("x16"); // 16 hex chars for 64-bit hash
        
        return (hashHex, size);
    }
    
    /// <summary>
    /// Async streaming hash for very large files (use when I/O bound)
    /// </summary>
    public static async Task<(string Hash, long Size)> ComputeFileHashAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            return ("unknown", 0);
            
        var fileInfo = new FileInfo(filePath);
        var size = fileInfo.Length;
        
        var hasher = new XxHash64();
        var buffer = new byte[HashBufferSize];
        
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: HashBufferSize,
            useAsync: true);
        
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
        {
            hasher.Append(buffer.AsSpan(0, bytesRead));
        }
        
        var hashValue = hasher.GetCurrentHashAsUInt64();
        var hashHex = hashValue.ToString("x16");
        
        return (hashHex, size);
    }
    
    /// <summary>
    /// Compute a content hash for database sources (tables/queries).
    /// Since we can't hash the raw bytes, we hash a fingerprint consisting of:
    /// - Row count
    /// - Schema (column names + types)
    /// - Sample of first/last rows (for change detection)
    /// - Aggregate stats (sum, min, max of numeric columns)
    /// </summary>
    public static string ComputeDatabaseHash(DataProfile profile)
    {
        var hasher = new XxHash64();
        
        // Include row count
        hasher.Append(BitConverter.GetBytes(profile.RowCount));
        
        // Include schema
        foreach (var col in profile.Columns.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            hasher.Append(Encoding.UTF8.GetBytes(col.Name.ToLowerInvariant()));
            hasher.Append(Encoding.UTF8.GetBytes(col.InferredType.ToString()));
            
            // Include key stats that would change if data changes
            if (col.Min.HasValue)
                hasher.Append(BitConverter.GetBytes(col.Min.Value));
            if (col.Max.HasValue)
                hasher.Append(BitConverter.GetBytes(col.Max.Value));
            if (col.Mean.HasValue)
                hasher.Append(BitConverter.GetBytes(col.Mean.Value));
            hasher.Append(BitConverter.GetBytes(col.NullCount));
            hasher.Append(BitConverter.GetBytes(col.UniqueCount));
        }
        
        var hashValue = hasher.GetCurrentHashAsUInt64();
        return $"db:{hashValue:x16}"; // Prefix with "db:" to distinguish from file hashes
    }
    
    /// <summary>
    /// Compute content hash for any source type (file or database)
    /// </summary>
    public static (string Hash, long Size) ComputeSourceHash(DataProfile profile)
    {
        // Check if source is a file
        if (File.Exists(profile.SourcePath))
        {
            return ComputeFileHashWithSize(profile.SourcePath);
        }
        
        // For database/query sources, compute fingerprint hash
        // Size is row count for databases
        return (ComputeDatabaseHash(profile), profile.RowCount);
    }

    /// <summary>
    /// Compute hash of schema (column names + types) for schema matching.
    /// Uses xxHash64 for speed.
    /// </summary>
    public static string ComputeSchemaHash(DataProfile profile)
    {
        var sb = new StringBuilder();
        foreach (var col in profile.Columns.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(col.Name.ToLowerInvariant());
            sb.Append(':');
            sb.Append(col.InferredType.ToString());
            sb.Append('|');
        }
        
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        Span<byte> hashBytes = stackalloc byte[8];
        XxHash64.Hash(bytes, hashBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Compute statistical signature for similarity matching.
    /// This is a normalized fingerprint of the data's statistical properties.
    /// </summary>
    public static StatisticalSignature ComputeStatisticalSignature(DataProfile profile)
    {
        var sig = new StatisticalSignature
        {
            RowCountBucket = GetRowCountBucket(profile.RowCount),
            ColumnCount = profile.ColumnCount,
            NumericColumnCount = profile.Columns.Count(c => c.InferredType == ColumnType.Numeric),
            CategoricalColumnCount = profile.Columns.Count(c => c.InferredType == ColumnType.Categorical),
            DateColumnCount = profile.Columns.Count(c => c.InferredType == ColumnType.DateTime),
            TextColumnCount = profile.Columns.Count(c => c.InferredType == ColumnType.Text),
            AvgNullPercent = profile.Columns.Average(c => c.NullPercent),
            AvgUniquePercent = profile.Columns.Average(c => c.UniquePercent),
            ColumnNames = profile.Columns.Select(c => c.Name.ToLowerInvariant()).ToList()
        };

        // Numeric column stats fingerprint
        var numericCols = profile.Columns.Where(c => c.InferredType == ColumnType.Numeric && c.Mean.HasValue).ToList();
        if (numericCols.Count > 0)
        {
            sig.NumericMeans = numericCols.Select(c => NormalizeStat(c.Mean!.Value)).ToList();
            sig.NumericStdDevs = numericCols.Where(c => c.StdDev.HasValue).Select(c => NormalizeStat(c.StdDev!.Value)).ToList();
        }

        // Categorical column cardinality fingerprint
        var catCols = profile.Columns.Where(c => c.InferredType == ColumnType.Categorical).ToList();
        if (catCols.Count > 0)
        {
            sig.CategoricalCardinalities = catCols.Select(c => (int)c.UniqueCount).ToList();
        }

        // Per-column signatures for drift detection
        foreach (var col in profile.Columns)
        {
            var colSig = new ColumnSignature
            {
                NormalizedName = col.Name.ToLowerInvariant().Trim(),
                Type = col.InferredType,
                NullPercent = col.NullPercent,
                UniquePercent = col.UniquePercent
            };

            if (col.InferredType == ColumnType.Numeric)
            {
                colSig.Mean = col.Mean;
                colSig.Median = col.Median;
                colSig.StdDev = col.StdDev;
                // MAD will be computed separately if needed
                colSig.Skewness = col.Skewness;
                colSig.Quantiles = new[] { col.Q25 ?? 0, col.Median ?? 0, col.Q75 ?? 0 };
                colSig.OutlierRatio = col.OutlierCount > 0 ? (double)col.OutlierCount / profile.RowCount : 0;
            }
            else if (col.InferredType == ColumnType.Categorical)
            {
                colSig.Cardinality = (int)col.UniqueCount;
                colSig.Entropy = col.Entropy;
                colSig.ImbalanceRatio = col.ImbalanceRatio;
                
                // Store top-K distribution for JS divergence calculation
                if (col.TopValues?.Count > 0)
                {
                    colSig.TopKDistribution = col.TopValues
                        .ToDictionary(
                            vc => vc.Value,
                            vc => vc.Percent / 100.0 // Already a percentage, convert to fraction
                        );
                }
            }

            sig.PerColumnStats[colSig.NormalizedName] = colSig;
        }

        return sig;
    }

    private static string GetRowCountBucket(long rowCount) => rowCount switch
    {
        < 100 => "tiny",
        < 1000 => "small",
        < 10000 => "medium",
        < 100000 => "large",
        < 1000000 => "xlarge",
        _ => "massive"
    };

    private static double NormalizeStat(double value)
    {
        // Log-scale normalization for better similarity matching
        if (value == 0) return 0;
        var sign = Math.Sign(value);
        return sign * Math.Log10(1 + Math.Abs(value));
    }

    /// <summary>
    /// Compute similarity between two statistical signatures (0-1)
    /// </summary>
    public static double ComputeSignatureSimilarity(StatisticalSignature a, StatisticalSignature b)
    {
        var scores = new List<double>();

        // Column count similarity (important)
        scores.Add(1.0 - Math.Min(1.0, Math.Abs(a.ColumnCount - b.ColumnCount) / 10.0));
        
        // Column type distribution similarity
        var typeScore = 1.0 - (
            Math.Abs(a.NumericColumnCount - b.NumericColumnCount) +
            Math.Abs(a.CategoricalColumnCount - b.CategoricalColumnCount) +
            Math.Abs(a.DateColumnCount - b.DateColumnCount) +
            Math.Abs(a.TextColumnCount - b.TextColumnCount)
        ) / (double)Math.Max(a.ColumnCount + b.ColumnCount, 1);
        scores.Add(Math.Max(0, typeScore));

        // Row count bucket match
        scores.Add(a.RowCountBucket == b.RowCountBucket ? 1.0 : 0.5);

        // Column name overlap (Jaccard similarity)
        var aNames = a.ColumnNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bNames = b.ColumnNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var intersection = aNames.Intersect(bNames, StringComparer.OrdinalIgnoreCase).Count();
        var union = aNames.Union(bNames, StringComparer.OrdinalIgnoreCase).Count();
        scores.Add(union > 0 ? (double)intersection / union : 0);

        // Null percentage similarity
        scores.Add(1.0 - Math.Min(1.0, Math.Abs(a.AvgNullPercent - b.AvgNullPercent) / 50.0));

        // Unique percentage similarity
        scores.Add(1.0 - Math.Min(1.0, Math.Abs(a.AvgUniquePercent - b.AvgUniquePercent) / 50.0));

        // Weight: column names most important, then types, then stats
        var weights = new[] { 0.15, 0.2, 0.1, 0.35, 0.1, 0.1 };
        return scores.Zip(weights, (s, w) => s * w).Sum();
    }

    private static ProfileMatchType DetermineMatchType(DataProfile current, StoredProfileInfo stored, double similarity)
    {
        if (similarity >= 0.95)
        {
            var currentSchema = ComputeSchemaHash(current);
            if (currentSchema == stored.SchemaHash)
                return ProfileMatchType.ExactSchema;
            return ProfileMatchType.HighSimilarity;
        }
        if (similarity >= 0.8)
            return ProfileMatchType.SimilarStructure;
        return ProfileMatchType.RelatedData;
    }

    #endregion

    #region Index Management

    private ProfileIndex LoadIndex()
    {
        if (!File.Exists(_indexPath))
            return new ProfileIndex();

        try
        {
            var json = File.ReadAllText(_indexPath);
            return JsonSerializer.Deserialize<ProfileIndex>(json, _jsonOptions) ?? new ProfileIndex();
        }
        catch
        {
            return new ProfileIndex();
        }
    }

    private void SaveIndex()
    {
        var json = JsonSerializer.Serialize(_index, _jsonOptions);
        File.WriteAllText(_indexPath, json);
    }

    #endregion
}

#region Models

/// <summary>
/// Index of all stored profiles for fast lookup
/// </summary>
public class ProfileIndex
{
    public Dictionary<string, StoredProfileInfo> Profiles { get; set; } = new();
    public Dictionary<string, string> ByContentHash { get; set; } = new(); // contentHash -> profileId
    public Dictionary<string, List<string>> BySchemaHash { get; set; } = new(); // schemaHash -> [profileIds]
    public Dictionary<string, List<string>> BySegmentGroup { get; set; } = new(); // segmentGroup -> [profileIds]
}

/// <summary>
/// Metadata about a stored profile
/// </summary>
public class StoredProfileInfo
{
    public string Id { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTime StoredAt { get; set; }
    public long RowCount { get; set; }
    public int ColumnCount { get; set; }
    
    /// <summary>
    /// xxHash64 of file content (16 hex chars) - very fast for exact match detection
    /// </summary>
    public string ContentHash { get; set; } = "";
    
    /// <summary>
    /// File size in bytes (for quick pre-check before hashing)
    /// </summary>
    public long FileSize { get; set; }
    
    /// <summary>
    /// Hash of column names + types (for schema matching)
    /// </summary>
    public string SchemaHash { get; set; } = "";
    
    /// <summary>
    /// Statistical fingerprint for similarity matching
    /// </summary>
    public StatisticalSignature StatisticalSignature { get; set; } = new();
    
    /// <summary>
    /// Path to the stored profile JSON
    /// </summary>
    public string ProfilePath { get; set; } = "";
    
    /// <summary>
    /// Time taken to profile
    /// </summary>
    public TimeSpan ProfileTime { get; set; }
    
    /// <summary>
    /// Segment name if this is a segment profile (e.g., "Q1-2024", "region=US")
    /// </summary>
    public string? SegmentName { get; set; }
    
    /// <summary>
    /// Segment filter expression used to create this segment (SQL WHERE clause)
    /// </summary>
    public string? SegmentFilter { get; set; }
    
    /// <summary>
    /// Group ID linking related segments (e.g., all quarterly segments of same dataset)
    /// </summary>
    public string? SegmentGroup { get; set; }
    
    /// <summary>
    /// Centroid vector for distance calculations (computed from profile stats)
    /// </summary>
    public double[]? CentroidVector { get; set; }
    
    /// <summary>
    /// If true, this profile is pinned as the baseline for drift comparison
    /// Only one profile per schema can be pinned
    /// </summary>
    public bool IsPinnedBaseline { get; set; }
    
    /// <summary>
    /// If true, exclude this profile from automatic baseline selection
    /// Useful for known-bad batches or outlier data
    /// </summary>
    public bool ExcludeFromBaseline { get; set; }
    
    /// <summary>
    /// Comma-separated tags for categorization (e.g., "production,validated,Q4-2024")
    /// </summary>
    public string? Tags { get; set; }
    
    /// <summary>
    /// User notes about this profile (e.g., "Known data quality issue", "Pre-migration baseline")
    /// </summary>
    public string? Notes { get; set; }
}

/// <summary>
/// Statistical signature for similarity matching and drift detection.
/// This is intentionally separate from the fingerprint (schema hash).
/// 
/// - Fingerprint (stable): column names + types - survives new batches
/// - Signature (changes): statistical properties - detects drift
/// 
/// Use fingerprint for "same dataset family" matching.
/// Use signature for drift detection within that family.
/// </summary>
public class StatisticalSignature
{
    // Dataset-level stats (for coarse matching)
    public string RowCountBucket { get; set; } = "";
    public int ColumnCount { get; set; }
    public int NumericColumnCount { get; set; }
    public int CategoricalColumnCount { get; set; }
    public int DateColumnCount { get; set; }
    public int TextColumnCount { get; set; }
    public double AvgNullPercent { get; set; }
    public double AvgUniquePercent { get; set; }
    
    // Normalized column names (for fuzzy schema matching)
    public List<string> ColumnNames { get; set; } = [];
    
    // Per-column signatures for drift detection
    public Dictionary<string, ColumnSignature> PerColumnStats { get; set; } = new();
    
    // Legacy aggregate stats (keep for compatibility)
    public List<double> NumericMeans { get; set; } = [];
    public List<double> NumericStdDevs { get; set; } = [];
    public List<int> CategoricalCardinalities { get; set; } = [];
}

/// <summary>
/// Per-column statistical signature for drift detection
/// </summary>
public class ColumnSignature
{
    public string NormalizedName { get; set; } = ""; // lowercase, trimmed
    public ColumnType Type { get; set; }
    
    // Data quality metrics
    public double NullPercent { get; set; }
    public double UniquePercent { get; set; }
    
    // Numeric-specific (for KS/Wasserstein approximation)
    public double? Mean { get; set; }
    public double? Median { get; set; }
    public double? StdDev { get; set; }
    public double? MAD { get; set; } // Median Absolute Deviation
    public double? Skewness { get; set; }
    public double[]? Quantiles { get; set; } // [Q25, Q50, Q75] for distribution comparison
    public double? OutlierRatio { get; set; }
    
    // Categorical-specific (for JS divergence)
    public int? Cardinality { get; set; }
    public Dictionary<string, double>? TopKDistribution { get; set; } // top-K value frequencies
    public double? Entropy { get; set; }
    public double? ImbalanceRatio { get; set; }
}

/// <summary>
/// Result of a similarity search
/// </summary>
public class SimilarProfile
{
    public StoredProfileInfo Info { get; set; } = new();
    public double Similarity { get; set; }
    public ProfileMatchType MatchType { get; set; }
}

/// <summary>
/// Type of profile match
/// </summary>
public enum ProfileMatchType
{
    /// <summary>Exact same schema (column names + types)</summary>
    ExactSchema,
    /// <summary>Very high similarity (>95%)</summary>
    HighSimilarity,
    /// <summary>Similar structure (80-95%)</summary>
    SimilarStructure,
    /// <summary>Related data (70-80%)</summary>
    RelatedData
}

/// <summary>
/// Information about a data segment
/// </summary>
public class SegmentInfo
{
    /// <summary>
    /// Human-readable segment name (e.g., "Q1-2024", "region=US", "churned=true")
    /// </summary>
    public string Name { get; set; } = "";
    
    /// <summary>
    /// SQL WHERE clause filter that defines this segment
    /// </summary>
    public string? Filter { get; set; }
    
    /// <summary>
    /// Group ID linking related segments together
    /// </summary>
    public string? Group { get; set; }
}

/// <summary>
/// Statistics about the profile store
/// </summary>
public class StoreStats
{
    public int TotalProfiles { get; set; }
    public long TotalSizeBytes { get; set; }
    public int UniqueSchemas { get; set; }
    public int SegmentGroups { get; set; }
    public DateTime? OldestProfile { get; set; }
    public DateTime? NewestProfile { get; set; }
    public string StorePath { get; set; } = "";
    
    public string TotalSizeFormatted => TotalSizeBytes switch
    {
        < 1024 => $"{TotalSizeBytes} B",
        < 1024 * 1024 => $"{TotalSizeBytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{TotalSizeBytes / (1024.0 * 1024):F1} MB",
        _ => $"{TotalSizeBytes / (1024.0 * 1024 * 1024):F2} GB"
    };
}

/// <summary>
/// Detailed statistics for interactive profile management menu
/// </summary>
public class StoreStatistics
{
    public int TotalProfiles { get; set; }
    public int UniqueSchemas { get; set; }
    public long TotalRowsProfiled { get; set; }
    public double TotalDiskUsageMB { get; set; }
    public DateTime? OldestProfile { get; set; }
    public DateTime? NewestProfile { get; set; }
}

#endregion
