using System.CommandLine;
using System.CommandLine.Parsing;
using LucidRAG.ImageCli.Services;
using Mostlylucid.DocSummarizer.Images.Services;
using Mostlylucid.DocSummarizer.Images.Services.Vision;
using LucidRAG.ImageCli.Services.OutputFormatters;
using Microsoft.Extensions.Configuration;
using Mostlylucid.DocSummarizer.Images.Models;
using Spectre.Console;
using EscalationResult = Mostlylucid.DocSummarizer.Images.Services.EscalationResult;
using ImageFilter = Mostlylucid.DocSummarizer.Images.Services.Vision.ImageFilter;

namespace LucidRAG.ImageCli.Commands;

/// <summary>
/// Command for batch processing images in a directory.
/// </summary>
public static class BatchCommand
{
    private static readonly Argument<string> DirectoryArg = new("directory") { Description = "Directory containing images to process" };

    private static readonly Option<string> PatternOpt = new("--pattern", "-p") { Description = "Glob pattern for filtering files (e.g., **/*.jpg, photos/**/*.png)", DefaultValueFactory = _ => "**/*" };

    private static readonly Option<bool> RecursiveOpt = new("--recursive", "-r") { Description = "Process subdirectories recursively", DefaultValueFactory = _ => true };

    private static readonly Option<int> MaxParallelOpt = new("--max-parallel") { Description = "Maximum parallel workers (0 = auto-detect based on CPU cores)", DefaultValueFactory = _ => 0 };

    private static readonly Option<OutputFormat> FormatOpt = new("--format", "-f") { Description = "Output format", DefaultValueFactory = _ => OutputFormat.Table };

    private static readonly Option<string?> OutputOpt = new("--output", "-o") { Description = "Save output to file" };

    private static readonly Option<string?> ExportCsvOpt = new("--export-csv") { Description = "Export summary as CSV" };

    private static readonly Option<string?> ExportJsonLdOpt = new("--export-jsonld") { Description = "Export fingerprints as JSON-LD for inspection" };

    private static readonly Option<ImageType?> FilterTypeOpt = new("--filter-type") { Description = "Filter by detected image type" };

    private static readonly Option<double?> MinTextScoreOpt = new("--min-text-score") { Description = "Minimum text-likeliness score (0.0-1.0)" };

    private static readonly Option<double?> MinSharpnessOpt = new("--min-sharpness") { Description = "Minimum sharpness (Laplacian variance)" };

    private static readonly Option<string?> FilterQueryOpt = new("--filter", "-q") { Description = "Natural language filter query (e.g., 'country:UK resolution:high has_text:true')" };

    private static readonly Option<bool> EnableEscalationOpt = new("--enable-escalation") { Description = "Enable auto-escalation to vision LLM for uncertain cases", DefaultValueFactory = _ => false };

    private static readonly Option<bool> IncludeOcrOpt = new("--include-ocr") { Description = "Extract text using OCR for images with text", DefaultValueFactory = _ => false };

    private static readonly Option<bool> ProgressOpt = new("--progress") { Description = "Show progress bars", DefaultValueFactory = _ => true };

    private static readonly Option<string?> OrderByOpt = new("--order-by") { Description = "Sort results by property: color, resolution, sharpness, brightness, saturation, type, text-score" };

    private static readonly Option<bool> DescendingOpt = new("--descending") { Description = "Sort in descending order", DefaultValueFactory = _ => false };

