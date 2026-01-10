namespace LucidRAG.Entities;

/// <summary>
/// Stores pre-computed salient terms for collection autocomplete.
/// Periodically updated based on document content, entities, and query patterns.
/// </summary>
public class CollectionSalientTerm
{
    public Guid Id { get; set; }
    public Guid CollectionId { get; set; }

    /// <summary>
    /// The salient term (e.g., "machine learning", "neural network")
    /// </summary>
    public required string Term { get; set; }

    /// <summary>
    /// Normalized term for matching (lowercase, trimmed)
    /// </summary>
    public required string NormalizedTerm { get; set; }

    /// <summary>
    /// Combined RRF score from multiple signals (TF-IDF, entity frequency, query patterns)
    /// </summary>
    public double Score { get; set; }

    /// <summary>
    /// Source of the term: "tfidf", "entity", "query", "combined"
    /// </summary>
    public required string Source { get; set; }

    /// <summary>
    /// Number of documents containing this term
    /// </summary>
    public int DocumentFrequency { get; set; }

    /// <summary>
    /// Last time this term was updated
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public CollectionEntity? Collection { get; set; }
}
