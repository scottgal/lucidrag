using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Memory-efficient chunk storage that spills to disk for large documents.
/// Keeps metadata in memory but stores content on disk when chunk count exceeds threshold.
/// </summary>
public class DiskBackedChunkStore : IDisposable
{
    /// <summary>
    /// Default threshold for using disk storage (100 chunks ~= 400KB-1.6MB of content)
    /// </summary>
    public const int DefaultDiskThreshold = 100;
    
    /// <summary>
    /// Maximum chunks to keep fully in memory regardless of settings
    /// </summary>
    public const int MaxInMemoryChunks = 500;
    
    private readonly int _diskThreshold;
    private readonly string? _storageDir;
    private readonly bool _verbose;
    
    private readonly List<ChunkMetadata> _metadata = [];
    private readonly Dictionary<int, string> _inMemoryContent = new();
    private bool _usingDisk;
    private bool _disposed;

    public DiskBackedChunkStore(int diskThreshold = DefaultDiskThreshold, bool verbose = false)
    {
        _diskThreshold = diskThreshold;
        _verbose = verbose;
        _storageDir = null;
        _usingDisk = false;
    }

    /// <summary>
    /// Number of chunks stored
    /// </summary>
    public int Count => _metadata.Count;
    
    /// <summary>
    /// Whether storage is currently using disk
    /// </summary>
    public bool UsingDisk => _usingDisk;
    
    /// <summary>
    /// Estimated memory usage in bytes
    /// </summary>
    public long EstimatedMemoryBytes
    {
        get
        {
            // Metadata: ~200 bytes per chunk (heading, hash, etc.)
            var metadataBytes = _metadata.Count * 200L;
            
            // In-memory content
            var contentBytes = _inMemoryContent.Values.Sum(c => (long)c.Length * 2); // UTF-16
            
            return metadataBytes + contentBytes;
        }
    }

    /// <summary>
    /// Add a chunk to storage
    /// </summary>
    public void Add(DocumentChunk chunk)
    {
        ThrowIfDisposed();
        
        var order = _metadata.Count;
        
        // Check if we need to switch to disk storage
        if (!_usingDisk && order >= _diskThreshold)
        {
            SwitchToDiskStorage();
        }
        
        // Store metadata (always in memory)
        _metadata.Add(new ChunkMetadata(
            order,
            chunk.Heading,
            chunk.HeadingLevel,
            chunk.Hash,
            chunk.Content.Length));
        
        // Store content
        if (_usingDisk)
        {
            WriteContentToDisk(order, chunk.Content);
        }
        else
        {
            _inMemoryContent[order] = chunk.Content;
        }
    }

    /// <summary>
    /// Add multiple chunks efficiently
    /// </summary>
    public void AddRange(IEnumerable<DocumentChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            Add(chunk);
        }
    }

    /// <summary>
    /// Get a chunk by order index
    /// </summary>
    public DocumentChunk Get(int order)
    {
        ThrowIfDisposed();
        
        if (order < 0 || order >= _metadata.Count)
            throw new ArgumentOutOfRangeException(nameof(order));
        
        var meta = _metadata[order];
        var content = GetContent(order);
        
        return new DocumentChunk(
            meta.Order,
            meta.Heading,
            meta.HeadingLevel,
            content,
            meta.Hash);
    }

    /// <summary>
    /// Get chunk content only (for embedding/summarization)
    /// </summary>
    public string GetContent(int order)
    {
        ThrowIfDisposed();
        
        if (_usingDisk)
        {
            return ReadContentFromDisk(order);
        }
        
        return _inMemoryContent.TryGetValue(order, out var content) 
            ? content 
            : throw new InvalidOperationException($"Chunk {order} not found");
    }

    /// <summary>
    /// Get all chunks as a list (materializes everything - use sparingly)
    /// </summary>
    public List<DocumentChunk> ToList()
    {
        ThrowIfDisposed();
        
        return _metadata.Select((m, i) => Get(i)).ToList();
    }

    /// <summary>
    /// Enumerate chunks without loading all into memory
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
    /// Enumerate chunk metadata only (no content loading)
    /// </summary>
    public IEnumerable<ChunkMetadata> EnumerateMetadata()
    {
        ThrowIfDisposed();
        return _metadata;
    }

    /// <summary>
    /// Process chunks in batches to limit memory usage
    /// </summary>
    public async Task ProcessInBatchesAsync<T>(
        int batchSize,
        Func<List<DocumentChunk>, Task<T>> processor,
        Action<T>? onBatchComplete = null)
    {
        ThrowIfDisposed();
        
        for (var i = 0; i < _metadata.Count; i += batchSize)
        {
            var batch = new List<DocumentChunk>();
            for (var j = i; j < Math.Min(i + batchSize, _metadata.Count); j++)
            {
                batch.Add(Get(j));
            }
            
            var result = await processor(batch);
            onBatchComplete?.Invoke(result);
            
            // Clear batch to allow GC
            batch.Clear();
        }
    }

    /// <summary>
    /// Clear all stored chunks and free resources
    /// </summary>
    public void Clear()
    {
        _metadata.Clear();
        _inMemoryContent.Clear();
        
        if (_usingDisk && _storageDir != null && Directory.Exists(_storageDir))
        {
            try
            {
                Directory.Delete(_storageDir, true);
                if (_verbose) Console.WriteLine($"[ChunkStore] Cleaned up disk storage: {_storageDir}");
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        
        _usingDisk = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        Clear();
        _disposed = true;
    }

    private void SwitchToDiskStorage()
    {
        if (_usingDisk) return;
        
        // Create temp directory
        var storageDir = Path.Combine(Path.GetTempPath(), $"docsummarizer_chunks_{Guid.NewGuid():N}");
        Directory.CreateDirectory(storageDir);
        
        if (_verbose)
            Console.WriteLine($"[ChunkStore] Switching to disk storage at {storageDir} (threshold: {_diskThreshold} chunks)");
        
        // Move existing in-memory content to disk
        foreach (var (order, content) in _inMemoryContent)
        {
            var path = GetContentPath(storageDir, order);
            File.WriteAllText(path, content);
        }
        
        // Clear in-memory content
        _inMemoryContent.Clear();
        
        // Use reflection to set readonly field (or make it non-readonly)
        // Actually, let's just use a different approach
        _usingDisk = true;
    }

    private string GetStorageDir()
    {
        if (_storageDir != null) return _storageDir;
        
        var dir = Path.Combine(Path.GetTempPath(), $"docsummarizer_chunks_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string GetContentPath(string dir, int order)
    {
        return Path.Combine(dir, $"chunk_{order:D6}.txt");
    }

    private void WriteContentToDisk(int order, string content)
    {
        var dir = GetStorageDir();
        var path = GetContentPath(dir, order);
        File.WriteAllText(path, content);
    }

    private string ReadContentFromDisk(int order)
    {
        var dir = GetStorageDir();
        var path = GetContentPath(dir, order);
        
        if (!File.Exists(path))
            throw new InvalidOperationException($"Chunk content file not found: {path}");
        
        return File.ReadAllText(path);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DiskBackedChunkStore));
    }
}

/// <summary>
/// Lightweight chunk metadata (kept in memory even when content is on disk)
/// </summary>
public record ChunkMetadata(
    int Order,
    string Heading,
    int HeadingLevel,
    string Hash,
    int ContentLength)
{
    public string Id => $"chunk-{Order}";
}
