using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Extensions;
using Mostlylucid.DocSummarizer.Images.Services;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Pipelines;
using Mostlylucid.DocSummarizer.Images.Coordination;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;
using Spectre.Console;

namespace Mostlylucid.ImageSummarizer.Cli;

/// <summary>
/// Standalone image analysis and OCR CLI tool
/// Supports multiple output formats for easy tool integration (MCP, scripts, etc.)
/// Part of the LucidRAG Summarizer family.
/// </summary>
class Program
{
    static async Task<int> Main(string[] args)
    {
        // Check for MCP mode first (doesn't require image path)
        if (args.Contains("--mcp"))
        {
            await RunMcpServer();
            return 0;
        }

        var rootCommand = new RootCommand("Image Intelligence - Heuristic analysis + Vision LLM escalation");

        // Arguments - image is optional now (if not provided, enters interactive mode)
        var imageArg = new Argument<string?>("image", () => null, "Path to image file (all ImageSharp formats: JPEG, PNG, GIF, BMP, TIFF, TGA, WebP, PBM). If not provided, enters interactive mode.");

        // Options
        var pipelineOpt = new Option<string>(
            "--pipeline",
            getDefaultValue: () => "auto",
            description: "Pipeline: auto (smart routing), caption, vision (no OCR), motion (fast), advancedocr, simpleocr, quality, stats, alttext, florence2 (fast ONNX), florence2+llm (hybrid)");

        var outputOpt = new Option<string>(
            "--output",
            getDefaultValue: () => "auto",
            description: "Output: auto, text, json, signals, metrics, caption, alttext, markdown, visual");

        var languageOpt = new Option<string>(
            "--language",
            getDefaultValue: () => "en_US",
            description: "Language for spell checking");

        var verboseOpt = new Option<bool>(
            "--verbose",
            getDefaultValue: () => false,
            description: "Enable verbose logging");

        // Vision LLM options
        var llmOpt = new Option<bool?>(
            "--llm",
            getDefaultValue: () => null,
            description: "Enable/disable Vision LLM (default: auto-detect Ollama)");

        var modelOpt = new Option<string?>(
            "--model",
            getDefaultValue: () => null,
            description: "Vision LLM model (default: minicpm-v:8b, or $VISION_MODEL)");

        var ollamaOpt = new Option<string?>(
            "--ollama",
            getDefaultValue: () => null,
            description: "Ollama base URL (default: http://localhost:11434, or $OLLAMA_BASE_URL)");

        var signalsOpt = new Option<string?>(
            "--signals",
            getDefaultValue: () => null,
            description: "Glob patterns to select signals for JSON output (e.g., 'motion.*,color.dominant*' or '@motion' for predefined collections)");

        var allComputedOpt = new Option<bool>(
            "--all-computed",
            getDefaultValue: () => false,
            description: "Include all computed signals in output (not just those matching --signals pattern). Useful for debugging and learning.");

        var pipelineFileOpt = new Option<string?>(
            "--pipeline-file",
            getDefaultValue: () => null,
            description: "YAML file defining a dynamic pipeline (use '-' for stdin). See 'imagesummarizer sample-pipeline' for format.");

        var saveDebugOpt = new Option<string?>(
            "--save-debug",
            getDefaultValue: () => null,
            description: "Save debug images (temporal median, frames) to specified directory");

        // Add list-pipelines command
        var listCmd = new Command("list-pipelines", "List all available OCR pipelines");
        listCmd.SetHandler(async () =>
        {
            await ListPipelines();
        });

        // Add list-signals command
        var listSignalsCmd = new Command("list-signals", "List all available signals and collections for --signals filtering");
        listSignalsCmd.SetHandler(() =>
        {
            ListSignals();
        });

        // Add sample-pipeline command to output example YAML
        var samplePipelineCmd = new Command("sample-pipeline", "Output a sample YAML pipeline definition");
        samplePipelineCmd.SetHandler(() =>
        {
            Console.WriteLine(DynamicPipelineLoader.GetSampleYaml());
        });

        // Add export-strip command for generating frame strips
        var exportStripCmd = new Command("export-strip", "Export a frame strip from an animated GIF");
        var stripImageArg = new Argument<string>("image", "Path to animated GIF");
        var stripOutputArg = new Argument<string?>("output", () => null, "Output path for PNG (default: <image>_strip.png)");
        var maxFramesOpt = new Option<int>("--max-frames", () => 10, "Maximum frames to include");
        var dedupeOpt = new Option<bool>("--dedupe", () => false, "Deduplicate similar frames");
        var modeOpt = new Option<string>("--mode", () => "auto", "Strip mode: auto, ocr (text-changes only), motion (keyframes), text-only (bounding boxes only)");
        exportStripCmd.AddArgument(stripImageArg);
        exportStripCmd.AddArgument(stripOutputArg);
        exportStripCmd.AddOption(maxFramesOpt);
        exportStripCmd.AddOption(dedupeOpt);
        exportStripCmd.AddOption(modeOpt);
        exportStripCmd.SetHandler(async (string imagePath, string? outputPath, int maxFrames, bool dedupe, string mode) =>
        {
            await ExportFrameStrip(imagePath, outputPath, maxFrames, dedupe, mode);
        }, stripImageArg, stripOutputArg, maxFramesOpt, dedupeOpt, modeOpt);

        rootCommand.AddArgument(imageArg);
        rootCommand.AddOption(pipelineOpt);
        rootCommand.AddOption(outputOpt);
        rootCommand.AddOption(languageOpt);
        rootCommand.AddOption(verboseOpt);
        rootCommand.AddOption(llmOpt);
        rootCommand.AddOption(modelOpt);
        rootCommand.AddOption(ollamaOpt);
        rootCommand.AddOption(signalsOpt);
        rootCommand.AddOption(allComputedOpt);
        rootCommand.AddOption(pipelineFileOpt);
        rootCommand.AddCommand(listCmd);
        rootCommand.AddCommand(listSignalsCmd);
        rootCommand.AddCommand(samplePipelineCmd);
        rootCommand.AddCommand(exportStripCmd);

        rootCommand.SetHandler(async (context) =>
        {
            var inputPath = context.ParseResult.GetValueForArgument(imageArg);
            var pipeline = context.ParseResult.GetValueForOption(pipelineOpt)!;
            var output = context.ParseResult.GetValueForOption(outputOpt)!;
            var language = context.ParseResult.GetValueForOption(languageOpt)!;
            var verbose = context.ParseResult.GetValueForOption(verboseOpt);
            var llm = context.ParseResult.GetValueForOption(llmOpt);
            var model = context.ParseResult.GetValueForOption(modelOpt);
            var ollama = context.ParseResult.GetValueForOption(ollamaOpt);
            var signals = context.ParseResult.GetValueForOption(signalsOpt);
            var allComputed = context.ParseResult.GetValueForOption(allComputedOpt);
            var pipelineFile = context.ParseResult.GetValueForOption(pipelineFileOpt);

            // Load dynamic pipeline if specified
            DynamicPipeline? dynamicPipeline = null;
            if (!string.IsNullOrWhiteSpace(pipelineFile))
            {
                try
                {
                    dynamicPipeline = pipelineFile == "-"
                        ? DynamicPipelineLoader.LoadFromStream(Console.OpenStandardInput())
                        : DynamicPipelineLoader.LoadFromFile(pipelineFile);

                    // Validate pipeline
                    var (isValid, errors) = DynamicPipelineLoader.Validate(dynamicPipeline);
                    if (!isValid)
                    {
                        Console.Error.WriteLine($"Invalid pipeline definition:");
                        foreach (var error in errors)
                            Console.Error.WriteLine($"  - {error}");
                        Environment.Exit(1);
                    }

                    // Override settings from pipeline file
                    if (!string.IsNullOrWhiteSpace(dynamicPipeline.Output.Format))
                        output = dynamicPipeline.Output.Format;
                    if (dynamicPipeline.Llm.Model != null)
                        model = dynamicPipeline.Llm.Model;
                    if (dynamicPipeline.Llm.OllamaUrl != null)
                        ollama = dynamicPipeline.Llm.OllamaUrl;
                    llm = dynamicPipeline.Llm.Enabled;

                    // Use signals from pipeline if not overridden by --signals
                    if (string.IsNullOrWhiteSpace(signals) && dynamicPipeline.UsesSignalSelection)
                        signals = dynamicPipeline.GetSignalPattern();

                    if (verbose)
                        Console.Error.WriteLine($"Loaded pipeline: {dynamicPipeline.Name} ({dynamicPipeline.Description ?? "no description"})");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to load pipeline file: {ex.Message}");
                    Environment.Exit(1);
                }
            }

            // If no path provided, enter interactive mode with the configured options
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                await InteractiveMode(pipeline, output, language, verbose, llm, model, ollama);
            }
            else if (Directory.Exists(inputPath))
            {
                // Process all images in directory
                await ProcessDirectory(inputPath, pipeline, output, language, verbose, llm, model, ollama, signals, dynamicPipeline, allComputed);
            }
            else
            {
                await ProcessImage(inputPath, pipeline, output, language, verbose, llm, model, ollama, signals, dynamicPipeline, allComputed);
            }
        });

