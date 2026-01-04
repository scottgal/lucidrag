using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Analyzes motion in animated GIFs using OpenCV optical flow.
///
/// Uses Farneback dense optical flow to compute motion vectors for every pixel
/// between consecutive frames, then aggregates to determine:
/// - Dominant motion direction (left, right, up, down, radial, static)
/// - Motion magnitude (pixels per frame)
/// - Regions with significant motion
/// </summary>
public class GifMotionAnalyzer : IDisposable
{
    private readonly ILogger<GifMotionAnalyzer>? _logger;
    private readonly double _motionThreshold;
    private readonly int _maxFramesToAnalyze;
    private readonly bool _enableDetailedFrameData;
    private bool _disposed;

    public GifMotionAnalyzer(
        ILogger<GifMotionAnalyzer>? logger = null,
        double motionThreshold = 2.0,
        int maxFramesToAnalyze = 50,
        bool enableDetailedFrameData = false)
    {
        _logger = logger;
        _motionThreshold = motionThreshold;
        _maxFramesToAnalyze = maxFramesToAnalyze;
        _enableDetailedFrameData = enableDetailedFrameData;
    }

    /// <summary>
    /// Analyze motion in an animated GIF.
    /// </summary>
    public async Task<GifMotionProfile> AnalyzeAsync(
        string gifPath,
        CancellationToken ct = default)
    {
        _logger?.LogInformation("Analyzing motion in GIF: {Path}", gifPath);

        try
        {
            // Load GIF and extract frames
            using var gifImage = await SixLabors.ImageSharp.Image.LoadAsync<Rgba32>(gifPath, ct);

            var metadata = gifImage.Metadata;
            var gifMetadata = metadata.GetGifMetadata();

            var frameCount = gifImage.Frames.Count;
            var frameDelay = gifMetadata.RepeatCount > 0 ?
                (gifImage.Frames.RootFrame.Metadata.GetGifMetadata()?.FrameDelay ?? 10) * 10 :
                100; // Default 100ms if not specified

            _logger?.LogDebug("GIF has {FrameCount} frames, delay: {Delay}ms", frameCount, frameDelay);

            // If only 1 frame or static, return early
            if (frameCount <= 1)
            {
                // Still run complexity analysis for static images
                GifComplexityProfile? complexity = null;
                try
                {
                    var complexityAnalyzer = new GifComplexityAnalyzer(_logger as ILogger<GifComplexityAnalyzer>);
                    complexity = await complexityAnalyzer.AnalyzeAsync(gifPath, ct);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Complexity analysis failed for static image {Path}", gifPath);
                }

                return new GifMotionProfile
                {
                    FrameCount = frameCount,
                    FrameDelayMs = frameDelay,
                    TotalDurationMs = frameDelay * frameCount,
                    Loops = gifMetadata.RepeatCount != 1,
                    MotionDirection = "static",
                    MotionMagnitude = 0,
                    MaxMotionMagnitude = 0,
                    MotionPercentage = 0,
                    Confidence = 1.0,
                    Complexity = complexity
                };
            }

            // Extract frames for analysis (limit to avoid memory issues)
            var framesToAnalyze = Math.Min(frameCount, _maxFramesToAnalyze);
            var frameStep = frameCount > _maxFramesToAnalyze ? frameCount / _maxFramesToAnalyze : 1;

            var frames = new List<Mat>();
            try
            {
                for (int i = 0; i < frameCount; i += frameStep)
                {
                    if (frames.Count >= framesToAnalyze) break;

                    var frame = gifImage.Frames[i];
                    var mat = ConvertToGrayscaleMat(frame);
                    frames.Add(mat);

                    if (ct.IsCancellationRequested)
                        break;
                }

                // Compute optical flow between consecutive frames
                var motionData = ComputeOpticalFlow(frames, ct);

                // Analyze motion patterns
                var profile = AnalyzeMotionPatterns(motionData, frameCount, frameDelay, gifMetadata.RepeatCount != 1);

                // Compute complexity metrics (deterministic animation analysis)
                try
                {
                    var complexityAnalyzer = new GifComplexityAnalyzer(_logger as ILogger<GifComplexityAnalyzer>);
                    var complexity = await complexityAnalyzer.AnalyzeAsync(gifPath, ct);

                    _logger?.LogDebug("Complexity: stability={Stability:F2}, type={Type}, overall={Overall:F2}",
                        complexity.VisualStability, complexity.AnimationType, complexity.OverallComplexity);

                    // Add complexity to profile
                    profile = profile with { Complexity = complexity };
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Complexity analysis failed for {Path}, continuing without it", gifPath);
                }

                return profile;
            }
            finally
            {
                // Clean up OpenCV Mats
                foreach (var frame in frames)
                {
                    frame?.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error analyzing GIF motion: {Path}", gifPath);
            throw;
        }
    }

    /// <summary>
    /// Convert ImageSharp frame to OpenCV grayscale Mat.
    /// </summary>
    private Mat ConvertToGrayscaleMat(ImageFrame<Rgba32> frame)
    {
        var width = frame.Width;
        var height = frame.Height;

        // Create grayscale byte array
        var grayscale = new byte[width * height];

        frame.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    var pixel = row[x];
                    // Convert to grayscale using luminance formula
                    var gray = (byte)(0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B);
                    grayscale[y * width + x] = gray;
                }
            }
        });

        // Create Mat from byte array using InputArray
        var mat = Mat.FromPixelData(height, width, MatType.CV_8UC1, grayscale);
        return mat;
    }

    /// <summary>
    /// Compute optical flow between consecutive frames using Farneback algorithm.
    /// </summary>
    private List<FrameMotionData> ComputeOpticalFlow(List<Mat> frames, CancellationToken ct)
    {
        var motionData = new List<FrameMotionData>();

        for (int i = 0; i < frames.Count - 1; i++)
        {
            if (ct.IsCancellationRequested)
                break;

            var prevFrame = frames[i];
            var nextFrame = frames[i + 1];

            // Compute dense optical flow using Farneback method
            using var flow = new Mat();
            Cv2.CalcOpticalFlowFarneback(
                prevFrame,
                nextFrame,
                flow,
                pyrScale: 0.5,      // Pyramid scale (smaller = more levels)
                levels: 3,          // Number of pyramid levels
                winsize: 15,        // Window size for averaging
                iterations: 3,      // Iterations at each pyramid level
                polyN: 5,           // Polynomial expansion degree
                polySigma: 1.2,     // Gaussian sigma for polynomial expansion
                flags: 0
            );

            // Analyze flow field
            var frameMotion = AnalyzeFlowField(flow, i);
            motionData.Add(frameMotion);
        }

        return motionData;
    }

    /// <summary>
    /// Analyze optical flow field to extract motion statistics.
    /// </summary>
    private FrameMotionData AnalyzeFlowField(Mat flow, int frameIndex)
    {
        double totalHorizontal = 0;
        double totalVertical = 0;
        double totalMagnitude = 0;
        int significantMotionPixels = 0;
        int totalPixels = flow.Rows * flow.Cols;

        // Iterate through flow field
        for (int y = 0; y < flow.Rows; y++)
        {
            for (int x = 0; x < flow.Cols; x++)
            {
                var flowVec = flow.At<Vec2f>(y, x);
                var dx = flowVec.Item0;
                var dy = flowVec.Item1;

                var magnitude = Math.Sqrt(dx * dx + dy * dy);

                if (magnitude > _motionThreshold)
                {
                    totalHorizontal += dx;
                    totalVertical += dy;
                    totalMagnitude += magnitude;
                    significantMotionPixels++;
                }
            }
        }

        // Calculate averages
        var avgHorizontal = significantMotionPixels > 0 ? totalHorizontal / significantMotionPixels : 0;
        var avgVertical = significantMotionPixels > 0 ? totalVertical / significantMotionPixels : 0;
        var avgMagnitude = significantMotionPixels > 0 ? totalMagnitude / significantMotionPixels : 0;

        // Determine direction
        var direction = DetermineDirection(avgHorizontal, avgVertical);

        return new FrameMotionData
        {
            FrameIndex = frameIndex,
            Magnitude = avgMagnitude,
            Direction = direction,
            HorizontalMotion = avgHorizontal,
            VerticalMotion = avgVertical
        };
    }

    /// <summary>
    /// Determine motion direction from horizontal and vertical components.
    /// </summary>
    private string DetermineDirection(double horizontal, double vertical)
    {
        var magnitude = Math.Sqrt(horizontal * horizontal + vertical * vertical);

        if (magnitude < _motionThreshold)
            return "static";

        var angle = Math.Atan2(vertical, horizontal) * 180 / Math.PI;

        // Classify into 8 directions
        if (angle >= -22.5 && angle < 22.5)
            return "right";
        else if (angle >= 22.5 && angle < 67.5)
            return "down-right";
        else if (angle >= 67.5 && angle < 112.5)
            return "down";
        else if (angle >= 112.5 && angle < 157.5)
            return "down-left";
        else if (angle >= 157.5 || angle < -157.5)
            return "left";
        else if (angle >= -157.5 && angle < -112.5)
            return "up-left";
        else if (angle >= -112.5 && angle < -67.5)
            return "up";
        else // -67.5 to -22.5
            return "up-right";
    }

    /// <summary>
    /// Analyze motion patterns across all frames.
    /// </summary>
    private GifMotionProfile AnalyzeMotionPatterns(
        List<FrameMotionData> frameData,
        int totalFrameCount,
        int frameDelayMs,
        bool loops)
    {
        if (frameData.Count == 0)
        {
            return new GifMotionProfile
            {
                FrameCount = totalFrameCount,
                FrameDelayMs = frameDelayMs,
                TotalDurationMs = frameDelayMs * totalFrameCount,
                Loops = loops,
                MotionDirection = "static",
                MotionMagnitude = 0,
                MaxMotionMagnitude = 0,
                MotionPercentage = 0,
                Confidence = 1.0
            };
        }

        // Calculate statistics
        var avgMagnitude = frameData.Average(f => f.Magnitude);
        var maxMagnitude = frameData.Max(f => f.Magnitude);
        var framesWithMotion = frameData.Count(f => f.Magnitude > _motionThreshold);
        var motionPercentage = (double)framesWithMotion / frameData.Count * 100;

        // Determine dominant direction by voting
        var directionCounts = frameData
            .Where(f => f.Magnitude > _motionThreshold)
            .GroupBy(f => f.Direction)
            .Select(g => new { Direction = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToList();

        var dominantDirection = directionCounts.FirstOrDefault()?.Direction ?? "static";

        // Calculate confidence based on consistency
        var confidence = framesWithMotion > 0
            ? (directionCounts.FirstOrDefault()?.Count ?? 0) / (double)framesWithMotion
            : 1.0; // High confidence for static

        return new GifMotionProfile
        {
            FrameCount = totalFrameCount,
            FrameDelayMs = frameDelayMs,
            TotalDurationMs = frameDelayMs * totalFrameCount,
            Loops = loops,
            MotionDirection = dominantDirection,
            MotionMagnitude = avgMagnitude,
            MaxMotionMagnitude = maxMagnitude,
            MotionPercentage = motionPercentage,
            FrameMotionData = _enableDetailedFrameData ? frameData : null,
            Confidence = confidence
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}
