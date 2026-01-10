using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Main image analyzer that produces deterministic ImageProfile from images.
/// Combines all sub-analyzers (Color, Edge, Blur, TextLikeliness) into a single profile.
/// </summary>
public class ImageAnalyzer : IImageAnalyzer
{
    private readonly ImageConfig _config;
    private readonly ColorAnalyzer _colorAnalyzer;
    private readonly EdgeAnalyzer _edgeAnalyzer;
    private readonly BlurAnalyzer _blurAnalyzer;
    private readonly TextLikelinessAnalyzer _textAnalyzer;

    public ImageAnalyzer(IOptions<ImageConfig> config)
    {
        _config = config.Value;
        _colorAnalyzer = new ColorAnalyzer(config);
        _edgeAnalyzer = new EdgeAnalyzer();
        _blurAnalyzer = new BlurAnalyzer();
        _textAnalyzer = new TextLikelinessAnalyzer();
    }

    public ImageAnalyzer(
        ImageConfig config,
        ColorAnalyzer colorAnalyzer,
        EdgeAnalyzer edgeAnalyzer,
        BlurAnalyzer blurAnalyzer,
        TextLikelinessAnalyzer textAnalyzer)
    {
        _config = config;
        _colorAnalyzer = colorAnalyzer;
        _edgeAnalyzer = edgeAnalyzer;
        _blurAnalyzer = blurAnalyzer;
        _textAnalyzer = textAnalyzer;
    }

    /// <inheritdoc />
    public async Task<ImageProfile> AnalyzeAsync(string imagePath, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(imagePath, ct);
        return await AnalyzeAsync(bytes, Path.GetFileName(imagePath), ct);
    }

    /// <inheritdoc />
    public Task<ImageProfile> AnalyzeAsync(byte[] imageBytes, string fileName, CancellationToken ct = default)
    {
        // Calculate SHA256 hash
        var sha256 = Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant();

        using var image = Image.Load<Rgba32>(imageBytes);
        var format = Image.DetectFormat(imageBytes)?.Name ?? "Unknown";

        // Basic properties
        var width = image.Width;
        var height = image.Height;
        var aspectRatio = width / (double)height;

        // Check for EXIF
        var hasExif = image.Metadata.ExifProfile is { Values.Count: > 0 };

        // === Run all analyzers ===

        // Color analysis
        var dominantColors = _colorAnalyzer.ExtractDominantColors(image);
        var colorGrid = _colorAnalyzer.ComputeColorGrid(image.Clone());
        var meanSaturation = _colorAnalyzer.CalculateMeanSaturation(image);
        var isMostlyGrayscale = _colorAnalyzer.IsMostlyGrayscale(image);

        // Edge analysis
        var edgeDensity = _edgeAnalyzer.CalculateEdgeDensity(image);
        var luminanceEntropy = _edgeAnalyzer.CalculateLuminanceEntropy(image);
        var straightEdgeRatio = _edgeAnalyzer.CalculateStraightEdgeRatio(image);

        // Blur analysis
        var laplacianVariance = _blurAnalyzer.CalculateLaplacianVariance(image);

        // Text detection
        var textLikeliness = _textAnalyzer.CalculateTextLikeliness(image);

        // Brightness/contrast
        var (meanLuminance, lumStdDev, clippedBlacks, clippedWhites) = CalculateBrightnessStats(image);

        // JPEG artifact detection (simplified)
        double? compressionArtifacts = format.Equals("JPEG", StringComparison.OrdinalIgnoreCase)
            ? DetectJpegArtifacts(image)
            : null;

        // Type detection
        var (detectedType, typeConfidence) = DetectImageType(
            edgeDensity, luminanceEntropy, straightEdgeRatio,
            textLikeliness, meanSaturation, isMostlyGrayscale,
            dominantColors, width, height, laplacianVariance);

        // Perceptual hash
        var perceptualHash = CalculateDHash(image);

        var profile = new ImageProfile
        {
            Sha256 = sha256,
            Format = format,
            Width = width,
            Height = height,
            AspectRatio = aspectRatio,
            HasExif = hasExif,
            EdgeDensity = edgeDensity,
            LuminanceEntropy = luminanceEntropy,
            CompressionArtifacts = compressionArtifacts,
            MeanLuminance = meanLuminance,
            LuminanceStdDev = lumStdDev,
            ClippedBlacksPercent = clippedBlacks,
            ClippedWhitesPercent = clippedWhites,
            DominantColors = dominantColors,
            ColorGrid = colorGrid,
            MeanSaturation = meanSaturation,
            IsMostlyGrayscale = isMostlyGrayscale,
            LaplacianVariance = laplacianVariance,
            TextLikeliness = textLikeliness,
            DetectedType = detectedType,
            TypeConfidence = typeConfidence,
            PerceptualHash = perceptualHash
        };

        return Task.FromResult(profile);
    }

