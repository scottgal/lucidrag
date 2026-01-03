using System.Text.RegularExpressions;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// BM25 (Best Matching 25) sparse retrieval scorer.
/// 
/// Provides lexical/keyword matching to complement dense semantic search.
/// Particularly good for:
/// - Exact term matches (proper nouns, technical terms)
/// - Rare words that embeddings may not capture well
/// - Code snippets and identifiers
/// 
/// Combined with dense retrieval via RRF for hybrid search.
/// </summary>
public class BM25Scorer
{
    // BM25 parameters (standard values from literature)
    private readonly double _k1;  // Term frequency saturation
    private readonly double _b;   // Length normalization
    
    // Corpus statistics (computed once)
    private Dictionary<string, double> _idf = new();
    private double _avgDocLength;
    private int _corpusSize;
    private bool _initialized;
    
    // Simple tokenizer pattern
    private static readonly Regex TokenPattern = new(@"\b\w+\b", RegexOptions.Compiled);
    
    public BM25Scorer(double k1 = 1.5, double b = 0.75)
    {
        _k1 = k1;
        _b = b;
    }

    /// <summary>
    /// Initialize BM25 with corpus statistics from segments
    /// </summary>
    public void Initialize(IEnumerable<Segment> segments)
    {
        var segmentList = segments.ToList();
        _corpusSize = segmentList.Count;
        
        // Document frequencies (how many docs contain each term)
        var docFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var totalLength = 0L;
        
        foreach (var segment in segmentList)
        {
            var tokens = Tokenize(segment.Text);
            var uniqueTokens = tokens.Distinct(StringComparer.OrdinalIgnoreCase);
            
            totalLength += tokens.Count;
            
            foreach (var token in uniqueTokens)
            {
                docFreq[token] = docFreq.GetValueOrDefault(token) + 1;
            }
        }
        
        _avgDocLength = (double)totalLength / _corpusSize;
        
        // Compute IDF for each term
        // IDF = log((N - df + 0.5) / (df + 0.5) + 1)
        _idf = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (term, df) in docFreq)
        {
            _idf[term] = Math.Log((_corpusSize - df + 0.5) / (df + 0.5) + 1);
        }
        
        _initialized = true;
    }

    /// <summary>
    /// Score a segment against a query using BM25
    /// </summary>
    public double Score(Segment segment, string query)
    {
        if (!_initialized)
            throw new InvalidOperationException("BM25 not initialized. Call Initialize() first.");
        
        var queryTokens = Tokenize(query);
        var docTokens = Tokenize(segment.Text);
        var docLength = docTokens.Count;
        
        // Count term frequencies in document
        var termFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in docTokens)
        {
            termFreq[token] = termFreq.GetValueOrDefault(token) + 1;
        }
        
        // BM25 score
        double score = 0;
        foreach (var term in queryTokens.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!termFreq.TryGetValue(term, out var tf)) continue;
            if (!_idf.TryGetValue(term, out var idf)) continue;
            
            // BM25 formula
            var numerator = tf * (_k1 + 1);
            var denominator = tf + _k1 * (1 - _b + _b * docLength / _avgDocLength);
            score += idf * numerator / denominator;
        }
        
        return score;
    }

    /// <summary>
    /// Score all segments against a query, returning ranked list
    /// </summary>
    public List<(Segment segment, double score)> ScoreAll(IEnumerable<Segment> segments, string query)
    {
        return segments
            .Select(s => (segment: s, score: Score(s, query)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ToList();
    }

    /// <summary>
    /// Simple tokenization (lowercase, alphanumeric words)
    /// </summary>
    private static List<string> Tokenize(string text)
    {
        return TokenPattern.Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(t => t.Length > 1) // Skip single chars
            .ToList();
    }
}

/// <summary>
/// Extended RRF that combines three signals: dense, sparse (BM25), and salience
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
