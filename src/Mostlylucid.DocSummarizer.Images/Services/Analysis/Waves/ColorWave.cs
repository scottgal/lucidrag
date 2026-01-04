using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Analysis wave for color-related signals.
/// Wraps existing ColorAnalyzer functionality.
/// Uses streaming and auto-downscaling for large images.
/// </summary>
public class ColorWave : IAnalysisWave
{
    private readonly ColorAnalyzer _colorAnalyzer;
    private readonly ImageStreamProcessor? _streamProcessor;

    public string Name => "ColorWave";
    public int Priority => 100; // High priority - many waves depend on color info
    public IReadOnlyList<string> Tags => new[] { SignalTags.Visual, SignalTags.Color };

    public ColorWave(ColorAnalyzer colorAnalyzer, ImageStreamProcessor? streamProcessor = null)
    {
        _colorAnalyzer = colorAnalyzer;
        _streamProcessor = streamProcessor;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        // Use stream processor if available for automatic downscaling
        Image<Rgba32> image;
        if (_streamProcessor != null)
        {
            image = await _streamProcessor.LoadImageSafelyAsync(imagePath, ct);
        }
        else
        {
            // Fallback: direct load
            await using var stream = File.OpenRead(imagePath);
            image = await Image.LoadAsync<Rgba32>(stream, ct);
        }

        using (image)
        {

        // Dominant colors
        var dominantColors = _colorAnalyzer.ExtractDominantColors(image);
        if (dominantColors?.Any() == true)
        {
            signals.Add(new Signal
            {
                Key = "color.dominant_colors",
                Value = dominantColors,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { SignalTags.Color },
                Metadata = new Dictionary<string, object>
                {
                    ["count"] = dominantColors.Count
                }
            });

            // Individual dominant colors for easy access
            for (int i = 0; i < Math.Min(5, dominantColors.Count); i++)
            {
                var color = dominantColors[i];
                signals.Add(new Signal
                {
                    Key = $"color.dominant_{i + 1}",
                    Value = color.Hex,
                    Confidence = color.Percentage / 100.0, // Use coverage as confidence
                    Source = Name,
                    Tags = new List<string> { SignalTags.Color },
                    Metadata = new Dictionary<string, object>
                    {
                        ["name"] = color.Name,
                        ["percentage"] = color.Percentage
                    }
                });
            }
        }

        // Color grid
        var colorGrid = _colorAnalyzer.ComputeColorGrid(image);
        if (colorGrid != null)
        {
            // Emit aggregate grid signal
            signals.Add(new Signal
            {
                Key = "color.grid",
                Value = colorGrid,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { SignalTags.Color },
                Metadata = new Dictionary<string, object>
                {
                    ["rows"] = colorGrid.Rows,
                    ["cols"] = colorGrid.Cols,
                    ["cells"] = colorGrid.Cells?.Count ?? 0
                }
            });

            // Emit per-cell signals for cropping detection and parallel chunk signatures
            if (colorGrid.Cells != null)
            {
                foreach (var cell in colorGrid.Cells)
                {
                    var isEdgeCell = cell.Row == 0 || cell.Row == colorGrid.Rows - 1 ||
                                   cell.Col == 0 || cell.Col == colorGrid.Cols - 1;
                    var isCenterCell = cell.Row == colorGrid.Rows / 2 && cell.Col == colorGrid.Cols / 2;

                    signals.Add(new Signal
                    {
                        Key = $"color.grid.cell.{cell.Row}_{cell.Col}",
                        Value = cell,
                        Confidence = 1.0,
                        Source = Name,
                        Tags = new List<string> { SignalTags.Color, "grid_cell" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["row"] = cell.Row,
                            ["col"] = cell.Col,
                            ["is_edge"] = isEdgeCell,
                            ["is_center"] = isCenterCell,
                            ["dominant_hex"] = cell.Hex,
                            ["coverage"] = cell.Coverage,
                            // Chunk signature for parallel processing
                            ["chunk_signature"] = $"{cell.Row}_{cell.Col}_{cell.Hex}"
                        }
                    });
                }
            }
        }

        // Mean saturation
        var meanSaturation = _colorAnalyzer.CalculateMeanSaturation(image);
        signals.Add(new Signal
        {
            Key = "color.mean_saturation",
            Value = meanSaturation,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { SignalTags.Color }
        });

        // Grayscale detection
        var isGrayscale = _colorAnalyzer.IsMostlyGrayscale(image);
        signals.Add(new Signal
        {
            Key = "color.is_grayscale",
            Value = isGrayscale,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { SignalTags.Color },
            Metadata = new Dictionary<string, object>
            {
                ["saturation_threshold"] = 0.1
            }
        });

        // Color palette (hex codes)
        if (dominantColors?.Any() == true)
        {
            var palette = dominantColors.Select(c => c.Hex).ToList();
            signals.Add(new Signal
            {
                Key = "color.palette",
                Value = palette,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { SignalTags.Color },
                Metadata = new Dictionary<string, object>
                {
                    ["count"] = palette.Count
                }
            });

            // Unique color count (estimate from dominant colors)
            signals.Add(new Signal
            {
                Key = "color.unique_count",
                Value = dominantColors.Count,
                Confidence = 0.8, // Estimate, not exact
                Source = Name,
                Tags = new List<string> { SignalTags.Color }
            });

            // Vibrant and muted colors (if available)
            var sortedBySaturation = dominantColors.OrderByDescending(c => CalculateSaturationFromHex(c.Hex)).ToList();
            if (sortedBySaturation.Any())
            {
                signals.Add(new Signal
                {
                    Key = "color.vibrant",
                    Value = sortedBySaturation.First().Hex,
                    Confidence = 0.9,
                    Source = Name,
                    Tags = new List<string> { SignalTags.Color }
                });

                signals.Add(new Signal
                {
                    Key = "color.muted",
                    Value = sortedBySaturation.Last().Hex,
                    Confidence = 0.9,
                    Source = Name,
                    Tags = new List<string> { SignalTags.Color }
                });
            }
        }

            // Cache image for other waves
            context.SetCached("image", image.CloneAs<Rgba32>());

            return signals;
        } // end using(image)
    }

    private static double CalculateSaturationFromHex(string hex)
    {
        // Simple HSL saturation estimate from hex color
        // Strip '#' prefix if present
        if (hex.StartsWith('#'))
            hex = hex.Substring(1);

        var r = Convert.ToInt32(hex.Substring(0, 2), 16) / 255.0;
        var g = Convert.ToInt32(hex.Substring(2, 2), 16) / 255.0;
        var b = Convert.ToInt32(hex.Substring(4, 2), 16) / 255.0;

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
