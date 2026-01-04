using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Analyzer for extracting dominant colors and color grid from images using ImageSharp.
/// Uses grid-based quantization for spatial color analysis.
/// </summary>
public class ColorAnalyzer
{
    private readonly ColorGridConfig _config;

    // Expanded named colors for better coverage of color space
    // Includes saturated primaries, desaturated tones, and pastels
    private static readonly Dictionary<string, (int R, int G, int B)> NamedColors = new()
    {
        // Grayscale
        ["Black"] = (0, 0, 0),
        ["White"] = (255, 255, 255),
        ["Gray"] = (128, 128, 128),
        ["Light Gray"] = (192, 192, 192),
        ["Dark Gray"] = (64, 64, 64),
        ["Silver"] = (192, 192, 192),
        ["Charcoal"] = (54, 69, 79),
        ["Slate"] = (112, 128, 144),

        // Reds (saturated)
        ["Red"] = (255, 0, 0),
        ["Crimson"] = (220, 20, 60),
        ["Scarlet"] = (255, 36, 0),
        ["Ruby"] = (224, 17, 95),
        ["Maroon"] = (128, 0, 0),
        ["Burgundy"] = (128, 0, 32),

        // Reds (desaturated/pastel)
        ["Pink"] = (255, 192, 203),
        ["Hot Pink"] = (255, 105, 180),
        ["Rose"] = (255, 0, 127),
        ["Salmon"] = (250, 128, 114),
        ["Coral"] = (255, 127, 80),
        ["Peach"] = (255, 218, 185),

        // Oranges
        ["Orange"] = (255, 165, 0),
        ["Dark Orange"] = (255, 140, 0),
        ["Tangerine"] = (255, 127, 0),
        ["Burnt Orange"] = (204, 85, 0),
        ["Amber"] = (255, 191, 0),

        // Yellows
        ["Yellow"] = (255, 255, 0),
        ["Gold"] = (255, 215, 0),
        ["Lemon"] = (255, 247, 0),
        ["Cream"] = (255, 253, 208),
        ["Khaki"] = (240, 230, 140),
        ["Beige"] = (245, 245, 220),
        ["Ivory"] = (255, 255, 240),
        ["Tan"] = (210, 180, 140),

        // Greens (saturated)
        ["Green"] = (0, 128, 0),
        ["Lime"] = (0, 255, 0),
        ["Forest Green"] = (34, 139, 34),
        ["Emerald"] = (0, 201, 87),
        ["Jade"] = (0, 168, 107),
        ["Olive"] = (128, 128, 0),
        ["Dark Green"] = (0, 100, 0),

        // Greens (desaturated/pastel)
        ["Mint"] = (152, 255, 152),
        ["Seafoam"] = (159, 226, 191),
        ["Sage"] = (188, 184, 138),
        ["Chartreuse"] = (127, 255, 0),

        // Cyans/Teals
        ["Cyan"] = (0, 255, 255),
        ["Aqua"] = (0, 255, 255),
        ["Teal"] = (0, 128, 128),
        ["Turquoise"] = (64, 224, 208),
        ["Aquamarine"] = (127, 255, 212),

        // Blues (saturated)
        ["Blue"] = (0, 0, 255),
        ["Navy"] = (0, 0, 128),
        ["Royal Blue"] = (65, 105, 225),
        ["Cobalt"] = (0, 71, 171),
        ["Sapphire"] = (15, 82, 186),
        ["Indigo"] = (75, 0, 130),
        ["Midnight Blue"] = (25, 25, 112),

        // Blues (desaturated/pastel)
        ["Sky Blue"] = (135, 206, 235),
        ["Light Blue"] = (173, 216, 230),
        ["Baby Blue"] = (137, 207, 240),
        ["Powder Blue"] = (176, 224, 230),
        ["Steel Blue"] = (70, 130, 180),
        ["Periwinkle"] = (204, 204, 255),

        // Purples/Violets (saturated)
        ["Purple"] = (128, 0, 128),
        ["Violet"] = (238, 130, 238),
        ["Magenta"] = (255, 0, 255),
        ["Fuchsia"] = (255, 0, 255),
        ["Plum"] = (221, 160, 221),
        ["Orchid"] = (218, 112, 214),
        ["Amethyst"] = (153, 102, 204),

        // Purples (desaturated/pastel)
        ["Lavender"] = (230, 230, 250),
        ["Lilac"] = (200, 162, 200),
        ["Mauve"] = (224, 176, 255),

        // Browns
        ["Brown"] = (139, 69, 19),
        ["Chocolate"] = (210, 105, 30),
        ["Sienna"] = (160, 82, 45),
        ["Saddle Brown"] = (139, 69, 19),
        ["Coffee"] = (111, 78, 55),
        ["Mocha"] = (128, 71, 41),
    };

