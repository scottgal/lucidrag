using Microsoft.AspNetCore.Mvc;
using LucidRAG.Filters;
using LucidRAG.Models;
using LucidRAG.Services;

namespace LucidRAG.Controllers.Api;

/// <summary>
/// API for managing content ingestion from external sources.
/// Supports directory, GitHub, FTP, and S3 sources.
/// </summary>
[ApiController]
[Route("api/ingestion")]
[DemoModeWriteBlock(Operation = "Content ingestion")]
public class IngestionController(
    IIngestionService ingestionService,
    ILogger<IngestionController> logger) : ControllerBase
{
    #region Sources

    /// <summary>
    /// Create a new ingestion source
    /// </summary>
    [HttpPost("sources")]
    public async Task<IActionResult> CreateSource(
        [FromBody] CreateIngestionSourceRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Name is required" });
        }

        if (string.IsNullOrWhiteSpace(request.SourceType))
        {
            return BadRequest(new { error = "SourceType is required" });
        }

        if (string.IsNullOrWhiteSpace(request.Location))
        {
            return BadRequest(new { error = "Location is required" });
        }

        var validTypes = new[] { "directory", "github", "ftp", "s3" };
        if (!validTypes.Contains(request.SourceType.ToLowerInvariant()))
        {
            return BadRequest(new { error = $"Invalid source type. Valid types: {string.Join(", ", validTypes)}" });
        }

        try
        {
            var source = await ingestionService.CreateSourceAsync(request, ct);

            return CreatedAtAction(nameof(GetSource), new { id = source.Id }, new
            {
                id = source.Id,
                name = source.Name,
                sourceType = source.SourceType,
                location = source.Location,
                filePattern = source.FilePattern,
                recursive = source.Recursive,
                collectionId = source.CollectionId,
                createdAt = source.CreatedAt
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create ingestion source");
            return StatusCode(500, new { error = "Failed to create source" });
        }
    }

    /// <summary>
    /// List all ingestion sources
    /// </summary>
    [HttpGet("sources")]
    public async Task<IActionResult> ListSources(CancellationToken ct = default)
    {
        var sources = await ingestionService.GetSourcesAsync(ct);

        return Ok(new
        {
            sources = sources.Select(s => new
            {
                id = s.Id,
                name = s.Name,
                sourceType = s.SourceType,
                location = s.Location,
                filePattern = s.FilePattern,
                recursive = s.Recursive,
                collectionId = s.CollectionId,
                createdAt = s.CreatedAt,
                lastSyncAt = s.LastSyncAt,
                totalItemsIngested = s.TotalItemsIngested
            })
        });
    }

    /// <summary>
    /// Get a specific ingestion source
    /// </summary>
    [HttpGet("sources/{id:guid}")]
    public async Task<IActionResult> GetSource(Guid id, CancellationToken ct = default)
    {
        var source = await ingestionService.GetSourceAsync(id, ct);
        if (source == null)
        {
            return NotFound(new { error = "Source not found" });
        }

        return Ok(new
        {
            id = source.Id,
            name = source.Name,
            sourceType = source.SourceType,
            location = source.Location,
            filePattern = source.FilePattern,
            recursive = source.Recursive,
            collectionId = source.CollectionId,
            createdAt = source.CreatedAt,
            lastSyncAt = source.LastSyncAt,
            totalItemsIngested = source.TotalItemsIngested
        });
    }

    /// <summary>
    /// Delete an ingestion source
    /// </summary>
    [HttpDelete("sources/{id:guid}")]
    public async Task<IActionResult> DeleteSource(Guid id, CancellationToken ct = default)
    {
        var deleted = await ingestionService.DeleteSourceAsync(id, ct);
        if (!deleted)
        {
            return NotFound(new { error = "Source not found" });
        }

        return Ok(new { success = true });
    }

    /// <summary>
    /// Trigger a sync for an ingestion source
    /// </summary>
    [HttpPost("sources/{id:guid}/sync")]
    public async Task<IActionResult> TriggerSync(
        Guid id,
        [FromBody] TriggerSyncRequest? request = null,
        CancellationToken ct = default)
    {
        var source = await ingestionService.GetSourceAsync(id, ct);
        if (source == null)
        {
            return NotFound(new { error = "Source not found" });
        }

        try
        {
            var jobId = await ingestionService.StartIngestionAsync(new StartIngestionRequest(
                id,
                request?.IncrementalSync ?? true,
                request?.MaxItems ?? 0,
                request?.ContinueOnError ?? true,
                request?.Priority ?? 100
            ), ct);

            return Ok(new IngestionStartResponse(jobId, id, "Ingestion job started"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start ingestion for source {SourceId}", id);
            return StatusCode(500, new { error = "Failed to start ingestion" });
        }
    }

    #endregion

    #region Jobs

    /// <summary>
    /// Start a new ingestion job
    /// </summary>
    [HttpPost("jobs")]
    public async Task<IActionResult> StartJob(
        [FromBody] StartIngestionRequest request,
        CancellationToken ct = default)
    {
        var source = await ingestionService.GetSourceAsync(request.SourceId, ct);
        if (source == null)
        {
            return NotFound(new { error = "Source not found" });
        }

        try
        {
            var jobId = await ingestionService.StartIngestionAsync(request, ct);
            return Ok(new IngestionStartResponse(jobId, request.SourceId, "Ingestion job started"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start ingestion job");
            return StatusCode(500, new { error = "Failed to start job" });
        }
    }

    /// <summary>
    /// List ingestion jobs
    /// </summary>
    [HttpGet("jobs")]
    public IActionResult ListJobs([FromQuery] Guid? sourceId = null)
    {
        var jobs = ingestionService.GetJobs(sourceId);

        return Ok(new
        {
            jobs = jobs.Select(j => new
            {
                jobId = j.JobId,
                sourceId = j.SourceId,
                sourceName = j.SourceName,
                sourceType = j.SourceType,
                status = j.Status.ToString().ToLowerInvariant(),
                itemsDiscovered = j.ItemsDiscovered,
                itemsProcessed = j.ItemsProcessed,
                itemsFailed = j.ItemsFailed,
                itemsSkipped = j.ItemsSkipped,
                progress = j.Progress,
                currentItem = j.CurrentItem,
                errorMessage = j.ErrorMessage,
                startedAt = j.StartedAt,
                completedAt = j.CompletedAt
            })
        });
    }

    /// <summary>
    /// Get ingestion job status
    /// </summary>
    [HttpGet("jobs/{id:guid}")]
    public IActionResult GetJob(Guid id)
    {
        var job = ingestionService.GetJob(id);
        if (job == null)
        {
            return NotFound(new { error = "Job not found" });
        }

        return Ok(new
        {
            jobId = job.JobId,
            sourceId = job.SourceId,
            sourceName = job.SourceName,
            sourceType = job.SourceType,
            status = job.Status.ToString().ToLowerInvariant(),
            itemsDiscovered = job.ItemsDiscovered,
            itemsProcessed = job.ItemsProcessed,
            itemsFailed = job.ItemsFailed,
            itemsSkipped = job.ItemsSkipped,
            progress = job.Progress,
            currentItem = job.CurrentItem,
            errorMessage = job.ErrorMessage,
            startedAt = job.StartedAt,
            completedAt = job.CompletedAt
        });
    }

    /// <summary>
    /// Cancel an ingestion job
    /// </summary>
    [HttpDelete("jobs/{id:guid}")]
    public async Task<IActionResult> CancelJob(Guid id, CancellationToken ct = default)
    {
        var cancelled = await ingestionService.CancelJobAsync(id, ct);
        if (!cancelled)
        {
            return NotFound(new Models.ApiError("Job not found or already completed", "JOB_NOT_FOUND"));
        }

        return Ok(new { cancelled = true, jobId = id });
    }

    /// <summary>
    /// Stream job progress via SSE
    /// </summary>
    [HttpGet("jobs/{id:guid}/stream")]
    public async Task StreamProgress(Guid id, CancellationToken ct = default)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        await foreach (var progress in ingestionService.StreamProgressAsync(id, ct))
        {
            var data = System.Text.Json.JsonSerializer.Serialize(new
            {
                jobId = progress.JobId,
                sourceId = progress.SourceId,
                itemsDiscovered = progress.ItemsDiscovered,
                itemsProcessed = progress.ItemsProcessed,
                itemsFailed = progress.ItemsFailed,
                itemsSkipped = progress.ItemsSkipped,
                currentItem = progress.CurrentItem,
                status = progress.Status.ToString().ToLowerInvariant(),
                progress = progress.Progress,
                errorMessage = progress.ErrorMessage
            });

            await Response.WriteAsync($"data: {data}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }

    /// <summary>
    /// Get signals emitted by an ingestion job
    /// </summary>
    [HttpGet("jobs/{id:guid}/signals")]
    public IActionResult GetJobSignals(Guid id)
    {
        var job = ingestionService.GetJob(id);
        if (job == null)
        {
            return NotFound(new { error = "Job not found" });
        }

        var signals = ingestionService.GetJobSignals(id);

        return Ok(new
        {
            jobId = id,
            signals = signals.Select(s => new
            {
                signalType = s.SignalType,
                payload = s.Payload,
                emittedAt = s.EmittedAt
            })
        });
    }

    #endregion
}

/// <summary>
/// Request to trigger a sync for an ingestion source
/// </summary>
public record TriggerSyncRequest(
    bool IncrementalSync = true,
    int MaxItems = 0,
    bool ContinueOnError = true,
    int Priority = 100
);
