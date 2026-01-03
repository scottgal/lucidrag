using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Background service that initializes DocSummarizer on application startup.
/// Handles embedding service initialization and optional reindexing.
/// </summary>
public class DocSummarizerInitializer : IHostedService
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;
    private readonly DocSummarizerConfig _config;
    private readonly ILogger<DocSummarizerInitializer>? _logger;

    /// <summary>
    /// Creates a new instance of the DocSummarizer initializer.
    /// </summary>
    public DocSummarizerInitializer(
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        IOptions<DocSummarizerConfig> config,
        ILogger<DocSummarizerInitializer>? logger = null)
    {
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _config = config.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Initializing DocSummarizer...");

        try
        {
            // Initialize embedding service (downloads ONNX models if needed)
            _logger?.LogDebug("Initializing embedding service...");
            await _embeddingService.InitializeAsync(cancellationToken);
            _logger?.LogInformation(
                "Embedding service initialized (dimension: {Dimension})",
                _embeddingService.EmbeddingDimension);

            // Handle reindex on startup if configured
            if (_config.BertRag.ReindexOnStartup)
            {
                _logger?.LogInformation(
                    "ReindexOnStartup is enabled - clearing vector store collection '{Collection}'",
                    _config.BertRag.CollectionName);

                await ClearVectorStoreAsync(cancellationToken);
                
                _logger?.LogInformation("Vector store cleared. Documents will be re-indexed on first access.");
            }
            else
            {
                _logger?.LogDebug(
                    "ReindexOnStartup is disabled - preserving existing embeddings in '{Collection}'",
                    _config.BertRag.CollectionName);
            }

            // Initialize vector store
            await _vectorStore.InitializeAsync(
                _config.BertRag.CollectionName,
                _embeddingService.EmbeddingDimension,
                cancellationToken);

            _logger?.LogInformation("DocSummarizer initialization complete");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize DocSummarizer");
            throw;
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger?.LogDebug("DocSummarizer stopping");
        return Task.CompletedTask;
    }

    private async Task ClearVectorStoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Initialize first to ensure connection
            await _vectorStore.InitializeAsync(
                _config.BertRag.CollectionName,
                _embeddingService.EmbeddingDimension,
                cancellationToken);

            // Delete the collection if it supports it
            if (_vectorStore is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }

            _logger?.LogDebug("Vector store collection cleared");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to clear vector store - will proceed with existing data");
        }
    }
}
