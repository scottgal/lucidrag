using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Enhanced image analysis using OpenCV's advanced capabilities.
/// Provides edge detection, perspective correction, quality metrics, and more.
/// </summary>
public class OpenCvEnhancedAnalyzer
{
    private readonly ILogger<OpenCvEnhancedAnalyzer>? _logger;

    public OpenCvEnhancedAnalyzer(ILogger<OpenCvEnhancedAnalyzer>? logger = null)
    {
        _logger = logger;
    }

    #region Edge Detection & Diagram Analysis

    /// <summary>
    /// Detect if image contains diagrams, charts, or line drawings using edge analysis.
    /// </summary>
    public DiagramAnalysisResult AnalyzeDiagramContent(string imagePath)
    {
        var result = new DiagramAnalysisResult();

        try
        {
            using var mat = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (mat.Empty()) return result;

            using var gray = new Mat();
            using var edges = new Mat();
            using var lines = new Mat();

            Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);

            // Canny edge detection
            Cv2.Canny(gray, edges, 50, 150);

            // Hough line detection for structured diagrams
            var detectedLines = Cv2.HoughLinesP(edges, 1, Math.PI / 180, 50, 30, 10);

            result.LineCount = detectedLines.Length;
            result.HasStructuredLines = detectedLines.Length > 10;

            // Analyze line orientations
            var horizontalLines = 0;
            var verticalLines = 0;
            var diagonalLines = 0;

            foreach (var line in detectedLines)
            {
                var angle = Math.Atan2(line.P2.Y - line.P1.Y, line.P2.X - line.P1.X) * 180 / Math.PI;
                angle = Math.Abs(angle);

                if (angle < 15 || angle > 165)
                    horizontalLines++;
                else if (angle > 75 && angle < 105)
                    verticalLines++;
                else
                    diagonalLines++;
            }

            result.HorizontalLineCount = horizontalLines;
            result.VerticalLineCount = verticalLines;
            result.DiagonalLineCount = diagonalLines;

            // Detect circles (for flowcharts, pie charts)
            var circles = Cv2.HoughCircles(gray, HoughModes.Gradient, 1, 20, 100, 30, 10, 100);
            result.CircleCount = circles?.Length ?? 0;

            // Detect rectangles/squares (for org charts, block diagrams)
            Cv2.FindContours(edges, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            var rectangleCount = 0;
            foreach (var contour in contours)
            {
                var approx = Cv2.ApproxPolyDP(contour, Cv2.ArcLength(contour, true) * 0.02, true);
                if (approx.Length == 4 && Cv2.ContourArea(approx) > 500)
                {
                    rectangleCount++;
                }
            }
            result.RectangleCount = rectangleCount;

            // Classify diagram type
            result.DiagramType = ClassifyDiagramType(result);
            result.IsDiagram = result.DiagramType != "none";
            result.Confidence = CalculateDiagramConfidence(result);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Diagram analysis failed");
        }

        return result;
    }

    private string ClassifyDiagramType(DiagramAnalysisResult result)
    {
        if (result.CircleCount > 3 && result.LineCount < 20)
            return "pie_chart";

        if (result.RectangleCount > 5 && result.LineCount > 10)
            return "flowchart";

        if (result.HorizontalLineCount > 5 && result.VerticalLineCount > 5 && result.RectangleCount > 3)
            return "org_chart";

        if (result.HorizontalLineCount > 10 && result.VerticalLineCount > 3)
            return "bar_chart";

        if (result.DiagonalLineCount > result.HorizontalLineCount + result.VerticalLineCount)
            return "line_graph";

        if (result.LineCount > 20 && result.HasStructuredLines)
            return "technical_drawing";

        if (result.LineCount > 5)
            return "diagram";

        return "none";
    }

    private double CalculateDiagramConfidence(DiagramAnalysisResult result)
    {
        var score = 0.0;

        if (result.LineCount > 10) score += 0.3;
        if (result.RectangleCount > 3) score += 0.2;
        if (result.CircleCount > 0) score += 0.1;
        if (result.HasStructuredLines) score += 0.2;
        if (result.HorizontalLineCount > 5 || result.VerticalLineCount > 5) score += 0.2;

        return Math.Min(1.0, score);
    }

