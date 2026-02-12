namespace LucidRAG.Entities;

/// <summary>
///     Records processing lifecycle signals for documents.
///     Follows the SaasQueryLogEntity pattern for time-series event storage.
/// </summary>
public class ProcessingSignalEntity
{
    public Guid Id { get; set; }

    /// <summary>
    ///     Signal type: staging.created, processing.started, processing.completed,
    ///     processing.failed, processing.escalated, staging.cleaned, orphan.detected
    /// </summary>
    public required string SignalType { get; set; }

    /// <summary>
    ///     Correlation ID linking all signals from one operation
    ///     (e.g., import → staging → processing → completion).
    /// </summary>
    public Guid CorrelationId { get; set; }

    /// <summary>
    ///     The document this signal relates to.
    /// </summary>
    public Guid? DocumentId { get; set; }

    /// <summary>
    ///     Collection/tenant scope.
    /// </summary>
    public Guid? CollectionId { get; set; }

    /// <summary>
    ///     S3 staging path (for staging signals).
    /// </summary>
    public string? StagingPath { get; set; }

    /// <summary>
    ///     Optional detail message (error messages, status info).
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    ///     Optional JSON metadata for extensible signal data.
    /// </summary>
    public string? Metadata { get; set; }

    /// <summary>
    ///     Processing duration in milliseconds (for completed/failed signals).
    /// </summary>
    public long? DurationMs { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
///     Well-known signal types for processing lifecycle.
/// </summary>
public static class ProcessingSignalTypes
{
    public const string StagingCreated = "staging.created";
    public const string ProcessingStarted = "processing.started";
    public const string ProcessingCompleted = "processing.completed";
    public const string ProcessingFailed = "processing.failed";
    public const string ProcessingEscalated = "processing.escalated";
    public const string StagingCleaned = "staging.cleaned";
    public const string OrphanDetected = "orphan.detected";
    public const string ProcessingRetried = "processing.retried";
}
