namespace LucidRAG.Services.Sentinel;

/// <summary>
///     Semantic router that auto-selects which collection(s) to search
///     based on query content matched against collection salient terms
///     and feature embeddings.
///     Analogous to DoomSummarizer's SentinelSourceMapper, adapted for
///     LucidRAG's multi-collection document architecture.
/// </summary>
public interface ICollectionRouter
{
    /// <summary>
    ///     Route a query to the best-matching collection(s).
    ///     Returns null if no collection is confidently matched (search all).
    /// </summary>
    /// <param name="query">The user's natural language query.</param>
    /// <param name="queryPlan">Optional QueryPlan from Sentinel (provides intent, sub-queries).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Routing result with ranked collections, or empty if no confident match.</returns>
    Task<CollectionRoutingResult> RouteAsync(
        string query,
        QueryPlan? queryPlan = null,
        CancellationToken ct = default);
}

/// <summary>
///     Result of collection routing with ranked candidates.
/// </summary>
public record CollectionRoutingResult
{
    /// <summary>
    ///     Ranked collection candidates, highest confidence first.
    /// </summary>
    public List<CollectionCandidate> Candidates { get; init; } = [];

    /// <summary>
    ///     The top collection ID if confidence exceeds the threshold, otherwise null.
    /// </summary>
    public Guid? BestCollectionId => Candidates.Count > 0 && Candidates[0].Confidence >= ConfidenceThreshold
        ? Candidates[0].CollectionId
        : null;

    /// <summary>
    ///     Confidence threshold used for routing decisions.
    /// </summary>
    public double ConfidenceThreshold { get; init; } = 0.3;

    /// <summary>
    ///     Whether routing produced a confident match.
    /// </summary>
    public bool HasConfidentMatch => BestCollectionId.HasValue;

    /// <summary>
    ///     Human-readable explanation of the routing decision (for transparency/logging).
    /// </summary>
    public string? Explanation { get; init; }
}

/// <summary>
///     A candidate collection with its routing confidence score.
/// </summary>
public record CollectionCandidate(
    Guid CollectionId,
    string Name,
    double Confidence,
    int MatchedTermCount,
    List<string> MatchedTerms);
