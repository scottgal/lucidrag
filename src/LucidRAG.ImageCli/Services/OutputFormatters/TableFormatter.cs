using Mostlylucid.DocSummarizer.Images.Models;
using LucidRAG.ImageCli.Services.VisionClients;
using Spectre.Console;

namespace LucidRAG.ImageCli.Services.OutputFormatters;

/// <summary>
/// Formats image analysis results as Spectre.Console tables.
/// </summary>
public class TableFormatter : IOutputFormatter
{
    public string FormatSingle(string filePath, ImageProfile profile, string? llmCaption = null, string? extractedText = null, GifMotionProfile? gifMotion = null, List<EvidenceClaim>? evidenceClaims = null)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .Title($"[cyan]Image Analysis: {Path.GetFileName(filePath)}[/]");

        table.AddColumn("[cyan]Property[/]");
        table.AddColumn("[white]Value[/]");

        // Basic info
        table.AddRow("File Path", Markup.Escape(filePath));
        table.AddRow("Format", profile.Format);
        table.AddRow("Dimensions", $"{profile.Width}x{profile.Height} ({profile.AspectRatio:F2}:1)");
        table.AddRow("SHA256 (first 16)", profile.Sha256[..Math.Min(16, profile.Sha256.Length)]);

        // Type detection
        var confidence = $"{profile.TypeConfidence:P0}";
        table.AddRow("Detected Type", $"{profile.DetectedType} ([yellow]{confidence}[/])");

        // Visual metrics
        table.AddRow("Edge Density", $"{profile.EdgeDensity:F3}");
        table.AddRow("Luminance Entropy", $"{profile.LuminanceEntropy:F2}");
        table.AddRow("Mean Luminance", $"{profile.MeanLuminance:F1}");
        table.AddRow("Luminance Std Dev", $"{profile.LuminanceStdDev:F1}");

        // Sharpness
        var sharpnessDesc = profile.LaplacianVariance < 100 ? "Blurry" :
                           profile.LaplacianVariance < 500 ? "Soft" : "Sharp";
        table.AddRow("Sharpness", $"{profile.LaplacianVariance:F1} ([green]{sharpnessDesc}[/])");

        // Text likelihood
        var textLikely = profile.TextLikeliness > 0.4 ? "High" :
                        profile.TextLikeliness > 0.2 ? "Medium" : "Low";
        table.AddRow("Text Likeliness", $"{profile.TextLikeliness:F3} ([yellow]{textLikely}[/])");

        // Color analysis
        if (profile.DominantColors?.Any() == true)
        {
            var colors = string.Join(", ", profile.DominantColors.Take(3)
                .Select(c => $"{c.Name} ({c.Percentage:F1}%)"));
            table.AddRow("Dominant Colors", colors);
        }

        table.AddRow("Mean Saturation", $"{profile.MeanSaturation:F3}");
        table.AddRow("Grayscale", profile.IsMostlyGrayscale ? "[green]Yes[/]" : "[red]No[/]");

        // Perceptual hash
        if (!string.IsNullOrEmpty(profile.PerceptualHash))
        {
            table.AddRow("Perceptual Hash", profile.PerceptualHash);
        }

