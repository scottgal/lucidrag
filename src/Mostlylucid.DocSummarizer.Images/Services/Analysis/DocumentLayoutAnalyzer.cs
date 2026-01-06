using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Rectangle = SixLabors.ImageSharp.Rectangle;
using Size = OpenCvSharp.Size;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Analyzes document layout and detects segments (text blocks, images, charts, tables)
/// </summary>
public class DocumentLayoutAnalyzer
{
    private readonly ILogger<DocumentLayoutAnalyzer>? _logger;

    public DocumentLayoutAnalyzer(ILogger<DocumentLayoutAnalyzer>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyze document layout and detect segments
    /// </summary>
    public async Task<DocumentLayout> AnalyzeAsync(
        string imagePath,
        CancellationToken ct = default)
    {
        using var image = await Image.LoadAsync<Rgb24>(imagePath, ct);
        return await Task.Run(() => AnalyzeLayout(imagePath, image), ct);
    }

    private DocumentLayout AnalyzeLayout(string imagePath, Image<Rgb24> image)
    {
        // Load image with OpenCV for analysis
        using var mat = Cv2.ImRead(imagePath, ImreadModes.Color);
        using var gray = new Mat();
        Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);

        var layout = new DocumentLayout();

        // Detect segments
        var segments = new List<DocumentSegment>();

        // 1. Detect text blocks using MSER + connected components
        var textBlocks = DetectTextBlocks(mat, gray);
        segments.AddRange(textBlocks);

        // 2. Detect images/charts (regions without text, high variance)
        var imageRegions = DetectImageRegions(mat, gray, textBlocks);
        segments.AddRange(imageRegions);

        // 3. Detect tables (grid patterns)
        var tables = DetectTables(mat, gray);
        segments.AddRange(tables);

        // 4. Analyze color for each segment
        AnalyzeSegmentColors(mat, image, segments);

        // 5. Determine reading order (z-order)
        AssignReadingOrder(segments, image.Width);

        // 6. Classify layout type
        layout.LayoutType = ClassifyLayout(segments, image.Width, image.Height);
        layout.ColumnCount = DetectColumns(segments, image.Width);

        // 7. Calculate overall statistics
        layout.IsColor = segments.Any(s => s.IsColor);
        layout.AverageSaturation = segments.Any() ? segments.Average(s => s.Saturation) : 0;
        layout.Confidence = CalculateLayoutConfidence(segments);
        layout.Segments = segments.OrderBy(s => s.ZOrder).ToList();

        _logger?.LogInformation(
            "Detected {Count} segments: {TextBlocks} text, {Images} images, {Tables} tables. Layout: {Layout}, Color: {IsColor}",
            segments.Count,
            segments.Count(s => s.Type == SegmentType.TextBlock),
            segments.Count(s => s.Type is SegmentType.Image or SegmentType.Chart),
            segments.Count(s => s.Type == SegmentType.Table),
            layout.LayoutType,
            layout.IsColor);

        return layout;
    }

