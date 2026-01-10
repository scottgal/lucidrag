using System.Collections.Concurrent;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// In-memory vector store for BertRag pipeline.
/// Default implementation - no external dependencies, vectors are stored in memory.
/// Segments and cached summaries are lost when the process exits.
/// </summary>
public class InMemoryVectorStore : IVectorStore
{
    private readonly ConcurrentDictionary<string, List<Segment>> _collections = new();
    private readonly ConcurrentDictionary<string, DocumentSummary> _summaryCache = new();
    private readonly bool _verbose;
    
    public InMemoryVectorStore(bool verbose = false)
    {
        _verbose = verbose;
    }
    
    public bool IsPersistent => false;
    
    public Task InitializeAsync(string collectionName, int vectorSize, CancellationToken ct = default)
    {
        // Ensure collection exists
        _collections.TryAdd(collectionName, new List<Segment>());
        return Task.CompletedTask;
    }
    
    public Task<bool> HasDocumentAsync(string collectionName, string docHash, CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var segments))
            return Task.FromResult(false);

        // Check if any segment belongs to this document (by content hash)
        var hasDoc = segments.Any(s => s.Id.StartsWith(SanitizeDocId(docHash) + "_") ||
                                       s.ContentHash?.StartsWith(docHash) == true);
        return Task.FromResult(hasDoc);
    }
    
    public Task UpsertSegmentsAsync(string collectionName, IEnumerable<Segment> segments, CancellationToken ct = default)
    {
        var segmentList = segments.Where(s => s.Embedding != null).ToList();
        
        if (segmentList.Count == 0)
            return Task.CompletedTask;
        
        _collections.AddOrUpdate(
            collectionName,
            _ => segmentList,
            (_, existing) =>
            {
                // Remove existing segments with same IDs, then add new ones
                var newIds = segmentList.Select(s => s.Id).ToHashSet();
                var filtered = existing.Where(s => !newIds.Contains(s.Id)).ToList();
                filtered.AddRange(segmentList);
                return filtered;
            });
        
        if (_verbose)
            Console.WriteLine($"[InMemoryVectorStore] Upserted {segmentList.Count} segments to '{collectionName}'");
        
        return Task.CompletedTask;
    }
    
    public Task<List<Segment>> SearchAsync(
        string collectionName,
        float[] queryEmbedding,
        int topK,
        string? docHash = null,
        CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var segments))
            return Task.FromResult(new List<Segment>());

        var candidates = segments.AsEnumerable();

        // Filter by document hash if specified
        if (!string.IsNullOrEmpty(docHash))
        {
            var prefix = SanitizeDocId(docHash) + "_";
            candidates = candidates.Where(s => s.Id.StartsWith(prefix) ||
                                               s.ContentHash?.StartsWith(docHash) == true);
        }

        // Score by cosine similarity and return top K
        var results = candidates
            .Where(s => s.Embedding != null)
            .Select(s => (Segment: s, Score: CosineSimilarity(queryEmbedding, s.Embedding!)))
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x =>
            {
                // Set the query similarity on the segment for downstream use
                x.Segment.QuerySimilarity = x.Score;
                return x.Segment;
            })
            .ToList();

        return Task.FromResult(results);
    }
    
    public Task<List<Segment>> GetDocumentSegmentsAsync(string collectionName, string docHash, CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var segments))
            return Task.FromResult(new List<Segment>());

        var prefix = SanitizeDocId(docHash) + "_";
        var docSegments = segments
            .Where(s => s.Id.StartsWith(prefix) || s.ContentHash?.StartsWith(docHash) == true)
            .OrderBy(s => s.Index)
            .ToList();

        return Task.FromResult(docSegments);
    }
    
    public Task DeleteCollectionAsync(string collectionName, CancellationToken ct = default)
    {
        _collections.TryRemove(collectionName, out _);
        
        if (_verbose)
            Console.WriteLine($"[InMemoryVectorStore] Deleted collection '{collectionName}'");
        
        return Task.CompletedTask;
    }
    
    public Task DeleteDocumentAsync(string collectionName, string docHash, CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var segments))
            return Task.CompletedTask;

        var prefix = SanitizeDocId(docHash) + "_";
        var remaining = segments.Where(s => !s.Id.StartsWith(prefix) &&
                                            s.ContentHash?.StartsWith(docHash) != true).ToList();

        _collections[collectionName] = remaining;

        if (_verbose)
            Console.WriteLine($"[InMemoryVectorStore] Deleted document by hash from '{collectionName}'");

        return Task.CompletedTask;
    }
    
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _collections.Clear();
        _summaryCache.Clear();
    }
    
    // === Segment-Level Caching (Granular Invalidation) ===
    
    public Task<Dictionary<string, Segment>> GetSegmentsByHashAsync(
        string collectionName, 
        IEnumerable<string> contentHashes, 
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, Segment>();
        
        if (!_collections.TryGetValue(collectionName, out var segments))
            return Task.FromResult(result);
        
        var hashSet = contentHashes.ToHashSet();
        foreach (var segment in segments)
        {
            if (hashSet.Contains(segment.ContentHash))
            {
                result[segment.ContentHash] = segment;
            }
        }
        
        if (_verbose && result.Count > 0)
            Console.WriteLine($"[InMemoryVectorStore] Found {result.Count}/{hashSet.Count} cached segments by hash");
        
        return Task.FromResult(result);
    }
    
    public Task RemoveStaleSegmentsAsync(
        string collectionName,
        string docHash,
        IEnumerable<string> validContentHashes,
        CancellationToken ct = default)
    {
        if (!_collections.TryGetValue(collectionName, out var segments))
            return Task.CompletedTask;

        var validHashes = validContentHashes.ToHashSet();
        var prefix = SanitizeDocId(docHash) + "_";

        // Keep segments that don't belong to this doc, or have valid content hashes
        var remaining = segments
            .Where(s => (!s.Id.StartsWith(prefix) && s.ContentHash?.StartsWith(docHash) != true) ||
                        validHashes.Contains(s.ContentHash ?? ""))
            .ToList();

        var removed = segments.Count - remaining.Count;
        _collections[collectionName] = remaining;

        if (_verbose && removed > 0)
            Console.WriteLine($"[InMemoryVectorStore] Removed {removed} stale segments");

        return Task.CompletedTask;
    }
    
    // === Summary Caching ===
    
    public Task<DocumentSummary?> GetCachedSummaryAsync(string collectionName, string evidenceHash, CancellationToken ct = default)
    {
        var fullKey = $"{collectionName}:{evidenceHash}";
        _summaryCache.TryGetValue(fullKey, out var summary);
        
        if (_verbose && summary != null)
            Console.WriteLine($"[InMemoryVectorStore] Summary cache hit for evidence '{evidenceHash[..Math.Min(16, evidenceHash.Length)]}'");
        
        return Task.FromResult(summary);
    }
    
    public Task CacheSummaryAsync(string collectionName, string evidenceHash, DocumentSummary summary, CancellationToken ct = default)
    {
        var fullKey = $"{collectionName}:{evidenceHash}";
        _summaryCache[fullKey] = summary;
        
        if (_verbose)
            Console.WriteLine($"[InMemoryVectorStore] Cached summary for evidence '{evidenceHash[..Math.Min(16, evidenceHash.Length)]}'");
        
        return Task.CompletedTask;
    }
    
    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        
        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom > 0 ? dot / denom : 0;
    }
    
    private static string SanitizeDocId(string docId)
    {
        // Match Segment.SanitizeDocId logic
        var sb = new System.Text.StringBuilder();
        foreach (var c in docId)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
            else if (c == '.' || c == '-' || c == ' ')
                sb.Append('_');
        }
        return sb.ToString().ToLowerInvariant();
    }
}
