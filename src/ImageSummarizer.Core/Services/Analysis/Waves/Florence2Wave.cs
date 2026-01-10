using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Vision;
using OpenCvSharp;
using static Mostlylucid.DocSummarizer.Images.Models.Dynamic.ImageLedger;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Florence-2 Wave - Fast local captioning and OCR using ONNX models.
/// Provides sub-second inference without requiring external services.
/// Uses ColorWave signals to compensate for Florence-2's weak color detection.
/// Also runs OpenCV complexity assessment to help decide on LLM escalation.
/// Priority: 55 (after color analysis, can be used as first pass before VisionLlmWave)
///
/// In full learning mode, Florence-2 runs alongside Vision LLM to compare results
/// and learn from differences between fast/local vs slow/cloud approaches.
/// </summary>
public class Florence2Wave : IAnalysisWave
{
    private readonly Florence2CaptionService _florence2Service;
    private readonly IOptions<ImageConfig> _configOptions;
    private readonly ILogger<Florence2Wave>? _logger;

    private ImageConfig Config => _configOptions.Value;

    public string Name => "Florence2Wave";
    public int Priority => 55; // After color, before Vision LLM
    public IReadOnlyList<string> Tags => new[] { SignalTags.Content, "vision", "florence2", "onnx", "local" };

    public Florence2Wave(
        Florence2CaptionService florence2Service,
        IOptions<ImageConfig> config,
        ILogger<Florence2Wave>? logger = null)
    {
        _florence2Service = florence2Service;
        _configOptions = config;
        _logger = logger;
    }

    /// <summary>
    /// Florence-2 should run if it's enabled and available.
    /// For animated GIFs: Skip if MlOcrWave is using filmstrip mode (VisionLlmWave will handle OCR).
    /// It's a fast alternative to Vision LLM that works offline.
    /// </summary>
    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        // Check if Florence-2 is enabled
        if (!Config.EnableFlorence2)
            return false;

        // OPTIMIZATION: For animated GIFs in filmstrip mode, skip Florence-2 per-frame OCR
        // MlOcrWave has cached frames and VisionLlmWave will create a text-only strip
        // This saves ~15-20 seconds of per-frame Florence-2 processing
        var isAnimated = context.GetValue<bool>("identity.is_animated");
        var frameCount = context.GetValue<int>("identity.frame_count");
        var deferToVisionLlm = context.GetValue<bool>("ocr.ml.defer_to_visionllm");

        if (isAnimated && frameCount > 1 && deferToVisionLlm)
        {
            _logger?.LogDebug("Skipping Florence2Wave: filmstrip mode active (MlOcrWave deferred to VisionLLM)");
            return false;
        }

