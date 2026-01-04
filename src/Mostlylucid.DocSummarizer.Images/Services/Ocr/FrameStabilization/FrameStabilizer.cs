using Microsoft.Extensions.Logging;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.DocSummarizer.Images.Services.Ocr.FrameStabilization;

/// <summary>
/// Stabilizes frames in an animated image sequence using feature detection and homography.
/// Compensates for camera shake and jitter by aligning frames to a common reference.
///
/// Algorithm:
/// 1. Detect ORB features in reference frame (first frame)
/// 2. Detect ORB features in each target frame
/// 3. Match features using BFMatcher
/// 4. Compute homography matrix from matches
/// 5. Warp target frame to align with reference
/// </summary>
public class FrameStabilizer
{
    private readonly ILogger<FrameStabilizer>? _logger;
    private readonly double _confidenceThreshold;
    private readonly int _maxFeatures;
    private readonly bool _verbose;

    public FrameStabilizer(
        double confidenceThreshold = 0.7,
        int maxFeatures = 500,
        bool verbose = false,
        ILogger<FrameStabilizer>? logger = null)
    {
        _confidenceThreshold = confidenceThreshold;
        _maxFeatures = maxFeatures;
        _verbose = verbose;
        _logger = logger;
    }

    /// <summary>
    /// Stabilize a sequence of frames by aligning them to the first frame.
    /// Returns stabilized frames and confidence scores.
    /// </summary>
    public StabilizationResult StabilizeFrames(List<Image<Rgba32>> frames)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("No frames provided", nameof(frames));
        }

        if (frames.Count == 1)
        {
            // Single frame - no stabilization needed
            return new StabilizationResult
            {
                StabilizedFrames = new List<Image<Rgba32>> { frames[0].Clone() },
                HomographyMatrices = new List<Mat?> { null },
                ConfidenceScores = new List<double> { 1.0 },
                FailedFrameIndices = new List<int>()
            };
        }

        _logger?.LogInformation("Stabilizing {Count} frames using ORB feature detection", frames.Count);

        var stabilizedFrames = new List<Image<Rgba32>>();
        var homographies = new List<Mat?>();
        var confidences = new List<double>();
        var failedIndices = new List<int>();

        // Convert reference frame (first frame) to OpenCV format
        using var referenceFrame = ConvertToOpenCv(frames[0]);
        using var referenceGray = new Mat();
        Cv2.CvtColor(referenceFrame, referenceGray, ColorConversionCodes.BGR2GRAY);

        // Detect features in reference frame
        using var orb = ORB.Create(nFeatures: _maxFeatures);
        using var referenceKeypoints = new Mat();
        using var referenceDescriptors = new Mat();

        orb.DetectAndCompute(referenceGray, null, out var refKeyPoints, referenceDescriptors);

        if (refKeyPoints.Length < 4)
        {
            _logger?.LogWarning("Reference frame has insufficient features ({Count}), stabilization disabled",
                refKeyPoints.Length);

            // Return clones of original frames
            foreach (var frame in frames)
            {
                stabilizedFrames.Add(frame.Clone());
                homographies.Add(null);
                confidences.Add(0.0);
            }

            return new StabilizationResult
            {
                StabilizedFrames = stabilizedFrames,
                HomographyMatrices = homographies,
                ConfidenceScores = confidences,
                FailedFrameIndices = Enumerable.Range(0, frames.Count).ToList()
            };
        }

        if (_verbose)
        {
            _logger?.LogDebug("Reference frame: detected {Count} ORB features", refKeyPoints.Length);
        }

        // First frame is the reference - no transformation needed
        stabilizedFrames.Add(frames[0].Clone());
        homographies.Add(null);
        confidences.Add(1.0);

        // Create matcher for feature matching
        using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: true);

        // Process each frame
        for (int i = 1; i < frames.Count; i++)
        {
            try
            {
                // Convert frame to OpenCV format
                using var currentFrame = ConvertToOpenCv(frames[i]);
                using var currentGray = new Mat();
                Cv2.CvtColor(currentFrame, currentGray, ColorConversionCodes.BGR2GRAY);

                // Detect features in current frame
                using var currentDescriptors = new Mat();
                orb.DetectAndCompute(currentGray, null, out var currentKeyPoints, currentDescriptors);

                if (currentKeyPoints.Length < 4)
                {
                    _logger?.LogWarning("Frame {Index}: insufficient features ({Count}), using original",
                        i, currentKeyPoints.Length);

                    stabilizedFrames.Add(frames[i].Clone());
                    homographies.Add(null);
                    confidences.Add(0.0);
                    failedIndices.Add(i);

                    continue;
                }

                // Match features
                var matches = matcher.Match(referenceDescriptors, currentDescriptors);

                // Filter good matches (distance < threshold)
                var goodMatches = matches
                    .Where(m => m.Distance < 50) // Hamming distance threshold for ORB
                    .OrderBy(m => m.Distance)
                    .ToArray();

                if (goodMatches.Length < 4)
                {
                    _logger?.LogWarning("Frame {Index}: insufficient good matches ({Count}), using original",
                        i, goodMatches.Length);

                    stabilizedFrames.Add(frames[i].Clone());
                    homographies.Add(null);
                    confidences.Add(0.0);
                    failedIndices.Add(i);

                    currentDescriptors?.Dispose();
                    continue;
                }

                // Extract matched keypoint positions
                var refPoints = goodMatches.Select(m => refKeyPoints[m.QueryIdx].Pt).ToArray();
                var curPoints = goodMatches.Select(m => currentKeyPoints[m.TrainIdx].Pt).ToArray();

                // Compute homography using RANSAC
                using var homography = Cv2.FindHomography(
                    InputArray.Create(curPoints),
                    InputArray.Create(refPoints),
                    HomographyMethods.Ransac,
                    ransacReprojThreshold: 3.0);

                if (homography.Empty())
                {
                    _logger?.LogWarning("Frame {Index}: homography computation failed, using original", i);

                    stabilizedFrames.Add(frames[i].Clone());
                    homographies.Add(null);
                    confidences.Add(0.0);
                    failedIndices.Add(i);

                    currentDescriptors?.Dispose();
                    continue;
                }

                // Calculate confidence based on inlier ratio
                var confidence = CalculateHomographyConfidence(homography, curPoints, refPoints);

                if (confidence < _confidenceThreshold)
                {
                    _logger?.LogWarning(
                        "Frame {Index}: low homography confidence ({Confidence:F3} < {Threshold:F3}), using original",
                        i, confidence, _confidenceThreshold);

                    stabilizedFrames.Add(frames[i].Clone());
                    homographies.Add(null);
                    confidences.Add(confidence);
                    failedIndices.Add(i);

                    currentDescriptors?.Dispose();
                    continue;
                }

                // Warp frame using homography
                using var stabilized = new Mat();
                Cv2.WarpPerspective(
                    currentFrame,
                    stabilized,
                    homography,
                    currentFrame.Size(),
                    InterpolationFlags.Linear,
                    BorderTypes.Constant);

                // Convert back to ImageSharp
                var stabilizedImageSharp = ConvertFromOpenCv(stabilized);
                stabilizedFrames.Add(stabilizedImageSharp);
                homographies.Add(homography.Clone());
                confidences.Add(confidence);

                if (_verbose)
                {
                    _logger?.LogDebug(
                        "Frame {Index}: stabilized with {Matches} matches, confidence={Confidence:F3}",
                        i, goodMatches.Length, confidence);
                }

                currentDescriptors?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Frame {Index}: stabilization failed", i);

                stabilizedFrames.Add(frames[i].Clone());
                homographies.Add(null);
                confidences.Add(0.0);
                failedIndices.Add(i);
            }
        }

        _logger?.LogInformation(
            "Stabilization complete: {Success}/{Total} frames stabilized, {Failed} failed",
            frames.Count - failedIndices.Count, frames.Count, failedIndices.Count);

        return new StabilizationResult
        {
            StabilizedFrames = stabilizedFrames,
            HomographyMatrices = homographies,
            ConfidenceScores = confidences,
            FailedFrameIndices = failedIndices
        };
    }

    /// <summary>
    /// Calculate confidence score for homography based on reprojection error.
    /// </summary>
    private double CalculateHomographyConfidence(Mat homography, Point2f[] sourcePoints, Point2f[] targetPoints)
    {
        if (sourcePoints.Length == 0) return 0.0;

        double totalError = 0.0;
        int inliers = 0;
        const double inlierThreshold = 3.0; // pixels

        for (int i = 0; i < sourcePoints.Length; i++)
        {
            // Apply homography to source point
            var src = new double[] { sourcePoints[i].X, sourcePoints[i].Y, 1.0 };
            var transformed = new double[3];

            for (int row = 0; row < 3; row++)
            {
                double sum = 0.0;
                for (int col = 0; col < 3; col++)
                {
                    sum += homography.At<double>(row, col) * src[col];
                }
                transformed[row] = sum;
            }

            // Convert from homogeneous coordinates
            var projectedX = transformed[0] / transformed[2];
            var projectedY = transformed[1] / transformed[2];

            // Calculate reprojection error
            var dx = projectedX - targetPoints[i].X;
            var dy = projectedY - targetPoints[i].Y;
            var error = Math.Sqrt(dx * dx + dy * dy);

            totalError += error;

            if (error < inlierThreshold)
            {
                inliers++;
            }
        }

        // Confidence is inlier ratio (0-1)
        var confidence = inliers / (double)sourcePoints.Length;
        return confidence;
    }

    /// <summary>
    /// Convert ImageSharp Image to OpenCV Mat (BGR format).
    /// </summary>
    private Mat ConvertToOpenCv(Image<Rgba32> image)
    {
        var mat = new Mat(image.Height, image.Width, MatType.CV_8UC3);

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < image.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < image.Width; x++)
                {
                    var pixel = row[x];

                    // OpenCV uses BGR order
                    mat.Set(y, x, new Vec3b(pixel.B, pixel.G, pixel.R));
                }
            }
        });

        return mat;
    }

    /// <summary>
    /// Convert OpenCV Mat (BGR format) to ImageSharp Image.
    /// </summary>
    private Image<Rgba32> ConvertFromOpenCv(Mat mat)
    {
        var image = new Image<Rgba32>(mat.Width, mat.Height);

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < mat.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < mat.Width; x++)
                {
                    var bgr = mat.At<Vec3b>(y, x);

                    // Convert BGR to RGBA
                    row[x] = new Rgba32(bgr.Item2, bgr.Item1, bgr.Item0, 255);
                }
            }
        });

        return image;
    }
}

/// <summary>
/// Result of frame stabilization operation.
/// </summary>
public record StabilizationResult
{
    /// <summary>
    /// Stabilized frames aligned to reference frame.
    /// </summary>
    public required List<Image<Rgba32>> StabilizedFrames { get; init; }

    /// <summary>
    /// Homography matrices for each frame (null for reference frame or failed frames).
    /// </summary>
    public required List<Mat?> HomographyMatrices { get; init; }

    /// <summary>
    /// Confidence scores (0-1) for each stabilization (1.0 = reference frame, 0.0 = failed).
    /// </summary>
    public required List<double> ConfidenceScores { get; init; }

    /// <summary>
    /// Indices of frames that failed stabilization.
    /// </summary>
    public required List<int> FailedFrameIndices { get; init; }

    /// <summary>
    /// Average stabilization confidence across all frames.
    /// </summary>
    public double AverageConfidence => ConfidenceScores.Count > 0
        ? ConfidenceScores.Average()
        : 0.0;
}
