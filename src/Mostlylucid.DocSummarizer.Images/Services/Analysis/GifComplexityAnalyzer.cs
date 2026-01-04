using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Analyzes the complexity of animated images (GIF/WebP) using deterministic metrics.
/// Provides insights into animation patterns, visual stability, and temporal complexity.
/// </summary>
public class GifComplexityAnalyzer
{
    private readonly ILogger<GifComplexityAnalyzer>? _logger;
    private readonly int _maxFramesToAnalyze = 50;

    public GifComplexityAnalyzer(ILogger<GifComplexityAnalyzer>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyze the complexity of an animated image.
    /// </summary>
    public async Task<GifComplexityProfile> AnalyzeAsync(
        string imagePath,
        CancellationToken ct = default)
    {
        var format = Image.DetectFormat(imagePath);
        var formatName = format?.Name?.ToUpperInvariant();

        if (formatName != "GIF" && formatName != "WEBP")
        {
            throw new ArgumentException($"Unsupported format: {formatName}. Only GIF and WebP are supported.");
        }

        using var image = await Image.LoadAsync<Rgba32>(imagePath, ct);
        var frameCount = image.Frames.Count;

        if (frameCount == 1)
        {
            // Static image - zero complexity
            return new GifComplexityProfile
            {
                FrameCount = 1,
                VisualStability = 1.0,
                ColorVariation = 0.0,
                EntropyVariation = 0.0,
                SceneChangeCount = 0,
                AnimationType = "static",
                OverallComplexity = 0.0
            };
        }

        _logger?.LogInformation("Analyzing complexity for {Format} with {FrameCount} frames",
            formatName, frameCount);

        var framesToAnalyze = Math.Min(frameCount, _maxFramesToAnalyze);
        var samplingInterval = Math.Max(1, frameCount / framesToAnalyze);

        // Analyze frames
        var frameAnalyses = new List<FrameAnalysis>();
        var previousFrame = default(Image<Rgba32>);

        for (int i = 0; i < frameCount; i += samplingInterval)
        {
            if (ct.IsCancellationRequested) break;
            if (frameAnalyses.Count >= framesToAnalyze) break;

            var frame = image.Frames.CloneFrame(i);

            // Compute frame metrics
            var entropy = ComputeEntropy(frame);
            var dominantColor = GetDominantColor(frame);
            double frameDifference = 0;

            if (previousFrame != null)
            {
                frameDifference = ComputeFrameDifference(previousFrame, frame);
                previousFrame.Dispose();
            }

            frameAnalyses.Add(new FrameAnalysis
            {
                FrameIndex = i,
                Entropy = entropy,
                DominantColor = dominantColor,
                DifferenceFromPrevious = frameDifference
            });

            previousFrame = frame;
        }

        previousFrame?.Dispose();

        // Compute complexity metrics
        var visualStability = ComputeVisualStability(frameAnalyses);
        var colorVariation = ComputeColorVariation(frameAnalyses);
        var entropyVariation = ComputeEntropyVariation(frameAnalyses);
        var sceneChangeCount = DetectSceneChanges(frameAnalyses);
        var animationType = ClassifyAnimationType(frameAnalyses, sceneChangeCount);
        var overallComplexity = ComputeOverallComplexity(visualStability, colorVariation, entropyVariation, sceneChangeCount, frameCount);

        _logger?.LogInformation("Complexity analysis complete: stability={Stability:F2}, colorVar={ColorVar:F2}, type={Type}",
            visualStability, colorVariation, animationType);

        return new GifComplexityProfile
        {
            FrameCount = frameCount,
            VisualStability = visualStability,
            ColorVariation = colorVariation,
            EntropyVariation = entropyVariation,
            SceneChangeCount = sceneChangeCount,
            AnimationType = animationType,
            OverallComplexity = overallComplexity,
            AverageFrameDifference = frameAnalyses.Count > 1 ? frameAnalyses.Average(f => f.DifferenceFromPrevious) : 0,
            MaxFrameDifference = frameAnalyses.Count > 1 ? frameAnalyses.Max(f => f.DifferenceFromPrevious) : 0
        };
    }

    /// <summary>
    /// Compute visual stability (1.0 = very stable, 0.0 = very chaotic).
    /// Based on frame-to-frame differences.
    /// </summary>
    private double ComputeVisualStability(List<FrameAnalysis> frames)
    {
        if (frames.Count <= 1) return 1.0;

        var avgDifference = frames.Average(f => f.DifferenceFromPrevious);

        // Normalize: 0.05 = very stable, 0.3+ = very unstable
        var stability = 1.0 - Math.Clamp(avgDifference / 0.3, 0, 1);

        return stability;
    }

    /// <summary>
    /// Compute color variation across frames (0.0 = same colors, 1.0 = wildly changing).
    /// </summary>
    private double ComputeColorVariation(List<FrameAnalysis> frames)
    {
        if (frames.Count <= 1) return 0;

        var colorDistances = new List<double>();

        for (int i = 1; i < frames.Count; i++)
        {
            var color1 = frames[i - 1].DominantColor;
            var color2 = frames[i].DominantColor;

            var distance = Math.Sqrt(
                Math.Pow(color1.R - color2.R, 2) +
                Math.Pow(color1.G - color2.G, 2) +
                Math.Pow(color1.B - color2.B, 2)
            ) / 441.67; // Normalize to 0-1 (max RGB distance is sqrt(255^2 * 3) = 441.67)

            colorDistances.Add(distance);
        }

        return colorDistances.Average();
    }

    /// <summary>
    /// Compute entropy variation (how much visual complexity changes).
    /// </summary>
    private double ComputeEntropyVariation(List<FrameAnalysis> frames)
    {
        if (frames.Count <= 1) return 0;

        var entropies = frames.Select(f => f.Entropy).ToList();
        var avgEntropy = entropies.Average();
        var variance = entropies.Average(e => Math.Pow(e - avgEntropy, 2));
        var stdDev = Math.Sqrt(variance);

        // Normalize by typical entropy range (0-8)
        return Math.Clamp(stdDev / 2.0, 0, 1);
    }

    /// <summary>
    /// Detect scene changes (abrupt transitions with high frame difference).
    /// </summary>
    private int DetectSceneChanges(List<FrameAnalysis> frames)
    {
        if (frames.Count <= 1) return 0;

        var avgDiff = frames.Average(f => f.DifferenceFromPrevious);
        var threshold = avgDiff * 2.5; // Scene change = 2.5x average difference

        return frames.Count(f => f.DifferenceFromPrevious > threshold);
    }

    /// <summary>
    /// Classify animation type based on patterns.
    /// </summary>
    private string ClassifyAnimationType(List<FrameAnalysis> frames, int sceneChanges)
    {
        if (frames.Count <= 1) return "static";

        var avgDiff = frames.Average(f => f.DifferenceFromPrevious);

        // Scene cuts (e.g., slideshow)
        if (sceneChanges > frames.Count * 0.3)
        {
            return "slideshow";
        }

        // Very low difference = simple toggle or loop
        if (avgDiff < 0.05)
        {
            return "simple-loop";
        }

        // Moderate difference = smooth animation
        if (avgDiff < 0.15)
        {
            return "smooth-animation";
        }

        // High difference = complex/chaotic
        return "complex-animation";
    }

    /// <summary>
    /// Compute overall complexity score (0-1).
    /// </summary>
    private double ComputeOverallComplexity(
        double visualStability,
        double colorVariation,
        double entropyVariation,
        int sceneChangeCount,
        int totalFrames)
    {
        var instability = 1.0 - visualStability;
        var sceneChangeRatio = Math.Clamp((double)sceneChangeCount / totalFrames, 0, 1);

        // Weighted combination
        var complexity = 0.4 * instability
                       + 0.3 * colorVariation
                       + 0.2 * entropyVariation
                       + 0.1 * sceneChangeRatio;

        return Math.Clamp(complexity, 0, 1);
    }

    /// <summary>
    /// Compute frame-to-frame difference (0 = identical, 1 = completely different).
    /// </summary>
    private double ComputeFrameDifference(Image<Rgba32> frame1, Image<Rgba32> frame2)
    {
        var width = Math.Min(frame1.Width, frame2.Width);
        var height = Math.Min(frame1.Height, frame2.Height);

        long totalDifference = 0;
        long totalPixels = 0;

        for (int y = 0; y < height; y += 4) // Sample every 4th pixel for speed
        {
            var row1 = frame1.DangerousGetPixelRowMemory(y).Span;
            var row2 = frame2.DangerousGetPixelRowMemory(y).Span;

            for (int x = 0; x < width; x += 4)
            {
                var p1 = row1[x];
                var p2 = row2[x];

                var diff = Math.Abs(p1.R - p2.R) + Math.Abs(p1.G - p2.G) + Math.Abs(p1.B - p2.B);
                totalDifference += diff;
                totalPixels++;
            }
        }

        // Normalize by max possible difference (255 * 3 per pixel)
        return totalPixels > 0 ? totalDifference / (totalPixels * 765.0) : 0;
    }

    /// <summary>
    /// Compute luminance entropy of a frame.
    /// </summary>
    private double ComputeEntropy(Image<Rgba32> frame)
    {
        var histogram = new int[256];
        var totalPixels = 0;

        for (int y = 0; y < frame.Height; y += 2)
        {
            var row = frame.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < frame.Width; x += 2)
            {
                var pixel = row[x];
                var luminance = (int)(0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B);
                histogram[luminance]++;
                totalPixels++;
            }
        }

        var entropy = 0.0;
        for (int i = 0; i < 256; i++)
        {
            if (histogram[i] > 0)
            {
                var probability = histogram[i] / (double)totalPixels;
                entropy -= probability * Math.Log2(probability);
            }
        }

        return entropy;
    }

    /// <summary>
    /// Get dominant color of a frame (simple average).
    /// </summary>
    private Rgba32 GetDominantColor(Image<Rgba32> frame)
    {
        long r = 0, g = 0, b = 0;
        long count = 0;

        for (int y = 0; y < frame.Height; y += 4)
        {
            var row = frame.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < frame.Width; x += 4)
            {
                var pixel = row[x];
                r += pixel.R;
                g += pixel.G;
                b += pixel.B;
                count++;
            }
        }

        return new Rgba32(
            (byte)(r / count),
            (byte)(g / count),
            (byte)(b / count)
        );
    }

    private class FrameAnalysis
    {
        public int FrameIndex { get; init; }
        public double Entropy { get; init; }
        public Rgba32 DominantColor { get; init; }
        public double DifferenceFromPrevious { get; init; }
    }
}
