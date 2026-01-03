using Microsoft.Extensions.Logging;
using Mostlylucid.RAG.Models;

namespace Mostlylucid.RAG.Services;

/// <summary>
/// Hybrid search service combining PostgreSQL full-text search with semantic vector search
/// Uses Reciprocal Rank Fusion (RRF) to merge results from both sources
/// </summary>
public class HybridSearchService : IHybridSearchService
{
    private readonly ILogger<HybridSearchService> _logger;
    private readonly ISemanticSearchService _semanticSearchService;
    private const int RrfConstant = 60; // Standard RRF k parameter

    public HybridSearchService(
        ILogger<HybridSearchService> logger,
        ISemanticSearchService semanticSearchService)
    {
        _logger = logger;
        _semanticSearchService = semanticSearchService;
    }

    public async Task<List<SearchResult>> SearchAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<SearchResult>();

        try
        {
            // Execute semantic search
            var semanticResults = await _semanticSearchService.SearchAsync(
                query,
                limit * 2, // Get more results for better fusion
                cancellationToken);

            _logger.LogDebug(
                "Hybrid search for '{Query}': Semantic={SemanticCount}",
                query, semanticResults.Count);

            // Apply Reciprocal Rank Fusion
            var fusedResults = ApplyReciprocalRankFusion(semanticResults);

            // Return top results
            var finalResults = fusedResults
                .Take(limit)
                .ToList();

            _logger.LogInformation(
                "Hybrid search returned {Count} results for '{Query}'",
                finalResults.Count, query);

            return finalResults;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hybrid search failed for query '{Query}'", query);
            return new List<SearchResult>();
        }
    }

    /// <summary>
    /// Applies Reciprocal Rank Fusion to combine results from multiple sources
    /// RRF score = S(1 / (k + rank)) where k is typically 60
    /// </summary>
    private List<SearchResult> ApplyReciprocalRankFusion(
        List<SearchResult> semanticResults)
    {
        var rrfScores = new Dictionary<string, RrfScore>();

        // Process semantic results
        for (int i = 0; i < semanticResults.Count; i++)
        {
            var result = semanticResults[i];
            var key = GetResultKey(result);

            if (!rrfScores.ContainsKey(key))
            {
                rrfScores[key] = new RrfScore
                {
                    Result = result,
                    SemanticRank = i + 1
                };
            }

            // RRF formula: 1 / (k + rank)
            var rrfScore = 1.0 / (RrfConstant + i + 1);
            rrfScores[key].Score += rrfScore;
            rrfScores[key].SemanticScore = result.Score;
        }

        // Sort by combined RRF score (descending)
        var rankedResults = rrfScores.Values
            .OrderByDescending(x => x.Score)
            .Select(x =>
            {
                // Update the score to reflect the combined RRF score
                x.Result.Score = (float)x.Score;
                return x.Result;
            })
            .ToList();

        _logger.LogDebug(
            "RRF fusion: Combined {UniqueCount} unique results from {SemanticCount} semantic results",
            rrfScores.Count, semanticResults.Count);

        return rankedResults;
    }

    /// <summary>
    /// Generates a unique key for deduplication based on slug
    /// </summary>
    private string GetResultKey(SearchResult result)
    {
        return result.Slug;
    }

    /// <summary>
    /// Internal class to track RRF scoring
    /// </summary>
    private class RrfScore
    {
        public required SearchResult Result { get; set; }
        public double Score { get; set; }
        public int? SemanticRank { get; set; }
        public double SemanticScore { get; set; }
    }
}
