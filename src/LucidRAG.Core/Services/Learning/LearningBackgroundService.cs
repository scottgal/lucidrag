using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LucidRAG.Core.Services.Learning;

/// <summary>
/// Background service that scans for documents needing learning and submits them to the coordinator.
/// Runs in hosted mode only (not CLI).
///
/// Pattern: Like BotDetection's learning background service - periodic scans,
/// submits to keyed coordinator for sequential processing per document.
/// </summary>
public class LearningBackgroundService : BackgroundService
{
    private readonly ILogger<LearningBackgroundService> _logger;
    private readonly ILearningCoordinator _coordinator;
    private readonly ILearningScanner _scanner;
    private readonly LearningConfig _config;

    public LearningBackgroundService(
        ILogger<LearningBackgroundService> logger,
        ILearningCoordinator coordinator,
        ILearningScanner scanner,
        LearningConfig config)
    {
        _logger = logger;
        _coordinator = coordinator;
        _scanner = scanner;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("Learning background service disabled");
            return;
        }

        _logger.LogInformation("Learning background service starting (scan interval: {Interval})", _config.ScanInterval);

        // Wait a bit before starting (let system initialize)
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunLearningScanAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in learning scan");
            }

            // Wait before next scan
            _logger.LogDebug("Waiting {Interval} before next learning scan", _config.ScanInterval);
            await Task.Delay(_config.ScanInterval, stoppingToken);
        }

        _logger.LogInformation("Learning background service stopping");

        // Graceful shutdown - wait for in-flight learning to complete
        await _coordinator.ShutdownAsync(stoppingToken);
    }

    /// <summary>
    /// Scan for documents that need learning and submit them to coordinator.
    /// </summary>
    private async Task RunLearningScanAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting learning scan");

        try
        {
            // Find documents that need learning
            var candidates = await _scanner.ScanForLearningCandidatesAsync(_config, ct);

            if (!candidates.Any())
            {
                _logger.LogDebug("No documents found needing learning");
                return;
            }

            _logger.LogInformation("Found {Count} documents needing learning", candidates.Count);

            // Submit each to the coordinator
            var submitted = 0;
            foreach (var candidate in candidates)
            {
                if (_coordinator.TrySubmitLearning(
                    candidate.TenantId,
                    candidate.DocumentId,
                    candidate.Reason,
                    candidate.Priority))
                {
                    submitted++;
                }
                else
                {
                    _logger.LogWarning(
                        "Failed to submit document {DocumentId} (tenant: {TenantId}) for learning (queue full?)",
                        candidate.DocumentId, candidate.TenantId);
                }
            }

            _logger.LogInformation(
                "Learning scan complete: submitted {Submitted}/{Total} documents",
                submitted, candidates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during learning scan");
        }
    }
}

/// <summary>
/// Scanner that finds documents eligible for learning.
/// </summary>
public interface ILearningScanner
{
    /// <summary>
    /// Scan for documents that need learning based on various criteria.
    /// </summary>
    Task<List<LearningCandidate>> ScanForLearningCandidatesAsync(
        LearningConfig config,
        CancellationToken ct = default);
}

/// <summary>
/// Document candidate for learning.
/// </summary>
public class LearningCandidate
{
    public required string TenantId { get; init; }
    public required Guid DocumentId { get; init; }
    public required string Reason { get; init; }
    public required string DocumentType { get; init; }
    public double? CurrentConfidence { get; init; }
    public int? CurrentEntityCount { get; init; }
    public DateTime ProcessedAt { get; init; }
    public int Priority { get; init; } = 50;  // Default medium priority
}

/// <summary>
/// Default implementation of learning scanner.
/// Finds documents based on:
/// - Low confidence scores
/// - Low entity counts
/// - User feedback
/// - Periodic refresh
/// </summary>
public class LearningScanner : ILearningScanner
{
    private readonly ILogger<LearningScanner> _logger;
    private readonly IDocumentRepository _documentRepository;
    private readonly IEntityRepository _entityRepository;
    private readonly IEvidenceRepository _evidenceRepository;

    public LearningScanner(
        ILogger<LearningScanner> logger,
        IDocumentRepository documentRepository,
        IEntityRepository entityRepository,
        IEvidenceRepository evidenceRepository)
    {
        _logger = logger;
        _documentRepository = documentRepository;
        _entityRepository = entityRepository;
        _evidenceRepository = evidenceRepository;
    }

