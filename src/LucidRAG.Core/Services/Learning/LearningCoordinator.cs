using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LucidRAG.Core.Services.Learning;

/// <summary>
/// Learning coordinator for background document reprocessing.
/// Runs the FULL processing stack (no early exit) to find better results,
/// then updates entity signatures and evidence if improvements found.
///
/// Pattern: Like BotDetection's learning pipeline - keyed sequential processing,
/// one document at a time per key, parallel across documents.
/// Multi-tenant support with composite keying (tenantId:documentId).
/// </summary>
public interface ILearningCoordinator
{
    /// <summary>
    /// Submit a document for learning/reprocessing.
    /// Non-blocking - returns immediately.
    /// </summary>
    /// <param name="tenantId">Tenant ID (for multi-tenant isolation)</param>
    /// <param name="documentId">Document to reprocess</param>
    /// <param name="reason">Why this document needs learning (low confidence, user feedback, etc.)</param>
    /// <param name="priority">Priority (0 = highest, 100 = lowest, default: 50)</param>
    /// <returns>True if queued successfully</returns>
    bool TrySubmitLearning(string tenantId, Guid documentId, string reason, int priority = 50);

    /// <summary>
    /// Get learning statistics for a document.
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="documentId">Document ID</param>
    /// <param name="ct">Cancellation token</param>
    Task<LearningStats?> GetStatsAsync(string tenantId, Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// Get all documents currently in learning queue.
    /// </summary>
    IReadOnlyList<LearningQueueItem> GetQueuedDocuments();

    /// <summary>
    /// Shutdown gracefully, waiting for in-flight learning to complete.
    /// </summary>
    Task ShutdownAsync(CancellationToken ct = default);
}

/// <summary>
/// A learning task to reprocess a document.
/// </summary>
public class LearningTask : IComparable<LearningTask>
{
    /// <summary>Tenant ID for multi-tenant keying</summary>
    public required string TenantId { get; init; }

    /// <summary>Document to reprocess</summary>
    public required Guid DocumentId { get; init; }

    /// <summary>Reason for learning (low_confidence, user_feedback, periodic_refresh)</summary>
    public required string Reason { get; init; }

    /// <summary>Document type (image, audio, document, data)</summary>
    public required string DocumentType { get; init; }

    /// <summary>Priority (0 = highest, 100 = lowest, default: 50)</summary>
    public int Priority { get; init; } = 50;

    /// <summary>Current entity count (baseline for comparison)</summary>
    public int? CurrentEntityCount { get; init; }

    /// <summary>Current confidence score (baseline for comparison)</summary>
    public double? CurrentConfidence { get; init; }

    /// <summary>Processing options to use</summary>
    public Dictionary<string, object>? Options { get; init; }

    /// <summary>When task was created</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Compare by priority (lower number = higher priority), then by timestamp (older first)</summary>
    public int CompareTo(LearningTask? other)
    {
        if (other == null) return 1;

        // First compare by priority (lower number = higher priority)
        var priorityComparison = Priority.CompareTo(other.Priority);
        if (priorityComparison != 0)
            return priorityComparison;

        // Then by timestamp (older tasks first)
        return Timestamp.CompareTo(other.Timestamp);
    }
}

/// <summary>
/// Statistics for a document's learning history.
/// </summary>
public class LearningStats
{
    public Guid DocumentId { get; init; }
    public long TotalLearningRuns { get; set; }
    public long ImprovementsFound { get; set; }
    public long NoImprovementRuns { get; set; }
    public DateTimeOffset? LastLearningRun { get; set; }
    public DateTimeOffset? LastImprovementFound { get; set; }
    public TimeSpan AverageProcessingTime { get; set; }
    public string? LastImprovement { get; set; }  // Description of last improvement

    /// <summary>Hash of document when last processed (to detect changes)</summary>
    public string? LastProcessedHash { get; set; }

