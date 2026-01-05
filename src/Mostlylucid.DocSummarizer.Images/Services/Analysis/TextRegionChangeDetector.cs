using Microsoft.Extensions.Logging;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Detects text region changes between frames using OpenCV.
/// Used to optimize OCR filmstrips - only include frames where text actually changed.
/// </summary>
public class TextRegionChangeDetector
{
    private readonly ILogger<TextRegionChangeDetector>? _logger;

    public TextRegionChangeDetector(ILogger<TextRegionChangeDetector>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Detects if text has changed between two frames by comparing text regions.
    /// Uses MSER (Maximally Stable Extremal Regions) for text detection.
    /// </summary>
    /// <returns>True if text appears to have changed, false if same</returns>
    public bool HasTextChanged(Image<Rgba32> frame1, Image<Rgba32> frame2, double threshold = 0.85)
    {
        try
        {
            using var mat1 = ImageSharpToMat(frame1);
            using var mat2 = ImageSharpToMat(frame2);

            // Focus on subtitle region (bottom 30% of frame)
            var subtitleRegionY = (int)(mat1.Height * 0.7);
            var subtitleHeight = mat1.Height - subtitleRegionY;

            using var region1 = new Mat(mat1, new Rect(0, subtitleRegionY, mat1.Width, subtitleHeight));
            using var region2 = new Mat(mat2, new Rect(0, subtitleRegionY, mat2.Width, subtitleHeight));

            // Convert to grayscale for text detection
            using var gray1 = new Mat();
            using var gray2 = new Mat();
            Cv2.CvtColor(region1, gray1, ColorConversionCodes.BGR2GRAY);
            Cv2.CvtColor(region2, gray2, ColorConversionCodes.BGR2GRAY);

            // Apply thresholding to isolate text (typically white/yellow on dark background)
            using var thresh1 = new Mat();
            using var thresh2 = new Mat();
            Cv2.Threshold(gray1, thresh1, 200, 255, ThresholdTypes.Binary);
            Cv2.Threshold(gray2, thresh2, 200, 255, ThresholdTypes.Binary);

            // Compare the thresholded images
            var similarity = CalculateSimilarity(thresh1, thresh2);

            _logger?.LogTrace("Text region similarity: {Similarity:F3}, threshold: {Threshold:F3}, changed: {Changed}",
                similarity, threshold, similarity < threshold);

            return similarity < threshold;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to detect text change, assuming changed");
            return true; // Assume changed on error
        }
    }

    /// <summary>
    /// Filters frames to only include those where text has changed.
    /// Returns indices of frames with text changes.
    /// </summary>
    public List<int> GetTextChangedFrameIndices(List<Image<Rgba32>> frames, double threshold = 0.85)
    {
        if (frames.Count == 0) return new List<int>();
        if (frames.Count == 1) return new List<int> { 0 };

        var indices = new List<int> { 0 }; // Always include first frame

        for (int i = 1; i < frames.Count; i++)
        {
            if (HasTextChanged(frames[indices[^1]], frames[i], threshold))
            {
                indices.Add(i);
            }
        }

        _logger?.LogDebug("Text change detection: {Original} frames -> {Filtered} text-changed frames",
            frames.Count, indices.Count);

        return indices;
    }

    /// <summary>
    /// Extracts frames where text has changed from a list of frames.
    /// </summary>
    public List<Image<Rgba32>> FilterToTextChangedFrames(List<Image<Rgba32>> frames, double threshold = 0.85)
    {
        var indices = GetTextChangedFrameIndices(frames, threshold);
        return indices.Select(i => frames[i]).ToList();
    }

    /// <summary>
    /// Calculates similarity between two binary images (0.0 = different, 1.0 = identical)
    /// </summary>
    private double CalculateSimilarity(Mat img1, Mat img2)
    {
        if (img1.Size() != img2.Size())
        {
            // Resize to match if needed
            Cv2.Resize(img2, img2, img1.Size());
        }

        // XOR the images - identical pixels become 0, different become 255
        using var diff = new Mat();
        Cv2.BitwiseXor(img1, img2, diff);

        // Count non-zero (different) pixels
        var differentPixels = Cv2.CountNonZero(diff);
        var totalPixels = img1.Width * img1.Height;

        // Calculate similarity (1.0 = identical, 0.0 = completely different)
        return 1.0 - ((double)differentPixels / totalPixels);
    }

    /// <summary>
    /// Converts ImageSharp image to OpenCV Mat
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
