using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Spectre.Console;
using LucidRAG.Cli.Services;

namespace LucidRAG.Cli.Commands;

/// <summary>
/// Extract text from images using advanced OCR pipeline
/// </summary>
public static class OcrCommand
{
    private static readonly Argument<string[]> ImagesArg = new("images")
    {
        Description = "Image files (GIF, PNG, JPG, WebP) to extract text from",
        Arity = ArgumentArity.OneOrMore
    };

    private static readonly Option<bool> ShowSignalsOpt = new("--signals", "-s")
    {
        Description = "Show all OCR signals with metadata",
        DefaultValueFactory = _ => false
    };

    private static readonly Option<bool> VerboseOpt = new("-v", "--verbose")
    {
        Description = "Verbose output",
        DefaultValueFactory = _ => false
    };

    private static readonly Option<string?> DataDirOpt = new("--data-dir")
    {
        Description = "Data directory for models/cache"
    };

    public static Command Create()
    {
        var command = new Command("ocr", "Extract text from images using advanced OCR pipeline");
        command.Arguments.Add(ImagesArg);
        command.Options.Add(ShowSignalsOpt);
        command.Options.Add(VerboseOpt);
        command.Options.Add(DataDirOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var imagePaths = parseResult.GetValue(ImagesArg) ?? [];
            var showSignals = parseResult.GetValue(ShowSignalsOpt);
            var verbose = parseResult.GetValue(VerboseOpt);
            var dataDir = parseResult.GetValue(DataDirOpt);

            var config = new CliConfig
            {
                DataDirectory = Program.EnsureDataDirectory(dataDir),
                Verbose = verbose
            };

            AnsiConsole.Write(new FigletText("OCR Pipeline").Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[dim]Advanced multi-frame OCR with temporal processing[/]\n");

            await using var services = CliServiceRegistration.BuildServiceProvider(config, verbose);
            using var scope = services.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<WaveOrchestrator>();

            foreach (var path in imagePaths)
            {
                var fullPath = Path.GetFullPath(path);

                if (!File.Exists(fullPath))
                {
                    AnsiConsole.MarkupLine($"[red]✗ File not found:[/] {fullPath}\n");
                    continue;
                }

                AnsiConsole.MarkupLine($"[cyan bold]═══ {Path.GetFileName(fullPath)} ═══[/]\n");

                try
                {
                    var profile = await AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots)
                        .SpinnerStyle(Style.Parse("cyan"))
                        .StartAsync($"Analyzing {Path.GetFileName(fullPath)}...", async ctx =>
                        {
                            return await orchestrator.AnalyzeAsync(fullPath, ct);
                        });

                    // Display analysis summary
                    AnsiConsole.MarkupLine($"[dim]Analysis completed in {profile.AnalysisDurationMs}ms[/]");
                    AnsiConsole.MarkupLine($"[dim]Signals: {profile.GetAllSignals().Count()} | Waves: {string.Join(", ", profile.ContributingWaves)}[/]\n");

                    // Get extracted text
                    string? extractedText = null;
                    string? source = null;
                    double confidence = 0;

                    if (profile.HasSignal("ocr.corrected.text"))
                    {
                        extractedText = profile.GetValue<string>("ocr.corrected.text");
                        source = "Post-Corrected";
                        confidence = profile.GetBestSignal("ocr.corrected.text")?.Confidence ?? 0;
                    }
                    else if (profile.HasSignal("ocr.voting.consensus_text"))
                    {
                        extractedText = profile.GetValue<string>("ocr.voting.consensus_text");
                        source = "Temporal Voting";
                        confidence = profile.GetBestSignal("ocr.voting.consensus_text")?.Confidence ?? 0;
                    }
                    else if (profile.HasSignal("ocr.temporal_median.full_text"))
                    {
                        extractedText = profile.GetValue<string>("ocr.temporal_median.full_text");
                        source = "Temporal Median";
                        confidence = profile.GetBestSignal("ocr.temporal_median.full_text")?.Confidence ?? 0;
                    }
                    else if (profile.HasSignal("ocr.full_text"))
                    {
                        extractedText = profile.GetValue<string>("ocr.full_text");
                        source = "Simple OCR";
                        confidence = profile.GetBestSignal("ocr.full_text")?.Confidence ?? 0;
                    }

                    // Display text extraction results
                    if (!string.IsNullOrWhiteSpace(extractedText))
                    {
                        var panel = new Panel(extractedText.Trim())
                            .Header($"[green bold]✓ Extracted Text[/] [dim]({source}, confidence: {confidence:F2})[/]")
                            .BorderColor(Color.Green)
                            .Padding(1, 1);
                        AnsiConsole.Write(panel);

                        // Show additional metrics
                        var table = new Table()
                            .Border(TableBorder.Rounded)
                            .BorderColor(Color.Grey)
                            .AddColumn("[dim]Metric[/]")
                            .AddColumn("[dim]Value[/]");

                        table.AddRow("Characters", extractedText.Length.ToString());
                        table.AddRow("Words", extractedText.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length.ToString());

                        if (profile.HasSignal("ocr.frames.extracted"))
                        {
                            table.AddRow("Frames Processed", profile.GetValue<int>("ocr.frames.extracted").ToString());
                        }

                        if (profile.HasSignal("ocr.voting.agreement_score"))
                        {
                            var agreement = profile.GetValue<double>("ocr.voting.agreement_score");
                            table.AddRow("Frame Agreement", $"{agreement:F2}");
                        }

                        if (profile.HasSignal("ocr.stabilization.confidence"))
                        {
                            var stabConf = profile.GetValue<double>("ocr.stabilization.confidence");
                            table.AddRow("Stabilization Quality", $"{stabConf:F2}");
                        }

                        AnsiConsole.Write(table);
                    }
                    else
                    {
                        // No text extracted - show why
                        var panel = new Panel("[yellow]No text extracted[/]")
                            .Header("[yellow bold]⚠ OCR Result[/]")
                            .BorderColor(Color.Yellow)
                            .Padding(1, 1);
                        AnsiConsole.Write(panel);

                        if (profile.HasSignal("ocr.skipped") || profile.HasSignal("ocr.advanced.skipped"))
                        {
                            var skipSignal = profile.GetBestSignal("ocr.advanced.skipped") ?? profile.GetBestSignal("ocr.skipped");
                            if (skipSignal?.Metadata != null && skipSignal.Metadata.ContainsKey("reason"))
                            {
                                AnsiConsole.MarkupLine($"[dim]Reason: {skipSignal.Metadata["reason"]}[/]");
                            }
                        }
                    }

                    // Show all signals if requested
                    if (showSignals)
                    {
                        AnsiConsole.WriteLine();
                        var signalsTable = new Table()
                            .Title("[cyan bold]All Signals[/]")
                            .Border(TableBorder.Rounded)
                            .BorderColor(Color.Cyan1)
                            .AddColumn("[bold]Source[/]")
                            .AddColumn("[bold]Key[/]")
                            .AddColumn("[bold]Value[/]")
                            .AddColumn("[bold]Confidence[/]");

                        foreach (var signal in profile.GetAllSignals().OrderBy(s => s.Source).ThenBy(s => s.Key))
                        {
                            var valueStr = signal.Value?.ToString() ?? "null";
                            if (valueStr.Length > 50)
                                valueStr = valueStr.Substring(0, 47) + "...";

                            signalsTable.AddRow(
                                signal.Source,
                                signal.Key,
                                valueStr,
                                signal.Confidence.ToString("F3")
                            );
                        }

                        AnsiConsole.Write(signalsTable);
                    }

                    AnsiConsole.WriteLine();
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]✗ Error:[/] {ex.Message}");
                    if (verbose)
                    {
                        AnsiConsole.WriteException(ex);
                    }
                    AnsiConsole.WriteLine();
                }
            }
        });

        return command;
    }
}