    /// <summary>
    /// Detect text blocks using MSER and connected components
    /// </summary>
    private List<DocumentSegment> DetectTextBlocks(Mat color, Mat gray)
    {
        var segments = new List<DocumentSegment>();

        try
        {
            // Use MSER to find text regions
            using var mser = MSER.Create();
            mser.DetectRegions(gray, out var regions, out var bboxes);

            if (bboxes.Length == 0)
                return segments;

            // Group nearby regions into text blocks
            var textBlocks = GroupTextRegions(bboxes);

            foreach (var block in textBlocks)
            {
                segments.Add(new DocumentSegment
                {
                    Type = SegmentType.TextBlock,
                    BoundingBox = new Rectangle(block.X, block.Y, block.Width, block.Height),
                    Confidence = 0.85
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Text block detection failed");
        }

        return segments;
    }

    /// <summary>
    /// Group nearby text regions into blocks
    /// </summary>
    private List<Rect> GroupTextRegions(Rect[] regions)
    {
        if (regions.Length == 0)
            return new List<Rect>();

        // Sort by Y coordinate
        var sorted = regions.OrderBy(r => r.Y).ToList();
        var blocks = new List<Rect>();

        var currentBlock = sorted[0];

        for (int i = 1; i < sorted.Count; i++)
        {
            var region = sorted[i];

            // If regions are close vertically, merge them
            if (Math.Abs(region.Y - currentBlock.Bottom) < 50)
            {
                // Expand block to include this region
                var x = Math.Min(currentBlock.X, region.X);
                var y = Math.Min(currentBlock.Y, region.Y);
                var right = Math.Max(currentBlock.Right, region.Right);
                var bottom = Math.Max(currentBlock.Bottom, region.Bottom);

                currentBlock = new Rect(x, y, right - x, bottom - y);
            }
            else
            {
                // Start new block
                if (currentBlock.Width > 20 && currentBlock.Height > 20)
                    blocks.Add(currentBlock);

                currentBlock = region;
            }
        }

        // Add last block
        if (currentBlock.Width > 20 && currentBlock.Height > 20)
            blocks.Add(currentBlock);

        return blocks;
    }

    /// <summary>
    /// Detect image/chart regions (high variance, no text)
    /// </summary>
    private List<DocumentSegment> DetectImageRegions(Mat color, Mat gray, List<DocumentSegment> textBlocks)
    {
        var segments = new List<DocumentSegment>();

        try
        {
            // Find contours of non-text regions
            using var edges = new Mat();
            Cv2.Canny(gray, edges, 50, 150);

            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            Cv2.Dilate(edges, edges, kernel);

            Cv2.FindContours(edges, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            foreach (var contour in contours)
            {
                var rect = Cv2.BoundingRect(contour);

                // Filter by size
                if (rect.Width < 100 || rect.Height < 100)
                    continue;

                // Skip if overlaps with text blocks
                var overlapsText = textBlocks.Any(tb =>
                {
                    var tbRect = new Rect(tb.BoundingBox.X, tb.BoundingBox.Y, tb.BoundingBox.Width, tb.BoundingBox.Height);
                    return CalculateIoU(rect, tbRect) > 0.3;
                });

                if (overlapsText)
                    continue;

                // Check if it's a chart (contains some structure)
                var isChart = IsChart(color, rect);

                segments.Add(new DocumentSegment
                {
                    Type = isChart ? SegmentType.Chart : SegmentType.Image,
                    BoundingBox = new Rectangle(rect.X, rect.Y, rect.Width, rect.Height),
                    Confidence = 0.75
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Image region detection failed");
        }

        return segments;
    }

    /// <summary>
    /// Detect tables using horizontal/vertical lines
    /// </summary>
    private List<DocumentSegment> DetectTables(Mat color, Mat gray)
    {
        var segments = new List<DocumentSegment>();

        try
        {
            // Detect horizontal and vertical lines
            using var horizontal = DetectLines(gray, isHorizontal: true);
            using var vertical = DetectLines(gray, isHorizontal: false);

            // Find intersections (table cells)
            using var tableMask = new Mat();
            Cv2.BitwiseAnd(horizontal, vertical, tableMask);

            Cv2.FindContours(tableMask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            foreach (var contour in contours)
            {
                var rect = Cv2.BoundingRect(contour);

                // Filter by size
                if (rect.Width < 100 || rect.Height < 50)
                    continue;

                segments.Add(new DocumentSegment
                {
                    Type = SegmentType.Table,
                    BoundingBox = new Rectangle(rect.X, rect.Y, rect.Width, rect.Height),
                    Confidence = 0.80
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Table detection failed");
        }

        return segments;
    }

    /// <summary>
    /// Detect horizontal or vertical lines
    /// </summary>
    private Mat DetectLines(Mat gray, bool isHorizontal)
    {
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 50, 150);

        // Create line detection kernel
        var kernelLength = isHorizontal ? gray.Width / 30 : gray.Height / 30;
        var kernel = isHorizontal
            ? Cv2.GetStructuringElement(MorphShapes.Rect, new Size(kernelLength, 1))
            : Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, kernelLength));

        var result = new Mat();
        Cv2.MorphologyEx(edges, result, MorphTypes.Open, kernel);

        return result;
    }

    /// <summary>
    /// Analyze color for each segment
    /// </summary>
    private void AnalyzeSegmentColors(Mat mat, Image<Rgb24> image, List<DocumentSegment> segments)
    {
        foreach (var segment in segments)
        {
            try
            {
                // Crop segment region
                var roi = new Rect(
                    segment.BoundingBox.X,
                    segment.BoundingBox.Y,
                    segment.BoundingBox.Width,
                    segment.BoundingBox.Height
                );

                using var segmentMat = new Mat(mat, roi);

                // Convert to HSV for saturation analysis
                using var hsv = new Mat();
                Cv2.CvtColor(segmentMat, hsv, ColorConversionCodes.BGR2HSV);

                // Calculate average saturation
                var channels = Cv2.Split(hsv);
                var saturation = channels[1]; // S channel

                var meanSat = Cv2.Mean(saturation);
                segment.Saturation = meanSat.Val0 / 255.0;

                // Consider grayscale if saturation < 10%
                segment.IsColor = segment.Saturation > 0.1;

                // Get dominant color if color segment
                if (segment.IsColor)
                {
                    var bgr = Cv2.Mean(segmentMat);
                    segment.DominantColor = $"#{(int)bgr.Val2:X2}{(int)bgr.Val1:X2}{(int)bgr.Val0:X2}";
                }

                // Cleanup
                foreach (var channel in channels)
                    channel?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Color analysis failed for segment {Id}", segment.Id);
            }
        }
    }

    /// <summary>
    /// Assign reading order (z-order) to segments
    /// Top-to-bottom, left-to-right, respecting columns
    /// </summary>
    private void AssignReadingOrder(List<DocumentSegment> segments, int imageWidth)
    {
        // Sort by Y (top to bottom), then X (left to right)
        var sorted = segments
            .OrderBy(s => s.BoundingBox.Y)
            .ThenBy(s => s.BoundingBox.X)
            .ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            sorted[i].ZOrder = i;
        }
    }

    /// <summary>
    /// Classify overall layout type
    /// </summary>
    private LayoutType ClassifyLayout(List<DocumentSegment> segments, int width, int height)
    {
        if (segments.Count == 0)
            return LayoutType.Unknown;

        var textCount = segments.Count(s => s.Type == SegmentType.TextBlock);
        var imageCount = segments.Count(s => s.Type is SegmentType.Image or SegmentType.Chart);
        var tableCount = segments.Count(s => s.Type == SegmentType.Table);

        // Determine if multi-column
        var columns = DetectColumns(segments, width);

        if (tableCount > segments.Count * 0.5)
            return LayoutType.TableBased;

        if (imageCount > textCount * 2)
            return LayoutType.ImageHeavy;

        if (columns > 1)
            return LayoutType.MultiColumn;

        if (imageCount > 0 || tableCount > 0)
            return LayoutType.Mixed;

        return LayoutType.SingleColumn;
    }

    /// <summary>
    /// Detect number of columns
    /// </summary>
    private int DetectColumns(List<DocumentSegment> segments, int imageWidth)
    {
        if (segments.Count == 0)
            return 1;

        // Find text blocks
        var textBlocks = segments.Where(s => s.Type == SegmentType.TextBlock).ToList();

        if (textBlocks.Count == 0)
            return 1;

        // Group by X coordinate
        var columnThreshold = imageWidth * 0.4; // 40% of width
        var columns = new List<int>();

        foreach (var block in textBlocks)
        {
            var centerX = block.BoundingBox.X + block.BoundingBox.Width / 2;

            if (!columns.Any(c => Math.Abs(c - centerX) < columnThreshold))
            {
                columns.Add(centerX);
            }
        }

        return Math.Max(1, columns.Count);
    }

    /// <summary>
    /// Calculate IoU (Intersection over Union) for two rectangles
    /// </summary>
    private double CalculateIoU(Rect a, Rect b)
    {
        var intersectX = Math.Max(a.X, b.X);
        var intersectY = Math.Max(a.Y, b.Y);
        var intersectRight = Math.Min(a.Right, b.Right);
        var intersectBottom = Math.Min(a.Bottom, b.Bottom);

        if (intersectRight < intersectX || intersectBottom < intersectY)
            return 0;

        var intersectArea = (intersectRight - intersectX) * (intersectBottom - intersectY);
        var unionArea = a.Width * a.Height + b.Width * b.Height - intersectArea;

        return unionArea > 0 ? (double)intersectArea / unionArea : 0;
    }

    /// <summary>
    /// Check if region is likely a chart
    /// </summary>
    private bool IsChart(Mat color, Rect rect)
    {
        try
        {
            using var roi = new Mat(color, rect);
            using var gray = new Mat();
            Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);

            // Charts have regular patterns and lines
            using var edges = new Mat();
            Cv2.Canny(gray, edges, 50, 150);

            var edgeDensity = Cv2.CountNonZero(edges) / (double)(rect.Width * rect.Height);

            // Charts have moderate edge density (0.1 - 0.3)
            return edgeDensity > 0.1 && edgeDensity < 0.3;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Calculate overall layout confidence
    /// </summary>
    private double CalculateLayoutConfidence(List<DocumentSegment> segments)
    {
        if (segments.Count == 0)
            return 0;

        return segments.Average(s => s.Confidence);
    }
}
