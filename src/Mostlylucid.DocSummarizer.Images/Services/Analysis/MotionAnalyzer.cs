using Microsoft.Extensions.Logging;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Analyzes motion in animated GIFs using optical flow algorithms.
/// Uses Farneback dense optical flow for comprehensive motion detection.
/// </summary>
public class MotionAnalyzer
{
    private readonly ILogger<MotionAnalyzer>? _logger;

    public MotionAnalyzer(ILogger<MotionAnalyzer>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyze motion in an animated GIF.
    /// </summary>
    /// <param name="imagePath">Path to the GIF file</param>
    /// <param name="maxFrames">Maximum number of frames to analyze (0 = all)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Motion analysis result</returns>
    public async Task<MotionAnalysisResult> AnalyzeAsync(
        string imagePath,
        int maxFrames = 30,
        CancellationToken ct = default)
    {
        var result = new MotionAnalysisResult();

        try
        {
            // Load GIF and extract frames
            using var image = await Image.LoadAsync<Rgba32>(imagePath, ct);

            if (image.Frames.Count < 2)
            {
                _logger?.LogDebug("Image has less than 2 frames, no motion analysis possible");
                result.HasMotion = false;
                result.FrameCount = image.Frames.Count;
                return result;
            }

            result.FrameCount = image.Frames.Count;

            // Extract frames for analysis
            var framesToAnalyze = maxFrames > 0
                ? Math.Min(maxFrames, image.Frames.Count)
                : image.Frames.Count;

            var frames = ExtractFrames(image, framesToAnalyze);

            if (frames.Count < 2)
            {
                result.HasMotion = false;
                return result;
            }

            // Analyze optical flow between consecutive frames
            var flowResults = new List<FrameFlowResult>();

            for (int i = 0; i < frames.Count - 1; i++)
            {
                ct.ThrowIfCancellationRequested();

                var flowResult = AnalyzeFramePair(frames[i], frames[i + 1], i, i + 1);
                flowResults.Add(flowResult);
            }

            // Aggregate results
            result = AggregateResults(flowResults, result);

            // Clean up OpenCV mats
            foreach (var frame in frames)
            {
                frame.Dispose();
            }

            _logger?.LogInformation(
                "Motion analysis complete: {Direction} motion, magnitude={Magnitude:F2}, activity={Activity:P0}",
                result.DominantDirection, result.AverageMagnitude, result.MotionActivity);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Motion analysis failed for {ImagePath}", imagePath);
            result.Error = ex.Message;
        }

        return result;
    }

    private List<Mat> ExtractFrames(Image<Rgba32> image, int maxFrames)
    {
        var frames = new List<Mat>();
        var step = Math.Max(1, image.Frames.Count / maxFrames);

        for (int i = 0; i < image.Frames.Count && frames.Count < maxFrames; i += step)
        {
            var frame = image.Frames.CloneFrame(i);
            var mat = ImageSharpToMat(frame);
            frames.Add(mat);
            frame.Dispose();
        }

        return frames;
    }

    private Mat ImageSharpToMat(Image<Rgba32> image)
    {
        var mat = new Mat(image.Height, image.Width, MatType.CV_8UC3);

        unsafe
        {
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    var matRow = mat.Ptr(y);

                    for (int x = 0; x < accessor.Width; x++)
                    {
                        var pixel = row[x];
                        // OpenCV uses BGR format
                        var ptr = (byte*)matRow + x * 3;
                        ptr[0] = pixel.B;
                        ptr[1] = pixel.G;
                        ptr[2] = pixel.R;
                    }
                }
            });
        }

        return mat;
    }

    private FrameFlowResult AnalyzeFramePair(Mat frame1, Mat frame2, int frameIndex1, int frameIndex2)
    {
        var result = new FrameFlowResult
        {
            FrameIndex1 = frameIndex1,
            FrameIndex2 = frameIndex2
        };

        // Convert to grayscale for optical flow
        using var gray1 = new Mat();
        using var gray2 = new Mat();
        Cv2.CvtColor(frame1, gray1, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(frame2, gray2, ColorConversionCodes.BGR2GRAY);

        // Calculate dense optical flow using Farneback method
        using var flow = new Mat();
        Cv2.CalcOpticalFlowFarneback(
            gray1, gray2, flow,
            pyrScale: 0.5,
            levels: 3,
            winsize: 15,
            iterations: 3,
            polyN: 5,
            polySigma: 1.2,
            flags: 0);

        // Analyze flow vectors
        AnalyzeFlowVectors(flow, result);

        return result;
    }

    private void AnalyzeFlowVectors(Mat flow, FrameFlowResult result)
    {
        var width = flow.Width;
        var height = flow.Height;
        var totalPixels = width * height;

        double sumMagnitude = 0;
        double sumAngle = 0;
        int movingPixels = 0;

        // Direction buckets (8 directions + stationary)
        var directionCounts = new int[9]; // 0-7 = directions, 8 = stationary

        // Motion regions
        var regionSize = 64;
        var regionsX = (width + regionSize - 1) / regionSize;
        var regionsY = (height + regionSize - 1) / regionSize;
        var regionMotion = new double[regionsX, regionsY];

        // Sample flow vectors (every 4th pixel for performance)
        var step = 4;
        var magnitudeThreshold = 1.0; // Minimum magnitude to consider as motion

        unsafe
        {
            for (int y = 0; y < height; y += step)
            {
                var ptr = (float*)flow.Ptr(y);

                for (int x = 0; x < width; x += step)
                {
                    var dx = ptr[x * 2];
                    var dy = ptr[x * 2 + 1];

                    var magnitude = Math.Sqrt(dx * dx + dy * dy);

                    if (magnitude > magnitudeThreshold)
                    {
                        sumMagnitude += magnitude;
                        movingPixels++;

                        // Calculate angle (0-360 degrees)
                        var angle = Math.Atan2(dy, dx) * 180 / Math.PI;
                        if (angle < 0) angle += 360;
                        sumAngle += angle;

                        // Map to 8 directions
                        var dirIndex = (int)((angle + 22.5) / 45) % 8;
                        directionCounts[dirIndex]++;

                        // Track region motion
                        var rx = x / regionSize;
                        var ry = y / regionSize;
                        if (rx < regionsX && ry < regionsY)
                        {
                            regionMotion[rx, ry] += magnitude;
                        }
                    }
                    else
                    {
                        directionCounts[8]++; // Stationary
                    }
                }
            }
        }

        var sampledPixels = (height / step) * (width / step);

        result.AverageMagnitude = movingPixels > 0 ? sumMagnitude / movingPixels : 0;
        result.MaxMagnitude = sumMagnitude > 0 ? result.AverageMagnitude * 2 : 0; // Approximate
        result.MotionActivity = (double)movingPixels / sampledPixels;
        result.AverageAngle = movingPixels > 0 ? sumAngle / movingPixels : 0;

        // Determine dominant direction
        var maxDirCount = 0;
        var dominantDirIndex = 8;
        for (int i = 0; i < 9; i++)
        {
            if (directionCounts[i] > maxDirCount)
            {
                maxDirCount = directionCounts[i];
                dominantDirIndex = i;
            }
        }
        result.DominantDirectionIndex = dominantDirIndex;

        // Find motion regions (regions with significant motion)
        var motionRegions = new List<MotionRegion>();
        for (int ry = 0; ry < regionsY; ry++)
        {
            for (int rx = 0; rx < regionsX; rx++)
            {
                var regionMag = regionMotion[rx, ry];
                if (regionMag > magnitudeThreshold * 10)
                {
                    motionRegions.Add(new MotionRegion
                    {
                        X = rx * regionSize,
                        Y = ry * regionSize,
                        Width = Math.Min(regionSize, width - rx * regionSize),
                        Height = Math.Min(regionSize, height - ry * regionSize),
                        Magnitude = regionMag
                    });
                }
            }
        }
        result.MotionRegions = motionRegions;
    }

    private MotionAnalysisResult AggregateResults(List<FrameFlowResult> flowResults, MotionAnalysisResult result)
    {
        if (flowResults.Count == 0)
        {
            result.HasMotion = false;
            return result;
        }

        // Aggregate statistics
        result.AverageMagnitude = flowResults.Average(f => f.AverageMagnitude);
        result.MaxMagnitude = flowResults.Max(f => f.MaxMagnitude);
        result.MotionActivity = flowResults.Average(f => f.MotionActivity);
        result.HasMotion = result.AverageMagnitude > 0.5 || result.MotionActivity > 0.05;

        // Determine dominant direction across all frames
        var directionVotes = new int[9];
        foreach (var fr in flowResults)
        {
            directionVotes[fr.DominantDirectionIndex]++;
        }

        var maxVotes = 0;
        var dominantIndex = 8;
        for (int i = 0; i < 9; i++)
        {
            if (directionVotes[i] > maxVotes)
            {
                maxVotes = directionVotes[i];
                dominantIndex = i;
            }
        }

        result.DominantDirection = IndexToDirection(dominantIndex);
        result.DominantDirectionConfidence = flowResults.Count > 0
            ? (double)maxVotes / flowResults.Count
            : 0;

        // Calculate direction consistency
        result.DirectionConsistency = CalculateDirectionConsistency(flowResults);

        // Classify motion type
        result.MotionType = ClassifyMotionType(result, flowResults);

        // Aggregate motion regions
        var allRegions = flowResults.SelectMany(f => f.MotionRegions).ToList();
        result.MotionRegions = MergeMotionRegions(allRegions);

        // Calculate temporal consistency
        result.TemporalConsistency = CalculateTemporalConsistency(flowResults);

        return result;
    }

    private string IndexToDirection(int index)
    {
        return index switch
        {
            0 => "right",
            1 => "down-right",
            2 => "down",
            3 => "down-left",
            4 => "left",
            5 => "up-left",
            6 => "up",
            7 => "up-right",
            _ => "stationary"
        };
    }

    private double CalculateDirectionConsistency(List<FrameFlowResult> flowResults)
    {
        if (flowResults.Count < 2) return 1.0;

        var angles = flowResults.Select(f => f.AverageAngle).ToList();
        var meanAngle = angles.Average();

        // Calculate circular standard deviation
        var variance = angles.Sum(a =>
        {
            var diff = a - meanAngle;
            // Handle wraparound
            if (diff > 180) diff -= 360;
            if (diff < -180) diff += 360;
            return diff * diff;
        }) / angles.Count;

        var stdDev = Math.Sqrt(variance);

        // Convert to consistency score (0-1, higher = more consistent)
        return Math.Max(0, 1 - stdDev / 180);
    }

    private string ClassifyMotionType(MotionAnalysisResult result, List<FrameFlowResult> flowResults)
    {
        if (!result.HasMotion)
            return "static";

        // Check for oscillating motion (back-and-forth)
        if (result.DirectionConsistency < 0.3)
            return "oscillating";

        // Check for radial motion (zooming in/out)
        var avgActivity = result.MotionActivity;
        if (avgActivity > 0.7 && result.DirectionConsistency < 0.5)
            return "radial";

        // Check for rotational motion
        if (result.DirectionConsistency < 0.5 && avgActivity > 0.3)
            return "rotating";

        // Check for panning
        if (result.DirectionConsistency > 0.7 && avgActivity > 0.5)
            return "panning";

        // Check for localized motion (object moving)
        if (avgActivity < 0.3 && result.AverageMagnitude > 2)
            return "object_motion";

        // Default to general motion
        return "general";
    }

    private List<MotionRegion> MergeMotionRegions(List<MotionRegion> regions)
    {
        if (regions.Count == 0) return new List<MotionRegion>();

        // Group overlapping regions
        var merged = new List<MotionRegion>();
        var used = new bool[regions.Count];

        for (int i = 0; i < regions.Count; i++)
        {
            if (used[i]) continue;

            var current = regions[i];
            var totalMag = current.Magnitude;
            var count = 1;

            for (int j = i + 1; j < regions.Count; j++)
            {
                if (used[j]) continue;

                var other = regions[j];
                if (RegionsOverlap(current, other))
                {
                    // Expand current region
                    var minX = Math.Min(current.X, other.X);
                    var minY = Math.Min(current.Y, other.Y);
                    var maxX = Math.Max(current.X + current.Width, other.X + other.Width);
                    var maxY = Math.Max(current.Y + current.Height, other.Y + other.Height);

                    current = new MotionRegion
                    {
                        X = minX,
                        Y = minY,
                        Width = maxX - minX,
                        Height = maxY - minY,
                        Magnitude = current.Magnitude + other.Magnitude
                    };

                    totalMag += other.Magnitude;
                    count++;
                    used[j] = true;
                }
            }

            merged.Add(new MotionRegion
            {
                X = current.X,
                Y = current.Y,
                Width = current.Width,
                Height = current.Height,
                Magnitude = totalMag / count // Average magnitude
            });
        }

        // Return top 5 regions by magnitude
        return merged.OrderByDescending(r => r.Magnitude).Take(5).ToList();
    }

    private bool RegionsOverlap(MotionRegion a, MotionRegion b)
    {
        return a.X < b.X + b.Width &&
               a.X + a.Width > b.X &&
               a.Y < b.Y + b.Height &&
               a.Y + a.Height > b.Y;
    }

    private double CalculateTemporalConsistency(List<FrameFlowResult> flowResults)
    {
        if (flowResults.Count < 2) return 1.0;

        // Calculate variance in magnitude over time
        var magnitudes = flowResults.Select(f => f.AverageMagnitude).ToList();
        var mean = magnitudes.Average();
        if (mean < 0.01) return 1.0; // No motion = perfectly consistent

        var variance = magnitudes.Sum(m => (m - mean) * (m - mean)) / magnitudes.Count;
        var coeffOfVariation = Math.Sqrt(variance) / mean;

        // Convert to consistency score (0-1, higher = more consistent)
        return Math.Max(0, 1 - coeffOfVariation);
    }
}