    public async Task<List<LearningCandidate>> ScanForLearningCandidatesAsync(
        LearningConfig config,
        CancellationToken ct = default)
    {
        var candidates = new List<LearningCandidate>();

        try
        {
            // CRITERION 1: Documents with low confidence
            var lowConfidenceDocs = await FindLowConfidenceDocumentsAsync(config, ct);
            candidates.AddRange(lowConfidenceDocs);

            _logger.LogDebug("Found {Count} documents with low confidence", lowConfidenceDocs.Count);

            // CRITERION 2: Documents with low entity counts
            var lowEntityDocs = await FindLowEntityCountDocumentsAsync(config, ct);
            candidates.AddRange(lowEntityDocs.Where(c =>
                !candidates.Any(existing => existing.DocumentId == c.DocumentId)));

            _logger.LogDebug("Found {Count} documents with low entity counts", lowEntityDocs.Count);

            // CRITERION 3: Documents with user feedback (negative)
            var feedbackDocs = await FindDocumentsWithNegativeFeedbackAsync(config, ct);
            candidates.AddRange(feedbackDocs.Where(c =>
                !candidates.Any(existing => existing.DocumentId == c.DocumentId)));

            _logger.LogDebug("Found {Count} documents with negative feedback", feedbackDocs.Count);

            // CRITERION 4: Periodic refresh (old documents that haven't been reprocessed)
            var staleDocs = await FindStaleDocumentsAsync(config, ct);
            candidates.AddRange(staleDocs.Where(c =>
                !candidates.Any(existing => existing.DocumentId == c.DocumentId)));

            _logger.LogDebug("Found {Count} stale documents needing refresh", staleDocs.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning for learning candidates");
        }

        return candidates;
    }

    private async Task<List<LearningCandidate>> FindLowConfidenceDocumentsAsync(
        LearningConfig config,
        CancellationToken ct)
    {
        var candidates = new List<LearningCandidate>();

        // Query documents with average confidence below threshold
        var lowConfDocs = await _documentRepository.FindByConfidenceAsync(
            maxConfidence: config.ConfidenceThreshold,
            minAge: config.MinDocumentAge,
            ct: ct);

        foreach (var doc in lowConfDocs)
        {
            var evidence = await _evidenceRepository.GetAsync(doc.Id, "entity_extraction", ct);
            var currentConfidence = evidence?.Metadata?
                .TryGetValue("average_confidence", out var conf) == true && conf is double c
                ? c
                : 0.0;

            candidates.Add(new LearningCandidate
            {
                TenantId = doc.TenantId,
                DocumentId = doc.Id,
                Reason = $"low_confidence ({currentConfidence:F2})",
                DocumentType = doc.ContentType ?? "document",
                CurrentConfidence = currentConfidence,
                ProcessedAt = doc.ProcessedAt ?? DateTime.UtcNow
            });
        }

        return candidates;
    }

    private async Task<List<LearningCandidate>> FindLowEntityCountDocumentsAsync(
        LearningConfig config,
        CancellationToken ct)
    {
        var candidates = new List<LearningCandidate>();

        // Query documents with very few entities (likely poor extraction)
        var lowEntityDocs = await _documentRepository.FindByEntityCountAsync(
            maxEntityCount: 3,
            minAge: config.MinDocumentAge,
            ct: ct);

        foreach (var doc in lowEntityDocs)
        {
            var entities = await _entityRepository.GetEntitiesAsync(doc.Id, ct);

            candidates.Add(new LearningCandidate
            {
                TenantId = doc.TenantId,
                DocumentId = doc.Id,
                Reason = $"low_entity_count ({entities.Count})",
                DocumentType = doc.ContentType ?? "document",
                CurrentEntityCount = entities.Count,
                ProcessedAt = doc.ProcessedAt ?? DateTime.UtcNow
            });
        }

        return candidates;
    }

    private async Task<List<LearningCandidate>> FindDocumentsWithNegativeFeedbackAsync(
        LearningConfig config,
        CancellationToken ct)
    {
        var candidates = new List<LearningCandidate>();

        // Query documents with user feedback indicating poor results
        var feedbackDocs = await _documentRepository.FindWithNegativeFeedbackAsync(
            minAge: config.MinDocumentAge,
            ct: ct);

        foreach (var doc in feedbackDocs)
        {
            candidates.Add(new LearningCandidate
            {
                TenantId = doc.TenantId,
                DocumentId = doc.Id,
                Reason = "user_feedback",
                DocumentType = doc.ContentType ?? "document",
                ProcessedAt = doc.ProcessedAt ?? DateTime.UtcNow,
                Priority = 10  // User feedback gets high priority
            });
        }

        return candidates;
    }

    private async Task<List<LearningCandidate>> FindStaleDocumentsAsync(
        LearningConfig config,
        CancellationToken ct)
    {
        var candidates = new List<LearningCandidate>();

        // Query documents that haven't been reprocessed in a long time (e.g., 30 days)
        var staleThreshold = DateTime.UtcNow.AddDays(-30);
        var staleDocs = await _documentRepository.FindProcessedBeforeAsync(
            cutoffDate: staleThreshold,
            ct: ct);

        foreach (var doc in staleDocs)
        {
            candidates.Add(new LearningCandidate
            {
                TenantId = doc.TenantId,
                DocumentId = doc.Id,
                Reason = "periodic_refresh",
                DocumentType = doc.ContentType ?? "document",
                ProcessedAt = doc.ProcessedAt ?? DateTime.UtcNow,
                Priority = 80  // Periodic refresh gets low priority
            });
        }

        return candidates;
    }
}