        // GIF motion data if available
        if (gifMotion != null)
        {
            table.AddEmptyRow();
            table.AddRow("[cyan]GIF/WebP Motion Analysis[/]", "");
            table.AddRow("  Frame Count", gifMotion.FrameCount.ToString());
            table.AddRow("  Frame Rate", $"{gifMotion.Fps:F1} fps ({gifMotion.FrameDelayMs}ms delay)");
            table.AddRow("  Duration", $"{gifMotion.TotalDurationMs}ms{(gifMotion.Loops ? " (loops)" : "")}");

            var motionIcon = gifMotion.MotionDirection switch
            {
                "right" => "→",
                "left" => "←",
                "up" => "↑",
                "down" => "↓",
                "up-right" => "↗",
                "up-left" => "↖",
                "down-right" => "↘",
                "down-left" => "↙",
                _ => "•"
            };

            var motionColor = gifMotion.MotionMagnitude > 10 ? "green" :
                            gifMotion.MotionMagnitude > 5 ? "yellow" : "dim";

            table.AddRow("  Motion Direction", $"[{motionColor}]{motionIcon} {gifMotion.MotionDirection}[/]");
            table.AddRow("  Motion Magnitude", $"{gifMotion.MotionMagnitude:F2} px/frame (max: {gifMotion.MaxMotionMagnitude:F2})");
            table.AddRow("  Motion Coverage", $"{gifMotion.MotionPercentage:F1}% of frames");
            table.AddRow("  Confidence", $"{gifMotion.Confidence:P0}");

            // Complexity metrics if available
            if (gifMotion.Complexity != null)
            {
                var complexity = gifMotion.Complexity;
                table.AddEmptyRow();
                table.AddRow("[cyan]Animation Complexity[/]", "");
                table.AddRow("  Animation Type", complexity.AnimationType);
                table.AddRow("  Overall Complexity", $"{complexity.OverallComplexity:F2} ({(complexity.OverallComplexity < 0.3 ? "Simple" : complexity.OverallComplexity < 0.6 ? "Moderate" : "Complex")})");
                table.AddRow("  Visual Stability", $"{complexity.VisualStability:F2} ({(complexity.VisualStability > 0.7 ? "Stable" : complexity.VisualStability > 0.4 ? "Moderate" : "Chaotic")})");
                table.AddRow("  Color Variation", $"{complexity.ColorVariation:F2}");
                table.AddRow("  Scene Changes", complexity.SceneChangeCount.ToString());
                table.AddRow("  Avg Frame Difference", $"{complexity.AverageFrameDifference:F3}");
            }
        }

        // LLM caption if available
        if (!string.IsNullOrEmpty(llmCaption))
        {
            table.AddEmptyRow();
            table.AddRow("[green]LLM Caption[/]", Markup.Escape(llmCaption));
        }

        // Evidence claims if available
        if (evidenceClaims != null && evidenceClaims.Count > 0)
        {
            table.AddEmptyRow();
            table.AddRow("[cyan]Evidence Claims[/]", $"{evidenceClaims.Count} claims with source tracking");

            foreach (var claim in evidenceClaims.Take(5)) // Show first 5
            {
                var sources = Markup.Escape(string.Join(",", claim.Sources));
                var evidenceText = claim.Evidence != null && claim.Evidence.Count > 0
                    ? $" [dim]{Markup.Escape(string.Join(", ", claim.Evidence.Take(2)))}[/]"
                    : "";
                table.AddRow($"  • [cyan]{sources}[/]", Markup.Escape(claim.Text) + evidenceText);
            }

            if (evidenceClaims.Count > 5)
            {
                table.AddRow("", $"[dim]... and {evidenceClaims.Count - 5} more claims[/]");
            }
        }

        // OCR text if available
        if (!string.IsNullOrEmpty(extractedText))
        {
            table.AddEmptyRow();
            var preview = extractedText.Length > 200 ?
                extractedText[..200] + "..." : extractedText;
            table.AddRow("[green]Extracted Text[/]", Markup.Escape(preview));
        }

