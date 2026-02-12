using System.Collections.Concurrent;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services.Utilities;

namespace LucidRAG.Services;

/// <summary>
///     In-memory per-conversation LFU cache that promotes frequently-relevant segments
///     and evicts stale ones. Carries context across conversation turns so the RAG
///     system can blend cached salient segments with fresh retrieval results.
///     Ported from DoomSummarizer's SalientSegmentCache, adapted for LucidRAG's Segment model.
/// </summary>
public sealed class SalientSegmentCache
{
    private readonly int _capacity;
    private readonly Dictionary<string, int> _documentFocusCounts = new();
    private readonly Dictionary<string, CachedSegment> _segments = new();

    public SalientSegmentCache(int capacity = 50)
    {
        _capacity = capacity;
    }

    /// <summary>
    ///     Cache statistics for diagnostics.
    /// </summary>
    public (int count, int capacity, int evicted) Stats
        => (_segments.Count, _capacity, _totalEvicted);

    private int _totalEvicted;

    /// <summary>
    ///     Add new segments from retrieval results. Existing segments get their
    ///     turn updated but are not replaced.
    /// </summary>
    public void AddRange(IEnumerable<Segment> segments, int currentTurn)
    {
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment.Id)) continue;

            if (_segments.TryGetValue(segment.Id, out var existing))
            {
                existing.LastAccessedTurn = currentTurn;
            }
            else
            {
                _segments[segment.Id] = new CachedSegment
                {
                    Segment = segment,
                    AccessCount = 0,
                    TurnAdded = currentTurn,
                    LastAccessedTurn = currentTurn,
                    LastSalience = 0f
                };
            }
        }
    }

    /// <summary>
    ///     Get segments salient to the current query via cosine similarity.
    ///     Uses embeddings stored on segments.
    /// </summary>
    public List<Segment> GetSalient(
        float[] queryEmbedding, int currentTurn,
        float threshold = 0.35f, int maxItems = 10)
    {
        if (queryEmbedding is not { Length: > 0 })
            return [];

        var scored = new List<(CachedSegment seg, float sim)>();

        foreach (var entry in _segments.Values)
        {
            if (entry.Segment.Embedding is not { Length: > 0 }) continue;

            var sim = VectorMath.CosineSimilarity(queryEmbedding, entry.Segment.Embedding);
            if (sim >= threshold)
                scored.Add((entry, sim));
        }

        var results = scored
            .OrderByDescending(x => x.sim)
            .Take(maxItems)
            .ToList();

        // Update access tracking for returned segments
        foreach (var (seg, sim) in results)
        {
            seg.AccessCount++;
            seg.LastAccessedTurn = currentTurn;
            seg.LastSalience = sim;
        }

        return results.Select(r => r.seg.Segment).ToList();
    }

    /// <summary>
    ///     Evict stale and low-frequency segments.
    ///     Phase 1: Remove segments not accessed in the last <paramref name="staleTurns" /> turns.
    ///     Phase 2: If still over capacity, remove lowest AccessCount segments.
    /// </summary>
    public void Evict(int currentTurn, int staleTurns = 5)
    {
        // Phase 1: staleness eviction
        List<string>? staleIds = null;
        foreach (var (id, seg) in _segments)
        {
            if (currentTurn - seg.LastAccessedTurn > staleTurns)
                (staleIds ??= []).Add(id);
        }

        if (staleIds != null)
            foreach (var id in staleIds)
            {
                _segments.Remove(id);
                _totalEvicted++;
            }

        // Phase 2: LFU eviction when over capacity
        if (_segments.Count > _capacity)
        {
            var toRemove = _segments
                .OrderBy(kv => kv.Value.AccessCount)
                .ThenBy(kv => kv.Value.LastAccessedTurn)
                .Take(_segments.Count - _capacity)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var id in toRemove)
            {
                _segments.Remove(id);
                _totalEvicted++;
            }
        }
    }

    /// <summary>
    ///     Track which document source was concentrated in retrieval results.
    /// </summary>
    public void TrackDocumentFocus(string? concentratedSource)
    {
        if (concentratedSource == null)
            return;

        _documentFocusCounts[concentratedSource] =
            _documentFocusCounts.GetValueOrDefault(concentratedSource) + 1;
    }

    /// <summary>
    ///     Get document focus counts for progressive narrowing.
    /// </summary>
    public Dictionary<string, int> GetDocumentFocusCounts()
        => new(_documentFocusCounts);

    public sealed class CachedSegment
    {
        public required Segment Segment { get; init; }
        public int AccessCount { get; set; }
        public int TurnAdded { get; init; }
        public int LastAccessedTurn { get; set; }
        public float LastSalience { get; set; }
    }
}

/// <summary>
///     Thread-safe registry of per-conversation segment caches.
///     Scoped as singleton so caches persist across requests for the same conversation.
/// </summary>
public sealed class SalientSegmentCacheRegistry
{
    private readonly ConcurrentDictionary<Guid, (SalientSegmentCache cache, int turn)> _caches = new();

    /// <summary>
    ///     Get or create the cache for a conversation, returning the cache and current turn number.
    /// </summary>
    public (SalientSegmentCache cache, int turn) GetOrCreate(Guid conversationId)
    {
        return _caches.GetOrAdd(conversationId, _ => (new SalientSegmentCache(), 0));
    }

    /// <summary>
    ///     Increment the turn counter for a conversation and return the new turn number.
    /// </summary>
    public int IncrementTurn(Guid conversationId)
    {
        _caches.AddOrUpdate(
            conversationId,
            _ => (new SalientSegmentCache(), 1),
            (_, existing) => (existing.cache, existing.turn + 1));
        return _caches[conversationId].turn;
    }

    /// <summary>
    ///     Remove a conversation's cache (e.g., when conversation is deleted).
    /// </summary>
    public void Remove(Guid conversationId) => _caches.TryRemove(conversationId, out _);
}
