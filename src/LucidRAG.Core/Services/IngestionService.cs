using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Models;

namespace LucidRAG.Services;

/// <summary>
/// Service for managing content ingestion from external sources.
/// Connects to the signal-based pipeline architecture.
/// </summary>
public class IngestionService : IIngestionService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IngestionService> _logger;

    // Active jobs in memory (for progress tracking and cancellation)
    private readonly ConcurrentDictionary<Guid, ActiveIngestionJob> _activeJobs = new();

    // Signal channels for progress streaming
    private readonly ConcurrentDictionary<Guid, Channel<IngestionProgress>> _progressChannels = new();

    // Signal history per job (limited)
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<IngestionSignal>> _jobSignals = new();
    private const int MaxSignalsPerJob = 1000;

    public event EventHandler<IngestionSignal>? SignalEmitted;

    public IngestionService(
        IServiceScopeFactory scopeFactory,
        ILogger<IngestionService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<IngestionSourceInfo> CreateSourceAsync(CreateIngestionSourceRequest request, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

        var entity = new IngestionSourceEntity
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            SourceType = request.SourceType.ToLowerInvariant(),
            Location = request.Location,
            FilePattern = request.FilePattern,
            Recursive = request.Recursive,
            CollectionId = request.CollectionId,
            Options = request.Options != null ? JsonSerializer.Serialize(request.Options) : null
        };

        db.IngestionSources.Add(entity);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Created ingestion source {SourceId} ({Name}) of type {SourceType}",
            entity.Id, entity.Name, entity.SourceType);

        return ToSourceInfo(entity);
    }

    public async Task<IReadOnlyList<IngestionSourceInfo>> GetSourcesAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

        var entities = await db.IngestionSources
            .Where(s => s.IsEnabled)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(ToSourceInfo).ToList();
    }

    public async Task<IngestionSourceInfo?> GetSourceAsync(Guid sourceId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

        var entity = await db.IngestionSources.FindAsync([sourceId], ct);
        return entity != null ? ToSourceInfo(entity) : null;
    }

    public async Task<bool> DeleteSourceAsync(Guid sourceId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

        var entity = await db.IngestionSources.FindAsync([sourceId], ct);
        if (entity == null)
            return false;

        db.IngestionSources.Remove(entity);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Deleted ingestion source {SourceId}", sourceId);
        return true;
    }

    public async Task<Guid> StartIngestionAsync(StartIngestionRequest request, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

        var source = await db.IngestionSources.FindAsync([request.SourceId], ct);
        if (source == null)
            throw new InvalidOperationException($"Source {request.SourceId} not found");

        // Create job record
        var jobEntity = new IngestionJobEntity
        {
            Id = Guid.NewGuid(),
            SourceId = source.Id,
            Status = IngestionJobStatus.Queued.ToString(),
            IncrementalSync = request.IncrementalSync,
            MaxItems = request.MaxItems,
            Priority = request.Priority
        };

        db.IngestionJobs.Add(jobEntity);
        await db.SaveChangesAsync(ct);

        // Create active job tracker
        var activeJob = new ActiveIngestionJob
        {
            JobId = jobEntity.Id,
            SourceId = source.Id,
            SourceName = source.Name,
            SourceType = source.SourceType,
            Status = IngestionJobStatus.Queued,
            CancellationSource = new CancellationTokenSource()
        };

        _activeJobs[jobEntity.Id] = activeJob;
        _progressChannels[jobEntity.Id] = Channel.CreateUnbounded<IngestionProgress>();
        _jobSignals[jobEntity.Id] = new ConcurrentQueue<IngestionSignal>();

        // Start job in background
        _ = Task.Run(() => ExecuteJobAsync(activeJob, source, request), CancellationToken.None);

        _logger.LogInformation("Started ingestion job {JobId} for source {SourceId} ({SourceName})",
            jobEntity.Id, source.Id, source.Name);

        return jobEntity.Id;
    }

    public IngestionJobInfo? GetJob(Guid jobId)
    {
        if (_activeJobs.TryGetValue(jobId, out var active))
        {
            return ToJobInfo(active);
        }

        // Check database for completed jobs
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

        var entity = db.IngestionJobs
            .Include(j => j.Source)
            .FirstOrDefault(j => j.Id == jobId);

        return entity != null ? ToJobInfo(entity) : null;
    }

    public IEnumerable<IngestionJobInfo> GetJobs(Guid? sourceId = null)
    {
        var active = _activeJobs.Values
            .Where(j => sourceId == null || j.SourceId == sourceId)
            .Select(ToJobInfo);

        // Include recent completed jobs from database
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

        var recent = db.IngestionJobs
            .Include(j => j.Source)
            .Where(j => sourceId == null || j.SourceId == sourceId)
            .Where(j => !_activeJobs.ContainsKey(j.Id))
            .OrderByDescending(j => j.CreatedAt)
            .Take(50)
            .ToList()
            .Select(ToJobInfo);

        return active.Concat(recent);
    }

    public async Task<bool> CancelJobAsync(Guid jobId, CancellationToken ct = default)
    {
        if (!_activeJobs.TryGetValue(jobId, out var active))
            return false;

        active.CancellationSource?.Cancel();
        active.Status = IngestionJobStatus.Cancelling;

        _logger.LogInformation("Cancelling ingestion job {JobId}", jobId);
        return true;
    }

    public async IAsyncEnumerable<IngestionProgress> StreamProgressAsync(
        Guid jobId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_progressChannels.TryGetValue(jobId, out var channel))
        {
            // Return final status if job completed
            if (_activeJobs.TryGetValue(jobId, out var job))
            {
                yield return CreateProgress(job);
            }
            yield break;
        }

        await foreach (var progress in channel.Reader.ReadAllAsync(ct))
        {
            yield return progress;
        }
    }

    public IReadOnlyList<IngestionSignal> GetJobSignals(Guid jobId)
    {
        if (_jobSignals.TryGetValue(jobId, out var signals))
        {
            return signals.ToArray();
        }
        return Array.Empty<IngestionSignal>();
    }

    private async Task ExecuteJobAsync(
        ActiveIngestionJob job,
        IngestionSourceEntity source,
        StartIngestionRequest request)
    {
        var ct = job.CancellationSource?.Token ?? CancellationToken.None;

        try
        {
            job.Status = IngestionJobStatus.Discovering;
            job.StartedAt = DateTimeOffset.UtcNow;
            await UpdateJobInDatabaseAsync(job);
            await BroadcastProgressAsync(job);

            // Emit job started signal
            EmitSignal(job.JobId, Entities.SignalTypes.JobStarted, new
            {
                jobId = job.JobId,
                sourceId = job.SourceId,
                sourceName = job.SourceName,
                sourceType = job.SourceType
            });

            // Discover items based on source type
            var items = await DiscoverItemsAsync(source, request, ct);
            job.ItemsDiscovered = items.Count;
            await BroadcastProgressAsync(job);

            _logger.LogInformation("Job {JobId}: Discovered {Count} items", job.JobId, items.Count);

            // Process items
            job.Status = IngestionJobStatus.Processing;
            await UpdateJobInDatabaseAsync(job);

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();

                job.CurrentItem = item.Path;
                await BroadcastProgressAsync(job);

                try
                {
                    await ProcessItemAsync(job, source, item, ct);
                    job.ItemsProcessed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process item {Path}", item.Path);
                    job.ItemsFailed++;
                }
            }

            // Complete job
            job.Status = job.ItemsFailed > 0
                ? IngestionJobStatus.CompletedWithErrors
                : IngestionJobStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.CurrentItem = null;

            // Update source last sync time
            await UpdateSourceLastSyncAsync(source.Id, job.ItemsProcessed);

            // Emit job completed signal
            EmitSignal(job.JobId, Entities.SignalTypes.JobCompleted, new
            {
                jobId = job.JobId,
                sourceId = job.SourceId,
                status = job.Status.ToString(),
                itemsDiscovered = job.ItemsDiscovered,
                itemsProcessed = job.ItemsProcessed,
                itemsFailed = job.ItemsFailed,
                itemsSkipped = job.ItemsSkipped
            });

            _logger.LogInformation("Job {JobId} completed: {Status}, {Processed}/{Discovered} items",
                job.JobId, job.Status, job.ItemsProcessed, job.ItemsDiscovered);
        }
        catch (OperationCanceledException)
        {
            job.Status = IngestionJobStatus.Cancelled;
            job.CompletedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("Job {JobId} was cancelled", job.JobId);
        }
        catch (Exception ex)
        {
            job.Status = IngestionJobStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.CompletedAt = DateTimeOffset.UtcNow;
            _logger.LogError(ex, "Job {JobId} failed", job.JobId);
        }
        finally
        {
            await UpdateJobInDatabaseAsync(job);
            await BroadcastProgressAsync(job);

            // Close progress channel
            if (_progressChannels.TryRemove(job.JobId, out var channel))
            {
                channel.Writer.Complete();
            }
        }
    }

    private async Task<List<DiscoveredItem>> DiscoverItemsAsync(
        IngestionSourceEntity source,
        StartIngestionRequest request,
        CancellationToken ct)
    {
        var items = new List<DiscoveredItem>();

        switch (source.SourceType)
        {
            case "directory":
                items = await DiscoverDirectoryAsync(source, request, ct);
                break;
            case "github":
                items = await DiscoverGitHubAsync(source, request, ct);
                break;
            default:
                _logger.LogWarning("Unknown source type: {SourceType}", source.SourceType);
                break;
        }

        // Apply max items limit
        if (request.MaxItems > 0 && items.Count > request.MaxItems)
        {
            items = items.Take(request.MaxItems).ToList();
        }

        return items;
    }

    private async Task<List<DiscoveredItem>> DiscoverDirectoryAsync(
        IngestionSourceEntity source,
        StartIngestionRequest request,
        CancellationToken ct)
    {
        var items = new List<DiscoveredItem>();

        if (!Directory.Exists(source.Location))
        {
            _logger.LogWarning("Directory not found: {Location}", source.Location);
            return items;
        }

        var searchOption = source.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var pattern = source.FilePattern ?? "*.*";

        foreach (var file in Directory.EnumerateFiles(source.Location, pattern, searchOption))
        {
            ct.ThrowIfCancellationRequested();

            var fileInfo = new FileInfo(file);
            items.Add(new DiscoveredItem
            {
                Path = file,
                Name = fileInfo.Name,
                SizeBytes = fileInfo.Length,
                ModifiedAt = fileInfo.LastWriteTimeUtc,
                ContentHash = null // Computed during processing if needed
            });
        }

        return items;
    }

    private async Task<List<DiscoveredItem>> DiscoverGitHubAsync(
        IngestionSourceEntity source,
        StartIngestionRequest request,
        CancellationToken ct)
    {
        // GitHub discovery would use Octokit - simplified stub for now
        _logger.LogWarning("GitHub discovery not fully implemented yet");
        return [];
    }

    private async Task ProcessItemAsync(
        ActiveIngestionJob job,
        IngestionSourceEntity source,
        DiscoveredItem item,
        CancellationToken ct)
    {
        // Generate non-reversible hash for item identification (unique per tenant)
        var itemHash = GenerateSecureHash(source.Id, item.Path);

        // Determine MIME type and content type for routing
        var mimeType = GetMimeType(item.Path);
        var contentType = Entities.ContentTypes.FromExtension(Path.GetExtension(item.Path));

        // Emit content stored signal for pipeline routing
        // This drives the signal-based architecture:
        // - document → DocSummarizer molecule
        // - image → ImageSummarizer molecule
        // - data → DataSummarizer molecule
        // - audio/video → Transcription molecules
        EmitSignal(job.JobId, Entities.SignalTypes.ContentStored, new
        {
            jobId = job.JobId,
            itemHash = itemHash,
            sourcePath = item.Path,
            fileName = item.Name,
            mimeType = mimeType,
            contentType = contentType,  // Key for molecule routing
            sizeBytes = item.SizeBytes,
            sourceType = source.SourceType,
            collectionId = source.CollectionId,
            modifiedAt = item.ModifiedAt
        });

        // The actual processing (summarization, embedding, evidence storage)
        // is handled by pipeline molecules listening to the signal.
        // Each molecule activates only when its subscribed content type matches.
    }

    private void EmitSignal(Guid jobId, string signalType, object payload)
    {
        var signal = new IngestionSignal(signalType, jobId, payload, DateTimeOffset.UtcNow);

        // Store in job history
        if (_jobSignals.TryGetValue(jobId, out var signals))
        {
            signals.Enqueue(signal);
            while (signals.Count > MaxSignalsPerJob && signals.TryDequeue(out _)) { }
        }

        // Raise event for pipeline subscribers
        SignalEmitted?.Invoke(this, signal);
    }

    private async Task BroadcastProgressAsync(ActiveIngestionJob job)
    {
        if (_progressChannels.TryGetValue(job.JobId, out var channel))
        {
            await channel.Writer.WriteAsync(CreateProgress(job));
        }
    }

    private static IngestionProgress CreateProgress(ActiveIngestionJob job)
    {
        return new IngestionProgress(
            job.JobId,
            job.SourceId,
            job.ItemsDiscovered,
            job.ItemsProcessed,
            job.ItemsFailed,
            job.ItemsSkipped,
            job.CurrentItem,
            job.Status,
            job.ItemsDiscovered > 0
                ? (double)(job.ItemsProcessed + job.ItemsFailed + job.ItemsSkipped) / job.ItemsDiscovered * 100
                : 0,
            job.ErrorMessage
        );
    }

    private async Task UpdateJobInDatabaseAsync(ActiveIngestionJob job)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

        var entity = await db.IngestionJobs.FindAsync(job.JobId);
        if (entity != null)
        {
            entity.Status = job.Status.ToString();
            entity.ItemsDiscovered = job.ItemsDiscovered;
            entity.ItemsProcessed = job.ItemsProcessed;
            entity.ItemsFailed = job.ItemsFailed;
            entity.ItemsSkipped = job.ItemsSkipped;
            entity.ErrorMessage = job.ErrorMessage;
            entity.StartedAt = job.StartedAt;
            entity.CompletedAt = job.CompletedAt;
            await db.SaveChangesAsync();
        }
    }

    private async Task UpdateSourceLastSyncAsync(Guid sourceId, int itemsIngested)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

        var source = await db.IngestionSources.FindAsync(sourceId);
        if (source != null)
        {
            source.LastSyncAt = DateTimeOffset.UtcNow;
            source.TotalItemsIngested += itemsIngested;
            await db.SaveChangesAsync();
        }
    }

    private static IngestionSourceInfo ToSourceInfo(IngestionSourceEntity entity)
    {
        return new IngestionSourceInfo(
            entity.Id,
            entity.Name,
            entity.SourceType,
            entity.Location,
            entity.FilePattern,
            entity.Recursive,
            entity.CollectionId,
            entity.CreatedAt,
            entity.LastSyncAt,
            entity.TotalItemsIngested
        );
    }

    private static IngestionJobInfo ToJobInfo(ActiveIngestionJob job)
    {
        return new IngestionJobInfo(
            job.JobId,
            job.SourceId,
            job.SourceName,
            job.SourceType,
            job.Status,
            job.ItemsDiscovered,
            job.ItemsProcessed,
            job.ItemsFailed,
            job.ItemsSkipped,
            job.CurrentItem,
            job.ErrorMessage,
            job.ItemsDiscovered > 0
                ? (double)(job.ItemsProcessed + job.ItemsFailed + job.ItemsSkipped) / job.ItemsDiscovered * 100
                : 0,
            job.StartedAt,
            job.CompletedAt
        );
    }

    private static IngestionJobInfo ToJobInfo(IngestionJobEntity entity)
    {
        return new IngestionJobInfo(
            entity.Id,
            entity.SourceId,
            entity.Source?.Name ?? "Unknown",
            entity.Source?.SourceType ?? "unknown",
            Enum.TryParse<IngestionJobStatus>(entity.Status, out var status) ? status : IngestionJobStatus.Failed,
            entity.ItemsDiscovered,
            entity.ItemsProcessed,
            entity.ItemsFailed,
            entity.ItemsSkipped,
            null,
            entity.ErrorMessage,
            entity.ItemsDiscovered > 0
                ? (double)(entity.ItemsProcessed + entity.ItemsFailed + entity.ItemsSkipped) / entity.ItemsDiscovered * 100
                : 0,
            entity.StartedAt,
            entity.CompletedAt
        );
    }

    /// <summary>
    /// Generate a non-reversible hash for item identification.
    /// Combines source ID and path to create unique but non-leaking identifier.
    /// </summary>
    private static string GenerateSecureHash(Guid sourceId, string path)
    {
        var input = $"{sourceId}:{path}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToBase64String(bytes)[..16].Replace("+", "-").Replace("/", "_");
    }

    private static string GetMimeType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".doc" => "application/msword",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".xls" => "application/vnd.ms-excel",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".html" or ".htm" => "text/html",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            _ => "application/octet-stream"
        };
    }

    public void Dispose()
    {
        foreach (var job in _activeJobs.Values)
        {
            job.CancellationSource?.Cancel();
            job.CancellationSource?.Dispose();
        }

        foreach (var channel in _progressChannels.Values)
        {
            channel.Writer.Complete();
        }
    }

    private class ActiveIngestionJob
    {
        public Guid JobId { get; init; }
        public Guid SourceId { get; init; }
        public required string SourceName { get; init; }
        public required string SourceType { get; init; }
        public IngestionJobStatus Status { get; set; }
        public int ItemsDiscovered { get; set; }
        public int ItemsProcessed { get; set; }
        public int ItemsFailed { get; set; }
        public int ItemsSkipped { get; set; }
        public string? CurrentItem { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public CancellationTokenSource? CancellationSource { get; init; }
    }

    private record DiscoveredItem
    {
        public required string Path { get; init; }
        public required string Name { get; init; }
        public long? SizeBytes { get; init; }
        public DateTimeOffset? ModifiedAt { get; init; }
        public string? ContentHash { get; init; }
    }
}
