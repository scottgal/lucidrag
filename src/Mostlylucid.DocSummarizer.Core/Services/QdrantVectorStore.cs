using System.IO.Hashing;
using System.Text;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;
using Qdrant.Client;
using Qdrant.Client.Grpc;


namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Qdrant-backed vector store for BertRag pipeline using official gRPC client.
/// Provides persistent storage - vectors survive between runs.
/// Requires Qdrant server to be running.
/// </summary>
public class QdrantVectorStore : IVectorStore
{
    private readonly QdrantClient _client;
    private readonly bool _verbose;
    private readonly bool _deleteOnDispose;
    private readonly int _vectorSize;
    private string? _activeCollection;
    
    public QdrantVectorStore(QdrantConfig config, bool verbose = false, bool deleteOnDispose = false)
    {
        _client = new QdrantClient(config.Host, config.Port, apiKey: config.ApiKey);
        _verbose = verbose;
        _deleteOnDispose = deleteOnDispose;
        _vectorSize = config.VectorSize;
    }
    
    public QdrantVectorStore(string host = "localhost", int port = 6334, string? apiKey = null, int vectorSize = 384, bool verbose = false, bool deleteOnDispose = false)
    {
        _client = new QdrantClient(host, port, apiKey: apiKey);
        _verbose = verbose;
        _deleteOnDispose = deleteOnDispose;
        _vectorSize = vectorSize;
    }
    
    public bool IsPersistent => true;
    
