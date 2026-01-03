using System.Text.RegularExpressions;
using Mostlylucid.GraphRag.Services;
using Mostlylucid.GraphRag.Storage;

namespace Mostlylucid.GraphRag.Search;

/// <summary>
/// Hybrid search: BERT dense + BM25 sparse via RRF fusion.
/// </summary>
public sealed class SearchService
{
    private readonly GraphRagDb _db;
    private readonly EmbeddingService _embedder;
    private readonly int _rrfK;
    
    // BM25 corpus stats
    private Dictionary<string, double>? _idf;
    private double _avgDocLen;
    private int _corpusSize;

    public SearchService(GraphRagDb db, EmbeddingService embedder, int rrfK = 60)
    {
        _db = db;
        _embedder = embedder;
        _rrfK = rrfK;
    }

    public async Task InitializeBM25Async()
    {
        var chunks = await _db.GetAllChunksAsync();
        _corpusSize = chunks.Count;
        if (_corpusSize == 0) return;

        var docFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long totalLen = 0;

        foreach (var c in chunks)
        {
            var tokens = Tokenize(c.Text);
            totalLen += tokens.Count;
            foreach (var t in tokens.Distinct(StringComparer.OrdinalIgnoreCase))
                docFreq[t] = docFreq.GetValueOrDefault(t) + 1;
        }

        _avgDocLen = (double)totalLen / _corpusSize;
        _idf = docFreq.ToDictionary(kv => kv.Key, kv => Math.Log((_corpusSize - kv.Value + 0.5) / (kv.Value + 0.5) + 1), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<List<SearchResult>> SearchAsync(string query, int topK = 10, CancellationToken ct = default)
    {
        if (_idf == null) await InitializeBM25Async();

        // Dense search
        var queryEmb = await _embedder.EmbedAsync(query, ct);
        var denseResults = await _db.SearchChunksAsync(queryEmb, topK * 2);

        // BM25 sparse search
        var allChunks = await _db.GetAllChunksAsync();
        var bm25Scores = allChunks.Select(c => (c, Score: BM25Score(c.Text, query)))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(topK * 2).ToList();

        // RRF fusion
        var rrfScores = new Dictionary<string, (double Score, ChunkResult Chunk)>();

        for (int i = 0; i < denseResults.Count; i++)
        {
            var c = denseResults[i];
            rrfScores[c.Id] = (1.0 / (_rrfK + i + 1), c);
        }

        for (int i = 0; i < bm25Scores.Count; i++)
        {
            var (c, _) = bm25Scores[i];
            var score = 1.0 / (_rrfK + i + 1);
            if (rrfScores.TryGetValue(c.Id, out var existing))
                rrfScores[c.Id] = (existing.Score + score, existing.Chunk);
            else
                rrfScores[c.Id] = (score, c);
        }

        var results = rrfScores.OrderByDescending(kv => kv.Value.Score).Take(topK).ToList();
        var searchResults = new List<SearchResult>();

        foreach (var (id, (score, chunk)) in results)
        {
            // Find related entities
            var entities = await FindEntitiesInTextAsync(chunk.Text);
            var relationships = new List<RelationshipResult>();
            foreach (var e in entities.Take(3))
                relationships.AddRange(await _db.GetRelationshipsForEntityAsync(e.Id));

            searchResults.Add(new SearchResult(chunk.Id, chunk.DocumentId, chunk.Text, score, chunk.Similarity, entities, relationships.DistinctBy(r => r.Id).ToList()));
        }

        return searchResults;
    }

    private double BM25Score(string doc, string query)
    {
        if (_idf == null || _avgDocLen == 0) return 0;
        const double k1 = 1.5, b = 0.75;

        var queryTokens = Tokenize(query);
        var docTokens = Tokenize(doc);
        var tf = docTokens.GroupBy(t => t, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        double score = 0;
        foreach (var term in queryTokens.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!tf.TryGetValue(term, out var freq) || !_idf.TryGetValue(term, out var idf)) continue;
            score += idf * freq * (k1 + 1) / (freq + k1 * (1 - b + b * docTokens.Count / _avgDocLen));
        }
        return score;
    }

    private async Task<List<EntityResult>> FindEntitiesInTextAsync(string text)
    {
        var all = await _db.GetAllEntitiesAsync();
        var lower = text.ToLowerInvariant();
        return all.Where(e => lower.Contains(e.Name.ToLowerInvariant())).ToList();
    }

    private static readonly Regex TokenRx = new(@"\b\w+\b", RegexOptions.Compiled);
    private static List<string> Tokenize(string text) => TokenRx.Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(t => t.Length > 1).ToList();
}

public record SearchResult(string ChunkId, string DocumentId, string Text, double Score, float DenseSim, List<EntityResult> Entities, List<RelationshipResult> Relationships);