        return await rootCommand.InvokeAsync(args);
    }

    static async Task<int> InteractiveMode(
        string pipeline = "caption",
        string output = "visual",
        string language = "en_US",
        bool verbose = false,
        bool? llm = null,
        string? model = null,
        string? ollama = null)
    {
        // Resolve LLM settings from args > env > defaults
        var ollamaUrl = ollama ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434";
        var visionModel = model ?? Environment.GetEnvironmentVariable("VISION_MODEL") ?? "minicpm-v:8b";
        var enableLlm = llm ?? true; // Default enabled, auto-detect will handle availability

        // Banner with Spectre.Console
        var figlet = new Spectre.Console.FigletText("ImageSummarizer");
        figlet.Color = Spectre.Console.Color.Cyan1;
        Spectre.Console.AnsiConsole.Write(figlet);

        Spectre.Console.AnsiConsole.MarkupLine("[cyan1]Image Intelligence[/] [dim]• Vision AI • Motion • Signals • Color • Quality[/]");
        Spectre.Console.AnsiConsole.MarkupLine("[dim]Part of the LucidRAG DocSummarizer family[/]");
        Spectre.Console.AnsiConsole.WriteLine();

        // Show current settings
        ShowSettings(pipeline, output, language, verbose, enableLlm, visionModel, ollamaUrl);
        ShowCommands();

        // Interactive loop
        while (true)
        {
            Spectre.Console.AnsiConsole.Markup("[cyan]>[/] ");
            var input = Console.ReadLine()?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(input))
                continue;

            // Handle slash commands
            if (input.StartsWith("/"))
            {
                var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var cmd = parts[0].ToLowerInvariant();
                var arg = parts.Length > 1 ? parts[1].Trim() : null;

                switch (cmd)
                {
                    case "/quit" or "/exit" or "/q":
                        Spectre.Console.AnsiConsole.MarkupLine("[dim]Goodbye![/]");
                        return 0;

                    case "/help" or "/?":
                        ShowCommands();
                        continue;

                    case "/settings":
                        ShowSettings(pipeline, output, language, verbose);
                        continue;

                    case "/pipeline":
                        if (arg == null)
                        {
                            Spectre.Console.AnsiConsole.MarkupLine($"[dim]Current pipeline:[/] [cyan]{pipeline}[/]");
                            Spectre.Console.AnsiConsole.MarkupLine("[dim]Available: auto, caption, vision, motion, advancedocr, simpleocr, quality, stats, alttext[/]");
                            Spectre.Console.AnsiConsole.MarkupLine("[dim]  auto        - Smart routing (fast/balanced/quality based on image)[/]");
                            Spectre.Console.AnsiConsole.MarkupLine("[dim]  caption     - Vision LLM captions with OCR fallback[/]");
                            Spectre.Console.AnsiConsole.MarkupLine("[dim]  vision      - Vision LLM only (no Tesseract required)[/]");
                            Spectre.Console.AnsiConsole.MarkupLine("[dim]  motion      - Motion analysis only (fast, no LLM/OCR)[/]");
                            Spectre.Console.AnsiConsole.MarkupLine("[dim]  advancedocr - Multi-frame OCR with stabilization[/]");
                        }
                        else if (arg is "auto" or "advancedocr" or "simpleocr" or "quality" or "stats" or "caption" or "alttext" or "vision" or "motion")
                        {
                            pipeline = arg;
                            Spectre.Console.AnsiConsole.MarkupLine($"[green]✓[/] Pipeline set to [cyan]{pipeline}[/]");
                            // Auto-set output format for specialized pipelines
                            if (arg == "stats" && output == "auto") output = "metrics";
                            if (arg == "caption" && output == "auto") output = "caption";
                            if (arg == "alttext" && output == "auto") output = "alttext";
                            if (arg == "motion" && output == "auto") output = "visual";
                            if (arg == "vision" && output == "auto") output = "visual";
                        }
                        else
                        {
                            Spectre.Console.AnsiConsole.MarkupLine($"[red]✗[/] Unknown pipeline: {arg}. Use: auto, caption, vision, motion, advancedocr, simpleocr, quality, stats, alttext");
                        }
                        continue;

                    case "/output":
                        if (arg == null)
                        {
                            Spectre.Console.AnsiConsole.MarkupLine($"[dim]Current output:[/] [cyan]{output}[/]");
                            Spectre.Console.AnsiConsole.MarkupLine("[dim]Available: auto, text, json, signals, metrics, caption, alttext, markdown, visual[/]");
                        }
                        else if (arg is "auto" or "text" or "json" or "signals" or "metrics" or "caption" or "alttext" or "markdown" or "visual")
                        {
                            output = arg;
                            Spectre.Console.AnsiConsole.MarkupLine($"[green]✓[/] Output set to [cyan]{output}[/]");
                        }
                        else
                        {
                            Spectre.Console.AnsiConsole.MarkupLine($"[red]✗[/] Unknown output format: {arg}. Use: auto, text, json, signals, metrics, caption, alttext, markdown, visual");
                        }
                        continue;

                    case "/language" or "/lang":
                        if (arg == null)
                        {
                            Spectre.Console.AnsiConsole.MarkupLine($"[dim]Current language:[/] [cyan]{language}[/]");
                        }
                        else
                        {
                            language = arg;
                            Spectre.Console.AnsiConsole.MarkupLine($"[green]✓[/] Language set to [cyan]{language}[/]");
                        }
                        continue;

                    case "/verbose":
                        if (arg == null)
                        {
                            verbose = !verbose;
                        }
                        else
                        {
                            verbose = arg.ToLowerInvariant() is "on" or "true" or "1" or "yes";
                        }
                        Spectre.Console.AnsiConsole.MarkupLine($"[green]✓[/] Verbose mode: [cyan]{(verbose ? "on" : "off")}[/]");
                        continue;

                    case "/llm":
                        if (arg == null)
                        {
                            enableLlm = !enableLlm;
                        }
                        else
                        {
                            enableLlm = arg.ToLowerInvariant() is "on" or "true" or "1" or "yes";
                        }
                        Spectre.Console.AnsiConsole.MarkupLine($"[green]✓[/] Vision LLM: [cyan]{(enableLlm ? "enabled" : "disabled")}[/]");
                        continue;

                    case "/model":
                        if (arg == null)
                        {
                            Spectre.Console.AnsiConsole.MarkupLine($"[dim]Current model:[/] [cyan]{visionModel}[/]");
                            Spectre.Console.AnsiConsole.MarkupLine("[dim]Use /models to list available Ollama models[/]");
                        }
                        else
                        {
                            visionModel = arg;
                            Spectre.Console.AnsiConsole.MarkupLine($"[green]✓[/] Vision model set to [cyan]{visionModel}[/]");
                        }
                        continue;

                    case "/ollama":
                        if (arg == null)
                        {
                            Spectre.Console.AnsiConsole.MarkupLine($"[dim]Current Ollama URL:[/] [cyan]{ollamaUrl}[/]");
                        }
                        else
                        {
                            ollamaUrl = arg;
                            Spectre.Console.AnsiConsole.MarkupLine($"[green]✓[/] Ollama URL set to [cyan]{ollamaUrl}[/]");
                        }
                        continue;

                    case "/models":
                        await ListOllamaModels(ollamaUrl);
                        continue;

                    default:
                        Spectre.Console.AnsiConsole.MarkupLine($"[red]✗[/] Unknown command: {cmd}. Type /help for available commands.");
                        continue;
                }
            }

            // Treat as image path or directory
            var inputPath = input.Trim('"');

            if (Directory.Exists(inputPath))
            {
                // Process all images in directory
                await ProcessDirectory(inputPath, pipeline, output, language, verbose, enableLlm, visionModel, ollamaUrl);
                Spectre.Console.AnsiConsole.WriteLine();
                continue;
            }

            if (!File.Exists(inputPath))
            {
                Spectre.Console.AnsiConsole.MarkupLine($"[red]✗[/] File or directory not found: {Spectre.Console.Markup.Escape(inputPath)}");
                continue;
            }

            // Process the image
            await ProcessImage(inputPath, pipeline, output, language, verbose, enableLlm, visionModel, ollamaUrl);
            Spectre.Console.AnsiConsole.WriteLine();
        }
    }

    static void ShowSettings(string pipeline, string output, string language, bool verbose, bool enableLlm = true, string visionModel = "minicpm-v:8b", string ollamaUrl = "http://localhost:11434")
    {
        Spectre.Console.AnsiConsole.MarkupLine("[dim]Current settings:[/]");
        Spectre.Console.AnsiConsole.MarkupLine($"  [dim]pipeline:[/]  [cyan]{pipeline}[/]");
        Spectre.Console.AnsiConsole.MarkupLine($"  [dim]output:[/]    [cyan]{output}[/]");
        Spectre.Console.AnsiConsole.MarkupLine($"  [dim]language:[/]  [cyan]{language}[/]");
        Spectre.Console.AnsiConsole.MarkupLine($"  [dim]verbose:[/]   [cyan]{(verbose ? "on" : "off")}[/]");
        Spectre.Console.AnsiConsole.MarkupLine($"  [dim]llm:[/]       [cyan]{(enableLlm ? "enabled" : "disabled")}[/]");
        Spectre.Console.AnsiConsole.MarkupLine($"  [dim]model:[/]     [cyan]{visionModel}[/]");
        Spectre.Console.AnsiConsole.MarkupLine($"  [dim]ollama:[/]    [cyan]{ollamaUrl}[/]");
        Spectre.Console.AnsiConsole.WriteLine();
    }

    static void ShowCommands()
    {
        Spectre.Console.AnsiConsole.MarkupLine("[dim]Commands:[/]");
        Spectre.Console.AnsiConsole.MarkupLine("  [cyan]/pipeline[/] [dim]<name>[/]    Set pipeline:");
        Spectre.Console.AnsiConsole.MarkupLine("                         advancedocr, simpleocr, quality, stats, caption, alttext");
        Spectre.Console.AnsiConsole.MarkupLine("  [cyan]/output[/] [dim]<format>[/]    Set output format:");
        Spectre.Console.AnsiConsole.MarkupLine("                         auto, text, json, signals, metrics, caption, alttext, markdown, visual");
        Spectre.Console.AnsiConsole.MarkupLine("  [cyan]/language[/] [dim]<code>[/]    Set spell-check language (e.g., en_US)");
        Spectre.Console.AnsiConsole.MarkupLine("  [cyan]/verbose[/] [dim]<on/off>[/]   Toggle verbose logging");
        Spectre.Console.AnsiConsole.MarkupLine("  [cyan]/llm[/] [dim]<on/off>[/]       Toggle Vision LLM for captions");
        Spectre.Console.AnsiConsole.MarkupLine("  [cyan]/model[/] [dim]<name>[/]       Set Vision LLM model (e.g., minicpm-v:8b, llava)");
        Spectre.Console.AnsiConsole.MarkupLine("  [cyan]/ollama[/] [dim]<url>[/]       Set Ollama base URL");
        Spectre.Console.AnsiConsole.MarkupLine("  [cyan]/models[/]              List available Ollama vision models");
        Spectre.Console.AnsiConsole.MarkupLine("  [cyan]/settings[/]            Show current settings");
        Spectre.Console.AnsiConsole.MarkupLine("  [cyan]/quit[/]                Exit");
        Spectre.Console.AnsiConsole.WriteLine();

        Spectre.Console.AnsiConsole.MarkupLine("[cyan]Pipelines:[/]");
        Spectre.Console.AnsiConsole.MarkupLine("  [yellow]stats[/]       Fast metrics only - dimensions, colors, sharpness, type");
        Spectre.Console.AnsiConsole.MarkupLine("  [yellow]simpleocr[/]   Baseline OCR - fastest, lower accuracy");
        Spectre.Console.AnsiConsole.MarkupLine("  [yellow]advancedocr[/] Multi-frame OCR with voting [dim](default)[/]");
        Spectre.Console.AnsiConsole.MarkupLine("  [yellow]quality[/]     Maximum OCR accuracy - slower");
        Spectre.Console.AnsiConsole.MarkupLine("  [yellow]caption[/]     Vision LLM caption with evidence claims");
        Spectre.Console.AnsiConsole.MarkupLine("  [yellow]alttext[/]     Accessibility-focused alt text generation");
        Spectre.Console.AnsiConsole.WriteLine();

        Spectre.Console.AnsiConsole.MarkupLine("[dim]Examples:[/]");
        Spectre.Console.AnsiConsole.MarkupLine("  [dim]# Quick stats for batch processing[/]");
        Spectre.Console.AnsiConsole.MarkupLine("  imagesummarizer photo.jpg --pipeline stats");
        Spectre.Console.AnsiConsole.WriteLine();
        Spectre.Console.AnsiConsole.MarkupLine("  [dim]# Generate LLM caption with evidence[/]");
        Spectre.Console.AnsiConsole.MarkupLine("  imagesummarizer photo.jpg --pipeline caption");
        Spectre.Console.AnsiConsole.WriteLine();
        Spectre.Console.AnsiConsole.MarkupLine("  [dim]# Generate accessibility alt text[/]");
        Spectre.Console.AnsiConsole.MarkupLine("  imagesummarizer photo.jpg --pipeline alttext");
        Spectre.Console.AnsiConsole.WriteLine();
        Spectre.Console.AnsiConsole.MarkupLine("  [dim]# Extract text from screenshot as markdown[/]");
        Spectre.Console.AnsiConsole.MarkupLine("  imagesummarizer screenshot.png --output markdown");
        Spectre.Console.AnsiConsole.WriteLine();
        Spectre.Console.AnsiConsole.MarkupLine("  [dim]# Full JSON output for integration[/]");
        Spectre.Console.AnsiConsole.MarkupLine("  imagesummarizer image.png --output json");
        Spectre.Console.AnsiConsole.WriteLine();
        Spectre.Console.AnsiConsole.MarkupLine("  [dim]# Start MCP server for Claude Desktop[/]");
        Spectre.Console.AnsiConsole.MarkupLine("  imagesummarizer --mcp");
        Spectre.Console.AnsiConsole.WriteLine();

        Spectre.Console.AnsiConsole.MarkupLine("[dim]Enter an image path to analyze, or drag & drop a file.[/]");
        Spectre.Console.AnsiConsole.WriteLine();
    }

    static void AnimateSpinner(CancellationToken ct)
    {
        var spinner = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
        int i = 0;
        while (!ct.IsCancellationRequested)
        {
            Console.Write($"\r⏳ Analyzing image {spinner[i++ % spinner.Length]}");
            Thread.Sleep(80);
        }
        Console.Write("\r");
    }

    static void ShowAsciiPreview(string imagePath)
    {
        try
        {
            using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(imagePath);

            // Resize to fit console (max 60 chars wide, 20 lines high)
            var targetWidth = Math.Min(60, Console.WindowWidth - 10);
            var targetHeight = (int)(targetWidth * 0.5 * ((double)image.Height / image.Width));
            targetHeight = Math.Min(targetHeight, 20);

            image.Mutate(x => x.Resize(targetWidth, targetHeight));

            // Pixel-art style colored preview using Spectre.Console
            var canvas = new Spectre.Console.Canvas(targetWidth, targetHeight);

            for (int y = 0; y < image.Height; y++)
            {
                for (int x = 0; x < image.Width; x++)
                {
                    var pixel = image[x, y];

                    // Convert RGB to Spectre.Console.Color
                    var color = new Spectre.Console.Color(pixel.R, pixel.G, pixel.B);

                    // Use block characters for pixel-art effect
                    canvas.SetPixel(x, y, color);
                }
            }

            Spectre.Console.AnsiConsole.Write(canvas);
            Spectre.Console.AnsiConsole.WriteLine();
        }
        catch
        {
            // Skip preview if image can't be loaded
        }
    }

    static void ShowInteractiveResults(DynamicImageProfile profile)
    {
        // Get ledger for structured view
        var ledger = profile.GetLedger();

        Spectre.Console.AnsiConsole.WriteLine();
        Spectre.Console.AnsiConsole.Write(
            new Spectre.Console.Rule("[cyan]📊 Analysis Results[/]"));
        Spectre.Console.AnsiConsole.WriteLine();

        // Key metrics
        Spectre.Console.AnsiConsole.MarkupLine($"[dim]Image:[/] [white]{Spectre.Console.Markup.Escape(Path.GetFileName(profile.ImagePath) ?? "")}[/]");
        Spectre.Console.AnsiConsole.MarkupLine($"[dim]Duration:[/] [yellow]{profile.AnalysisDurationMs}ms[/]");
        Spectre.Console.AnsiConsole.MarkupLine($"[dim]Waves:[/] [cyan]{profile.ContributingWaves.Count}[/] executed");
        Spectre.Console.AnsiConsole.MarkupLine($"[dim]Signals:[/] [green]{profile.GetAllSignals().Count()}[/] captured");
        Spectre.Console.AnsiConsole.WriteLine();

        // Show ledger summary
        Spectre.Console.AnsiConsole.Write(new Spectre.Console.Rule("[cyan]📋 Image Intelligence Ledger[/]"));
        Spectre.Console.AnsiConsole.WriteLine();
        Spectre.Console.AnsiConsole.MarkupLine($"[dim]{Spectre.Console.Markup.Escape(ledger.ToLlmSummary())}[/]");
        Spectre.Console.AnsiConsole.WriteLine();

        // Show signal breakdown table
        ShowSignalBreakdown(profile);
        Spectre.Console.AnsiConsole.WriteLine();

        // Extracted text
        var text = ledger.Text.ExtractedText;
        var confidence = ledger.Text.Confidence;

        if (!string.IsNullOrWhiteSpace(text))
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✅ Extracted Text:");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("┌─────────────────────────────────────────────────────────┐");
            Console.WriteLine($"│ {text.PadRight(55).Substring(0, 55)} │");
            Console.WriteLine("└─────────────────────────────────────────────────────────┘");
            Console.ResetColor();
            Console.WriteLine();

            // Quality indicators
            var spellScore = profile.GetValue<double>("ocr.quality.spell_check_score");
            var isGarbled = profile.GetValue<bool>("ocr.quality.is_garbled");

            Console.WriteLine("📈 Quality Metrics:");
            Console.WriteLine($"  • Confidence: {confidence:P0}");
            Console.WriteLine($"  • Spell Check: {spellScore:P0} correct");

            if (isGarbled)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  ⚠️  Text appears garbled (< 50% correct words)");
                Console.WriteLine("  💡 Try: --pipeline quality for better results");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  ✓ Text quality looks good");
                Console.ResetColor();
            }
            Console.WriteLine();

            // Frame processing details
            if (profile.HasSignal("ocr.frames.extracted"))
            {
                var frames = profile.GetValue<int>("ocr.frames.extracted");
                var stabQuality = profile.GetValue<double>("ocr.stabilization.confidence");
                var agreement = profile.GetValue<double>("ocr.voting.agreement_score");

                Console.WriteLine("🎬 Multi-Frame Analysis:");
                Console.WriteLine($"  • Frames processed: {frames}");
                Console.WriteLine($"  • Stabilization: {stabQuality:P0}");
                Console.WriteLine($"  • Frame agreement: {agreement:P0}");
                Console.WriteLine();
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("⚠️  No text detected in image");
            Console.ResetColor();
            Console.WriteLine();
        }

        // Usage tips
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("💡 Tips:");
        Console.WriteLine("  • Use --output json for machine-readable output");
        Console.WriteLine("  • Use --pipeline quality for best accuracy (slower)");
        Console.WriteLine("  • Use --language es_ES to change spell-check language");
        Console.ResetColor();
    }

    static async Task ProcessImage(
        string imagePath,
        string pipeline,
        string outputFormat,
        string language,
        bool verbose,
        bool? llmParam = null,
        string? modelParam = null,
        string? ollamaParam = null,
        string? signalGlobs = null,
        DynamicPipeline? dynamicPipeline = null,
        bool allComputed = false)
    {
        if (!File.Exists(imagePath))
        {
            Console.Error.WriteLine($"Error: File not found: {imagePath}");
            Environment.Exit(1);
        }

        // Resolve 'auto' output format based on pipeline
        var resolvedOutput = outputFormat.ToLowerInvariant() == "auto"
            ? GetDefaultOutputForPipeline(pipeline)
            : outputFormat.ToLowerInvariant();

        // Setup DI
        var services = new ServiceCollection();

        // Logging - always register but configure level based on verbose flag
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning);
        });

        // Resolve LLM settings: CLI args > env vars > defaults
        var ollamaUrl = ollamaParam ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434";
        var visionModel = modelParam ?? Environment.GetEnvironmentVariable("VISION_MODEL") ?? "minicpm-v:8b";

        // For stats pipeline, keep it fast (no LLM); for others enable all features
        // CLI arg takes precedence
        var enableLlm = llmParam ?? (pipeline != "stats");

        // Configure image analysis based on pipeline
        services.AddDocSummarizerImages(opt =>
        {
            // OCR is disabled for stats pipeline, enabled for others
            opt.EnableOcr = pipeline != "stats";
            opt.Ocr.SpellCheckLanguage = language;
            opt.Ocr.PipelineName = pipeline; // Use JSON-configured pipeline
            opt.Ocr.UseAdvancedPipeline = true; // Enable advanced pipeline system
            opt.Ocr.TextDetectionConfidenceThreshold = 0; // Always run (controlled by pipeline config)

            // Enable Vision LLM by default for non-stats pipelines
            opt.EnableVisionLlm = enableLlm;
            opt.VisionLlmModel = visionModel;
            opt.OllamaBaseUrl = ollamaUrl;
        });

        var provider = services.BuildServiceProvider();

        // Use ProfiledWaveCoordinator for wave filtering based on pipeline
        var coordinator = provider.GetRequiredService<ProfiledWaveCoordinator>();
        var coordProfile = CoordinatorProfiles.GetByName(pipeline);

        try
        {
            DynamicImageProfile profile;
            var context = new AnalysisContext();

            // Use profiled coordinator for wave filtering
            var result = await coordinator.ExecuteAsync(imagePath, coordProfile, context);

            // Convert AnalysisResult signals to DynamicImageProfile
            profile = new DynamicImageProfile { ImagePath = imagePath };
            profile.AddSignals(result.Signals);
            profile.AnalysisDurationMs = (long)result.TotalDuration.TotalMilliseconds;

            // Signal-driven post-filtering if requested
            if (!string.IsNullOrWhiteSpace(signalGlobs) || dynamicPipeline?.UsesSignalSelection == true)
            {
                // Signals already captured from profiled execution
            }

            // For caption/alttext pipelines, always escalate to LLM
            // For fast pipelines, check signals for smart escalation
            string? llmCaption = null;
            var neverEscalate = pipeline is "stats" or "motion" or "streaming";
            var forceEscalate = pipeline is "caption" or "alttext" or "socialmediaalt" or "vision";

            // For auto/florence2: escalate only if florence2 signals suggest it
            var smartEscalate = pipeline is "auto" or "florence2" or "advancedocr" or "simpleocr";
            var shouldEscalate = profile.GetValue<bool>("florence2.should_escalate");

            if (enableLlm && !neverEscalate && (forceEscalate || (smartEscalate && shouldEscalate)))
            {
                var escalationService = provider.GetService<Mostlylucid.DocSummarizer.Images.Services.EscalationService>();
                if (escalationService != null)
                {
                    var escalationResult = await escalationService.AnalyzeWithEscalationAsync(
                        imagePath,
                        forceEscalate: forceEscalate,
                        enableOcr: pipeline != "stats");
                    llmCaption = escalationResult.LlmCaption;
                }
            }

            // Output results (pass signalGlobs for filtered output)
            switch (resolvedOutput)
            {
                case "json":
                    OutputJson(profile, llmCaption, signalGlobs, allComputed);
                    break;

                case "signals":
                    OutputSignals(profile, signalGlobs);
                    break;

                case "metrics":
                    OutputMetrics(profile);
                    break;

                case "caption":
                    OutputCaption(profile, llmCaption);
                    break;

                case "alttext":
                    OutputAltText(profile, llmCaption);
                    break;

                case "markdown":
                    OutputMarkdown(profile, llmCaption);
                    break;

                case "visual":
                    OutputVisual(profile, llmCaption);
                    break;

                case "auto":
                    OutputAdaptive(profile, llmCaption);
                    break;

                case "text":
                default:
                    OutputText(profile);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (verbose)
                Console.Error.WriteLine(ex.StackTrace);
            Environment.Exit(1);
        }
    }

    static string GetDefaultOutputForPipeline(string pipeline)
    {
        return pipeline switch
        {
            "auto" => "auto",       // Auto pipeline uses adaptive output
            "stats" => "metrics",
            "caption" => "caption",
            "alttext" => "alttext",
            "simpleocr" => "text",  // Simple OCR pipeline wants text output
            _ => "visual"           // Default to visual for full analysis display
        };
    }

    static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tiff", ".tif", ".tga", ".pbm" };

    static async Task ProcessDirectory(
        string directoryPath,
        string pipeline,
        string outputFormat,
        string language,
        bool verbose,
        bool? llmParam = null,
        string? modelParam = null,
        string? ollamaParam = null,
        string? signalGlobs = null,
        DynamicPipeline? dynamicPipeline = null,
        bool allComputed = false)
    {
        // Find all image files in directory
        var imageFiles = Directory.GetFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f)
            .ToList();

        if (imageFiles.Count == 0)
        {
            Spectre.Console.AnsiConsole.MarkupLine($"[yellow]No image files found in:[/] {Spectre.Console.Markup.Escape(directoryPath)}");
            return;
        }

        Spectre.Console.AnsiConsole.MarkupLine($"[cyan]Processing {imageFiles.Count} images in:[/] {Spectre.Console.Markup.Escape(directoryPath)}");
        Spectre.Console.AnsiConsole.WriteLine();

        var processed = 0;
        var failed = 0;

        foreach (var imagePath in imageFiles)
        {
            try
            {
                var fileName = Path.GetFileName(imagePath);
                Spectre.Console.AnsiConsole.MarkupLine($"[dim][[{processed + 1}/{imageFiles.Count}]][/] {Spectre.Console.Markup.Escape(fileName)}");

                await ProcessImage(imagePath, pipeline, outputFormat, language, verbose, llmParam, modelParam, ollamaParam, signalGlobs, dynamicPipeline, allComputed);
                processed++;
                Spectre.Console.AnsiConsole.WriteLine();
            }
            catch (Exception ex)
            {
                failed++;
                Spectre.Console.AnsiConsole.MarkupLine($"[red]✗[/] {Spectre.Console.Markup.Escape(Path.GetFileName(imagePath))}: {ex.Message}");
            }
        }

        Spectre.Console.AnsiConsole.WriteLine();
        Spectre.Console.AnsiConsole.MarkupLine($"[green]✓ Processed:[/] {processed}  [red]✗ Failed:[/] {failed}");
    }

    static void OutputText(DynamicImageProfile profile)
    {
        var output = new List<string>();

        // Show route info if using auto pipeline
        var route = profile.GetValue<string>("route.selected");
        if (!string.IsNullOrWhiteSpace(route))
        {
            var reason = profile.GetValue<string>("route.reason") ?? "";
            output.Add($"[{route.ToUpperInvariant()} route{(string.IsNullOrEmpty(reason) ? "" : $": {reason}")}]");
        }

        // Get OCR text
        var text = GetExtractedText(profile);
        if (!string.IsNullOrWhiteSpace(text))
        {
            output.Add(text.Trim());
        }

        // Get best caption (Vision LLM takes precedence over Florence2)
        var caption = profile.GetValue<string>("vision.llm.caption")
                   ?? profile.GetValue<string>("florence2.caption");
        if (!string.IsNullOrWhiteSpace(caption))
        {
            output.Add($"Caption: {caption}");
        }

        // Get scene classification
        var scene = profile.GetValue<string>("vision.llm.scene");
        if (!string.IsNullOrWhiteSpace(scene))
        {
            output.Add($"Scene: {scene}");
        }

        // Get motion info for animated images
        var motionType = profile.GetValue<string>("motion.type");
        var motionSummary = profile.GetValue<string>("motion.summary");
        if (!string.IsNullOrWhiteSpace(motionType) || !string.IsNullOrWhiteSpace(motionSummary))
        {
            var motionDesc = motionSummary ?? motionType ?? "";
            output.Add($"Motion: {motionDesc}");
        }

        if (output.Count > 0)
        {
            Console.WriteLine(string.Join("\n", output));
        }
        else
        {
            Console.Error.WriteLine("No content extracted");
            Environment.Exit(1);
        }
    }

    static void OutputJson(DynamicImageProfile profile, string? llmCaption = null, string? signalGlobs = null, bool allComputed = false)
    {
        var ledger = profile.GetLedger();

        // If specific signals requested via globs, output filtered or all computed
        if (!string.IsNullOrWhiteSpace(signalGlobs))
        {
            // Group by key and pick highest confidence signal for each key
            // (multiple waves can emit the same signal, e.g., vision.llm.caption from Florence2 and VisionLlm)
            var allSignals = profile.GetAllSignals()
                .GroupBy(s => s.Key)
                .ToDictionary(
                    g => g.Key,
                    g => {
                        var best = g.OrderByDescending(s => s.Confidence).First();
                        return new { value = best.Value, confidence = best.Confidence, source = best.Source };
                    });

            // When allComputed=true, show all computed signals with requested pattern noted
            // When false, filter to only matching signals
            var outputSignals = allComputed
                ? allSignals
                : SignalGlobMatcher.FilterSignals(profile, signalGlobs)
                    .GroupBy(s => s.Key)
                    .ToDictionary(
                        g => g.Key,
                        g => {
                            var best = g.OrderByDescending(s => s.Confidence).First();
                            return new { value = best.Value, confidence = best.Confidence, source = best.Source };
                        });

            var filteredResult = new
            {
                image = Path.GetFileName(profile.ImagePath),
                globs = signalGlobs,
                all_computed = allComputed,
                requested_count = SignalGlobMatcher.FilterSignals(profile, signalGlobs).Count(),
                signal_count = outputSignals.Count,
                waves_executed = profile.ContributingWaves,
                signals = outputSignals
            };

            var filteredOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            Console.WriteLine(JsonSerializer.Serialize(filteredResult, filteredOptions));
            return;
        }

        // Default: full JSON output
        var result = new
        {
            image = profile.ImagePath,
            duration_ms = profile.AnalysisDurationMs,
            waves = profile.ContributingWaves,
            text = GetExtractedText(profile),
            confidence = GetTextConfidence(profile),
            caption = llmCaption,
            quality = GetQualityMetrics(profile),
            metadata = GetMetadata(profile),
            // Include ledger for structured access to salient features
            ledger = new
            {
                identity = ledger.Identity,
                colors = ledger.Colors,
                text = ledger.Text,
                objects = ledger.Objects,
                motion = ledger.Motion,
                quality = ledger.Quality,
                composition = ledger.Composition,
                // Convenience fields for common use cases
                alt_text_context = ledger.ToAltTextContext(),
                llm_summary = ledger.ToLlmSummary()
            }
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        Console.WriteLine(JsonSerializer.Serialize(result, options));
    }

    /// <summary>
    /// Output an adaptive, detailed description based on image content.
    /// Adapts the output style to the image type (animated, text-heavy, photo, etc.)
    /// </summary>
    static void OutputAdaptive(DynamicImageProfile profile, string? llmCaption)
    {
        var ledger = profile.GetLedger();

        // Get the best caption
        var caption = llmCaption != null ? ExtractCaptionFromLlmResponse(llmCaption) : null;
        caption ??= profile.GetValue<string>("florence2.caption");
        caption ??= ledger.ToLlmSummary();

        // Clean up common prefixes
        if (!string.IsNullOrWhiteSpace(caption))
        {
            caption = caption.Trim();
            if (caption.StartsWith("In this image", StringComparison.OrdinalIgnoreCase))
            {
                caption = caption.Substring("In this image".Length).TrimStart(',', ' ');
                if (caption.Length > 0)
                    caption = char.ToUpper(caption[0]) + caption[1..];
            }
        }

        // Check image characteristics
        var isAnimated = ledger.Identity.IsAnimated;
        var ocrText = GetExtractedText(profile);
        var hasText = !string.IsNullOrWhiteSpace(ocrText);
        var motionSummary = profile.GetValue<string>("motion.summary");
        var hasMotion = !string.IsNullOrWhiteSpace(motionSummary);
        var scene = profile.GetValue<string>("vision.llm.scene");
        var route = profile.GetValue<string>("route.selected");

        var output = new List<string>();

        // Show route if using auto pipeline
        if (!string.IsNullOrEmpty(route))
        {
            var reason = profile.GetValue<string>("route.reason") ?? "";
            output.Add($"[{route.ToUpperInvariant()} route: {reason}]");
            output.Add("");
        }

        // Build adaptive description
        if (isAnimated && hasMotion)
        {
            // Animated image with motion
            var frameCount = ledger.Motion?.FrameCount ?? profile.GetValue<int>("identity.frame_count");
            output.Add($"📽️ Animated {ledger.Identity.Format} ({frameCount} frames)");
            output.Add("");
            output.Add(caption ?? "");

            if (!string.IsNullOrWhiteSpace(motionSummary))
            {
                output.Add("");
                output.Add($"Motion: {motionSummary}");
            }
        }
        else
        {
            // Standard photo/image (including text-heavy ones)
            output.Add(caption ?? "");
        }

        // ALWAYS show OCR text if available (summarize if >200 chars)
        if (hasText)
        {
            output.Add("");
            output.Add("📝 Text:");
            var displayText = Mostlylucid.DocSummarizer.Services.ShortTextSummarizer.Summarize(ocrText!.Trim(), 200);
            output.Add($"\"{displayText}\"");
        }

        // Add scene context if available and not already in caption
        if (!string.IsNullOrWhiteSpace(scene) &&
            !string.IsNullOrWhiteSpace(caption) &&
            !caption.ToLowerInvariant().Contains(scene.ToLowerInvariant()))
        {
            output.Add("");
            output.Add($"📍 Scene: {scene}");
        }

        Console.WriteLine(string.Join("\n", output).Trim());
    }

    static void OutputCaption(DynamicImageProfile profile, string? llmCaption)
    {
        if (!string.IsNullOrWhiteSpace(llmCaption))
        {
            // Extract and sanitize caption
            var cleanCaption = ExtractCaptionFromLlmResponse(llmCaption);
            if (!string.IsNullOrWhiteSpace(cleanCaption))
            {
                Console.WriteLine(cleanCaption);
                return;
            }
        }

        // Fallback to ledger summary if no LLM caption
        var ledger = profile.GetLedger();
        Console.WriteLine(ledger.ToLlmSummary());
    }

    static void OutputAltText(DynamicImageProfile profile, string? llmCaption)
    {
        var ledger = profile.GetLedger();
        var text = GetExtractedText(profile);

        // Build accessibility-focused alt text
        var parts = new List<string>();

        // Start with LLM caption if available, or ledger summary
        if (!string.IsNullOrWhiteSpace(llmCaption))
        {
            // Extract caption from JSON or use as plain text
            string captionText = ExtractCaptionFromLlmResponse(llmCaption);

            // Use full caption for alt text (not just first sentence) for better accessibility
            // Remove any trailing incomplete sentences
            captionText = captionText.Trim();
            if (captionText.EndsWith(",") || captionText.EndsWith(" and") || captionText.EndsWith(" with"))
            {
                var lastPeriod = captionText.LastIndexOf('.');
                if (lastPeriod > 0)
                    captionText = captionText[..(lastPeriod + 1)];
            }
            parts.Add(captionText);
        }
        else
        {
            // Use ledger's alt text context
            parts.Add(ledger.ToAltTextContext());
        }

        // Add motion context for animated images (critical for accessibility)
        if (ledger.Identity.IsAnimated && ledger.Motion != null)
        {
            if (ledger.Motion.MovingObjects.Count > 0)
            {
                parts.Add($"Animated, showing {string.Join(", ", ledger.Motion.MovingObjects)} in motion");
            }
            else if (!string.IsNullOrWhiteSpace(ledger.Motion.Summary))
            {
                parts.Add($"Animated with {ledger.Motion.Summary.ToLowerInvariant()}");
            }
            else if (ledger.Motion.HasMotion)
            {
                parts.Add($"Animated GIF with {ledger.Motion.MotionType ?? "general"} motion");
            }
            else
            {
                parts.Add($"Animated GIF ({ledger.Motion.FrameCount} frames)");
            }
        }

        // Add OCR text if present (important for accessibility)
        if (!string.IsNullOrWhiteSpace(text) && text.Length <= 100)
        {
            parts.Add($"Text: \"{text.Trim()}\"");
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            parts.Add($"Text: \"{text.Trim()[..97]}...\"");
        }

        Console.WriteLine(string.Join(". ", parts));
    }

    static void OutputMarkdown(DynamicImageProfile profile, string? llmCaption)
    {
        var ledger = profile.GetLedger();
        var text = GetExtractedText(profile);
        var fileName = Path.GetFileName(profile.ImagePath) ?? "image";

        Console.WriteLine($"# {fileName}");
        Console.WriteLine();

        // Image stats
        Console.WriteLine("## Image Analysis");
        Console.WriteLine($"- **Dimensions:** {ledger.Identity.Width}x{ledger.Identity.Height}");
        Console.WriteLine($"- **Format:** {ledger.Identity.Format}");

        // Get type detection from signals
        var detectedType = profile.GetValue<string>("content.type") ?? "Unknown";
        var typeConfidence = profile.GetValue<double>("content.type_confidence");
        Console.WriteLine($"- **Type:** {detectedType} ({typeConfidence:P0} confidence)");

        if (ledger.Quality.Sharpness.HasValue)
        {
            Console.WriteLine($"- **Sharpness:** {ledger.Quality.Sharpness:F0}");
        }
        Console.WriteLine();

        // Colors
        if (ledger.Colors.DominantColors?.Any() == true)
        {
            Console.WriteLine("## Colors");
            foreach (var color in ledger.Colors.DominantColors.Take(5))
            {
                Console.WriteLine($"- {color.Name} `{color.Hex}` ({color.Percentage:F1}%)");
            }
            Console.WriteLine();
        }

        // Caption
        if (!string.IsNullOrWhiteSpace(llmCaption))
        {
            var cleanCaption = ExtractCaptionFromLlmResponse(llmCaption);
            if (!string.IsNullOrWhiteSpace(cleanCaption))
            {
                Console.WriteLine("## Description");
                Console.WriteLine(cleanCaption);
                Console.WriteLine();
            }
        }

        // OCR text
        if (!string.IsNullOrWhiteSpace(text))
        {
            Console.WriteLine("## Extracted Text");
            Console.WriteLine("```");
            Console.WriteLine(text.Trim());
            Console.WriteLine("```");
            Console.WriteLine();
        }

        // Motion (for animated images)
        if (ledger.Identity.IsAnimated && ledger.Motion != null)
        {
            Console.WriteLine("## Animation");
            Console.WriteLine($"- **Frames:** {ledger.Motion.FrameCount}");
            if (ledger.Motion.Duration.HasValue)
            {
                Console.WriteLine($"- **Duration:** {ledger.Motion.Duration * 1000:F0}ms");
            }
            if (ledger.Motion.MotionIntensity > 0)
            {
                Console.WriteLine($"- **Motion Intensity:** {ledger.Motion.MotionIntensity:F2}");
            }
            Console.WriteLine();
        }
    }

    static void OutputVisual(DynamicImageProfile profile, string? llmCaption)
    {
        var ledger = profile.GetLedger();
        var text = GetExtractedText(profile);

        // Header with image info
        Spectre.Console.AnsiConsole.Write(new Spectre.Console.Rule($"[cyan]{Spectre.Console.Markup.Escape(Path.GetFileName(profile.ImagePath) ?? "Image")}[/]"));
        Spectre.Console.AnsiConsole.WriteLine();

        // Image preview (console rendering)
        if (!string.IsNullOrEmpty(profile.ImagePath) && File.Exists(profile.ImagePath))
        {
            Spectre.Console.AnsiConsole.MarkupLine("[yellow]Preview[/]");
            var isGrayscale = ledger.Colors.IsGrayscale;
            Services.ConsoleImageRenderer.RenderToConsole(profile.ImagePath, maxWidth: 60, maxHeight: 15, grayscale: isGrayscale);
            Spectre.Console.AnsiConsole.WriteLine();
        }

        // Identity panel
        var identityTable = new Spectre.Console.Table().Border(Spectre.Console.TableBorder.Rounded);
        identityTable.AddColumn("[dim]Property[/]");
        identityTable.AddColumn("[cyan]Value[/]");
        identityTable.AddRow("Format", ledger.Identity.Format ?? "Unknown");
        identityTable.AddRow("Dimensions", $"{ledger.Identity.Width}×{ledger.Identity.Height}");
        identityTable.AddRow("Aspect Ratio", $"{ledger.Identity.AspectRatio:F2}");
        if (ledger.Identity.IsAnimated)
        {
            identityTable.AddRow("Frames", ledger.Motion?.FrameCount.ToString() ?? "Multiple");
            identityTable.AddRow("[green]Animated[/]", "Yes");
        }
        identityTable.AddRow("Analysis Time", $"{profile.AnalysisDurationMs}ms");

        Spectre.Console.AnsiConsole.MarkupLine("[yellow]Identity[/]");
        Spectre.Console.AnsiConsole.Write(identityTable);
        Spectre.Console.AnsiConsole.WriteLine();

        // Color palette with actual colors
        if (ledger.Colors.DominantColors?.Any() == true)
        {
            Spectre.Console.AnsiConsole.MarkupLine("[yellow]Color Palette[/]");

            foreach (var color in ledger.Colors.DominantColors.Take(6))
            {
                // Parse hex color to RGB
                var hex = color.Hex?.TrimStart('#') ?? "000000";
                if (hex.Length == 6 &&
                    byte.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) &&
                    byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) &&
                    byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                {
                    var spectreColor = new Spectre.Console.Color(r, g, b);

                    // Create a colored block using markup
                    var colorSwatch = new Spectre.Console.Text("████", new Spectre.Console.Style(spectreColor));
                    var colorInfo = $" {color.Hex} {color.Name} ({color.Percentage:F1}%)";

                    Spectre.Console.AnsiConsole.Write(colorSwatch);
                    Spectre.Console.AnsiConsole.MarkupLine($"[dim]{Spectre.Console.Markup.Escape(colorInfo)}[/]");
                }
                else
                {
                    Spectre.Console.AnsiConsole.MarkupLine($"[dim]  {color.Hex} {color.Name} ({color.Percentage:F1}%)[/]");
                }
            }
            Spectre.Console.AnsiConsole.WriteLine();
        }

        // Caption (LLM or fallback) - sanitize to remove prompt leakage
        string? captionText = null;
        if (!string.IsNullOrWhiteSpace(llmCaption))
        {
            captionText = ExtractCaptionFromLlmResponse(llmCaption);
        }

        if (!string.IsNullOrWhiteSpace(captionText))
        {
            Spectre.Console.AnsiConsole.MarkupLine("[yellow]Caption[/]");
            Spectre.Console.AnsiConsole.Write(new Spectre.Console.Panel(Spectre.Console.Markup.Escape(captionText))
                .Border(Spectre.Console.BoxBorder.Rounded)
                .BorderColor(Spectre.Console.Color.Cyan1));
            Spectre.Console.AnsiConsole.WriteLine();
        }
        else
        {
            Spectre.Console.AnsiConsole.MarkupLine("[yellow]Summary[/]");
            Spectre.Console.AnsiConsole.MarkupLine($"[dim]{Spectre.Console.Markup.Escape(ledger.ToLlmSummary())}[/]");
            Spectre.Console.AnsiConsole.WriteLine();
        }

        // OCR text
        if (!string.IsNullOrWhiteSpace(text))
        {
            Spectre.Console.AnsiConsole.MarkupLine("[yellow]Extracted Text[/]");
            var textPanel = new Spectre.Console.Panel(Spectre.Console.Markup.Escape(text.Trim()))
                .Border(Spectre.Console.BoxBorder.Rounded)
                .Header("[green]OCR[/]");
            Spectre.Console.AnsiConsole.Write(textPanel);
            Spectre.Console.AnsiConsole.WriteLine();
        }

        // Motion info for animated images
        if (ledger.Identity.IsAnimated && ledger.Motion != null)
        {
            Spectre.Console.AnsiConsole.MarkupLine("[yellow]Animation[/]");
            var motionTable = new Spectre.Console.Table().Border(Spectre.Console.TableBorder.Rounded);
            motionTable.AddColumn("[dim]Property[/]");
            motionTable.AddColumn("[green]Value[/]");
            motionTable.AddRow("Frames", ledger.Motion.FrameCount.ToString());
            if (ledger.Motion.Duration.HasValue)
            {
                motionTable.AddRow("Duration", $"{ledger.Motion.Duration * 1000:F0}ms");
            }
            if (ledger.Motion.MotionIntensity > 0)
            {
                motionTable.AddRow("Motion Intensity", $"{ledger.Motion.MotionIntensity:F2}");
            }
            Spectre.Console.AnsiConsole.Write(motionTable);
            Spectre.Console.AnsiConsole.WriteLine();
        }

        // ALL SIGNALS - Expandable tree view grouped by wave/source
        OutputSignalsTree(profile);
    }

    static void OutputSignalsTree(DynamicImageProfile profile)
    {
        var allSignals = profile.GetAllSignals().ToList();
        if (!allSignals.Any()) return;

        Spectre.Console.AnsiConsole.MarkupLine("[yellow]All Signals[/] [dim]({0} total)[/]", allSignals.Count);

        // Group signals by source (wave)
        var signalsBySource = allSignals
            .GroupBy(s => s.Source ?? "Unknown")
            .OrderBy(g => g.Key);

        var tree = new Spectre.Console.Tree("[cyan]Signals[/]");

        foreach (var sourceGroup in signalsBySource)
        {
            var sourceNode = tree.AddNode($"[green]{Spectre.Console.Markup.Escape(sourceGroup.Key)}[/] [dim]({sourceGroup.Count()})[/]");

            // Group by key prefix (e.g., "vision.clip", "ocr.text")
            var keyGroups = sourceGroup
                .GroupBy(s => GetKeyPrefix(s.Key))
                .OrderBy(g => g.Key);

            foreach (var keyGroup in keyGroups)
            {
                var keyNode = sourceNode.AddNode($"[blue]{Spectre.Console.Markup.Escape(keyGroup.Key)}[/]");

                foreach (var signal in keyGroup.OrderBy(s => s.Key))
                {
                    var valueStr = FormatSignalValue(signal.Value);
                    var confStr = signal.Confidence < 1.0 ? $" [dim]({signal.Confidence:P0})[/]" : "";
                    var keyName = signal.Key.StartsWith(keyGroup.Key + ".")
                        ? signal.Key.Substring(keyGroup.Key.Length + 1)
                        : signal.Key;

                    keyNode.AddNode($"[dim]{Spectre.Console.Markup.Escape(keyName)}:[/] {Spectre.Console.Markup.Escape(valueStr)}{confStr}");
                }
            }
        }

        Spectre.Console.AnsiConsole.Write(tree);
        Spectre.Console.AnsiConsole.WriteLine();
    }

    static string GetKeyPrefix(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "misc";
        var parts = key.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : parts[0];
    }

    static string FormatSignalValue(object? value)
    {
        if (value == null) return "[null]";

        var type = value.GetType();

        // Primitive types - display directly
        if (type.IsPrimitive || value is string || value is decimal)
        {
            var str = value.ToString() ?? "";
            if (str.Length > 80)
                return str.Substring(0, 77) + "...";
            return str;
        }

        // DateTime
        if (value is DateTime dt)
            return dt.ToString("O");

        // Handle arrays (like embeddings, color lists)
        if (value is float[] floatArray)
        {
            if (floatArray.Length <= 5)
                return $"[{string.Join(", ", floatArray.Select(f => f.ToString("F3")))}]";
            return $"float[{floatArray.Length}]";
        }

        if (value is double[] doubleArray)
        {
            if (doubleArray.Length <= 5)
                return $"[{string.Join(", ", doubleArray.Select(d => d.ToString("F3")))}]";
            return $"double[{doubleArray.Length}]";
        }

        // Check if it's a collection (but not string)
        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            var items = enumerable.Cast<object>().ToList();
            if (items.Count == 0)
                return "[]";

            // For small collections, try to serialize to JSON
            if (items.Count <= 5)
            {
                try
                {
                    var json = JsonSerializer.Serialize(value,
                        new JsonSerializerOptions { WriteIndented = false });
                    if (json.Length <= 200)
                        return json;
                    return json.Substring(0, 197) + "...";
                }
                catch
                {
                    return $"[{items.Count} items]";
                }
            }

            return $"[{items.Count} items]";
        }

        // Complex objects - try JSON serialization
        try
        {
            var json = JsonSerializer.Serialize(value,
                new JsonSerializerOptions { WriteIndented = false });
            // Truncate very long JSON
            if (json.Length > 200)
                return json.Substring(0, 197) + "...";
            return json;
        }
        catch
        {
            // Fallback to ToString
            var str = value.ToString() ?? type.Name;
            if (str.Length > 80)
                return str.Substring(0, 77) + "...";
            return str;
        }
    }

    static void OutputSignals(DynamicImageProfile profile, string? signalGlobs = null)
    {
        IEnumerable<Signal> signals;

        // Filter by globs if provided
        if (!string.IsNullOrWhiteSpace(signalGlobs))
        {
            signals = SignalGlobMatcher.FilterSignals(profile, signalGlobs);
        }
        else
        {
            signals = profile.GetAllSignals();
        }

        var output = signals.Select(s => new
        {
            source = s.Source,
            key = s.Key,
            value = FormatSignalValue(s.Value),
            confidence = s.Confidence
        });

        var options = new JsonSerializerOptions { WriteIndented = true };
        Console.WriteLine(JsonSerializer.Serialize(output, options));
    }

    static void OutputMetrics(DynamicImageProfile profile)
    {
        var ledger = profile.GetLedger();

        var metrics = new
        {
            // Basic info
            file = Path.GetFileName(profile.ImagePath),
            analysis_duration_ms = profile.AnalysisDurationMs,
            waves_executed = profile.ContributingWaves.Count(),
            signals_emitted = profile.GetAllSignals().Count(),

            // Image identity
            identity = new
            {
                width = ledger.Identity.Width,
                height = ledger.Identity.Height,
                format = ledger.Identity.Format,
                aspect_ratio = ledger.Identity.AspectRatio,
                file_size = ledger.Identity.FileSize,
                is_animated = ledger.Identity.IsAnimated
            },

            // Quality metrics
            quality = new
            {
                sharpness = ledger.Quality.Sharpness,
                blur = ledger.Quality.Blur,
                exposure = ledger.Quality.Exposure.ToString(),
                overall = ledger.Quality.OverallQuality
            },

            // Color analysis
            colors = new
            {
                is_grayscale = ledger.Colors.IsGrayscale,
                saturation = ledger.Colors.MeanSaturation,
                dominant = ledger.Colors.DominantColors.Take(3).Select(c => new { c.Name, c.Hex, c.Percentage })
            },

            // Composition
            composition = new
            {
                edge_density = ledger.Composition.EdgeDensity,
                complexity = ledger.Composition.Complexity,
                brightness = ledger.Composition.Brightness,
                contrast = ledger.Composition.Contrast
            },

            // Text detection
            text = new
            {
                detected = !string.IsNullOrEmpty(ledger.Text.ExtractedText),
                length = ledger.Text.ExtractedText?.Length ?? 0,
                word_count = ledger.Text.WordCount,
                confidence = ledger.Text.Confidence,
                spell_check_score = ledger.Text.SpellCheckScore,
                is_garbled = ledger.Text.IsGarbled,
                text_likeliness = ledger.Text.TextLikeliness
            },

            // Objects
            objects = new
            {
                count = ledger.Objects.ObjectCount,
                types = ledger.Objects.ObjectTypes,
                faces = ledger.Objects.Faces.Count
            },

            // Motion (if animated)
            motion = ledger.Motion != null ? new
            {
                frame_count = ledger.Motion.FrameCount,
                duration = ledger.Motion.Duration,
                frame_rate = ledger.Motion.FrameRate,
                intensity = ledger.Motion.MotionIntensity
            } : null
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        Console.WriteLine(JsonSerializer.Serialize(metrics, options));
    }

    /// <summary>
    /// Extract caption text from LLM response, handling JSON or plain text formats.
    /// Sanitizes output to remove prompt leakage.
    /// </summary>
    static string ExtractCaptionFromLlmResponse(string llmResponse)
    {
        if (string.IsNullOrWhiteSpace(llmResponse))
            return "";

        string? rawCaption = null;

        // First, try to parse as valid JSON
        try
        {
            var doc = JsonDocument.Parse(llmResponse);
            // Check multiple property names
            foreach (var propName in new[] { "caption", "description", "scene", "summary" })
            {
                if (doc.RootElement.TryGetProperty(propName, out var prop))
                {
                    var val = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        rawCaption = val;
                        break;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // JSON parsing failed, try regex extraction
        }

        // Fallback: Try to extract caption from malformed JSON using regex
        if (rawCaption == null)
        {
            var captionMatch = System.Text.RegularExpressions.Regex.Match(
                llmResponse,
                @"""(?:caption|description)""\s*:\s*""([^""]+)""",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            if (captionMatch.Success && captionMatch.Groups.Count > 1)
            {
                rawCaption = captionMatch.Groups[1].Value;
            }
        }

        // If no JSON structure found, check if it's plain text
        if (rawCaption == null && !llmResponse.TrimStart().StartsWith("{"))
        {
            rawCaption = llmResponse.Trim();
        }

        // Last resort: Try to find any quoted string that looks like a caption
        if (rawCaption == null)
        {
            var anyQuotedMatch = System.Text.RegularExpressions.Regex.Match(
                llmResponse,
                @"""([A-Z][^""]{10,200})""");

            if (anyQuotedMatch.Success && anyQuotedMatch.Groups.Count > 1)
            {
                rawCaption = anyQuotedMatch.Groups[1].Value;
            }
        }

        // Sanitize and return
        return SanitizeCaption(rawCaption ?? "");
    }

    /// <summary>
    /// Remove prompt leakage and instruction text from captions.
    /// </summary>
    static string SanitizeCaption(string caption, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(caption))
            return "";

        var result = caption.Trim();

        // Common prompt leakage patterns to strip (comprehensive list)
        var leakagePatterns = new[]
        {
            // Long verbose patterns (check first - more specific)
            @"^Based on (?:the )?(?:provided |given )?(?:visual )?(?:information|image|analysis).*?(?:here's|here is).*?(?:description|caption|summary).*?[:,]\s*",
            @"^(?:Here is|Here's) (?:a |the )?(?:structured )?(?:output|description|caption|summary).*?(?:in )?(?:JSON )?(?:format)?.*?[:,]\s*",
            @"^.*?(?:in JSON format|JSON format that|structured description|structured output).*?[:,]\s*",
            @"^.*?captures the key.*?[:,]\s*",

            // "The provided/given image" patterns
            @"^(?:The |This )?(?:provided |given )?image (?:appears|seems) to (?:be |show |depict |display |feature |contain )?",
            @"^(?:The |This )?(?:provided |given )?image (?:shows|depicts|displays|features|contains|presents)\s*",
            @"^(?:The |This )?(?:provided |given )?image (?:is |appears to be )(?:a |an )?",

            // Standard patterns
            @"^Based on (?:the |this )?(provided |given )?image.*?[:,]\s*",
            @"^According to (?:the )?(?:provided |given )?(?:image|guidelines|analysis).*?[:,]\s*",
            @"^(?:The |This )?image (?:shows|depicts|displays|features|contains|presents)\s*",
            @"^In (?:the |this )?image,?\s*",
            @"^(?:Here is|Here's) (?:a|the) (?:caption|description).*?:\s*",
            @"^(?:Here is|Here's) (?:a |the )?(?:structured )?.*?[:,]\s*",
            @"^For accessibility[:,]\s*",
            @"^(?:Caption|Description|Summary):\s*",
            @"^\{[^}]*\}\s*", // Leading JSON
            @"^""[^""]*"":\s*""?", // Partial JSON key
            @"\s*\{[^}]*$", // Trailing incomplete JSON
            @"^```(?:json)?\s*", // Code block start
            @"\s*```$", // Code block end
            @"^I (?:can )?see\s+",
            @"^(?:Looking at (?:the|this) image,?\s*)?",
            @"^(?:Sure|Certainly|Of course)[!,.]?\s*",
            @"^(?:From|Given) (?:the|this) (?:image|visual|provided).*?[:,]\s*",
            @"^Using (?:the )?(?:provided |given )?image.*?[:,]\s*",
            @"\*\*(?:Caption|Description|Summary)\*\*:?\s*",  // Markdown bold headers
            @"^(?:\*\*)?(?:Caption|Description|Summary)(?:\*\*)?:?\s*",  // With or without markdown
            @"^(?:The )?(?:JSON )?output (?:generated|produced|created).*?(?:includes|contains|describes).*?[:,]\s*",
            @"^(?:The )?(?:generated |produced )?(?:JSON |structured )?(?:output|response|result).*?[:,]\s*",
            @"^(?:The )?image (?:provided|given|shown) (?:seems|appears) to (?:be |show |depict )?",
            @"^.*?(?:does not|doesn't) provide (?:clear |enough )?(?:visual )?(?:information|details).*?(?:that|to|for).*",
            @"^I (?:observed|noticed|can observe|can see) (?:several |some |many )?(?:notable |key |important )?(?:features|elements|things).*?[.:]\s*",
            @"^In this (?:outdoor |indoor )?(?:setting|scene|image).*?(?:featuring|showing|with)?\s*",
        };

        foreach (var pattern in leakagePatterns)
        {
            result = System.Text.RegularExpressions.Regex.Replace(
                result, pattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // Clean up quotes and whitespace
        result = result.Trim('"', '\'', ' ');

        // Capitalize first letter
        if (result.Length > 0 && char.IsLower(result[0]))
        {
            result = char.ToUpper(result[0]) + result[1..];
        }

        // Truncate if too long
        if (result.Length > maxLength)
        {
            var lastPeriod = result.LastIndexOf('.', maxLength - 1);
            if (lastPeriod > maxLength / 2)
                result = result[..(lastPeriod + 1)];
            else
            {
                var lastSpace = result.LastIndexOf(' ', maxLength - 1);
                result = lastSpace > maxLength / 2 ? result[..lastSpace] + "..." : result[..(maxLength - 3)] + "...";
            }
        }

        return result;
    }

    static string? GetExtractedText(DynamicImageProfile profile)
    {
        // Priority: Vision LLM text (best for stylized fonts) > Tier 2/3 corrections > voting > temporal median > raw OCR
        var visionText = profile.GetValue<string>("vision.llm.text");
        if (!string.IsNullOrEmpty(visionText))
            return visionText;
        if (profile.HasSignal("ocr.ml.fused_text"))
            return profile.GetValue<string>("ocr.ml.fused_text");
        if (profile.HasSignal("ocr.ml.text"))
            return profile.GetValue<string>("ocr.ml.text");
        if (profile.HasSignal("ocr.final.corrected_text"))
            return profile.GetValue<string>("ocr.final.corrected_text");
        if (profile.HasSignal("ocr.corrected.text"))
            return profile.GetValue<string>("ocr.corrected.text");
        if (profile.HasSignal("ocr.voting.consensus_text"))
            return profile.GetValue<string>("ocr.voting.consensus_text");
        if (profile.HasSignal("ocr.temporal_median.full_text"))
            return profile.GetValue<string>("ocr.temporal_median.full_text");
        if (profile.HasSignal("ocr.full_text"))
            return profile.GetValue<string>("ocr.full_text");
        return null;
    }

    static double GetTextConfidence(DynamicImageProfile profile)
    {
        if (profile.HasSignal("ocr.ml.fused_text"))
            return profile.GetBestSignal("ocr.ml.fused_text")?.Confidence ?? 0;
        if (profile.HasSignal("ocr.ml.text"))
            return profile.GetBestSignal("ocr.ml.text")?.Confidence ?? 0;
        if (profile.HasSignal("ocr.final.corrected_text"))
            return profile.GetBestSignal("ocr.final.corrected_text")?.Confidence ?? 0;
        if (profile.HasSignal("ocr.corrected.text"))
            return profile.GetBestSignal("ocr.corrected.text")?.Confidence ?? 0;
        if (profile.HasSignal("ocr.voting.consensus_text"))
            return profile.GetBestSignal("ocr.voting.consensus_text")?.Confidence ?? 0;
        if (profile.HasSignal("ocr.temporal_median.full_text"))
            return profile.GetBestSignal("ocr.temporal_median.full_text")?.Confidence ?? 0;
        if (profile.HasSignal("ocr.full_text"))
            return profile.GetBestSignal("ocr.full_text")?.Confidence ?? 0;
        return 0;
    }

    static object GetQualityMetrics(DynamicImageProfile profile)
    {
        return new
        {
            spell_check_score = profile.GetValue<double>("ocr.quality.spell_check_score"),
            is_garbled = profile.GetValue<bool>("ocr.quality.is_garbled"),
            text_likeliness = profile.GetValue<double>("content.text_likeliness")
        };
    }

    static object GetMetadata(DynamicImageProfile profile)
    {
        return new
        {
            frames_processed = profile.GetValue<int>("ocr.frames.extracted"),
            stabilization_quality = profile.GetValue<double>("ocr.stabilization.confidence"),
            frame_agreement = profile.GetValue<double>("ocr.voting.agreement_score")
        };
    }

    static async Task ExportFrameStrip(string imagePath, string? outputPath, int maxFrames, bool dedupe = false, string mode = "auto")
    {
        try
        {
            if (!File.Exists(imagePath))
            {
                Spectre.Console.AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Spectre.Console.Markup.Escape(imagePath)}");
                return;
            }

            using var image = await SixLabors.ImageSharp.Image.LoadAsync<SixLabors.ImageSharp.PixelFormats.Rgba32>(imagePath);

            if (image.Frames.Count <= 1)
            {
                Spectre.Console.AnsiConsole.MarkupLine("[yellow]Warning:[/] Image is not animated (single frame)");
                return;
            }

            var allFrames = new List<SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>>();
            for (int i = 0; i < image.Frames.Count; i++)
            {
                allFrames.Add(image.Frames.CloneFrame(i));
            }

            List<SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>> frames;
            string modeName;

            // Determine effective mode
            var effectiveMode = mode.ToLowerInvariant();
            if (effectiveMode == "auto")
            {
                // Auto-detect: if --dedupe is set, default to OCR mode for subtitle focus
                effectiveMode = dedupe ? "ocr" : "motion";
            }

            switch (effectiveMode)
            {
                case "ocr":
                    // OCR mode: Very aggressive deduplication, only keep frames where text changed
                    // Uses subtitle-aware similarity with very low threshold (0.85)
                    Spectre.Console.AnsiConsole.MarkupLine($"[dim]Deduplicating[/] [cyan]{allFrames.Count}[/] [dim]frames (OCR mode - text changes only)...[/]");
                    frames = DeduplicateFramesOcrMode(allFrames, 0.85);
                    modeName = "ocr";
                    Spectre.Console.AnsiConsole.MarkupLine($"  [green]Reduced to[/] [cyan]{frames.Count}[/] [green]unique text frames[/]");
                    break;

                case "motion":
                    // Motion mode: Keep keyframes showing full motion progression
                    // Uses standard similarity with moderate threshold (0.92)
                    Spectre.Console.AnsiConsole.MarkupLine($"[dim]Extracting[/] [cyan]{maxFrames}[/] [dim]keyframes from[/] [cyan]{allFrames.Count}[/] [dim]frames (motion mode)...[/]");
                    frames = ExtractMotionKeyframes(allFrames, maxFrames);
                    modeName = "motion";
                    Spectre.Console.AnsiConsole.MarkupLine($"  [green]Extracted[/] [cyan]{frames.Count}[/] [green]keyframes for motion inference[/]");
                    break;

                case "text-only":
                case "textonly":
                    // Text-only mode: Extract ONLY text bounding boxes for efficient OCR
                    // Creates a compact vertical strip of just the text regions
                    Spectre.Console.AnsiConsole.MarkupLine($"[dim]Extracting text bounding boxes from[/] [cyan]{allFrames.Count}[/] [dim]frames...[/]");

                    // Use the TextOnlyStripGenerator
                    var textOnlyGen = new Mostlylucid.DocSummarizer.Images.Services.Ocr.TextOnlyStripGenerator();
                    var textOnlyResult = await textOnlyGen.GenerateTextOnlyStripAsync(imagePath, maxFrames);

                    // Save the text-only strip
                    var textOnlySuffix = "_textonly_strip";
                    var textOnlyOutput = outputPath ?? Path.Combine(
                        Path.GetDirectoryName(imagePath) ?? ".",
                        Path.GetFileNameWithoutExtension(imagePath) + textOnlySuffix + ".png");

                    using (var textOnlyStream = File.Create(textOnlyOutput))
                    {
                        await textOnlyResult.StripImage.SaveAsync(textOnlyStream, new PngEncoder());
                    }

                    Spectre.Console.AnsiConsole.MarkupLine($"[green]✓ Saved text-only strip to:[/] [link]{Spectre.Console.Markup.Escape(textOnlyOutput)}[/]");
                    Spectre.Console.AnsiConsole.MarkupLine($"  [cyan]{textOnlyResult.TotalFrames}[/] frames → [cyan]{textOnlyResult.TextSegments}[/] segments → [cyan]{textOnlyResult.TextRegionsExtracted}[/] text regions");
                    Spectre.Console.AnsiConsole.MarkupLine($"  [dim]Clear frames detected:[/] [cyan]{textOnlyResult.ClearFramesDetected}[/]");
                    Spectre.Console.AnsiConsole.MarkupLine($"  [dim]Strip dimensions:[/] [cyan]{textOnlyResult.StripImage.Width}x{textOnlyResult.StripImage.Height}[/]");

                    textOnlyResult.StripImage.Dispose();
                    foreach (var f in allFrames) f.Dispose();
                    return; // Early return since we handled everything

                default:
                    // Fallback: evenly spaced frames
                    var step = Math.Max(1, allFrames.Count / maxFrames);
                    frames = new List<SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>>();
                    for (int i = 0; i < allFrames.Count && frames.Count < maxFrames; i += step)
                    {
                        frames.Add(allFrames[i]);
                    }
                    modeName = "strip";
                    // Dispose unused frames
                    foreach (var f in allFrames.Where(f => !frames.Contains(f)))
                        f.Dispose();
                    break;
            }

            if (frames.Count == 0)
            {
                Spectre.Console.AnsiConsole.MarkupLine("[red]Error:[/] No frames extracted");
                return;
            }

            // Create horizontal strip
            var frameWidth = frames[0].Width;
            var frameHeight = frames[0].Height;
            var stripWidth = frameWidth * frames.Count;

            using var strip = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(stripWidth, frameHeight);

            int xOffset = 0;
            foreach (var frame in frames)
            {
                strip.Mutate(ctx => ctx.DrawImage(frame, new SixLabors.ImageSharp.Point(xOffset, 0), 1f));
                xOffset += frameWidth;
                frame.Dispose();
            }

            // Determine output path
            var suffix = $"_{modeName}_strip";
            var finalOutput = outputPath ?? Path.Combine(
                Path.GetDirectoryName(imagePath) ?? ".",
                Path.GetFileNameWithoutExtension(imagePath) + suffix + ".png");

            using var fileStream = File.Create(finalOutput);
            var encoder = new PngEncoder();
            await strip.SaveAsync(fileStream, encoder);
            Spectre.Console.AnsiConsole.MarkupLine($"[green]✓ Saved {modeName} strip to:[/] [link]{Spectre.Console.Markup.Escape(finalOutput)}[/]");
            Spectre.Console.AnsiConsole.MarkupLine($"  [dim]Dimensions:[/] [cyan]{strip.Width}x{strip.Height}[/] ([cyan]{frames.Count}[/] frames)");
        }
        catch (Exception ex)
        {
            Spectre.Console.AnsiConsole.MarkupLine($"[red]Error:[/] {Spectre.Console.Markup.Escape(ex.Message)}");
        }
    }

    /// <summary>
    /// OCR mode deduplication - aggressive, only keeps frames where text region changed significantly
    /// </summary>
    static List<SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>> DeduplicateFramesOcrMode(
        List<SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>> frames,
        double threshold)
    {
        if (frames.Count <= 1) return frames;

        var deduplicated = new List<SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>> { frames[0] };

        for (int i = 1; i < frames.Count; i++)
        {
            var currentFrame = frames[i];
            var lastFrame = deduplicated[^1];

            // Use subtitle-aware similarity - very sensitive to text changes
            var similarity = CalculateSubtitleAwareSimilarity(lastFrame, currentFrame);

            if (similarity < threshold)
            {
                // Frame is sufficiently different (text changed), keep it
                deduplicated.Add(currentFrame);
            }
            else
            {
                // Dispose duplicate
                currentFrame.Dispose();
            }
        }

        return deduplicated;
    }

    /// <summary>
    /// Motion mode - extract keyframes showing complete motion sequence
    /// Uses motion-based sampling to capture key moments of movement
    /// </summary>
    static List<SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>> ExtractMotionKeyframes(
        List<SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>> frames,
        int maxFrames)
    {
        if (frames.Count <= maxFrames)
            return frames;

        // Calculate motion scores between consecutive frames
        var motionScores = new List<(int index, double score)>();
        for (int i = 1; i < frames.Count; i++)
        {
            var score = CalculateMotionScore(frames[i - 1], frames[i]);
            motionScores.Add((i, score));
        }

        // Always include first and last frame
        var selectedIndices = new HashSet<int> { 0, frames.Count - 1 };

        // Select frames at high-motion moments (peaks)
        var peakFrames = motionScores
            .OrderByDescending(m => m.score)
            .Take(maxFrames - 2)
            .Select(m => m.index)
            .ToList();

        foreach (var idx in peakFrames)
        {
            selectedIndices.Add(idx);
        }

        // If we still need more frames, add evenly spaced ones
        if (selectedIndices.Count < maxFrames)
        {
            var step = frames.Count / (maxFrames - selectedIndices.Count + 1);
            for (int i = step; i < frames.Count && selectedIndices.Count < maxFrames; i += step)
            {
                selectedIndices.Add(i);
            }
        }

        // Sort indices and select frames
        var sortedIndices = selectedIndices.OrderBy(i => i).Take(maxFrames).ToList();
        var keyframes = sortedIndices.Select(i => frames[i]).ToList();

        // Dispose unused frames
        for (int i = 0; i < frames.Count; i++)
        {
            if (!sortedIndices.Contains(i))
                frames[i].Dispose();
        }

        return keyframes;
    }

    /// <summary>
    /// Calculate motion score between two frames (higher = more motion)
    /// </summary>
    static double CalculateMotionScore(
        SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> frame1,
        SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> frame2)
    {
        const int sampleStep = 4;
        double totalDiff = 0;
        int sampleCount = 0;

        for (int y = 0; y < frame1.Height; y += sampleStep)
        {
            for (int x = 0; x < frame1.Width; x += sampleStep)
            {
                var p1 = frame1[x, y];
                var p2 = frame2[x, y];

                // RGB difference
                var diff = Math.Abs(p1.R - p2.R) + Math.Abs(p1.G - p2.G) + Math.Abs(p1.B - p2.B);
                totalDiff += diff;
                sampleCount++;
            }
        }

        // Normalize to 0-1 range (max diff per pixel is 765 = 255*3)
        return sampleCount > 0 ? totalDiff / sampleCount / 765.0 : 0;
    }

    /// <summary>
    /// Calculate subtitle-aware similarity between two frames
    /// </summary>
    static double CalculateSubtitleAwareSimilarity(
        SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> frame1,
        SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> frame2)
    {
        const int sampleStep = 4;
        var subtitleRegionStart = (int)(frame1.Height * 0.75);
        const double textBrightnessThreshold = 200.0;

        double mainDiff = 0, subtitleDiff = 0, textColorDiff = 0;
        int mainCount = 0, subtitleCount = 0, textCount = 0;

        for (int y = 0; y < frame1.Height; y += sampleStep)
        {
            for (int x = 0; x < frame1.Width; x += sampleStep)
            {
                var p1 = frame1[x, y];
                var p2 = frame2[x, y];

                var lum1 = 0.299 * p1.R + 0.587 * p1.G + 0.114 * p1.B;
                var lum2 = 0.299 * p2.R + 0.587 * p2.G + 0.114 * p2.B;
                var diff = Math.Abs(lum1 - lum2);

                if (y >= subtitleRegionStart)
                {
                    if (lum1 > textBrightnessThreshold || lum2 > textBrightnessThreshold)
                    {
                        textColorDiff += diff * 3.0;
                        textCount++;
                    }
                    subtitleDiff += diff;
                    subtitleCount++;
                }
                else
                {
                    mainDiff += diff;
                    mainCount++;
                }
            }
        }

        var mainSim = 1.0 - (mainCount > 0 ? mainDiff / mainCount / 255.0 : 0);
        var subtitleSim = 1.0 - (subtitleCount > 0 ? subtitleDiff / subtitleCount / 255.0 : 0);
        var textSim = 1.0 - Math.Min(textCount > 0 ? textColorDiff / textCount / 255.0 : 0, 1.0);

        return (mainSim * 0.3) + (subtitleSim * 0.4) + (textSim * 0.3);
    }

    static async Task ListPipelines()
    {
        try
        {
            var pipelineService = new PipelineService();
            var config = await pipelineService.LoadPipelinesAsync();

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n╔═══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║              Available OCR Pipelines                     ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════╝\n");
            Console.ResetColor();

            foreach (var pipeline in config.Pipelines)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($"  {pipeline.Name}");
                if (pipeline.IsDefault)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write(" (default)");
                }
                Console.WriteLine();
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine($"    {pipeline.DisplayName}");
                Console.ResetColor();

                if (!string.IsNullOrEmpty(pipeline.Description))
                {
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.WriteLine($"    {pipeline.Description}");
                    Console.ResetColor();
                }

                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine($"    ⏱️  ~{pipeline.EstimatedDurationSeconds:F1}s");
                if (pipeline.AccuracyImprovement.HasValue && pipeline.AccuracyImprovement > 0)
                {
                    Console.WriteLine($"    📈 +{pipeline.AccuracyImprovement:F0}% accuracy");
                }
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    Phases: {pipeline.Phases.Count} ({string.Join(", ", pipeline.Phases.Take(3).Select(p => p.Name))}{(pipeline.Phases.Count > 3 ? "..." : "")})");
                Console.ResetColor();

                Console.WriteLine();
            }

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("Usage: imagesummarizer image.gif --pipeline <name>");
            Console.WriteLine($"Default: {config.DefaultPipeline ?? "advancedocr"}");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Error loading pipelines: {ex.Message}");
            Console.ResetColor();
            Environment.Exit(1);
        }
    }

    static void ListSignals()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n╔═══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║          Available Signals & Collections                  ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════╝\n");
        Console.ResetColor();

        // Show predefined collections from SignalGlobMatcher
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  📦 Predefined Collections (use @name):");
        Console.ResetColor();

        var collections = SignalGlobMatcher.GetCollections();
        foreach (var collection in collections.OrderBy(c => c.Key))
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($"    @{collection.Key,-12}");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($" → {string.Join(", ", collection.Value)}");
            Console.ResetColor();
        }

        Console.WriteLine();

        // Show signals by wave (from WaveRegistry)
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  🌊 Signals by Wave (emits → requires/optional):");
        Console.ResetColor();

        // Use WaveRegistry for accurate manifest data
        foreach (var manifest in WaveRegistry.Manifests.OrderBy(m => m.Priority))
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"    {manifest.WaveName,-22}");
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($" P{manifest.Priority} tags: [{string.Join(", ", manifest.Tags)}]");
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"      ↳ emits:   {string.Join(", ", manifest.Emits.Take(4))}{(manifest.Emits.Count > 4 ? "..." : "")}");
            Console.ResetColor();

            if (manifest.Requires.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"      ↳ requires: {string.Join(", ", manifest.Requires)}");
                Console.ResetColor();
            }

            if (manifest.Optional.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"      ↳ optional: {string.Join(", ", manifest.Optional.Take(3))}{(manifest.Optional.Count > 3 ? "..." : "")}");
                Console.ResetColor();
            }
        }

        Console.WriteLine();

        // Show glob pattern examples
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  📝 Glob Pattern Examples:");
        Console.ResetColor();

        var examples = new[]
        {
            ("motion.*", "All motion signals"),
            ("color.dominant*", "Prefix match: dominant colors"),
            ("vision.llm.caption", "Exact match: LLM caption only"),
            ("@motion,color.*", "Collection + pattern: motion and all colors"),
            ("@alttext", "Collection: signals for alt text generation"),
            ("*", "All signals (runs full pipeline)")
        };

        foreach (var (pattern, desc) in examples)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"    --signals \"{pattern}\"");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"  # {desc}");
            Console.ResetColor();
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  Usage: imagesummarizer image.gif --signals \"motion.*,color.dominant*\" --output json");
        Console.WriteLine("  Tip: Use @tool collection for MCP/automation tool calls");
        Console.ResetColor();
    }

    static void ShowSignalBreakdown(DynamicImageProfile profile)
    {
        var signals = profile.GetAllSignals().ToList();

        // Group signals by category (first part of key before '.')
        var grouped = signals
            .GroupBy(s => s.Key.Split('.')[0])
            .OrderByDescending(g => g.Count())
            .ToList();

        Spectre.Console.AnsiConsole.Write(new Spectre.Console.Rule("[cyan]📡 Signal Breakdown[/]"));
        Spectre.Console.AnsiConsole.WriteLine();

        var table = new Spectre.Console.Table();
        table.Border = Spectre.Console.TableBorder.Rounded;
        table.AddColumn(new Spectre.Console.TableColumn("[cyan]Category[/]"));
        table.AddColumn(new Spectre.Console.TableColumn("[yellow]Signals[/]"));
        table.AddColumn(new Spectre.Console.TableColumn("[dim]Key Signals[/]"));

        foreach (var group in grouped)
        {
            var category = group.Key;
            var count = group.Count();
            var examples = string.Join(", ", group.Take(3).Select(s => s.Key.Split('.').Last()));

            table.AddRow(
                $"[cyan]{Spectre.Console.Markup.Escape(category)}[/]",
                $"[yellow]{count}[/]",
                $"[dim]{Spectre.Console.Markup.Escape(examples)}...[/]");
        }

        Spectre.Console.AnsiConsole.Write(table);
    }

    static async Task RunMcpServer()
    {
        var builder = Host.CreateApplicationBuilder();

        // Log to stderr to avoid interfering with stdio MCP protocol
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        // Register MCP server with auto-discovery
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        var app = builder.Build();
        await app.RunAsync();
    }

    static async Task ListOllamaModels(string ollamaUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetAsync($"{ollamaUrl}/api/tags");

            if (!response.IsSuccessStatusCode)
            {
                Spectre.Console.AnsiConsole.MarkupLine($"[red]✗[/] Failed to connect to Ollama at {ollamaUrl}");
                Spectre.Console.AnsiConsole.MarkupLine("[dim]Make sure Ollama is running: ollama serve[/]");
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            Spectre.Console.AnsiConsole.MarkupLine($"[cyan]Available models at {ollamaUrl}:[/]");
            Spectre.Console.AnsiConsole.WriteLine();

            var visionModels = new List<string>();
            var otherModels = new List<string>();

            if (doc.RootElement.TryGetProperty("models", out var models))
            {
                foreach (var model in models.EnumerateArray())
                {
                    var name = model.GetProperty("name").GetString() ?? "";
                    var size = model.TryGetProperty("size", out var s) ? s.GetInt64() / (1024 * 1024 * 1024.0) : 0;

                    // Vision models typically have these keywords
                    var isVision = name.Contains("llava", StringComparison.OrdinalIgnoreCase) ||
                                   name.Contains("vision", StringComparison.OrdinalIgnoreCase) ||
                                   name.Contains("minicpm-v", StringComparison.OrdinalIgnoreCase) ||
                                   name.Contains("bakllava", StringComparison.OrdinalIgnoreCase) ||
                                   name.Contains("moondream", StringComparison.OrdinalIgnoreCase) ||
                                   name.Contains("llava-phi", StringComparison.OrdinalIgnoreCase);

                    if (isVision)
                        visionModels.Add($"  [green]★[/] [cyan]{name}[/] [dim]({size:F1} GB) - Vision capable[/]");
                    else
                        otherModels.Add($"  [dim]  {name} ({size:F1} GB)[/]");
                }
            }

            if (visionModels.Count > 0)
            {
                Spectre.Console.AnsiConsole.MarkupLine("[yellow]Vision Models (recommended):[/]");
                foreach (var m in visionModels)
                    Spectre.Console.AnsiConsole.MarkupLine(m);
                Spectre.Console.AnsiConsole.WriteLine();
            }

            if (otherModels.Count > 0)
            {
                Spectre.Console.AnsiConsole.MarkupLine("[dim]Other Models:[/]");
                foreach (var m in otherModels.Take(10))
                    Spectre.Console.AnsiConsole.MarkupLine(m);
                if (otherModels.Count > 10)
                    Spectre.Console.AnsiConsole.MarkupLine($"[dim]  ... and {otherModels.Count - 10} more[/]");
                Spectre.Console.AnsiConsole.WriteLine();
            }

            if (visionModels.Count == 0 && otherModels.Count == 0)
            {
                Spectre.Console.AnsiConsole.MarkupLine("[yellow]No models found. Pull a vision model with:[/]");
                Spectre.Console.AnsiConsole.MarkupLine("[dim]  ollama pull minicpm-v:8b[/]");
                Spectre.Console.AnsiConsole.MarkupLine("[dim]  ollama pull llava:7b[/]");
            }
            else if (visionModels.Count == 0)
            {
                Spectre.Console.AnsiConsole.MarkupLine("[yellow]No vision models found. Install one with:[/]");
                Spectre.Console.AnsiConsole.MarkupLine("[dim]  ollama pull minicpm-v:8b[/]");
            }
        }
        catch (HttpRequestException)
        {
            Spectre.Console.AnsiConsole.MarkupLine($"[red]✗[/] Cannot connect to Ollama at {ollamaUrl}");
            Spectre.Console.AnsiConsole.MarkupLine("[dim]Make sure Ollama is running: ollama serve[/]");
        }
        catch (TaskCanceledException)
        {
            Spectre.Console.AnsiConsole.MarkupLine($"[red]✗[/] Timeout connecting to Ollama at {ollamaUrl}");
        }
        catch (Exception ex)
        {
            Spectre.Console.AnsiConsole.MarkupLine($"[red]✗[/] Error: {ex.Message}");
        }
    }
}
