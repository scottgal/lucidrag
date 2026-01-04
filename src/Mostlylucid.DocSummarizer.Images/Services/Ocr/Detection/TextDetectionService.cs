using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.Models;

namespace Mostlylucid.DocSummarizer.Images.Services.Ocr.Detection;

/// <summary>
/// Text detection service that identifies text regions in images.
///
/// Detection methods (in order of preference):
/// 1. EAST (ONNX) - Deep learning scene text detector (if model available)
/// 2. CRAFT (ONNX) - Character-level text detector (if model available)
/// 3. Tesseract PSM - Fallback using Tesseract's page segmentation
///
/// Gracefully falls back to simpler methods if ONNX models unavailable.
/// </summary>
public class TextDetectionService
{
    private readonly ILogger<TextDetectionService>? _logger;
    private readonly ModelDownloader _modelDownloader;
    private readonly OcrConfig _config;
    private readonly bool _verbose;

    public TextDetectionService(
        ModelDownloader modelDownloader,
        OcrConfig config,
        ILogger<TextDetectionService>? logger = null)
    {
        _modelDownloader = modelDownloader;
        _config = config;
        _verbose = config.EmitPerformanceMetrics;
        _logger = logger;
    }

    /// <summary>
    /// Detect text regions in an image.
    /// Returns bounding boxes for detected text regions.
    /// </summary>
    public async Task<TextDetectionResult> DetectTextRegionsAsync(
        string imagePath,
        CancellationToken ct = default)
    {
        var detectionMethod = "None";
        var boundingBoxes = new List<BoundingBox>();

        try
        {
            // Try EAST detection if enabled
            if (_config.EnableTextDetection)
            {
                var eastPath = await _modelDownloader.GetModelPathAsync(ModelType.EAST, ct);
                if (eastPath != null)
                {
                    _logger?.LogInformation("Using EAST text detection (ONNX model available)");
                    detectionMethod = "EAST";

                    // TODO: Implement EAST ONNX inference
                    // For now, fall through to Tesseract PSM
                    _logger?.LogWarning("EAST inference not yet implemented, falling back to Tesseract PSM");
                }
            }

            // Try CRAFT detection if enabled and EAST not available
            if (detectionMethod == "None" && _config.EnableTextDetection)
            {
                var craftPath = await _modelDownloader.GetModelPathAsync(ModelType.CRAFT, ct);
                if (craftPath != null)
                {
                    _logger?.LogInformation("Using CRAFT text detection (ONNX model available)");
                    detectionMethod = "CRAFT";

                    // TODO: Implement CRAFT ONNX inference
                    // For now, fall through to Tesseract PSM
                    _logger?.LogWarning("CRAFT inference not yet implemented, falling back to Tesseract PSM");
                }
            }

            // Fallback: Use Tesseract PSM (no external models needed)
            if (detectionMethod == "None")
            {
                _logger?.LogInformation("Using Tesseract PSM for text detection (fallback)");
                detectionMethod = "TesseractPSM";

                // Tesseract PSM doesn't pre-detect regions - OCR finds them during extraction
                // Return empty list to signal "use full image OCR"
                boundingBoxes = new List<BoundingBox>();
            }

            return new TextDetectionResult
            {
                DetectionMethod = detectionMethod,
                BoundingBoxes = boundingBoxes,
                Success = true
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Text detection failed");

            return new TextDetectionResult
            {
                DetectionMethod = "Failed",
                BoundingBoxes = new List<BoundingBox>(),
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Apply non-maximum suppression to merge overlapping bounding boxes.
    /// </summary>
    public List<BoundingBox> ApplyNonMaximumSuppression(
        List<BoundingBox> boxes,
        double iouThreshold)
    {
        if (boxes.Count == 0) return boxes;

        // Sort by confidence (would need confidence scores added to BoundingBox)
        // For now, sort by area (larger boxes first)
        var sorted = boxes.OrderByDescending(b => b.Width * b.Height).ToList();

        var keep = new List<BoundingBox>();

        while (sorted.Count > 0)
        {
            var current = sorted[0];
            keep.Add(current);
            sorted.RemoveAt(0);

            // Remove all boxes that overlap significantly with current
            sorted = sorted.Where(box =>
            {
                var iou = ComputeIoU(current, box);
                return iou < iouThreshold;
            }).ToList();
        }

        _logger?.LogDebug(
            "NMS: {Original} boxes → {Filtered} boxes (IoU threshold={Threshold:F2})",
            boxes.Count, keep.Count, iouThreshold);

        return keep;
    }

    /// <summary>
    /// Compute Intersection over Union (IoU) between two bounding boxes.
    /// </summary>
    private double ComputeIoU(BoundingBox box1, BoundingBox box2)
    {
        var x1 = Math.Max(box1.X1, box2.X1);
        var y1 = Math.Max(box1.Y1, box2.Y1);
        var x2 = Math.Min(box1.X2, box2.X2);
        var y2 = Math.Min(box1.Y2, box2.Y2);

        if (x2 < x1 || y2 < y1) return 0.0;

        var intersectionArea = (x2 - x1) * (y2 - y1);
        var box1Area = box1.Width * box1.Height;
        var box2Area = box2.Width * box2.Height;
        var unionArea = box1Area + box2Area - intersectionArea;

        return unionArea > 0 ? intersectionArea / (double)unionArea : 0.0;
    }
}

/// <summary>
/// Result of text detection operation.
/// </summary>
public record TextDetectionResult
{
    /// <summary>
    /// Detection method used (EAST, CRAFT, TesseractPSM, or Failed).
    /// </summary>
    public required string DetectionMethod { get; init; }

    /// <summary>
    /// Detected text region bounding boxes.
    /// Empty list means "use full image OCR" (Tesseract PSM mode).
    /// </summary>
    public required List<BoundingBox> BoundingBoxes { get; init; }

    /// <summary>
    /// Whether detection succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Error message if detection failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
}
