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
    IRetrievalEntityService retrievalEntityService,
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

    /// <summary>
    /// Upload a document (alias for /upload)
    /// </summary>
    [HttpPost]
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

    /// <summary>
    /// Retry processing a stuck or failed document.
    /// </summary>
    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, [FromQuery] bool fullReprocess = false, CancellationToken ct = default)
    {
        try
        {
            await documentService.RetryProcessingAsync(id, fullReprocess, ct);
            return Ok(new { success = true, message = fullReprocess ? "Document queued for full reprocessing" : "Document queued for signal recovery" });
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

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
    /// Clear all documents, vectors, and related data (DEV/TESTING ONLY).
    /// </summary>
    [HttpDelete("clear-all")]
    public async Task<IActionResult> ClearAll([FromQuery] bool clearVectors = true, CancellationToken ct = default)
    {
        try
        {
            var count = await documentService.ClearAllAsync(clearVectors, ct);
            logger.LogWarning("CLEAR ALL: Deleted {Count} documents and all related data", count);
            return Ok(new
            {
                success = true,
                message = $"Cleared {count} documents and all related data",
                documentsDeleted = count,
                vectorsCleared = clearVectors
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clear all data");
            return StatusCode(500, new { error = "Failed to clear all data", details = ex.Message });
        }
    }
}
