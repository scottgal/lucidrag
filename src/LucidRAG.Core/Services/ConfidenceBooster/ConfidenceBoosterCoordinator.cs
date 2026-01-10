using Microsoft.Extensions.Logging;

namespace LucidRAG.Core.Services.ConfidenceBooster;

/// <summary>
/// Coordinator for background confidence boosting across all document types.
/// Runs as part of the background learning pipeline, not during real-time ingestion.
/// </summary>
public class ConfidenceBoosterCoordinator
{
    private readonly ILogger<ConfidenceBoosterCoordinator> _logger;
    private readonly IDocumentQueueService _queueService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConfidenceBoosterConfig _config;

    public ConfidenceBoosterCoordinator(
        ILogger<ConfidenceBoosterCoordinator> logger,
        IDocumentQueueService queueService,
        IServiceProvider serviceProvider,
        ConfidenceBoosterConfig config)
    {
        _logger = logger;
        _queueService = queueService;
        _serviceProvider = serviceProvider;
        _config = config;
    }

    /// <summary>
    /// Scan for documents with low-confidence signals and queue for boosting.
    /// Called by background coordinator job.
    /// </summary>
    public async Task ScanAndQueueAsync(CancellationToken ct = default)
    {
        if (!_config.Enabled)
        {
            _logger.LogDebug("ConfidenceBooster is disabled, skipping scan");
            return;
        }

        _logger.LogInformation("Starting confidence booster scan");

        try
        {
            // Get documents that need boosting
            var candidates = await _queueService.GetDocumentsNeedingBoostAsync(
                _config.ConfidenceThreshold,
                ct);

            if (!candidates.Any())
            {
                _logger.LogDebug("No documents found needing confidence boost");
                return;
            }

            _logger.LogInformation("Found {Count} documents needing confidence boost", candidates.Count);

            // Queue each for background processing
            foreach (var doc in candidates)
            {
                await _queueService.QueueForBoostAsync(doc.Id, doc.Type, ct);
            }

            _logger.LogInformation("Queued {Count} documents for confidence boosting", candidates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan and queue documents for boosting");
        }
    }

    /// <summary>
    /// Process a single document through confidence boosting.
    /// Called by background worker when document is dequeued.
    /// </summary>
    public async Task<BoostSummary> ProcessDocumentAsync(
        Guid documentId,
        string documentType,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Processing document {DocumentId} for confidence boosting (type: {Type})",
            documentId, documentType);

        var summary = new BoostSummary
        {
            DocumentId = documentId,
            DocumentType = documentType,
            StartTime = DateTime.UtcNow
        };

        try
        {
            // Route to appropriate domain-specific booster
            var booster = GetBoosterForType(documentType);
            if (booster == null)
            {
                _logger.LogWarning("No booster available for document type: {Type}", documentType);
                summary.Status = BoostStatus.Skipped;
                summary.Message = $"No booster for type: {documentType}";
                return summary;
            }

            // PHASE 1: Extract artifacts
            var artifacts = await booster.ExtractArtifactsAsync(
                documentId,
                _config.ConfidenceThreshold,
                _config.MaxArtifactsPerDocument,
                ct);

            summary.ArtifactsExtracted = artifacts.Count;

            if (!artifacts.Any())
            {
                _logger.LogInformation("No artifacts extracted for document {DocumentId}", documentId);
                summary.Status = BoostStatus.NoArtifacts;
                summary.Message = "No low-confidence signals found";
                return summary;
            }

            // PHASE 2: Boost artifacts with LLM
            var results = await booster.BoostBatchAsync(artifacts, ct);
            summary.ArtifactsBoosted = results.Count(r => r.Success);
            summary.TotalTokensUsed = results.Sum(r => r.TokensUsed);

            if (!results.Any(r => r.Success))
            {
                _logger.LogWarning("No artifacts successfully boosted for document {DocumentId}", documentId);
                summary.Status = BoostStatus.Failed;
                summary.Message = "All boost attempts failed";
                return summary;
            }

            // PHASE 3: Update signal ledger
            await booster.UpdateSignalLedgerAsync(documentId, results, ct);

            summary.Status = BoostStatus.Success;
            summary.Message = $"Boosted {summary.ArtifactsBoosted}/{summary.ArtifactsExtracted} artifacts";

            _logger.LogInformation(
                "Confidence boost complete for document {DocumentId}: {Boosted}/{Extracted} artifacts, {Tokens} tokens",
                documentId,
                summary.ArtifactsBoosted,
                summary.ArtifactsExtracted,
                summary.TotalTokensUsed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to boost document {DocumentId}", documentId);
            summary.Status = BoostStatus.Error;
            summary.Message = ex.Message;
        }
        finally
        {
            summary.EndTime = DateTime.UtcNow;
        }

        return summary;
    }

    /// <summary>
    /// Get the appropriate booster service for a document type.
    /// </summary>
    private IConfidenceBoosterBase? GetBoosterForType(string documentType)
    {
        return documentType.ToLowerInvariant() switch
        {
            "image" => _serviceProvider.GetService(typeof(IConfidenceBooster<>)),
            "audio" => _serviceProvider.GetService(typeof(IConfidenceBooster<>)),
            "document" => _serviceProvider.GetService(typeof(IConfidenceBooster<>)),
            "data" => _serviceProvider.GetService(typeof(IConfidenceBooster<>)),
            _ => null
        } as IConfidenceBoosterBase;
    }
}

/// <summary>
/// Base interface for confidence boosters (non-generic for coordinator).
/// </summary>
public interface IConfidenceBoosterBase
{
    Task<List<IArtifact>> ExtractArtifactsAsync(
        Guid documentId,
        double confidenceThreshold = 0.75,
        int maxArtifacts = 5,
        CancellationToken ct = default);

    Task<List<BoostResult>> BoostBatchAsync(
        IEnumerable<IArtifact> artifacts,
        CancellationToken ct = default);

    Task UpdateSignalLedgerAsync(
        Guid documentId,
        IEnumerable<BoostResult> results,
        CancellationToken ct = default);
}

// IDocumentQueueService moved to ConfidenceBoosterBackgroundService.cs

/// <summary>
/// Document candidate for boosting.
/// </summary>
public class DocumentCandidate
{
    public required Guid Id { get; init; }
    public required string Type { get; init; }
    public double LowestConfidence { get; init; }
    public int LowConfidenceSignalCount { get; init; }
}

/// <summary>
/// Summary of boost processing for a document.
/// </summary>
public class BoostSummary
{
    public required Guid DocumentId { get; init; }
    public required string DocumentType { get; init; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public TimeSpan Duration => (EndTime ?? DateTime.UtcNow) - StartTime;

    public BoostStatus Status { get; set; } = BoostStatus.Pending;
    public int ArtifactsExtracted { get; set; }
    public int ArtifactsBoosted { get; set; }
    public int TotalTokensUsed { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Boost processing status.
/// </summary>
public enum BoostStatus
{
    Pending,
    Success,
    NoArtifacts,
    Failed,
    Skipped,
    Error
}
