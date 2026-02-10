using System.Text;
using DomainClassifier.Core.Interfaces;
using DomainClassifier.Core.Models;
using DomainClassifier.Core.Services;
using LucidRAG.Core.Services;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Hubs;
using Microsoft.EntityFrameworkCore;
using Mostlylucid.DocSummarizer;
using Mostlylucid.DocSummarizer.Images.Pipeline;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.Summarizer.Core.Pipeline;
using Mostlylucid.Summarizer.Core.Utilities;
using StyloFlow.Retrieval.Entities;
using VideoSummarizer.Core.Pipeline;

namespace LucidRAG.Services.Background;

public class DocumentQueueProcessor(
    DocumentProcessingQueue queue,
    IServiceScopeFactory scopeFactory,
    IProcessingNotificationService notifications,
    ILogger<DocumentQueueProcessor> logger) : BackgroundService
{
    // Maximum time to process a single document before timing out
    private static readonly TimeSpan DocumentProcessingTimeout = TimeSpan.FromMinutes(30);

    // How often to run cleanup for abandoned progress channels
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Document queue processor started with queue instance {QueueInstanceId}",
            queue.InstanceId);

        // Clean up failed documents from previous runs
        await CleanupFailedDocumentsAsync(stoppingToken);

        // Re-queue pending documents from database (survives restarts)
        await RequeuePendingDocumentsAsync(stoppingToken);

        // Start cleanup timer
        var cleanupTimer = new PeriodicTimer(CleanupInterval);
        _ = RunCleanupLoopAsync(cleanupTimer, stoppingToken);

        logger.LogInformation("Document queue processor entering main loop, queue depth: {Depth}", queue.QueueDepth);

        while (!stoppingToken.IsCancellationRequested)
            try
            {
                logger.LogDebug("Waiting for next document in queue (depth: {Depth})...", queue.QueueDepth);
                var job = await queue.DequeueAsync(stoppingToken);
                logger.LogInformation("Dequeued document {DocumentId} for processing", job.DocumentId);

                // Create a linked token with timeout for this specific document
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeoutCts.CancelAfter(DocumentProcessingTimeout);

                try
                {
                    await ProcessDocumentAsync(job, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Document processing timed out
                    logger.LogWarning("Document {DocumentId} processing timed out after {Timeout}",
                        job.DocumentId, DocumentProcessingTimeout);
                    await MarkDocumentFailedAsync(job.DocumentId, "Processing timed out", stoppingToken);
                    queue.CompleteProgressChannel(job.DocumentId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing document from queue");
            }

        cleanupTimer.Dispose();
        logger.LogInformation("Document queue processor stopped");
    }

    private async Task RunCleanupLoopAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var cleaned = queue.CleanupAbandonedProgressChannels();
                if (cleaned > 0) logger.LogInformation("Cleaned up {Count} abandoned progress channels", cleaned);

                // Log queue stats periodically
                logger.LogDebug("Queue stats: {QueueDepth} documents queued, {ActiveChannels} active progress channels",
                    queue.QueueDepth, queue.ActiveProgressChannels);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    private async Task MarkDocumentFailedAsync(Guid documentId, string message, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

            var document = await db.Documents.FindAsync([documentId], ct);
            if (document is not null)
            {
                document.Status = DocumentStatus.Failed;
                document.StatusMessage = message;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark document {DocumentId} as failed", documentId);
        }
    }

    /// <summary>
    ///     Clean up failed and stuck documents on startup
    /// </summary>
    private async Task CleanupFailedDocumentsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

            // Find all failed documents and documents stuck in processing
            // Compute cutoff time before query to avoid LINQ translation issues
            var cutoffTime = DateTimeOffset.UtcNow.AddHours(-1);
            var failedDocs = await db.Documents
                .Where(d => d.Status == DocumentStatus.Failed ||
                            (d.Status == DocumentStatus.Processing && d.CreatedAt < cutoffTime))
                .ToListAsync(ct);

            if (failedDocs.Count == 0)
            {
                logger.LogInformation("No failed documents to clean up");
                return;
            }

            logger.LogInformation("Cleaning up {Count} failed/stuck documents on startup", failedDocs.Count);

            foreach (var doc in failedDocs)
                try
                {
                    // Delete the uploaded file if it exists
                    if (!string.IsNullOrEmpty(doc.FilePath) && File.Exists(doc.FilePath))
                    {
                        var directory = Path.GetDirectoryName(doc.FilePath);
                        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                        {
                            Directory.Delete(directory, true);
                            logger.LogDebug("Deleted upload directory for document {DocumentId}", doc.Id);
                        }
                    }

                    // Remove from database
                    db.Documents.Remove(doc);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to clean up document {DocumentId}", doc.Id);
                }

            await db.SaveChangesAsync(ct);
            logger.LogInformation("Cleaned up {Count} failed documents", failedDocs.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during failed document cleanup");
        }
    }

    /// <summary>
    ///     Re-queue pending documents from database on startup.
    ///     This ensures documents survive server restarts.
    /// </summary>
    private async Task RequeuePendingDocumentsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

            // Find all pending documents that need processing
            var pendingDocs = await db.Documents
                .Where(d => d.Status == DocumentStatus.Pending)
                .OrderBy(d => d.CreatedAt) // Process oldest first
                .ToListAsync(ct);

            if (pendingDocs.Count == 0)
            {
                logger.LogInformation("No pending documents to re-queue on startup");
                return;
            }

            logger.LogInformation("Re-queuing {Count} pending documents from database", pendingDocs.Count);

            var queued = 0;
            foreach (var doc in pendingDocs)
                try
                {
                    // Only queue if file still exists
                    if (string.IsNullOrEmpty(doc.FilePath) || !File.Exists(doc.FilePath))
                    {
                        logger.LogWarning("Skipping document {DocumentId} - file not found: {FilePath}",
                            doc.Id, doc.FilePath);

                        // Mark as failed since file is missing
                        doc.Status = DocumentStatus.Failed;
                        doc.StatusMessage = "File not found on disk";
                        continue;
                    }

                    var job = new DocumentProcessingJob(doc.Id, doc.FilePath, doc.CollectionId);
                    await queue.EnqueueAsync(job, ct);
                    queued++;

                    logger.LogDebug("Re-queued document {DocumentId}: {FileName}",
                        doc.Id, Path.GetFileName(doc.FilePath));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to re-queue document {DocumentId}", doc.Id);
                }

            await db.SaveChangesAsync(ct);
            logger.LogInformation("Re-queued {Queued}/{Total} pending documents for processing",
                queued, pendingDocs.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during pending document re-queue");
        }
    }

    private async Task ProcessDocumentAsync(DocumentProcessingJob job, CancellationToken ct)
    {
        logger.LogInformation("ProcessDocumentAsync starting for {DocumentId}, FilePath={FilePath}", job.DocumentId,
            job.FilePath);
        using var scope = scopeFactory.CreateScope();
        logger.LogDebug("Created scope for document {DocumentId}", job.DocumentId);

        // Service resolution with detailed logging
        logger.LogDebug("Resolving RagDocumentsDbContext...");
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
        logger.LogDebug("Resolving IDocumentSummarizer...");
        var summarizer = scope.ServiceProvider.GetRequiredService<IDocumentSummarizer>();
        logger.LogDebug("Resolving IVectorStore...");
        var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        logger.LogDebug("Resolving IEntityGraphService...");
        var entityGraph = scope.ServiceProvider.GetRequiredService<IEntityGraphService>();
        logger.LogDebug("Resolving IRetrievalEntityService...");
        var retrievalEntityService = scope.ServiceProvider.GetRequiredService<IRetrievalEntityService>();
        logger.LogDebug("Resolving IEvidenceRepository...");
        var evidenceRepository = scope.ServiceProvider.GetRequiredService<IEvidenceRepository>();
        logger.LogDebug("Resolving IEmbeddingService...");
        var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
        logger.LogDebug("All core services resolved successfully");

        // Check if this is an image or video file - try to resolve pipelines with timeout
        var extension = Path.GetExtension(job.FilePath).ToLowerInvariant();
        var isImageExtension =
            extension is ".gif" or ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".tiff" or ".tif";
        var isVideoExtension = extension is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".flv"
            or ".m4v" or ".mpeg" or ".mpg";
        ImagePipeline? imagePipeline = null;
        VideoPipeline? videoPipeline = null;

        if (isImageExtension)
            // Try to resolve ImagePipeline with timeout (was previously blocking on DI)
            try
            {
                logger.LogDebug("Attempting to resolve ImagePipeline for image processing...");
                var pipelineTask = Task.Run(() => scope.ServiceProvider.GetService<ImagePipeline>(), ct);
                if (await Task.WhenAny(pipelineTask, Task.Delay(10000, ct)) == pipelineTask)
                {
                    imagePipeline = await pipelineTask;
                    if (imagePipeline != null)
                        logger.LogInformation("ImagePipeline resolved successfully for image processing");
                    else
                        logger.LogWarning("ImagePipeline resolved as null, using fallback");
                }
                else
                {
                    logger.LogWarning("ImagePipeline resolution timed out (10s), using fallback");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ImagePipeline resolution failed, using fallback");
            }
        else if (isVideoExtension)
            // Try to resolve VideoPipeline for video processing
            try
            {
                logger.LogDebug("Attempting to resolve VideoPipeline for video processing...");
                var pipelineTask = Task.Run(() => scope.ServiceProvider.GetService<VideoPipeline>(), ct);
                if (await Task.WhenAny(pipelineTask, Task.Delay(10000, ct)) == pipelineTask)
                {
                    videoPipeline = await pipelineTask;
                    if (videoPipeline != null)
                        logger.LogInformation("VideoPipeline resolved successfully for video processing");
                    else
                        logger.LogWarning("VideoPipeline resolved as null, video processing unavailable");
                }
                else
                {
                    logger.LogWarning("VideoPipeline resolution timed out (10s), video processing unavailable");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "VideoPipeline resolution failed, video processing unavailable");
            }

        var document = await db.Documents.FindAsync([job.DocumentId], ct);
        if (document is null)
        {
            logger.LogWarning("Document {DocumentId} not found, skipping", job.DocumentId);
            return;
        }

        var progressChannel = queue.GetOrCreateProgressChannel(job.DocumentId);

        try
        {
            document.Status = DocumentStatus.Processing;
            document.ProcessingProgress = 0;
            await db.SaveChangesAsync(ct);

            // Notify via SignalR
            await notifications.NotifyDocumentStarted(document.Id, document.Name, document.CollectionId);

            // Report start
            await progressChannel.Writer.WriteAsync(
                ProgressUpdates.Stage("Processing", "Starting document processing..."), ct);

            // Check if file should use image or video pipeline based on extension
            var isImageFile = imagePipeline != null && isImageExtension;
            var useImageFallback = imagePipeline == null && isImageExtension;
            var isVideoFile = videoPipeline != null && isVideoExtension;
            logger.LogInformation(
                "Pipeline check for {FilePath}: extension={Extension}, imagePipeline={HasImagePipeline}, videoPipeline={HasVideoPipeline}, isImageFile={IsImage}, isVideoFile={IsVideo}, fallback={UseFallback}",
                job.FilePath, extension, imagePipeline != null ? "available" : "null",
                videoPipeline != null ? "available" : "null", isImageFile, isVideoFile, useImageFallback);
            DocumentSummary? result = null;
            List<Segment>? mediaSegments = null; // Store segments for images/videos

            if (useImageFallback)
            {
                // Image file but ImagePipeline not available - use minimal fallback
                logger.LogInformation("Processing {FileName} using minimal image fallback (ImagePipeline unavailable)",
                    Path.GetFileName(job.FilePath));

                await progressChannel.Writer.WriteAsync(
                    ProgressUpdates.Stage("Analyzing", "Processing image (minimal mode)..."), ct);

                // Create a basic segment with just the filename and extension info
                var fileBytes = await File.ReadAllBytesAsync(job.FilePath, ct);
                var contentHash = ContentHasher.ComputeHash(fileBytes);
                var filename = Path.GetFileNameWithoutExtension(job.FilePath);
                document.VectorStoreDocId = $"{filename}_{contentHash}";

                // Create a minimal segment describing the image
                var description = $"Image file: {Path.GetFileName(job.FilePath)} ({fileBytes.Length / 1024} KB)";
                if (extension == ".gif")
                    description = $"Animated GIF: {Path.GetFileName(job.FilePath)} ({fileBytes.Length / 1024} KB)";

                // Create segment using proper constructor
                var segment = new Segment(
                    document.VectorStoreDocId,
                    description,
                    SegmentType.Sentence, // Using Sentence as fallback for image description
                    0,
                    0,
                    description.Length,
                    contentHash);

                // Generate embedding
                var embedding = await embeddingService.EmbedAsync(description, ct);
                segment.Embedding = embedding;

                // Index in vector store
                await vectorStore.UpsertSegmentsAsync(
                    "ragdocs",
                    new[] { segment },
                    ct);

                document.SegmentCount = 1;
                document.ProcessingProgress = 70;
                await db.SaveChangesAsync(ct);

                mediaSegments = [segment];

                logger.LogInformation("Image fallback created 1 segment for {FileName}",
                    Path.GetFileName(job.FilePath));
            }
            else if (isImageFile)
            {
                // Use pipeline for images/GIFs
                logger.LogInformation("Processing {FileName} via {PipelineName}",
                    Path.GetFileName(job.FilePath), imagePipeline.Name);

                var pipelineProgress = new Progress<PipelineProgress>(p =>
                {
                    progressChannel.Writer.TryWrite(
                        ProgressUpdates.Stage(p.Stage, p.Message));
                });

                var pipelineResult = await imagePipeline.ProcessAsync(job.FilePath, null, pipelineProgress, ct);

                if (!pipelineResult.Success)
                    throw new InvalidOperationException($"Pipeline processing failed: {pipelineResult.Error}");

                // Update document with pipeline results
                document.SegmentCount = pipelineResult.Chunks.Count;

                // Compute content hash from file for stable document ID (matches BertRagSummarizer pattern)
                var fileBytes = await File.ReadAllBytesAsync(job.FilePath, ct);
                var contentHash = ContentHasher.ComputeHash(fileBytes);
                var filename = Path.GetFileNameWithoutExtension(job.FilePath);
                document.VectorStoreDocId = $"{filename}_{contentHash}";

                document.ProcessingProgress = 60;
                await db.SaveChangesAsync(ct);

                logger.LogInformation("Pipeline processed {FileName}: {ChunkCount} chunks in {Time}ms",
                    Path.GetFileName(job.FilePath), pipelineResult.Chunks.Count,
                    pipelineResult.ProcessingTime.TotalMilliseconds);

                // Convert ContentChunks to Segments with embeddings and index in vector store
                mediaSegments = await ConvertAndIndexImageChunksAsync(
                    pipelineResult.Chunks,
                    document.VectorStoreDocId,
                    vectorStore,
                    embeddingService,
                    logger,
                    ct);

                // Create a minimal DocumentSummary for compatibility with downstream code
                result = new DocumentSummary(
                    $"Image processed with {pipelineResult.Chunks.Count} chunks",
                    new List<TopicSummary>(),
                    new List<string>(),
                    new SummarizationTrace(
                        document.VectorStoreDocId,
                        mediaSegments.Count,
                        mediaSegments.Count,
                        new List<string>(),
                        pipelineResult.ProcessingTime,
                        1.0,
                        0.0
                    )
                );
            }
            else if (isVideoFile)
            {
                // Use VideoPipeline for video files
                logger.LogInformation("Processing video {FileName} via {PipelineName}",
                    Path.GetFileName(job.FilePath), videoPipeline!.Name);

                var pipelineProgress = new Progress<PipelineProgress>(p =>
                {
                    progressChannel.Writer.TryWrite(
                        ProgressUpdates.Stage(p.Stage, p.Message));
                });

                var pipelineResult = await videoPipeline.ProcessAsync(job.FilePath, null, pipelineProgress, ct);

                if (!pipelineResult.Success)
                    throw new InvalidOperationException($"Video pipeline processing failed: {pipelineResult.Error}");

                // Update document with pipeline results
                document.SegmentCount = pipelineResult.Chunks.Count;

                // Compute content hash from file for stable document ID
                var fileBytes = await File.ReadAllBytesAsync(job.FilePath, ct);
                var contentHash = ContentHasher.ComputeHash(fileBytes);
                var filename = Path.GetFileNameWithoutExtension(job.FilePath);
                document.VectorStoreDocId = $"{filename}_{contentHash}";

                document.ProcessingProgress = 60;
                await db.SaveChangesAsync(ct);

                logger.LogInformation("Video pipeline processed {FileName}: {ChunkCount} chunks in {Time}ms",
                    Path.GetFileName(job.FilePath), pipelineResult.Chunks.Count,
                    pipelineResult.ProcessingTime.TotalMilliseconds);

                // Convert ContentChunks to Segments with embeddings and index in vector store
                mediaSegments = await ConvertAndIndexImageChunksAsync(
                    pipelineResult.Chunks,
                    document.VectorStoreDocId,
                    vectorStore,
                    embeddingService,
                    logger,
                    ct);

                // Create a DocumentSummary for compatibility with downstream code
                result = new DocumentSummary(
                    $"Video processed with {pipelineResult.Chunks.Count} chunks",
                    new List<TopicSummary>(),
                    new List<string>(),
                    new SummarizationTrace(
                        document.VectorStoreDocId,
                        mediaSegments.Count,
                        mediaSegments.Count,
                        new List<string>(),
                        pipelineResult.ProcessingTime,
                        1.0,
                        0.0
                    )
                );
            }
            else if (isVideoExtension && videoPipeline == null)
            {
                // Video file but VideoPipeline not available - error since we can't process videos without it
                throw new NotSupportedException(
                    $"Video processing is not available. VideoPipeline could not be resolved for file: {Path.GetFileName(job.FilePath)}");
            }
            else
            {
                // Use document summarizer for traditional documents
                result = await summarizer.SummarizeFileAsync(
                    job.FilePath,
                    progressChannel.Writer,
                    cancellationToken: ct);

                // Update document with initial results
                document.SegmentCount = result.Trace.TotalChunks;
                document.VectorStoreDocId = result.Trace.DocumentId; // Store the stableDocId for segment retrieval
                document.ProcessingProgress = 60;
                await db.SaveChangesAsync(ct);
            }

            // Notify progress via SignalR
            await notifications.NotifyDocumentProgress(document.Id, document.Name, 60, "Table extraction",
                document.CollectionId);

            // Extract tables from document (if supported)
            try
            {
                progressChannel.Writer.TryWrite(
                    ProgressUpdates.Stage("Tables", "Extracting tables..."));

                var tableProcessingService = scope.ServiceProvider.GetService<TableProcessingService>();
                if (tableProcessingService != null)
                {
                    var tableEntities = await tableProcessingService.ProcessDocumentTablesAsync(
                        job.FilePath,
                        document.Id, // Use document ID as parent
                        document.CollectionId ?? Guid.Empty,
                        null, // Default options
                        ct);

                    if (tableEntities.Count > 0)
                    {
                        logger.LogInformation("Extracted {TableCount} tables from document {DocumentId}",
                            tableEntities.Count, job.DocumentId);

                        document.TableCount = tableEntities.Count;
                        document.ProcessingProgress = 70;
                        await db.SaveChangesAsync(ct);
                    }
                }
            }
            catch (Exception ex)
            {
                // Table extraction failure shouldn't fail the whole document processing
                logger.LogWarning(ex, "Table extraction failed for document {DocumentId}, continuing", job.DocumentId);
            }

            // Domain enrichment - optional post-processing step (no-op if no plugins registered)
            try
            {
                var domainEnrichment = scope.ServiceProvider.GetService<DomainEnrichmentService>();
                if (domainEnrichment != null && mediaSegments != null)
                {
                    progressChannel.Writer.TryWrite(
                        ProgressUpdates.Stage("Domain", "Running domain classification..."));

                    // Build ContentChunks from segments for domain enrichment
                    var enrichmentChunks = mediaSegments.Select((s, i) => new ContentChunk
                    {
                        Id = s.Id,
                        Text = s.Text,
                        ContentType = Mostlylucid.Summarizer.Core.Pipeline.ContentType.DocumentText,
                        SourcePath = job.FilePath,
                        Index = i
                    }).ToList();

                    await domainEnrichment.EnrichAndMergeAsync(enrichmentChunks, ct: ct);

                    logger.LogDebug("Domain enrichment completed for document {DocumentId}", job.DocumentId);
                }
            }
            catch (Exception ex)
            {
                // Domain enrichment failure shouldn't fail the whole document processing
                logger.LogWarning(ex, "Domain enrichment failed for document {DocumentId}, continuing",
                    job.DocumentId);
            }

            // Update progress
            document.ProcessingProgress = 80;
            await db.SaveChangesAsync(ct);

            // Notify progress via SignalR
            await notifications.NotifyDocumentProgress(document.Id, document.Name, 80, "Entity extraction",
                document.CollectionId);

            // Report entity extraction starting
            progressChannel.Writer.TryWrite(
                ProgressUpdates.Stage("Entities", "Extracting entities..."));

            // Get segments with TEXT for entity extraction and evidence storage
            // Vector store only contains embeddings (no text for privacy), so we extract directly from file
            List<Segment> segments;
            string? rawFileContent = null; // Kept for domain enrichment (segments from Qdrant cache have empty text)
            var stableDocId = result?.Trace?.DocumentId ?? document.VectorStoreDocId;
            try
            {
                logger.LogDebug("Extracting segments with text for VectorStoreDocId: {DocId}", stableDocId);

                // For images, we already have the segments from ConvertAndIndexImageChunksAsync
                if (mediaSegments != null)
                {
                    // Image pipeline - reuse segments we just created
                    segments = mediaSegments;
                    logger.LogDebug("Image processing: using recently created segments ({Count})", segments.Count);
                }
                else
                {
                    // Document pipeline - extract segments with text directly from file
                    // Vector store doesn't store text (for privacy), so we extract fresh
                    rawFileContent = await File.ReadAllTextAsync(job.FilePath, ct);
                    var extractionResult = await summarizer.ExtractSegmentsAsync(rawFileContent, stableDocId, ct);
                    segments = extractionResult.AllSegments;
                    logger.LogDebug("Extracted {SegmentCount} segments with text from file for {DocId}",
                        segments.Count, stableDocId);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to extract segments for document {DocumentId}, continuing without evidence storage",
                    job.DocumentId);
                segments = new List<Segment>();
            }

            // Domain enrichment for document segments (not media - those were enriched earlier)
            // Populates DomainDetected, DomainEntities etc. on segments before entity extraction
            if (segments.Count > 0 && mediaSegments == null && !string.IsNullOrEmpty(rawFileContent))
            {
                try
                {
                    var domainEnrichment = scope.ServiceProvider.GetService<DomainEnrichmentService>();
                    if (domainEnrichment != null)
                    {
                        // Build ContentChunks from raw file content, NOT from segments.
                        // Segments loaded from Qdrant cache have empty Text (privacy: no text in vector store).
                        // Split file content into ~2000-char chunks for domain classification and entity extraction.
                        var enrichmentChunks = new List<ContentChunk>();
                        const int chunkSize = 2000;
                        for (var ci = 0; ci < rawFileContent.Length; ci += chunkSize)
                        {
                            var len = Math.Min(chunkSize, rawFileContent.Length - ci);
                            enrichmentChunks.Add(new ContentChunk
                            {
                                Id = $"domain_chunk_{ci / chunkSize}",
                                Text = rawFileContent.Substring(ci, len),
                                ContentType = Mostlylucid.Summarizer.Core.Pipeline.ContentType.DocumentText,
                                SourcePath = job.FilePath,
                                Index = ci / chunkSize
                            });
                        }

                        logger.LogDebug("Domain enrichment: created {ChunkCount} chunks ({TotalChars} chars) from raw file content",
                            enrichmentChunks.Count, rawFileContent.Length);

                        var enrichmentResult = await domainEnrichment.EnrichChunksAsync(enrichmentChunks, ct: ct);

                        // Only apply domain metadata if enrichment actually ran (has entities/signals),
                        // not just classification-only results that fell below threshold
                        var topClassification = enrichmentResult.Classifications
                            .OrderByDescending(c => c.Confidence).FirstOrDefault();

                        if (enrichmentResult != DomainEnrichmentResult.Empty &&
                            topClassification is { Confidence: > 0 } &&
                            enrichmentResult.Entities.Count > 0)
                        {

                            var signalsJson = enrichmentResult.Signals.Count > 0
                                ? System.Text.Json.JsonSerializer.Serialize(
                                    enrichmentResult.Signals.ToDictionary(s => s.Key, s => s.Value))
                                : null;

                            // Entity texts are document-wide domain signals (not per-segment positional),
                            // so assign all distinct entities to every segment for retrieval matching.
                            // Exclude "dialogue" entities — they're full quoted speech and too verbose
                            // for Qdrant payload matching. Keep characters, locations, time periods, etc.
                            var allEntityTexts = enrichmentResult.Entities
                                .Where(e => e.EntityType != "dialogue")
                                .Select(e => e.Text).Distinct().ToList();

                            foreach (var segment in segments)
                            {
                                segment.DomainDetected = topClassification.DomainId;
                                segment.DomainConfidence = topClassification.Confidence;
                                segment.DomainEntities = allEntityTexts;
                                segment.DomainSignalsJson = signalsJson;
                            }

                            // Update Qdrant payload with domain metadata (no re-embedding needed)
                            var domainVectorStore = scope.ServiceProvider.GetService<IVectorStore>();
                            if (domainVectorStore != null)
                            {
                                await domainVectorStore.UpdateDomainMetadataAsync("ragdocs", segments, ct);
                            }

                            logger.LogInformation(
                                "Domain enrichment for doc segments: {Domain} ({Confidence:P1}), {EntityCount} entities",
                                topClassification.DomainId, topClassification.Confidence,
                                enrichmentResult.Entities.Count);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Domain enrichment for doc segments failed for {DocumentId}, continuing",
                        job.DocumentId);
                }
            }

            // Entity extraction - optional, failures don't block document completion
            if (segments.Count > 0)
            {
                try
                {
                    var entityResult = await entityGraph.ExtractAndStoreEntitiesAsync(
                        job.DocumentId,
                        segments,
                        ct);

                    document.EntityCount = entityResult.EntitiesExtracted;
                    logger.LogInformation(
                        "Extracted {EntityCount} entities and {RelCount} relationships for document {DocumentId}",
                        entityResult.EntitiesExtracted, entityResult.RelationshipsCreated, job.DocumentId);
                }
                catch (Exception ex)
                {
                    // Entity extraction failure shouldn't fail the whole document processing
                    logger.LogWarning(ex, "Entity extraction failed for document {DocumentId}, continuing",
                        job.DocumentId);
                }

                // Extract links from document content → graph edges (URLs, DOIs, arXiv IDs)
                // This runs for ALL documents, not domain-specific
                if (!string.IsNullOrEmpty(rawFileContent))
                {
                    try
                    {
                        var links = LinkGraphExtractor.Extract(rawFileContent);
                        if (links.Count > 0)
                        {
                            var linkCount = await entityGraph.StoreLinkEntitiesAsync(job.DocumentId, links, ct);
                            logger.LogInformation(
                                "Stored {LinkCount} link entities (URLs/DOIs/arXiv) for document {DocumentId}",
                                linkCount, job.DocumentId);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "Link graph extraction failed for document {DocumentId}, continuing",
                            job.DocumentId);
                    }
                }

                // Store as unified RetrievalEntity and evidence - independent of entity extraction
                // These two operations are decoupled so evidence storage succeeds even if
                // RetrievalEntity storage fails (e.g., SQLite schema differences in standalone mode)
                RetrievalEntity? retrievalEntity = null;
                try
                {
                    progressChannel.Writer.TryWrite(
                        ProgressUpdates.Stage("Indexing", "Indexing for cross-modal search..."));

                    var extractedEntities = await db.DocumentEntityLinks
                        .Where(del => del.DocumentId == job.DocumentId)
                        .Include(del => del.Entity)
                        .Select(del => del.Entity!)
                        .ToListAsync(ct);

                    retrievalEntity =
                        await retrievalEntityService.StoreDocumentAsync(document, segments, extractedEntities, result,
                            ct);
                    logger.LogInformation(
                        "Stored document {DocumentId} as RetrievalEntity with summary for cross-modal search",
                        job.DocumentId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Failed to store document {DocumentId} as RetrievalEntity, will store evidence using document ID directly",
                        job.DocumentId);
                }

                // Store each segment as evidence artifact (uses document ID as fallback entity ID)
                try
                {
                    // When RetrievalEntity storage failed, create a minimal RetrievalEntityRecord
                    // so the FK constraint on EvidenceArtifact.EntityId is satisfied
                    if (retrievalEntity == null)
                    {
                        var existingRecord = await db.RetrievalEntities
                            .AnyAsync(r => r.Id == document.Id, ct);
                        if (!existingRecord)
                        {
                            db.RetrievalEntities.Add(new RetrievalEntityRecord
                            {
                                Id = document.Id,
                                ContentType = "document",
                                Source = document.FilePath ?? document.SourceUrl ?? document.Name,
                                Title = document.OriginalFilename ?? document.Name,
                                CollectionId = document.CollectionId
                            });
                            await db.SaveChangesAsync(ct);
                            logger.LogInformation(
                                "Created minimal RetrievalEntityRecord {DocumentId} for evidence FK",
                                document.Id);
                        }
                    }

                    progressChannel.Writer.TryWrite(
                        ProgressUpdates.Stage("Evidence", $"Storing {segments.Count} segment evidence artifacts..."));

                    await StoreSegmentEvidenceAsync(
                        evidenceRepository, document.Id, retrievalEntity, segments, logger, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to store segment evidence for document {DocumentId}",
                        job.DocumentId);
                }
            }

            // Propagate YAML frontmatter metadata to DocumentEntity.Metadata (for venue quality, etc.)
            PropagateFileMetadata(document, job.FilePath);

            // Mark complete
            document.Status = DocumentStatus.Completed;
            document.ProcessingProgress = 100;
            document.ProcessedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            // Notify completion via SignalR
            await notifications.NotifyDocumentCompleted(document.Id, document.Name, document.SegmentCount,
                document.EntityCount, document.TableCount, document.CollectionId);

            // Report completion (channel may already be completed by summarizer)
            progressChannel.Writer.TryWrite(
                ProgressUpdates.Completed(
                    $"Completed! {document.SegmentCount} segments, {document.EntityCount} entities, {document.TableCount} tables.",
                    0));

            logger.LogInformation(
                "Document {DocumentId} processed successfully with {SegmentCount} segments, {EntityCount} entities, {TableCount} tables",
                job.DocumentId, document.SegmentCount, document.EntityCount, document.TableCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process document {DocumentId}", job.DocumentId);

            document.Status = DocumentStatus.Failed;
            document.StatusMessage = ex.Message;
            await db.SaveChangesAsync(ct);

            // Notify failure via SignalR
            await notifications.NotifyDocumentFailed(document.Id, document.Name, ex.Message, document.CollectionId);

            // Channel may already be completed
            progressChannel.Writer.TryWrite(
                ProgressUpdates.Error("Processing", $"Failed: {ex.Message}"));
        }
        finally
        {
            queue.CompleteProgressChannel(job.DocumentId);
        }
    }

    /// <summary>
    ///     Convert ContentChunks from ImagePipeline to Segments with embeddings and index in vector store.
    /// </summary>
    private static async Task<List<Segment>> ConvertAndIndexImageChunksAsync(
        IReadOnlyList<ContentChunk> chunks,
        string docId,
        IVectorStore vectorStore,
        IEmbeddingService embeddingService,
        ILogger logger,
        CancellationToken ct)
    {
        var segments = new List<Segment>();

        // Initialize embedding service if needed
        Console.WriteLine($"[DEBUG] Initializing embedding service for {chunks.Count} chunks");
        await embeddingService.InitializeAsync(ct);

        // Collect all texts for batch embedding
        var texts = chunks.Select(c => c.Text).ToList();
        Console.WriteLine(
            $"[DEBUG] Generating embeddings for {texts.Count} texts, first text: {texts.FirstOrDefault()?.Substring(0, Math.Min(50, texts.FirstOrDefault()?.Length ?? 0))}");

        // Generate embeddings for all caption texts in batch
        var embeddings = await embeddingService.EmbedBatchAsync(texts, ct);
        Console.WriteLine(
            $"[DEBUG] Generated {embeddings?.Length ?? 0} embeddings, first embedding dim: {embeddings?.FirstOrDefault()?.Length ?? 0}");

        // Detailed debug: show first 5 values of first 3 embeddings to verify they're different
        for (var dbgIdx = 0; dbgIdx < Math.Min(3, embeddings.Length); dbgIdx++)
        {
            var first5 = string.Join(",", embeddings[dbgIdx].Take(5).Select(v => v.ToString("F4")));
            var textSnippet = texts[dbgIdx]?.Substring(0, Math.Min(30, texts[dbgIdx]?.Length ?? 0)) ?? "<null>";
            Console.WriteLine($"[DEBUG] Embed[{dbgIdx}]: first5=[{first5}] text=\"{textSnippet}...\"");
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var contentHash = HashHelper.ComputeHash(chunk.Text);
            var segment = new Segment(
                docId,
                chunk.Text,
                SegmentType.Caption,
                i,
                0,
                chunk.Text.Length,
                contentHash)
            {
                SectionTitle = chunk.ContentType.ToString(),
                SalienceScore = 1.0, // Images are salient by default
                PositionWeight = 1.0,
                ChunkIndex = i,
                Embedding = embeddings[i] // Add generated embedding
            };

            segments.Add(segment);
        }

        // Apply segment selection BEFORE indexing - only index segments that will have evidence
        // This ensures every Qdrant result has corresponding text in evidence repository
        var selector = new SegmentSelector(
            0.05,
            0.80,
            250
        );
        var selectedSegments = selector.SelectForEvidence(segments);

        logger.LogInformation("SegmentSelector: {Selected}/{Total} segments selected for Qdrant indexing",
            selectedSegments.Count, segments.Count);

        // Index ONLY selected segments in vector store
        Console.WriteLine(
            $"[DEBUG] Upserting {selectedSegments.Count} selected segments to vector store (from {segments.Count} total)");
        if (selectedSegments.Count > 0)
        {
            await vectorStore.UpsertSegmentsAsync("ragdocs", selectedSegments, ct);
            Console.WriteLine($"[DEBUG] Successfully upserted {selectedSegments.Count} segments");
        }

        // Return selected segments (these will be stored as evidence)
        return selectedSegments;
    }

    /// <summary>
    ///     Store segment text as evidence artifacts.
    ///     Each segment becomes an evidence artifact linked to the RetrievalEntity (or document ID as fallback).
    ///     Evidence type: segment_text - the actual content extracted from document.
    ///     This allows the RAG vector store to contain only embeddings,
    ///     with all plaintext stored securely in the evidence repository.
    /// </summary>
    private static async Task StoreSegmentEvidenceAsync(
        IEvidenceRepository evidenceRepository,
        Guid documentId,
        RetrievalEntity? entity,
        IReadOnlyList<Segment> segments,
        ILogger logger,
        CancellationToken ct)
    {
        // Prefer RetrievalEntity ID (matches cross-modal lookup), fall back to document ID
        Guid entityId;
        if (entity != null && Guid.TryParse(entity.Id, out var parsedId))
        {
            entityId = parsedId;
        }
        else
        {
            entityId = documentId;
            logger.LogInformation(
                "Using document ID {DocumentId} as entity ID for evidence storage (RetrievalEntity unavailable)",
                documentId);
        }

        // NOTE: Segments are already pre-selected before indexing in Qdrant
        // (see ConvertAndIndexImageChunksAsync) - no need to re-select here
        logger.LogInformation(
            "Storing evidence for {Count} pre-selected segments",
            segments.Count);

        var stored = 0;
        foreach (var segment in segments)
            try
            {
                // Create segment evidence with full metadata
                var metadata = new
                {
                    segmentId = segment.Id,
                    segmentType = segment.Type.ToString(),
                    index = segment.Index,
                    sectionTitle = segment.SectionTitle,
                    headingPath = segment.HeadingPath,
                    headingLevel = segment.HeadingLevel,
                    pageNumber = segment.PageNumber,
                    lineNumber = segment.LineNumber,
                    startChar = segment.StartChar,
                    endChar = segment.EndChar,
                    contentHash = segment.ContentHash,
                    salienceScore = segment.SalienceScore,
                    positionWeight = segment.PositionWeight,
                    chunkIndex = segment.ChunkIndex
                };

                using var textStream = new MemoryStream(Encoding.UTF8.GetBytes(segment.Text));

                await evidenceRepository.StoreAsync(
                    entityId,
                    EvidenceTypes.SegmentText,
                    textStream,
                    "text/plain",
                    "BertRAG",
                    confidence: segment.SalienceScore,
                    metadata: metadata,
                    segmentHash: segment.ContentHash, // For RAG text hydration lookups
                    ct: ct);

                stored++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to store segment evidence for segment {SegmentId}", segment.Id);
            }

        logger.LogInformation("Stored {Count}/{Total} segment evidence artifacts for entity {EntityId}",
            stored, segments.Count, entityId);
    }

    /// <summary>
    ///     Read YAML frontmatter from a markdown file and propagate selected metadata
    ///     to DocumentEntity.Metadata as JSON. Used for venue quality scoring in RRF.
    /// </summary>
    private static void PropagateFileMetadata(DocumentEntity document, string filePath)
    {
        try
        {
            if (!Path.GetExtension(filePath).Equals(".md", StringComparison.OrdinalIgnoreCase))
                return;

            var content = File.ReadAllText(filePath);
            if (!content.StartsWith("---"))
                return;

            var endIdx = content.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (endIdx < 0)
                return;

            var yamlBlock = content[3..endIdx];
            var frontmatter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in yamlBlock.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var colonIdx = line.IndexOf(':');
                if (colonIdx <= 0) continue;

                var key = line[..colonIdx].Trim().ToLowerInvariant();
                var value = line[(colonIdx + 1)..].Trim();

                // Strip surrounding quotes
                if (value.Length >= 2 &&
                    ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                    value = value[1..^1];

                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                    frontmatter[key] = value;
            }

            if (frontmatter.Count == 0)
                return;

            // Build metadata JSON from frontmatter keys relevant to search ranking
            var metadataDict = new Dictionary<string, object>();

            if (frontmatter.TryGetValue("venue_quality", out var vq) && double.TryParse(vq, out var venueQuality))
                metadataDict["venue_quality"] = venueQuality;

            if (frontmatter.TryGetValue("citation_count", out var cc) && int.TryParse(cc, out var citationCount))
                metadataDict["citation_count"] = citationCount;

            if (frontmatter.TryGetValue("influential_citations", out var ic) && int.TryParse(ic, out var influential))
                metadataDict["influential_citations"] = influential;

            if (frontmatter.TryGetValue("venue", out var venue) && !string.IsNullOrEmpty(venue))
                metadataDict["venue"] = venue;

            if (frontmatter.TryGetValue("year", out var yr) && int.TryParse(yr, out var year))
                metadataDict["year"] = year;

            if (metadataDict.Count > 0)
                document.Metadata = System.Text.Json.JsonSerializer.Serialize(metadataDict);
        }
        catch
        {
            // Metadata propagation failure shouldn't affect document processing
        }
    }
}