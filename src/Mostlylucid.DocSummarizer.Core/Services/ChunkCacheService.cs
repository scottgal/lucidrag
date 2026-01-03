using System.Text.Json;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Simple disk-based cache for Docling chunks keyed by file hash.
/// Supports lazy content loading to reduce memory usage for large documents.
/// </summary>
public class ChunkCacheService
{
    private readonly ChunkCacheConfig _config;
    private readonly bool _verbose;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ChunkCacheService(ChunkCacheConfig config, bool verbose = false)
    {
        _config = config ?? new ChunkCacheConfig();
        _verbose = verbose;
        EnsureCacheDirectory();
        CleanupExpired();
    }

    public bool Enabled => _config.EnableChunkCache;

    /// <summary>
    /// Try to load cached chunks for a document if the file hash matches.
    /// Returns a ChunkStore that provides lazy or eager access to chunks.
    /// </summary>
    public async Task<CachedChunkStore?> TryLoadStoreAsync(string docId, string fileHash, CancellationToken ct = default)
    {
        if (!Enabled) return null;

        var basePath = GetCacheBasePath(docId, fileHash);
        var metadataPath = basePath + ".json";
        var contentDir = basePath + "_content";

        if (File.Exists(metadataPath) && Directory.Exists(contentDir))
        {
            return await TryLoadV2StoreAsync(metadataPath, contentDir, fileHash, ct);
        }

        return null;
    }

    /// <summary>
    /// Try to load cached chunks for a document if the file hash matches.
    /// For backward compatibility - returns List&lt;DocumentChunk&gt; with all content loaded.
    /// Prefer TryLoadStoreAsync for memory-efficient access.
    /// </summary>
    public async Task<List<DocumentChunk>?> TryLoadAsync(string docId, string fileHash, CancellationToken ct = default)
    {
        var store = await TryLoadStoreAsync(docId, fileHash, ct);
        return store?.ToList();
    }

