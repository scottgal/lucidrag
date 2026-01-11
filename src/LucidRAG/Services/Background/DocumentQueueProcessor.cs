using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.Summarizer.Core.Pipeline;
using LucidRAG.Config;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Hubs;

namespace LucidRAG.Services.Background;

public class DocumentQueueProcessor(
    DocumentProcessingQueue queue,
    IServiceScopeFactory scopeFactory,
    IProcessingNotificationService notifications,
    IPipelineRegistry pipelineRegistry,
    ILogger<DocumentQueueProcessor> logger) : BackgroundService
{
    // Maximum time to process a single document before timing out
    private static readonly TimeSpan DocumentProcessingTimeout = TimeSpan.FromMinutes(30);

    // How often to run cleanup for abandoned progress channels
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Document queue processor started");

        // Clean up failed documents from previous runs
        await CleanupFailedDocumentsAsync(stoppingToken);

        // Start cleanup timer
        var cleanupTimer = new PeriodicTimer(CleanupInterval);
        _ = RunCleanupLoopAsync(cleanupTimer, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await queue.DequeueAsync(stoppingToken);

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
                if (cleaned > 0)
                {
                    logger.LogInformation("Cleaned up {Count} abandoned progress channels", cleaned);
                }

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
    /// Clean up failed and stuck documents on startup
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
            {
                try
                {
                    // Delete the uploaded file if it exists
                    if (!string.IsNullOrEmpty(doc.FilePath) && File.Exists(doc.FilePath))
                    {
                        var directory = Path.GetDirectoryName(doc.FilePath);
                        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                        {
                            Directory.Delete(directory, recursive: true);
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
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation("Cleaned up {Count} failed documents", failedDocs.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during failed document cleanup");
        }
    }

    private async Task ProcessDocumentAsync(DocumentProcessingJob job, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
        var summarizer = scope.ServiceProvider.GetRequiredService<IDocumentSummarizer>();
        var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        var entityGraph = scope.ServiceProvider.GetRequiredService<IEntityGraphService>();
        var retrievalEntityService = scope.ServiceProvider.GetRequiredService<IRetrievalEntityService>();
        var evidenceRepository = scope.ServiceProvider.GetRequiredService<IEvidenceRepository>();

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
                ProgressUpdates.Stage("Processing", "Starting document processing...", 0, 0), ct);

            // Check if file should use pipeline (images/GIFs) or document summarizer
            var pipeline = pipelineRegistry.FindForFile(job.FilePath);
            DocumentSummary? result = null;

            if (pipeline != null)
            {
                // Use pipeline for images/GIFs
                logger.LogInformation("Processing {FileName} via {PipelineName}",
                    Path.GetFileName(job.FilePath), pipeline.Name);

                var pipelineProgress = new Progress<PipelineProgress>(p =>
                {
                    progressChannel.Writer.TryWrite(
                        ProgressUpdates.Stage(p.Stage, p.Message, 0, 0));
                });

                var pipelineResult = await pipeline.ProcessAsync(job.FilePath, null, pipelineProgress, ct);

                if (!pipelineResult.Success)
                {
                    throw new InvalidOperationException($"Pipeline processing failed: {pipelineResult.Error}");
                }

                // Update document with pipeline results
                document.SegmentCount = pipelineResult.Chunks.Count;
                document.VectorStoreDocId = Path.GetFileNameWithoutExtension(job.FilePath); // Use filename as stable ID
                document.ProcessingProgress = 60;
                await db.SaveChangesAsync(ct);

                logger.LogInformation("Pipeline processed {FileName}: {ChunkCount} chunks in {Time}ms",
                    Path.GetFileName(job.FilePath), pipelineResult.Chunks.Count,
                    pipelineResult.ProcessingTime.TotalMilliseconds);

                // Convert ContentChunks to Segments and index in vector store
                var segments = await ConvertAndIndexImageChunksAsync(
                    pipelineResult.Chunks,
                    document.VectorStoreDocId,
                    vectorStore,
                    ct);

                // Create a minimal DocumentSummary for compatibility with downstream code
                result = new DocumentSummary(
                    ExecutiveSummary: $"Image processed with {pipelineResult.Chunks.Count} chunks",
                    TopicSummaries: new List<TopicSummary>(),
                    OpenQuestions: new List<string>(),
                    Trace: new SummarizationTrace(
                        DocumentId: document.VectorStoreDocId,
                        TotalChunks: segments.Count,
                        ChunksProcessed: segments.Count,
                        Topics: new List<string>(),
                        TotalTime: pipelineResult.ProcessingTime,
                        CoverageScore: 1.0,
                        CitationRate: 0.0
                    )
                );
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
            await notifications.NotifyDocumentProgress(document.Id, document.Name, 60, "Table extraction", document.CollectionId);

            // Extract tables from document (if supported)
            try
            {
                progressChannel.Writer.TryWrite(
                    ProgressUpdates.Stage("Tables", "Extracting tables...", 0, 0));

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

            // Update progress
            document.ProcessingProgress = 80;
            await db.SaveChangesAsync(ct);

            // Notify progress via SignalR
            await notifications.NotifyDocumentProgress(document.Id, document.Name, 80, "Entity extraction", document.CollectionId);

            // Report entity extraction starting
            progressChannel.Writer.TryWrite(
                ProgressUpdates.Stage("Entities", "Extracting entities...", 0, 0));

            // Get segments from vector store and extract entities
            try
            {
                logger.LogDebug("Fetching segments for VectorStoreDocId: {DocId}", result.Trace.DocumentId);

                var segments = await vectorStore.GetDocumentSegmentsAsync(
                    "ragdocs", // Collection name from config
                    result.Trace.DocumentId,
                    ct);

                logger.LogDebug("Retrieved {SegmentCount} segments from vector store for {DocId}",
                    segments.Count, result.Trace.DocumentId);

                if (segments.Count > 0)
                {
                    var entityResult = await entityGraph.ExtractAndStoreEntitiesAsync(
                        job.DocumentId,
                        segments,
                        ct);

                    document.EntityCount = entityResult.EntitiesExtracted;
                    logger.LogInformation(
                        "Extracted {EntityCount} entities and {RelCount} relationships for document {DocumentId}",
                        entityResult.EntitiesExtracted, entityResult.RelationshipsCreated, job.DocumentId);

                    // Store as unified RetrievalEntity for cross-modal search
                    try
                    {
                        progressChannel.Writer.TryWrite(
                            ProgressUpdates.Stage("Indexing", "Indexing for cross-modal search...", 0, 0));

                        var extractedEntities = await db.DocumentEntityLinks
                            .Where(del => del.DocumentId == job.DocumentId)
                            .Include(del => del.Entity)
                            .Select(del => del.Entity!)
                            .ToListAsync(ct);

                        var retrievalEntity = await retrievalEntityService.StoreDocumentAsync(document, segments, extractedEntities, result, ct);
                        logger.LogInformation("Stored document {DocumentId} as RetrievalEntity with summary for cross-modal search", job.DocumentId);

                        // Store each segment as evidence artifact
                        progressChannel.Writer.TryWrite(
                            ProgressUpdates.Stage("Evidence", $"Storing {segments.Count} segment evidence artifacts...", 0, 0));

                        await StoreSegmentEvidenceAsync(evidenceRepository, retrievalEntity, segments, logger, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to store document {DocumentId} as RetrievalEntity, continuing", job.DocumentId);
                    }
                }
            }
            catch (Exception ex)
            {
                // Entity extraction failure shouldn't fail the whole document processing
                logger.LogWarning(ex, "Entity extraction failed for document {DocumentId}, continuing", job.DocumentId);
            }

            // Mark complete
            document.Status = DocumentStatus.Completed;
            document.ProcessingProgress = 100;
            document.ProcessedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            // Notify completion via SignalR
            await notifications.NotifyDocumentCompleted(document.Id, document.Name, document.SegmentCount, document.EntityCount, document.TableCount, document.CollectionId);

            // Report completion (channel may already be completed by summarizer)
            progressChannel.Writer.TryWrite(
                ProgressUpdates.Completed($"Completed! {document.SegmentCount} segments, {document.EntityCount} entities, {document.TableCount} tables.", 0));

            logger.LogInformation("Document {DocumentId} processed successfully with {SegmentCount} segments, {EntityCount} entities, {TableCount} tables",
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
                ProgressUpdates.Error("Processing", $"Failed: {ex.Message}", 0));
        }
        finally
        {
            queue.CompleteProgressChannel(job.DocumentId);
        }
    }

    /// <summary>
    /// Convert ContentChunks from ImagePipeline to Segments with embeddings and index in vector store.
    /// </summary>
    private static async Task<List<Mostlylucid.DocSummarizer.Models.Segment>> ConvertAndIndexImageChunksAsync(
        IReadOnlyList<Mostlylucid.Summarizer.Core.Pipeline.ContentChunk> chunks,
        string docId,
        IVectorStore vectorStore,
        CancellationToken ct)
    {
        var segments = new List<Mostlylucid.DocSummarizer.Models.Segment>();

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var contentHash = Mostlylucid.DocSummarizer.Models.HashHelper.ComputeHash(chunk.Text);
            var segment = new Mostlylucid.DocSummarizer.Models.Segment(
                docId,
                chunk.Text,
                Mostlylucid.DocSummarizer.Models.SegmentType.Caption,
                i,
                0,
                chunk.Text.Length,
                contentHash)
            {
                SectionTitle = chunk.ContentType.ToString(),
                SalienceScore = 1.0, // Images are salient by default
                PositionWeight = 1.0,
                ChunkIndex = i
            };

            segments.Add(segment);
        }

        // Index in vector store
        if (segments.Count > 0)
        {
            await vectorStore.UpsertSegmentsAsync("ragdocs", segments, ct);
        }

        return segments;
    }

    /// <summary>
    /// Store segment text as evidence artifacts.
    /// Each segment becomes an evidence artifact linked to the RetrievalEntity.
    /// Evidence type: segment_text - the actual content extracted from document.
    ///
    /// This allows the RAG vector store to contain only embeddings,
    /// with all plaintext stored securely in the evidence repository.
    /// </summary>
    private static async Task StoreSegmentEvidenceAsync(
        IEvidenceRepository evidenceRepository,
        StyloFlow.Retrieval.Entities.RetrievalEntity entity,
        IReadOnlyList<Mostlylucid.DocSummarizer.Models.Segment> segments,
        ILogger logger,
        CancellationToken ct)
    {
        if (!Guid.TryParse(entity.Id, out var entityId))
        {
            logger.LogWarning("Invalid entity ID format for evidence storage: {EntityId}", entity.Id);
            return;
        }

        var stored = 0;
        foreach (var segment in segments)
        {
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
                    entityId: entityId,
                    artifactType: EvidenceTypes.SegmentText,
                    content: textStream,
                    mimeType: "text/plain",
                    producerSource: "BertRAG",
                    confidence: segment.SalienceScore,
                    metadata: metadata,
                    segmentHash: segment.ContentHash,  // For RAG text hydration lookups
                    ct: ct);

                stored++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to store segment evidence for segment {SegmentId}", segment.Id);
            }
        }

        logger.LogInformation("Stored {Count}/{Total} segment evidence artifacts for entity {EntityId}",
            stored, segments.Count, entityId);
    }
}
