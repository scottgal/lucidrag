using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace LucidRAG.Core.Services.ConfidenceBooster;

/// <summary>
/// Background service that runs confidence boosting in the coordinator's learning pipeline.
/// Scans for low-confidence signals and processes them with LLM refinement.
/// </summary>
public class ConfidenceBoosterBackgroundService : BackgroundService
{
    private readonly ILogger<ConfidenceBoosterBackgroundService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConfidenceBoosterConfig _config;

    public ConfidenceBoosterBackgroundService(
        ILogger<ConfidenceBoosterBackgroundService> logger,
        IServiceProvider serviceProvider,
        ConfidenceBoosterConfig config)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ConfidenceBooster background service starting");

        // Wait a bit before starting (let system initialize)
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunBoostCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in confidence booster cycle");
            }

            // Wait before next cycle (configurable, default 5 minutes)
            var delay = TimeSpan.FromMinutes(5);
            _logger.LogDebug("Waiting {Delay} before next boost cycle", delay);
            await Task.Delay(delay, stoppingToken);
        }

        _logger.LogInformation("ConfidenceBooster background service stopping");
    }

    /// <summary>
    /// Run a complete boost cycle: scan → queue → process.
    /// </summary>
    private async Task RunBoostCycleAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting confidence boost cycle");

        using var scope = _serviceProvider.CreateScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<ConfidenceBoosterCoordinator>();
        var queueService = scope.ServiceProvider.GetRequiredService<IDocumentQueueService>();

        try
        {
            // PHASE 1: Scan for documents needing boost
            await coordinator.ScanAndQueueAsync(ct);

            // PHASE 2: Process queued documents
            var processed = 0;
            var maxPerCycle = 10;  // Process max 10 documents per cycle (cost control)

            while (processed < maxPerCycle && !ct.IsCancellationRequested)
            {
                var item = await queueService.DequeueBoostItemAsync(ct);
                if (item == null)
                {
                    _logger.LogDebug("No more items in boost queue");
                    break;
                }

                _logger.LogInformation(
                    "Processing boost queue item {ItemId}: document {DocumentId} (type: {Type})",
                    item.Id,
                    item.DocumentId,
                    item.DocumentType);

                var summary = await coordinator.ProcessDocumentAsync(
                    item.DocumentId,
                    item.DocumentType,
                    ct);

                _logger.LogInformation(
                    "Boost complete for document {DocumentId}: {Status} - {Message} ({TokensUsed} tokens, {Duration}ms)",
                    item.DocumentId,
                    summary.Status,
                    summary.Message,
                    summary.TotalTokensUsed,
                    summary.Duration.TotalMilliseconds);

                processed++;

                // Mark item as processed
                await queueService.CompleteBoostItemAsync(item.Id, summary, ct);
            }

            _logger.LogInformation("Boost cycle complete: processed {Count} documents", processed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in boost cycle");
        }
    }
}

/// <summary>
/// Queue item for confidence boosting.
/// </summary>
public class BoostQueueItem
{
    public required Guid Id { get; init; }
    public required Guid DocumentId { get; init; }
    public required string DocumentType { get; init; }
    public DateTime QueuedAt { get; init; }
}

/// <summary>
/// Extended document queue service interface.
/// </summary>
public interface IDocumentQueueService
{
    Task<List<DocumentCandidate>> GetDocumentsNeedingBoostAsync(
        double confidenceThreshold,
        CancellationToken ct = default);

    Task QueueForBoostAsync(Guid documentId, string documentType, CancellationToken ct = default);

    Task<BoostQueueItem?> DequeueBoostItemAsync(CancellationToken ct = default);

    Task CompleteBoostItemAsync(Guid itemId, BoostSummary summary, CancellationToken ct = default);
}