    /// <inheritdoc />
    public Task<string> GeneratePerceptualHashAsync(string imagePath, CancellationToken ct = default)
    {
        using var image = Image.Load<Rgba32>(imagePath);
        return Task.FromResult(CalculateDHash(image));
    }

    /// <inheritdoc />
    public async Task<byte[]> GenerateThumbnailAsync(string imagePath, int maxSize = 256, CancellationToken ct = default)
    {
        using var image = Image.Load<Rgba32>(imagePath);

        // Calculate new dimensions maintaining aspect ratio
        var scale = Math.Min(maxSize / (double)image.Width, maxSize / (double)image.Height);
        var newWidth = (int)(image.Width * scale);
        var newHeight = (int)(image.Height * scale);

        image.Mutate(x => x.Resize(newWidth, newHeight));

        using var ms = new MemoryStream();
        await image.SaveAsWebpAsync(ms, new WebpEncoder { Quality = 80 }, ct);
        return ms.ToArray();
    }

    /// <summary>
    /// Calculate brightness statistics
    /// </summary>
    private (double Mean, double StdDev, double ClippedBlacks, double ClippedWhites) CalculateBrightnessStats(Image<Rgba32> image)
    {
        var values = new List<double>();
        var clippedBlacks = 0;
        var clippedWhites = 0;

        for (var y = 0; y < image.Height; y += 2)
        {
            var row = image.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < image.Width; x += 2)
            {
                var p = row[x];
                if (p.A < 16) continue;

                var lum = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
                values.Add(lum);

                if (lum < 5) clippedBlacks++;
                if (lum > 250) clippedWhites++;
            }
        }

        if (values.Count == 0)
            return (0, 0, 0, 0);

        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        var stdDev = Math.Sqrt(variance);

        var blacksPercent = 100.0 * clippedBlacks / values.Count;
        var whitesPercent = 100.0 * clippedWhites / values.Count;

