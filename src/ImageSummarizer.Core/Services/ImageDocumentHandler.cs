using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.DocSummarizer.Services;

namespace Mostlylucid.DocSummarizer.Images.Services;

/// <summary>
/// Document handler for image files. Integrates with the DocSummarizer.Core handler registry.
/// </summary>
public class ImageDocumentHandler : IDocumentHandler
{
    private readonly ImageConfig _config;
    private readonly IImageAnalyzer _analyzer;

    public ImageDocumentHandler(IOptions<ImageConfig> config, IImageAnalyzer analyzer)
    {
        _config = config.Value;
        _analyzer = analyzer;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedExtensions => _config.SupportedExtensions.AsReadOnly();

    /// <inheritdoc />
    public int Priority => 10; // Higher priority than default handlers

    /// <inheritdoc />
    public string HandlerName => "ImageHandler";

    /// <inheritdoc />
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return _config.SupportedExtensions.Contains(extension);
    }

    /// <inheritdoc />
    public async Task<DocumentContent> ProcessAsync(string filePath, DocumentHandlerOptions options)
    {
        // Analyze the image
        var profile = await _analyzer.AnalyzeAsync(filePath, options.CancellationToken);

        // Convert profile to markdown
        var markdown = ConvertProfileToMarkdown(profile, filePath);

        var metadata = new Dictionary<string, object>
        {
            ["imageType"] = profile.DetectedType.ToString(),
            ["width"] = profile.Width,
            ["height"] = profile.Height,
            ["format"] = profile.Format,
            ["sha256"] = profile.Sha256,
            ["textLikeliness"] = profile.TextLikeliness,
            ["dominantColors"] = profile.DominantColors.Select(c => c.Name).ToList()
        };

        return new DocumentContent
        {
            Markdown = markdown,
            Title = $"{profile.DetectedType}: {Path.GetFileName(filePath)}",
            ContentType = "image",
            Metadata = metadata
        };
    }

    private string ConvertProfileToMarkdown(ImageProfile profile, string filePath)
    {
        var sb = new System.Text.StringBuilder();

        // Title
        sb.AppendLine($"# Image: {Path.GetFileName(filePath)}");
        sb.AppendLine();

        // Type and basic info
        sb.AppendLine($"**Type:** {profile.DetectedType}");
        sb.AppendLine($"**Dimensions:** {profile.Width} x {profile.Height} ({profile.Format})");
        sb.AppendLine($"**Aspect Ratio:** {profile.AspectRatio:F2}");
        sb.AppendLine();

        // Visual properties
        sb.AppendLine("## Visual Properties");
        sb.AppendLine();
        sb.AppendLine($"- **Brightness:** {DescribeBrightness(profile.MeanLuminance)}");
        sb.AppendLine($"- **Contrast:** {DescribeContrast(profile.LuminanceStdDev)}");
        sb.AppendLine($"- **Sharpness:** {DescribeSharpness(profile.LaplacianVariance)}");
        sb.AppendLine($"- **Edge Density:** {DescribeEdgeDensity(profile.EdgeDensity)}");
        sb.AppendLine();

        // Colors
        sb.AppendLine("## Color Analysis");
        sb.AppendLine();
        if (profile.IsMostlyGrayscale)
        {
            sb.AppendLine("The image is **mostly grayscale**.");
        }
        else
        {
            sb.AppendLine($"**Saturation:** {profile.MeanSaturation:P0}");
        }
        sb.AppendLine();

        sb.AppendLine("**Dominant Colors:**");
        foreach (var color in profile.DominantColors.Take(5))
        {
            sb.AppendLine($"- {color.Name} ({color.Hex}): {color.Percentage:F1}%");
        }
        sb.AppendLine();

        // Color grid summary if available
        if (profile.ColorGrid != null)
        {
            sb.AppendLine("**Color Distribution:**");
            var grid = profile.ColorGrid;
            for (var r = 0; r < grid.Rows; r++)
            {
                var rowColors = grid.Cells.Where(c => c.Row == r).OrderBy(c => c.Col);
                var descriptions = rowColors.Select(c =>
                {
                    var colorName = GetSimpleColorName(c.Hex);
                    return $"{colorName} ({c.Coverage:P0})";
                });
                var position = r == 0 ? "Top" : r == grid.Rows - 1 ? "Bottom" : "Middle";
                sb.AppendLine($"- {position}: {string.Join(", ", descriptions)}");
            }
            sb.AppendLine();
        }

        // Text detection
        if (profile.TextLikeliness > 0.3)
        {
            sb.AppendLine("## Text Detection");
            sb.AppendLine();
            sb.AppendLine($"**Text Likeliness:** {profile.TextLikeliness:P0}");
            sb.AppendLine("This image likely contains readable text. OCR recommended.");
            sb.AppendLine();
        }

        // Technical details
        sb.AppendLine("## Technical Details");
        sb.AppendLine();
        sb.AppendLine($"- **SHA256:** `{profile.Sha256[..16]}...`");
        sb.AppendLine($"- **Perceptual Hash:** `{profile.PerceptualHash}`");
        if (profile.HasExif)
            sb.AppendLine("- Contains EXIF metadata");
        if (profile.CompressionArtifacts.HasValue)
            sb.AppendLine($"- **Compression Artifacts:** {profile.CompressionArtifacts:F2}");

        return sb.ToString();
    }

    private static string DescribeBrightness(double meanLuminance) =>
        meanLuminance switch
        {
            < 40 => "Very dark",
            < 85 => "Dark",
            < 170 => "Normal",
            < 215 => "Bright",
            _ => "Very bright"
        };

    private static string DescribeContrast(double stdDev) =>
        stdDev switch
        {
            < 30 => "Low contrast",
            < 60 => "Normal contrast",
            _ => "High contrast"
        };

    private static string DescribeSharpness(double laplacian) =>
        laplacian switch
        {
            < 100 => "Blurry",
            < 500 => "Slightly soft",
            < 1500 => "Sharp",
            _ => "Very sharp"
        };

    private static string DescribeEdgeDensity(double density) =>
        density switch
        {
            < 0.1 => "Minimal detail",
            < 0.25 => "Low detail",
            < 0.5 => "Moderate detail",
            _ => "High detail"
        };

    private static string GetSimpleColorName(string hex)
    {
        // Parse hex to RGB
        var r = Convert.ToByte(hex.Substring(1, 2), 16);
        var g = Convert.ToByte(hex.Substring(3, 2), 16);
        var b = Convert.ToByte(hex.Substring(5, 2), 16);

        // Simple color classification
        var brightness = (r + g + b) / 3.0;
        if (brightness < 50) return "Dark";
        if (brightness > 200) return "Light";

        // Check for grayscale
        var maxDiff = Math.Max(Math.Abs(r - g), Math.Max(Math.Abs(g - b), Math.Abs(r - b)));
        if (maxDiff < 30) return "Gray";

        // Find dominant channel
        if (r > g && r > b) return b > 100 ? "Pink" : "Red";
        if (g > r && g > b) return "Green";
        if (b > r && b > g) return r > 100 ? "Purple" : "Blue";
        if (r > b && g > b) return "Yellow/Orange";
        if (b > r && g > r) return "Cyan";

        return "Mixed";
    }
}