    public ColorAnalyzer(IOptions<ImageConfig> config)
    {
        _config = config.Value.ColorGrid;
    }

    public ColorAnalyzer(ColorGridConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Compute dominant color grid from an image
    /// </summary>
    public ColorGrid ComputeColorGrid(Image<Rgba32> image)
    {
        // Resize if needed (keep aspect)
        var targetWidth = _config.TargetWidth;
        if (image.Width > targetWidth)
        {
            var newHeight = (int)Math.Round(image.Height * (targetWidth / (double)image.Width));
            image.Mutate(x => x.Resize(targetWidth, newHeight));
        }

        var rows = _config.Rows;
        var cols = _config.Cols;
        var sampleStep = _config.SampleStep;
        var bucketBits = _config.BucketBits;

        var cellW = Math.Max(1, image.Width / cols);
        var cellH = Math.Max(1, image.Height / rows);

        var cells = new List<CellColor>(rows * cols);

        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
        {
            var x0 = c * cellW;
            var y0 = r * cellH;
            var x1 = (c == cols - 1) ? image.Width : x0 + cellW;
            var y1 = (r == rows - 1) ? image.Height : y0 + cellH;

            var buckets = new Dictionary<int, int>();
            var sampled = 0;

            for (var y = y0; y < y1; y += sampleStep)
            {
                var rowSpan = image.DangerousGetPixelRowMemory(y).Span;
                for (var x = x0; x < x1; x += sampleStep)
                {
                    var p = rowSpan[x];

                    // Skip transparent pixels
                    if (p.A < 16) continue;

                    sampled++;

                    // Quantize RGB to bucketBits per channel
                    var shift = 8 - bucketBits;
                    var rq = p.R >> shift;
                    var gq = p.G >> shift;
                    var bq = p.B >> shift;

                    var key = (rq << (2 * bucketBits)) | (gq << bucketBits) | bq;
                    buckets[key] = buckets.TryGetValue(key, out var cnt) ? cnt + 1 : 1;
                }
            }

            if (sampled == 0 || buckets.Count == 0)
            {
                cells.Add(new CellColor(r, c, "#000000", 0));
                continue;
            }

            var (bestKey, bestCount) = buckets.MaxBy(kvp => kvp.Value);
            var coverage = bestCount / (double)sampled;

            // Convert bucket center back to RGB
            var shiftBack = 8 - bucketBits;
            var mask = (1 << bucketBits) - 1;

            var rq2 = (bestKey >> (2 * bucketBits)) & mask;
            var gq2 = (bestKey >> bucketBits) & mask;
            var bq2 = bestKey & mask;

            // Use center of bucket
            var r8 = (byte)((rq2 << shiftBack) + (1 << (shiftBack - 1)));
            var g8 = (byte)((gq2 << shiftBack) + (1 << (shiftBack - 1)));
            var b8 = (byte)((bq2 << shiftBack) + (1 << (shiftBack - 1)));

            var hex = $"#{r8:X2}{g8:X2}{b8:X2}";
            cells.Add(new CellColor(r, c, hex, coverage));
        }

        return new ColorGrid(cells, rows, cols);
    }

    /// <summary>
    /// Extract dominant colors from the entire image
    /// </summary>
    public List<DominantColor> ExtractDominantColors(Image<Rgba32> image, int maxColors = 5)
    {
        var buckets = new Dictionary<int, (long R, long G, long B, int Count)>();
        var bucketBits = _config.BucketBits;
        var totalPixels = 0;

        // Sample the entire image
        for (var y = 0; y < image.Height; y += 2)
        {
            var rowSpan = image.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < image.Width; x += 2)
            {
                var p = rowSpan[x];
                if (p.A < 16) continue;

                totalPixels++;

                var shift = 8 - bucketBits;
                var rq = p.R >> shift;
                var gq = p.G >> shift;
                var bq = p.B >> shift;

                var key = (rq << (2 * bucketBits)) | (gq << bucketBits) | bq;

                if (buckets.TryGetValue(key, out var existing))
                {
                    buckets[key] = (existing.R + p.R, existing.G + p.G, existing.B + p.B, existing.Count + 1);
                }
                else
                {
                    buckets[key] = (p.R, p.G, p.B, 1);
                }
            }
        }

        if (totalPixels == 0)
            return [new DominantColor("#000000", 100, "Black")];

        // Get top colors
        var topBuckets = buckets
            .OrderByDescending(kvp => kvp.Value.Count)
            .Take(maxColors)
            .ToList();

        var result = new List<DominantColor>();
        foreach (var (_, (r, g, b, count)) in topBuckets)
        {
            // Average color in this bucket
            var avgR = (byte)(r / count);
            var avgG = (byte)(g / count);
            var avgB = (byte)(b / count);

            var hex = $"#{avgR:X2}{avgG:X2}{avgB:X2}";
            var percentage = (count / (double)totalPixels) * 100;
            var name = GetClosestColorName(avgR, avgG, avgB);

            result.Add(new DominantColor(hex, percentage, name));
        }

        return result;
    }

