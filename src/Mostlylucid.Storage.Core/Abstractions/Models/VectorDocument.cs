namespace Mostlylucid.Storage.Core.Abstractions.Models;

/// <summary>
/// Generic vector document for unified storage.
/// Replaces Segment (DocSummarizer), ProfileEntry (DataSummarizer), and BlogPostDocument (RAG).
/// </summary>
public class VectorDocument
{
    /// <summary>
    /// Unique identifier for this document.
    /// For segments: {parentDocId}:{segmentIndex}
    /// For images: {imageHash}:{facet} (e.g., "abc123:text", "abc123:visual")
    /// </summary>
    public required string Id { get; set; }

    /// <summary>
    /// Vector embedding (dimension determined by collection schema).
    /// </summary>
    public required float[] Embedding { get; set; }

    /// <summary>
    /// Optional parent document ID (for grouping segments).
    /// </summary>
    public string? ParentId { get; set; }

    /// <summary>
    /// Content hash for deduplication (XxHash64).
    /// Used to detect unchanged content and reuse embeddings.
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// Original text content (optional - may be omitted for privacy).
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// Arbitrary metadata fields for filtering and retrieval.
    /// Examples: { "type": "ocr", "index": 0, "language": "en", "confidence": 0.95 }
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// When this document was created/indexed.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When this document was last updated.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// Schema definition for a vector collection.
/// </summary>
public class VectorStoreSchema
{
    /// <summary>
    /// Vector dimension (e.g., 128, 384, 512, 1536).
    /// Must match the embedding model's output dimension.
    /// </summary>
    public required int VectorDimension { get; set; }

    /// <summary>
    /// Distance metric for similarity calculation.
    /// </summary>
    public VectorDistance DistanceMetric { get; set; } = VectorDistance.Cosine;

    /// <summary>
    /// Optional metadata fields with types for filtering/indexing.
    /// </summary>
    public List<MetadataField> MetadataFields { get; set; } = new();

    /// <summary>
    /// Whether to store text content in the vector store.
    /// Set to false for privacy-preserving deployments.
    /// </summary>
    public bool StoreText { get; set; } = true;
}

/// <summary>
/// Distance metric for vector similarity.
/// </summary>
public enum VectorDistance
{
    /// <summary>
    /// Cosine similarity (default) - range [0, 1], normalized.
    /// Best for: Text embeddings, BERT-style models.
    /// </summary>
    Cosine,

    /// <summary>
    /// Euclidean distance (L2) - unnormalized distance.
    /// Best for: Image embeddings, spatial data.
    /// </summary>
    Euclidean,

    /// <summary>
    /// Dot product - raw inner product.
    /// Best for: Pre-normalized embeddings.
    /// </summary>
    DotProduct
}

/// <summary>
/// Metadata field definition for indexing.
/// </summary>
public class MetadataField
{
    public required string Name { get; set; }
    public MetadataFieldType Type { get; set; }
    public bool Indexed { get; set; } = false;
}

/// <summary>
/// Metadata field types for filtering.
/// </summary>
public enum MetadataFieldType
{
    String,
    Integer,
    Float,
    Boolean,
    DateTime,
    StringArray
}

/// <summary>
/// Cached summary for a document.
/// Used to avoid re-summarizing the same content.
/// </summary>
public class CachedSummary
{
    public required string DocumentId { get; set; }
    public required string Summary { get; set; }
    public string? Model { get; set; }
    public DateTimeOffset CachedAt { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, object> Metadata { get; set; } = new();
}
