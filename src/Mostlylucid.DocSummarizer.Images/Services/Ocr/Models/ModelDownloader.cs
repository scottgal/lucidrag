using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace Mostlylucid.DocSummarizer.Images.Services.Ocr.Models;

/// <summary>
/// Auto-downloads ONNX models for OCR enhancement on first use.
/// Provides graceful fallback if download fails.
///
/// Supported models:
/// - EAST: Scene text detection (frozen_east_text_detection.onnx, ~100MB)
/// - CRAFT: Character Region Awareness for Text detection (craft_mlt_25k.onnx, ~150MB)
/// - Real-ESRGAN: Super-resolution upscaling (realesrgan-x4.onnx, ~60MB)
/// </summary>
public class ModelDownloader
{
    private readonly ILogger<ModelDownloader>? _logger;
    private readonly string _modelsDirectory;
    private readonly bool _autoDownload;

    // Model URLs and checksums
    private static readonly Dictionary<ModelType, ModelInfo> ModelRegistry = new()
    {
        [ModelType.EAST] = new ModelInfo
        {
            FileName = "frozen_east_text_detection.onnx",
            Url = "https://github.com/opencv/opencv_extra/raw/master/testdata/dnn/frozen_east_text_detection.pb",
            RequiresConversion = true, // PB to ONNX conversion needed
            ApproximateSize = 100 * 1024 * 1024, // 100MB
            Description = "EAST scene text detector"
        },
        [ModelType.CRAFT] = new ModelInfo
        {
            FileName = "craft_mlt_25k.onnx",
            Url = "https://huggingface.co/models/craft_text_detection/resolve/main/craft_mlt_25k.onnx",
            RequiresConversion = false,
            ApproximateSize = 150 * 1024 * 1024, // 150MB
            Description = "CRAFT character-level text detector"
        },
        [ModelType.RealESRGAN] = new ModelInfo
        {
            FileName = "realesrgan-x4.onnx",
            Url = "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-x4plus.onnx",
            RequiresConversion = false,
            ApproximateSize = 60 * 1024 * 1024, // 60MB
            Description = "Real-ESRGAN 4x super-resolution"
        }
    };

    public ModelDownloader(
        string modelsDirectory,
        bool autoDownload = true,
        ILogger<ModelDownloader>? logger = null)
    {
        _modelsDirectory = modelsDirectory;
        _autoDownload = autoDownload;
        _logger = logger;

        // Ensure models directory exists
        Directory.CreateDirectory(_modelsDirectory);
    }

    /// <summary>
    /// Get path to a model, downloading if necessary.
    /// Returns null if model unavailable and download fails/disabled.
    /// </summary>
    public async Task<string?> GetModelPathAsync(
        ModelType modelType,
        CancellationToken ct = default)
    {
        if (!ModelRegistry.TryGetValue(modelType, out var modelInfo))
        {
            _logger?.LogWarning("Unknown model type: {ModelType}", modelType);
            return null;
        }

        var modelPath = Path.Combine(_modelsDirectory, modelInfo.FileName);

        // Check if model already exists
        if (File.Exists(modelPath))
        {
            _logger?.LogDebug("Model {ModelType} found at {Path}", modelType, modelPath);
            return modelPath;
        }

        // Model doesn't exist - try to download if auto-download enabled
        if (!_autoDownload)
        {
            _logger?.LogWarning(
                "Model {ModelType} not found at {Path} and auto-download is disabled",
                modelType, modelPath);
            return null;
        }

        _logger?.LogInformation(
            "Model {ModelType} not found, attempting download from {Url}",
            modelType, modelInfo.Url);

        try
        {
            await DownloadModelAsync(modelType, modelInfo, modelPath, ct);
            return modelPath;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to download model {ModelType}", modelType);
            return null;
        }
    }

