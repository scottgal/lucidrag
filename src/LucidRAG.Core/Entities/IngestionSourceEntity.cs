namespace LucidRAG.Entities;

/// <summary>
/// Registered source for content ingestion.
/// </summary>
public class IngestionSourceEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string SourceType { get; set; }  // "directory", "github", "ftp", "s3"
    public required string Location { get; set; }
    public string? FilePattern { get; set; }
    public bool Recursive { get; set; } = true;
    public Guid? CollectionId { get; set; }
    public string? Options { get; set; }  // JSON for source-specific options
    public string? Credentials { get; set; }  // Encrypted credentials/connection string
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSyncAt { get; set; }
    public int TotalItemsIngested { get; set; }
    public bool IsEnabled { get; set; } = true;

    // Navigation
    public CollectionEntity? Collection { get; set; }
    public ICollection<IngestionJobEntity> Jobs { get; set; } = [];
}

/// <summary>
/// Record of an ingestion job execution.
/// </summary>
public class IngestionJobEntity
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public required string Status { get; set; }
    public int ItemsDiscovered { get; set; }
    public int ItemsProcessed { get; set; }
    public int ItemsFailed { get; set; }
    public int ItemsSkipped { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Errors { get; set; }  // JSON array of item errors
    public bool IncrementalSync { get; set; }
    public int MaxItems { get; set; }
    public int Priority { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // Navigation
    public IngestionSourceEntity? Source { get; set; }
}
