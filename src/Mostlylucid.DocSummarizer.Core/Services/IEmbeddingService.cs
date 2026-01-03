namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
///     Interface for embedding services (ONNX or Ollama)
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    ///     Embedding dimension for this model
    /// </summary>
    int EmbeddingDimension { get; }

    /// <summary>
    ///     Initialize the service (downloads models if needed for ONNX)
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    ///     Generate embedding for text
    /// </summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>
    ///     Generate embeddings for multiple texts
    /// </summary>
    Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default);
}