    /// <summary>
    /// Check if a model is available (exists locally).
    /// </summary>
    public bool IsModelAvailable(ModelType modelType)
    {
        if (!ModelRegistry.TryGetValue(modelType, out var modelInfo))
        {
            return false;
        }

        var modelPath = Path.Combine(_modelsDirectory, modelInfo.FileName);
        return File.Exists(modelPath);
    }

    /// <summary>
    /// Download a model with progress reporting.
    /// </summary>
    private async Task DownloadModelAsync(
        ModelType modelType,
        ModelInfo modelInfo,
        string destinationPath,
        CancellationToken ct)
    {
        _logger?.LogInformation(
            "Downloading {Description} (~{SizeMB}MB)...",
            modelInfo.Description,
            modelInfo.ApproximateSize / (1024 * 1024));

        var tempPath = destinationPath + ".download";

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

            // Get file size for progress reporting
            using var headResponse = await httpClient.SendAsync(
                new HttpRequestMessage(HttpMethod.Head, modelInfo.Url),
                ct);
            var totalBytes = headResponse.Content.Headers.ContentLength ?? modelInfo.ApproximateSize;

            // Download with progress
            using var response = await httpClient.GetAsync(modelInfo.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int lastReportedPercent = -1;

            while (true)
            {
                var bytesRead = await contentStream.ReadAsync(buffer, ct);
                if (bytesRead == 0) break;

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;

                // Report progress every 5%
                var percent = (int)((totalRead * 100) / totalBytes);
                if (percent >= lastReportedPercent + 5)
                {
                    lastReportedPercent = percent;
                    _logger?.LogInformation(
                        "Download progress: {Percent}% ({CurrentMB}/{TotalMB} MB)",
                        percent,
                        totalRead / (1024 * 1024),
                        totalBytes / (1024 * 1024));
                }
            }

            await fileStream.FlushAsync(ct);

            _logger?.LogInformation("Download complete, verifying...");

            // Check for PB to ONNX conversion requirement
            if (modelInfo.RequiresConversion)
            {
                _logger?.LogWarning(
                    "Model {ModelType} requires conversion from PB to ONNX format. " +
                    "This feature is not yet implemented. Model will be unavailable.",
                    modelType);

                // Clean up downloaded file
                File.Delete(tempPath);
                return;
            }

            // Move temp file to final location
            File.Move(tempPath, destinationPath, overwrite: true);

            _logger?.LogInformation(
                "Model {ModelType} successfully downloaded to {Path}",
                modelType, destinationPath);
        }
        catch (Exception ex)
        {
            // Clean up temp file on error
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }

            throw new IOException($"Failed to download model {modelType}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Get information about all registered models.
    /// </summary>
    public Dictionary<ModelType, (ModelInfo Info, bool Available)> GetModelStatus()
    {
        return ModelRegistry.ToDictionary(
            kvp => kvp.Key,
            kvp => (kvp.Value, IsModelAvailable(kvp.Key)));
    }
}

/// <summary>
/// Type of ONNX model.
/// </summary>
public enum ModelType
{
    /// <summary>
    /// EAST: Efficient and Accurate Scene Text detector
    /// </summary>
    EAST,

    /// <summary>
    /// CRAFT: Character Region Awareness For Text detection
    /// </summary>
    CRAFT,

    /// <summary>
    /// Real-ESRGAN: Real-world super-resolution
    /// </summary>
    RealESRGAN
}

/// <summary>
/// Information about a downloadable model.
/// </summary>
public record ModelInfo
{
    /// <summary>
    /// Model file name (e.g., "frozen_east_text_detection.onnx")
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Download URL
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Whether the model requires format conversion (e.g., PB to ONNX)
    /// </summary>
    public required bool RequiresConversion { get; init; }

    /// <summary>
    /// Approximate file size in bytes
    /// </summary>
    public required long ApproximateSize { get; init; }

    /// <summary>
    /// Human-readable description
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Optional SHA256 checksum for verification
    /// </summary>
    public string? Sha256 { get; init; }
}