    public async Task InitializeAsync(string collectionName, int vectorSize, CancellationToken ct = default)
    {
        _activeCollection = collectionName;
        
        try
        {
            var collections = await _client.ListCollectionsAsync(ct);
            var exists = collections.Any(c => c == collectionName);
            
            if (!exists)
            {
                await _client.CreateCollectionAsync(
                    collectionName,
                    new VectorParams
                    {
                        Size = (ulong)vectorSize,
                        Distance = Distance.Cosine
                    },
                    cancellationToken: ct);
                
                if (_verbose)
                    VerboseHelper.Log($"[dim]Created Qdrant collection '{VerboseHelper.Escape(collectionName)}' (dim={vectorSize})[/]");
            }
            else if (_verbose)
            {
                VerboseHelper.Log($"[dim]Using existing Qdrant collection '{VerboseHelper.Escape(collectionName)}'[/]");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to initialize Qdrant collection '{collectionName}': {ex.Message}", ex);
        }
    }
    
    public async Task<bool> HasDocumentAsync(string collectionName, string docId, CancellationToken ct = default)
    {
        try
        {
            var collections = await _client.ListCollectionsAsync(ct);
            if (!collections.Any(c => c == collectionName))
                return false;

            // Extract content hash from docId (format: sanitizedFilename_contentHash)
            // Filter by docHash only - no filename/PII in vector DB
            var docHash = ExtractDocHash(docId);

            var scrollResult = await _client.ScrollAsync(
                collectionName,
                filter: new Filter
                {
                    Must =
                    {
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key = "docHash",
                                Match = new Match { Keyword = docHash }
                            }
                        }
                    }
                },
                limit: 1,
                cancellationToken: ct);

            return scrollResult.Result.Count > 0;
        }
        catch
        {
            return false;
        }
    }
    
    public async Task UpsertSegmentsAsync(string collectionName, IEnumerable<Segment> segments, CancellationToken ct = default)
    {
        var segmentList = segments.Where(s => s.Embedding != null).ToList();

        if (segmentList.Count == 0)
            return;

        // Convert segments to Qdrant points
        // NOTE: No text or PII stored in vector DB - only embeddings + non-PII metadata
        // Text is stored in evidence repository (PostgreSQL) keyed by contentHash
        var points = segmentList.Select(s => new PointStruct
        {
            Id = new PointId { Uuid = GenerateUuidFromId(s.ContentHash ?? s.Id) },
            Vectors = s.Embedding!,
            Payload =
            {
                // Use content hash as ID - no filename/PII leakage
                ["segmentHash"] = s.ContentHash ?? GenerateHashFromText(s.Text),
                ["docHash"] = ExtractDocHash(s.Id),
                // Non-PII metadata only
                ["type"] = s.Type.ToString(),
                ["index"] = s.Index,
                ["headingLevel"] = s.HeadingLevel,
                ["salienceScore"] = s.SalienceScore,
                ["positionWeight"] = s.PositionWeight,
                ["startChar"] = s.StartChar,
                ["endChar"] = s.EndChar,
                ["chunkIndex"] = s.ChunkIndex
            }
        }).ToList();
        
        // Upsert in batches
        const int batchSize = 100;
        for (int i = 0; i < points.Count; i += batchSize)
        {
            var batch = points.Skip(i).Take(batchSize).ToList();
            await _client.UpsertAsync(collectionName, batch, cancellationToken: ct);
            
            if (_verbose && points.Count > batchSize)
                VerboseHelper.Log($"[dim]Upserted batch {i / batchSize + 1}/{(points.Count + batchSize - 1) / batchSize}[/]");
        }
        
        if (_verbose)
            VerboseHelper.Log($"[dim]Upserted {segmentList.Count} segments to Qdrant collection '{VerboseHelper.Escape(collectionName)}'[/]");
    }
    
    public async Task<List<Segment>> SearchAsync(
        string collectionName,
        float[] queryEmbedding,
        int topK,
        string? docId = null,
        CancellationToken ct = default)
    {
        Filter? filter = null;
        if (!string.IsNullOrEmpty(docId))
        {
            // Extract content hash from docId and filter by docHash (no PII)
            var docHash = ExtractDocHash(docId);
            filter = new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "docHash",
                            Match = new Match { Keyword = docHash }
                        }
                    }
                }
            };
        }

        var results = await _client.SearchAsync(
            collectionName,
            queryEmbedding,
            filter: filter,
            limit: (ulong)topK,
            payloadSelector: true,
            cancellationToken: ct);

        return results
            .Select(r => PayloadToSegment(r.Payload, r.Score))
            .Where(s => s != null)
            .Cast<Segment>()
            .ToList();
    }
    
    public async Task<List<Segment>> GetDocumentSegmentsAsync(string collectionName, string docId, CancellationToken ct = default)
    {
        var segments = new List<Segment>();
        PointId? offset = null;

        // Extract content hash from docId (format: sanitizedFilename_contentHash)
        var docHash = ExtractDocHash(docId);

        if (_verbose)
            VerboseHelper.Log($"[dim]GetDocumentSegmentsAsync: collection='{VerboseHelper.Escape(collectionName)}', docHash='{VerboseHelper.Escape(docHash)}'[/]");

        // Scroll through all points for this document (filtered by docHash, not filename)
        while (true)
        {
            var scrollResult = await _client.ScrollAsync(
                collectionName,
                filter: new Filter
                {
                    Must =
                    {
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key = "docHash",
                                Match = new Match { Keyword = docHash }
                            }
                        }
                    }
                },
                limit: 100,
                offset: offset,
                payloadSelector: true,
                vectorsSelector: true,
                cancellationToken: ct);

            if (_verbose)
                VerboseHelper.Log($"[dim]  Scroll returned {scrollResult.Result.Count} points[/]");

            foreach (var point in scrollResult.Result)
            {
                var segment = PayloadToSegment(point.Payload, 0, ExtractVectorData(point.Vectors));
                if (segment != null)
                    segments.Add(segment);
            }

            if (scrollResult.NextPageOffset == null)
                break;

            offset = scrollResult.NextPageOffset;
        }

        if (_verbose)
            VerboseHelper.Log($"[dim]  Total segments retrieved: {segments.Count}[/]");

        return segments.OrderBy(s => s.Index).ToList();
    }
    
    public async Task DeleteCollectionAsync(string collectionName, CancellationToken ct = default)
    {
        try
        {
            await _client.DeleteCollectionAsync(collectionName, cancellationToken: ct);
            
            if (_verbose)
                VerboseHelper.Log($"[dim]Deleted Qdrant collection '{VerboseHelper.Escape(collectionName)}'[/]");
        }
        catch
        {
            // Ignore if collection doesn't exist
        }
    }
    
    public async Task DeleteDocumentAsync(string collectionName, string docId, CancellationToken ct = default)
    {
        // Extract content hash from docId and delete by docHash (no PII)
        var docHash = ExtractDocHash(docId);

        await _client.DeleteAsync(
            collectionName,
            new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "docHash",
                            Match = new Match { Keyword = docHash }
                        }
                    }
                }
            },
            cancellationToken: ct);

        if (_verbose)
            VerboseHelper.Log($"[dim]Deleted document by hash from Qdrant collection '{VerboseHelper.Escape(collectionName)}'[/]");
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_deleteOnDispose && !string.IsNullOrEmpty(_activeCollection))
        {
            try
            {
                await DeleteCollectionAsync(_activeCollection);
            }
            catch
            {
                // Ignore errors on cleanup
            }
        }
        
        _client.Dispose();
    }

    public void Dispose()
    {
        if (_deleteOnDispose && !string.IsNullOrEmpty(_activeCollection))
        {
            try
            {
                DeleteCollectionAsync(_activeCollection).GetAwaiter().GetResult();
            }
            catch
            {
                // Ignore errors on cleanup
            }
        }
        
        _client.Dispose();
    }
    
    /// <summary>
    /// Generate a deterministic UUID from segment ID for Qdrant point ID
    /// </summary>
    private static string GenerateUuidFromId(string id)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(id));
        return new Guid(hash).ToString();
    }
    
    private static Segment? PayloadToSegment(
        IDictionary<string, Value>? payload,
        float score,
        float[]? embedding = null)
    {
        if (payload == null) return null;

        try
        {
            // No PII stored in vector DB - use hashes as identifiers
            // Text is retrieved from evidence repository (PostgreSQL) using segmentHash
            var segmentHash = payload.TryGetValue("segmentHash", out var shVal) ? shVal.StringValue : "";
            var docHash = payload.TryGetValue("docHash", out var dhVal) ? dhVal.StringValue : "";
            var typeStr = payload.TryGetValue("type", out var typeVal) ? typeVal.StringValue : "Sentence";
            var index = payload.TryGetValue("index", out var indexVal) ? (int)indexVal.IntegerValue : 0;
            var startChar = payload.TryGetValue("startChar", out var startVal) ? (int)startVal.IntegerValue : 0;
            var endChar = payload.TryGetValue("endChar", out var endVal) ? (int)endVal.IntegerValue : 0;

            if (!Enum.TryParse<SegmentType>(typeStr, out var type))
                type = SegmentType.Sentence;

            // Create segment with hash-based ID and empty text
            // Text will be hydrated from evidence repository by the caller
            var segment = new Segment(docHash, "", type, index, startChar, endChar)
            {
                ContentHash = segmentHash,
                // No PII fields: sectionTitle, headingPath removed
                HeadingLevel = payload.TryGetValue("headingLevel", out var hlVal) ? (int)hlVal.IntegerValue : 0,
                SalienceScore = payload.TryGetValue("salienceScore", out var salVal) ? salVal.DoubleValue : 0,
                PositionWeight = payload.TryGetValue("positionWeight", out var pwVal) ? pwVal.DoubleValue : 1.0,
                ChunkIndex = payload.TryGetValue("chunkIndex", out var ciVal) ? (int)ciVal.IntegerValue : 0,
                QuerySimilarity = score,
                Embedding = embedding
            };

            return segment;
        }
        catch
        {
            return null;
        }
    }
    
    /// <summary>
    /// Extract a non-PII doc hash from a segment ID.
    /// Returns only the hash portion, not the filename.
    /// </summary>
    private static string ExtractDocHash(string segmentId)
    {
        // Segment ID format: filename_contenthash_type_index
        // We want just the content hash portion (typically second-to-last group before _s_ or _p_)
        var parts = segmentId.Split('_');
        if (parts.Length >= 4)
        {
            // Look for the hash portion (16 char hex)
            for (int i = parts.Length - 3; i >= 0; i--)
            {
                if (parts[i].Length == 16 && parts[i].All(c => char.IsLetterOrDigit(c)))
                {
                    return parts[i];
                }
            }
        }
        // Fallback: generate hash from the full ID
        return GenerateHashFromText(segmentId);
    }

    /// <summary>
    /// Generate a deterministic hash from text for non-PII identification.
    /// </summary>
    private static string GenerateHashFromText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = XxHash64.Hash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // === Segment-Level Caching (Granular Invalidation) ===
    
    public async Task<Dictionary<string, Segment>> GetSegmentsByHashAsync(
        string collectionName,
        IEnumerable<string> contentHashes,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, Segment>();
        var hashList = contentHashes.ToList();

        if (hashList.Count == 0)
            return result;

        try
        {
            // Scroll through segments with matching segment hashes
            // Qdrant doesn't support IN filter well, so we use multiple OR conditions
            // For large hash sets, this could be batched
            foreach (var hashBatch in hashList.Chunk(50))
            {
                var filter = new Filter();
                foreach (var hash in hashBatch)
                {
                    filter.Should.Add(new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "segmentHash",  // Changed from contentHash
                            Match = new Match { Keyword = hash }
                        }
                    });
                }

                var scrollResult = await _client.ScrollAsync(
                    collectionName,
                    filter: filter,
                    limit: (uint)hashBatch.Length,
                    payloadSelector: true,
                    vectorsSelector: true,
                    cancellationToken: ct);

                foreach (var point in scrollResult.Result)
                {
                    var segment = PayloadToSegment(point.Payload, 0, ExtractVectorData(point.Vectors));
                    if (segment != null && !string.IsNullOrEmpty(segment.ContentHash))
                    {
                        result[segment.ContentHash] = segment;
                    }
                }
            }

            if (_verbose && result.Count > 0)
                VerboseHelper.Log(_verbose, $"[dim]Found {result.Count}/{hashList.Count} cached segments by hash[/]");
        }
        catch (Exception ex)
        {
            if (_verbose)
                VerboseHelper.Log($"[yellow]GetSegmentsByHash failed: {VerboseHelper.Escape(ex.Message)}[/]");
        }

        return result;
    }
    
    public async Task RemoveStaleSegmentsAsync(
        string collectionName,
        string docId,
        IEnumerable<string> validContentHashes,
        CancellationToken ct = default)
    {
        try
        {
            var validHashes = validContentHashes.ToHashSet();

            // Get all segments for this document (by extracted docHash)
            var docSegments = await GetDocumentSegmentsAsync(collectionName, docId, ct);

            // Find segments to delete (those not in valid hashes)
            var staleHashes = docSegments
                .Where(s => !validHashes.Contains(s.ContentHash))
                .Select(s => s.ContentHash)
                .Where(h => !string.IsNullOrEmpty(h))
                .ToList();

            if (staleHashes.Count == 0)
                return;

            // Delete stale segments by their content hash (used as point ID)
            var pointIds = staleHashes.Select(hash => new PointId { Uuid = GenerateUuidFromId(hash!) }).ToList();
            await _client.DeleteAsync(collectionName, pointIds, cancellationToken: ct);

            if (_verbose)
                VerboseHelper.Log($"[dim]Removed {staleHashes.Count} stale segments from document[/]");
        }
        catch (Exception ex)
        {
            if (_verbose)
                VerboseHelper.Log($"[yellow]RemoveStaleSegments failed: {VerboseHelper.Escape(ex.Message)}[/]");
        }
    }
    
    // === Summary Caching ===
    // Summaries are stored using xxHash64 of evidence hash as the vector.
    // This allows fast exact-match lookups via vector search (score = 1.0).
    // Much faster than filter-based scroll for large collections.
    
    /// <summary>
    /// Generate a deterministic vector from cache key using xxHash64.
    /// The hash is expanded to fill the vector dimensions.
    /// </summary>
    private float[] GenerateCacheVector(string cacheKey)
    {
        var bytes = Encoding.UTF8.GetBytes(cacheKey);
        var hash = XxHash64.HashToUInt64(bytes);
        
        // Expand hash to vector size using deterministic pattern
        var vector = new float[_vectorSize];
        var hashBytes = BitConverter.GetBytes(hash);
        
        for (int i = 0; i < _vectorSize; i++)
        {
            // Use rotating bytes from hash, normalized to [-1, 1]
            var byteVal = hashBytes[i % 8];
            vector[i] = (byteVal / 127.5f) - 1f;
        }
        
        // Normalize to unit vector for cosine similarity
        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 0)
        {
            for (int i = 0; i < _vectorSize; i++)
                vector[i] /= norm;
        }
        
        return vector;
    }
    
    public async Task<DocumentSummary?> GetCachedSummaryAsync(string collectionName, string cacheKey, CancellationToken ct = default)
    {
        try
        {
            var collections = await _client.ListCollectionsAsync(ct);
            if (!collections.Any(c => c == collectionName))
                return null;
            
            // Generate the cache vector and search for exact match
            var cacheVector = GenerateCacheVector(cacheKey);
            
            var results = await _client.SearchAsync(
                collectionName,
                cacheVector,
                filter: new Filter
                {
                    Must =
                    {
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key = "type",
                                Match = new Match { Keyword = "summary" }
                            }
                        }
                    }
                },
                limit: 1,
                payloadSelector: true,
                cancellationToken: ct);
            
            // Check for exact match (score very close to 1.0)
            if (results.Count == 0 || results[0].Score < 0.999f)
                return null;
            
            var point = results[0];
            if (!point.Payload.TryGetValue("summaryJson", out var jsonVal))
                return null;
            
            var json = jsonVal.StringValue;
            var summary = System.Text.Json.JsonSerializer.Deserialize<DocumentSummary>(json);
            
            if (_verbose)
                VerboseHelper.Log($"[dim]Cache hit for '{VerboseHelper.Escape(cacheKey)}' (score={point.Score:F4})[/]");
            
            return summary;
        }
        catch (Exception ex)
        {
            if (_verbose)
                VerboseHelper.Log($"[yellow]Cache lookup failed: {VerboseHelper.Escape(ex.Message)}[/]");
            return null;
        }
    }
    
    public async Task CacheSummaryAsync(string collectionName, string cacheKey, DocumentSummary summary, CancellationToken ct = default)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(summary);
            var cacheVector = GenerateCacheVector(cacheKey);
            
            var point = new PointStruct
            {
                Id = new PointId { Uuid = GenerateUuidFromId($"summary:{cacheKey}") },
                Vectors = cacheVector,
                Payload =
                {
                    ["type"] = "summary",
                    ["cacheKey"] = cacheKey,
                    ["summaryJson"] = json,
                    ["cachedAt"] = DateTime.UtcNow.ToString("O")
                }
            };
            
            await _client.UpsertAsync(collectionName, new[] { point }, cancellationToken: ct);
            
            if (_verbose)
                VerboseHelper.Log($"[dim]Cached summary for '{VerboseHelper.Escape(cacheKey)}'[/]");
        }
        catch (Exception ex)
        {
            if (_verbose)
                VerboseHelper.Log($"[yellow]Cache write failed: {VerboseHelper.Escape(ex.Message)}[/]");
        }
    }
    
    /// <summary>
    /// Extract vector data from VectorsOutput, handling API changes
    /// </summary>
#pragma warning disable CS0612 // Suppress obsolete warning - need to support both old and new API
    private static float[]? ExtractVectorData(VectorsOutput? vectors)
    {
        if (vectors?.Vector == null) return null;
        
        // Try the new API first (direct array access), fall back to deprecated Data property
        try
        {
            // In newer versions, Vector should be directly convertible to float[]
            var vectorData = vectors.Vector.Data;
            return vectorData?.ToArray();
        }
        catch
        {
            return null;
        }
    }
#pragma warning restore CS0612
}