    /// <summary>
    /// Load cache: metadata JSON + separate content files
    /// </summary>
    private async Task<CachedChunkStore?> TryLoadV2StoreAsync(string metadataPath, string contentDir, string fileHash, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(metadataPath, ct);
            var entry = JsonSerializer.Deserialize<ChunkCacheMetadata>(json, _jsonOptions);
            if (entry == null) return null;

            if (!string.Equals(entry.Version, _config.VersionToken, StringComparison.Ordinal))
                return null;

            if (!string.Equals(entry.FileHash, fileHash, StringComparison.Ordinal))
                return null;

            if (IsExpired(entry.CreatedUtc))
            {
                TryDeleteCacheEntry(metadataPath, contentDir);
                return null;
            }

            // Touch last access
            entry.LastAccessUtc = DateTimeOffset.UtcNow;
            await SaveMetadataAsync(metadataPath, entry, ct);

            var total = entry.ChunkMetadata?.Count ?? 0;

            // Decide whether to use lazy loading based on chunk count
            var useLazyLoading = _config.LazyLoadContent && total >= _config.LazyLoadThreshold;

            if (_verbose)
            {
                var mode = useLazyLoading ? "lazy (memory-efficient)" : "eager (small document)";
                Console.WriteLine($"[Cache] Loading {total} chunks in {mode} mode");
            }

            return new CachedChunkStore(entry.ChunkMetadata ?? [], contentDir, useLazyLoading);
        }
        catch (Exception ex)
        {
            if (_verbose)
            {
                Console.WriteLine($"[Cache] Failed to load cache: {ex.Message}");
            }
            return null;
        }
    }

    /// <summary>
    /// Persist chunks for future runs.
    /// </summary>
    public async Task SaveAsync(string docId, string fileHash, List<DocumentChunk> chunks, CancellationToken ct = default)
    {
        if (!Enabled || chunks.Count == 0) return;

        var basePath = GetCacheBasePath(docId, fileHash);
        var metadataPath = basePath + ".json";
        var contentDir = basePath + "_content";

        // Create content directory
        Directory.CreateDirectory(contentDir);

        // Build metadata (without content)
        var metadata = new ChunkCacheMetadata
        {
            DocId = docId,
            FileHash = fileHash,
            Version = _config.VersionToken,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastAccessUtc = DateTimeOffset.UtcNow,
            ChunkMetadata = chunks.Select(c => new ChunkCacheMetadataEntry
            {
                Order = c.Order,
                Heading = c.Heading,
                HeadingLevel = c.HeadingLevel,
                Hash = c.Hash,
                PageStart = c.PageStart,
                PageEnd = c.PageEnd,
                ContentLength = c.Content.Length
            }).ToList()
        };

        // Save metadata
        EnsureCacheDirectory();
        await SaveMetadataAsync(metadataPath, metadata, ct);

        // Save content files
        foreach (var chunk in chunks)
        {
            var contentPath = Path.Combine(contentDir, $"{chunk.Order:D6}.txt");
            await File.WriteAllTextAsync(contentPath, chunk.Content, ct);
        }

        if (_verbose)
        {
            var totalContentBytes = chunks.Sum(c => c.Content.Length);
            Console.WriteLine($"[Cache] Saved {chunks.Count} chunks ({totalContentBytes / 1024:N0} KB)");
        }
    }

    /// <summary>
    /// Remove cache files older than retention window.
    /// </summary>
    public void CleanupExpired()
    {
        if (!Enabled || _config.RetentionDays <= 0) return;
        var dir = GetCacheDirectory();
        if (!Directory.Exists(dir)) return;

        var cutoff = DateTimeOffset.UtcNow.AddDays(-_config.RetentionDays);
        
        // Clean up expired cache entries
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < cutoff)
                {
                    var contentDir = Path.ChangeExtension(file, null) + "_content";
                    TryDeleteCacheEntry(file, contentDir);
                    if (_verbose) Console.WriteLine($"[Cache] Removed expired cache: {info.Name}");
                }
            }
            catch
            {
                // Ignore cleanup issues
            }
        }

        // Clean up orphaned content directories
        foreach (var contentDir in Directory.EnumerateDirectories(dir, "*_content"))
        {
            try
            {
                var metadataPath = contentDir.Replace("_content", ".json");
                if (!File.Exists(metadataPath))
                {
                    Directory.Delete(contentDir, true);
                    if (_verbose) Console.WriteLine($"[Cache] Removed orphaned content directory: {Path.GetFileName(contentDir)}");
                }
            }
            catch
            {
                // Ignore cleanup issues
            }
        }
    }

    /// <summary>
    /// Get estimated memory savings from lazy loading for a cached document
    /// </summary>
    public long GetEstimatedMemorySavings(string docId, string fileHash)
    {
        var basePath = GetCacheBasePath(docId, fileHash);
        var metadataPath = basePath + ".json";

        if (!File.Exists(metadataPath)) return 0;

        try
        {
            var json = File.ReadAllText(metadataPath);
            var metadata = JsonSerializer.Deserialize<ChunkCacheMetadata>(json, _jsonOptions);
            if (metadata?.ChunkMetadata == null) return 0;

            // Estimate: content would take ~2 bytes per char in memory (UTF-16)
            return metadata.ChunkMetadata.Sum(m => (long)m.ContentLength * 2);
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeleteCacheEntry(string metadataPath, string contentDir)
    {
        try
        {
            if (Directory.Exists(contentDir))
                Directory.Delete(contentDir, true);
            if (File.Exists(metadataPath))
                File.Delete(metadataPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get base path for cache (without extension)
    /// </summary>
    private string GetCacheBasePath(string docId, string fileHash)
    {
        var safeId = string.Join("", docId.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        var name = $"{safeId}_{fileHash}";
        return Path.Combine(GetCacheDirectory(), name);
    }

    private string GetCacheDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_config.CacheDirectory))
            return _config.CacheDirectory!;

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docsummarizer", "chunks");
    }

    private void EnsureCacheDirectory()
    {
        if (!Enabled) return;
        var dir = GetCacheDirectory();
        Directory.CreateDirectory(dir);
    }

    private bool IsExpired(DateTimeOffset created)
    {
        if (_config.RetentionDays <= 0) return false;
        return created < DateTimeOffset.UtcNow.AddDays(-_config.RetentionDays);
    }

    private async Task SaveMetadataAsync(string path, ChunkCacheMetadata metadata, CancellationToken ct)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, metadata, _jsonOptions, ct);
    }
}

#region Cache Entry Models

/// <summary>
/// Cache metadata entry (content stored in separate files)
/// </summary>
public class ChunkCacheMetadata
{
    public string DocId { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastAccessUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<ChunkCacheMetadataEntry> ChunkMetadata { get; set; } = [];
}

/// <summary>
/// Chunk metadata entry (no content - content stored in separate file)
/// </summary>
public class ChunkCacheMetadataEntry
{
    public int Order { get; set; }
    public string Heading { get; set; } = string.Empty;
    public int HeadingLevel { get; set; }
    public string Hash { get; set; } = string.Empty;
    public int? PageStart { get; set; }
    public int? PageEnd { get; set; }
    public int ContentLength { get; set; }
}

#endregion

#region Cached Chunk Store

/// <summary>
/// Provides access to cached chunks with optional lazy content loading.
/// Content can be loaded on-demand from disk to reduce memory usage.
/// </summary>
public class CachedChunkStore : IDisposable
{
    private readonly List<ChunkCacheMetadataEntry> _metadata;
    private readonly string _contentDir;
    private readonly bool _lazyLoad;
    private readonly Dictionary<int, string> _contentCache = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Create a store from cache metadata
    /// </summary>
    public CachedChunkStore(List<ChunkCacheMetadataEntry> metadata, string contentDir, bool lazyLoad)
    {
        _metadata = metadata;
        _contentDir = contentDir;
        _lazyLoad = lazyLoad;
        Count = metadata.Count;
        
        // If not lazy loading, pre-load all content
        if (!lazyLoad)
        {
            PreloadAllContent();
        }
    }