    public static Command Create()
    {
        var command = new Command("batch", "Batch process images in a directory");
        command.Arguments.Add(DirectoryArg);
        command.Options.Add(PatternOpt);
        command.Options.Add(RecursiveOpt);
        command.Options.Add(MaxParallelOpt);
        command.Options.Add(FormatOpt);
        command.Options.Add(OutputOpt);
        command.Options.Add(ExportCsvOpt);
        command.Options.Add(ExportJsonLdOpt);
        command.Options.Add(FilterTypeOpt);
        command.Options.Add(MinTextScoreOpt);
        command.Options.Add(MinSharpnessOpt);
        command.Options.Add(FilterQueryOpt);
        command.Options.Add(EnableEscalationOpt);
        command.Options.Add(IncludeOcrOpt);
        command.Options.Add(ProgressOpt);
        command.Options.Add(OrderByOpt);
        command.Options.Add(DescendingOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var directory = parseResult.GetValue(DirectoryArg)!;
            var pattern = parseResult.GetValue(PatternOpt)!;
            var recursive = parseResult.GetValue(RecursiveOpt);
            var maxParallel = parseResult.GetValue(MaxParallelOpt);
            var format = parseResult.GetValue(FormatOpt);
            var output = parseResult.GetValue(OutputOpt);
            var exportCsv = parseResult.GetValue(ExportCsvOpt);
            var exportJsonLd = parseResult.GetValue(ExportJsonLdOpt);
            var filterType = parseResult.GetValue(FilterTypeOpt);
            var minTextScore = parseResult.GetValue(MinTextScoreOpt);
            var minSharpness = parseResult.GetValue(MinSharpnessOpt);
            var filterQuery = parseResult.GetValue(FilterQueryOpt);
            var enableEscalation = parseResult.GetValue(EnableEscalationOpt);
            var includeOcr = parseResult.GetValue(IncludeOcrOpt);
            var showProgress = parseResult.GetValue(ProgressOpt);
            var orderBy = parseResult.GetValue(OrderByOpt);
            var descending = parseResult.GetValue(DescendingOpt);

            // Validate directory
            if (!Directory.Exists(directory))
            {
                AnsiConsole.MarkupLine($"[red]✗ Error:[/] Directory not found: {Markup.Escape(directory)}");
                return 1;
            }

            // Auto-detect max parallel workers if not specified
            if (maxParallel <= 0)
            {
                maxParallel = Math.Max(1, Environment.ProcessorCount / 2);
                AnsiConsole.MarkupLine($"[dim]Auto-detected {maxParallel} parallel workers[/]");
            }

            // Build service provider
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .Build();

            var services = Program.BuildServiceProvider(configuration);

            // Get services
            var batchProcessor = services.GetRequiredService<ImageBatchProcessor>();
            var visionLlmService = services.GetRequiredService<VisionLlmService>();

            IOutputFormatter formatter = format switch
            {
                OutputFormat.Json => services.GetRequiredService<JsonFormatter>(),
                OutputFormat.Markdown => services.GetRequiredService<MarkdownFormatter>(),
                _ => services.GetRequiredService<TableFormatter>()
            };

            try
            {
                // Check if escalation is available
                if (enableEscalation)
                {
                    var (available, message) = await visionLlmService.CheckAvailabilityAsync(ct);
                    if (!available)
                    {
                        AnsiConsole.MarkupLine($"[yellow]⚠ Warning:[/] {Markup.Escape(message ?? "Ollama not available")}");
                        AnsiConsole.MarkupLine("[dim]Escalation disabled, continuing with deterministic analysis...[/]");
                        enableEscalation = false;
                    }
                }

                // Process natural language filter query if provided
                Func<EscalationResult, bool>? filter = null;
                if (!string.IsNullOrWhiteSpace(filterQuery))
                {
                    var decomposition = await visionLlmService.DecomposeFilterQueryAsync(filterQuery, ct);
                    filter = result => ApplyFilters(result, decomposition.Filters);

                    AnsiConsole.MarkupLine($"[cyan]ℹ[/] Applying {decomposition.Filters.Count} filters from query");
                }
                else if (filterType != null || minTextScore != null || minSharpness != null)
                {
                    filter = result => ApplySimpleFilters(result, filterType, minTextScore, minSharpness);
                }

                // Process batch with progress
                BatchProcessingResult? batchResult = null;

                if (showProgress)
                {
                    await AnsiConsole.Progress()
                        .AutoClear(false)
                        .HideCompleted(false)
                        .Columns(
                            new TaskDescriptionColumn(),
                            new ProgressBarColumn(),
                            new PercentageColumn(),
                            new RemainingTimeColumn(),
                            new SpinnerColumn())
                        .StartAsync(async ctx =>
                        {
                            var tasks = new List<ProgressTask>();
                            for (int i = 0; i < maxParallel; i++)
                            {
                                tasks.Add(ctx.AddTask($"[cyan]Worker {i + 1}[/]"));
                            }

                            var progress = new Progress<BatchProgress>(p =>
                            {
                                var task = tasks[p.WorkerId];
                                task.Description = $"[cyan]Worker {p.WorkerId + 1}:[/] {Markup.Escape(Path.GetFileName(p.FilePath))}";

                                if (p.Total > 0)
                                {
                                    task.MaxValue = p.Total;
                                    task.Value = p.Processed;
                                }
                                else
                                {
                                    task.Increment(1);
                                }

                                if (p.Success)
                                {
                                    task.Description += " [green]✓[/]";
                                }
                                else if (p.Error != null)
                                {
                                    task.Description += $" [red]✗ {Markup.Escape(p.Error)}[/]";
                                }
                            });

                            batchResult = await batchProcessor.ProcessBatchAsync(
                                directory,
                                pattern,
                                recursive,
                                maxParallel,
                                enableEscalation,
                                includeOcr,
                                filter,
                                progress,
                                ct);
                        });
                }
                else
                {
                    batchResult = await batchProcessor.ProcessBatchAsync(
                        directory,
                        pattern,
                        recursive,
                        maxParallel,
                        enableEscalation,
                        includeOcr,
                        filter,
                        null,
                        ct);
                }

                if (batchResult == null)
                {
                    AnsiConsole.MarkupLine("[red]✗ Batch processing failed[/]");
                    return 1;
                }

                // Sort results if requested
                if (!string.IsNullOrWhiteSpace(orderBy))
                {
                    batchResult = SortResults(batchResult, orderBy, descending);
                    AnsiConsole.MarkupLine($"[dim]Sorted by {orderBy} ({(descending ? "descending" : "ascending")})[/]");
                }

                // Export CSV if requested
                if (!string.IsNullOrEmpty(exportCsv))
                {
                    await batchProcessor.ExportToCsvAsync(batchResult, exportCsv, ct);
                }

                // Export JSON-LD if requested
                if (!string.IsNullOrEmpty(exportJsonLd))
                {
                    await ExportToJsonLd(batchResult, exportJsonLd, ct);
                    AnsiConsole.MarkupLine($"[green]✓[/] JSON-LD exported to: [cyan]{Markup.Escape(exportJsonLd)}[/]");
                }

                // Display results
                AnsiConsole.WriteLine();
                var formattedOutput = formatter.FormatBatch(batchResult.Results);
                await formatter.WriteAsync(formattedOutput, output);

                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Error:[/] {Markup.Escape(ex.Message)}");
                AnsiConsole.WriteException(ex);
                return 1;
            }
        });

