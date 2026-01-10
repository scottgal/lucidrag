using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Mostlylucid.DocSummarizer.Images.Orchestration.Contributors;

/// <summary>
///     Color analysis contributor - extracts color information from images.
///     Uses the ConfiguredWaveBase pattern with YAML configuration.
/// </summary>
public sealed class ColorContributor : ConfiguredWaveBase
{
    private readonly ColorAnalyzer _colorAnalyzer;
    private readonly ImageStreamProcessor? _streamProcessor;

    public ColorContributor(
        IWaveConfigProvider configProvider,
        ColorAnalyzer colorAnalyzer,
        ImageStreamProcessor? streamProcessor = null)
        : base(configProvider)
    {
        _colorAnalyzer = colorAnalyzer;
        _streamProcessor = streamProcessor;
    }

    public override string Name => "ColorWave";

    // No triggers - runs in first wave
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    // Config-driven parameters from YAML
    private int DominantColorCount => GetParam("dominant_color_count", 5);
    private double GrayscaleSaturationThreshold => GetParam("grayscale_saturation_threshold", 0.02);
    private double MinColorPercentage => GetParam("min_color_percentage", 1.0);

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        ImageBlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        // Use the already-loaded image from state if available
        Image<Rgba32>? image = null;
        var disposeImage = false;

        if (state.LoadedImage != null)
        {
            image = state.LoadedImage.CloneAs<Rgba32>();
            disposeImage = true;
        }
        else if (_streamProcessor != null)
        {
            image = await _streamProcessor.LoadImageSafelyAsync(state.ImagePath, cancellationToken);
            disposeImage = true;
        }
        else
        {
            await using var stream = File.OpenRead(state.ImagePath);
            image = await Image.LoadAsync<Rgba32>(stream, cancellationToken);
            disposeImage = true;
        }