    /// <summary>
    /// Number of chunks in the store
    /// </summary>
    public int Count { get; }

    /// <summary>
    /// Whether content is loaded lazily
    /// </summary>
    public bool IsLazyLoading => _lazyLoad && _metadata != null;

    /// <summary>
    /// Estimated memory used by cached content (bytes)
    /// </summary>
    public long CachedContentMemory
    {
        get
        {
            lock (_lock)
            {
                return _contentCache.Values.Sum(c => (long)c.Length * 2);
            }
        }
    }

    /// <summary>
    /// Get a chunk by index. Content is loaded on-demand if lazy loading is enabled.
    /// </summary>
    public DocumentChunk Get(int index)
    {
        ThrowIfDisposed();

        if (index < 0 || index >= _metadata.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        var meta = _metadata[index];
        var content = GetContent(meta.Order);

        return new DocumentChunk(
            meta.Order,
            meta.Heading,
            meta.HeadingLevel,
            content,
            meta.Hash,
            meta.PageStart,
            meta.PageEnd,
            Count);
    }

    /// <summary>
    /// Get chunk content by order. Loads from disk if not cached.
    /// </summary>
    public string GetContent(int order)
    {
        ThrowIfDisposed();

        // Check cache first
        lock (_lock)
        {
            if (_contentCache.TryGetValue(order, out var cached))
                return cached;
        }

        // Load from disk
        var contentPath = Path.Combine(_contentDir, $"{order:D6}.txt");
        if (!File.Exists(contentPath))
            return string.Empty;

        var content = File.ReadAllText(contentPath);

        // Cache if we're not in lazy mode (or if explicitly requested)
        if (!_lazyLoad)
        {
            lock (_lock)
            {
                _contentCache[order] = content;
            }
        }

        return content;
    }

    /// <summary>
    /// Pre-load content for specific chunks (useful for batch processing)
    /// </summary>
    public void PreloadContent(IEnumerable<int> orders)
    {
        ThrowIfDisposed();

        foreach (var order in orders)
        {
            lock (_lock)
            {
                if (_contentCache.ContainsKey(order)) continue;
            }

            var contentPath = Path.Combine(_contentDir, $"{order:D6}.txt");
            if (File.Exists(contentPath))
            {
                var content = File.ReadAllText(contentPath);
                lock (_lock)
                {
                    _contentCache[order] = content;
                }
            }
        }
    }

    /// <summary>
    /// Release cached content to free memory (content will be reloaded on next access)
    /// </summary>
    public void ReleaseContent()
    {
        lock (_lock)
        {
            _contentCache.Clear();
        }
    }

    /// <summary>
    /// Release content for specific chunks
    /// </summary>
    public void ReleaseContent(IEnumerable<int> orders)
    {
        lock (_lock)
        {
            foreach (var order in orders)
            {
                _contentCache.Remove(order);
            }
        }
    }

    /// <summary>
    /// Enumerate all chunks. Content is loaded on-demand.
    /// </summary>
    public IEnumerable<DocumentChunk> Enumerate()
    {
        ThrowIfDisposed();

        for (var i = 0; i < _metadata.Count; i++)
        {
            yield return Get(i);
        }
    }

    /// <summary>
    /// Convert to a list. Loads all content into memory.
    /// </summary>
    public List<DocumentChunk> ToList()
    {
        ThrowIfDisposed();
        return Enumerate().ToList();
    }

    /// <summary>
    /// Process chunks in batches to limit memory usage.
    /// Content is loaded for each batch and released after processing.
    /// </summary>
    public async Task ProcessInBatchesAsync<T>(
        int batchSize,
        Func<List<DocumentChunk>, Task<T>> processor,
        Action<T>? onBatchComplete = null)
    {
        ThrowIfDisposed();

        for (var i = 0; i < Count; i += batchSize)
        {
            var batch = new List<DocumentChunk>();
            var orders = new List<int>();
            
            for (var j = i; j < Math.Min(i + batchSize, Count); j++)
            {
                batch.Add(Get(j));
                orders.Add(_metadata[j].Order);
            }

            var result = await processor(batch);
            onBatchComplete?.Invoke(result);

            // Release content for processed batch if lazy loading
            if (_lazyLoad && orders.Count > 0)
            {
                ReleaseContent(orders);
            }
        }
    }

    private void PreloadAllContent()
    {
        foreach (var meta in _metadata)
        {
            var contentPath = Path.Combine(_contentDir, $"{meta.Order:D6}.txt");
            if (File.Exists(contentPath))
            {
                var content = File.ReadAllText(contentPath);
                _contentCache[meta.Order] = content;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CachedChunkStore));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _contentCache.Clear();
        _disposed = true;
    }
}

#endregion
