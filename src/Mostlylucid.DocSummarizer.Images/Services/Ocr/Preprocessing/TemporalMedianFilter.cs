using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Mostlylucid.DocSummarizer.Images.Services.Ocr.Preprocessing;

/// <summary>
/// Creates a noise-free composite image by computing pixel-wise median across multiple aligned frames.
/// This is one of the most effective techniques for GIF OCR - it removes noise while preserving text edges.
///
/// Algorithm:
/// 1. Stack all aligned frames
/// 2. For each pixel position, collect values across all frames
/// 3. Compute median value (middle value when sorted)
/// 4. Result: "best possible text plate" with noise removed
///
/// Benefits:
/// - Removes temporal noise (compression artifacts, camera noise)
/// - Preserves edges better than mean/blur
/// - Works well even with partial misalignment
/// </summary>
public class TemporalMedianFilter
{
    private readonly ILogger<TemporalMedianFilter>? _logger;
    private readonly bool _verbose;

    public TemporalMedianFilter(
        bool verbose = false,
        ILogger<TemporalMedianFilter>? logger = null)
    {
        _verbose = verbose;
        _logger = logger;
    }

    /// <summary>
    /// Compute temporal median composite from a sequence of aligned frames.
    /// Returns a single "best possible" frame with noise removed.
    /// </summary>
    public Image<Rgba32> ComputeTemporalMedian(List<Image<Rgba32>> frames)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("No frames provided", nameof(frames));
        }

        if (frames.Count == 1)
        {
            _logger?.LogInformation("Single frame - returning clone without median filtering");
            return frames[0].Clone();
        }

        _logger?.LogInformation("Computing temporal median from {Count} frames", frames.Count);

        var width = frames[0].Width;
        var height = frames[0].Height;

        // Verify all frames have same dimensions
        if (frames.Any(f => f.Width != width || f.Height != height))
        {
            throw new ArgumentException("All frames must have the same dimensions");
        }

        // Create result image
        var result = new Image<Rgba32>(width, height);

        // Preallocate pixel value arrays for each channel
        var rValues = new byte[frames.Count];
        var gValues = new byte[frames.Count];
        var bValues = new byte[frames.Count];
        var aValues = new byte[frames.Count];

        // Process each pixel
        result.ProcessPixelRows(resultAccessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var resultRow = resultAccessor.GetRowSpan(y);

                for (int x = 0; x < width; x++)
                {
                    // Collect pixel values from all frames at position (x, y)
                    for (int i = 0; i < frames.Count; i++)
                    {
                        frames[i].ProcessPixelRows(frameAccessor =>
                        {
                            var frameRow = frameAccessor.GetRowSpan(y);
                            var pixel = frameRow[x];

                            rValues[i] = pixel.R;
                            gValues[i] = pixel.G;
                            bValues[i] = pixel.B;
                            aValues[i] = pixel.A;
                        });
                    }

                    // Compute median for each channel
                    var medianR = ComputeMedian(rValues);
                    var medianG = ComputeMedian(gValues);
                    var medianB = ComputeMedian(bValues);
                    var medianA = ComputeMedian(aValues);

                    resultRow[x] = new Rgba32(medianR, medianG, medianB, medianA);
                }

                if (_verbose && y % 50 == 0)
                {
                    var progress = (y + 1) / (double)height * 100;
                    _logger?.LogDebug("Temporal median progress: {Progress:F1}%", progress);
                }
            }
        });

        _logger?.LogInformation("Temporal median complete: created noise-free composite from {Count} frames", frames.Count);

        return result;
    }

    /// <summary>
    /// Compute temporal median composite with optional foreground masking.
    /// Only pixels marked as foreground in masks are considered for median calculation.
    /// </summary>
    public Image<Rgba32> ComputeTemporalMedianWithMasks(
        List<Image<Rgba32>> frames,
        List<Image<L8>> foregroundMasks)
    {
        if (frames.Count != foregroundMasks.Count)
        {
            throw new ArgumentException("Frame count must match mask count");
        }

        if (frames.Count == 0)
        {
            throw new ArgumentException("No frames provided", nameof(frames));
        }

        if (frames.Count == 1)
        {
            return frames[0].Clone();
        }

        _logger?.LogInformation("Computing masked temporal median from {Count} frames", frames.Count);

        var width = frames[0].Width;
        var height = frames[0].Height;

        var result = new Image<Rgba32>(width, height);

        // Preallocate with max possible size
        var rValues = new List<byte>(frames.Count);
        var gValues = new List<byte>(frames.Count);
        var bValues = new List<byte>(frames.Count);

        result.ProcessPixelRows(resultAccessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var resultRow = resultAccessor.GetRowSpan(y);

                for (int x = 0; x < width; x++)
                {
                    rValues.Clear();
                    gValues.Clear();
                    bValues.Clear();

                    // Collect pixel values only from frames where mask indicates foreground
                    for (int i = 0; i < frames.Count; i++)
                    {
                        // Check if this pixel is foreground in the mask
                        bool isForeground = false;
                        foregroundMasks[i].ProcessPixelRows(maskAccessor =>
                        {
                            var maskRow = maskAccessor.GetRowSpan(y);
                            isForeground = maskRow[x].PackedValue > 128; // Threshold at 50%
                        });

                        if (isForeground)
                        {
                            frames[i].ProcessPixelRows(frameAccessor =>
                            {
                                var frameRow = frameAccessor.GetRowSpan(y);
                                var pixel = frameRow[x];

                                rValues.Add(pixel.R);
                                gValues.Add(pixel.G);
                                bValues.Add(pixel.B);
                            });
                        }
                    }

                    // If no foreground pixels, use first frame value (fallback)
                    if (rValues.Count == 0)
                    {
                        Rgba32 fallbackPixel = default;
                        frames[0].ProcessPixelRows(frameAccessor =>
                        {
                            var frameRow = frameAccessor.GetRowSpan(y);
                            fallbackPixel = frameRow[x];
                        });
                        resultRow[x] = fallbackPixel;
                    }
                    else
                    {
                        // Compute median from foreground pixels only
                        var medianR = ComputeMedian(rValues.ToArray());
                        var medianG = ComputeMedian(gValues.ToArray());
                        var medianB = ComputeMedian(bValues.ToArray());

                        resultRow[x] = new Rgba32(medianR, medianG, medianB, 255);
                    }
                }
            }
        });

        _logger?.LogInformation("Masked temporal median complete");

        return result;
    }

    /// <summary>
    /// Compute median value from an array of bytes.
    /// Uses partial sort for efficiency (only need middle element).
    /// </summary>
    private byte ComputeMedian(byte[] values)
    {
        if (values.Length == 0) return 0;
        if (values.Length == 1) return values[0];
        if (values.Length == 2) return (byte)((values[0] + values[1]) / 2);

        // Sort array (in-place)
        Array.Sort(values);

        // Return middle value
        int middleIndex = values.Length / 2;

        if (values.Length % 2 == 0)
        {
            // Even number of values - average the two middle values
            return (byte)((values[middleIndex - 1] + values[middleIndex]) / 2);
        }
        else
        {
            // Odd number of values - return middle value
            return values[middleIndex];
        }
    }
}
