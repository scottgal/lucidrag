using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;
using LucidRAG.Config;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Services.Background;

namespace LucidRAG.Services;

public class DocumentProcessingService(
    RagDocumentsDbContext db,
    DocumentProcessingQueue queue,
    IVectorStore vectorStore,
    IOptions<RagDocumentsConfig> config,
    ILogger<DocumentProcessingService> logger) : IDocumentProcessingService
{
    private readonly RagDocumentsConfig _config = config.Value;
    private const string CollectionName = "ragdocs";

    public async Task<Guid> QueueDocumentAsync(Stream fileStream, string filename, Guid? collectionId, CancellationToken ct = default)
    {
        // Validate extension
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        if (!_config.AllowedExtensions.Contains(extension))
        {
            throw new ArgumentException($"File type '{extension}' is not allowed. Allowed types: {string.Join(", ", _config.AllowedExtensions)}");
        }

        // Compute content hash - NOTE: This hash is for deduplication based on raw file bytes,
        // NOT for matching with vector store (which uses canonicalized markdown content)
        // The stableDocId format in vector store is: {filename}_{BertRagSummarizer.ComputeContentHash(markdown)}
        using var sha = SHA256.Create();
        var hashBytes = await sha.ComputeHashAsync(fileStream, ct);
        var contentHash = Convert.ToHexString(hashBytes).ToLowerInvariant()[..32]; // First 32 hex chars for deduplication
        fileStream.Position = 0;

        // Check for duplicate
        var existingDoc = await db.Documents
            .FirstOrDefaultAsync(d => d.ContentHash == contentHash && d.CollectionId == collectionId, ct);
        if (existingDoc is not null)
        {
            logger.LogInformation("Document with hash {Hash} already exists as {DocumentId}", contentHash, existingDoc.Id);
            return existingDoc.Id;
        }

        // Save file
        var documentId = Guid.NewGuid();
        var uploadDir = Path.Combine(_config.UploadPath, documentId.ToString());
        Directory.CreateDirectory(uploadDir);
        var filePath = Path.Combine(uploadDir, filename);

        await using (var fs = new FileStream(filePath, FileMode.Create))
        {
            await fileStream.CopyToAsync(fs, ct);
        }

        // Create document entity
        var document = new DocumentEntity
        {
            Id = documentId,
            CollectionId = collectionId,
            Name = Path.GetFileNameWithoutExtension(filename),
            OriginalFilename = filename,
            ContentHash = contentHash,
            FilePath = filePath,
            FileSizeBytes = new FileInfo(filePath).Length,
            MimeType = GetMimeType(extension),
            Status = DocumentStatus.Pending
        };

        db.Documents.Add(document);
        await db.SaveChangesAsync(ct);

        // Queue for processing
        await queue.EnqueueAsync(new DocumentProcessingJob(documentId, filePath, collectionId), ct);

        logger.LogInformation("Document {DocumentId} queued for processing: {Filename}", documentId, filename);

        return documentId;
    }

    public async Task<ImportResult> ImportDocumentAsync(
        Stream fileStream,
        string filename,
        Guid? collectionId,
        string sourcePath,
        DateTimeOffset? sourceCreatedAt,
        DateTimeOffset? sourceModifiedAt,
        CancellationToken ct = default)
    {
        // Validate extension
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        if (!_config.AllowedExtensions.Contains(extension))
        {
            throw new ArgumentException($"File type '{extension}' is not allowed. Allowed types: {string.Join(", ", _config.AllowedExtensions)}");
        }

        // Compute content hash for change detection
        using var sha = SHA256.Create();
        var hashBytes = await sha.ComputeHashAsync(fileStream, ct);
        var contentHash = Convert.ToHexString(hashBytes).ToLowerInvariant()[..32];
        fileStream.Position = 0;

        // Normalize source path for consistent matching
        var normalizedSourcePath = sourcePath.Replace('\\', '/').ToLowerInvariant();

        // Check for existing document with same source path in collection
        var existingDoc = await db.Documents
            .FirstOrDefaultAsync(d =>
                d.SourcePath == normalizedSourcePath &&
                d.CollectionId == collectionId, ct);

        if (existingDoc != null)
        {
            // Document exists - check if content changed
            if (existingDoc.ContentHash == contentHash)
            {
                // Content unchanged - skip
                logger.LogDebug("Document unchanged (same hash), skipping: {SourcePath}", normalizedSourcePath);
                return new ImportResult(existingDoc.Id, normalizedSourcePath, ImportAction.Unchanged, existingDoc.Version);
            }

            // Content changed - update the document
            logger.LogInformation("Document content changed, updating: {SourcePath} (version {Version})",
                normalizedSourcePath, existingDoc.Version + 1);

            // Delete old vectors if they exist
            if (!string.IsNullOrEmpty(existingDoc.VectorStoreDocId))
            {
                try
                {
                    await vectorStore.DeleteDocumentAsync(CollectionName, existingDoc.VectorStoreDocId, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete old vectors for document update: {DocId}", existingDoc.Id);
                }
            }

            // Update file on disk
            var existingFilePath = existingDoc.FilePath;
            if (!string.IsNullOrEmpty(existingFilePath) && File.Exists(existingFilePath))
            {
                await using var fs = new FileStream(existingFilePath, FileMode.Create);
                await fileStream.CopyToAsync(fs, ct);
            }
            else
            {
                // Create new file path
                var uploadDir = Path.Combine(_config.UploadPath, existingDoc.Id.ToString());
                Directory.CreateDirectory(uploadDir);
                existingFilePath = Path.Combine(uploadDir, filename);
                await using var fs = new FileStream(existingFilePath, FileMode.Create);
                await fileStream.CopyToAsync(fs, ct);
            }

            // Update entity
            existingDoc.ContentHash = contentHash;
            existingDoc.FilePath = existingFilePath;
            existingDoc.FileSizeBytes = new FileInfo(existingFilePath).Length;
            existingDoc.OriginalFilename = filename;
            existingDoc.SourceModifiedAt = sourceModifiedAt ?? DateTimeOffset.UtcNow;
            existingDoc.Version++;
            existingDoc.Status = DocumentStatus.Pending;
            existingDoc.StatusMessage = null;
            existingDoc.ProcessingProgress = 0;
            existingDoc.ProcessedAt = null;
            existingDoc.VectorStoreDocId = null;

            await db.SaveChangesAsync(ct);

            // Re-queue for processing
            await queue.EnqueueAsync(new DocumentProcessingJob(existingDoc.Id, existingFilePath, collectionId), ct);

            return new ImportResult(existingDoc.Id, normalizedSourcePath, ImportAction.Updated, existingDoc.Version);
        }

        // No existing document - create new
        var documentId = Guid.NewGuid();
        var uploadDirectory = Path.Combine(_config.UploadPath, documentId.ToString());
        Directory.CreateDirectory(uploadDirectory);
        var filePath = Path.Combine(uploadDirectory, filename);

        await using (var fs = new FileStream(filePath, FileMode.Create))
        {
            await fileStream.CopyToAsync(fs, ct);
        }

        var document = new DocumentEntity
        {
            Id = documentId,
            CollectionId = collectionId,
            Name = Path.GetFileNameWithoutExtension(filename),
            OriginalFilename = filename,
            ContentHash = contentHash,
            FilePath = filePath,
            FileSizeBytes = new FileInfo(filePath).Length,
            MimeType = GetMimeType(extension),
            Status = DocumentStatus.Pending,
            SourcePath = normalizedSourcePath,
            SourceCreatedAt = sourceCreatedAt,
            SourceModifiedAt = sourceModifiedAt ?? DateTimeOffset.UtcNow,
            Version = 1
        };

        // Preserve original creation date if provided
        if (sourceCreatedAt.HasValue)
        {
            document.CreatedAt = sourceCreatedAt.Value;
        }

        db.Documents.Add(document);
        await db.SaveChangesAsync(ct);

        await queue.EnqueueAsync(new DocumentProcessingJob(documentId, filePath, collectionId), ct);

        logger.LogInformation("Document {DocumentId} imported and queued: {SourcePath}", documentId, normalizedSourcePath);

        return new ImportResult(documentId, normalizedSourcePath, ImportAction.Created, 1);
    }

    public async Task<DocumentEntity?> GetDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        return await db.Documents
            .Include(d => d.Collection)
            .FirstOrDefaultAsync(d => d.Id == documentId, ct);
    }

    public async Task<List<DocumentEntity>> GetDocumentsAsync(Guid? collectionId = null, CancellationToken ct = default)
    {
        var query = db.Documents.Include(d => d.Collection).AsQueryable();

        if (collectionId.HasValue)
        {
            query = query.Where(d => d.CollectionId == collectionId);
        }

        return await query.OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
    }

    public async Task DeleteDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await db.Documents.FindAsync([documentId], ct);
        if (document is null) return;

        // Delete vectors from DocSummarizer's vector store
        try
        {
            // The document ID used by DocSummarizer is based on content hash
            var docIdForVectors = document.ContentHash;
            if (!string.IsNullOrEmpty(docIdForVectors))
            {
                await vectorStore.DeleteDocumentAsync(CollectionName, docIdForVectors, ct);
                logger.LogInformation("Deleted vectors for document {DocumentId} (hash: {Hash})", documentId, docIdForVectors);
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail - vector store cleanup is best effort
            logger.LogWarning(ex, "Failed to delete vectors for document {DocumentId}", documentId);
        }

        // Delete file from disk
        if (!string.IsNullOrEmpty(document.FilePath) && File.Exists(document.FilePath))
        {
            var dir = Path.GetDirectoryName(document.FilePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // Delete from database (cascades to entity links)
        db.Documents.Remove(document);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Document {DocumentId} deleted", documentId);
    }

    public async IAsyncEnumerable<ProgressUpdate> StreamProgressAsync(Guid documentId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!queue.TryGetProgressChannel(documentId, out var channel) || channel is null)
        {
            // Document might already be processed or doesn't exist
            var doc = await GetDocumentAsync(documentId, ct);
            if (doc is null)
            {
                yield return ProgressUpdates.Error("Status", "Document not found", 0);
                yield break;
            }

            yield return doc.Status switch
            {
                DocumentStatus.Completed => ProgressUpdates.Completed($"Completed with {doc.SegmentCount} segments", 0),
                DocumentStatus.Failed => ProgressUpdates.Error("Processing", doc.StatusMessage ?? "Failed", 0),
                DocumentStatus.Pending => ProgressUpdates.Stage("Pending", "Waiting in queue...", 0, 0),
                _ => ProgressUpdates.Stage("Processing", $"Progress: {doc.ProcessingProgress}%", doc.ProcessingProgress, 0)
            };
            yield break;
        }

        await foreach (var update in channel.Reader.ReadAllAsync(ct))
        {
            yield return update;
        }
    }

    private static string GetMimeType(string extension) => extension switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".md" => "text/markdown",
        ".txt" => "text/plain",
        ".html" => "text/html",
        _ => "application/octet-stream"
    };

    public async Task<List<Segment>> GetSegmentsAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await db.Documents.FindAsync([documentId], ct);
        if (document is null) return [];

        // Use the VectorStoreDocId if available (set after processing)
        // Otherwise fall back to trying to construct it from Name/ContentHash
        var stableDocId = document.VectorStoreDocId;
        if (string.IsNullOrEmpty(stableDocId))
        {
            logger.LogWarning("Document {DocumentId} has no VectorStoreDocId - may not have been fully processed", documentId);
            return [];
        }

        // Use search with docId filter to get all segments
        try
        {
            // Get segment count from document
            if (document.SegmentCount == 0) return [];

            // Search with a high topK and filter by docId
            var embedding = new float[384]; // ONNX embedding dimension
            var segments = await vectorStore.SearchAsync(CollectionName, embedding, Math.Max(document.SegmentCount, 100), stableDocId, ct);
            return segments;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get segments for document {DocumentId} (VectorStoreDocId: {StableDocId})", documentId, stableDocId);
            return [];
        }
    }

    public async Task<List<ExtractedEntity>> GetEntitiesAsync(Guid documentId, CancellationToken ct = default)
    {
        // Get entities linked to this document through DocumentEntityLink
        return await db.DocumentEntityLinks
            .Where(link => link.DocumentId == documentId)
            .Select(link => link.Entity)
            .Distinct()
            .OrderByDescending(e => e.UpdatedAt)
            .ToListAsync(ct);
    }

    public async Task<List<EvidenceArtifact>> GetEvidenceAsync(Guid documentId, CancellationToken ct = default)
    {
        // Get evidence artifacts linked to retrieval entities for this collection
        var document = await db.Documents.FindAsync([documentId], ct);
        if (document?.CollectionId == null) return [];

        return await db.EvidenceArtifacts
            .Where(e => db.RetrievalEntities
                .Any(ent => ent.Id == e.EntityId && ent.CollectionId == document.CollectionId))
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task RetryProcessingAsync(Guid documentId, bool fullReprocess = false, CancellationToken ct = default)
    {
        var document = await db.Documents.FindAsync([documentId], ct);
        if (document is null)
        {
            throw new ArgumentException($"Document {documentId} not found");
        }

        // Reset status to pending for reprocessing
        document.Status = DocumentStatus.Pending;
        document.ProcessingProgress = 0;
        document.StatusMessage = fullReprocess ? "Queued for full reprocessing" : "Queued for signal recovery";

        await db.SaveChangesAsync(ct);

        // Re-queue for processing
        await queue.EnqueueAsync(new DocumentProcessingJob(documentId, document.FilePath!, document.CollectionId), ct);

        logger.LogInformation("Document {DocumentId} queued for {Mode}", documentId, fullReprocess ? "full reprocessing" : "signal recovery");
    }

    public async Task<int> ClearAllAsync(bool clearVectors = true, CancellationToken ct = default)
    {
        // Get all documents first
        var documents = await db.Documents.ToListAsync(ct);
        var count = documents.Count;

        // Delete files from disk
        foreach (var doc in documents)
        {
            try
            {
                if (!string.IsNullOrEmpty(doc.FilePath) && File.Exists(doc.FilePath))
                {
                    var dir = Path.GetDirectoryName(doc.FilePath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete files for document {DocumentId}", doc.Id);
            }
        }

        // Clear vectors from vector store by deleting and recreating collection
        if (clearVectors)
        {
            try
            {
                await vectorStore.DeleteCollectionAsync(CollectionName, ct);
                await vectorStore.InitializeAsync(CollectionName, 384, ct); // Reinitialize empty collection
                logger.LogInformation("Cleared vectors from collection {CollectionName}", CollectionName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to clear vectors from collection {CollectionName}", CollectionName);
            }
        }

        // Clear database (cascade deletes entity links, etc.)
        db.Documents.RemoveRange(documents);

        // Also clear extracted entities not linked to any documents
        var orphanedEntities = await db.Entities.ToListAsync(ct);
        db.Entities.RemoveRange(orphanedEntities);

        // Clear collections
        var collections = await db.Collections.ToListAsync(ct);
        db.Collections.RemoveRange(collections);

        // Clear retrieval entities
        var retrievalEntities = await db.RetrievalEntities.ToListAsync(ct);
        db.RetrievalEntities.RemoveRange(retrievalEntities);

        // Clear evidence artifacts
        var evidence = await db.EvidenceArtifacts.ToListAsync(ct);
        db.EvidenceArtifacts.RemoveRange(evidence);

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Cleared {Count} documents and all related data", count);
        return count;
    }
}
