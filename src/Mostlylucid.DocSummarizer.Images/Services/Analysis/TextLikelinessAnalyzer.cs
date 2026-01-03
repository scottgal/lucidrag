using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Analyzer for detecting text-like regions in images.
/// Uses heuristics based on high-frequency edges, contrast patterns, and horizontal stroke bias.
/// </summary>
public class TextLikelinessAnalyzer
{
    /// <summary>
    /// Calculate text-likeliness score (0-1).
    /// Higher values indicate the image likely contains readable text.
    /// </summary>
    /// <param name="image">Image to analyze</param>
    /// <returns>Text-likeliness score (0-1)</returns>
    public double CalculateTextLikeliness(Image<Rgba32> image)
    {
        // Work on smaller version
        using var workImage = image.Clone();
        var targetWidth = Math.Min(512, workImage.Width);
        if (workImage.Width > targetWidth)
        {
            var scale = targetWidth / (double)workImage.Width;
            workImage.Mutate(x => x.Resize((int)(workImage.Width * scale), (int)(workImage.Height * scale)));
        }

        var width = workImage.Width;
        var height = workImage.Height;

        // Feature 1: High-frequency edge density (text has lots of fine edges)
        var highFreqScore = CalculateHighFrequencyScore(workImage);

        // Feature 2: Bimodal luminance distribution (text is usually dark on light or vice versa)
        var bimodalScore = CalculateBimodalScore(workImage);

        // Feature 3: Horizontal stroke bias (Latin text has horizontal strokes)
        var horizontalBias = CalculateHorizontalBias(workImage);

        // Feature 4: Local contrast variation (text regions have consistent high contrast)
        var contrastScore = CalculateLocalContrastScore(workImage);

        // Combine features with weights
        var score = 0.3 * highFreqScore
                    + 0.25 * bimodalScore
                    + 0.2 * horizontalBias
                    + 0.25 * contrastScore;

        return Math.Clamp(score, 0, 1);
    }

    /// <summary>
    /// Calculate high-frequency edge score
    /// </summary>
    private double CalculateHighFrequencyScore(Image<Rgba32> image)
    {
        var width = image.Width;
        var height = image.Height;
        var highFreqCount = 0;
        var totalCount = 0;

        for (var y = 1; y < height - 1; y += 2)
        {
            var prevRow = image.DangerousGetPixelRowMemory(y - 1).Span;
            var row = image.DangerousGetPixelRowMemory(y).Span;
            var nextRow = image.DangerousGetPixelRowMemory(y + 1).Span;

            for (var x = 1; x < width - 1; x += 2)
            {
                totalCount++;

                // Check for sharp transitions in all directions
                var center = GetLuminance(row[x]);
                var transitions = 0;

                if (Math.Abs(center - GetLuminance(row[x - 1])) > 40) transitions++;
                if (Math.Abs(center - GetLuminance(row[x + 1])) > 40) transitions++;
                if (Math.Abs(center - GetLuminance(prevRow[x])) > 40) transitions++;
                if (Math.Abs(center - GetLuminance(nextRow[x])) > 40) transitions++;

                // High frequency = multiple sharp transitions
                if (transitions >= 2) highFreqCount++;
            }
        }

        return totalCount > 0 ? (double)highFreqCount / totalCount : 0;
    }

    /// <summary>
    /// Calculate bimodal distribution score (text is usually two distinct colors)
    /// </summary>
    private double CalculateBimodalScore(Image<Rgba32> image)
    {
        var histogram = new int[256];
        var totalPixels = 0;

        for (var y = 0; y < image.Height; y += 2)
        {
            var row = image.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < image.Width; x += 2)
            {
                var lum = GetLuminance(row[x]);
                histogram[lum]++;
                totalPixels++;
            }
        }

        if (totalPixels == 0) return 0;

        // Find peaks in histogram (simplified: check dark and light regions)
        var darkSum = 0;
        var lightSum = 0;
        var midSum = 0;

        for (var i = 0; i < 85; i++) darkSum += histogram[i];
        for (var i = 85; i < 170; i++) midSum += histogram[i];
        for (var i = 170; i < 256; i++) lightSum += histogram[i];

        // Bimodal = high dark + high light, low mid
        var bimodal = (darkSum + lightSum) / (double)totalPixels;
        var midRatio = midSum / (double)totalPixels;

        // Score higher if we have both extremes and low middle
        return bimodal * (1 - midRatio);
    }

    /// <summary>
    /// Calculate horizontal stroke bias (text typically has horizontal alignment)
    /// </summary>
    private double CalculateHorizontalBias(Image<Rgba32> image)
    {
        var width = image.Width;
        var height = image.Height;
        var horizontalEdges = 0;
        var verticalEdges = 0;

        for (var y = 1; y < height - 1; y += 2)
        {
            var row = image.DangerousGetPixelRowMemory(y).Span;
            var nextRow = image.DangerousGetPixelRowMemory(y + 1).Span;

            for (var x = 1; x < width - 1; x += 2)
            {
                var current = GetLuminance(row[x]);
                var right = GetLuminance(row[x + 1]);
                var below = GetLuminance(nextRow[x]);

                // Horizontal edge (vertical line)
                if (Math.Abs(current - right) > 30) verticalEdges++;

                // Vertical edge (horizontal line)
                if (Math.Abs(current - below) > 30) horizontalEdges++;
            }
        }

        var total = horizontalEdges + verticalEdges;
        if (total == 0) return 0;

        // Text typically has slight horizontal bias due to baseline alignment
        // but not too much (would indicate lines/borders)
        var ratio = horizontalEdges / (double)total;

        // Optimal ratio for text is around 0.55-0.65
        if (ratio >= 0.5 && ratio <= 0.7)
            return 1.0 - Math.Abs(ratio - 0.58) * 4; // Peak at 0.58

        return 0;
    }

    /// <summary>
    /// Calculate local contrast score (text regions have consistent high contrast)
    /// </summary>
    private double CalculateLocalContrastScore(Image<Rgba32> image)
    {
        var width = image.Width;
        var height = image.Height;
        var highContrastBlocks = 0;
        var totalBlocks = 0;

        var blockSize = 16;

        for (var by = 0; by < height - blockSize; by += blockSize)
        for (var bx = 0; bx < width - blockSize; bx += blockSize)
        {
            totalBlocks++;

            var min = 255;
            var max = 0;

            for (var y = by; y < by + blockSize && y < height; y++)
            {
                var row = image.DangerousGetPixelRowMemory(y).Span;
                for (var x = bx; x < bx + blockSize && x < width; x++)
                {
                    var lum = GetLuminance(row[x]);
                    min = Math.Min(min, lum);
                    max = Math.Max(max, lum);
                }
            }

            // High local contrast (typical for text)
            if (max - min > 100) highContrastBlocks++;
        }

        return totalBlocks > 0 ? (double)highContrastBlocks / totalBlocks : 0;
    }

    private static int GetLuminance(Rgba32 p) =>
        (int)(0.299 * p.R + 0.587 * p.G + 0.114 * p.B);
}
