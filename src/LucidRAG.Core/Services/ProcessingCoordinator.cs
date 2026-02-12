using LucidRAG.Data;
using LucidRAG.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LucidRAG.Services;

/// <summary>
///     Ephemeral per-operation coordinator for emitting processing signals.
///     Create one per import/processing operation to correlate all related signals.
/// </summary>
public class ProcessingCoordinator
{
    private readonly RagDocumentsDbContext _db;
    private readonly ILogger<ProcessingCoordinator> _logger;
    private readonly DateTimeOffset _operationStart;

    public ProcessingCoordinator(
        RagDocumentsDbContext db,
        ILogger<ProcessingCoordinator> logger)
    {
        _db = db;
        _logger = logger;
        CorrelationId = Guid.NewGuid();
        _operationStart = DateTimeOffset.UtcNow;
    }

    /// <summary>
    ///     Unique ID correlating all signals from this operation.
    /// </summary>
    public Guid CorrelationId { get; }

    /// <summary>
    ///     Create a new coordinator with a fresh correlation ID.
    ///     Use the factory method to get a coordinator from DI scope.
    /// </summary>
    public static ProcessingCoordinator Create(IServiceProvider sp)
    {
        return new ProcessingCoordinator(
            sp.GetRequiredService<RagDocumentsDbContext>(),
            sp.GetRequiredService<ILogger<ProcessingCoordinator>>());
    }

    /// <summary>
    ///     Resume an existing correlation (e.g., when the background processor
    ///     picks up a job that was staged by the controller).
    /// </summary>
    public static ProcessingCoordinator Resume(IServiceProvider sp, Guid correlationId)
    {
        var coordinator = Create(sp);
        // Use reflection-free approach: create new instance with existing correlation
        return new ProcessingCoordinator(
            sp.GetRequiredService<RagDocumentsDbContext>(),
            sp.GetRequiredService<ILogger<ProcessingCoordinator>>(),
            correlationId);
    }

    private ProcessingCoordinator(
        RagDocumentsDbContext db,
        ILogger<ProcessingCoordinator> logger,
        Guid correlationId)
    {
        _db = db;
        _logger = logger;
        CorrelationId = correlationId;
        _operationStart = DateTimeOffset.UtcNow;
    }

    public async Task EmitStagingCreatedAsync(
        Guid documentId, Guid? collectionId, string stagingPath, CancellationToken ct = default)
    {
        await EmitAsync(ProcessingSignalTypes.StagingCreated, documentId, collectionId,
            stagingPath: stagingPath, ct: ct);
    }

    public async Task EmitProcessingStartedAsync(
        Guid documentId, Guid? collectionId, CancellationToken ct = default)
    {
        await EmitAsync(ProcessingSignalTypes.ProcessingStarted, documentId, collectionId, ct: ct);
    }

    public async Task EmitProcessingCompletedAsync(
        Guid documentId, Guid? collectionId, int segmentCount = 0, int entityCount = 0,
        CancellationToken ct = default)
    {
        var durationMs = (long)(DateTimeOffset.UtcNow - _operationStart).TotalMilliseconds;
        var metadata = segmentCount > 0 || entityCount > 0
            ? System.Text.Json.JsonSerializer.Serialize(new { segmentCount, entityCount })
            : null;

        await EmitAsync(ProcessingSignalTypes.ProcessingCompleted, documentId, collectionId,
            durationMs: durationMs, metadata: metadata, ct: ct);
    }

    public async Task EmitProcessingFailedAsync(
        Guid documentId, Guid? collectionId, string errorMessage, CancellationToken ct = default)
    {
        var durationMs = (long)(DateTimeOffset.UtcNow - _operationStart).TotalMilliseconds;
        await EmitAsync(ProcessingSignalTypes.ProcessingFailed, documentId, collectionId,
            message: errorMessage, durationMs: durationMs, ct: ct);
    }

    public async Task EmitProcessingEscalatedAsync(
        Guid documentId, Guid? collectionId, string reason, int attemptNumber,
        CancellationToken ct = default)
    {
        var metadata = System.Text.Json.JsonSerializer.Serialize(new { reason, attemptNumber });
        await EmitAsync(ProcessingSignalTypes.ProcessingEscalated, documentId, collectionId,
            message: reason, metadata: metadata, ct: ct);
    }

    public async Task EmitStagingCleanedAsync(
        Guid? documentId, Guid? collectionId, string stagingPath, CancellationToken ct = default)
    {
        await EmitAsync(ProcessingSignalTypes.StagingCleaned, documentId, collectionId,
            stagingPath: stagingPath, ct: ct);
    }

    public async Task EmitOrphanDetectedAsync(
        Guid? documentId, Guid? collectionId, string message, CancellationToken ct = default)
    {
        await EmitAsync(ProcessingSignalTypes.OrphanDetected, documentId, collectionId,
            message: message, ct: ct);
    }

    public async Task EmitRetriedAsync(
        Guid documentId, Guid? collectionId, int attemptNumber, string reason,
        CancellationToken ct = default)
    {
        var metadata = System.Text.Json.JsonSerializer.Serialize(new { attemptNumber, reason });
        await EmitAsync(ProcessingSignalTypes.ProcessingRetried, documentId, collectionId,
            message: reason, metadata: metadata, ct: ct);
    }

    private async Task EmitAsync(
        string signalType,
        Guid? documentId,
        Guid? collectionId,
        string? stagingPath = null,
        string? message = null,
        string? metadata = null,
        long? durationMs = null,
        CancellationToken ct = default)
    {
        var signal = new ProcessingSignalEntity
        {
            Id = Guid.NewGuid(),
            SignalType = signalType,
            CorrelationId = CorrelationId,
            DocumentId = documentId,
            CollectionId = collectionId,
            StagingPath = stagingPath,
            Message = message,
            Metadata = metadata,
            DurationMs = durationMs
        };

        _db.ProcessingSignals.Add(signal);

        try
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogDebug("Signal emitted: {SignalType} for doc {DocumentId} (correlation {CorrelationId})",
                signalType, documentId, CorrelationId);
        }
        catch (Exception ex)
        {
            // Signal emission should never fail the main operation
            _logger.LogWarning(ex, "Failed to emit signal {SignalType} for doc {DocumentId}",
                signalType, documentId);
        }
    }
}
