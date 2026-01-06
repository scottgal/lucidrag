using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

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
public class TextDetectionService : ITextDetectionService
{
    private readonly ILogger<TextDetectionService>? _logger;
    private readonly ModelDownloader _modelDownloader;
    private readonly OcrConfig _config;
    private readonly bool _verbose;

    // EAST model constants
    private const int EastInputSize = 320; // Must be multiple of 32
    private const float EastScoreThreshold = 0.5f;
    private const float EastNmsThreshold = 0.4f;

    // Cached ONNX sessions
    private static InferenceSession? _eastSession;
    private static readonly object _modelLock = new();

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
                    try
                    {
                        _logger?.LogInformation("Using EAST text detection (ONNX model available)");
                        boundingBoxes = await RunEastDetectionAsync(imagePath, eastPath, ct);
                        detectionMethod = "EAST";
                        _logger?.LogInformation("EAST detected {Count} text regions", boundingBoxes.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "EAST inference failed, falling back to Tesseract PSM");
                        detectionMethod = "None";
                    }
                }
            }

            // Try CRAFT detection if enabled and EAST not available/failed
            if (detectionMethod == "None" && _config.EnableTextDetection)
            {
                var craftPath = await _modelDownloader.GetModelPathAsync(ModelType.CRAFT, ct);
                if (craftPath != null)
                {
                    try
                    {
                        _logger?.LogInformation("Using CRAFT text detection (ONNX model available)");
                        boundingBoxes = await RunCraftDetectionAsync(imagePath, craftPath, ct);
                        detectionMethod = "CRAFT";
                        _logger?.LogInformation("CRAFT detected {Count} text regions", boundingBoxes.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "CRAFT inference failed, falling back to Tesseract PSM");
                        detectionMethod = "None";
                    }
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

    #region EAST Detection

    /// <summary>
    /// Run EAST (Efficient and Accurate Scene Text) detection on an image.
    /// EAST outputs score map and geometry for rotated bounding boxes.
    /// </summary>
    private async Task<List<BoundingBox>> RunEastDetectionAsync(
        string imagePath,
        string modelPath,
        CancellationToken ct)
    {
        var session = GetOrLoadEastSession(modelPath);
        if (session == null)
            throw new InvalidOperationException("Failed to load EAST model");

        // Load and preprocess image
        using var image = await Image.LoadAsync<Rgb24>(imagePath, ct);
        var originalWidth = image.Width;
        var originalHeight = image.Height;

        // EAST requires input size to be multiple of 32
        var inputWidth = ((originalWidth + 31) / 32) * 32;
        var inputHeight = ((originalHeight + 31) / 32) * 32;
        inputWidth = Math.Min(inputWidth, EastInputSize);
        inputHeight = Math.Min(inputHeight, EastInputSize);

        // Resize while maintaining aspect ratio
        image.Mutate(x => x.Resize(inputWidth, inputHeight));

        // Create input tensor (NCHW format, BGR, mean subtraction)
        var tensor = PreprocessForEast(image);

        // Run inference
        var inputName = session.InputNames[0];
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, tensor)
        };

        using var results = session.Run(inputs);
        var outputs = results.ToList();

        // EAST outputs: scores (confidence) and geometry (box coordinates)
        // Standard EAST model has 2 outputs: feature_fusion/Conv_7/Sigmoid and feature_fusion/concat_3
        var scores = outputs[0].AsTensor<float>();
        var geometry = outputs[1].AsTensor<float>();

        // Decode boxes from EAST output
        var boxes = DecodeEastBoxes(scores, geometry, originalWidth, originalHeight, inputWidth, inputHeight);

        // Apply NMS
        return ApplyNonMaximumSuppression(boxes, EastNmsThreshold);
    }

    private InferenceSession? GetOrLoadEastSession(string modelPath)
    {
        if (_eastSession != null) return _eastSession;

        lock (_modelLock)
        {
            if (_eastSession != null) return _eastSession;

            try
            {
                _logger?.LogInformation("Loading EAST model from {Path}", modelPath);
                var sessionOptions = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                };
                _eastSession = new InferenceSession(modelPath, sessionOptions);
                _logger?.LogInformation("EAST model loaded successfully");
                return _eastSession;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load EAST model");
                return null;
            }
        }
    }

    private DenseTensor<float> PreprocessForEast(Image<Rgb24> image)
    {
        var width = image.Width;
        var height = image.Height;
        var tensor = new DenseTensor<float>(new[] { 1, 3, height, width });

        // EAST uses BGR format with mean subtraction [123.68, 116.78, 103.94]
        var meanR = 123.68f;
        var meanG = 116.78f;
        var meanB = 103.94f;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var pixel = row[x];
                    // BGR order with mean subtraction
                    tensor[0, 0, y, x] = pixel.B - meanB;  // B channel
                    tensor[0, 1, y, x] = pixel.G - meanG;  // G channel
                    tensor[0, 2, y, x] = pixel.R - meanR;  // R channel
                }
            }
        });

        return tensor;
    }

    private List<BoundingBox> DecodeEastBoxes(
        Tensor<float> scores,
        Tensor<float> geometry,
        int originalWidth,
        int originalHeight,
        int inputWidth,
        int inputHeight)
    {
        var boxes = new List<BoundingBox>();

        // EAST output is downsampled by 4
        var scoreHeight = scores.Dimensions[2];
        var scoreWidth = scores.Dimensions[3];
        var scaleX = (float)originalWidth / inputWidth;
        var scaleY = (float)originalHeight / inputHeight;

        for (var y = 0; y < scoreHeight; y++)
        {
            for (var x = 0; x < scoreWidth; x++)
            {
                var score = scores[0, 0, y, x];
                if (score < EastScoreThreshold) continue;

                // Geometry: [top, right, bottom, left, angle]
                var top = geometry[0, 0, y, x];
                var right = geometry[0, 1, y, x];
                var bottom = geometry[0, 2, y, x];
                var left = geometry[0, 3, y, x];
                // var angle = geometry[0, 4, y, x]; // For rotated boxes

                // Convert to image coordinates
                var offsetX = x * 4.0f; // EAST uses stride 4
                var offsetY = y * 4.0f;

                var x1 = (int)((offsetX - left) * scaleX);
                var y1 = (int)((offsetY - top) * scaleY);
                var x2 = (int)((offsetX + right) * scaleX);
                var y2 = (int)((offsetY + bottom) * scaleY);

                // Clamp to image bounds
                x1 = Math.Max(0, Math.Min(x1, originalWidth - 1));
                y1 = Math.Max(0, Math.Min(y1, originalHeight - 1));
                x2 = Math.Max(0, Math.Min(x2, originalWidth - 1));
                y2 = Math.Max(0, Math.Min(y2, originalHeight - 1));

                if (x2 > x1 && y2 > y1)
                {
                    boxes.Add(new BoundingBox
                    {
                        X1 = x1,
                        Y1 = y1,
                        X2 = x2,
                        Y2 = y2,
                        Confidence = score
                    });
                }
            }
        }

        _logger?.LogDebug("EAST decoded {Count} candidate boxes before NMS", boxes.Count);
        return boxes;
    }

    #endregion

    #region CRAFT Detection

    /// <summary>
    /// Run CRAFT (Character Region Awareness for Text) detection on an image.
    /// CRAFT excels at detecting curved and rotated text by finding character-level regions.
    /// </summary>
    private async Task<List<BoundingBox>> RunCraftDetectionAsync(
        string imagePath,
        string modelPath,
        CancellationToken ct)
    {
        // Load CRAFT model (reuse session infrastructure)
        var session = GetOrLoadCraftSession(modelPath);
        if (session == null)
            throw new InvalidOperationException("Failed to load CRAFT model");

        // Load and preprocess image
        using var image = await Image.LoadAsync<Rgb24>(imagePath, ct);
        var originalWidth = image.Width;
        var originalHeight = image.Height;

        // CRAFT typically uses 1280 max dimension
        const int maxDim = 1280;
        var scale = Math.Min((float)maxDim / originalWidth, (float)maxDim / originalHeight);
        scale = Math.Min(scale, 1.0f); // Don't upscale

        var inputWidth = ((int)(originalWidth * scale) + 31) / 32 * 32;
        var inputHeight = ((int)(originalHeight * scale) + 31) / 32 * 32;

        image.Mutate(x => x.Resize(inputWidth, inputHeight));

        // Create input tensor (NCHW format, normalized)
        var tensor = PreprocessForCraft(image);

        // Run inference
        var inputName = session.InputNames[0];
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, tensor)
        };

        using var results = session.Run(inputs);
        var outputs = results.ToList();

        // CRAFT outputs: text region score map and affinity score map
        var textScore = outputs[0].AsTensor<float>();

        // Decode boxes from CRAFT output
        var boxes = DecodeCraftBoxes(textScore, originalWidth, originalHeight, inputWidth, inputHeight);

        // Apply NMS
        return ApplyNonMaximumSuppression(boxes, EastNmsThreshold);
    }

    private static InferenceSession? _craftSession;

    private InferenceSession? GetOrLoadCraftSession(string modelPath)
    {
        if (_craftSession != null) return _craftSession;

        lock (_modelLock)
        {
            if (_craftSession != null) return _craftSession;

            try
            {
                _logger?.LogInformation("Loading CRAFT model from {Path}", modelPath);
                var sessionOptions = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                };
                _craftSession = new InferenceSession(modelPath, sessionOptions);
                _logger?.LogInformation("CRAFT model loaded successfully");
                return _craftSession;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load CRAFT model");
                return null;
            }
        }
    }

    private DenseTensor<float> PreprocessForCraft(Image<Rgb24> image)
    {
        var width = image.Width;
        var height = image.Height;
        var tensor = new DenseTensor<float>(new[] { 1, 3, height, width });

        // CRAFT uses RGB normalized to [0, 1] then ImageNet normalization
        var meanR = 0.485f;
        var meanG = 0.456f;
        var meanB = 0.406f;
        var stdR = 0.229f;
        var stdG = 0.224f;
        var stdB = 0.225f;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var pixel = row[x];
                    // RGB order, normalized with ImageNet stats
                    tensor[0, 0, y, x] = (pixel.R / 255f - meanR) / stdR;
                    tensor[0, 1, y, x] = (pixel.G / 255f - meanG) / stdG;
                    tensor[0, 2, y, x] = (pixel.B / 255f - meanB) / stdB;
                }
            }
        });

        return tensor;
    }

    private List<BoundingBox> DecodeCraftBoxes(
        Tensor<float> textScore,
        int originalWidth,
        int originalHeight,
        int inputWidth,
        int inputHeight)
    {
        var boxes = new List<BoundingBox>();

        // CRAFT output is downsampled by 2
        var scoreHeight = textScore.Dimensions[2];
        var scoreWidth = textScore.Dimensions[3];
        var scaleX = (float)originalWidth / inputWidth * 2;
        var scaleY = (float)originalHeight / inputHeight * 2;

        // Binary threshold the score map to find text regions
        const float threshold = 0.4f;
        var visited = new bool[scoreHeight, scoreWidth];

        for (var y = 0; y < scoreHeight; y++)
        {
            for (var x = 0; x < scoreWidth; x++)
            {
                if (visited[y, x]) continue;

                var score = textScore[0, 0, y, x];
                if (score < threshold) continue;

                // Find connected component (simple flood fill)
                var (minX, minY, maxX, maxY, avgScore) = FloodFillRegion(
                    textScore, visited, x, y, scoreWidth, scoreHeight, threshold);

                // Convert to image coordinates
                var x1 = (int)(minX * scaleX);
                var y1 = (int)(minY * scaleY);
                var x2 = (int)(maxX * scaleX);
                var y2 = (int)(maxY * scaleY);

                // Clamp and add padding
                const int padding = 5;
                x1 = Math.Max(0, x1 - padding);
                y1 = Math.Max(0, y1 - padding);
                x2 = Math.Min(originalWidth - 1, x2 + padding);
                y2 = Math.Min(originalHeight - 1, y2 + padding);

                if (x2 > x1 + 10 && y2 > y1 + 5) // Min size filter
                {
                    boxes.Add(new BoundingBox
                    {
                        X1 = x1,
                        Y1 = y1,
                        X2 = x2,
                        Y2 = y2,
                        Confidence = avgScore
                    });
                }
            }
        }

        _logger?.LogDebug("CRAFT decoded {Count} text regions before NMS", boxes.Count);
        return boxes;
    }

    private (int minX, int minY, int maxX, int maxY, float avgScore) FloodFillRegion(
        Tensor<float> scores,
        bool[,] visited,
        int startX,
        int startY,
        int width,
        int height,
        float threshold)
    {
        var stack = new Stack<(int x, int y)>();
        stack.Push((startX, startY));

        var minX = startX;
        var minY = startY;
        var maxX = startX;
        var maxY = startY;
        var totalScore = 0f;
        var count = 0;

        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();

            if (x < 0 || x >= width || y < 0 || y >= height) continue;
            if (visited[y, x]) continue;

            var score = scores[0, 0, y, x];
            if (score < threshold) continue;

            visited[y, x] = true;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            totalScore += score;
            count++;

            // 4-connected neighbors
            stack.Push((x + 1, y));
            stack.Push((x - 1, y));
            stack.Push((x, y + 1));
            stack.Push((x, y - 1));
        }

        return (minX, minY, maxX, maxY, count > 0 ? totalScore / count : 0);
    }

    #endregion
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
