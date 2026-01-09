using Mostlylucid.DocSummarizer.Models;
using StyloFlowBm25 = StyloFlow.Retrieval.Bm25Scorer;
using StyloFlowBm25Corpus = StyloFlow.Retrieval.Bm25Corpus;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// BM25 (Best Matching 25) sparse retrieval scorer - Segment adapter.
///
/// Wraps StyloFlow.Retrieval.Bm25Scorer for use with Segment objects.
/// This adapter pattern eliminates code duplication while maintaining
/// the domain-specific API for DocSummarizer consumers.
///
/// For new code, consider using StyloFlow.Retrieval.Bm25Scorer directly.
/// </summary>
public class BM25Scorer
{
    private readonly StyloFlowBm25 _scorer;
    private StyloFlowBm25Corpus? _corpus;
    private List<Segment>? _segments;
    private bool _initialized;

    public BM25Scorer(double k1 = 1.5, double b = 0.75)
    {
        _scorer = new StyloFlowBm25(k1, b);
    }

    /// <summary>
    /// Initialize BM25 with corpus statistics from segments.
    /// </summary>
    public void Initialize(IEnumerable<Segment> segments)
    {
        _segments = segments.ToList();
        _corpus = StyloFlowBm25Corpus.Build(_segments.Select(s => StyloFlowBm25.Tokenize(s.Text)));
        _initialized = true;
    }

    /// <summary>
    /// Score a segment against a query using BM25.
    /// </summary>
    public double Score(Segment segment, string query)
    {
        if (!_initialized)
            throw new InvalidOperationException("BM25 not initialized. Call Initialize() first.");

        // Use the underlying StyloFlow scorer with pre-built corpus
        var scorer = new StyloFlowBm25(_corpus!, _scorer.Name == "BM25" ? 1.5 : 1.5, 0.75);
        return scorer.Score(query, segment.Text);
    }

    /// <summary>
    /// Score all segments against a query, returning ranked list.
    /// </summary>
    public List<(Segment segment, double score)> ScoreAll(IEnumerable<Segment> segments, string query)
    {
        if (!_initialized)
            throw new InvalidOperationException("BM25 not initialized. Call Initialize() first.");

        var scorer = new StyloFlowBm25(_corpus!);
        var results = scorer.ScoreAll(segments, s => s.Text, query);

        return results
            .Select(r => (r.Item, r.Score))
            .ToList();
    }
}

/// <summary>
/// Extended RRF that combines three signals: dense, sparse (BM25), and salience.
///
/// This remains Segment-specific as it manipulates domain objects directly.
/// For generic RRF, use StyloFlow.Retrieval.ReciprocalRankFusion.
/// </summary>
public static class HybridRRF
{
    /// <summary>
    /// Reciprocal Rank Fusion combining dense similarity, BM25, and salience scores.
    ///
    /// RRF(d) = 1/(k + rank_dense) + 1/(k + rank_bm25) + 1/(k + rank_salience)
    ///
    /// This three-way fusion captures:
    /// - Semantic similarity (dense embeddings)
    /// - Lexical matching (BM25 sparse)
    /// - Document importance (salience from extraction)
    /// </summary>
    public static List<Segment> Fuse(
        List<Segment> segments,
        string query,
        BM25Scorer bm25,
        int k = 60,
        int topK = 25)
    {
        // Rank by dense similarity (already computed on segments)
        var byDense = segments
            .Where(s => s.Embedding != null)
            .OrderByDescending(s => s.QuerySimilarity)
            .ToList();

        // Rank by BM25
        var byBM25 = bm25.ScoreAll(segments, query)
            .Select(x => x.segment)
            .ToList();

        // Rank by salience
        var bySalience = segments
            .OrderByDescending(s => s.SalienceScore)
            .ToList();

        // Compute RRF scores
        var rrfScores = new Dictionary<Segment, double>();

        // Dense ranking contribution
        for (int i = 0; i < byDense.Count; i++)
        {
            var segment = byDense[i];
            rrfScores[segment] = 1.0 / (k + i + 1);
        }

        // BM25 ranking contribution
        for (int i = 0; i < byBM25.Count; i++)
        {
            var segment = byBM25[i];
            if (rrfScores.ContainsKey(segment))
                rrfScores[segment] += 1.0 / (k + i + 1);
            else
                rrfScores[segment] = 1.0 / (k + i + 1);
        }

        // Salience ranking contribution
        for (int i = 0; i < bySalience.Count; i++)
        {
            var segment = bySalience[i];
            if (rrfScores.ContainsKey(segment))
                rrfScores[segment] += 1.0 / (k + i + 1);
            else
                rrfScores[segment] = 1.0 / (k + i + 1);
        }

        // Store final scores and return top-K
        foreach (var (segment, score) in rrfScores)
        {
            segment.RetrievalScore = score;
        }

        return rrfScores
            .OrderByDescending(kv => kv.Value)
            .Take(topK)
            .Select(kv => kv.Key)
            .ToList();
    }
}
