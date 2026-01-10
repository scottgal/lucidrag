namespace Mostlylucid.Storage.Core.Abstractions.Models;

/// <summary>
/// Query for vector similarity search.
/// </summary>
public class VectorSearchQuery
{
    /// <summary>
    /// Query embedding vector.
    /// </summary>
    public required float[] QueryEmbedding { get; set; }

    /// <summary>
    /// Number of results to return (default: 10).
    /// </summary>
    public int TopK { get; set; } = 10;

    /// <summary>
    /// Minimum similarity score (0.0 - 1.0 for cosine).
    /// Results below this threshold are filtered out.
    /// </summary>
    public double MinScore { get; set; } = 0.0;

    /// <summary>
    /// Maximum similarity score (1.0 for cosine).
    /// Useful for filtering out exact duplicates (score = 1.0).
    /// </summary>
    public double? MaxScore { get; set; }

    /// <summary>
    /// Optional metadata filters.
    /// Examples: { "type": "ocr", "language": "en", "confidence_gte": 0.8 }
    /// </summary>
    public Dictionary<string, object>? Filters { get; set; }

    /// <summary>
    /// Optional parent ID filter (return only documents with this parent).
    /// </summary>
    public string? ParentId { get; set; }

    /// <summary>
    /// Whether to include the full document in results (default: false for privacy).
    /// If false, only ID and score are returned.
    /// </summary>
    public bool IncludeDocument { get; set; } = false;

    /// <summary>
    /// Whether to include embedding vectors in results (default: false).
    /// Embeddings are large - only include if needed for further processing.
    /// </summary>
    public bool IncludeEmbedding { get; set; } = false;
}

/// <summary>
/// Result from vector similarity search.
/// </summary>
public class VectorSearchResult
{
    /// <summary>
    /// Document ID.
    /// </summary>
    public required string Id { get; set; }

    /// <summary>
    /// Similarity score (0.0 - 1.0 for cosine, higher = more similar).
    /// </summary>
    public required double Score { get; set; }

    /// <summary>
    /// Distance from query (depends on metric - lower = more similar for Euclidean).
    /// </summary>
    public double? Distance { get; set; }

    /// <summary>
    /// Full document (only included if IncludeDocument = true in query).
    /// </summary>
    public VectorDocument? Document { get; set; }

    /// <summary>
    /// Metadata extracted from document (always included for convenience).
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// Text snippet (if available and requested).
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// Parent document ID (if document is a segment).
    /// </summary>
    public string? ParentId { get; set; }
}
