namespace LucidRAG.Models;

/// <summary>
/// Request to create an ingestion source
/// </summary>
public record CreateIngestionSourceRequest(
    /// <summary>
    /// Display name for the source
    /// </summary>
    string Name,

    /// <summary>
    /// Source type: "directory", "github", "ftp", "s3"
    /// </summary>
    string SourceType,

    /// <summary>
    /// Location (path, URL, bucket, etc.)
    /// </summary>
    string Location,

    /// <summary>
    /// File pattern filter (e.g., "*.pdf", "**/*.md")
    /// </summary>
    string? FilePattern = null,

    /// <summary>
    /// Whether to scan subdirectories
    /// </summary>
    bool Recursive = true,

    /// <summary>
    /// Collection to add ingested documents to
    /// </summary>
    Guid? CollectionId = null,

    /// <summary>
    /// Additional source-specific options
    /// </summary>
    Dictionary<string, object>? Options = null
);

/// <summary>
/// Request to start an ingestion job
/// </summary>
public record StartIngestionRequest(
    /// <summary>
    /// Source ID to ingest from
    /// </summary>
    Guid SourceId,

    /// <summary>
    /// Only process items modified since last sync
    /// </summary>
    bool IncrementalSync = true,

    /// <summary>
    /// Maximum items to process (0 = unlimited)
    /// </summary>
    int MaxItems = 0,

    /// <summary>
    /// Continue processing on individual item errors
    /// </summary>
    bool ContinueOnError = true,

    /// <summary>
    /// Job priority (lower = higher priority)
    /// </summary>
    int Priority = 100
);

/// <summary>
/// Registered ingestion source
/// </summary>
public record IngestionSourceInfo(
    Guid Id,
    string Name,
    string SourceType,
    string Location,
    string? FilePattern,
    bool Recursive,
    Guid? CollectionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSyncAt,
    int TotalItemsIngested
);

/// <summary>
/// Active ingestion job
/// </summary>
public record IngestionJobInfo(
    Guid JobId,
    Guid SourceId,
    string SourceName,
    string SourceType,
    IngestionJobStatus Status,
    int ItemsDiscovered,
    int ItemsProcessed,
    int ItemsFailed,
    int ItemsSkipped,
    string? CurrentItem,
    string? ErrorMessage,
    double Progress,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt
);

/// <summary>
/// Status of an ingestion job
/// </summary>
public enum IngestionJobStatus
{
    Pending,
    Queued,
    Discovering,
    Processing,
    Cancelling,
    Cancelled,
    Completed,
    CompletedWithErrors,
    Failed
}

/// <summary>
/// Progress update for SSE streaming
/// </summary>
public record IngestionProgress(
    Guid JobId,
    Guid SourceId,
    int ItemsDiscovered,
    int ItemsProcessed,
    int ItemsFailed,
    int ItemsSkipped,
    string? CurrentItem,
    IngestionJobStatus Status,
    double Progress,
    string? ErrorMessage = null
);

/// <summary>
/// Response after starting an ingestion job
/// </summary>
public record IngestionStartResponse(
    Guid JobId,
    Guid SourceId,
    string Message
);

/// <summary>
/// Signal emitted during ingestion
/// </summary>
public record IngestionSignal(
    string SignalType,
    Guid JobId,
    object Payload,
    DateTimeOffset EmittedAt
);
