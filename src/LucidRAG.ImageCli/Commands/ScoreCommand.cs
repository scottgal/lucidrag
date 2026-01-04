using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.DocSummarizer.Images.Services.Storage;
using LucidRAG.ImageCli.Services;
using Spectre.Console;

namespace LucidRAG.ImageCli.Commands;

/// <summary>
/// Command for demonstrating multi-vector discriminator scoring with decay-based learning
/// </summary>
public static class ScoreCommand
{
    private static readonly Argument<string> ImagePathArg = new("image-path") { Description = "Path to image file" };
    private static readonly Option<string?> ModelOpt = new("--model", "-m") { Description = "Vision model to use (e.g., anthropic:claude-3-opus-20240229)" };
    private static readonly Option<string> GoalOpt = new("--goal", "-g") { Description = "Analysis goal (caption, ocr, object_detection, etc.)", DefaultValueFactory = _ => "caption" };
    private static readonly Option<bool?> AcceptOpt = new("--accept", "-a") { Description = "Accept result (true) or reject (false) for learning" };
    private static readonly Option<string?> FeedbackOpt = new("--feedback", "-f") { Description = "Optional feedback text" };
    private static readonly Option<bool> ShowTopOpt = new("--show-top") { Description = "Show top discriminators for this image type", DefaultValueFactory = _ => false };
    private static readonly Option<bool> PruneOpt = new("--prune") { Description = "Prune ineffective discriminators", DefaultValueFactory = _ => false };

