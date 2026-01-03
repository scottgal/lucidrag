using Microsoft.Extensions.Logging;
using Mostlylucid.RAG.Config;
using Mostlylucid.DocSummarizer.Config;
using DocSummarizerOnnx = Mostlylucid.DocSummarizer.Services.Onnx.OnnxEmbeddingService;

namespace Mostlylucid.RAG.Services;

/// <summary>
/// Embedding service that uses DocSummarizer.Core's ONNX embedding infrastructure.
/// Zero external dependencies - models auto-download on first use.
/// </summary>
public class DocSummarizerEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly ILogger<DocSummarizerEmbeddingService> _logger;
    private readonly DocSummarizerOnnx _onnxService;
    private bool _initialized;

    public DocSummarizerEmbeddingService(
        ILogger<DocSummarizerEmbeddingService> logger,
        SemanticSearchConfig config)
    {
        _logger = logger;
        
        // Create ONNX config from semantic search config
        var onnxConfig = new OnnxConfig
        {
            EmbeddingModel = OnnxEmbeddingModel.AllMiniLmL6V2, // 384 dimensions, good balance
            UseQuantized = true, // Smaller, faster
            MaxEmbeddingSequenceLength = 256
        };
        
        _onnxService = new DocSummarizerOnnx(onnxConfig, verbose: false);
        _logger.LogInformation("DocSummarizer ONNX embedding service created (model: all-MiniLM-L6-v2, dim: 384)");
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        
        await _onnxService.InitializeAsync(cancellationToken);
        _initialized = true;
        
        _logger.LogInformation("ONNX embedding model initialized");
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        
        var embedding = await _onnxService.EmbedAsync(text, cancellationToken);
        
        _logger.LogDebug("Generated embedding for text of length {Length}", text.Length);
        
        return embedding;
    }

    public async Task<List<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        
        var embeddings = await _onnxService.EmbedBatchAsync(texts, cancellationToken);
        
        _logger.LogDebug("Generated {Count} embeddings", embeddings.Length);
        
        return embeddings.ToList();
    }

    public void Dispose()
    {
        _onnxService.Dispose();
    }
}
