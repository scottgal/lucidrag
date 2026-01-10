using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Analyzer for edge detection and density using Sobel-like operators.
/// </summary>
public class EdgeAnalyzer
{
    /// <summary>
    /// Calculate edge density (0-1) using a Sobel-like approximation
    /// </summary>
    /// <param name="image">Image to analyze</param>
    /// <returns>Edge density score (0-1)</returns>
    public double CalculateEdgeDensity(Image<Rgba32> image)
    {
        // Work on a smaller version for speed
        using var workImage = image.Clone();
        var targetWidth = Math.Min(256, workImage.Width);
        if (workImage.Width > targetWidth)
        {
            var scale = targetWidth / (double)workImage.Width;
            workImage.Mutate(x => x.Resize((int)(workImage.Width * scale), (int)(workImage.Height * scale)));
        }

        // Convert to grayscale luminance values
        var width = workImage.Width;
        var height = workImage.Height;
        var lum = new double[height, width];

        for (var y = 0; y < height; y++)
        {
            var row = workImage.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < width; x++)
            {
                var p = row[x];
                // Standard luminance formula
                lum[y, x] = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
            }
        }

        // Sobel-like edge detection
        double edgeSum = 0;
        var edgeCount = 0;

        for (var y = 1; y < height - 1; y++)
        for (var x = 1; x < width - 1; x++)
        {
            // Horizontal gradient (Gx)
            var gx = -lum[y - 1, x - 1] - 2 * lum[y, x - 1] - lum[y + 1, x - 1]
                     + lum[y - 1, x + 1] + 2 * lum[y, x + 1] + lum[y + 1, x + 1];

            // Vertical gradient (Gy)
            var gy = -lum[y - 1, x - 1] - 2 * lum[y - 1, x] - lum[y - 1, x + 1]
                     + lum[y + 1, x - 1] + 2 * lum[y + 1, x] + lum[y + 1, x + 1];

            // Gradient magnitude
            var magnitude = Math.Sqrt(gx * gx + gy * gy);
            edgeSum += magnitude;
            edgeCount++;
        }

        if (edgeCount == 0) return 0;

        // Normalize: typical edge magnitudes range 0-1000+
        // Normalize to 0-1 range (empirically, 400 is a reasonable max for "high edge")
        var avgEdge = edgeSum / edgeCount;
        return Math.Min(1.0, avgEdge / 400.0);
    }

    /// <summary>
    /// Calculate entropy of luminance histogram (0-8 range)
    /// </summary>
    public double CalculateLuminanceEntropy(Image<Rgba32> image)
    {
        var histogram = new int[256];
        var totalPixels = 0;

        for (var y = 0; y < image.Height; y += 2)
        {
            var row = image.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < image.Width; x += 2)
            {
                var p = row[x];
                if (p.A < 16) continue;

                var lum = (int)(0.299 * p.R + 0.587 * p.G + 0.114 * p.B);
                histogram[Math.Clamp(lum, 0, 255)]++;
                totalPixels++;
            }
        }

        if (totalPixels == 0) return 0;

        // Calculate Shannon entropy
        double entropy = 0;
        foreach (var count in histogram)
        {
            if (count == 0) continue;
            var p = count / (double)totalPixels;
            entropy -= p * Math.Log2(p);
        }

        return entropy; // Range 0-8 (log2(256) = 8)
    }

    /// <summary>
    /// Detect if image looks like it has straight edges (screenshot/UI indicator)
    /// </summary>
    public double CalculateStraightEdgeRatio(Image<Rgba32> image)
    {
        // Work on smaller version
        using var workImage = image.Clone();
        var targetWidth = Math.Min(256, workImage.Width);
        if (workImage.Width > targetWidth)
        {
            var scale = targetWidth / (double)workImage.Width;
            workImage.Mutate(x => x.Resize((int)(workImage.Width * scale), (int)(workImage.Height * scale)));
        }

        var width = workImage.Width;
        var height = workImage.Height;

        // Count horizontal and vertical edge transitions
        var horizontalEdges = 0;
        var verticalEdges = 0;
        var totalEdges = 0;

        for (var y = 0; y < height - 1; y++)
        {
            var row = workImage.DangerousGetPixelRowMemory(y).Span;
            var nextRow = workImage.DangerousGetPixelRowMemory(y + 1).Span;

            for (var x = 0; x < width - 1; x++)
            {
                var current = row[x];
                var right = row[x + 1];
                var below = nextRow[x];

                // Horizontal edge (between current and right)
                var hDiff = Math.Abs(GetLuminance(current) - GetLuminance(right));
                if (hDiff > 30)
                {
                    verticalEdges++; // Vertical line detected
                    totalEdges++;
                }

                // Vertical edge (between current and below)
                var vDiff = Math.Abs(GetLuminance(current) - GetLuminance(below));
                if (vDiff > 30)
                {
                    horizontalEdges++; // Horizontal line detected
                    totalEdges++;
                }
            }
        }

        if (totalEdges == 0) return 0;

        // High ratio of horizontal + vertical edges vs total edges indicates UI/screenshot
        return (horizontalEdges + verticalEdges) / (double)(totalEdges * 2);
    }

    private static int GetLuminance(Rgba32 p) =>
        (int)(0.299 * p.R + 0.587 * p.G + 0.114 * p.B);
}
