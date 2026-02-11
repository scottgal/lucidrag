namespace LucidRAG.Entities;

/// <summary>
///     Pre-aggregated daily usage stats per API key. Background service rolls up from query logs.
/// </summary>
public class SaasUsageRollupEntity
{
    public Guid Id { get; set; }
    public Guid ApiKeyId { get; set; }
    public DateOnly Date { get; set; }

    public long SearchCount { get; set; }
    public long ChatCount { get; set; }
    public long AutocompleteCount { get; set; }
    public long FailedCount { get; set; }

    public int AvgResponseTimeMs { get; set; }
    public int P95ResponseTimeMs { get; set; }
    public int P99ResponseTimeMs { get; set; }

    public DateTimeOffset AggregatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public ApiKeyEntity? ApiKey { get; set; }
}