        return (mean, stdDev, blacksPercent, whitesPercent);
    }

    /// <summary>
    /// Detect JPEG compression artifacts (blockiness)
    /// </summary>
    private double DetectJpegArtifacts(Image<Rgba32> image)
    {
        // Simplified: check for 8x8 block boundaries
        var width = image.Width;
        var height = image.Height;
        var blockBoundaryDiffs = 0.0;
        var internalDiffs = 0.0;
        var blockCount = 0;
        var internalCount = 0;

        for (var y = 8; y < height - 8; y += 8)
        {
            var prevRow = image.DangerousGetPixelRowMemory(y - 1).Span;
            var row = image.DangerousGetPixelRowMemory(y).Span;

            for (var x = 8; x < width - 8; x += 8)
            {
                // Difference at block boundary
                var boundaryDiff = Math.Abs(GetLuminance(prevRow[x]) - GetLuminance(row[x]));
                blockBoundaryDiffs += boundaryDiff;
                blockCount++;

                // Difference inside block
                if (y + 4 < height)
                {
                    var midRow = image.DangerousGetPixelRowMemory(y + 4).Span;
                    var internalDiff = Math.Abs(GetLuminance(row[x]) - GetLuminance(midRow[x]));
                    internalDiffs += internalDiff;
                    internalCount++;
                }
            }
        }

        if (blockCount == 0 || internalCount == 0) return 0;

        var avgBoundary = blockBoundaryDiffs / blockCount;
        var avgInternal = internalDiffs / internalCount;

        // Ratio of boundary to internal differences (higher = more artifacts)
        return avgInternal > 0 ? Math.Min(1, avgBoundary / avgInternal / 2) : 0;
    }

    /// <summary>
    /// Detect image type based on measured properties
    /// </summary>
    private (ImageType Type, double Confidence) DetectImageType(
        double edgeDensity, double luminanceEntropy, double straightEdgeRatio,
        double textLikeliness, double meanSaturation, bool isMostlyGrayscale,
        List<DominantColor> dominantColors, int width, int height, double laplacianVariance)
    {
        var scores = new Dictionary<ImageType, double>();

        // Screenshot: high straight edge ratio, limited color palette, high text likeliness
        var screenshotScore = 0.0;
        screenshotScore += straightEdgeRatio * 0.4;
        screenshotScore += textLikeliness * 0.3;
        if (dominantColors.Count >= 2 && dominantColors[0].Percentage > 40) screenshotScore += 0.2;
        if (isMostlyGrayscale) screenshotScore -= 0.1; // Screenshots usually have some color
        scores[ImageType.Screenshot] = screenshotScore;

        // Scanned document: high text, mostly grayscale or sepia, high contrast
        var docScore = 0.0;
        docScore += textLikeliness * 0.5;
        if (isMostlyGrayscale || meanSaturation < 0.15) docScore += 0.3;
        if (dominantColors.Any(c => c.Name is "White" or "Ivory" or "Beige") &&
            dominantColors.Any(c => c.Name is "Black" or "Dark Gray"))
            docScore += 0.2;
        scores[ImageType.ScannedDocument] = docScore;

        // Photo: natural color distribution, moderate entropy, EXIF likely
        var photoScore = 0.0;
        if (!isMostlyGrayscale && meanSaturation > 0.15) photoScore += 0.3;
        if (luminanceEntropy > 5 && luminanceEntropy < 7.5) photoScore += 0.2;
        if (edgeDensity > 0.1 && edgeDensity < 0.5) photoScore += 0.2;
        if (straightEdgeRatio < 0.4) photoScore += 0.15;
        if (textLikeliness < 0.3) photoScore += 0.15;
        scores[ImageType.Photo] = photoScore;

        // Diagram: high contrast, limited colors, geometric shapes
        var diagramScore = 0.0;
        diagramScore += straightEdgeRatio * 0.3;
        if (dominantColors.Count <= 6 && dominantColors[0].Percentage > 50) diagramScore += 0.25;
        if (isMostlyGrayscale || meanSaturation < 0.2) diagramScore += 0.15;
        if (textLikeliness > 0.2 && textLikeliness < 0.6) diagramScore += 0.15;
        if (edgeDensity > 0.2) diagramScore += 0.15;
        scores[ImageType.Diagram] = diagramScore;

        // Chart: geometric, limited colors, often includes text
        var chartScore = 0.0;
        if (!isMostlyGrayscale && dominantColors.Count >= 3 && dominantColors.Count <= 8) chartScore += 0.3;
        chartScore += straightEdgeRatio * 0.2;
        if (textLikeliness > 0.1 && textLikeliness < 0.4) chartScore += 0.2;
        if (dominantColors[0].Percentage > 30) chartScore += 0.15;
        scores[ImageType.Chart] = chartScore;

        // Icon: small dimensions, limited colors, high edge density
        var iconScore = 0.0;
        if (width <= 256 && height <= 256) iconScore += 0.4;
        else if (width <= 512 && height <= 512) iconScore += 0.2;
        if (dominantColors.Count <= 6) iconScore += 0.2;
        if (edgeDensity > 0.3) iconScore += 0.2;
        scores[ImageType.Icon] = iconScore;

        // Get the highest scoring type
        var bestType = scores.MaxBy(kvp => kvp.Value);

        // Confidence is the difference from the second-best score
        var sortedScores = scores.OrderByDescending(kvp => kvp.Value).ToList();
        var confidence = sortedScores.Count > 1
            ? Math.Min(1, sortedScores[0].Value - sortedScores[1].Value + 0.3)
            : sortedScores[0].Value;

        // If best score is too low, return Unknown
        if (bestType.Value < 0.3)
            return (ImageType.Unknown, 0.5);

        return (bestType.Key, confidence);
    }

    /// <summary>
    /// Calculate difference hash (dHash) for perceptual deduplication
    /// </summary>
    private string CalculateDHash(Image<Rgba32> image)
    {
        // Resize to 9x8 (we need 8x8 differences = 64 bits)
        using var small = image.Clone();
        small.Mutate(x => x.Resize(9, 8).Grayscale());

        var hash = 0UL;
        var bit = 0;

        for (var y = 0; y < 8; y++)
        {
            var row = small.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < 8; x++)
            {
                // Compare adjacent pixels (left < right = 1)
                if (row[x].R < row[x + 1].R)
                    hash |= 1UL << bit;
                bit++;
            }
        }

        return hash.ToString("X16");
    }

    private static int GetLuminance(Rgba32 p) =>
        (int)(0.299 * p.R + 0.587 * p.G + 0.114 * p.B);
}
