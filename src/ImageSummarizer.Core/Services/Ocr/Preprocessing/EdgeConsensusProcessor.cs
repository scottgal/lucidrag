using Microsoft.Extensions.Logging;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Mostlylucid.DocSummarizer.Images.Services.Ocr.Preprocessing;

/// <summary>
/// Creates high-quality edge maps using consensus voting from multiple edge detection algorithms.
/// Combines Sobel, Canny, and Laplacian of Gaussian (LoG) to identify robust text boundaries.
///
/// Algorithm:
/// 1. Apply Sobel edge detection (gradient-based)
/// 2. Apply Canny edge detection (gradient + non-maximum suppression)
/// 3. Apply Laplacian of Gaussian (LoG) edge detection (second derivative)
/// 4. Create consensus mask: pixels detected by 2+ methods
/// </summary>
public class EdgeConsensusProcessor
{
    private readonly ILogger<EdgeConsensusProcessor>? _logger;
    private readonly bool _verbose;
    private readonly int _consensusThreshold; // Minimum votes (1-3)

    // Canny parameters
    private readonly double _cannyLow;
    private readonly double _cannyHigh;

    // Sobel parameters
    private readonly int _sobelKernelSize;
    private readonly double _sobelThreshold;

    // LoG parameters
    private readonly int _logKernelSize;
    private readonly double _logSigma;
    private readonly double _logThreshold;

    public EdgeConsensusProcessor(
        int consensusThreshold = 2,
        double cannyLow = 50,
        double cannyHigh = 150,
        int sobelKernelSize = 3,
        double sobelThreshold = 50,
        int logKernelSize = 5,
        double logSigma = 1.0,
        double logThreshold = 10,
        bool verbose = false,
        ILogger<EdgeConsensusProcessor>? logger = null)
    {
        if (consensusThreshold < 1 || consensusThreshold > 3)
        {
            throw new ArgumentException("Consensus threshold must be between 1 and 3", nameof(consensusThreshold));
        }

        _consensusThreshold = consensusThreshold;
        _cannyLow = cannyLow;
        _cannyHigh = cannyHigh;
        _sobelKernelSize = sobelKernelSize;
        _sobelThreshold = sobelThreshold;
        _logKernelSize = logKernelSize;
        _logSigma = logSigma;
        _logThreshold = logThreshold;
        _verbose = verbose;
        _logger = logger;
    }

