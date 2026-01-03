namespace Mostlylucid.RAG.Services;

/// <summary>
/// Service for generating text embeddings
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Ensures the embedding model is initialized, downloading if necessary
    /// </summary>
    Task EnsureInitializedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate an embedding vector for the given text
    /// </summary>
    /// <param name="text">Text to embed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Embedding vector</returns>
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate embeddings for multiple texts in a batch
    /// </summary>
    /// <param name="texts">Texts to embed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Embedding vectors</returns>
    Task<List<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default);
}
