using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using LucidRAG.Config;
using LucidRAG.Filters;
using LucidRAG.Services;

namespace LucidRAG.Controllers.Api;

[ApiController]
[Route("api/documents")]
[DemoModeWriteBlock] // Blocks POST/PUT/DELETE when demo mode is enabled
public class DocumentsController(
    IDocumentProcessingService documentService,
    IOptions<RagDocumentsConfig> config,
    ILogger<DocumentsController> logger) : ControllerBase
{
    private readonly RagDocumentsConfig _config = config.Value;

    /// <summary>
    /// Get demo mode status for UI
    /// </summary>
    [HttpGet("demo-status")]
    public IActionResult GetDemoStatus()
    {
        return Ok(new
        {
            demoMode = _config.DemoMode.Enabled,
            message = _config.DemoMode.Enabled ? _config.DemoMode.BannerMessage : null,
            uploadsEnabled = !_config.DemoMode.Enabled
        });
    }

    [HttpPost("upload")]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100MB
    public async Task<IActionResult> Upload(
        IFormFile? file,
        [FromForm] Guid? collectionId = null,
        CancellationToken ct = default)
    {
        // Log all form data for debugging
        logger.LogInformation("Upload request - File: {FileName}, Size: {Size}, ContentType: {ContentType}, CollectionId: {CollectionId}",
            file?.FileName ?? "NULL",
            file?.Length ?? 0,
            file?.ContentType ?? "NULL",
            collectionId);

        if (file == null || file.Length == 0)
        {
            logger.LogWarning("Upload failed: No file provided or file is empty. Request ContentType: {ContentType}",
                Request.ContentType);
            return BadRequest(new { error = "No file provided", details = $"File was {(file == null ? "null" : "empty")}" });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var documentId = await documentService.QueueDocumentAsync(stream, file.FileName, collectionId, ct);

            logger.LogInformation("Document queued successfully: {DocumentId} ({FileName})", documentId, file.FileName);

            return Ok(new
            {
                documentId,
                filename = file.FileName,
                status = "queued",
                message = "Document queued for processing"
            });
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Upload validation failed for {FileName}: {Message}", file.FileName, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error uploading document {Filename}", file.FileName);
            return StatusCode(500, new { error = "Failed to upload document" });
        }
    }

    [HttpPost("upload-batch")]
    [RequestSizeLimit(500 * 1024 * 1024)] // 500MB for batch
    public async Task<IActionResult> UploadBatch(
        IFormFileCollection files,
        [FromForm] Guid? collectionId = null,
        CancellationToken ct = default)
    {
        if (files.Count == 0)
        {
            return BadRequest(new { error = "No files provided" });
        }

        var results = new List<object>();
        foreach (var file in files)
        {
            try
            {
                await using var stream = file.OpenReadStream();
                var documentId = await documentService.QueueDocumentAsync(stream, file.FileName, collectionId, ct);
                results.Add(new { documentId, filename = file.FileName, status = "queued" });
            }
            catch (Exception ex)
            {
                results.Add(new { filename = file.FileName, status = "error", error = ex.Message });
            }
        }

        return Ok(new { documents = results });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct = default)
    {
        var document = await documentService.GetDocumentAsync(id, ct);
        if (document is null)
        {
            return NotFound(new { error = "Document not found" });
        }

        return Ok(new
        {
            id = document.Id,
            name = document.Name,
            originalFilename = document.OriginalFilename,
            status = document.Status.ToString().ToLowerInvariant(),
            statusMessage = document.StatusMessage,
            progress = document.ProcessingProgress,
            segmentCount = document.SegmentCount,
            entityCount = document.EntityCount,
            fileSizeBytes = document.FileSizeBytes,
            mimeType = document.MimeType,
            createdAt = document.CreatedAt,
            processedAt = document.ProcessedAt,
            collectionId = document.CollectionId,
            collectionName = document.Collection?.Name,
            sourceUrl = document.SourceUrl
        });
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? collectionId = null, CancellationToken ct = default)
    {
        var documents = await documentService.GetDocumentsAsync(collectionId, ct);

        return Ok(new
        {
            documents = documents.Select(d => new
            {
                id = d.Id,
                name = d.Name,
                originalFilename = d.OriginalFilename,
                status = d.Status.ToString().ToLowerInvariant(),
                statusMessage = d.StatusMessage, // Include error message for failed docs
                progress = d.ProcessingProgress,
                segmentCount = d.SegmentCount,
                createdAt = d.CreatedAt,
                collectionId = d.CollectionId,
                collectionName = d.Collection?.Name,
                sourceUrl = d.SourceUrl
            })
        });
    }

    [HttpGet("{id:guid}/status")]
    public async Task StreamStatus(Guid id, CancellationToken ct = default)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        await foreach (var update in documentService.StreamProgressAsync(id, ct))
        {
            var data = System.Text.Json.JsonSerializer.Serialize(new
            {
                type = update.Type.ToString().ToLowerInvariant(),
                stage = update.Stage,
                message = update.Message,
                progress = update.PercentComplete,
                current = update.Current,
                total = update.Total
            });

            await Response.WriteAsync($"data: {data}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        await documentService.DeleteDocumentAsync(id, ct);
        return Ok(new { success = true });
    }
}
