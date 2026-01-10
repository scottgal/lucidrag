namespace LucidRAG.Services;

/// <summary>
/// Service for extracting and managing salient terms for collection autocomplete.
/// Uses TF-IDF, entity extraction, and RRF (Reciprocal Rank Fusion) to combine signals.
/// </summary>
public interface ISalientTermsService
{
    /// <summary>
    /// Extract and update salient terms for a collection.
    /// Combines TF-IDF from document content, entity names, and query patterns using RRF.
    /// </summary>
    Task UpdateCollectionTermsAsync(Guid collectionId, CancellationToken ct = default);

    /// <summary>
    /// Get autocomplete suggestions for a query prefix within a collection.
    /// Returns pre-computed salient terms ranked by relevance.
    /// </summary>
    Task<List<SalientTermSuggestion>> GetAutocompleteSuggestionsAsync(
        Guid collectionId,
        string queryPrefix,
        int maxResults = 10,
        CancellationToken ct = default);

    /// <summary>
    /// Update salient terms for all collections.
    /// Called periodically by background job.
    /// </summary>
    Task UpdateAllCollectionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Get statistics about salient terms for a collection.
    /// </summary>
    Task<SalientTermStats> GetStatsAsync(Guid collectionId, CancellationToken ct = default);
}

public record SalientTermSuggestion(
    string Term,
    double Score,
    string Source,
    int DocumentFrequency
);

public record SalientTermStats(
    Guid CollectionId,
    int TotalTerms,
    int TfIdfTerms,
    int EntityTerms,
    int QueryTerms,
    DateTimeOffset LastUpdated
);
