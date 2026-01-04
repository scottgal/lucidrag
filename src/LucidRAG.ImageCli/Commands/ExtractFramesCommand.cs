using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Services.Ocr;
using Spectre.Console;

namespace LucidRAG.ImageCli.Commands;

/// <summary>
/// Command for extracting and saving processed frames from animated images (GIFs/WebP).
/// Creates a "minimum" animated GIF showing only the deduplicated, stabilized frames
/// used for OCR analysis.
/// </summary>
public static class ExtractFramesCommand
{
    private static readonly Argument<string> InputArg = new("input")
    {
        Description = "Path to animated image (GIF/WebP)"
    };

    private static readonly Argument<string> OutputArg = new("output")
    {
        Description = "Output path for processed animated GIF"
    };

    private static readonly Option<int> FrameDelayOpt = new("--delay", "-d")
    {
        Description = "Frame delay in centiseconds (default: 10 = 100ms)",
        DefaultValueFactory = _ => 10
    };

    private static readonly Option<bool> VerboseOpt = new("--verbose", "-v")
    {
        Description = "Show detailed processing information",
        DefaultValueFactory = _ => false
    };

    public static Command Create()
    {
        var command = new Command("extract-frames",
            "Extract and save processed frames from animated images");

        command.Arguments.Add(InputArg);
        command.Arguments.Add(OutputArg);
        command.Options.Add(FrameDelayOpt);
        command.Options.Add(VerboseOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.GetValue(InputArg)!;
            var output = parseResult.GetValue(OutputArg)!;
            var frameDelay = parseResult.GetValue(FrameDelayOpt);
            var verbose = parseResult.GetValue(VerboseOpt);

            return await ExecuteAsync(input, output, frameDelay, verbose, ct);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string inputPath,
        string outputPath,
        int frameDelay,
        bool verbose,
        CancellationToken ct)
    {
        if (!File.Exists(inputPath))
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Input file not found: {inputPath}");
            return 1;
        }

        // Build configuration
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddUserSecrets<Program>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var logLevel = verbose ? LogLevel.Information : LogLevel.Warning;
        var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(logLevel));

        try
        {
            await AnsiConsole.Status()
                .StartAsync("Processing animated image...", async ctx =>
                {
                    // Create OCR service
                    var ocrConfig = config.GetSection("Ocr").Get<OcrConfig>() ?? new OcrConfig();
                    var ocrEngine = new TesseractOcrEngine(); // Use defaults

                    var serviceLogger = loggerFactory.CreateLogger<AdvancedGifOcrService>();
                    var advancedOcrService = new AdvancedGifOcrService(ocrEngine, ocrConfig, serviceLogger);

                    ctx.Status("Extracting and processing frames...");

                    // Extract with frame capture enabled
                    var result = await advancedOcrService.ExtractTextAsync(
                        inputPath,
                        captureProcessedFrames: true,
                        ct: ct);

                    if (result.ProcessedFrames == null || result.ProcessedFrames.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[red]✗[/] No frames were processed");
                        return;
                    }

                    ctx.Status($"Saving {result.ProcessedFrames.Count} frames as animated GIF...");

                    // Ensure output directory exists
                    var outputDir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(outputDir))
                    {
                        Directory.CreateDirectory(outputDir);
                    }

                    // Save as animated GIF
                    AdvancedGifOcrService.SaveAsAnimatedGif(result.ProcessedFrames, outputPath, frameDelay);

                    // Clean up
                    foreach (var frame in result.ProcessedFrames)
                    {
                        frame.Dispose();
                    }

                    ctx.Status("Complete!");
                });

            // Show success with details
            var outputInfo = new FileInfo(outputPath);
            var panel = new Panel(new Markup(
                $"[green]✓[/] Processed frames saved successfully!\n\n" +
                $"[cyan]Output:[/] {outputPath}\n" +
                $"[cyan]Size:[/] {outputInfo.Length / 1024.0:F1} KB\n" +
                $"[cyan]Frame delay:[/] {frameDelay * 10}ms"
            ))
            {
                Header = new PanelHeader("[green]Success[/]"),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Green)
            };

            AnsiConsole.Write(panel);

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
            return 1;
        }
    }
}