    /// <summary>Skip count (how many times skipped due to no changes)</summary>
    public long SkippedUnchanged { get; set; }
}

/// <summary>
/// Item in the learning queue.
/// </summary>
public class LearningQueueItem
{
    public required string TenantId { get; init; }
    public required Guid DocumentId { get; init; }
    public required string Reason { get; init; }
    public required string DocumentType { get; init; }
    public DateTimeOffset QueuedAt { get; init; }
}

/// <summary>
/// Result of learning/reprocessing a document.
/// </summary>
public class LearningResult
{
    public required Guid DocumentId { get; init; }
    public required bool Success { get; init; }
    public required TimeSpan ProcessingTime { get; init; }

    /// <summary>Whether improvements were found and applied</summary>
    public required bool ImprovementsApplied { get; init; }

    /// <summary>Description of improvements (e.g., "+5 entities", "better OCR confidence")</summary>
    public List<string> Improvements { get; init; } = new();

    /// <summary>Comparison metrics before/after</summary>
    public Dictionary<string, ComparisonMetric> Metrics { get; init; } = new();

    /// <summary>Error message if failed</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Before/after comparison metric.
/// </summary>
public class ComparisonMetric
{
    public required string Name { get; init; }
    public required object Before { get; init; }
    public required object After { get; init; }
    public required bool Improved { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Keyed learning coordinator implementation.
/// One queue per document, sequential processing per document, parallel across documents.
/// </summary>
public class LearningCoordinator : ILearningCoordinator, IAsyncDisposable
{
    private readonly ILogger<LearningCoordinator> _logger;
    private readonly IEnumerable<ILearningHandler> _handlers;
    private readonly IServiceProvider _serviceProvider;
    private readonly LearningConfig _config;
    private readonly int _maxQueueSize;

    // Keyed by "tenantId:documentId" for multi-tenant sequential processing
    private readonly ConcurrentDictionary<string, LearningQueue> _queues = new();
    private readonly ConcurrentDictionary<string, LearningStats> _stats = new();
    private readonly SemaphoreSlim _shutdownLock = new(1, 1);
    private bool _disposed;
    private bool _isShuttingDown;

    public LearningCoordinator(
        ILogger<LearningCoordinator> logger,
        IEnumerable<ILearningHandler> handlers,
        IServiceProvider serviceProvider,
        LearningConfig config,
        int maxQueueSize = 100)
    {
        _logger = logger;
        _handlers = handlers;
        _serviceProvider = serviceProvider;
        _config = config;
        _maxQueueSize = maxQueueSize;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await ShutdownAsync();

        foreach (var queue in _queues.Values)
            queue.Dispose();

        _queues.Clear();
        _shutdownLock.Dispose();
    }

    public bool TrySubmitLearning(string tenantId, Guid documentId, string reason, int priority = 50)
    {
        if (_isShuttingDown || _disposed)
        {
            _logger.LogWarning("Learning coordinator shutting down, cannot submit document {DocumentId}", documentId);
            return false;
        }

        // Create composite key: tenantId:documentId
        var key = $"{tenantId}:{documentId}";

        // Get or create queue for this tenant+document
        var queue = _queues.GetOrAdd(key, k =>
        {
            _logger.LogInformation("Creating learning queue for {Key} (tenant: {TenantId}, document: {DocumentId})",
                k, tenantId, documentId);

            var stats = new LearningStats { DocumentId = documentId };
            _stats[k] = stats;

            var newQueue = new LearningQueue(k, _maxQueueSize, _logger);

            // Start background processor for this tenant+document (low priority)
            _ = Task.Run(async () =>
            {
                // Set thread priority to background/low for learning tasks
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                Thread.CurrentThread.IsBackground = true;

                await ProcessQueueAsync(k, newQueue);
            }, _config.CancellationToken ?? CancellationToken.None)
                .ConfigureAwait(false);

            return newQueue;
        });

        // Create learning task
        var task = new LearningTask
        {
            TenantId = tenantId,
            DocumentId = documentId,
            Reason = reason,
            DocumentType = "unknown",  // Will be resolved during processing
            Priority = priority
        };

        // Try to enqueue (priority queue - lower number = higher priority)
        if (queue.TryEnqueue(task))
        {
            _logger.LogInformation(
                "Queued document {DocumentId} for learning (tenant: {TenantId}, priority: {Priority}, reason: {Reason})",
                documentId, tenantId, priority, reason);
            return true;
        }

        _logger.LogWarning("Learning queue for {Key} is full, dropping task", key);
        return false;
    }

    public Task<LearningStats?> GetStatsAsync(string tenantId, Guid documentId, CancellationToken ct = default)
    {
        var key = $"{tenantId}:{documentId}";
        return Task.FromResult(_stats.TryGetValue(key, out var stats) ? stats : null);
    }

    public IReadOnlyList<LearningQueueItem> GetQueuedDocuments()
    {
        var items = new List<LearningQueueItem>();
        foreach (var kvp in _queues)
        {
            var queue = kvp.Value;
            if (queue.Count > 0)
            {
                // Parse composite key "tenantId:documentId"
                var parts = kvp.Key.Split(':', 2);
                var tenantId = parts.Length > 0 ? parts[0] : "unknown";
                var documentId = parts.Length > 1 && Guid.TryParse(parts[1], out var docId)
                    ? docId
                    : Guid.Empty;

                items.Add(new LearningQueueItem
                {
                    TenantId = tenantId,
                    DocumentId = documentId,
                    Reason = "queued",
                    DocumentType = "unknown",
                    QueuedAt = DateTimeOffset.UtcNow
                });
            }
        }
        return items;
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        await _shutdownLock.WaitAsync(ct);
        try
        {
            if (_isShuttingDown)
                return;

            _logger.LogInformation("Learning coordinator shutting down...");
            _isShuttingDown = true;

            // Wait for queues to drain (with timeout)
            var drainTimeout = TimeSpan.FromSeconds(30);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            foreach (var kvp in _queues)
            {
                var remaining = drainTimeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    _logger.LogWarning("Shutdown timeout exceeded, some queues may have pending tasks");
                    break;
                }

                while (kvp.Value.Count > 0 && stopwatch.Elapsed < drainTimeout)
                    await Task.Delay(100, ct);
            }

            _logger.LogInformation("Learning coordinator shutdown complete");
        }
        finally
        {
            _shutdownLock.Release();
        }
    }

    /// <summary>
    /// Background processor for a single document's learning queue.
    /// Processes tasks sequentially to avoid conflicts.
    /// </summary>
    private async Task ProcessQueueAsync(string key, LearningQueue queue)
    {
        _logger.LogInformation("Learning processor started for key {Key}", key);

        try
        {
            while (!_isShuttingDown)
            {
                // Wait for next task (with timeout to check shutdown)
                var task = await queue.DequeueAsync(TimeSpan.FromSeconds(1));

                if (task == null)
                    continue; // Timeout, check shutdown flag

                await ProcessLearningTaskAsync(key, task);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in learning processor for key {Key}", key);
        }

        _logger.LogInformation("Learning processor stopped for key {Key}", key);
    }

    /// <summary>
    /// Process a single learning task - run full stack, compare results, update if better.
    /// Skips if document unchanged since last processing.
    /// </summary>
    private async Task ProcessLearningTaskAsync(string key, LearningTask task)
    {
        var stats = _stats[key];
        var startTime = DateTimeOffset.UtcNow;

        try
        {
            _logger.LogInformation(
                "Processing learning task for document {DocumentId} (tenant: {TenantId}, reason: {Reason})",
                task.DocumentId, task.TenantId, task.Reason);

            // Find handler for this document type
            var handler = _handlers.FirstOrDefault(h => h.CanHandle(task.DocumentType));
            if (handler == null)
            {
                _logger.LogWarning("No learning handler found for document type: {Type}", task.DocumentType);
                return;
            }

            // CRITICAL: Check if document has changed since last processing
            // Only run full stack if document is new or has been modified
            var currentHash = await handler.GetDocumentHashAsync(task, CancellationToken.None);
            if (!string.IsNullOrEmpty(stats.LastProcessedHash) && stats.LastProcessedHash == currentHash)
            {
                stats.SkippedUnchanged++;
                _logger.LogDebug(
                    "Skipping learning for document {DocumentId} - unchanged since last run (hash: {Hash})",
                    task.DocumentId, currentHash);
                return;
            }

            // Run the learning/reprocessing
            var result = await handler.LearnAsync(task, CancellationToken.None);

            // Update stats
            stats.TotalLearningRuns++;
            stats.LastLearningRun = DateTimeOffset.UtcNow;
            stats.AverageProcessingTime = TimeSpan.FromMilliseconds(
                (stats.AverageProcessingTime.TotalMilliseconds * (stats.TotalLearningRuns - 1) +
                 result.ProcessingTime.TotalMilliseconds) / stats.TotalLearningRuns);

            if (result.ImprovementsApplied)
            {
                stats.ImprovementsFound++;
                stats.LastImprovementFound = DateTimeOffset.UtcNow;
                stats.LastImprovement = string.Join(", ", result.Improvements);

                _logger.LogInformation(
                    "Learning found improvements for document {DocumentId}: {Improvements}",
                    task.DocumentId, stats.LastImprovement);
            }
            else
            {
                stats.NoImprovementRuns++;
                _logger.LogDebug("Learning run for document {DocumentId} found no improvements", task.DocumentId);
            }

            // Update hash to track document version processed
            stats.LastProcessedHash = currentHash;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing learning task for document {DocumentId}", task.DocumentId);
        }
    }
}

/// <summary>
/// Thread-safe priority queue for a single document's learning tasks.
/// Tasks are ordered by priority (0 = highest), then by timestamp (older first).
/// </summary>
internal class LearningQueue : IDisposable
{
    private readonly string _key;
    private readonly int _maxSize;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly PriorityQueue<LearningTask, LearningTask> _queue = new();
    private readonly SemaphoreSlim _semaphore;
    private int _currentCount;
    private bool _disposed;

    public LearningQueue(string key, int maxSize, ILogger logger)
    {
        _key = key;
        _maxSize = maxSize;
        _logger = logger;
        _semaphore = new SemaphoreSlim(0);
    }

    public int Count => _currentCount;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _semaphore.Dispose();
    }

    public bool TryEnqueue(LearningTask task)
    {
        if (_disposed)
            return false;

        lock (_lock)
        {
            if (_currentCount >= _maxSize)
                return false;

            // Enqueue with priority (task is IComparable, lower priority number = higher priority)
            _queue.Enqueue(task, task);
            _currentCount++;
        }

        _semaphore.Release();
        return true;
    }

    public async Task<LearningTask?> DequeueAsync(TimeSpan timeout)
    {
        if (_disposed)
            return null;

        if (!await _semaphore.WaitAsync(timeout))
            return null; // Timeout

        lock (_lock)
        {
            if (_queue.TryDequeue(out var task, out _))
            {
                _currentCount--;
                return task;
            }
        }

        return null;
    }
}

/// <summary>
/// Handler for learning operations on specific document types.
/// </summary>
public interface ILearningHandler
{
    /// <summary>
    /// Check if this handler can process the given document type.
    /// </summary>
    bool CanHandle(string documentType);

    /// <summary>
    /// Get the current document hash to detect changes.
    /// Returns SHA-256 hash of document content or metadata signature.
    /// </summary>
    Task<string> GetDocumentHashAsync(LearningTask task, CancellationToken ct = default);

    /// <summary>
    /// Run learning/reprocessing for the document.
    /// Runs FULL stack (no early exit), compares results, updates if better.
    /// </summary>
    Task<LearningResult> LearnAsync(LearningTask task, CancellationToken ct = default);
}

/// <summary>
/// Configuration for learning coordinator.
/// </summary>
public class LearningConfig
{
    /// <summary>Enable learning (default: false - opt-in for hosted mode)</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Interval between learning scans (default: 30 minutes)</summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Minimum age before document eligible for learning (default: 1 hour)</summary>
    public TimeSpan MinDocumentAge { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Confidence threshold for triggering learning (default: 0.75)</summary>
    public double ConfidenceThreshold { get; set; } = 0.75;

    /// <summary>Only run in hosted mode (not CLI)</summary>
    public bool HostedModeOnly { get; set; } = true;

    /// <summary>Optional cancellation token for graceful shutdown</summary>
    public CancellationToken? CancellationToken { get; set; }
}
