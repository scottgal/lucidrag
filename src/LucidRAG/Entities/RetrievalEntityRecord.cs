using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using StyloFlow.Retrieval.Entities;

namespace LucidRAG.Entities;

/// <summary>
/// Database record for cross-modal retrieval entities.
/// Supports documents, images, audio, video, and data.
/// Multi-vector storage for modality-specific embeddings.
/// </summary>
public class RetrievalEntityRecord
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Content type: document, image, audio, video, data, mixed
    /// </summary>
    [Required]
    [MaxLength(32)]
    public required string ContentType { get; set; }

    /// <summary>
    /// Source file path, URL, or identifier.
    /// </summary>
    [Required]
    [MaxLength(2048)]
    public required string Source { get; set; }

    /// <summary>
    /// SHA256 hash for deduplication.
    /// </summary>
    [MaxLength(64)]
    public string? ContentHash { get; set; }

    /// <summary>
    /// Collection/folder this entity belongs to.
    /// </summary>
    public Guid? CollectionId { get; set; }

    /// <summary>
    /// Title or filename.
    /// </summary>
    [MaxLength(512)]
    public string? Title { get; set; }

    /// <summary>
    /// Brief summary or caption.
    /// </summary>
    [MaxLength(4000)]
    public string? Summary { get; set; }

    /// <summary>
    /// Full text content for search (OCR, transcription, document text).
    /// </summary>
    public string? TextContent { get; set; }

    /// <summary>
    /// Primary semantic embedding model used.
    /// </summary>
    [MaxLength(128)]
    public string? EmbeddingModel { get; set; }

    /// <summary>
    /// Overall quality score (0-1).
    /// </summary>
    public double QualityScore { get; set; } = 1.0;

    /// <summary>
    /// Confidence in extracted content.
    /// </summary>
    public double ContentConfidence { get; set; } = 1.0;

    /// <summary>
    /// Whether this entity needs review.
    /// </summary>
    public bool NeedsReview { get; set; }

    /// <summary>
    /// Reason for needing review.
    /// </summary>
    [MaxLength(1000)]
    public string? ReviewReason { get; set; }

    /// <summary>
    /// Tags for categorization (JSON array).
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? Tags { get; set; }

    /// <summary>
    /// Content-type specific metadata (JSON).
    /// Width, height, duration, page count, etc.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? Metadata { get; set; }

    /// <summary>
    /// Custom user-defined metadata (JSON).
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? CustomMetadata { get; set; }

    /// <summary>
    /// Analysis signals from processing pipeline (JSON).
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? Signals { get; set; }

    /// <summary>
    /// Extracted entities (JSON array).
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? ExtractedEntities { get; set; }

    /// <summary>
    /// Entity relationships (JSON array).
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? Relationships { get; set; }

    /// <summary>
    /// Modalities present in this entity (e.g., ["visual", "text", "audio"]).
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? SourceModalities { get; set; }

    /// <summary>
    /// Processing state tracking which modalities have been processed (JSON).
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? ProcessingState { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public CollectionEntity? Collection { get; set; }

    /// <summary>
    /// Multi-vector embeddings for cross-modal search.
    /// </summary>
    public ICollection<EntityEmbedding> Embeddings { get; set; } = [];

    /// <summary>
    /// Evidence artifacts for this entity.
    /// </summary>
    public ICollection<EvidenceArtifact> EvidenceArtifacts { get; set; } = [];

    /// <summary>
    /// Page group memberships (for scanned page entities).
    /// </summary>
    public ICollection<ScannedPageMembership> PageMemberships { get; set; } = [];
}

/// <summary>
/// Multi-vector storage for different embedding types per entity.
/// Enables cross-modal search with modality-specific vectors.
/// </summary>
public class EntityEmbedding
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Parent entity ID.
    /// </summary>
    public Guid EntityId { get; set; }

    /// <summary>
    /// Embedding type/name (text, clip_visual, clip_text, audio, whisper, etc.)
    /// </summary>
    [Required]
    [MaxLength(64)]
    public required string Name { get; set; }

    /// <summary>
    /// Model used to generate this embedding.
    /// </summary>
    [MaxLength(128)]
    public string? Model { get; set; }

    /// <summary>
    /// Embedding dimension.
    /// </summary>
    public int Dimension { get; set; }

    /// <summary>
    /// The embedding vector (stored as JSON array or binary).
    /// For PostgreSQL, consider using pgvector extension.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? Vector { get; set; }

    /// <summary>
    /// Binary storage for large vectors (optional, for efficiency).
    /// </summary>
    public byte[]? VectorBinary { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public RetrievalEntityRecord? Entity { get; set; }
}

/// <summary>
/// Standard embedding names for cross-modal search.
/// </summary>
public static class EmbeddingNames
{
    /// <summary>Primary text embedding (all-MiniLM-L6-v2, etc.)</summary>
    public const string Text = "text";

    /// <summary>CLIP visual embedding from images.</summary>
    public const string ClipVisual = "clip_visual";

    /// <summary>CLIP text embedding (for text-to-image search).</summary>
    public const string ClipText = "clip_text";

    /// <summary>Audio embedding from Whisper or similar.</summary>
    public const string Audio = "audio";

    /// <summary>Speech embedding from transcription.</summary>
    public const string Speech = "speech";

    /// <summary>Video frame embedding (averaged or key frames).</summary>
    public const string VideoFrame = "video_frame";

    /// <summary>Code-specific embedding.</summary>
    public const string Code = "code";

    /// <summary>Table/structured data embedding.</summary>
    public const string Data = "data";
}
