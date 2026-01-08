using Mostlylucid.DataSummarizer.Configuration;
using Mostlylucid.DataSummarizer.Services.Onnx;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Factory for creating embedding services
/// </summary>
public static class EmbeddingServiceFactory
{
    private static IEmbeddingService? _instance;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Get or create an embedding service based on settings.
    /// If config is null or ONNX is disabled, uses hash-based embeddings.
    /// </summary>
    public static async Task<IEmbeddingService> GetOrCreateAsync(
        OnnxConfig? config = null, 
        bool verbose = false,
        CancellationToken ct = default)
    {
        if (_instance != null) return _instance;

        await _lock.WaitAsync(ct);
        try
        {
            if (_instance != null) return _instance;

            // If no config provided, use hash-based for backward compatibility
            if (config == null)
            {
                _instance = new HashEmbeddingService();
                await _instance.InitializeAsync(ct);
                return _instance;
            }

            if (config.Enabled)
            {
                try
                {
                    // Add a timeout for initialization (30 seconds) to avoid blocking tests/CI
                    using var initCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    initCts.CancelAfter(TimeSpan.FromSeconds(30));
                    
                    var onnxService = new OnnxEmbeddingService(config, verbose);
                    await onnxService.InitializeAsync(initCts.Token);
                    _instance = onnxService;
                    return _instance;
                }
                catch (OperationCanceledException)
                {
                    if (verbose)
                    {
                        Console.WriteLine("[EmbeddingFactory] ONNX initialization timed out, falling back to hash-based embeddings");
                    }
                }
                catch (Exception ex)
                {
                    if (verbose)
                    {
                        Console.WriteLine($"[EmbeddingFactory] ONNX embedding service failed to initialize: {ex.Message}");
                        Console.WriteLine("[EmbeddingFactory] Falling back to hash-based embeddings");
                    }
                }
            }

            // Fallback to hash-based embeddings
            _instance = new HashEmbeddingService();
            await _instance.InitializeAsync(ct);
            return _instance;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Create a new embedding service (does not cache/reuse instance)
    /// </summary>
    public static async Task<IEmbeddingService> CreateAsync(
        OnnxConfig? config = null,
        bool useOnnx = true,
        bool verbose = false,
        CancellationToken ct = default)
    {
        config ??= new OnnxConfig { Enabled = useOnnx };

        if (config.Enabled)
        {
            try
            {
                var onnxService = new OnnxEmbeddingService(config, verbose);
                await onnxService.InitializeAsync(ct);
                return onnxService;
            }
            catch (Exception ex)
            {
                if (verbose)
                {
                    Console.WriteLine($"[EmbeddingFactory] ONNX failed: {ex.Message}, using hash-based fallback");
                }
            }
        }

        var hashService = new HashEmbeddingService();
        await hashService.InitializeAsync(ct);
        return hashService;
    }

    /// <summary>
    /// Reset the cached instance (useful for testing)
    /// </summary>
    public static void Reset()
    {
        if (_instance is IDisposable disposable)
        {
            disposable.Dispose();
        }
        _instance = null;
    }
}
