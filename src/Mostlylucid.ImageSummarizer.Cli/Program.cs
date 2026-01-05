using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Extensions;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Pipelines;
using SixLabors.ImageSharp.Processing;
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
            getDefaultValue: () => "advancedocr",
            description: "Pipeline: advancedocr, simpleocr, quality, stats, caption, alttext");

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

        rootCommand.AddArgument(imageArg);
        rootCommand.AddOption(pipelineOpt);
        rootCommand.AddOption(outputOpt);
        rootCommand.AddOption(languageOpt);
        rootCommand.AddOption(verboseOpt);
        rootCommand.AddOption(llmOpt);
        rootCommand.AddOption(modelOpt);
        rootCommand.AddOption(ollamaOpt);
        rootCommand.AddCommand(listCmd);

        rootCommand.SetHandler(async (string? inputPath, string pipeline, string output, string language, bool verbose, bool? llm, string? model, string? ollama) =>
        {
            // If no path provided, enter interactive mode with the configured options
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                await InteractiveMode(pipeline, output, language, verbose, llm, model, ollama);
            }
            else if (Directory.Exists(inputPath))
            {
                // Process all images in directory
                await ProcessDirectory(inputPath, pipeline, output, language, verbose, llm, model, ollama);
            }
            else
            {
                await ProcessImage(inputPath, pipeline, output, language, verbose, llm, model, ollama);
            }
        }, imageArg, pipelineOpt, outputOpt, languageOpt, verboseOpt, llmOpt, modelOpt, ollamaOpt);

        return await rootCommand.InvokeAsync(args);
    }

    static async Task<int> InteractiveMode(
        string pipeline = "advancedocr",
        string output = "text",
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
                            Spectre.Console.AnsiConsole.MarkupLine("[dim]Available: advancedocr, simpleocr, quality, stats, caption, alttext[/]");
                        }
                        else if (arg is "advancedocr" or "simpleocr" or "quality" or "stats" or "caption" or "alttext")
                        {
                            pipeline = arg;
                            Spectre.Console.AnsiConsole.MarkupLine($"[green]✓[/] Pipeline set to [cyan]{pipeline}[/]");
                            // Auto-set output format for specialized pipelines
                            if (arg == "stats" && output == "auto") output = "metrics";
                            if (arg == "caption" && output == "auto") output = "caption";
                            if (arg == "alttext" && output == "auto") output = "alttext";
                        }
                        else
                        {
                            Spectre.Console.AnsiConsole.MarkupLine($"[red]✗[/] Unknown pipeline: {arg}. Use: advancedocr, simpleocr, quality, stats, caption, alttext");
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
        string? ollamaParam = null)
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
        var orchestrator = provider.GetRequiredService<WaveOrchestrator>();

        try
        {
            // Analyze image
            var profile = await orchestrator.AnalyzeAsync(imagePath);

            // For caption/alttext pipelines, always escalate to LLM
            // For others, escalate if auto-escalation conditions are met
            string? llmCaption = null;
            if (enableLlm)
            {
                var escalationService = provider.GetService<Mostlylucid.DocSummarizer.Images.Services.EscalationService>();
                if (escalationService != null)
                {
                    // Force escalation for caption/alttext, auto-escalate for others
                    var forceEscalate = pipeline is "caption" or "alttext";
                    var result = await escalationService.AnalyzeWithEscalationAsync(
                        imagePath,
                        forceEscalate: forceEscalate,
                        enableOcr: pipeline != "stats");
                    llmCaption = result.LlmCaption;
                }
            }

            // Output results
            switch (resolvedOutput)
            {
                case "json":
                    OutputJson(profile, llmCaption);
                    break;

                case "signals":
                    OutputSignals(profile);
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
        string? ollamaParam = null)
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

                await ProcessImage(imagePath, pipeline, outputFormat, language, verbose, llmParam, modelParam, ollamaParam);
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
        // Extract best text (same priority as GetExtractedText)
        var text = GetExtractedText(profile);

        if (!string.IsNullOrWhiteSpace(text))
        {
            Console.WriteLine(text.Trim());
        }
        else
        {
            Console.Error.WriteLine("No text extracted");
            Environment.Exit(1);
        }
    }

    static void OutputJson(DynamicImageProfile profile, string? llmCaption = null)
    {
        var ledger = profile.GetLedger();

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

    static void OutputCaption(DynamicImageProfile profile, string? llmCaption)
    {
        if (!string.IsNullOrWhiteSpace(llmCaption))
        {
            // Try to parse as JSON and extract caption field
            try
            {
                var doc = JsonDocument.Parse(llmCaption);
                if (doc.RootElement.TryGetProperty("caption", out var captionProp))
                {
                    Console.WriteLine(captionProp.GetString());
                    return;
                }
            }
            catch (JsonException)
            {
                // Not JSON, use as-is
            }

            // Use as-is if not JSON or no caption field
            Console.WriteLine(llmCaption);
        }
        else
        {
            // Fallback to ledger summary if no LLM caption
            var ledger = profile.GetLedger();
            Console.WriteLine(ledger.ToLlmSummary());
        }
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
            // Try to parse JSON caption
            string captionText = llmCaption;
            try
            {
                var doc = JsonDocument.Parse(llmCaption);
                if (doc.RootElement.TryGetProperty("caption", out var captionProp))
                {
                    captionText = captionProp.GetString() ?? llmCaption;
                }
            }
            catch (JsonException) { }

            // Extract first sentence for concise alt text
            var firstSentence = captionText.Split(new[] { '.', '!', '?' }, 2)[0].Trim();
            parts.Add(firstSentence);
        }
        else
        {
            // Use ledger's alt text context
            parts.Add(ledger.ToAltTextContext());
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
            Console.WriteLine("## Description");
            Console.WriteLine(llmCaption);
            Console.WriteLine();
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

        // Caption (LLM or fallback)
        string? captionText = llmCaption;
        if (!string.IsNullOrWhiteSpace(llmCaption))
        {
            try
            {
                var doc = JsonDocument.Parse(llmCaption);
                if (doc.RootElement.TryGetProperty("caption", out var captionProp))
                {
                    captionText = captionProp.GetString();
                }
            }
            catch (JsonException) { }
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

        // Handle arrays (like embeddings, color lists)
        if (value is float[] floatArray)
        {
            if (floatArray.Length <= 5)
                return $"[{string.Join(", ", floatArray.Select(f => f.ToString("F3")))}]";
            return $"[{floatArray.Length}D vector: {floatArray[0]:F3}, {floatArray[1]:F3}...{floatArray[^1]:F3}]";
        }

        if (value is double[] doubleArray)
        {
            if (doubleArray.Length <= 5)
                return $"[{string.Join(", ", doubleArray.Select(d => d.ToString("F3")))}]";
            return $"[{doubleArray.Length}D vector]";
        }

        if (value is IEnumerable<object> enumerable && value is not string)
        {
            var list = enumerable.ToList();
            if (list.Count <= 3)
                return $"[{string.Join(", ", list.Select(o => o?.ToString() ?? "null"))}]";
            return $"[{list.Count} items]";
        }

        // Truncate long strings
        var str = value.ToString() ?? "";
        if (str.Length > 80)
            return str.Substring(0, 77) + "...";

        return str;
    }

    static void OutputSignals(DynamicImageProfile profile)
    {
        var signals = profile.GetAllSignals()
            .Select(s => new
            {
                source = s.Source,
                key = s.Key,
                value = s.Value?.ToString(),
                confidence = s.Confidence
            });

        var options = new JsonSerializerOptions { WriteIndented = true };
        Console.WriteLine(JsonSerializer.Serialize(signals, options));
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

    static string? GetExtractedText(DynamicImageProfile profile)
    {
        // Priority: Vision LLM text (best for stylized fonts) > Tier 2/3 corrections > voting > temporal median > raw OCR
        var visionText = profile.GetValue<string>("vision.llm.text");
        if (!string.IsNullOrEmpty(visionText))
            return visionText;
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
