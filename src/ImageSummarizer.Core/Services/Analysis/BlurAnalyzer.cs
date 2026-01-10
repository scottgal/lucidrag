using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Analyzer for blur/sharpness detection using Laplacian variance.
/// Higher values indicate sharper images.
/// </summary>
public class BlurAnalyzer
{
    /// <summary>
    /// Calculate Laplacian variance (sharpness measure).
    /// Higher values = sharper image. Typical range: 0-5000+
    /// </summary>
    /// <param name="image">Image to analyze</param>
    /// <returns>Laplacian variance (higher = sharper)</returns>
    public double CalculateLaplacianVariance(Image<Rgba32> image)
    {
        // Work on a smaller version for speed
        using var workImage = image.Clone();
        var targetWidth = Math.Min(512, workImage.Width);
        if (workImage.Width > targetWidth)
        {
            var scale = targetWidth / (double)workImage.Width;
            workImage.Mutate(x => x.Resize((int)(workImage.Width * scale), (int)(workImage.Height * scale)));
        }

        var width = workImage.Width;
        var height = workImage.Height;

        // Convert to grayscale luminance
        var lum = new double[height, width];
        for (var y = 0; y < height; y++)
        {
            var row = workImage.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < width; x++)
            {
                var p = row[x];
                lum[y, x] = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
            }
        }

        // Apply Laplacian kernel: [0, 1, 0]
        //                         [1,-4, 1]
        //                         [0, 1, 0]
        var laplacianValues = new List<double>();

        for (var y = 1; y < height - 1; y++)
        for (var x = 1; x < width - 1; x++)
        {
            var lap = -4 * lum[y, x]
                      + lum[y - 1, x]
                      + lum[y + 1, x]
                      + lum[y, x - 1]
                      + lum[y, x + 1];

            laplacianValues.Add(lap);
        }

        if (laplacianValues.Count == 0) return 0;

        // Calculate variance of Laplacian
        var mean = laplacianValues.Average();
        var variance = laplacianValues.Sum(v => (v - mean) * (v - mean)) / laplacianValues.Count;

        return variance;
    }

    /// <summary>
    /// Categorize blur level
    /// </summary>
    public BlurLevel CategorizeBlur(double laplacianVariance)
    {
        return laplacianVariance switch
        {
            < 100 => BlurLevel.VeryBlurry,
            < 300 => BlurLevel.Blurry,
            < 700 => BlurLevel.SlightlySoft,
            < 1500 => BlurLevel.Sharp,
            _ => BlurLevel.VerySharp
        };
    }
}

/// <summary>
/// Categorized blur levels
/// </summary>
public enum BlurLevel
{
    VeryBlurry,
    Blurry,
    SlightlySoft,
    Sharp,
    VerySharp
}
