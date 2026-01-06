using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.Detection;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Text Detection Wave - Uses EAST/CRAFT ONNX models for fast text region detection.
/// Emits signals for detected text regions that downstream OCR waves can use.
///
/// Priority: 82 (runs after Identity/Color, before OcrWave)
///
/// Signals emitted:
/// - text_detection.method: Detection method used (EAST, CRAFT, TesseractPSM)
/// - text_detection.region_count: Number of detected regions
/// - text_detection.regions: Collection of detected bounding boxes
/// - text_detection.has_text: Boolean indicating if text regions were found
/// - text_detection.coverage: Percentage of image covered by text regions
/// </summary>
public class TextDetectionWave : IAnalysisWave
{
    private readonly ITextDetectionService? _detectionService;
    private readonly ILogger<TextDetectionWave>? _logger;
    private readonly bool _enabled;

    public string Name => "TextDetectionWave";
    public int Priority => 82; // After Identity/Color (100/99), before OcrWave (80)
    public IReadOnlyList<string> Tags => new[] { SignalTags.Content, "text", "detection", "ml" };

    public TextDetectionWave(
        ITextDetectionService? detectionService = null,
        IOptions<ImageConfig>? config = null,
        ILogger<TextDetectionWave>? logger = null)
    {
        _detectionService = detectionService;
        _logger = logger;
        _enabled = config?.Value.Ocr.EnableTextDetection ?? true;
    }

    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        if (!_enabled || _detectionService == null)
            return false;

        // Skip if fast route and not text-likely
        var route = context.GetValue<string>("route.selected");
        if (route == "fast")
        {
            var textLikeliness = context.GetValue<double>("content.text_likeliness");
            if (textLikeliness < 0.2)
            {
                _logger?.LogDebug("Skipping TextDetectionWave: fast route with low text likeliness");
                return false;
            }
        }

        return true;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        if (_detectionService == null)
        {
            signals.Add(new Signal
            {
                Key = "text_detection.status",
                Value = "unavailable",
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "text", "detection", "error" }
            });
            return signals;
        }

        try
        {
            var result = await _detectionService.DetectTextRegionsAsync(imagePath, ct);

            // Detection method signal
            signals.Add(new Signal
            {
                Key = "text_detection.method",
                Value = result.DetectionMethod,
                Confidence = result.Success ? 1.0 : 0.5,
                Source = Name,
                Tags = new List<string> { "text", "detection" }
            });

            // Region count signal
            signals.Add(new Signal
            {
                Key = "text_detection.region_count",
                Value = result.BoundingBoxes.Count,
                Confidence = result.Success ? 1.0 : 0.5,
                Source = Name,
                Tags = new List<string> { "text", "detection" }
            });

            // Has text boolean for quick checks
            var hasText = result.BoundingBoxes.Count > 0;
            signals.Add(new Signal
            {
                Key = "text_detection.has_text",
                Value = hasText,
                Confidence = result.Success ? 0.9 : 0.5,
                Source = Name,
                Tags = new List<string> { "text", "detection" }
            });

            // Calculate coverage if we have image dimensions
            var imageWidth = context.GetValue<int>("identity.width");
            var imageHeight = context.GetValue<int>("identity.height");
            if (imageWidth > 0 && imageHeight > 0 && result.BoundingBoxes.Count > 0)
            {
                var totalArea = result.BoundingBoxes.Sum(b => b.Width * b.Height);
                var imageArea = imageWidth * imageHeight;
                var coverage = (double)totalArea / imageArea;

                signals.Add(new Signal
                {
                    Key = "text_detection.coverage",
                    Value = Math.Min(coverage, 1.0), // Clamp due to overlapping boxes
                    Confidence = result.Success ? 0.9 : 0.5,
                    Source = Name,
                    Tags = new List<string> { "text", "detection" }
                });
            }

            // Emit individual region signals for downstream processing
            for (var i = 0; i < result.BoundingBoxes.Count; i++)
            {
                var box = result.BoundingBoxes[i];
                signals.Add(new Signal
                {
                    Key = $"text_detection.region.{i}",
                    Value = new
                    {
                        x1 = box.X1,
                        y1 = box.Y1,
                        x2 = box.X2,
                        y2 = box.Y2,
                        width = box.Width,
                        height = box.Height,
                        confidence = box.Confidence
                    },
                    Confidence = box.Confidence,
                    Source = Name,
                    Tags = new List<string> { "text", "detection", "region" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["index"] = i,
                        ["method"] = result.DetectionMethod
                    }
                });
            }

            // Collection signal for all regions (for easy downstream access)
            if (result.BoundingBoxes.Count > 0)
            {
                signals.Add(new Signal
                {
                    Key = "text_detection.regions",
                    Value = result.BoundingBoxes.Select(b => new
                    {
                        x1 = b.X1,
                        y1 = b.Y1,
                        x2 = b.X2,
                        y2 = b.Y2,
                        confidence = b.Confidence
                    }).ToList(),
                    Confidence = result.BoundingBoxes.Average(b => b.Confidence),
                    Source = Name,
                    Tags = new List<string> { "text", "detection", "collection" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["count"] = result.BoundingBoxes.Count,
                        ["method"] = result.DetectionMethod
                    }
                });
            }

            _logger?.LogInformation(
                "TextDetectionWave: {Method} detected {Count} regions for {Path}",
                result.DetectionMethod, result.BoundingBoxes.Count, Path.GetFileName(imagePath));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "TextDetectionWave failed for {Path}", imagePath);

            signals.Add(new Signal
            {
                Key = "text_detection.error",
                Value = ex.Message,
                Confidence = 0,
                Source = Name,
                Tags = new List<string> { "text", "detection", "error" }
            });
        }

        return signals;
    }
}