    #endregion

    #region Perspective Correction

    /// <summary>
    /// Detect if image needs perspective correction (tilted document, whiteboard, etc.)
    /// and optionally correct it.
    /// </summary>
    public PerspectiveAnalysisResult AnalyzePerspective(string imagePath, bool autoCorrect = false)
    {
        var result = new PerspectiveAnalysisResult();

        try
        {
            using var mat = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (mat.Empty()) return result;

            using var gray = new Mat();
            using var edges = new Mat();

            Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.GaussianBlur(gray, gray, new Size(5, 5), 0);
            Cv2.Canny(gray, edges, 75, 200);

            // Find contours
            Cv2.FindContours(edges, out var contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);

            // Look for quadrilateral that could be a document
            Point2f[]? documentCorners = null;
            var maxArea = 0.0;

            foreach (var contour in contours)
            {
                var peri = Cv2.ArcLength(contour, true);
                var approx = Cv2.ApproxPolyDP(contour, 0.02 * peri, true);

                if (approx.Length == 4)
                {
                    var area = Cv2.ContourArea(approx);
                    if (area > maxArea && area > mat.Width * mat.Height * 0.1)
                    {
                        maxArea = area;
                        documentCorners = approx.Select(p => new Point2f(p.X, p.Y)).ToArray();
                    }
                }
            }

            if (documentCorners != null)
            {
                result.HasPerspectiveDistortion = true;
                result.DetectedCorners = documentCorners;

                // Calculate skew angle
                var topEdge = documentCorners[1].X - documentCorners[0].X;
                var topHeight = documentCorners[1].Y - documentCorners[0].Y;
                result.SkewAngle = Math.Atan2(topHeight, topEdge) * 180 / Math.PI;

                // Calculate distortion magnitude
                var idealWidth = mat.Width * 0.9;
                var idealHeight = mat.Height * 0.9;
                var actualWidth = Distance(documentCorners[0], documentCorners[1]);
                var actualHeight = Distance(documentCorners[1], documentCorners[2]);

                result.DistortionMagnitude = Math.Abs(actualWidth / actualHeight - idealWidth / idealHeight);

                if (autoCorrect && result.DistortionMagnitude > 0.1)
                {
                    result.CorrectedImagePath = CorrectPerspective(mat, documentCorners, imagePath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Perspective analysis failed");
        }

        return result;
    }

    private string? CorrectPerspective(Mat mat, Point2f[] corners, string originalPath)
    {
        try
        {
            // Order corners: top-left, top-right, bottom-right, bottom-left
            var ordered = OrderCorners(corners);

            // Calculate output dimensions
            var width = (int)Math.Max(Distance(ordered[0], ordered[1]), Distance(ordered[2], ordered[3]));
            var height = (int)Math.Max(Distance(ordered[1], ordered[2]), Distance(ordered[0], ordered[3]));

            var dst = new Point2f[]
            {
                new(0, 0),
                new(width - 1, 0),
                new(width - 1, height - 1),
                new(0, height - 1)
            };

            using var transform = Cv2.GetPerspectiveTransform(ordered, dst);
            using var corrected = new Mat();
            Cv2.WarpPerspective(mat, corrected, transform, new Size(width, height));

            // Save corrected image
            var outputPath = Path.Combine(
                Path.GetDirectoryName(originalPath) ?? "",
                Path.GetFileNameWithoutExtension(originalPath) + "_corrected" + Path.GetExtension(originalPath));

            Cv2.ImWrite(outputPath, corrected);
            return outputPath;
        }
        catch
        {
            return null;
        }
    }

    private Point2f[] OrderCorners(Point2f[] corners)
    {
        // Sort by sum of coordinates (top-left has smallest sum)
        var sorted = corners.OrderBy(p => p.X + p.Y).ToArray();
        var topLeft = sorted[0];
        var bottomRight = sorted[3];

        // Sort by difference (top-right has largest difference)
        sorted = corners.OrderBy(p => p.X - p.Y).ToArray();
        var topRight = sorted[3];
        var bottomLeft = sorted[0];

        return new[] { topLeft, topRight, bottomRight, bottomLeft };
    }

    private double Distance(Point2f p1, Point2f p2)
    {
        return Math.Sqrt(Math.Pow(p2.X - p1.X, 2) + Math.Pow(p2.Y - p1.Y, 2));
    }

    #endregion

    #region Image Quality Analysis

    /// <summary>
    /// Comprehensive image quality analysis using OpenCV.
    /// </summary>
    public ImageQualityResult AnalyzeImageQuality(string imagePath)
    {
        var result = new ImageQualityResult();

        try
        {
            using var mat = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (mat.Empty()) return result;

            using var gray = new Mat();
            Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);

            // Blur detection using Laplacian variance
            using var laplacian = new Mat();
            Cv2.Laplacian(gray, laplacian, MatType.CV_64F);
            Cv2.MeanStdDev(laplacian, out _, out var stddev);
            result.LaplacianVariance = stddev.Val0 * stddev.Val0;
            result.IsBlurry = result.LaplacianVariance < 100;
            result.BlurScore = Math.Min(1.0, result.LaplacianVariance / 500);

            // Noise detection using high-frequency content
            using var highPass = new Mat();
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            Cv2.MorphologyEx(gray, highPass, MorphTypes.Gradient, kernel);
            Cv2.MeanStdDev(highPass, out var noiseMean, out var noiseStd);
            result.NoiseLevel = noiseStd.Val0;
            result.IsNoisy = result.NoiseLevel > 30;

            // Contrast analysis
            Cv2.MinMaxLoc(gray, out double minVal, out double maxVal, out _, out _);
            result.ContrastRange = maxVal - minVal;
            result.IsLowContrast = result.ContrastRange < 100;

            // Brightness analysis
            result.MeanBrightness = Cv2.Mean(gray).Val0;
            result.IsUnderexposed = result.MeanBrightness < 50;
            result.IsOverexposed = result.MeanBrightness > 220;

            // Compression artifact detection (blocking)
            result.BlockingScore = DetectJpegBlocking(gray);
            result.HasCompressionArtifacts = result.BlockingScore > 0.3;

            // Overall quality score
            result.OverallQuality = CalculateOverallQuality(result);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Quality analysis failed");
        }

        return result;
    }

    private double DetectJpegBlocking(Mat gray)
    {
        // Detect 8x8 block boundaries (JPEG compression artifacts)
        var blockScore = 0.0;
        var blockCount = 0;

        for (int y = 8; y < gray.Height - 8; y += 8)
        {
            for (int x = 8; x < gray.Width - 8; x += 8)
            {
                // Check horizontal block boundary
                var diff = Math.Abs(gray.At<byte>(y, x) - gray.At<byte>(y, x - 1));
                var innerDiff = Math.Abs(gray.At<byte>(y, x + 1) - gray.At<byte>(y, x));

                if (diff > innerDiff * 2 && diff > 10)
                {
                    blockScore += diff;
                    blockCount++;
                }
            }
        }

        return blockCount > 0 ? Math.Min(1.0, blockScore / (blockCount * 50)) : 0;
    }

    private double CalculateOverallQuality(ImageQualityResult result)
    {
        var score = 1.0;

        if (result.IsBlurry) score -= 0.3;
        if (result.IsNoisy) score -= 0.2;
        if (result.IsLowContrast) score -= 0.15;
        if (result.IsUnderexposed) score -= 0.2;
        if (result.IsOverexposed) score -= 0.2;
        if (result.HasCompressionArtifacts) score -= 0.15;

        return Math.Max(0, score);
    }

    #endregion

    #region Histogram & Similarity

    /// <summary>
    /// Calculate color histogram for image comparison.
    /// </summary>
    public double[] CalculateColorHistogram(string imagePath, int bins = 32)
    {
        try
        {
            using var mat = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (mat.Empty()) return Array.Empty<double>();

            using var hsv = new Mat();
            Cv2.CvtColor(mat, hsv, ColorConversionCodes.BGR2HSV);

            // Calculate histogram for H and S channels
            var hsvPlanes = Cv2.Split(hsv);
            var hHist = new Mat();
            var sHist = new Mat();

            Cv2.CalcHist(new[] { hsvPlanes[0] }, new[] { 0 }, null, hHist, 1, new[] { bins }, new[] { new Rangef(0, 180) });
            Cv2.CalcHist(new[] { hsvPlanes[1] }, new[] { 0 }, null, sHist, 1, new[] { bins }, new[] { new Rangef(0, 256) });

            Cv2.Normalize(hHist, hHist, 0, 1, NormTypes.MinMax);
            Cv2.Normalize(sHist, sHist, 0, 1, NormTypes.MinMax);

            // Combine into single histogram
            var result = new double[bins * 2];
            for (int i = 0; i < bins; i++)
            {
                result[i] = hHist.At<float>(i);
                result[bins + i] = sHist.At<float>(i);
            }

            foreach (var plane in hsvPlanes) plane.Dispose();
            hHist.Dispose();
            sHist.Dispose();

            return result;
        }
        catch
        {
            return Array.Empty<double>();
        }
    }

    /// <summary>
    /// Compare two images using histogram correlation.
    /// </summary>
    public double CompareHistograms(double[] hist1, double[] hist2)
    {
        if (hist1.Length != hist2.Length || hist1.Length == 0) return 0;

        // Pearson correlation
        var mean1 = hist1.Average();
        var mean2 = hist2.Average();

        var numerator = 0.0;
        var denom1 = 0.0;
        var denom2 = 0.0;

        for (int i = 0; i < hist1.Length; i++)
        {
            var diff1 = hist1[i] - mean1;
            var diff2 = hist2[i] - mean2;
            numerator += diff1 * diff2;
            denom1 += diff1 * diff1;
            denom2 += diff2 * diff2;
        }

        var denom = Math.Sqrt(denom1 * denom2);
        return denom > 0 ? numerator / denom : 0;
    }

    #endregion

    #region Template Matching

    /// <summary>
    /// Find occurrences of a template image within a larger image.
    /// Useful for logo/icon detection.
    /// </summary>
    public List<TemplateMatch> FindTemplate(string imagePath, string templatePath, double threshold = 0.8)
    {
        var matches = new List<TemplateMatch>();

        try
        {
            using var image = Cv2.ImRead(imagePath, ImreadModes.Color);
            using var template = Cv2.ImRead(templatePath, ImreadModes.Color);

            if (image.Empty() || template.Empty()) return matches;

            using var result = new Mat();
            Cv2.MatchTemplate(image, template, result, TemplateMatchModes.CCoeffNormed);

            while (true)
            {
                Cv2.MinMaxLoc(result, out _, out var maxVal, out _, out var maxLoc);

                if (maxVal < threshold) break;

                matches.Add(new TemplateMatch
                {
                    Location = new Rect(maxLoc.X, maxLoc.Y, template.Width, template.Height),
                    Confidence = maxVal
                });

                // Suppress this match to find others
                Cv2.FloodFill(result, maxLoc, new Scalar(0));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Template matching failed");
        }

        return matches;
    }

    #endregion

    #region Scene Detection

    /// <summary>
    /// Detect scene type using color distribution and edge patterns.
    /// </summary>
    public SceneDetectionResult DetectSceneType(string imagePath)
    {
        var result = new SceneDetectionResult();

        try
        {
            using var mat = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (mat.Empty()) return result;

            using var hsv = new Mat();
            using var gray = new Mat();
            Cv2.CvtColor(mat, hsv, ColorConversionCodes.BGR2HSV);
            Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);

            // Analyze color distribution
            var hsvPlanes = Cv2.Split(hsv);
            Cv2.MeanStdDev(hsvPlanes[0], out var hMean, out var hStd);
            Cv2.MeanStdDev(hsvPlanes[1], out var sMean, out var sStd);
            Cv2.MeanStdDev(hsvPlanes[2], out var vMean, out var vStd);

            result.MeanHue = hMean.Val0;
            result.MeanSaturation = sMean.Val0;
            result.MeanValue = vMean.Val0;
            result.HueVariance = hStd.Val0;
            result.SaturationVariance = sStd.Val0;

            // Edge density for indoor/outdoor detection
            using var edges = new Mat();
            Cv2.Canny(gray, edges, 50, 150);
            result.EdgeDensity = Cv2.CountNonZero(edges) / (double)(edges.Width * edges.Height);

            // Green detection (nature)
            using var greenMask = new Mat();
            Cv2.InRange(hsv, new Scalar(35, 40, 40), new Scalar(85, 255, 255), greenMask);
            result.GreenRatio = Cv2.CountNonZero(greenMask) / (double)(mat.Width * mat.Height);

            // Blue detection (sky/water)
            using var blueMask = new Mat();
            Cv2.InRange(hsv, new Scalar(100, 40, 40), new Scalar(130, 255, 255), blueMask);
            result.BlueRatio = Cv2.CountNonZero(blueMask) / (double)(mat.Width * mat.Height);

            // Classify scene
            result.SceneType = ClassifyScene(result);

            foreach (var plane in hsvPlanes) plane.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Scene detection failed");
        }

        return result;
    }

    private string ClassifyScene(SceneDetectionResult result)
    {
        // Nature/outdoor
        if (result.GreenRatio > 0.3)
            return "nature";

        if (result.BlueRatio > 0.3 && result.GreenRatio < 0.1)
            return "sky_water";

        // Indoor
        if (result.EdgeDensity > 0.1 && result.SaturationVariance < 30)
            return "indoor";

        // Document/screenshot
        if (result.MeanSaturation < 30 && result.EdgeDensity > 0.05)
            return "document";

        // Urban
        if (result.EdgeDensity > 0.15 && result.HueVariance > 20)
            return "urban";

        // Low saturation
        if (result.MeanSaturation < 50)
            return "muted";

        return "general";
    }

    #endregion
}

#region Result Classes

public class DiagramAnalysisResult
{
    public bool IsDiagram { get; set; }
    public string DiagramType { get; set; } = "none";
    public double Confidence { get; set; }
    public int LineCount { get; set; }
    public int HorizontalLineCount { get; set; }
    public int VerticalLineCount { get; set; }
    public int DiagonalLineCount { get; set; }
    public int CircleCount { get; set; }
    public int RectangleCount { get; set; }
    public bool HasStructuredLines { get; set; }
}

public class PerspectiveAnalysisResult
{
    public bool HasPerspectiveDistortion { get; set; }
    public double SkewAngle { get; set; }
    public double DistortionMagnitude { get; set; }
    public Point2f[]? DetectedCorners { get; set; }
    public string? CorrectedImagePath { get; set; }
}

public class ImageQualityResult
{
    public double LaplacianVariance { get; set; }
    public double BlurScore { get; set; }
    public bool IsBlurry { get; set; }
    public double NoiseLevel { get; set; }
    public bool IsNoisy { get; set; }
    public double ContrastRange { get; set; }
    public bool IsLowContrast { get; set; }
    public double MeanBrightness { get; set; }
    public bool IsUnderexposed { get; set; }
    public bool IsOverexposed { get; set; }
    public double BlockingScore { get; set; }
    public bool HasCompressionArtifacts { get; set; }
    public double OverallQuality { get; set; }
}

public class TemplateMatch
{
    public Rect Location { get; set; }
    public double Confidence { get; set; }
}

public class SceneDetectionResult
{
    public string SceneType { get; set; } = "unknown";
    public double MeanHue { get; set; }
    public double MeanSaturation { get; set; }
    public double MeanValue { get; set; }
    public double HueVariance { get; set; }
    public double SaturationVariance { get; set; }
    public double EdgeDensity { get; set; }
    public double GreenRatio { get; set; }
    public double BlueRatio { get; set; }
}

#endregion
