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

        // Interactive mode when no arguments provided
        if (args.Length == 0)
        {
            return await InteractiveMode();
        }

        var rootCommand = new RootCommand("Image OCR and Analysis CLI - Extract text and analyze images");

        // Arguments
        var imageArg = new Argument<string>("image", "Path to image file (all ImageSharp formats: JPEG, PNG, GIF, BMP, TIFF, TGA, WebP, PBM)");

        // Options
        var pipelineOpt = new Option<string>(
            "--pipeline",
            getDefaultValue: () => "advancedocr",
            description: "Pipeline to use: advancedocr, simpleocr, quality");

        var outputOpt = new Option<string>(
            "--output",
            getDefaultValue: () => "text",
            description: "Output format: text, json, signals, metrics");

        var languageOpt = new Option<string>(
            "--language",
            getDefaultValue: () => "en_US",
            description: "Language for spell checking");

        var verboseOpt = new Option<bool>(
            "--verbose",
            getDefaultValue: () => false,
            description: "Enable verbose logging");

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
        rootCommand.AddCommand(listCmd);

        rootCommand.SetHandler(async (string imagePath, string pipeline, string output, string language, bool verbose) =>
        {
            await ProcessImage(imagePath, pipeline, output, language, verbose);
        }, imageArg, pipelineOpt, outputOpt, languageOpt, verboseOpt);

        return await rootCommand.InvokeAsync(args);
    }

    static async Task<int> InteractiveMode()
    {
        // Banner
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════╗
║                   ImageCli - OCR Tool                     ║
║         Advanced multi-frame OCR with spell-check         ║
╚═══════════════════════════════════════════════════════════╝
");
        Console.ResetColor();

        // Prompt for image path
        Console.Write("📁 Enter image path (or drag & drop file): ");
        var imagePath = Console.ReadLine()?.Trim().Trim('"') ?? "";

        if (string.IsNullOrWhiteSpace(imagePath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("❌ No image path provided");
            Console.ResetColor();
            return 1;
        }

        if (!File.Exists(imagePath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"❌ File not found: {imagePath}");
            Console.ResetColor();
            return 1;
        }

        // Setup DI
        var services = new ServiceCollection();
        services.AddDocSummarizerImages(opt =>
        {
            opt.EnableOcr = true;
            opt.Ocr.UseAdvancedPipeline = true;
            opt.Ocr.QualityMode = OcrQualityMode.Fast;
            opt.Ocr.TextDetectionConfidenceThreshold = 0;
            opt.Ocr.EnableStabilization = true;
            opt.Ocr.EnableTemporalMedian = true;
            opt.Ocr.EnableTemporalVoting = true;
            opt.Ocr.EnableSpellChecking = true;
            opt.Ocr.SpellCheckLanguage = "en_US";

            // Enable Vision LLM for Tier 3 Sentinel correction
            opt.EnableVisionLlm = true;
            opt.VisionLlmModel = "minicpm-v:8b";
            opt.OllamaBaseUrl = "http://localhost:11434";
        });

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<WaveOrchestrator>();

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("⏳ Analyzing image");
        Console.ResetColor();

        // Animate while processing
        var cts = new CancellationTokenSource();
        var animationTask = Task.Run(() => AnimateSpinner(cts.Token));

        try
        {
            var profile = await orchestrator.AnalyzeAsync(imagePath);
            cts.Cancel();
            await animationTask;

            Console.WriteLine(" ✓");
            Console.WriteLine();

            // Show ASCII preview
            ShowAsciiPreview(imagePath);

            // Show analysis results
            ShowInteractiveResults(profile);

            return 0;
        }
        catch (Exception ex)
        {
            cts.Cancel();
            await animationTask;
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"❌ Error: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
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

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("┌" + new string('─', targetWidth + 2) + "┐");

            for (int y = 0; y < image.Height; y++)
            {
                Console.Write("│ ");
                for (int x = 0; x < image.Width; x++)
                {
                    var pixel = image[x, y];
                    var brightness = (pixel.R + pixel.G + pixel.B) / 3;

                    // ASCII gradient
                    var asciiChar = brightness switch
                    {
                        >= 230 => ' ',
                        >= 200 => '.',
                        >= 180 => '·',
                        >= 160 => ':',
                        >= 140 => '-',
                        >= 120 => '=',
                        >= 100 => '+',
                        >= 80 => '*',
                        >= 60 => '#',
                        >= 40 => '%',
                        >= 20 => '@',
                        _ => '█'
                    };

                    Console.Write(asciiChar);
                }
                Console.WriteLine(" │");
            }

            Console.WriteLine("└" + new string('─', targetWidth + 2) + "┘");
            Console.ResetColor();
            Console.WriteLine();
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

        // Image info
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("📊 Analysis Results");
        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.ResetColor();

        Console.WriteLine($"📁 Image: {Path.GetFileName(profile.ImagePath)}");
        Console.WriteLine($"⏱️  Duration: {profile.AnalysisDurationMs}ms");
        Console.WriteLine($"🌊 Waves: {string.Join(", ", profile.ContributingWaves)}");
        Console.WriteLine($"📡 Signals: {profile.GetAllSignals().Count()}");
        Console.WriteLine();

        // Show ledger summary
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("📋 Image Ledger (Salient Features):");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(ledger.ToLlmSummary());
        Console.ResetColor();
        Console.WriteLine();

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

    static async Task ProcessImage(string imagePath, string pipeline, string outputFormat, string language, bool verbose)
    {
        if (!File.Exists(imagePath))
        {
            Console.Error.WriteLine($"Error: File not found: {imagePath}");
            Environment.Exit(1);
        }

        // Setup DI
        var services = new ServiceCollection();

        // Logging
        if (verbose)
        {
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });
        }

        // Configure image analysis based on pipeline
        services.AddDocSummarizerImages(opt =>
        {
            opt.EnableOcr = true;
            opt.Ocr.SpellCheckLanguage = language;
            opt.Ocr.PipelineName = pipeline; // Use JSON-configured pipeline
            opt.Ocr.UseAdvancedPipeline = true; // Enable advanced pipeline system
            opt.Ocr.TextDetectionConfidenceThreshold = 0; // Always run (controlled by pipeline config)

            // Enable Vision LLM for Tier 3 Sentinel correction
            opt.EnableVisionLlm = true;
            opt.VisionLlmModel = "minicpm-v:8b";
            opt.OllamaBaseUrl = "http://localhost:11434";
        });

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<WaveOrchestrator>();

        try
        {
            // Analyze image
            var profile = await orchestrator.AnalyzeAsync(imagePath);

            // Output results
            switch (outputFormat.ToLowerInvariant())
            {
                case "json":
                    OutputJson(profile);
                    break;

                case "signals":
                    OutputSignals(profile);
                    break;

                case "metrics":
                    OutputMetrics(profile);
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

    static void OutputJson(DynamicImageProfile profile)
    {
        var ledger = profile.GetLedger();

        var result = new
        {
            image = profile.ImagePath,
            duration_ms = profile.AnalysisDurationMs,
            waves = profile.ContributingWaves,
            text = GetExtractedText(profile),
            confidence = GetTextConfidence(profile),
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
        var metrics = new
        {
            analysis_duration_ms = profile.AnalysisDurationMs,
            waves_executed = profile.ContributingWaves.Count(),
            signals_emitted = profile.GetAllSignals().Count(),
            frames_processed = profile.GetValue<int>("ocr.frames.extracted"),
            text_length = GetExtractedText(profile)?.Length ?? 0,
            confidence = GetTextConfidence(profile),
            spell_check_score = profile.GetValue<double>("ocr.quality.spell_check_score"),
            is_garbled = profile.GetValue<bool>("ocr.quality.is_garbled"),
            stabilization_quality = profile.GetValue<double>("ocr.stabilization.confidence"),
            frame_agreement = profile.GetValue<double>("ocr.voting.agreement_score")
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        Console.WriteLine(JsonSerializer.Serialize(metrics, options));
    }

    static string? GetExtractedText(DynamicImageProfile profile)
    {
        // Priority: Tier 2/3 corrections > legacy Tier 3 > voting > temporal median > raw OCR
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
            Console.WriteLine("Usage: imagecli image.gif --pipeline <name>");
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
}