    /// <summary>
    /// Calculate mean saturation (0-1)
    /// </summary>
    public double CalculateMeanSaturation(Image<Rgba32> image)
    {
        double totalSaturation = 0;
        var count = 0;

        for (var y = 0; y < image.Height; y += 4)
        {
            var rowSpan = image.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < image.Width; x += 4)
            {
                var p = rowSpan[x];
                if (p.A < 16) continue;

                var (_, s, _) = RgbToHsl(p.R, p.G, p.B);
                totalSaturation += s;
                count++;
            }
        }

        return count > 0 ? totalSaturation / count : 0;
    }

    /// <summary>
    /// Determine if image is mostly grayscale
    /// </summary>
    public bool IsMostlyGrayscale(Image<Rgba32> image, double threshold = 0.1)
    {
        var saturation = CalculateMeanSaturation(image);
        return saturation < threshold;
    }

    /// <summary>
    /// Find the closest named color
    /// </summary>
    private static string GetClosestColorName(byte r, byte g, byte b)
    {
        var bestName = "Unknown";
        var bestDistance = double.MaxValue;

        foreach (var (name, (nr, ng, nb)) in NamedColors)
        {
            // Weighted Euclidean distance (human perception)
            var dr = r - nr;
            var dg = g - ng;
            var db = b - nb;

            // Weight: R=0.3, G=0.59, B=0.11 (luminance-based)
            var distance = Math.Sqrt(0.3 * dr * dr + 0.59 * dg * dg + 0.11 * db * db);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestName = name;
            }
        }

        return bestName;
    }

    /// <summary>
    /// Convert RGB to HSL
    /// </summary>
    private static (double H, double S, double L) RgbToHsl(byte r, byte g, byte b)
    {
        var rf = r / 255.0;
        var gf = g / 255.0;
        var bf = b / 255.0;

        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;

        var l = (max + min) / 2;

        double s;
        if (delta == 0)
        {
            s = 0;
        }
        else
        {
            s = l > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        }

        double h = 0;
        if (delta != 0)
        {
            if (max == rf)
                h = ((gf - bf) / delta) % 6;
            else if (max == gf)
                h = (bf - rf) / delta + 2;
            else
                h = (rf - gf) / delta + 4;

            h *= 60;
            if (h < 0) h += 360;
        }

        return (h, s, l);
    }
}