        return command;
    }

    private static bool ApplySimpleFilters(
        EscalationResult result,
        ImageType? filterType,
        double? minTextScore,
        double? minSharpness)
    {
        if (result.Profile == null)
            return false;

        if (filterType != null && result.Profile.DetectedType != filterType.Value)
            return false;

        if (minTextScore != null && result.Profile.TextLikeliness < minTextScore.Value)
            return false;

        if (minSharpness != null && result.Profile.LaplacianVariance < minSharpness.Value)
            return false;

        return true;
    }

    private static bool ApplyFilters(EscalationResult result, List<ImageFilter> filters)
    {
        if (result.Profile == null)
            return false;

        foreach (var filter in filters)
        {
            if (!ApplySingleFilter(result.Profile, filter))
                return false;
        }

        return true;
    }

    private static bool ApplySingleFilter(ImageProfile profile, ImageFilter filter)
    {
        return filter.Property.ToLowerInvariant() switch
        {
            "type" => profile.DetectedType.ToString().Equals(filter.Value, StringComparison.OrdinalIgnoreCase),
            "resolution" => MatchResolution(profile, filter.Value),
            "text_score" => CompareNumeric(profile.TextLikeliness, filter.Operator, double.Parse(filter.Value)),
            "sharpness" => MatchSharpness(profile, filter.Value),
            "is_grayscale" => profile.IsMostlyGrayscale == bool.Parse(filter.Value),
            "has_text" => (profile.TextLikeliness > 0.4) == bool.Parse(filter.Value),
            "orientation" => MatchOrientation(profile, filter.Value),
            _ => true // Unknown filter, ignore
        };
    }

    private static bool MatchResolution(ImageProfile profile, string value)
    {
        return value.ToLowerInvariant() switch
        {
            "low" => profile.Width < 1280 && profile.Height < 720,
            "medium" or "hd" => profile.Width >= 1280 && profile.Width < 1920,
            "high" or "fullhd" => profile.Width >= 1920 && profile.Width < 3840,
            "4k" or "uhd" => profile.Width >= 3840 && profile.Width < 7680,
            "8k" => profile.Width >= 7680,
            _ => true
        };
    }

    private static bool MatchSharpness(ImageProfile profile, string value)
    {
        return value.ToLowerInvariant() switch
        {
            "blurry" => profile.LaplacianVariance < 300,
            "soft" => profile.LaplacianVariance >= 300 && profile.LaplacianVariance < 1000,
            "sharp" => profile.LaplacianVariance >= 1000,
            _ => true
        };
    }

    private static bool MatchOrientation(ImageProfile profile, string value)
    {
        var aspectRatio = profile.AspectRatio;
        return value.ToLowerInvariant() switch
        {
            "portrait" => aspectRatio < 0.95,
            "landscape" => aspectRatio > 1.05,
            "square" => aspectRatio >= 0.95 && aspectRatio <= 1.05,
            _ => true
        };
    }

    private static bool CompareNumeric(double actual, string op, double expected)
    {
        return op switch
        {
            "equals" => Math.Abs(actual - expected) < 0.01,
            "greater_than" => actual > expected,
            "less_than" => actual < expected,
            _ => true
        };
    }

    private static BatchProcessingResult SortResults(BatchProcessingResult batchResult, string orderBy, bool descending)
    {
        var sortedResults = orderBy.ToLowerInvariant() switch
        {
            "color" or "colour" => SortByColor(batchResult.Results, descending),
            "resolution" => SortByResolution(batchResult.Results, descending),
            "sharpness" => SortBySharpness(batchResult.Results, descending),
            "brightness" => SortByBrightness(batchResult.Results, descending),
            "saturation" => SortBySaturation(batchResult.Results, descending),
            "type" => SortByType(batchResult.Results, descending),
            "text-score" or "text" => SortByTextScore(batchResult.Results, descending),
            _ => batchResult.Results // Unknown sort, return as-is
        };

        return new BatchProcessingResult(
            sortedResults,
            batchResult.Directory,
            batchResult.Pattern);
    }

    private static List<ImageAnalysisResult> SortByColor(List<ImageAnalysisResult> results, bool descending)
    {
        // Sort by dominant color name (alphabetically)
        var sorted = results.OrderBy(r => r.Profile?.DominantColors.FirstOrDefault()?.Name ?? "zzz");
        return (descending ? sorted.Reverse() : sorted).ToList();
    }

    private static List<ImageAnalysisResult> SortByResolution(List<ImageAnalysisResult> results, bool descending)
    {
        var sorted = results.OrderBy(r => (r.Profile?.Width ?? 0) * (r.Profile?.Height ?? 0));
        return (descending ? sorted.Reverse() : sorted).ToList();
    }

    private static List<ImageAnalysisResult> SortBySharpness(List<ImageAnalysisResult> results, bool descending)
    {
        var sorted = results.OrderBy(r => r.Profile?.LaplacianVariance ?? 0);
        return (descending ? sorted.Reverse() : sorted).ToList();
    }

    private static List<ImageAnalysisResult> SortByBrightness(List<ImageAnalysisResult> results, bool descending)
    {
        var sorted = results.OrderBy(r => r.Profile?.MeanLuminance ?? 0);
        return (descending ? sorted.Reverse() : sorted).ToList();
    }

    private static List<ImageAnalysisResult> SortBySaturation(List<ImageAnalysisResult> results, bool descending)
    {
        var sorted = results.OrderBy(r => r.Profile?.MeanSaturation ?? 0);
        return (descending ? sorted.Reverse() : sorted).ToList();
    }

    private static List<ImageAnalysisResult> SortByType(List<ImageAnalysisResult> results, bool descending)
    {
        var sorted = results.OrderBy(r => r.Profile?.DetectedType.ToString() ?? "Unknown");
        return (descending ? sorted.Reverse() : sorted).ToList();
    }

    private static List<ImageAnalysisResult> SortByTextScore(List<ImageAnalysisResult> results, bool descending)
    {
        var sorted = results.OrderBy(r => r.Profile?.TextLikeliness ?? 0);
        return (descending ? sorted.Reverse() : sorted).ToList();
    }

    /// <summary>
    /// Export batch results to JSON-LD format for semantic web compatibility.
    /// </summary>
    private static async Task ExportToJsonLd(BatchProcessingResult batchResult, string outputPath, CancellationToken ct)
    {
        var jsonLd = new
        {
            @context = new
            {
                schema = "https://schema.org/",
                lucidrag = "https://lucidrag.dev/schema/",
                ImageFingerprint = "lucidrag:ImageFingerprint",
                sha256 = "lucidrag:sha256Hash",
                format = "schema:encodingFormat",
                width = "schema:width",
                height = "schema:height",
                contentUrl = "schema:contentUrl",
                dateAnalyzed = "schema:dateCreated",
                detectedType = "lucidrag:detectedType",
                typeConfidence = "lucidrag:typeConfidence",
                sharpness = "lucidrag:laplacianVariance",
                edgeDensity = "lucidrag:edgeDensity",
                textLikeliness = "lucidrag:textLikeliness",
                meanLuminance = "lucidrag:meanLuminance",
                meanSaturation = "lucidrag:meanSaturation",
                dominantColors = "lucidrag:dominantColors",
                llmCaption = "schema:caption",
                extractedText = "schema:text"
            },
            @type = "lucidrag:ImageFingerprintCollection",
            dateGenerated = DateTime.UtcNow.ToString("O"),
            directory = batchResult.Directory,
            pattern = batchResult.Pattern,
            totalImages = batchResult.Results.Count,
            fingerprints = batchResult.Results.Select(r => new
            {
                @type = "ImageFingerprint",
                contentUrl = r.FilePath,
                sha256 = r.Profile?.Sha256,
                format = r.Profile?.Format,
                width = r.Profile?.Width,
                height = r.Profile?.Height,
                dateAnalyzed = DateTime.UtcNow.ToString("O"),
                detectedType = r.Profile?.DetectedType.ToString(),
                typeConfidence = r.Profile?.TypeConfidence,
                sharpness = r.Profile?.LaplacianVariance,
                edgeDensity = r.Profile?.EdgeDensity,
                textLikeliness = r.Profile?.TextLikeliness,
                meanLuminance = r.Profile?.MeanLuminance,
                meanSaturation = r.Profile?.MeanSaturation,
                isMostlyGrayscale = r.Profile?.IsMostlyGrayscale,
                dominantColors = r.Profile?.DominantColors.Select(c => new
                {
                    @type = "lucidrag:DominantColor",
                    hex = c.Hex,
                    percentage = c.Percentage,
                    name = c.Name
                }).ToList(),
                llmCaption = r.LlmCaption,
                extractedText = r.ExtractedText,
                wasEscalated = r.WasEscalated
            }).ToList()
        };

        var json = System.Text.Json.JsonSerializer.Serialize(jsonLd, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        await File.WriteAllTextAsync(outputPath, json, ct);
    }
}

file static class ServiceProviderExtensions
{
    public static T GetRequiredService<T>(this IServiceProvider services) where T : notnull
    {
        return (T)(services.GetService(typeof(T)) ??
            throw new InvalidOperationException($"Service of type {typeof(T)} not found"));
    }
}