        AnsiConsole.Write(table);
        return string.Empty; // Already written to console
    }

    public string FormatBatch(IEnumerable<ImageAnalysisResult> results)
    {
        var resultsList = results.ToList();
        var successCount = resultsList.Count(r => r.Profile != null);
        var errorCount = resultsList.Count - successCount;
        var escalatedCount = resultsList.Count(r => r.WasEscalated);

        // Summary table
        var summary = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .Title("[cyan]Batch Analysis Summary[/]");

        summary.AddColumn("[cyan]Metric[/]");
        summary.AddColumn("[white]Value[/]");

        summary.AddRow("Total Images", resultsList.Count.ToString());
        summary.AddRow("[green]Successful[/]", successCount.ToString());
        summary.AddRow("[red]Failed[/]", errorCount.ToString());
        summary.AddRow("[yellow]Escalated to LLM[/]", escalatedCount.ToString());

        if (successCount > 0)
        {
            var successful = resultsList.Where(r => r.Profile != null).ToList();

            // Type distribution
            var typeGroups = successful
                .GroupBy(r => r.Profile!.DetectedType)
                .OrderByDescending(g => g.Count());

            summary.AddEmptyRow();
            summary.AddRow("[cyan]Type Distribution[/]", "");
            foreach (var group in typeGroups)
            {
                var percentage = (group.Count() * 100.0 / successCount);
                summary.AddRow($"  {group.Key}", $"{group.Count()} ({percentage:F1}%)");
            }

            // GIF/WebP motion statistics
            var animatedImages = resultsList.Where(r => r.GifMotion != null).ToList();
            if (animatedImages.Any())
            {
                summary.AddEmptyRow();
                summary.AddRow("[cyan]Animated Images (GIF/WebP)[/]", "");
                summary.AddRow("  Count", animatedImages.Count.ToString());
                summary.AddRow("  Avg Motion Magnitude", animatedImages.Average(r => r.GifMotion!.MotionMagnitude).ToString("F2") + " px/frame");
                summary.AddRow("  Avg Frame Count", animatedImages.Average(r => r.GifMotion!.FrameCount).ToString("F0"));
                summary.AddRow("  Avg FPS", animatedImages.Average(r => r.GifMotion!.Fps).ToString("F1"));

                // Motion direction distribution
                var motionDirs = animatedImages
                    .GroupBy(r => r.GifMotion!.MotionDirection)
                    .OrderByDescending(g => g.Count())
                    .Take(3);
                foreach (var dir in motionDirs)
                {
                    summary.AddRow($"    {dir.Key}", dir.Count().ToString());
                }
            }

            // Average metrics
            summary.AddEmptyRow();
            summary.AddRow("[cyan]Average Metrics[/]", "");
            summary.AddRow("  Edge Density", successful.Average(r => r.Profile!.EdgeDensity).ToString("F3"));
            summary.AddRow("  Sharpness", successful.Average(r => r.Profile!.LaplacianVariance).ToString("F1"));
            summary.AddRow("  Text Likeliness", successful.Average(r => r.Profile!.TextLikeliness).ToString("F3"));
            summary.AddRow("  Mean Saturation", successful.Average(r => r.Profile!.MeanSaturation).ToString("F3"));
        }

        AnsiConsole.Write(summary);

        // Detailed results table
        if (resultsList.Any())
        {
            AnsiConsole.WriteLine();
            var details = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .Title("[cyan]Detailed Results[/]");

            details.AddColumn("File");
            details.AddColumn("Type");
            details.AddColumn("Dimensions");
            details.AddColumn("Sharpness");
            details.AddColumn("Text Score");
            details.AddColumn("Motion");
            details.AddColumn("Status");

            foreach (var result in resultsList.Take(50)) // Limit to 50 for readability
            {
                if (result.Profile != null)
                {
                    var status = result.WasEscalated ? "[yellow]✓ LLM[/]" : "[green]✓[/]";

                    // Motion indicator
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

                        var color = result.GifMotion.MotionMagnitude > 10 ? "green" :
                                   result.GifMotion.MotionMagnitude > 5 ? "yellow" : "dim";

                        motionDisplay = $"[{color}]{icon} {result.GifMotion.MotionMagnitude:F1}[/]";
                    }

                    details.AddRow(
                        Markup.Escape(Path.GetFileName(result.FilePath)),
                        result.Profile.DetectedType.ToString(),
                        $"{result.Profile.Width}x{result.Profile.Height}",
                        result.Profile.LaplacianVariance.ToString("F0"),
                        result.Profile.TextLikeliness.ToString("F2"),
                        motionDisplay,
                        status
                    );
                }
                else
                {
                    details.AddRow(
                        Markup.Escape(Path.GetFileName(result.FilePath)),
                        "[red]ERROR[/]",
                        "-",
                        "-",
                        "-",
                        "-",
                        $"[red]✗ {Markup.Escape(result.Error ?? "Unknown error")}[/]"
                    );
                }
            }

            if (resultsList.Count > 50)
            {
                details.Caption($"[dim]Showing first 50 of {resultsList.Count} results[/]");
            }

            AnsiConsole.Write(details);
        }

        return string.Empty; // Already written to console
    }

    public async Task WriteAsync(string content, string? outputPath = null)
    {
        if (!string.IsNullOrEmpty(outputPath))
        {
            await File.WriteAllTextAsync(outputPath, content);
            AnsiConsole.MarkupLine($"[green]✓[/] Output saved to: {Markup.Escape(outputPath)}");
        }
    }
}
