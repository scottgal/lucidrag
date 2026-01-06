using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Security.Cryptography;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// CLIP Embedding Wave - Generates semantic image embeddings for similarity search
/// Uses ONNX CLIP model for fast, local embedding generation (no API calls)
/// Auto-downloads CLIP model on first use (~350MB)
/// Priority: 45 (runs after vision LLM, provides embeddings for RAG)
/// </summary>
public class ClipEmbeddingWave : IAnalysisWave
{
    private readonly ImageConfig _config;
    private readonly ModelDownloader? _modelDownloader;
    private readonly ILogger<ClipEmbeddingWave>? _logger;
    private static InferenceSession? _clipSession;
    private static readonly object _modelLock = new();

    public string Name => "ClipEmbeddingWave";
    public int Priority => 45; // After vision LLM, before synthesis
    public IReadOnlyList<string> Tags => new[] { SignalTags.Content, "embedding", "clip", "ml" };

    /// <summary>
    /// Check if CLIP should run. Respects auto-routing (fast route skips CLIP).
    /// </summary>
    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        // Skip if auto-routing says to skip this wave
        if (context.IsWaveSkippedByRouting(Name))
            return false;

        // Skip if CLIP is disabled
        if (!_config.EnableClipEmbedding)
            return false;

        return true;
    }

    // CLIP ViT-B/32 input dimensions
    private const int ClipImageSize = 224;
    private const int ClipEmbeddingSize = 512;

    public ClipEmbeddingWave(
        IOptions<ImageConfig> config,
        ModelDownloader? modelDownloader = null,
        ILogger<ClipEmbeddingWave>? logger = null)
    {
        _config = config.Value;
        _modelDownloader = modelDownloader;
        _logger = logger;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        // Skip if CLIP embedding is disabled
        if (!_config.EnableClipEmbedding)
        {
            signals.Add(new Signal
            {
                Key = "vision.clip.disabled",
                Value = true,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "embedding", "config" }
            });
            return signals;
        }

        try
        {
            // Load CLIP model (lazy, thread-safe)
            var session = await GetOrLoadClipModelAsync(ct);
            if (session == null)
            {
                _logger?.LogWarning("CLIP model not available, skipping embedding");
                signals.Add(new Signal
                {
                    Key = "vision.clip.model_unavailable",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "embedding", "warning" }
                });
                return signals;
            }

            // Generate embedding
            var embedding = await GenerateEmbeddingAsync(imagePath, session, ct);

            if (embedding != null)
            {
                // Normalize embedding (L2 norm for cosine similarity)
                var normalizedEmbedding = NormalizeEmbedding(embedding);

                // Hash embedding for deduplication
                var embeddingHash = HashEmbedding(normalizedEmbedding);

                signals.Add(new Signal
                {
                    Key = "vision.clip.embedding",
                    Value = normalizedEmbedding,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "embedding", "clip", "vector" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["dimensions"] = ClipEmbeddingSize,
                        ["model"] = "clip-vit-b-32",
                        ["normalized"] = true
                    }
                });

                signals.Add(new Signal
                {
                    Key = "vision.clip.embedding_hash",
                    Value = embeddingHash,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "embedding", "hash", "deduplication" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["algorithm"] = "sha256_truncated",
                        ["use_case"] = "deduplication"
                    }
                });

                _logger?.LogInformation(
                    "CLIP embedding generated: {Dimensions}D vector, hash={Hash}",
                    ClipEmbeddingSize,
                    embeddingHash.Substring(0, 16));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "CLIP embedding generation failed");
            signals.Add(new Signal
            {
                Key = "vision.clip.error",
                Value = ex.Message,
                Confidence = 0,
                Source = Name,
                Tags = new List<string> { "embedding", "error" }
            });
        }

        return signals;
    }

    private async Task<InferenceSession?> GetOrLoadClipModelAsync(CancellationToken ct)
    {
        if (_clipSession != null) return _clipSession;

        // Get or download the model path
        var modelPath = await GetOrDownloadClipModelAsync(ct);
        if (modelPath == null) return null;

        lock (_modelLock)
        {
            if (_clipSession != null) return _clipSession;

            try
            {
                _logger?.LogInformation("Loading CLIP model from {Path}", modelPath);

                var sessionOptions = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                };

                _clipSession = new InferenceSession(modelPath, sessionOptions);

                _logger?.LogInformation("CLIP model loaded successfully");
                return _clipSession;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load CLIP model");
                return null;
            }
        }
    }

    private async Task<string?> GetOrDownloadClipModelAsync(CancellationToken ct)
    {
        // Check explicit config path first
        if (!string.IsNullOrEmpty(_config.ClipModelPath) && File.Exists(_config.ClipModelPath))
        {
            return _config.ClipModelPath;
        }

        // Try to get from ModelDownloader (auto-downloads if needed)
        if (_modelDownloader != null)
        {
            try
            {
                _logger?.LogInformation("Checking/downloading CLIP model (~350MB on first run)...");
                var path = await _modelDownloader.GetModelPathAsync(ModelType.ClipVisual, ct);
                if (path != null && File.Exists(path))
                {
                    _logger?.LogDebug("CLIP model ready at: {Path}", path);
                    return path;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to auto-download CLIP model");
            }
        }

        // Fallback to checking default paths
        return GetClipModelPathFallback();
    }

    private string? GetClipModelPathFallback()
    {
        var paths = new[]
        {
            Path.Combine(_config.ModelsDirectory ?? "./models", "clip", "visual.onnx"),
            Path.Combine(_config.ModelsDirectory ?? "./models", "clip", "clip-vit-b-32-visual.onnx"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LucidRAG", "models", "clip", "visual.onnx")
        };

        foreach (var path in paths)
        {
            if (File.Exists(path)) return path;
        }

        _logger?.LogDebug("CLIP model not found in any default location");
        return null;
    }

    private async Task<float[]?> GenerateEmbeddingAsync(
        string imagePath,
        InferenceSession session,
        CancellationToken ct)
    {
        // Load and preprocess image
        using var image = await Image.LoadAsync<Rgb24>(imagePath, ct);

        // Resize to CLIP input size (224x224)
        image.Mutate(x => x.Resize(ClipImageSize, ClipImageSize));

        // Convert to tensor (CHW format, normalized to [0, 1])
        var tensor = new DenseTensor<float>(new[] { 1, 3, ClipImageSize, ClipImageSize });

        // Normalize using CLIP's normalization
        // Mean: [0.48145466, 0.4578275, 0.40821073]
        // Std:  [0.26862954, 0.26130258, 0.27577711]
        var mean = new[] { 0.48145466f, 0.4578275f, 0.40821073f };
        var std = new[] { 0.26862954f, 0.26130258f, 0.27577711f };

        for (int y = 0; y < ClipImageSize; y++)
        {
            for (int x = 0; x < ClipImageSize; x++)
            {
                var pixel = image[x, y];

                // R channel
                tensor[0, 0, y, x] = (pixel.R / 255f - mean[0]) / std[0];
                // G channel
                tensor[0, 1, y, x] = (pixel.G / 255f - mean[1]) / std[1];
                // B channel
                tensor[0, 2, y, x] = (pixel.B / 255f - mean[2]) / std[2];
            }
        }

        // Run inference - get input name from model metadata (varies by CLIP export)
        var inputName = session.InputNames.FirstOrDefault() ?? "input";
        _logger?.LogDebug("CLIP model input name: {InputName}", inputName);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, tensor)
        };

        using var results = session.Run(inputs);
        var output = results.FirstOrDefault()?.AsEnumerable<float>()?.ToArray();

        return output;
    }

    private float[] NormalizeEmbedding(float[] embedding)
    {
        // L2 normalization for cosine similarity
        var norm = Math.Sqrt(embedding.Sum(x => x * x));
        if (norm < 1e-10) return embedding;

        var normalized = new float[embedding.Length];
        for (int i = 0; i < embedding.Length; i++)
        {
            normalized[i] = embedding[i] / (float)norm;
        }

        return normalized;
    }

    private string HashEmbedding(float[] embedding)
    {
        // Convert to bytes and hash (for deduplication)
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(bytes);

        // Return first 16 chars of hex (64-bit hash, sufficient for deduplication)
        return Convert.ToHexString(hash).Substring(0, 16);
    }
}
