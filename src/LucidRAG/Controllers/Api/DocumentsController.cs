using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using LucidRAG.Config;
using LucidRAG.Filters;
using LucidRAG.Models;
using LucidRAG.Services;

namespace LucidRAG.Controllers.Api;

[ApiController]
[Route("api/documents")]
[DemoModeWriteBlock] // Blocks POST/PUT/DELETE when demo mode is enabled
public class DocumentsController(
    IDocumentProcessingService documentService,
    IRetrievalEntityService retrievalEntityService,
    IOptions<RagDocumentsConfig> config,
    ILogger<DocumentsController> logger) : ControllerBase
{
    private readonly RagDocumentsConfig _config = config.Value;

    /// <summary>
    /// Get demo mode status for UI
    /// </summary>
    [HttpGet("demo-status")]
    public Ok<DemoStatusResponse> GetDemoStatus()
    {
        return TypedResults.Ok(new DemoStatusResponse(
            DemoMode: _config.DemoMode.Enabled,
            Message: _config.DemoMode.Enabled ? _config.DemoMode.BannerMessage : null,
            UploadsEnabled: !_config.DemoMode.Enabled));
    }

    /// <summary>
    /// Upload a document (alias for /upload)
    /// </summary>
    [HttpPost]
    [HttpPost("upload")]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100MB
    public async Task<Results<Ok<DocumentUploadResponse>, BadRequest<ApiError>, StatusCodeHttpResult>> Upload(
        IFormFile? file,
        [FromForm] Guid? collectionId = null,
        CancellationToken ct = default)
    {
        logger.LogInformation("Upload request - File: {FileName}, Size: {Size}, ContentType: {ContentType}, CollectionId: {CollectionId}",
            file?.FileName ?? "NULL",
            file?.Length ?? 0,
            file?.ContentType ?? "NULL",
            collectionId);

        if (file == null || file.Length == 0)
        {
            logger.LogWarning("Upload failed: No file provided or file is empty. Request ContentType: {ContentType}",
                Request.ContentType);
            return TypedResults.BadRequest(new ApiError("No file provided", "FILE_REQUIRED"));
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var documentId = await documentService.QueueDocumentAsync(stream, file.FileName, collectionId, ct);

            logger.LogInformation("Document queued successfully: {DocumentId} ({FileName})", documentId, file.FileName);

            return TypedResults.Ok(new DocumentUploadResponse(
                DocumentId: documentId,
                Filename: file.FileName,
                Status: "queued",
                Message: "Document queued for processing"));
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Upload validation failed for {FileName}: {Message}", file.FileName, ex.Message);
            return TypedResults.BadRequest(new ApiError(ex.Message, "VALIDATION_ERROR"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error uploading document {Filename}", file.FileName);
            return TypedResults.StatusCode(500);
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

    /// <summary>
    /// Import/upsert a document with change detection based on source path.
    /// If a document with the same sourcePath exists in the collection, it will be updated if content changed.
    /// </summary>
    [HttpPost("import")]
    [RequestSizeLimit(100 * 1024 * 1024)]
    public async Task<IActionResult> Import(
        IFormFile? file,
        [FromForm] string? sourcePath = null,
        [FromForm] Guid? collectionId = null,
        [FromForm] DateTimeOffset? sourceCreatedAt = null,
        [FromForm] DateTimeOffset? sourceModifiedAt = null,
        CancellationToken ct = default)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { error = "No file provided" });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await documentService.ImportDocumentAsync(
                stream,
                file.FileName,
                collectionId,
                sourcePath ?? file.FileName,
                sourceCreatedAt,
                sourceModifiedAt,
                ct);

            return Ok(new
            {
                documentId = result.DocumentId,
                filename = file.FileName,
                sourcePath = result.SourcePath,
                action = result.Action.ToString().ToLowerInvariant(),
                version = result.Version,
                message = result.Action switch
                {
                    ImportAction.Created => "Document created and queued for processing",
                    ImportAction.Updated => "Document updated and queued for reprocessing",
                    ImportAction.Unchanged => "Document unchanged, skipped",
                    _ => "Document processed"
                }
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error importing document {Filename}", file.FileName);
            return StatusCode(500, new { error = "Failed to import document" });
        }
    }

    /// <summary>
    /// Batch import with change detection.
    /// </summary>
    [HttpPost("import-batch")]
    [RequestSizeLimit(500 * 1024 * 1024)]
    public async Task<IActionResult> ImportBatch(
        IFormFileCollection files,
        [FromForm] Guid? collectionId = null,
        [FromForm] string? sourceBasePath = null,
        CancellationToken ct = default)
    {
        if (files.Count == 0)
        {
            return BadRequest(new { error = "No files provided" });
        }

        var results = new List<object>();
        var created = 0;
        var updated = 0;
        var unchanged = 0;

        foreach (var file in files)
        {
            try
            {
                var sourcePath = string.IsNullOrEmpty(sourceBasePath)
                    ? file.FileName
                    : Path.Combine(sourceBasePath, file.FileName);

                await using var stream = file.OpenReadStream();
                var result = await documentService.ImportDocumentAsync(
                    stream,
                    file.FileName,
                    collectionId,
                    sourcePath,
                    null, null,
                    ct);

                switch (result.Action)
                {
                    case ImportAction.Created: created++; break;
                    case ImportAction.Updated: updated++; break;
                    case ImportAction.Unchanged: unchanged++; break;
                }

                results.Add(new
                {
                    documentId = result.DocumentId,
                    filename = file.FileName,
                    action = result.Action.ToString().ToLowerInvariant()
                });
            }
            catch (Exception ex)
            {
                results.Add(new { filename = file.FileName, action = "error", error = ex.Message });
            }
        }

        return Ok(new
        {
            summary = new { created, updated, unchanged, total = files.Count },
            documents = results
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<Results<Ok<DocumentResponse>, NotFound<ApiError>>> Get(Guid id, CancellationToken ct = default)
    {
        var document = await documentService.GetDocumentAsync(id, ct);
        if (document is null)
        {
            return TypedResults.NotFound(new ApiError("Document not found", "NOT_FOUND"));
        }

        return TypedResults.Ok(new DocumentResponse(
            Id: document.Id,
            Name: document.Name,
            OriginalFilename: document.OriginalFilename,
            Status: document.Status.ToString().ToLowerInvariant(),
            StatusMessage: document.StatusMessage,
            Progress: document.ProcessingProgress,
            SegmentCount: document.SegmentCount,
            EntityCount: document.EntityCount,
            FileSizeBytes: document.FileSizeBytes,
            MimeType: document.MimeType,
            CreatedAt: document.CreatedAt,
            ProcessedAt: document.ProcessedAt,
            CollectionId: document.CollectionId,
            CollectionName: document.Collection?.Name,
            SourceUrl: document.SourceUrl));
    }

    /// <summary>
    /// List documents with pagination.
    /// </summary>
    [HttpGet]
    public async Task<Ok<PagedResponse<DocumentListItem>>> List(
        [FromQuery] Guid? collectionId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var documents = await documentService.GetDocumentsAsync(collectionId, ct);

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<Entities.DocumentStatus>(status, true, out var statusEnum))
        {
            documents = documents.Where(d => d.Status == statusEnum).ToList();
        }

        var total = documents.Count;
        var paged = documents
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new DocumentListItem(
                Id: d.Id,
                Name: d.Name,
                OriginalFilename: d.OriginalFilename,
                Status: d.Status.ToString().ToLowerInvariant(),
                StatusMessage: d.StatusMessage,
                Progress: d.ProcessingProgress,
                SegmentCount: d.SegmentCount,
                CreatedAt: d.CreatedAt,
                CollectionId: d.CollectionId,
                CollectionName: d.Collection?.Name,
                SourceUrl: d.SourceUrl));

        return TypedResults.Ok(ApiResponseHelpers.Paged(paged, page, pageSize, total, "/api/documents"));
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
    public async Task<Ok<DeleteResponse>> Delete(Guid id, CancellationToken ct = default)
    {
        await documentService.DeleteDocumentAsync(id, ct);
        return TypedResults.Ok(new DeleteResponse(Success: true));
    }

    /// <summary>
    /// Reprocess a document. Use when a document is stuck or failed.
    /// POST body: { "mode": "full" | "signals" } - defaults to "signals"
    /// </summary>
    [HttpPost("{id:guid}/reprocess")]
    public async Task<Results<Ok<JobResponse>, NotFound<ApiError>>> Reprocess(
        Guid id,
        [FromBody] ReprocessRequest? request = null,
        CancellationToken ct = default)
    {
        try
        {
            var fullReprocess = request?.Mode?.Equals("full", StringComparison.OrdinalIgnoreCase) ?? false;
            await documentService.RetryProcessingAsync(id, fullReprocess, ct);

            return TypedResults.Ok(ApiResponseHelpers.Job(
                id,
                "queued",
                fullReprocess ? "Document queued for full reprocessing" : "Document queued for signal recovery",
                "/api/documents"));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.NotFound(new ApiError(ex.Message, "DOCUMENT_NOT_FOUND"));
        }
    }

    /// <summary>
    /// Request model for reprocessing a document.
    /// </summary>
    public record ReprocessRequest(string? Mode = null);

    /// <summary>
    /// Get detailed information about a document including segments, signals, and evidence.
    /// </summary>
    [HttpGet("{id:guid}/details")]
    public async Task<IActionResult> GetDetails(Guid id, CancellationToken ct = default)
    {
        var document = await documentService.GetDocumentAsync(id, ct);
        if (document is null)
        {
            return NotFound(new { error = "Document not found" });
        }

        // Get segments for this document
        var segments = await documentService.GetSegmentsAsync(id, ct);

        // Get retrieval entities (signals/evidence)
        var entities = await documentService.GetEntitiesAsync(id, ct);

        // Get evidence artifacts
        var evidence = await documentService.GetEvidenceAsync(id, ct);

        return Ok(new
        {
            document = new
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
                collectionName = document.Collection?.Name
            },
            segments = segments.Take(100).Select(s => new
            {
                id = s.Id,
                text = s.Text.Length > 500 ? s.Text[..500] + "..." : s.Text,
                sectionTitle = s.SectionTitle,
                pageNumber = s.PageNumber,
                index = s.Index
            }),
            segmentsTruncated = segments.Count > 100,
            totalSegments = segments.Count,
            entities = entities.Take(50).Select(e => new
            {
                id = e.Id,
                name = e.CanonicalName,
                type = e.EntityType,
                aliases = e.Aliases,
                description = e.Description
            }),
            entitiesTotalCount = entities.Count,
            evidence = evidence.Select(ev => new
            {
                id = ev.Id,
                type = ev.ArtifactType,
                description = GetEvidenceDescription(ev.ArtifactType),
                mimeType = ev.MimeType,
                sizeBytes = ev.FileSizeBytes,
                producer = ev.ProducerSource,
                confidence = ev.Confidence,
                createdAt = ev.CreatedAt
            }),
            evidenceCount = evidence.Count
        });
    }

    /// <summary>
    /// Get summary for a document (executive summary, topics, entities).
    /// </summary>
    [HttpGet("{id:guid}/summary")]
    public async Task<IActionResult> GetSummary(Guid id, CancellationToken ct = default)
    {
        // The document ID formatted as "N" is the entity ID in retrieval_entities
        var entityId = id.ToString("N");
        var entity = await retrievalEntityService.GetByIdAsync(entityId, ct);

        if (entity is null)
        {
            // Document may not have been processed with retrieval entity storage
            var document = await documentService.GetDocumentAsync(id, ct);
            if (document is null)
                return NotFound(new { error = "Document not found" });

            return Ok(new
            {
                documentId = id,
                documentName = document.Name,
                hasSummary = false,
                message = "Document does not have a summary yet. It may not have been fully processed."
            });
        }

        // Parse signals for topic summaries and open questions
        var signals = entity.Signals ?? [];
        var topicsSignal = signals.FirstOrDefault(s => s.Key == "document.topics");
        var questionsSignal = signals.FirstOrDefault(s => s.Key == "document.open_questions");

        object? topics = null;
        if (topicsSignal?.Value is string topicsJson && !string.IsNullOrEmpty(topicsJson))
        {
            try { topics = System.Text.Json.JsonSerializer.Deserialize<object>(topicsJson); }
            catch { /* ignore parse errors */ }
        }

        object? openQuestions = null;
        if (questionsSignal?.Value is string questionsJson && !string.IsNullOrEmpty(questionsJson))
        {
            try { openQuestions = System.Text.Json.JsonSerializer.Deserialize<object>(questionsJson); }
            catch { /* ignore parse errors */ }
        }

        return Ok(new
        {
            documentId = id,
            documentName = entity.Title,
            hasSummary = !string.IsNullOrEmpty(entity.Summary),
            executiveSummary = entity.Summary,
            topics,
            openQuestions,
            entityCount = entity.Entities?.Count ?? 0,
            extractedEntities = entity.Entities?.Take(20).Select(e => new
            {
                name = e.Name,
                type = e.Type,
                description = e.Description
            }),
            wordCount = entity.Metadata?.WordCount,
            contentType = entity.ContentType.ToString()
        });
    }

    private static string GetEvidenceDescription(string artifactType)
    {
        return artifactType switch
        {
            "ocr_text" => "OCR extracted text from image/scan",
            "ocr_word_boxes" => "Word-level bounding boxes from OCR",
            "llm_summary" => "LLM-generated summary",
            "filmstrip" => "Video filmstrip thumbnail grid",
            "key_frame" => "Key frame extracted from video",
            "transcript" => "Audio/video transcript",
            "audio_waveform" => "Audio waveform visualization",
            "signal_dump" => "Raw signal data export",
            "embedding" => "Vector embedding representation",
            _ => $"Evidence artifact ({artifactType})"
        };
    }

    /// <summary>
    /// Delete all documents. Requires X-Confirm-Delete: true header for safety.
    /// Use DELETE /api/documents/{id} for single document deletion.
    /// </summary>
    [HttpDelete]
    public async Task<Results<Ok<BulkDeleteResponse>, BadRequest<ApiError>, StatusCodeHttpResult>> DeleteAll(
        [FromHeader(Name = "X-Confirm-Delete")] bool confirm = false,
        [FromQuery] bool clearVectors = true,
        CancellationToken ct = default)
    {
        if (!confirm)
        {
            return TypedResults.BadRequest(new ApiError(
                "Bulk delete requires confirmation. Set X-Confirm-Delete: true header.",
                "CONFIRMATION_REQUIRED"));
        }

        try
        {
            var count = await documentService.ClearAllAsync(clearVectors, ct);
            logger.LogWarning("DELETE ALL: Deleted {Count} documents and all related data", count);

            return TypedResults.Ok(new BulkDeleteResponse(
                Deleted: count,
                VectorsCleared: clearVectors,
                Message: $"Deleted {count} documents and all related data"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete all data");
            return TypedResults.StatusCode(500);
        }
    }
}
