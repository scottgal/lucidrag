using Microsoft.Extensions.Logging;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Mostlylucid.DocSummarizer.Images.Services.Ocr.Preprocessing;

/// <summary>
/// Removes static background from animated images to isolate text/foreground content.
/// Uses MOG2 (Mixture of Gaussians) background subtraction for robust foreground detection.
///
/// Algorithm:
/// 1. Build background model from all frames using MOG2
/// 2. Apply background subtraction to each frame
/// 3. Generate binary foreground masks
/// 4. Optionally apply morphological operations to clean up masks
/// </summary>
public class BackgroundSubtractor
{
    private readonly ILogger<BackgroundSubtractor>? _logger;
    private readonly bool _verbose;
    private readonly int _history;
    private readonly double _varThreshold;
    private readonly bool _detectShadows;

    public BackgroundSubtractor(
        int history = 500,
        double varThreshold = 16.0,
        bool detectShadows = false,
        bool verbose = false,
        ILogger<BackgroundSubtractor>? logger = null)
    {
        _history = history;
        _varThreshold = varThreshold;
        _detectShadows = detectShadows;
        _verbose = verbose;
        _logger = logger;
    }

    /// <summary>
    /// Subtract background from a sequence of frames.
    /// Returns foreground masks for each frame.
    /// </summary>
    public BackgroundSubtractionResult SubtractBackground(List<Image<Rgba32>> frames)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("No frames provided", nameof(frames));
        }

        if (frames.Count == 1)
        {
            // Single frame - create full white mask (everything is foreground)
            var mask = CreateFullMask(frames[0].Width, frames[0].Height);
            return new BackgroundSubtractionResult
            {
                ForegroundMasks = new List<Image<L8>> { mask },
                BackgroundModel = null
            };
        }

        _logger?.LogInformation("Subtracting background from {Count} frames using MOG2", frames.Count);

        // Create MOG2 background subtractor
        using var mog2 = BackgroundSubtractorMOG2.Create(
            history: _history,
            varThreshold: _varThreshold,
            detectShadows: _detectShadows);

        var foregroundMasks = new List<Image<L8>>();

        // First pass: Build background model
        var openCvFrames = new List<Mat>();
        foreach (var frame in frames)
        {
            var mat = ConvertToOpenCv(frame);
            openCvFrames.Add(mat);

            // Feed frame to background model
            using var tempMask = new Mat();
            mog2.Apply(mat, tempMask);
        }

        if (_verbose)
        {
            _logger?.LogDebug("Background model built from {Count} frames", frames.Count);
        }

        // Second pass: Generate foreground masks
        for (int i = 0; i < openCvFrames.Count; i++)
        {
            using var fgMask = new Mat();
            mog2.Apply(openCvFrames[i], fgMask, learningRate: 0); // No learning, just extract mask

            // Apply morphological operations to clean up mask
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
            using var cleaned = new Mat();

            // Opening: erosion followed by dilation (removes small noise)
            Cv2.MorphologyEx(fgMask, cleaned, MorphTypes.Open, kernel);

            // Closing: dilation followed by erosion (fills small holes)
            using var final = new Mat();
            Cv2.MorphologyEx(cleaned, final, MorphTypes.Close, kernel);

            // Convert to ImageSharp grayscale
            var maskImage = ConvertMaskFromOpenCv(final);
            foregroundMasks.Add(maskImage);

            if (_verbose)
            {
                var foregroundRatio = CalculateForegroundRatio(final);
                _logger?.LogDebug("Frame {Index}: foreground ratio = {Ratio:F3}", i, foregroundRatio);
            }
        }

        // Get background model image (optional, for debugging)
        using var backgroundModel = new Mat();
        mog2.GetBackgroundImage(backgroundModel);
        var backgroundImage = backgroundModel.Empty() ? null : ConvertFromOpenCv(backgroundModel);

        // Clean up OpenCV mats
        foreach (var mat in openCvFrames)
        {
            mat.Dispose();
        }

        _logger?.LogInformation("Background subtraction complete: generated {Count} foreground masks", foregroundMasks.Count);

        return new BackgroundSubtractionResult
        {
            ForegroundMasks = foregroundMasks,
            BackgroundModel = backgroundImage
        };
    }

    /// <summary>
    /// Calculate the ratio of foreground pixels in a binary mask (0-1).
    /// </summary>
    private double CalculateForegroundRatio(Mat mask)
    {
        var totalPixels = mask.Width * mask.Height;
        if (totalPixels == 0) return 0.0;

        var foregroundPixels = Cv2.CountNonZero(mask);
        return foregroundPixels / (double)totalPixels;
    }

    /// <summary>
    /// Create a full white mask (everything is foreground).
    /// </summary>
    private Image<L8> CreateFullMask(int width, int height)
    {
        var mask = new Image<L8>(width, height);
        mask.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                row.Fill(new L8(255));
            }
        });
        return mask;
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

    /// <summary>
    /// Convert OpenCV grayscale mask Mat to ImageSharp L8 Image.
    /// </summary>
    private Image<L8> ConvertMaskFromOpenCv(Mat mask)
    {
        var image = new Image<L8>(mask.Width, mask.Height);

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < mask.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < mask.Width; x++)
                {
                    var value = mask.At<byte>(y, x);
                    row[x] = new L8(value);
                }
            }
        });

        return image;
    }
}

/// <summary>
/// Result of background subtraction operation.
/// </summary>
public record BackgroundSubtractionResult
{
    /// <summary>
    /// Binary foreground masks for each frame (white = foreground, black = background).
    /// </summary>
    public required List<Image<L8>> ForegroundMasks { get; init; }

    /// <summary>
    /// Learned background model image (optional, for debugging).
    /// </summary>
    public Image<Rgba32>? BackgroundModel { get; init; }
}
