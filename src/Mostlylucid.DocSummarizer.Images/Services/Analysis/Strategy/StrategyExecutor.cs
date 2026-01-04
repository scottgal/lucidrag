using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.PixelFormats;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Strategy;

/// <summary>
/// Executes analysis strategies by applying preprocessing steps using ImageSharp
/// </summary>
public class StrategyExecutor
{
    private readonly ILogger<StrategyExecutor> _logger;
    private readonly string _tempDirectory;

    public StrategyExecutor(ILogger<StrategyExecutor> logger)
    {
        _logger = logger;
        _tempDirectory = Path.Combine(Path.GetTempPath(), "lucidrag_strategies");
        Directory.CreateDirectory(_tempDirectory);
    }

    /// <summary>
    /// Select the best strategy for the given profile and goal
    /// </summary>
    public AnalysisStrategy? SelectStrategy(ImageProfile profile, string goal)
    {
        var applicable = BuiltInStrategies.All
            .Where(s => s.IsApplicable(profile, goal))
            .OrderByDescending(s => s.Priority)
            .ToList();

        if (applicable.Count == 0)
        {
            _logger.LogDebug("No strategy found for goal '{Goal}' and profile type", goal);
            return null;
        }

        var selected = applicable.First();
        _logger.LogInformation("Selected strategy '{Strategy}' for goal '{Goal}'", selected.Name, goal);
        return selected;
    }

    /// <summary>
    /// Execute a strategy on an image, returning the path to the preprocessed result
    /// </summary>
    public async Task<string> ExecuteStrategyAsync(
        string imagePath,
        AnalysisStrategy strategy,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Executing strategy '{Strategy}' with {StepCount} steps",
            strategy.Name, strategy.Steps.Count);

        var currentPath = imagePath;
        var stepNumber = 0;

        foreach (var step in strategy.Steps)
        {
            stepNumber++;
            _logger.LogDebug("Applying step {StepNum}/{Total}: {StepName}",
                stepNumber, strategy.Steps.Count, step.Name);

            try
            {
                currentPath = await ApplyPreprocessingStepAsync(currentPath, step, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply step '{StepName}'. Skipping.", step.Name);
                // Continue with previous image if step fails
            }
        }

        return currentPath;
    }

    /// <summary>
    /// Apply a single preprocessing step using ImageSharp
    /// </summary>
    private async Task<string> ApplyPreprocessingStepAsync(
        string inputPath,
        PreprocessingStep step,
        CancellationToken ct)
    {
        using var image = await Image.LoadAsync<Rgba32>(inputPath, ct);

        // Apply the preprocessing based on step type
        switch (step.Type.ToLowerInvariant())
        {
            case "color":
                ApplyColorProcessing(image, step);
                break;

            case "filter":
                ApplyFilterProcessing(image, step);
                break;

            case "resize":
                ApplyResizeProcessing(image, step);
                break;

            case "geometric":
                ApplyGeometricProcessing(image, step);
                break;

            case "detection":
            case "temporal":
                // These require multi-frame or complex analysis - skip for now
                _logger.LogDebug("Step type '{Type}' requires specialized processing - using original", step.Type);
                return inputPath;

            default:
                _logger.LogWarning("Unknown step type '{Type}' - skipping", step.Type);
                return inputPath;
        }

        // Save preprocessed image
        var outputPath = Path.Combine(_tempDirectory,
            $"{Path.GetFileNameWithoutExtension(inputPath)}_{step.Id}{Path.GetExtension(inputPath)}");

        await image.SaveAsync(outputPath, ct);
        _logger.LogDebug("Saved preprocessed image to {OutputPath}", outputPath);

        return outputPath;
    }

    private void ApplyColorProcessing(Image<Rgba32> image, PreprocessingStep step)
    {
        var stepId = step.Id.ToLowerInvariant();

        if (stepId.Contains("grayscale"))
        {
            _logger.LogDebug("Converting to grayscale");
            image.Mutate(x => x.Grayscale());
        }
        else if (stepId.Contains("binarize"))
        {
            _logger.LogDebug("Binarizing image");
            // Convert to grayscale first, then apply threshold
            image.Mutate(x => x.Grayscale().BinaryThreshold(0.5f));
        }
        else if (stepId.Contains("quantize") || stepId.Contains("quantization"))
        {
            var numColors = step.Parameters.TryGetValue("num_colors", out var nc)
                ? Convert.ToInt32(nc)
                : 8;
            _logger.LogDebug("Quantizing to {NumColors} colors", numColors);
            image.Mutate(x => x.Quantize(new SixLabors.ImageSharp.Processing.Processors.Quantization.WebSafePaletteQuantizer()));
        }
        else if (stepId.Contains("invert"))
        {
            _logger.LogDebug("Inverting colors");
            image.Mutate(x => x.Invert());
        }
    }