        try
        {
            var signals = new Dictionary<string, object>();

            // Dominant colors
            var dominantColors = _colorAnalyzer.ExtractDominantColors(image);
            if (dominantColors?.Any() == true)
            {
                // Primary dominant color
                var primary = dominantColors.First();
                signals[ImageSignalKeys.ColorDominant] = primary.Hex;

                // Color palette
                var palette = dominantColors
                    .Where(c => c.Percentage >= MinColorPercentage)
                    .Take(DominantColorCount)
                    .Select(c => c.Hex)
                    .ToList();
                signals[ImageSignalKeys.ColorPalette] = palette;

                // Individual dominant colors
                for (var i = 0; i < Math.Min(DominantColorCount, dominantColors.Count); i++)
                {
                    var color = dominantColors[i];
                    signals[$"color.dominant_{i + 1}"] = color.Hex;
                    signals[$"color.dominant_{i + 1}.name"] = color.Name;
                    signals[$"color.dominant_{i + 1}.percentage"] = color.Percentage;
                }

                // Vibrant and muted colors
                var sortedBySaturation = dominantColors
                    .OrderByDescending(c => CalculateSaturationFromHex(c.Hex))
                    .ToList();
                if (sortedBySaturation.Any())
                {
                    signals["color.vibrant"] = sortedBySaturation.First().Hex;
                    signals["color.muted"] = sortedBySaturation.Last().Hex;
                }
            }

            // Mean saturation
            var meanSaturation = _colorAnalyzer.CalculateMeanSaturation(image);
            signals[ImageSignalKeys.ColorSaturation] = meanSaturation;

            // Grayscale detection
            var isGrayscale = _colorAnalyzer.IsMostlyGrayscale(image);
            signals[ImageSignalKeys.IsGrayscale] = isGrayscale;

            // Color temperature estimation
            var temperature = EstimateColorTemperature(dominantColors);
            signals[ImageSignalKeys.ColorTemperature] = temperature;

            // Tinted grayscale detection (sepia, blue tint, aged photos, etc.)
            var (isTinted, tintType, colorCast) = _colorAnalyzer.DetectColorCastOpenCv(state.ImagePath);
            if (isTinted && !string.IsNullOrEmpty(tintType))
            {
                signals["color.tint_type"] = tintType;
                signals["color.tint_magnitude"] = colorCast;
            }

            // Color grid for spatial analysis
            var colorGrid = _colorAnalyzer.ComputeColorGrid(image);
            if (colorGrid != null)
            {
                signals["color.grid.rows"] = colorGrid.Rows;
                signals["color.grid.cols"] = colorGrid.Cols;

                // Calculate contrast from grid
                var contrast = CalculateGridContrast(colorGrid);
                signals[ImageSignalKeys.ColorContrast] = contrast;
            }

            // Create the contribution with all signals
            contributions.Add(HighConfidenceContribution(
                "color",
                $"Dominant color: {signals.GetValueOrDefault(ImageSignalKeys.ColorDominant, "unknown")}, " +
                $"Grayscale: {isGrayscale}, Temperature: {temperature}",
                signals));

            return contributions;
        }
        finally
        {
            if (disposeImage)
                image?.Dispose();
        }
    }

    private static string EstimateColorTemperature(IReadOnlyList<DominantColor>? colors)
    {
        if (colors == null || colors.Count == 0)
            return "neutral";

        // Calculate average warmth from dominant colors
        var warmth = 0.0;
        var total = 0.0;

        foreach (var color in colors)
        {
            var hex = color.Hex.TrimStart('#');
            if (hex.Length < 6) continue;

            var r = Convert.ToInt32(hex[..2], 16);
            var g = Convert.ToInt32(hex[2..4], 16);
            var b = Convert.ToInt32(hex[4..6], 16);

            // Simple warmth calculation: more red = warmer, more blue = cooler
            var colorWarmth = (r - b) / 255.0;
            warmth += colorWarmth * color.Percentage;
            total += color.Percentage;
        }

        if (total == 0) return "neutral";

        var avgWarmth = warmth / total;
        return avgWarmth switch
        {
            > 0.2 => "warm",
            < -0.2 => "cool",
            _ => "neutral"
        };
    }

    private static double CalculateGridContrast(ColorGrid grid)
    {
        if (grid.Cells == null || grid.Cells.Count < 2)
            return 0.5;

        // Calculate variance in luminance across grid cells
        var luminances = new List<double>();
        foreach (var cell in grid.Cells)
        {
            var hex = cell.Hex?.TrimStart('#') ?? "808080";
            if (hex.Length < 6) hex = "808080";

            var r = Convert.ToInt32(hex[..2], 16);
            var g = Convert.ToInt32(hex[2..4], 16);
            var b = Convert.ToInt32(hex[4..6], 16);

            // Calculate relative luminance
            var luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            luminances.Add(luminance);
        }

        if (luminances.Count < 2) return 0.5;

        // Calculate standard deviation as contrast measure
        var mean = luminances.Average();
        var variance = luminances.Sum(l => (l - mean) * (l - mean)) / luminances.Count;
        var stdDev = Math.Sqrt(variance);

        // Normalize to 0-1 range (max stdDev for black/white would be ~0.5)
        return Math.Min(1.0, stdDev * 2);
    }

    private static double CalculateSaturationFromHex(string hex)
    {
        // Strip '#' prefix if present
        if (hex.StartsWith('#'))
            hex = hex[1..];

        if (hex.Length < 6) return 0;

        var r = Convert.ToInt32(hex[..2], 16) / 255.0;
        var g = Convert.ToInt32(hex[2..4], 16) / 255.0;
        var b = Convert.ToInt32(hex[4..6], 16) / 255.0;

        var max = Math.Max(Math.Max(r, g), b);
        var min = Math.Min(Math.Min(r, g), b);
        var delta = max - min;

        if (delta == 0) return 0;

        var lightness = (max + min) / 2;
        return lightness > 0.5
            ? delta / (2.0 - max - min)
            : delta / (max + min);
    }
}
