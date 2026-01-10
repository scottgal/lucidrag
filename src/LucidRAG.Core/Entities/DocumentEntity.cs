namespace LucidRAG.Entities;

public class DocumentEntity
{
    public Guid Id { get; set; }
    public Guid? CollectionId { get; set; }
    public required string Name { get; set; }
    public string? OriginalFilename { get; set; }
    public required string ContentHash { get; set; }
    public string? FilePath { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? MimeType { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;
    public string? StatusMessage { get; set; }
    public float ProcessingProgress { get; set; }
    public int SegmentCount { get; set; }
    public int EntityCount { get; set; }
    public int TableCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? Metadata { get; set; }

    /// <summary>
    /// Source URL for crawled web pages. Null for uploaded files.
    /// </summary>
    public string? SourceUrl { get; set; }

    /// <summary>
    /// Source file path for imported files. Used for change detection on re-import.
    /// Combined with CollectionId to form a unique identifier.
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Original creation date of the source file (preserved from filesystem).
    /// </summary>
    public DateTimeOffset? SourceCreatedAt { get; set; }

    /// <summary>
    /// Last modified date of the source file at time of import.
    /// Used for change detection.
    /// </summary>
    public DateTimeOffset? SourceModifiedAt { get; set; }

    /// <summary>
    /// Number of times this document has been updated/reimported.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// The document ID used in the vector store (stableDocId).
    /// Format: {sanitized_filename}_{content_hash_from_canonicalized_markdown}
    /// Set after processing completes.
    /// </summary>
    public string? VectorStoreDocId { get; set; }

    // Navigation
    public CollectionEntity? Collection { get; set; }
    public ICollection<DocumentEntityLink> EntityLinks { get; set; } = [];
}

public enum DocumentStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}
