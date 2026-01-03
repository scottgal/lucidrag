using Mostlylucid.DocSummarizer.Config;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
///     Ollama-based embedding service wrapper implementing IEmbeddingService
/// </summary>
public class OllamaEmbeddingService : IEmbeddingService
{
    private readonly OllamaService _ollama;
    private readonly int _embeddingDimension;

    public OllamaEmbeddingService(OllamaService ollama, int embeddingDimension = 768)
    {
        _ollama = ollama;
        _embeddingDimension = embeddingDimension;
    }

    /// <summary>
    ///     Embedding dimension (768 for nomic-embed-text, 1024 for mxbai-embed-large)
    /// </summary>
    public int EmbeddingDimension => _embeddingDimension;

    /// <summary>
    ///     No initialization needed for Ollama - it's always ready
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    ///     Generate embedding using Ollama
    /// </summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        return await _ollama.EmbedAsync(text, cancellationToken: ct);
    }

    /// <summary>
    ///     Generate embeddings for multiple texts (sequential due to Ollama limitations)
    /// </summary>
    public async Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var results = new List<float[]>();
        foreach (var text in texts)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await EmbedAsync(text, ct));
        }
        return results.ToArray();
    }
}
