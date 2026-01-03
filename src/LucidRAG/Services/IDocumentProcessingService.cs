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
}
