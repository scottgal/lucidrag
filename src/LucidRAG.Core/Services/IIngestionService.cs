using LucidRAG.Models;

namespace LucidRAG.Services;

/// <summary>
/// Service for managing content ingestion from external sources.
/// </summary>
public interface IIngestionService
{
    /// <summary>
    /// Register a new ingestion source.
    /// </summary>
    Task<IngestionSourceInfo> CreateSourceAsync(CreateIngestionSourceRequest request, CancellationToken ct = default);

    /// <summary>
    /// Get all registered ingestion sources.
    /// </summary>
    Task<IReadOnlyList<IngestionSourceInfo>> GetSourcesAsync(CancellationToken ct = default);

    /// <summary>
    /// Get a specific ingestion source.
    /// </summary>
    Task<IngestionSourceInfo?> GetSourceAsync(Guid sourceId, CancellationToken ct = default);

    /// <summary>
    /// Delete an ingestion source.
    /// </summary>
    Task<bool> DeleteSourceAsync(Guid sourceId, CancellationToken ct = default);

    /// <summary>
    /// Start an ingestion job for a source.
    /// </summary>
    Task<Guid> StartIngestionAsync(StartIngestionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Get status of an ingestion job.
    /// </summary>
    IngestionJobInfo? GetJob(Guid jobId);

    /// <summary>
    /// Get all active and recent ingestion jobs.
    /// </summary>
    IEnumerable<IngestionJobInfo> GetJobs(Guid? sourceId = null);

    /// <summary>
    /// Cancel an active ingestion job.
    /// </summary>
    Task<bool> CancelJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Stream progress updates for an ingestion job.
    /// </summary>
    IAsyncEnumerable<IngestionProgress> StreamProgressAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Get signals emitted by an ingestion job.
    /// </summary>
    IReadOnlyList<IngestionSignal> GetJobSignals(Guid jobId);

    /// <summary>
    /// Subscribe to signals for pipeline routing.
    /// </summary>
    event EventHandler<IngestionSignal>? SignalEmitted;
}