    private void ApplyFilterProcessing(Image<Rgba32> image, PreprocessingStep step)
    {
        var stepId = step.Id.ToLowerInvariant();

        if (stepId.Contains("denoise"))
        {
            _logger.LogDebug("Applying median filter for denoising");
            // MedianBlur is computationally expensive - using GaussianBlur as alternative
            image.Mutate(x => x.GaussianBlur(0.8f));
        }
        else if (stepId.Contains("contrast"))
        {
            var amount = step.Parameters.TryGetValue("amount", out var a)
                ? Convert.ToSingle(a)
                : 1.5f;
            _logger.LogDebug("Boosting contrast by {Amount}", amount);
            image.Mutate(x => x.Contrast(amount));
        }
        else if (stepId.Contains("brightness"))
        {
            var amount = step.Parameters.TryGetValue("amount", out var a)
                ? Convert.ToSingle(a)
                : 1.2f;
            _logger.LogDebug("Adjusting brightness by {Amount}", amount);
            image.Mutate(x => x.Brightness(amount));
        }
        else if (stepId.Contains("sharpen") || stepId.Contains("edge"))
        {
            _logger.LogDebug("Sharpening image");
            image.Mutate(x => x.GaussianSharpen());
        }
        else if (stepId.Contains("blur"))
        {
            var sigma = step.Parameters.TryGetValue("sigma", out var s)
                ? Convert.ToSingle(s)
                : 1.5f;
            _logger.LogDebug("Applying Gaussian blur with sigma={Sigma}", sigma);
            image.Mutate(x => x.GaussianBlur(sigma));
        }
    }

    private void ApplyResizeProcessing(Image<Rgba32> image, PreprocessingStep step)
    {
        var stepId = step.Id.ToLowerInvariant();

        if (stepId.Contains("upscale"))
        {
            var scaleFactor = step.Parameters.TryGetValue("scale_factor", out var sf)
                ? Convert.ToDouble(sf)
                : 2.0;

            var newWidth = (int)(image.Width * scaleFactor);
            var newHeight = (int)(image.Height * scaleFactor);

            _logger.LogDebug("Upscaling from {OldW}x{OldH} to {NewW}x{NewH}",
                image.Width, image.Height, newWidth, newHeight);

            image.Mutate(x => x.Resize(newWidth, newHeight, KnownResamplers.Lanczos3));
        }
        else if (stepId.Contains("downscale"))
        {
            var scaleFactor = step.Parameters.TryGetValue("scale_factor", out var sf)
                ? Convert.ToDouble(sf)
                : 0.5;

            var newWidth = (int)(image.Width * scaleFactor);
            var newHeight = (int)(image.Height * scaleFactor);

            _logger.LogDebug("Downscaling from {OldW}x{OldH} to {NewW}x{NewH}",
                image.Width, image.Height, newWidth, newHeight);

            image.Mutate(x => x.Resize(newWidth, newHeight, KnownResamplers.Lanczos3));
        }
    }

    private void ApplyGeometricProcessing(Image<Rgba32> image, PreprocessingStep step)
    {
        var stepId = step.Id.ToLowerInvariant();

        if (stepId.Contains("rotate"))
        {
            var angle = step.Parameters.TryGetValue("angle", out var a)
                ? Convert.ToSingle(a)
                : 0f;

            if (Math.Abs(angle) > 0.1f)
            {
                _logger.LogDebug("Rotating by {Angle} degrees", angle);
                image.Mutate(x => x.Rotate(angle));
            }
        }
        else if (stepId.Contains("deskew"))
        {
            // Auto-deskew would require analysis - for now just log
            _logger.LogDebug("Auto-deskew requires analysis - skipping");
        }
        else if (stepId.Contains("crop"))
        {
            // Auto-crop would require detection - for now just log
            _logger.LogDebug("Auto-crop requires detection - skipping");
        }
    }

    /// <summary>
    /// Clean up temporary files created during strategy execution
    /// </summary>
    public void CleanupTempFiles()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                var files = Directory.GetFiles(_tempDirectory);
                foreach (var file in files)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete temp file {File}", file);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up temp directory");
        }
    }
}

/// <summary>
/// Result of strategy execution with metadata
/// </summary>
public record StrategyExecutionResult(
    string PreprocessedImagePath,
    AnalysisStrategy Strategy,
    List<string> AppliedSteps,
    Dictionary<string, object> Metadata);