        return true;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        // Check if Florence-2 is available
        if (!await _florence2Service.IsAvailableAsync(ct))
        {
            _logger?.LogDebug("Florence-2 models not available, skipping");
            signals.Add(new Signal
            {
                Key = "florence2.available",
                Value = false,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "florence2", "status" }
            });
            return signals;
        }

        signals.Add(new Signal
        {
            Key = "florence2.available",
            Value = true,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "florence2", "status" }
        });

        try
        {
            // Get caption with OCR using Florence-2
            var result = await _florence2Service.GetCaptionAsync(
                imagePath,
                detailed: true,
                enhanceWithColors: true, // Use ColorWave signals
                ct: ct);

            if (!result.Success)
            {
                _logger?.LogWarning("Florence-2 failed: {Error}", result.Error);
                signals.Add(new Signal
                {
                    Key = "florence2.error",
                    Value = result.Error,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "florence2", "error" }
                });
                return signals;
            }

            // Add caption signal
            if (!string.IsNullOrWhiteSpace(result.Caption))
            {
                signals.Add(new Signal
                {
                    Key = "florence2.caption",
                    Value = result.Caption,
                    Confidence = CalculateCaptionConfidence(result),
                    Source = Name,
                    Tags = new List<string> { SignalTags.Content, "caption", "florence2", "onnx" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["model"] = "florence-2-base",
                        ["duration_ms"] = result.DurationMs,
                        ["enhanced_with_colors"] = result.EnhancedWithColors,
                        ["frame_count"] = result.FrameCount
                    }
                });

                // Note: We intentionally do NOT emit vision.llm.caption here.
                // Each wave should use its own namespace to avoid duplicate key conflicts.
                // Output formatters should check both florence2.caption and vision.llm.caption.
            }

            // Add OCR text signal if detected
            if (!string.IsNullOrWhiteSpace(result.OcrText))
            {
                signals.Add(new Signal
                {
                    Key = "florence2.ocr_text",
                    Value = result.OcrText,
                    Confidence = 0.75, // Florence-2 OCR is good but not as accurate as Tesseract
                    Source = Name,
                    Tags = new List<string> { SignalTags.Content, "ocr", "text", "florence2" }
                });

                // Also emit as content.extracted_text for compatibility
                // but only if we don't already have OCR text from a better source
                var existingOcr = context.GetValue<string>("content.extracted_text");
                if (string.IsNullOrWhiteSpace(existingOcr))
                {
                    signals.Add(new Signal
                    {
                        Key = "content.extracted_text",
                        Value = result.OcrText,
                        Confidence = 0.7, // Lower than Tesseract
                        Source = Name,
                        Tags = new List<string> { SignalTags.Content, "text" }
                    });
                }
            }

            // Add timing signal
            signals.Add(new Signal
            {
                Key = "florence2.duration_ms",
                Value = result.DurationMs,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "florence2", "performance" }
            });

            // Add scene detection signals for animated GIFs (useful for other waves)
            if (result.SceneDetection != null)
            {
                signals.Add(new Signal
                {
                    Key = "scene.count",
                    Value = result.SceneDetection.SceneCount,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "scene", "motion", "animation" }
                });

                signals.Add(new Signal
                {
                    Key = "scene.frame_indices",
                    Value = result.SceneDetection.SceneEndFrameIndices,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "scene", "frames" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["total_frames"] = result.SceneDetection.TotalFrames,
                        ["used_motion_detection"] = result.SceneDetection.UsedMotionDetection
                    }
                });

                signals.Add(new Signal
                {
                    Key = "scene.last_frame",
                    Value = result.SceneDetection.LastSceneFrameIndex,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "scene", "frames" }
                });

                signals.Add(new Signal
                {
                    Key = "scene.avg_motion",
                    Value = result.SceneDetection.AverageMotion,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "scene", "motion" }
                });

                _logger?.LogDebug(
                    "Scene detection: {SceneCount} scenes from {TotalFrames} frames (avgMotion={AvgMotion:F3})",
                    result.SceneDetection.SceneCount,
                    result.SceneDetection.TotalFrames,
                    result.SceneDetection.AverageMotion);
            }

            // Determine if we should escalate to Vision LLM
            var shouldEscalate = ShouldEscalateToLlm(result, context);
            signals.Add(new Signal
            {
                Key = "florence2.should_escalate",
                Value = shouldEscalate,
                Confidence = 0.9,
                Source = Name,
                Tags = new List<string> { "florence2", "escalation" }
            });

            _logger?.LogDebug(
                "Florence-2 completed in {DurationMs}ms: Caption={HasCaption}, OCR={HasOcr}, ShouldEscalate={ShouldEscalate}",
                result.DurationMs,
                !string.IsNullOrWhiteSpace(result.Caption),
                !string.IsNullOrWhiteSpace(result.OcrText),
                shouldEscalate);

            return signals;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Florence-2 wave failed for {Path}", imagePath);
            signals.Add(new Signal
            {
                Key = "florence2.error",
                Value = ex.Message,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "florence2", "error" }
            });
            return signals;
        }
    }

    /// <summary>
    /// Calculate confidence score for Florence-2 caption based on various factors.
    /// </summary>
    private double CalculateCaptionConfidence(Florence2CaptionResult result)
    {
        var confidence = 0.8; // Base confidence for Florence-2

        // Boost for color enhancement (means we added accurate color info)
        if (result.EnhancedWithColors)
        {
            confidence += 0.05;
        }

        // Slight penalty for multi-frame GIFs (caption may be less focused)
        if (result.FrameCount > 4)
        {
            confidence -= 0.05;
        }

        // Boost if we also got OCR text (supports the caption)
        if (!string.IsNullOrWhiteSpace(result.OcrText))
        {
            confidence += 0.03;
        }

        return Math.Min(0.95, Math.Max(0.6, confidence));
    }

    /// <summary>
    /// Determine if we should escalate to a more powerful Vision LLM.
    /// Uses OpenCV complexity assessment and other signals.
    /// Florence-2 is weak at describing animations, so always escalate GIFs.
    /// </summary>
    private bool ShouldEscalateToLlm(Florence2CaptionResult result, AnalysisContext context)
    {
        // Escalate if no caption was generated
        if (string.IsNullOrWhiteSpace(result.Caption))
        {
            _logger?.LogDebug("Florence-2 escalating: no caption generated");
            return true;
        }

        // Escalate if caption is very short (may be incomplete)
        if (result.Caption.Length < 20)
        {
            _logger?.LogDebug("Florence-2 escalating: caption too short ({Length} chars)", result.Caption.Length);
            return true;
        }

        // Always escalate for animated GIFs - Florence-2 produces generic "animated image" descriptions
        if (result.FrameCount > 1)
        {
            _logger?.LogDebug("Florence-2 escalating: animated GIF ({FrameCount} frames)", result.FrameCount);
            return true;
        }

        // Escalate if caption contains generic animation descriptions (Florence-2 limitation)
        var captionLower = result.Caption.ToLowerInvariant();
        if (captionLower.Contains("animated image") ||
            captionLower.Contains("general motion") ||
            captionLower.Contains("moving image"))
        {
            _logger?.LogDebug("Florence-2 escalating: generic animation description detected");
            return true;
        }

        // Escalate if image has high text likeliness but Florence-2 found no OCR
        var textLikeliness = context.GetValue<double>("content.text_likeliness");
        if (textLikeliness > 0.6 && string.IsNullOrWhiteSpace(result.OcrText))
        {
            _logger?.LogDebug("Florence-2 escalating: high text likeliness but no OCR");
            return true;
        }

        // Escalate if image has motion (Florence-2 may miss animation details)
        var hasMotion = context.GetValue<bool>("motion.has_motion");
        var motionType = context.GetValue<string>("motion.type");
        if (hasMotion && motionType != "static")
        {
            _logger?.LogDebug("Florence-2 escalating: detected motion ({Type})", motionType);
            return true;
        }

        // Escalate if image type suggests complexity
        var imageType = context.GetValue<string>("content.type");
        if (imageType is "Diagram" or "Chart" or "ScannedDocument")
        {
            _logger?.LogDebug("Florence-2 escalating: complex image type ({Type})", imageType);
            return true;
        }

        // Check OpenCV complexity (edge density from ColorWave)
        var edgeDensity = context.GetValue<double>("quality.edge_density");
        if (edgeDensity > Config.Florence2ComplexityThreshold)
        {
            _logger?.LogDebug("Florence-2 escalating: high complexity (edge density {Density})", edgeDensity);
            return true;
        }

        // Default: don't escalate, Florence-2 is probably sufficient
        return false;
    }

    /// <summary>
    /// Quick OpenCV complexity assessment using Canny edge detection.
    /// Returns normalized edge density (0-1).
    /// </summary>
    private (double edgeDensity, double laplacianVariance) AssessComplexityOpenCv(string imagePath)
    {
        try
        {
            using var img = Cv2.ImRead(imagePath, ImreadModes.Grayscale);
            if (img.Empty())
            {
                return (0, 0);
            }

            // Resize for consistent analysis
            var maxDim = 512;
            if (img.Width > maxDim || img.Height > maxDim)
            {
                var scale = Math.Min((double)maxDim / img.Width, (double)maxDim / img.Height);
                Cv2.Resize(img, img, new Size((int)(img.Width * scale), (int)(img.Height * scale)));
            }

            // Edge detection using Canny
            using var edges = new Mat();
            Cv2.Canny(img, edges, 50, 150);
            var edgePixels = Cv2.CountNonZero(edges);
            var totalPixels = edges.Rows * edges.Cols;
            var edgeDensity = (double)edgePixels / totalPixels;

            // Laplacian variance for blur/detail detection
            using var laplacian = new Mat();
            Cv2.Laplacian(img, laplacian, MatType.CV_64F);
            Cv2.MeanStdDev(laplacian, out _, out var stdDev);
            var laplacianVariance = stdDev.Val0 * stdDev.Val0;

            return (edgeDensity, laplacianVariance);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "OpenCV complexity assessment failed");
            return (0, 0);
        }
    }
}
