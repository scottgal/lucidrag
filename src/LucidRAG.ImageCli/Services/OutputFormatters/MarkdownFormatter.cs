using System.Text;
using Mostlylucid.DocSummarizer.Images.Models;
using LucidRAG.ImageCli.Services.VisionClients;

namespace LucidRAG.ImageCli.Services.OutputFormatters;

/// <summary>
/// Formats image analysis results as detailed Markdown reports.
/// </summary>
public class MarkdownFormatter : IOutputFormatter
{
    public string FormatSingle(string filePath, ImageProfile profile, string? llmCaption = null, string? extractedText = null, GifMotionProfile? gifMotion = null, List<EvidenceClaim>? evidenceClaims = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Image Analysis Report");
        sb.AppendLine($"**Generated:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        // Summary
        sb.AppendLine("## Summary");
        sb.AppendLine($"- **File:** `{Path.GetFileName(filePath)}`");
        sb.AppendLine($"- **Full Path:** `{filePath}`");
        sb.AppendLine($"- **Type:** {profile.DetectedType} ({profile.TypeConfidence:P0} confidence)");
        sb.AppendLine($"- **Format:** {profile.Format}");
        sb.AppendLine($"- **Dimensions:** {profile.Width}x{profile.Height} " +
                     $"({profile.AspectRatio:F2}:1 aspect ratio)");
        sb.AppendLine();

        // LLM Caption
        if (!string.IsNullOrEmpty(llmCaption))
        {
            sb.AppendLine("## LLM Description");
            sb.AppendLine($"> {llmCaption}");
            sb.AppendLine();
        }

        // Evidence Claims
        if (evidenceClaims != null && evidenceClaims.Count > 0)
        {
            sb.AppendLine("##Evidence-Based Claims");
            sb.AppendLine();
            foreach (var claim in evidenceClaims)
            {
                var sources = string.Join(", ", claim.Sources);
                sb.AppendLine($"- **[{sources}]** {claim.Text}");
                if (claim.Evidence != null && claim.Evidence.Count > 0)
                {
                    foreach (var evidence in claim.Evidence)
                    {
                        sb.AppendLine($"  - _{evidence}_");
                    }
                }
            }
            sb.AppendLine();
        }

        // Visual Characteristics
        sb.AppendLine("## Visual Characteristics");
        sb.AppendLine($"- **Brightness:** {CategorizeValue(profile.MeanLuminance, 85, 170, "Dark", "Normal", "Bright")} " +
                     $"(mean luminance: {profile.MeanLuminance:F1})");
        sb.AppendLine($"- **Contrast:** {CategorizeValue(profile.LuminanceStdDev, 30, 60, "Low", "Medium", "High")} " +
                     $"(std dev: {profile.LuminanceStdDev:F1})");
        sb.AppendLine($"- **Sharpness:** {CategorizeSharpness(profile.LaplacianVariance)} " +
                     $"(Laplacian variance: {profile.LaplacianVariance:F1})");
        sb.AppendLine($"- **Edge Density:** {profile.EdgeDensity:F3} " +
                     $"({CategorizeValue(profile.EdgeDensity, 0.2, 0.5, "Low detail", "Moderate detail", "High detail")})");
        sb.AppendLine($"- **Luminance Entropy:** {profile.LuminanceEntropy:F2}");
        sb.AppendLine($"- **Clipped Blacks:** {profile.ClippedBlacksPercent:F2}%");
        sb.AppendLine($"- **Clipped Whites:** {profile.ClippedWhitesPercent:F2}%");
        sb.AppendLine();

        // Text Detection
        sb.AppendLine("## Text Detection");
        var textCategory = profile.TextLikeliness > 0.4 ? "High" :
                          profile.TextLikeliness > 0.2 ? "Medium" : "Low";
        sb.AppendLine($"- **Text Likeliness:** {profile.TextLikeliness:F3} ({textCategory})");

        if (!string.IsNullOrEmpty(extractedText))
        {
            sb.AppendLine($"- **Extracted Text ({extractedText.Length} characters):**");
            sb.AppendLine("```");
            sb.AppendLine(extractedText.Length > 500 ?
                extractedText[..500] + "\n... (truncated)" : extractedText);
            sb.AppendLine("```");
        }
        sb.AppendLine();

        // Color Analysis
        sb.AppendLine("## Color Analysis");
        if (profile.DominantColors?.Any() == true)
        {
            sb.AppendLine("**Dominant Colors:**");
            for (int i = 0; i < Math.Min(5, profile.DominantColors.Count); i++)
            {
                var color = profile.DominantColors[i];
                sb.AppendLine($"{i + 1}. {color.Name} (`{color.Hex}`) - {color.Percentage:F1}%");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"- **Mean Saturation:** {profile.MeanSaturation:F3}");
        sb.AppendLine($"- **Mostly Grayscale:** {(profile.IsMostlyGrayscale ? "Yes" : "No")}");

        // Color Grid
        if (profile.ColorGrid?.Cells?.Any() == true)
        {
            sb.AppendLine();
            sb.AppendLine("**Color Distribution Grid:**");
            sb.AppendLine();
            sb.AppendLine("| Position | Color | Hex | Coverage |");
            sb.AppendLine("|----------|-------|-----|----------|");

            foreach (var cell in profile.ColorGrid.Cells.Take(9))
            {
                sb.AppendLine($"| ({cell.Row},{cell.Col}) | {FindColorName(cell.Hex)} | `{cell.Hex}` | {cell.Coverage:F1}% |");
            }
        }
        sb.AppendLine();

        // GIF/WebP Motion Analysis
        if (gifMotion != null)
        {
            sb.AppendLine("## GIF/WebP Motion Analysis");
            sb.AppendLine($"- **Frame Count:** {gifMotion.FrameCount}");
            sb.AppendLine($"- **Frame Rate:** {gifMotion.Fps:F1} fps ({gifMotion.FrameDelayMs}ms delay)");
            sb.AppendLine($"- **Duration:** {gifMotion.TotalDurationMs}ms{(gifMotion.Loops ? " (loops)" : "")}");
            sb.AppendLine($"- **Motion Direction:** {gifMotion.MotionDirection}");
            sb.AppendLine($"- **Motion Magnitude:** {gifMotion.MotionMagnitude:F2} px/frame (max: {gifMotion.MaxMotionMagnitude:F2})");
            sb.AppendLine($"- **Motion Coverage:** {gifMotion.MotionPercentage:F1}% of frames");
            sb.AppendLine($"- **Confidence:** {gifMotion.Confidence:P0}");

            if (gifMotion.Complexity != null)
            {
                sb.AppendLine();
                sb.AppendLine("### Animation Complexity");
                sb.AppendLine($"- **Animation Type:** {gifMotion.Complexity.AnimationType}");
                sb.AppendLine($"- **Overall Complexity:** {gifMotion.Complexity.OverallComplexity:F2}");
                sb.AppendLine($"- **Visual Stability:** {gifMotion.Complexity.VisualStability:F2}");
                sb.AppendLine($"- **Color Variation:** {gifMotion.Complexity.ColorVariation:F2}");
                sb.AppendLine($"- **Entropy Variation:** {gifMotion.Complexity.EntropyVariation:F2}");
                sb.AppendLine($"- **Scene Changes:** {gifMotion.Complexity.SceneChangeCount}");
                sb.AppendLine($"- **Avg Frame Difference:** {gifMotion.Complexity.AverageFrameDifference:F3}");
            }

            sb.AppendLine();
        }

        // Technical Details
        sb.AppendLine("## Technical Details");
        sb.AppendLine($"- **SHA256:** `{profile.Sha256[..Math.Min(32, profile.Sha256.Length)]}`");
        sb.AppendLine($"- **Perceptual Hash:** `{profile.PerceptualHash ?? "N/A"}`");
        sb.AppendLine($"- **Has EXIF:** {(profile.HasExif ? "Yes" : "No")}");
        sb.AppendLine($"- **Compression Artifacts:** {profile.CompressionArtifacts:F3}");
        sb.AppendLine();

        // Salient Regions
        if (profile.SalientRegions?.Any() == true)
        {
            sb.AppendLine("## Salient Regions");
            foreach (var region in profile.SalientRegions.Take(5))
            {
                sb.AppendLine($"- Region at ({region.X}, {region.Y}), " +
                             $"size {region.Width}x{region.Height}, score: {region.Score:F3}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine($"*Generated by LucidRAG Image CLI at {DateTime.Now:yyyy-MM-dd HH:mm:ss}*");

        return sb.ToString();
    }

    public string FormatBatch(IEnumerable<ImageAnalysisResult> results)
    {
        var resultsList = results.ToList();
        var sb = new StringBuilder();

        sb.AppendLine("# Batch Image Analysis Report");
        sb.AppendLine($"**Generated:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        // Summary Statistics
        sb.AppendLine("## Summary");
        var successCount = resultsList.Count(r => r.Profile != null);
        var errorCount = resultsList.Count - successCount;
        var escalatedCount = resultsList.Count(r => r.WasEscalated);

        sb.AppendLine($"- **Total Images:** {resultsList.Count}");
        sb.AppendLine($"- **Successful:** {successCount}");
        sb.AppendLine($"- **Failed:** {errorCount}");
        sb.AppendLine($"- **Escalated to LLM:** {escalatedCount}");
        sb.AppendLine();

        if (successCount > 0)
        {
            var successful = resultsList.Where(r => r.Profile != null).ToList();

            // Type Distribution
            sb.AppendLine("## Type Distribution");
            var typeGroups = successful
                .GroupBy(r => r.Profile!.DetectedType)
                .OrderByDescending(g => g.Count());

            foreach (var group in typeGroups)
            {
                var percentage = (group.Count() * 100.0 / successCount);
                sb.AppendLine($"- **{group.Key}:** {group.Count()} ({percentage:F1}%)");
            }
            sb.AppendLine();

            // GIF/WebP Motion Statistics
            var animatedImages = resultsList.Where(r => r.GifMotion != null).ToList();
            if (animatedImages.Any())
            {
                sb.AppendLine("## Animated Images (GIF/WebP)");
                sb.AppendLine($"- **Count:** {animatedImages.Count}");
                sb.AppendLine($"- **Avg Motion Magnitude:** {animatedImages.Average(r => r.GifMotion!.MotionMagnitude):F2} px/frame");
                sb.AppendLine($"- **Avg Frame Count:** {animatedImages.Average(r => r.GifMotion!.FrameCount):F0}");
                sb.AppendLine($"- **Avg FPS:** {animatedImages.Average(r => r.GifMotion!.Fps):F1}");

                var motionDirs = animatedImages
                    .GroupBy(r => r.GifMotion!.MotionDirection)
                    .OrderByDescending(g => g.Count())
                    .Take(3);

                sb.AppendLine("- **Motion Directions:**");
                foreach (var dir in motionDirs)
                {
                    sb.AppendLine($"  - {dir.Key}: {dir.Count()}");
                }
                sb.AppendLine();
            }

            // Average Metrics
            sb.AppendLine("## Average Metrics");
            sb.AppendLine($"- **Edge Density:** {successful.Average(r => r.Profile!.EdgeDensity):F3}");
            sb.AppendLine($"- **Sharpness:** {successful.Average(r => r.Profile!.LaplacianVariance):F1}");
            sb.AppendLine($"- **Text Likeliness:** {successful.Average(r => r.Profile!.TextLikeliness):F3}");
            sb.AppendLine($"- **Mean Saturation:** {successful.Average(r => r.Profile!.MeanSaturation):F3}");
            sb.AppendLine($"- **Grayscale Images:** {successful.Count(r => r.Profile!.IsMostlyGrayscale)} " +
                         $"({successful.Count(r => r.Profile!.IsMostlyGrayscale) * 100.0 / successCount:F1}%)");
            sb.AppendLine();
        }

        // Detailed Results
        sb.AppendLine("## Detailed Results");
        sb.AppendLine();
        sb.AppendLine("| File | Type | Dimensions | Sharpness | Text Score | Motion | Status |");
        sb.AppendLine("|------|------|------------|-----------|------------|--------|--------|");

        foreach (var result in resultsList)
        {
            if (result.Profile != null)
            {
                var status = result.WasEscalated ? "✓ LLM" : "✓";

                var motionDisplay = "-";
                if (result.GifMotion != null)
                {
                    var icon = result.GifMotion.MotionDirection switch
                    {
                        "right" => "→",
                        "left" => "←",
                        "up" => "↑",
                        "down" => "↓",
                        "up-right" => "↗",
                        "up-left" => "↖",
                        "down-right" => "↘",
                        "down-left" => "↙",
                        "static" => "•",
                        _ => "?"
                    };
                    motionDisplay = $"{icon} {result.GifMotion.MotionMagnitude:F1}px";
                }

                sb.AppendLine($"| `{Path.GetFileName(result.FilePath)}` | " +
                             $"{result.Profile.DetectedType} | " +
                             $"{result.Profile.Width}x{result.Profile.Height} | " +
                             $"{result.Profile.LaplacianVariance:F0} | " +
                             $"{result.Profile.TextLikeliness:F2} | " +
                             $"{motionDisplay} | " +
                             $"{status} |");
            }
            else
            {
                sb.AppendLine($"| `{Path.GetFileName(result.FilePath)}` | " +
                             $"ERROR | - | - | - | - | ✗ {result.Error} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine($"*Generated by LucidRAG Image CLI at {DateTime.Now:yyyy-MM-dd HH:mm:ss}*");

        return sb.ToString();
    }

    public async Task WriteAsync(string content, string? outputPath = null)
    {
        if (!string.IsNullOrEmpty(outputPath))
        {
            await File.WriteAllTextAsync(outputPath, content);
            Console.WriteLine($"Output saved to: {outputPath}");
        }
        else
        {
            Console.WriteLine(content);
        }
    }

    private static string CategorizeValue(double value, double lowThreshold, double highThreshold,
        string low, string medium, string high)
    {
        return value < lowThreshold ? low :
               value < highThreshold ? medium : high;
    }

    private static string CategorizeSharpness(double laplacianVariance)
    {
        return laplacianVariance switch
        {
            < 100 => "Very Blurry",
            < 300 => "Blurry",
            < 500 => "Soft",
            < 1000 => "Sharp",
            _ => "Very Sharp"
        };
    }

    private static string FindColorName(string hex)
    {
        // Simple color name approximation based on RGB values
        if (hex.Length < 7) return "Unknown";

        var r = Convert.ToInt32(hex.Substring(1, 2), 16);
        var g = Convert.ToInt32(hex.Substring(3, 2), 16);
        var b = Convert.ToInt32(hex.Substring(5, 2), 16);

        var avg = (r + g + b) / 3.0;

        if (Math.Abs(r - g) < 30 && Math.Abs(g - b) < 30 && Math.Abs(r - b) < 30)
        {
            return avg switch
            {
                < 50 => "Black",
                < 100 => "Dark Gray",
                < 155 => "Gray",
                < 200 => "Light Gray",
                _ => "White"
            };
        }

        if (r > g && r > b) return "Red";
        if (g > r && g > b) return "Green";
        if (b > r && b > g) return "Blue";
        if (r > 200 && g > 200 && b < 100) return "Yellow";
        if (r > 200 && g < 100 && b > 200) return "Magenta";
        if (r < 100 && g > 200 && b > 200) return "Cyan";

        return "Mixed";
    }
}
