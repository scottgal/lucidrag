using Microsoft.Extensions.Logging;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Fast OpenCV-based text detection using MSER (Maximally Stable Extremal Regions).
/// Much faster than ML-based detection (~5-20ms vs 1-2s for Florence-2).
/// Used as a pre-filter to quickly determine if an image likely contains text.
/// </summary>
public class OpenCvTextDetector
{
    private readonly ILogger<OpenCvTextDetector>? _logger;

    public OpenCvTextDetector(ILogger<OpenCvTextDetector>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Result of text detection.
    /// </summary>
    public class TextDetectionResult
    {
        public bool HasTextLikeRegions { get; set; }
        public int TextRegionCount { get; set; }
        public double TextAreaRatio { get; set; } // Fraction of image that appears to be text
        public List<Rect> TextBoundingBoxes { get; set; } = new();
        public double Confidence { get; set; }
        public long DetectionTimeMs { get; set; }
    }

    /// <summary>
    /// Quickly detects if an image contains text-like regions using MSER.
    /// This is ~100x faster than ML-based detection.
    /// </summary>
    public TextDetectionResult DetectTextRegions(string imagePath)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var image = Cv2.ImRead(imagePath);
            return DetectTextRegionsFromMat(image, sw);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "OpenCV text detection failed for {Path}", imagePath);
            return new TextDetectionResult { HasTextLikeRegions = false, DetectionTimeMs = sw.ElapsedMilliseconds };
        }
    }

    /// <summary>
    /// Detects text regions from an ImageSharp image.
    /// </summary>
    public TextDetectionResult DetectTextRegions(Image<Rgba32> image)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var mat = ImageSharpToMat(image);
            return DetectTextRegionsFromMat(mat, sw);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "OpenCV text detection failed");
            return new TextDetectionResult { HasTextLikeRegions = false, DetectionTimeMs = sw.ElapsedMilliseconds };
        }
    }

    private TextDetectionResult DetectTextRegionsFromMat(Mat image, System.Diagnostics.Stopwatch sw)
    {
        var result = new TextDetectionResult();

        // Convert to grayscale
        using var gray = new Mat();
        if (image.Channels() > 1)
            Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        else
            image.CopyTo(gray);

        // Use MSER to detect text-like regions
        using var mser = MSER.Create(
            delta: 5,
            minArea: 60,
            maxArea: 14400,
            maxVariation: 0.25,
            minDiversity: 0.2,
            maxEvolution: 200,
            areaThreshold: 1.01,
            minMargin: 0.003,
            edgeBlurSize: 5);

        mser.DetectRegions(gray, out var msers, out var bboxes);

        // Filter bounding boxes to find text-like regions
        var textBoxes = new List<Rect>();
        var totalImageArea = image.Width * image.Height;
        var textArea = 0.0;

        foreach (var bbox in bboxes)
        {
            // Text-like regions have specific aspect ratios and sizes
            var aspectRatio = (double)bbox.Width / Math.Max(1, bbox.Height);
            var area = bbox.Width * bbox.Height;
            var areaRatio = (double)area / totalImageArea;

            // Heuristics for text-like regions:
            // - Aspect ratio between 0.1 and 10 (not too extreme)
            // - Area between 0.01% and 5% of image (reasonable text size)
            // - Not too small (min 20px wide)
            if (aspectRatio >= 0.1 && aspectRatio <= 10 &&
                areaRatio >= 0.0001 && areaRatio <= 0.05 &&
                bbox.Width >= 20)
            {
                textBoxes.Add(bbox);
                textArea += area;
            }
        }

        // Merge overlapping boxes
        textBoxes = MergeOverlappingBoxes(textBoxes);

        result.TextBoundingBoxes = textBoxes;
        result.TextRegionCount = textBoxes.Count;
        result.TextAreaRatio = textArea / totalImageArea;
        result.HasTextLikeRegions = textBoxes.Count >= 2 || result.TextAreaRatio > 0.01;

        // Calculate confidence based on region count and area
        result.Confidence = Math.Min(1.0,
            (textBoxes.Count * 0.1) + (result.TextAreaRatio * 5));

        sw.Stop();
        result.DetectionTimeMs = sw.ElapsedMilliseconds;

        _logger?.LogDebug(
            "OpenCV text detection: {RegionCount} regions, {AreaRatio:P2} area, {Confidence:F2} confidence in {Time}ms",
            result.TextRegionCount, result.TextAreaRatio, result.Confidence, result.DetectionTimeMs);

        return result;
    }

    /// <summary>
    /// Quick subtitle region detection - checks bottom 30% of image for bright text.
    /// Even faster than full MSER (~2-5ms).
    /// </summary>
    public bool HasSubtitleRegion(string imagePath)
    {
        try
        {
            using var image = Cv2.ImRead(imagePath);
            return HasSubtitleRegionInternal(image);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Quick subtitle region detection for ImageSharp images.
    /// </summary>
    public bool HasSubtitleRegion(Image<Rgba32> image)
    {
        try
        {
            using var mat = ImageSharpToMat(image);
            return HasSubtitleRegionInternal(mat);
        }
        catch
        {
            return false;
        }
    }

    private bool HasSubtitleRegionInternal(Mat image)
    {
        // Focus on bottom 30% where subtitles typically appear
        var subtitleY = (int)(image.Height * 0.7);
        var subtitleHeight = image.Height - subtitleY;

        using var subtitleRegion = new Mat(image, new Rect(0, subtitleY, image.Width, subtitleHeight));
        using var gray = new Mat();

        if (subtitleRegion.Channels() > 1)
            Cv2.CvtColor(subtitleRegion, gray, ColorConversionCodes.BGR2GRAY);
        else
            subtitleRegion.CopyTo(gray);

        // Threshold to find bright pixels (subtitles are usually white/yellow)
        using var thresh = new Mat();
        Cv2.Threshold(gray, thresh, 200, 255, ThresholdTypes.Binary);

        // Count bright pixels
        var brightPixels = Cv2.CountNonZero(thresh);
        var totalPixels = gray.Width * gray.Height;
        var brightRatio = (double)brightPixels / totalPixels;

        // If more than 1% but less than 30% is bright, likely has subtitles
        var hasSubtitles = brightRatio > 0.01 && brightRatio < 0.30;

        _logger?.LogTrace("Subtitle detection: {BrightRatio:P2} bright pixels, hasSubtitles={HasSubtitles}",
            brightRatio, hasSubtitles);

        return hasSubtitles;
    }

    /// <summary>
    /// Merges overlapping bounding boxes.
    /// </summary>
    private List<Rect> MergeOverlappingBoxes(List<Rect> boxes)
    {
        if (boxes.Count <= 1) return boxes;

        var merged = new List<Rect>();
        var used = new bool[boxes.Count];

        for (int i = 0; i < boxes.Count; i++)
        {
            if (used[i]) continue;

            var current = boxes[i];

            for (int j = i + 1; j < boxes.Count; j++)
            {
                if (used[j]) continue;

                // Check for overlap
                if (BoxesOverlap(current, boxes[j]))
                {
                    current = MergeBoxes(current, boxes[j]);
                    used[j] = true;
                }
            }

            merged.Add(current);
        }

        return merged;
    }

    private bool BoxesOverlap(Rect a, Rect b)
    {
        // Add some padding for close boxes
        const int padding = 10;
        return !(a.Right + padding < b.Left ||
                 b.Right + padding < a.Left ||
                 a.Bottom + padding < b.Top ||
                 b.Bottom + padding < a.Top);
    }

    private Rect MergeBoxes(Rect a, Rect b)
    {
        var left = Math.Min(a.Left, b.Left);
        var top = Math.Min(a.Top, b.Top);
        var right = Math.Max(a.Right, b.Right);
        var bottom = Math.Max(a.Bottom, b.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Converts ImageSharp image to OpenCV Mat.
    /// </summary>
    private Mat ImageSharpToMat(Image<Rgba32> image)
    {
        var mat = new Mat(image.Height, image.Width, MatType.CV_8UC4);

        image.ProcessPixelRows(accessor =>
        {
            unsafe
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    var matPtr = (byte*)mat.Ptr(y).ToPointer();

                    for (int x = 0; x < row.Length; x++)
                    {
                        var pixel = row[x];
                        matPtr[x * 4 + 0] = pixel.B;
                        matPtr[x * 4 + 1] = pixel.G;
                        matPtr[x * 4 + 2] = pixel.R;
                        matPtr[x * 4 + 3] = pixel.A;
                    }
                }
            }
        });

        return mat;
    }
}