    public static Command Create()
    {
        var command = new Command("score", "Score image analysis with multi-vector discriminators and learning");
        command.Arguments.Add(ImagePathArg);
        command.Options.Add(ModelOpt);
        command.Options.Add(GoalOpt);
        command.Options.Add(AcceptOpt);
        command.Options.Add(FeedbackOpt);
        command.Options.Add(ShowTopOpt);
        command.Options.Add(PruneOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var imagePath = parseResult.GetValue(ImagePathArg)!;
            var model = parseResult.GetValue(ModelOpt);
            var goal = parseResult.GetValue(GoalOpt);
            var accept = parseResult.GetValue(AcceptOpt);
            var feedback = parseResult.GetValue(FeedbackOpt);
            var showTop = parseResult.GetValue(ShowTopOpt);
            var prune = parseResult.GetValue(PruneOpt);

            return await ExecuteAsync(imagePath, model, goal, accept, feedback, showTop, prune, ct);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string imagePath,
        string? model,
        string goal,
        bool? accept,
        string? feedback,
        bool showTop,
        bool prune,
        CancellationToken ct)
    {
        if (!File.Exists(imagePath))
        {
            AnsiConsole.MarkupLine($"[red]✗[/] File not found: {imagePath}");
            return 1;
        }

        // Build configuration
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddUserSecrets<Program>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        // Setup services
        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LucidRAG", "ImageCli", "signals.db");

        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        using var database = new SignalDatabase(databasePath, loggerFactory.CreateLogger<SignalDatabase>());
        var tracker = new SignalEffectivenessTracker(
            loggerFactory.CreateLogger<SignalEffectivenessTracker>(),
            database);
        var discriminatorService = new DiscriminatorService(
            loggerFactory.CreateLogger<DiscriminatorService>(),
            tracker);

        // Handle prune request
        if (prune)
        {
            await AnsiConsole.Status()
                .StartAsync("Pruning ineffective discriminators...", async ctx =>
                {
                    await tracker.PruneIneffectiveDiscriminatorsAsync(ct: ct);
                });
            AnsiConsole.MarkupLine("[green]✓[/] Pruning complete");
            return 0;
        }

        // Build service provider for escalation service
        var services = Program.BuildServiceProvider(config, verbose: false);
        var escalationService = services.GetRequiredService<EscalationService>();

        // Analyze image
        await AnsiConsole.Status()
            .StartAsync("Analyzing image...", async ctx =>
            {
                // Run basic analysis
                var result = await escalationService.AnalyzeWithEscalationAsync(
                    imagePath,
                    forceEscalate: false);

                if (result.Profile == null)
                {
                    AnsiConsole.MarkupLine($"[red]✗[/] Analysis failed");
                    return;
                }

                var profile = result.Profile;
                var imageHash = profile.Sha256;

                // Show basic analysis
                var analysisTable = new Table()
                    .Border(TableBorder.Rounded)
                    .BorderColor(Color.Cyan1)
                    .Title("[cyan]Image Analysis[/]");

                analysisTable.AddColumn("[cyan]Property[/]");
                analysisTable.AddColumn("[white]Value[/]");

                analysisTable.AddRow("File", Path.GetFileName(imagePath));
                analysisTable.AddRow("Type", $"{profile.DetectedType} ({profile.TypeConfidence:P0})");
                analysisTable.AddRow("Dimensions", $"{profile.Width}x{profile.Height}");
                analysisTable.AddRow("Sharpness", $"{profile.LaplacianVariance:F1}");
                analysisTable.AddRow("Text Likeliness", $"{profile.TextLikeliness:F3}");
                analysisTable.AddRow("Edge Density", $"{profile.EdgeDensity:F3}");

                AnsiConsole.Write(analysisTable);
                AnsiConsole.WriteLine();

                // Run vision analysis if model specified
                Mostlylucid.DocSummarizer.Images.Services.Analysis.VisionResult? visionResult = null;
                if (!string.IsNullOrEmpty(model))
                {
                    ctx.Status("Running vision analysis...");

                    var unifiedVisionService = new UnifiedVisionService(config, loggerFactory);
                    var (provider, modelName) = unifiedVisionService.ParseModelSpec(model);

                    var prompt = $"Analyze this {goal} image and provide a detailed description.";
                    var cliVisionResult = await unifiedVisionService.AnalyzeImageAsync(
                        imagePath, prompt, provider, modelName, ct: ct);

                    if (cliVisionResult.Success && cliVisionResult.Caption != null)
                    {
                        AnsiConsole.MarkupLine($"[green]Caption:[/] {Markup.Escape(cliVisionResult.Caption)}");
                        AnsiConsole.WriteLine();

                        // Convert to discriminator VisionResult
                        Mostlylucid.DocSummarizer.Images.Services.Analysis.VisionMetadata? discMetadata = null;
                        if (cliVisionResult.EnhancedMetadata != null)
                        {
                            discMetadata = new Mostlylucid.DocSummarizer.Images.Services.Analysis.VisionMetadata
                            {
                                Tone = cliVisionResult.EnhancedMetadata.Tone,
                                Sentiment = cliVisionResult.EnhancedMetadata.Sentiment,
                                Complexity = cliVisionResult.EnhancedMetadata.Complexity,
                                AestheticScore = cliVisionResult.EnhancedMetadata.AestheticScore,
                                PrimarySubject = cliVisionResult.EnhancedMetadata.PrimarySubject,
                                Purpose = cliVisionResult.EnhancedMetadata.Purpose,
                                TargetAudience = cliVisionResult.EnhancedMetadata.TargetAudience,
                                Confidence = cliVisionResult.EnhancedMetadata.Confidence
                            };
                        }

                        visionResult = new Mostlylucid.DocSummarizer.Images.Services.Analysis.VisionResult(
                            Success: cliVisionResult.Success,
                            Error: cliVisionResult.Error,
                            Caption: cliVisionResult.Caption,
                            Model: cliVisionResult.Model,
                            ConfidenceScore: cliVisionResult.ConfidenceScore,
                            Claims: cliVisionResult.Claims?.Select(c =>
                                new Mostlylucid.DocSummarizer.Images.Services.Analysis.EvidenceClaim(
                                    c.Text, c.Sources, c.Evidence)).ToList(),
                            EnhancedMetadata: discMetadata);
                    }
                }

                // Compute discriminator scores
                ctx.Status("Computing discriminator scores...");

                var score = await discriminatorService.ScoreAnalysisAsync(
                    imageHash,
                    profile,
                    result.GifMotion,
                    visionResult,
                    result.ExtractedText,
                    goal,
                    ct);

                // Display discriminator scores
                var scoreTable = new Table()
                    .Border(TableBorder.Rounded)
                    .BorderColor(Color.Yellow)
                    .Title("[yellow]Multi-Vector Discriminator Scores[/]");

                scoreTable.AddColumn("[yellow]Vector[/]");
                scoreTable.AddColumn("[white]Score[/]");
                scoreTable.AddColumn("[dim]Description[/]");

                AddScoreRow(scoreTable, "Overall Quality", score.OverallScore,
                    "Weighted average across all vectors");
                AddScoreRow(scoreTable, "OCR Fidelity", score.Vectors.OcrFidelity,
                    "Text detection confidence and spell-check pass rate");
                AddScoreRow(scoreTable, "Motion Agreement", score.Vectors.MotionAgreement,
                    "Frame consistency and temporal voting consensus");
                AddScoreRow(scoreTable, "Palette Consistency", score.Vectors.PaletteConsistency,
                    "Color analysis reliability and saturation variance");
                AddScoreRow(scoreTable, "Structural Alignment", score.Vectors.StructuralAlignment,
                    "Edge density, sharpness, aspect ratio stability");
                AddScoreRow(scoreTable, "Grounding Completeness", score.Vectors.GroundingCompleteness,
                    "Evidence source coverage and non-synthesis grounding");
                AddScoreRow(scoreTable, "Novelty vs Prior", score.Vectors.NoveltyVsPrior,
                    "Difference from previous results for same image");

                AnsiConsole.Write(scoreTable);
                AnsiConsole.WriteLine();

                // Show signal contributions
                if (score.SignalContributions.Any())
                {
                    var contributionTable = new Table()
                        .Border(TableBorder.Rounded)
                        .BorderColor(Color.Blue)
                        .Title("[blue]Signal Contributions[/]");

                    contributionTable.AddColumn("[blue]Signal[/]");
                    contributionTable.AddColumn("[white]Strength[/]");
                    contributionTable.AddColumn("[white]Agreement[/]");
                    contributionTable.AddColumn("[dim]Vectors[/]");

                    foreach (var kvp in score.SignalContributions
                        .OrderByDescending(kvp => kvp.Value.Strength)
                        .Take(10))
                    {
                        var signalName = kvp.Key;
                        var contribution = kvp.Value;

                        var strengthColor = contribution.Strength > 0.7 ? "green" :
                                          contribution.Strength > 0.4 ? "yellow" : "red";
                        var agreementColor = contribution.Agreement > 0.7 ? "green" :
                                           contribution.Agreement > 0.4 ? "yellow" : "red";

                        contributionTable.AddRow(
                            signalName,
                            $"[{strengthColor}]{contribution.Strength:F3}[/]",
                            $"[{agreementColor}]{contribution.Agreement:F3}[/]",
                            string.Join(", ", contribution.ContributedVectors));
                    }

                    AnsiConsole.Write(contributionTable);
                    AnsiConsole.WriteLine();
                }

                // Record feedback if provided
                if (accept.HasValue)
                {
                    await discriminatorService.RecordFeedbackAsync(score, accept.Value, feedback, ct);

                    var feedbackColor = accept.Value ? "green" : "red";
                    var feedbackIcon = accept.Value ? "✓" : "✗";
                    AnsiConsole.MarkupLine($"[{feedbackColor}]{feedbackIcon}[/] Feedback recorded: {(accept.Value ? "ACCEPTED" : "REJECTED")}");

                    if (!string.IsNullOrEmpty(feedback))
                    {
                        AnsiConsole.MarkupLine($"[dim]  Note: {Markup.Escape(feedback)}[/]");
                    }

                    AnsiConsole.WriteLine();

                    // Show learning stats
                    var stats = await tracker.GetStatsAsync(ct);

                    var statsTable = new Table()
                        .Border(TableBorder.Rounded)
                        .BorderColor(Color.Green)
                        .Title("[green]Learning Progress[/]");

                    statsTable.AddColumn("[green]Metric[/]");
                    statsTable.AddColumn("[white]Value[/]");

                    statsTable.AddRow("Total Evaluations", stats.TotalScores.ToString());
                    statsTable.AddRow("With Feedback", $"{stats.TotalWithFeedback} ({(stats.TotalScores > 0 ? stats.TotalWithFeedback * 100.0 / stats.TotalScores : 0):F1}%)");
                    statsTable.AddRow("Active Discriminators", stats.ActiveDiscriminators.ToString());
                    statsTable.AddRow("Avg Weight", $"{stats.AverageWeight:F3}");
                    statsTable.AddRow("Avg Agreement Rate", $"{stats.AverageAgreementRate:P0}");

                    AnsiConsole.Write(statsTable);
                }

                // Show top discriminators if requested
                if (showTop)
                {
                    AnsiConsole.WriteLine();
                    var topDiscriminators = await tracker.GetTopDiscriminatorsAsync(
                        profile.DetectedType, goal, limit: 10, ct: ct);

                    if (topDiscriminators.Any())
                    {
                        var topTable = new Table()
                            .Border(TableBorder.Rounded)
                            .BorderColor(Color.Magenta1)
                            .Title($"[magenta1]Top Discriminators for {profile.DetectedType}/{goal}[/]");

                        topTable.AddColumn("[magenta1]Signal[/]");
                        topTable.AddColumn("[white]Weight[/]");
                        topTable.AddColumn("[white]Agreement Rate[/]");
                        topTable.AddColumn("[white]Evaluations[/]");

                        foreach (var disc in topDiscriminators)
                        {
                            var decayedWeight = disc.GetDecayedWeight(DateTimeOffset.UtcNow);
                            var weightColor = decayedWeight > 1.2 ? "green" :
                                            decayedWeight > 0.8 ? "yellow" : "red";

                            topTable.AddRow(
                                disc.SignalName,
                                $"[{weightColor}]{decayedWeight:F3}[/]",
                                $"{disc.AgreementRate:P0}",
                                $"{disc.AgreementCount}/{disc.EvaluationCount}");
                        }

                        AnsiConsole.Write(topTable);
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[dim]No discriminators found for this type/goal combination yet.[/]");
                    }
                }
            });

        return 0;
    }

    private static void AddScoreRow(Table table, string vector, double score, string description)
    {
        var color = score > 0.7 ? "green" :
                   score > 0.4 ? "yellow" :
                   score > 0.0 ? "red" : "dim";

        var bar = new string('█', (int)(score * 20));
        table.AddRow(
            vector,
            $"[{color}]{score:F3} {bar}[/]",
            $"[dim]{description}[/]");
    }
}
