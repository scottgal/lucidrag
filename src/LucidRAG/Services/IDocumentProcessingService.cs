using Mostlylucid.DocSummarizer;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;
using LucidRAG.Entities;

namespace LucidRAG.Services;

public interface IDocumentProcessingService
{
    Task<Guid> QueueDocumentAsync(Stream fileStream, string filename, Guid? collectionId, CancellationToken ct = default);
    Task<DocumentEntity?> GetDocumentAsync(Guid documentId, CancellationToken ct = default);
    Task<List<DocumentEntity>> GetDocumentsAsync(Guid? collectionId = null, CancellationToken ct = default);
    Task DeleteDocumentAsync(Guid documentId, CancellationToken ct = default);
    IAsyncEnumerable<ProgressUpdate> StreamProgressAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// Get segments for a document.
    /// </summary>
    Task<List<Segment>> GetSegmentsAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// Get extracted entities for a document.
    /// </summary>
    Task<List<ExtractedEntity>> GetEntitiesAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// Get evidence artifacts for a document.
    /// </summary>
    Task<List<EvidenceArtifact>> GetEvidenceAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// Retry processing for a specific document (reprocess missing signals).
    /// </summary>
    Task RetryProcessingAsync(Guid documentId, bool fullReprocess = false, CancellationToken ct = default);
}