    /// <summary>
    /// Compute edge consensus masks for a sequence of frames.
    /// Returns binary edge masks where pixels detected by multiple algorithms are white.
    /// </summary>
    public EdgeConsensusResult ComputeEdgeConsensus(List<Image<Rgba32>> frames)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("No frames provided", nameof(frames));
        }

        _logger?.LogInformation("Computing edge consensus for {Count} frames (threshold: {Threshold}/3 algorithms)",
            frames.Count, _consensusThreshold);

        var edgeMasks = new List<Image<L8>>();
        var consensusScores = new List<double>();

        foreach (var frame in frames)
        {
            // Convert to OpenCV
            using var mat = ConvertToOpenCv(frame);
            using var gray = new Mat();
            Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);

            // Apply three edge detection methods
            using var cannyEdges = DetectEdges_Canny(gray);
            using var sobelEdges = DetectEdges_Sobel(gray);
            using var logEdges = DetectEdges_LoG(gray);

            // Combine using consensus voting
            var (consensusMask, score) = CombineEdgeMaps(cannyEdges, sobelEdges, logEdges);
            edgeMasks.Add(consensusMask);
            consensusScores.Add(score);

            if (_verbose)
            {
                _logger?.LogDebug("Frame: edge consensus score = {Score:F3}", score);
            }
        }

        var avgScore = consensusScores.Average();
        _logger?.LogInformation("Edge consensus complete: average consensus score = {Score:F3}", avgScore);

        return new EdgeConsensusResult
        {
            EdgeMasks = edgeMasks,
            ConsensusScores = consensusScores,
            AverageConsensusScore = avgScore
        };
    }

    /// <summary>
    /// Detect edges using Canny algorithm.
    /// </summary>
    private Mat DetectEdges_Canny(Mat gray)
    {
        var edges = new Mat();
        Cv2.Canny(gray, edges, _cannyLow, _cannyHigh);
        return edges;
    }

    /// <summary>
    /// Detect edges using Sobel operator (gradient magnitude).
    /// </summary>
    private Mat DetectEdges_Sobel(Mat gray)
    {
        using var gradX = new Mat();
        using var gradY = new Mat();

        // Compute gradients in X and Y directions
        Cv2.Sobel(gray, gradX, MatType.CV_32F, 1, 0, ksize: _sobelKernelSize);
        Cv2.Sobel(gray, gradY, MatType.CV_32F, 0, 1, ksize: _sobelKernelSize);

        // Compute gradient magnitude
        using var magnitude = new Mat();
        Cv2.Magnitude(gradX, gradY, magnitude);

        // Threshold to get binary edge map
        using var normalized = new Mat();
        Cv2.Normalize(magnitude, normalized, 0, 255, NormTypes.MinMax);

        var edges = new Mat();
        normalized.ConvertTo(edges, MatType.CV_8U);

        using var binary = new Mat();
        Cv2.Threshold(edges, binary, _sobelThreshold, 255, ThresholdTypes.Binary);

        return binary.Clone();
    }

    /// <summary>
    /// Detect edges using Laplacian of Gaussian (LoG).
    /// </summary>
    private Mat DetectEdges_LoG(Mat gray)
    {
        // Apply Gaussian blur first (the "G" in LoG)
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new OpenCvSharp.Size(_logKernelSize, _logKernelSize), _logSigma);

        // Apply Laplacian (second derivative)
        using var laplacian = new Mat();
        Cv2.Laplacian(blurred, laplacian, MatType.CV_32F);

        // Take absolute value
        using var absLaplacian = new Mat();
        Cv2.ConvertScaleAbs(laplacian, absLaplacian);

        // Threshold to get binary edge map
        var edges = new Mat();
        Cv2.Threshold(absLaplacian, edges, _logThreshold, 255, ThresholdTypes.Binary);

        return edges;
    }

    /// <summary>
    /// Combine three edge maps using consensus voting.
    /// Returns binary mask where pixels detected by consensusThreshold or more algorithms are white.
    /// </summary>
    private (Image<L8> ConsensusMask, double ConsensusScore) CombineEdgeMaps(Mat canny, Mat sobel, Mat log)
    {
        var width = canny.Width;
        var height = canny.Height;

        // Create vote matrix
        var votes = new byte[height, width];
        int totalEdgePixels = 0;
        int consensusPixels = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte voteCount = 0;

                if (canny.At<byte>(y, x) > 0) voteCount++;
                if (sobel.At<byte>(y, x) > 0) voteCount++;
                if (log.At<byte>(y, x) > 0) voteCount++;

                votes[y, x] = voteCount;

                if (voteCount > 0) totalEdgePixels++;
                if (voteCount >= _consensusThreshold) consensusPixels++;
            }
        }

        // Calculate consensus score (what fraction of edge pixels meet threshold)
        double consensusScore = totalEdgePixels > 0
            ? consensusPixels / (double)totalEdgePixels
            : 0.0;

        // Create ImageSharp mask
        var consensusMask = new Image<L8>(width, height);
        consensusMask.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    byte value = votes[y, x] >= _consensusThreshold ? (byte)255 : (byte)0;
                    row[x] = new L8(value);
                }
            }
        });

        return (consensusMask, consensusScore);
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
}

/// <summary>
/// Result of edge consensus operation.
/// </summary>
public record EdgeConsensusResult
{
    /// <summary>
    /// Binary edge masks for each frame (white = edges detected by consensus).
    /// </summary>
    public required List<Image<L8>> EdgeMasks { get; init; }

    /// <summary>
    /// Consensus scores for each frame (0-1, higher = more agreement between algorithms).
    /// </summary>
    public required List<double> ConsensusScores { get; init; }

    /// <summary>
    /// Average consensus score across all frames.
    /// </summary>
    public required double AverageConsensusScore { get; init; }
}