/// <summary>
/// Result of motion analysis for an animated image.
/// </summary>
public class MotionAnalysisResult
{
    /// <summary>
    /// Whether significant motion was detected.
    /// </summary>
    public bool HasMotion { get; set; }

    /// <summary>
    /// Number of frames in the animation.
    /// </summary>
    public int FrameCount { get; set; }

    /// <summary>
    /// Average motion magnitude in pixels per frame.
    /// </summary>
    public double AverageMagnitude { get; set; }

    /// <summary>
    /// Maximum motion magnitude detected.
    /// </summary>
    public double MaxMagnitude { get; set; }

    /// <summary>
    /// Fraction of image with motion (0-1).
    /// </summary>
    public double MotionActivity { get; set; }

    /// <summary>
    /// Dominant direction of motion: right, down-right, down, down-left, left, up-left, up, up-right, stationary.
    /// </summary>
    public string DominantDirection { get; set; } = "stationary";

    /// <summary>
    /// Confidence in the dominant direction (0-1).
    /// </summary>
    public double DominantDirectionConfidence { get; set; }

    /// <summary>
    /// How consistent the motion direction is across frames (0-1).
    /// </summary>
    public double DirectionConsistency { get; set; }

    /// <summary>
    /// How consistent the motion magnitude is over time (0-1).
    /// </summary>
    public double TemporalConsistency { get; set; }

    /// <summary>
    /// Type of motion detected: static, panning, zooming, rotating, oscillating, object_motion, general.
    /// </summary>
    public string MotionType { get; set; } = "static";

    /// <summary>
    /// Regions of the image with significant motion.
    /// </summary>
    public List<MotionRegion> MotionRegions { get; set; } = new();

    /// <summary>
    /// Error message if analysis failed.
    /// </summary>
    public string? Error { get; set; }
}

/// <summary>
/// A region of the image with detected motion.
/// </summary>
public class MotionRegion
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Magnitude { get; set; }
}

/// <summary>
/// Optical flow result for a single frame pair.
/// </summary>
internal class FrameFlowResult
{
    public int FrameIndex1 { get; set; }
    public int FrameIndex2 { get; set; }
    public double AverageMagnitude { get; set; }
    public double MaxMagnitude { get; set; }
    public double MotionActivity { get; set; }
    public double AverageAngle { get; set; }
    public int DominantDirectionIndex { get; set; }
    public List<MotionRegion> MotionRegions { get; set; } = new();
}
