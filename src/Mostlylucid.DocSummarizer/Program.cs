using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Extensions;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;
using Spectre.Console;

// ============================================================================
// DocSummarizer CLI v4.0.0
// A local-first document summarization tool using LLMs and vector search.
// 
// This version uses Core library via DI for all document processing.
// ============================================================================

namespace Mostlylucid.DocSummarizer;

internal static class Program
{
    // ============================================================================
    // Command Options (global)
    // ============================================================================
    
    private static readonly Option<string?> ConfigOption = new("--config", "-c") { Description = "Path to configuration file (JSON)" };
    private static readonly Option<FileInfo?> FileOption = new("--file", "-f") { Description = "Path to the document (DOCX, PDF, or MD)" };
    private static readonly Option<DirectoryInfo?> DirectoryOption = new("--directory", "-d") { Description = "Path to directory for batch processing" };
    private static readonly Option<string?> UrlOption = new("--url", "-u") { Description = "Web URL to fetch and summarize (requires --web-enabled)" };
    private static readonly Option<bool> WebEnabledOption = new("--web-enabled") { Description = "Enable web URL fetching (required when using --url)", DefaultValueFactory = _ => false };
    private static readonly Option<SummarizationMode> ModeOption = new("--mode", "-m") { Description = "Summarization mode: Auto, BertRag, Bert, BertHybrid, Iterative, MapReduce, Rag", DefaultValueFactory = _ => SummarizationMode.Auto };
    private static readonly Option<string?> FocusOption = new("--focus") { Description = "Focus query for RAG mode (e.g., 'pricing terms', 'security requirements')" };
    private static readonly Option<string?> QueryOption = new("--query", "-q") { Description = "Query the document instead of summarizing" };
    private static readonly Option<string?> ModelOption = new("--model") { Description = "Ollama model to use (overrides config)" };
    private static readonly Option<bool> VerboseOption = new("--verbose", "-v") { Description = "Show detailed progress", DefaultValueFactory = _ => false };
    private static readonly Option<OutputFormat> OutputFormatOption = new("--output-format", "-o") { Description = "Output format: Console, Text, Markdown, Json", DefaultValueFactory = _ => OutputFormat.Console };
    private static readonly Option<string?> OutputDirOption = new("--output-dir") { Description = "Output directory for file outputs" };
    private static readonly Option<string[]?> ExtensionsOption = new("--extensions", "-e") { Description = "File extensions to process in batch mode" };
    private static readonly Option<bool> RecursiveOption = new("--recursive", "-r") { Description = "Process directories recursively", DefaultValueFactory = _ => false };
    private static readonly Option<string?> TemplateOption = new("--template", "-t") { Description = "Summary template (e.g., 'bookreport' or 'bookreport:500')" };
    private static readonly Option<int?> WordsOption = new("--words", "-w") { Description = "Target word count (overrides template default)" };
    private static readonly Option<bool> ShowStructureOption = new("--show-structure", "-s") { Description = "Include document structure in output", DefaultValueFactory = _ => false };
    private static readonly Option<EmbeddingBackend?> EmbeddingBackendOption = new("--embedding-backend") { Description = "Embedding backend: Onnx or Ollama" };
    private static readonly Option<string?> EmbeddingModelOption = new("--embedding-model") { Description = "ONNX embedding model name" };
    private static readonly Option<WebFetchMode?> WebModeOption = new("--web-mode") { Description = "Web fetch mode: Simple or Playwright" };
    private static readonly Option<string?> DoclingUrlOption = new("--docling-url") { Description = "Docling service URL" };
    private static readonly Option<int?> DoclingPagesPerChunkOption = new("--pages-per-chunk") { Description = "Pages per chunk for PDF split processing" };
    private static readonly Option<int?> DoclingMaxConcurrentOption = new("--max-concurrent-chunks") { Description = "Max concurrent chunks to process" };
    private static readonly Option<bool?> DoclingDisableSplitOption = new("--no-split") { Description = "Disable split processing" };
    private static readonly Option<int?> DoclingMinPagesForSplitOption = new("--min-pages-split") { Description = "Min pages before split processing" };
    private static readonly Option<string?> DoclingPdfBackendOption = new("--pdf-backend") { Description = "PDF backend: pypdfium2 or docling" };
    private static readonly Option<bool?> DoclingGpuOption = new("--docling-gpu") { Description = "Force GPU mode for Docling" };
    private static readonly Option<string?> OnnxGpuOption = new("--onnx-gpu") { Description = "ONNX execution provider: cpu, cuda, directml, auto" };
    private static readonly Option<int?> GpuDeviceIdOption = new("--gpu-device") { Description = "GPU device ID for ONNX" };
    
    // OpenTelemetry options
    private static readonly Option<bool> TelemetryOption = new("--telemetry") { Description = "Enable OpenTelemetry tracing and metrics", DefaultValueFactory = _ => false };
    private static readonly Option<bool> TelemetryConsoleOption = new("--telemetry-console") { Description = "Export telemetry to console", DefaultValueFactory = _ => false };
    private static readonly Option<string?> TelemetryOtlpOption = new("--telemetry-otlp") { Description = "OTLP endpoint (e.g., http://localhost:4317 for Jaeger/Grafana)" };

    public static int Main(string[] args)
    {
        var ui = new UIService();

        // Build root command with all options
        var rootCommand = BuildRootCommand();

        // Main handler
        rootCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var configPath = parseResult.GetValue(ConfigOption);
            var file = parseResult.GetValue(FileOption);
            var directory = parseResult.GetValue(DirectoryOption);
            var url = parseResult.GetValue(UrlOption);
            var webEnabled = parseResult.GetValue(WebEnabledOption);
            var mode = parseResult.GetValue(ModeOption);
            var focus = parseResult.GetValue(FocusOption);
            var query = parseResult.GetValue(QueryOption);
            var model = parseResult.GetValue(ModelOption);
            var verbose = parseResult.GetValue(VerboseOption);
            var outputFormat = parseResult.GetValue(OutputFormatOption);
            var outputDir = parseResult.GetValue(OutputDirOption);
            var extensions = parseResult.GetValue(ExtensionsOption);
            var recursive = parseResult.GetValue(RecursiveOption);
            var templateName = parseResult.GetValue(TemplateOption);
            var targetWords = parseResult.GetValue(WordsOption);
            var showStructure = parseResult.GetValue(ShowStructureOption);
            
            try
            {
                // Load and configure
                var config = LoadAndApplyConfig(parseResult, configPath);
                config.Output.Verbose = verbose;
                config.Output.Format = outputFormat;
                config.Output.IncludeChunkIndex = showStructure;
                if (outputDir != null) config.Output.OutputDirectory = outputDir;
                if (extensions != null && extensions.Length > 0) config.Batch.FileExtensions = extensions.ToList();
                config.Batch.Recursive = recursive;
                
                // Get explicit GPU setting
                var doclingGpu = parseResult.GetValue(DoclingGpuOption);
                
                // Parse telemetry options
                var telemetryEnabled = parseResult.GetValue(TelemetryOption);
                var telemetryConsole = parseResult.GetValue(TelemetryConsoleOption);
                var telemetryOtlp = parseResult.GetValue(TelemetryOtlpOption);
                var telemetryOptions = TelemetryExtensions.ParseTelemetryOptions(telemetryEnabled, telemetryConsole, telemetryOtlp);
                
                if (telemetryOptions.Enabled && verbose)
                {
                    var exporters = new List<string>();
                    if (telemetryOptions.ConsoleExporter) exporters.Add("Console");
                    if (!string.IsNullOrEmpty(telemetryOptions.OtlpEndpoint)) exporters.Add($"OTLP ({telemetryOptions.OtlpEndpoint})");
                    AnsiConsole.MarkupLine($"[dim]Telemetry enabled: {string.Join(", ", exporters)}[/]");
                }
                
                // Detect services and auto-adapt config
                var detectedServices = await ServiceDetector.DetectAndDisplayAsync(config, verbose, doclingGpu);
                AutoOptimizeConfig(config, detectedServices, parseResult, verbose);
                
                // Build DI container with configured services
                using var services = BuildServiceProvider(config, telemetryOptions);
                var summarizer = services.GetRequiredService<IDocumentSummarizer>();
                
                // Apply template
                var template = ParseTemplate(templateName ?? "default", targetWords);
                summarizer.Template = template;
                
                if (verbose && !string.IsNullOrEmpty(templateName))
                {
                    var wordInfo = template.TargetWords > 0 ? $" (~{template.TargetWords} words)" : "";
                    AnsiConsole.MarkupLine($"[dim]Template: {Markup.Escape(template.Name)} ({Markup.Escape(template.Description)}){Markup.Escape(wordInfo)}[/]");
                }
                
                if (mode != SummarizationMode.Auto)
                {
                    AnsiConsole.MarkupLine($"[cyan]Mode override:[/] {mode} [dim](user specified)[/]");
                }

                // Determine operation mode
                if (!string.IsNullOrEmpty(url))
                {
                    if (!webEnabled)
                    {
                        AnsiConsole.MarkupLine("[red]Error: --web-enabled must be specified when using --url[/]");
                        return 1;
                    }
                    config.WebFetch.Enabled = true;
                    await ProcessUrlAsync(summarizer, url, mode, focus, query, config, ui);
                }
                else if (directory != null)
                {
                    await ProcessBatchAsync(services, directory.FullName, mode, focus, config, ui);
                }
                else if (file != null)
                {
                    await ProcessFileAsync(summarizer, file.FullName, mode, focus, query, config, ui);
                }
                else
                {
                    var defaultFile = Path.Combine(Environment.CurrentDirectory, "README.md");
                    if (File.Exists(defaultFile))
                    {
                        ui.Info("No file specified, using README.md in current directory");
                        Console.WriteLine();
                        await ProcessFileAsync(summarizer, defaultFile, mode, focus, query, config, ui);
                    }
                    else
                    {
                        ui.Error("Either --file, --directory, or --url must be specified (or run from a directory containing README.md)");
                        return 1;
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                if (verbose) AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths);
                return 1;
            }
        });

        // Add subcommands
        AddCheckCommand(rootCommand);
        AddConfigCommand(rootCommand);
        AddBenchmarkCommand(rootCommand);
        AddBenchmarkTemplatesCommand(rootCommand);
        AddTemplatesCommand(rootCommand);
        AddToolCommand(rootCommand);
        AddSearchCommand(rootCommand);
        AddCacheCommand(rootCommand);

        return rootCommand.Parse(args).Invoke();
    }

    // ============================================================================
    // Build Root Command
    // ============================================================================

    private static RootCommand BuildRootCommand()
    {
        var rootCommand = new RootCommand("DocSummarizer v4.0 - Document summarization using local LLMs");
        
        rootCommand.Options.Add(ConfigOption);
        rootCommand.Options.Add(FileOption);
        rootCommand.Options.Add(DirectoryOption);
        rootCommand.Options.Add(UrlOption);
        rootCommand.Options.Add(WebEnabledOption);
        rootCommand.Options.Add(ModeOption);
        rootCommand.Options.Add(FocusOption);
        rootCommand.Options.Add(QueryOption);
        rootCommand.Options.Add(ModelOption);
        rootCommand.Options.Add(VerboseOption);
        rootCommand.Options.Add(OutputFormatOption);
        rootCommand.Options.Add(OutputDirOption);
        rootCommand.Options.Add(ExtensionsOption);
        rootCommand.Options.Add(RecursiveOption);
        rootCommand.Options.Add(TemplateOption);
        rootCommand.Options.Add(WordsOption);
        rootCommand.Options.Add(ShowStructureOption);
        rootCommand.Options.Add(EmbeddingBackendOption);
        rootCommand.Options.Add(EmbeddingModelOption);
        rootCommand.Options.Add(WebModeOption);
        rootCommand.Options.Add(DoclingUrlOption);
        rootCommand.Options.Add(DoclingPagesPerChunkOption);
        rootCommand.Options.Add(DoclingMaxConcurrentOption);
        rootCommand.Options.Add(DoclingDisableSplitOption);
        rootCommand.Options.Add(DoclingMinPagesForSplitOption);
        rootCommand.Options.Add(DoclingPdfBackendOption);
        rootCommand.Options.Add(DoclingGpuOption);
        rootCommand.Options.Add(OnnxGpuOption);
        rootCommand.Options.Add(GpuDeviceIdOption);
        
        // OpenTelemetry options
        rootCommand.Options.Add(TelemetryOption);
        rootCommand.Options.Add(TelemetryConsoleOption);
        rootCommand.Options.Add(TelemetryOtlpOption);
        
        return rootCommand;
    }

    // ============================================================================
    // Service Provider Factory
    // ============================================================================

    private static ServiceProvider BuildServiceProvider(DocSummarizerConfig config, TelemetryOptions? telemetryOptions = null)
    {
        var services = new ServiceCollection();
        
        // Register Core services via AddDocSummarizer
        services.AddDocSummarizer(opt =>
        {
            opt.Ollama = config.Ollama;
            opt.Docling = config.Docling;
            opt.Qdrant = config.Qdrant;
            opt.Onnx = config.Onnx;
            opt.BertRag = config.BertRag;
            opt.Output = config.Output;
            opt.Batch = config.Batch;
            opt.WebFetch = config.WebFetch;
            opt.Processing = config.Processing;
            opt.Extraction = config.Extraction;
            opt.Retrieval = config.Retrieval;
            opt.AdaptiveRetrieval = config.AdaptiveRetrieval;
            opt.Embedding = config.Embedding;
            opt.EmbeddingBackend = config.EmbeddingBackend;
        });
        
        // Add OpenTelemetry if configured
        if (telemetryOptions is { Enabled: true })
        {
            services.AddDocSummarizerTelemetry(telemetryOptions);
        }
        
        // Register CLI services
        services.AddSingleton<IUIService, UIService>();
        services.AddSingleton(config);
        
        return services.BuildServiceProvider();
    }

    // ============================================================================
    // Config Loading and Application
    // ============================================================================

    private static DocSummarizerConfig LoadAndApplyConfig(ParseResult parseResult, string? configPath)
    {
        var config = ConfigurationLoader.Load(configPath);
        
        // Apply command-line overrides
        var model = parseResult.GetValue(ModelOption);
        if (model != null) config.Ollama.Model = model;
        
        var embeddingBackend = parseResult.GetValue(EmbeddingBackendOption);
        if (embeddingBackend.HasValue) config.EmbeddingBackend = embeddingBackend.Value;
        
        var embeddingModel = parseResult.GetValue(EmbeddingModelOption);
        if (!string.IsNullOrEmpty(embeddingModel))
            config.Onnx.EmbeddingModel = Enum.Parse<OnnxEmbeddingModel>(embeddingModel, ignoreCase: true);
        
        var onnxGpu = parseResult.GetValue(OnnxGpuOption);
        if (!string.IsNullOrEmpty(onnxGpu))
            config.Onnx.ExecutionProvider = Enum.Parse<OnnxExecutionProvider>(onnxGpu, ignoreCase: true);
        
        var gpuDeviceId = parseResult.GetValue(GpuDeviceIdOption);
        if (gpuDeviceId.HasValue) config.Onnx.GpuDeviceId = gpuDeviceId.Value;
        
        var webMode = parseResult.GetValue(WebModeOption);
        if (webMode.HasValue) config.WebFetch.Mode = webMode.Value;
        
        var doclingUrl = parseResult.GetValue(DoclingUrlOption);
        if (!string.IsNullOrEmpty(doclingUrl)) config.Docling.BaseUrl = doclingUrl;
        
        var pagesPerChunk = parseResult.GetValue(DoclingPagesPerChunkOption);
        if (pagesPerChunk.HasValue) config.Docling.PagesPerChunk = pagesPerChunk.Value;
        
        var maxConcurrent = parseResult.GetValue(DoclingMaxConcurrentOption);
        if (maxConcurrent.HasValue) config.Docling.MaxConcurrentChunks = maxConcurrent.Value;
        
        var noSplit = parseResult.GetValue(DoclingDisableSplitOption);
        if (noSplit == true) config.Docling.EnableSplitProcessing = false;
        
        var minPagesForSplit = parseResult.GetValue(DoclingMinPagesForSplitOption);
        if (minPagesForSplit.HasValue) config.Docling.MinPagesForSplit = minPagesForSplit.Value;
        
        var pdfBackend = parseResult.GetValue(DoclingPdfBackendOption);
        if (!string.IsNullOrEmpty(pdfBackend)) config.Docling.PdfBackend = pdfBackend;
        
        return config;
    }

    private static void AutoOptimizeConfig(DocSummarizerConfig config, DetectedServices detectedServices, ParseResult parseResult, bool verbose)
    {
        var pagesPerChunk = parseResult.GetValue(DoclingPagesPerChunkOption);
        var maxConcurrent = parseResult.GetValue(DoclingMaxConcurrentOption);
        
        if (config.Docling.AutoDetectGpu && !pagesPerChunk.HasValue && !maxConcurrent.HasValue)
        {
            var optimizedDocling = detectedServices.GetOptimizedDoclingConfig(config.Docling);
            config.Docling.PagesPerChunk = optimizedDocling.PagesPerChunk;
            config.Docling.MaxConcurrentChunks = optimizedDocling.MaxConcurrentChunks;
            config.Docling.MinPagesForSplit = optimizedDocling.MinPagesForSplit;
            
            if (verbose && detectedServices.DoclingAvailable)
            {
                var gpuStatus = detectedServices.DoclingHasGpu ? "GPU" : "CPU";
                AnsiConsole.MarkupLine($"[dim]Docling ({gpuStatus}): pages/chunk={config.Docling.PagesPerChunk}, concurrent={config.Docling.MaxConcurrentChunks}[/]");
            }
        }
    }

    // ============================================================================
    // Template Parsing
    // ============================================================================

    private static SummaryTemplate ParseTemplate(string templateSpec, int? wordCountOverride)
    {
        var parts = templateSpec.Split(':', 2);
        var templateName = parts[0].Trim();
        var template = SummaryTemplate.Presets.GetByName(templateName);
        
        if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out var specWords))
            template.TargetWords = specWords;
        
        if (wordCountOverride.HasValue)
            template.TargetWords = wordCountOverride.Value;
        
        return template;
    }

    // ============================================================================
    // Processing Methods
    // ============================================================================

    private static async Task ProcessUrlAsync(
        IDocumentSummarizer summarizer,
        string url,
        SummarizationMode mode,
        string? focus,
        string? query,
        DocSummarizerConfig config,
        IUIService ui)
    {
        ui.Info($"Fetching URL: {url}");
        Console.WriteLine();
        
        var usePlaywright = config.WebFetch.Mode == WebFetchMode.Playwright;
        
        if (!string.IsNullOrEmpty(query))
        {
            ui.WriteHeader("DocSummarizer", "Query Mode");
            // Fetch and query - would need QueryAsync to accept URL
            // For now, fall through to summarize mode
        }
        
        ui.WriteHeader("DocSummarizer", "URL Mode");
        ui.WriteDocumentInfo(url, mode.ToString(), config.Ollama.Model, focus);
        
        var sw = Stopwatch.StartNew();
        var summary = await summarizer.SummarizeUrlAsync(url, focus, mode, usePlaywright);
        sw.Stop();
        
        ui.WriteCompletion(sw.Elapsed);
        DisplaySummary(summary, url, config, ui);
    }

    private static async Task ProcessFileAsync(
        IDocumentSummarizer summarizer,
        string filePath,
        SummarizationMode mode,
        string? focus,
        string? query,
        DocSummarizerConfig config,
        IUIService ui,
        string? sourceUrl = null)
    {
        var fileName = sourceUrl ?? Path.GetFileName(filePath);
        var sw = Stopwatch.StartNew();
        
        if (!string.IsNullOrEmpty(query))
        {
            ui.WriteHeader("DocSummarizer", "Query Mode");
            ui.WriteDocumentInfo(fileName, "Query", config.Ollama.Model);
            
            var markdown = await File.ReadAllTextAsync(filePath);
            var answer = await ui.WithSpinnerAsync("Querying document...", 
                () => summarizer.QueryAsync(markdown, query));
            
            ui.WriteSummary(answer.Answer, "Answer");
            ui.WriteCompletion(sw.Elapsed);
        }
        else
        {
            ui.WriteHeader("DocSummarizer");
            ui.WriteDocumentInfo(fileName, mode.ToString(), config.Ollama.Model, focus);
            
            var summary = await summarizer.SummarizeFileAsync(filePath, focus, mode);
            
            sw.Stop();
            ui.WriteCompletion(sw.Elapsed);
            DisplaySummary(summary, fileName, config, ui, filePath, sourceUrl);
        }
    }

    private static async Task ProcessBatchAsync(
        IServiceProvider services,
        string directoryPath,
        SummarizationMode mode,
        string? focus,
        DocSummarizerConfig config,
        IUIService ui)
    {
        ui.WriteHeader("DocSummarizer", "Batch Mode");
        ui.WriteDocumentInfo(directoryPath, mode.ToString(), config.Ollama.Model, focus);
        
        var effectiveOutputDir = config.Output.OutputDirectory ?? directoryPath;
        var outputInSourceDir = string.Equals(
            Path.GetFullPath(effectiveOutputDir), 
            Path.GetFullPath(directoryPath), 
            StringComparison.OrdinalIgnoreCase);
        
        if (outputInSourceDir && config.Output.Format != OutputFormat.Console)
        {
            AnsiConsole.MarkupLine("[yellow]Note:[/] Output files will be saved alongside source files.");
            AnsiConsole.MarkupLine("[dim]  Files ending in _summary will be automatically skipped.[/]");
            AnsiConsole.WriteLine();
        }

        // For batch, we still use the old DocumentSummarizer for ConvertToChunks etc.
        var summarizer = services.GetRequiredService<IDocumentSummarizer>();
        var legacySummarizer = new DocumentSummarizer(
            config.Ollama.Model,
            config.Docling.BaseUrl,
            config.Qdrant.Host,
            config.Output.Verbose,
            config.Docling,
            config.Processing,
            config.Qdrant,
            ollamaConfig: config.Ollama,
            onnxConfig: config.Onnx,
            embeddingBackend: config.EmbeddingBackend,
            bertRagConfig: config.BertRag);
        
        var batchProcessor = new BatchProcessor(legacySummarizer, config.Batch, config.Output.Verbose);
        var processed = 0;
        var totalFiles = 0;
        
        using (ui.EnterBatchContext())
        {
            async Task OnFileCompleted(BatchResult result)
            {
                processed++;
                var fileName = Path.GetFileName(result.FilePath);
                ui.WriteBatchProgress(processed, totalFiles, fileName, result.Success);
                
                if (result.Success && result.Summary != null && config.Output.Format != OutputFormat.Console)
                {
                    var output = OutputFormatter.Format(result.Summary, config.Output, fileName);
                    var outputDir = config.Output.OutputDirectory ?? Path.GetDirectoryName(result.FilePath);
                    await OutputFormatter.WriteOutputAsync(output, config.Output, fileName, outputDir);
                }
            }
            
            Console.WriteLine();
            
            var sw = Stopwatch.StartNew();
            var batchSummary = await batchProcessor.ProcessDirectoryAsync(directoryPath, mode, focus, OnFileCompleted);
            sw.Stop();

            Console.WriteLine();
            ui.WriteCompletion(sw.Elapsed, batchSummary.FailureCount == 0);
            ui.Success($"Processed: {batchSummary.SuccessCount} succeeded, {batchSummary.FailureCount} failed");
            
            if (config.Output.Format != OutputFormat.Console)
            {
                var batchOutput = OutputFormatter.FormatBatch(batchSummary, config.Output);
                await OutputFormatter.WriteOutputAsync(batchOutput, config.Output, "_batch_summary", config.Output.OutputDirectory);
            }
        }
    }

    private static void DisplaySummary(
        DocumentSummary summary,
        string fileName,
        DocSummarizerConfig config,
        IUIService ui,
        string? filePath = null,
        string? sourceUrl = null)
    {
        var output = OutputFormatter.Format(summary, config.Output, fileName);
        
        if (config.Output.Format == OutputFormat.Console)
        {
            Console.WriteLine();
            ui.WriteSummary(summary.ExecutiveSummary, "Summary");
            
            if (summary.Entities != null && summary.Entities.HasAny)
            {
                Console.WriteLine();
                ui.WriteEntities(summary.Entities);
            }
            
            if (summary.TopicSummaries?.Count > 0)
            {
                Console.WriteLine();
                ui.WriteTopics(summary.TopicSummaries.Select(t => (t.Topic, t.Summary)));
            }
            
            // Auto-save to .summary.md file
            if (filePath != null || sourceUrl != null)
            {
                var fileDir = sourceUrl != null ? Environment.CurrentDirectory : (Path.GetDirectoryName(filePath) ?? Environment.CurrentDirectory);
                var baseName = sourceUrl != null ? SanitizeFileName(new Uri(sourceUrl).Host) : Path.GetFileNameWithoutExtension(filePath!);
                if (baseName.EndsWith("_summary", StringComparison.OrdinalIgnoreCase))
                    baseName = baseName[..^8];
                var summaryPath = Path.Combine(fileDir, $"{baseName}_summary.md");
                
                var markdownConfig = new OutputConfig { Format = OutputFormat.Markdown, IncludeTrace = true };
                var markdownOutput = OutputFormatter.Format(summary, markdownConfig, fileName);
                File.WriteAllText(summaryPath, markdownOutput);
                
                Console.WriteLine();
                ui.Success($"Saved to: {summaryPath}");
            }
        }
        else
        {
            OutputFormatter.WriteOutputAsync(output, config.Output, fileName, config.Output.OutputDirectory).Wait();
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
    }

    // ============================================================================
    // Subcommands
    // ============================================================================

    private static void AddCheckCommand(RootCommand rootCommand)
    {
        var checkCommand = new Command("check", "Verify dependencies are available");
        var verboseOpt = new Option<bool>("--verbose", "-v") { Description = "Show detailed model information", DefaultValueFactory = _ => false };
        var configOpt = new Option<string?>("--config", "-c") { Description = "Configuration file path" };
        checkCommand.Options.Add(verboseOpt);
        checkCommand.Options.Add(configOpt);
        
        checkCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var verbose = parseResult.GetValue(verboseOpt);
            var configPath = parseResult.GetValue(configOpt);
            
            SpectreProgressService.WriteHeader("DocSummarizer", "Dependency Check");
            
            var config = ConfigurationLoader.Load(configPath);
            var detected = await ServiceDetector.DetectAsync(config, verbose);
            
            ModelInfo? modelInfo = null;
            if (detected.OllamaAvailable && verbose)
            {
                var ollama = new OllamaService(
                    model: config.Ollama.Model,
                    embedModel: config.Ollama.EmbedModel,
                    baseUrl: config.Ollama.BaseUrl,
                    timeout: TimeSpan.FromSeconds(config.Ollama.TimeoutSeconds),
                    classifierModel: config.Ollama.ClassifierModel);
                modelInfo = await ollama.GetModelInfoAsync();
            }

            // Display status table
            var statusTable = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Blue)
                .Title("[cyan]Dependency Status[/]");
            
            statusTable.AddColumn(new TableColumn("[blue]Service[/]").LeftAligned());
            statusTable.AddColumn(new TableColumn("[blue]Status[/]").Centered());
            statusTable.AddColumn(new TableColumn("[blue]Details[/]").LeftAligned());
            
            statusTable.AddRow(
                "[cyan]Ollama[/]",
                detected.OllamaAvailable ? "[green]OK[/]" : "[red]FAIL[/]",
                detected.OllamaAvailable ? $"{detected.AvailableModels.Count} models" : "Run: ollama serve");
            statusTable.AddRow(
                "[cyan]Docling[/]",
                detected.DoclingAvailable ? "[green]OK[/]" : "[yellow]Optional[/]",
                detected.DoclingAvailable 
                    ? (detected.DoclingHasGpu ? "[cyan]GPU accelerated[/]" : "CPU mode") 
                    : "PDF/DOCX disabled");
            statusTable.AddRow(
                "[cyan]Qdrant[/]",
                detected.QdrantAvailable ? "[green]OK[/]" : "[yellow]Optional[/]",
                detected.QdrantAvailable ? "Vector persistence enabled" : "Using in-memory vectors");
            statusTable.AddRow(
                "[cyan]ONNX[/]",
                "[green]OK[/]",
                "Embedded (always available)");
            
            AnsiConsole.Write(statusTable);
            AnsiConsole.WriteLine();
            
            // Display features table
            var featuresTable = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Cyan1)
                .Title("[cyan]Available Features[/]");
            
            featuresTable.AddColumn(new TableColumn("[cyan]Feature[/]").LeftAligned());
            featuresTable.AddColumn(new TableColumn("[cyan]Status[/]").Centered());
            featuresTable.AddColumn(new TableColumn("[cyan]Requires[/]").LeftAligned());
            
            var f = detected.Features;
            featuresTable.AddRow("PDF/DOCX conversion", f.PdfConversion ? "[green]✓[/]" : "[red]✗[/]", "Docling");
            featuresTable.AddRow("Fast GPU conversion", f.FastPdfConversion ? "[green]✓[/]" : "[yellow]○[/]", "Docling + CUDA");
            featuresTable.AddRow("BERT summarization", f.BertSummarization ? "[green]✓[/]" : "[red]✗[/]", "ONNX (embedded)");
            featuresTable.AddRow("LLM summarization", f.LlmSummarization ? "[green]✓[/]" : "[yellow]○[/]", "Ollama");
            featuresTable.AddRow("Document Q&A", f.DocumentQA ? "[green]✓[/]" : "[yellow]○[/]", "Ollama + ONNX");
            featuresTable.AddRow("Vector persistence", f.VectorPersistence ? "[green]✓[/]" : "[yellow]○[/]", "Qdrant");
            featuresTable.AddRow("Cross-session cache", f.CrossSessionCache ? "[green]✓[/]" : "[yellow]○[/]", "Qdrant");
            
            AnsiConsole.Write(featuresTable);
            AnsiConsole.WriteLine();
            
            AnsiConsole.MarkupLine($"[cyan]Recommended mode:[/] {f.BestModeDescription}");
            AnsiConsole.WriteLine();

            if (verbose && detected.OllamaAvailable && modelInfo != null)
            {
                var modelTable = new Table()
                    .Border(TableBorder.Rounded)
                    .BorderColor(Color.Green)
                    .Title("[green]Default Model Info[/]");
                
                modelTable.AddColumn(new TableColumn("[green]Property[/]"));
                modelTable.AddColumn(new TableColumn("[green]Value[/]"));
                
                modelTable.AddRow("Name", Markup.Escape(modelInfo.Name ?? "N/A"));
                modelTable.AddRow("Family", Markup.Escape(modelInfo.Family ?? "N/A"));
                modelTable.AddRow("Parameters", Markup.Escape(modelInfo.ParameterCount ?? "N/A"));
                modelTable.AddRow("Quantization", Markup.Escape(modelInfo.QuantizationLevel ?? "N/A"));
                modelTable.AddRow("Context Window", $"{modelInfo.ContextWindow:N0} tokens");
                
                AnsiConsole.Write(modelTable);
                AnsiConsole.WriteLine();
            }

            if (detected.OllamaAvailable || detected.Features.BertSummarization)
            {
                AnsiConsole.MarkupLine("[green]Ready to summarize![/]");
                return 0;
            }
            else
            {
                AnsiConsole.MarkupLine("[red]No summarization backend available.[/]");
                return 1;
            }
        });
        
        rootCommand.Subcommands.Add(checkCommand);
    }

    private static void AddConfigCommand(RootCommand rootCommand)
    {
        var configCommand = new Command("config", "Generate default configuration file");
        var outputOpt = new Option<string>("--output", "-o") { Description = "Output file path", DefaultValueFactory = _ => "docsummarizer.json" };
        configCommand.Options.Add(outputOpt);
        
        configCommand.SetAction((parseResult, cancellationToken) =>
        {
            var outputPath = parseResult.GetValue(outputOpt) ?? "docsummarizer.json";
            ConfigurationLoader.CreateDefault(outputPath);
            Console.WriteLine($"Created default configuration: {outputPath}");
            return Task.FromResult(0);
        });
        
        rootCommand.Subcommands.Add(configCommand);
    }

    private static void AddBenchmarkCommand(RootCommand rootCommand)
    {
        var benchmarkCommand = new Command("benchmark", "Compare multiple models on the same document");
        var fileOpt = new Option<FileInfo?>("--file", "-f") { Description = "Document to summarize (required)" };
        var modelsOpt = new Option<string?>("--models", "-m") { Description = "Comma-separated list of models" };
        var modeOpt = new Option<SummarizationMode>("--mode") { Description = "Summarization mode", DefaultValueFactory = _ => SummarizationMode.BertRag };
        var configOpt = new Option<string?>("--config", "-c") { Description = "Configuration file path" };
        
        benchmarkCommand.Options.Add(fileOpt);
        benchmarkCommand.Options.Add(modelsOpt);
        benchmarkCommand.Options.Add(modeOpt);
        benchmarkCommand.Options.Add(configOpt);
        
        benchmarkCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var file = parseResult.GetValue(fileOpt);
            var modelsString = parseResult.GetValue(modelsOpt);
            var mode = parseResult.GetValue(modeOpt);
            var configPath = parseResult.GetValue(configOpt);
            
            if (file == null)
            {
                AnsiConsole.MarkupLine("[red]Error: --file is required[/]");
                return 1;
            }
            
            if (string.IsNullOrEmpty(modelsString))
            {
                AnsiConsole.MarkupLine("[red]Error: --models is required[/]");
                return 1;
            }
            
            var models = modelsString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (models.Length == 0)
            {
                AnsiConsole.MarkupLine("[red]Error: No models specified[/]");
                return 1;
            }
            
            if (!file.Exists)
            {
                AnsiConsole.MarkupLine($"[red]Error: File not found: {Markup.Escape(file.FullName)}[/]");
                return 1;
            }
            
            SpectreProgressService.WriteHeader("DocSummarizer", "Model Benchmark");
            
            var config = ConfigurationLoader.Load(configPath);
            config.Output.Verbose = false;
            
            var results = new List<(string Model, TimeSpan Duration, int Words, string Summary)>();
            
            // Convert document once
            AnsiConsole.MarkupLine("[cyan]Converting document...[/]");
            
            var baseSummarizer = new DocumentSummarizer(
                config.Ollama.Model,
                config.Docling.BaseUrl,
                config.Qdrant.Host,
                verbose: false,
                config.Docling,
                config.Processing,
                config.Qdrant,
                ollamaConfig: config.Ollama,
                onnxConfig: config.Onnx,
                embeddingBackend: config.EmbeddingBackend,
                bertRagConfig: config.BertRag);
            
            var docId = Path.GetFileNameWithoutExtension(file.Name);
            var chunks = await SpectreProgressService.WithSpinnerAsync(
                "Parsing document...",
                () => baseSummarizer.ConvertToChunksAsync(file.FullName));
            
            AnsiConsole.MarkupLine($"[green]Document parsed: {chunks.Count} chunks[/]");
            AnsiConsole.WriteLine();
            
            // Benchmark each model
            foreach (var model in models)
            {
                AnsiConsole.MarkupLine($"[cyan]Testing model:[/] [yellow]{Markup.Escape(model)}[/]");
                
                try
                {
                    config.Ollama.Model = model;
                    
                    var summarizer = new DocumentSummarizer(
                        model,
                        config.Docling.BaseUrl,
                        config.Qdrant.Host,
                        verbose: false,
                        config.Docling,
                        config.Processing,
                        config.Qdrant,
                        ollamaConfig: config.Ollama,
                        onnxConfig: config.Onnx,
                        embeddingBackend: config.EmbeddingBackend,
                        bertRagConfig: config.BertRag);
                    
                    var sw = Stopwatch.StartNew();
                    var summary = await SpectreProgressService.WithSpinnerAsync(
                        $"Summarizing with {model}...",
                        () => summarizer.SummarizeFromChunksAsync(docId, chunks, mode));
                    sw.Stop();
                    
                    var wordCount = summary.ExecutiveSummary.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                    results.Add((model, sw.Elapsed, wordCount, summary.ExecutiveSummary));
                    
                    AnsiConsole.MarkupLine($"  [green]Completed in {sw.Elapsed.TotalSeconds:F1}s ({wordCount} words)[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"  [red]Failed: {Markup.Escape(ex.Message)}[/]");
                    results.Add((model, TimeSpan.Zero, 0, $"Error: {ex.Message}"));
                }
                
                AnsiConsole.WriteLine();
            }
            
            // Display results
            var resultsTable = new Table()
                .Border(TableBorder.Double)
                .BorderColor(Color.Green)
                .Title("[green]Benchmark Results[/]");
            
            resultsTable.AddColumn(new TableColumn("[green]Model[/]").LeftAligned());
            resultsTable.AddColumn(new TableColumn("[green]Time[/]").RightAligned());
            resultsTable.AddColumn(new TableColumn("[green]Words[/]").RightAligned());
            resultsTable.AddColumn(new TableColumn("[green]Speed[/]").RightAligned());
            
            foreach (var (model, duration, words, _) in results.OrderBy(r => r.Duration))
            {
                var speed = duration.TotalSeconds > 0 ? words / duration.TotalSeconds : 0;
                var timeColor = duration.TotalSeconds < 10 ? "green" : duration.TotalSeconds < 30 ? "yellow" : "red";
                
                resultsTable.AddRow(
                    Markup.Escape(model),
                    $"[{timeColor}]{duration.TotalSeconds:F1}s[/]",
                    $"{words}",
                    $"{speed:F1} w/s");
            }
            
            AnsiConsole.Write(resultsTable);
            
            var fastest = results.Where(r => r.Duration > TimeSpan.Zero).OrderBy(r => r.Duration).FirstOrDefault();
            if (fastest.Model != null)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[green]Fastest:[/] [yellow]{Markup.Escape(fastest.Model)}[/] ({fastest.Duration.TotalSeconds:F1}s)");
            }
            
            return 0;
        });
        
        rootCommand.Subcommands.Add(benchmarkCommand);
    }

    private static void AddBenchmarkTemplatesCommand(RootCommand rootCommand)
    {
        var command = new Command("benchmark-templates", "Compare templates on the same document");
        var fileOpt = new Option<FileInfo?>("--file", "-f") { Description = "Document to summarize" };
        var templatesOpt = new Option<string?>("--templates", "-t") { Description = "Templates to compare (comma-separated or 'all')" };
        var focusOpt = new Option<string?>("--focus", "-q") { Description = "Focus query" };
        var outputDirOpt = new Option<string?>("--output-dir", "-o") { Description = "Output directory" };
        var configOpt = new Option<string?>("--config", "-c") { Description = "Configuration file" };
        var verboseOpt = new Option<bool>("--verbose", "-v") { Description = "Verbose output", DefaultValueFactory = _ => false };
        
        command.Options.Add(fileOpt);
        command.Options.Add(templatesOpt);
        command.Options.Add(focusOpt);
        command.Options.Add(outputDirOpt);
        command.Options.Add(configOpt);
        command.Options.Add(verboseOpt);
        
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var file = parseResult.GetValue(fileOpt);
            var templatesString = parseResult.GetValue(templatesOpt);
            var focus = parseResult.GetValue(focusOpt);
            var outputDir = parseResult.GetValue(outputDirOpt);
            var configPath = parseResult.GetValue(configOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            
            if (file == null || !file.Exists)
            {
                AnsiConsole.MarkupLine("[red]Error: --file is required and must exist[/]");
                return 1;
            }
            
            var templateNames = string.IsNullOrWhiteSpace(templatesString) || templatesString.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? SummaryTemplate.Presets.AvailableTemplates.ToList()
                : templatesString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            
            SpectreProgressService.WriteHeader("DocSummarizer", "Template Benchmark");
            
            var config = ConfigurationLoader.Load(configPath);
            config.Output.Verbose = verbose;
            
            // Read document
            var extension = file.Extension.ToLowerInvariant();
            string markdown;
            
            if (extension is ".md" or ".txt")
            {
                markdown = await File.ReadAllTextAsync(file.FullName, cancellationToken);
            }
            else if (extension is ".zip")
            {
                markdown = await ArchiveHandler.ExtractTextAsync(file.FullName, ct: cancellationToken);
            }
            else
            {
                var docling = new DoclingClient(config.Docling);
                markdown = await docling.ConvertAsync(file.FullName);
            }
            
            var docId = Path.GetFileNameWithoutExtension(file.Name);
            var extractionConfig = config.Extraction.ToExtractionConfig();
            var retrievalConfig = config.Retrieval.ToRetrievalConfig();
            config.AdaptiveRetrieval.ApplyTo(retrievalConfig);
            
            await using var benchmarkService = new TemplateBenchmarkService(
                config.Onnx,
                new OllamaService(
                    model: config.Ollama.Model,
                    embedModel: config.Ollama.EmbedModel,
                    baseUrl: config.Ollama.BaseUrl,
                    timeout: TimeSpan.FromSeconds(config.Ollama.TimeoutSeconds),
                    classifierModel: config.Ollama.ClassifierModel),
                config.BertRag,
                extractionConfig: extractionConfig,
                retrievalConfig: retrievalConfig,
                verbose: verbose);
            
            var sw = Stopwatch.StartNew();
            var results = await benchmarkService.BenchmarkTemplatesAsync(docId, markdown, templateNames, focus, ct: cancellationToken);
            sw.Stop();
            
            // Display results
            var resultsTable = new Table()
                .Border(TableBorder.Double)
                .BorderColor(Color.Green)
                .Title("[green]Template Benchmark Results[/]");
            
            resultsTable.AddColumn(new TableColumn("[green]Template[/]").LeftAligned());
            resultsTable.AddColumn(new TableColumn("[green]Target[/]").RightAligned());
            resultsTable.AddColumn(new TableColumn("[green]Actual[/]").RightAligned());
            resultsTable.AddColumn(new TableColumn("[green]Time[/]").RightAligned());
            
            foreach (var r in results.TemplateResults)
            {
                resultsTable.AddRow(
                    $"[yellow]{Markup.Escape(r.TemplateName)}[/]",
                    r.TargetWords > 0 ? $"{r.TargetWords}" : "[dim]auto[/]",
                    r.Success ? $"{r.ActualWordCount}" : "[red]FAILED[/]",
                    r.Success ? $"{r.SynthesisTime.TotalSeconds:F2}s" : "-");
            }
            
            AnsiConsole.Write(resultsTable);
            AnsiConsole.MarkupLine($"\n[cyan]Total:[/] {sw.Elapsed.TotalSeconds:F1}s");
            
            var effectiveOutputDir = outputDir ?? Path.GetDirectoryName(file.FullName) ?? Environment.CurrentDirectory;
            await benchmarkService.SaveResultsAsync(results, effectiveOutputDir, docId);
            AnsiConsole.MarkupLine($"[green]Results saved to:[/] {Markup.Escape(effectiveOutputDir)}");
            
            return 0;
        });
        
        rootCommand.Subcommands.Add(command);
    }

    private static void AddTemplatesCommand(RootCommand rootCommand)
    {
        var command = new Command("templates", "List available summary templates");
        
        command.SetAction((parseResult, cancellationToken) =>
        {
            SpectreProgressService.WriteHeader("DocSummarizer", "Templates");
            
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Cyan1)
                .Title("[cyan]Available Templates[/]");
            
            table.AddColumn(new TableColumn("[cyan]Name[/]").LeftAligned());
            table.AddColumn(new TableColumn("[cyan]Words[/]").RightAligned());
            table.AddColumn(new TableColumn("[cyan]Description[/]"));
            
            var presets = new[]
            {
                ("default", "~500", "General purpose summary"),
                ("prose", "~400", "Clean multi-paragraph prose"),
                ("brief", "~50", "Quick scanning"),
                ("oneliner", "~25", "Single sentence"),
                ("strict", "~60", "Ultra-concise"),
                ("bullets", "auto", "Key takeaways as bullets"),
                ("executive", "~150", "C-suite reports"),
                ("detailed", "~1000", "Comprehensive analysis"),
                ("technical", "~300", "Tech documentation"),
                ("academic", "~250", "Research papers"),
                ("citations", "auto", "Key quotes with sources"),
                ("bookreport", "~800", "Book report style"),
                ("meeting", "~200", "Meeting notes")
            };
            
            foreach (var (name, words, desc) in presets)
                table.AddRow($"[yellow]{name}[/]", $"[dim]{words}[/]", desc);
            
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Usage: docsummarizer -f doc.pdf -t executive[/]");
            
            return Task.FromResult(0);
        });
        
        rootCommand.Subcommands.Add(command);
    }

    private static void AddToolCommand(RootCommand rootCommand)
    {
        var command = new Command("tool", "JSON output for LLM tool integration");
        var urlOpt = new Option<string?>("--url", "-u") { Description = "URL to process" };
        var fileOpt = new Option<FileInfo?>("--file", "-f") { Description = "File to process" };
        var askOpt = new Option<string?>("--ask", "-a") { Description = "Question to ask" };
        var queryOpt = new Option<string?>("--query", "-q") { Description = "Focus query" };
        var modeOpt = new Option<SummarizationMode>("--mode", "-m") { DefaultValueFactory = _ => SummarizationMode.Auto };
        var modelOpt = new Option<string?>("--model") { Description = "Ollama model" };
        var configOpt = new Option<string?>("--config", "-c") { Description = "Config file" };
        
        command.Options.Add(urlOpt);
        command.Options.Add(fileOpt);
        command.Options.Add(askOpt);
        command.Options.Add(queryOpt);
        command.Options.Add(modeOpt);
        command.Options.Add(modelOpt);
        command.Options.Add(configOpt);
        
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var url = parseResult.GetValue(urlOpt);
            var file = parseResult.GetValue(fileOpt);
            var ask = parseResult.GetValue(askOpt);
            var query = parseResult.GetValue(queryOpt);
            var mode = parseResult.GetValue(modeOpt);
            var model = parseResult.GetValue(modelOpt);
            var configPath = parseResult.GetValue(configOpt);
            
            var config = ConfigurationLoader.Load(configPath);
            if (model != null) config.Ollama.Model = model;
            config.Output.Verbose = false;
            
            using var services = BuildServiceProvider(config);
            var summarizer = services.GetRequiredService<IDocumentSummarizer>();
            
            try
            {
                string source;
                var sw = Stopwatch.StartNew();
                
                // Determine source
                if (!string.IsNullOrEmpty(url))
                {
                    source = url;
                }
                else if (file != null && file.Exists)
                {
                    source = file.FullName;
                }
                else
                {
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { success = false, error = "No input specified" }));
                    return 1;
                }
                
                // Q&A mode (--ask)
                if (!string.IsNullOrEmpty(ask))
                {
                    string markdown;
                    if (!string.IsNullOrEmpty(url))
                    {
                        // Would need to fetch URL content first
                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { success = false, error = "Q&A mode with URLs not yet supported. Use --file instead." }));
                        return 1;
                    }
                    else
                    {
                        markdown = await File.ReadAllTextAsync(file!.FullName, cancellationToken);
                    }
                    
                    var qaResult = await summarizer.QueryAsync(markdown, ask, null, cancellationToken);
                    sw.Stop();
                    
                    var qaOutput = new
                    {
                        success = true,
                        source,
                        type = "qa",
                        question = ask,
                        answer = qaResult.Answer,
                        confidence = qaResult.Confidence.ToString(),
                        evidence = qaResult.Evidence.Select(e => new
                        {
                            segmentId = e.SegmentId,
                            text = e.Text,
                            similarity = Math.Round(e.Similarity, 3),
                            section = e.SectionTitle
                        }).ToList(),
                        metadata = new
                        {
                            processingTimeMs = (int)sw.ElapsedMilliseconds,
                            model = config.Ollama.Model
                        }
                    };
                    
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(qaOutput, new System.Text.Json.JsonSerializerOptions 
                    { 
                        WriteIndented = true,
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                    }));
                    return 0;
                }
                
                // Summary mode
                DocumentSummary? summary = null;
                
                if (!string.IsNullOrEmpty(url))
                {
                    summary = await summarizer.SummarizeUrlAsync(url, query, mode);
                }
                else if (file != null && file.Exists)
                {
                    summary = await summarizer.SummarizeFileAsync(file.FullName, query, mode);
                }
                
                sw.Stop();
                
                // Build comprehensive output for LLM consumption
                var output = new
                {
                    success = true,
                    source,
                    type = "summary",
                    summary = summary!.ExecutiveSummary,
                    wordCount = summary.ExecutiveSummary.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
                    
                    // Extracted entities for knowledge extraction
                    entities = summary.Entities != null ? new
                    {
                        people = summary.Entities.Characters,
                        organizations = summary.Entities.Organizations,
                        locations = summary.Entities.Locations,
                        dates = summary.Entities.Dates,
                        events = summary.Entities.Events
                    } : null,
                    
                    // Topics with source references
                    topics = summary.TopicSummaries.Select(t => new 
                    { 
                        topic = t.Topic, 
                        summary = t.Summary,
                        sourceChunks = t.SourceChunks
                    }).ToList(),
                    
                    // Open questions identified in the document
                    openQuestions = summary.OpenQuestions,
                    
                    // Processing metadata
                    metadata = new
                    {
                        documentId = summary.Trace.DocumentId,
                        totalChunks = summary.Trace.TotalChunks,
                        chunksProcessed = summary.Trace.ChunksProcessed,
                        coverageScore = Math.Round(summary.Trace.CoverageScore, 3),
                        citationRate = Math.Round(summary.Trace.CitationRate, 3),
                        processingTimeMs = (int)sw.ElapsedMilliseconds,
                        mode = mode.ToString(),
                        model = config.Ollama.Model
                    }
                };
                
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(output, new System.Text.Json.JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                }));
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { success = false, error = ex.Message }));
                return 1;
            }
        });
        
        rootCommand.Subcommands.Add(command);
    }

    private static void AddSearchCommand(RootCommand rootCommand)
    {
        var command = new Command("search", "Search documents for relevant segments");
        var fileOpt = new Option<FileInfo?>("--file", "-f") { Description = "Document to search" };
        var queryOpt = new Option<string?>("--query", "-q") { Description = "Search query (required)" };
        var topKOpt = new Option<int>("--top", "-k") { Description = "Number of results", DefaultValueFactory = _ => 10 };
        var configOpt = new Option<string?>("--config", "-c") { Description = "Config file" };
        var jsonOpt = new Option<bool>("--json") { Description = "Output as JSON", DefaultValueFactory = _ => false };
        
        command.Options.Add(fileOpt);
        command.Options.Add(queryOpt);
        command.Options.Add(topKOpt);
        command.Options.Add(configOpt);
        command.Options.Add(jsonOpt);
        
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var file = parseResult.GetValue(fileOpt);
            var query = parseResult.GetValue(queryOpt);
            var topK = parseResult.GetValue(topKOpt);
            var configPath = parseResult.GetValue(configOpt);
            var outputJson = parseResult.GetValue(jsonOpt);
            
            if (file == null || !file.Exists)
            {
                AnsiConsole.MarkupLine("[red]Error: --file is required[/]");
                return 1;
            }
            
            if (string.IsNullOrEmpty(query))
            {
                AnsiConsole.MarkupLine("[red]Error: --query is required[/]");
                return 1;
            }
            
            var config = ConfigurationLoader.Load(configPath);
            
            using var services = BuildServiceProvider(config);
            var summarizer = services.GetRequiredService<IDocumentSummarizer>();
            
            var markdown = await File.ReadAllTextAsync(file.FullName, cancellationToken);
            var docId = Path.GetFileNameWithoutExtension(file.Name);
            
            var extraction = await summarizer.ExtractSegmentsAsync(markdown, docId, cancellationToken);
            
            // Embed query
            using var embedder = new OnnxEmbeddingService(config.Onnx, verbose: false);
            var queryEmbedding = await embedder.EmbedAsync(query, cancellationToken);
            
            // Score and rank
            var scored = extraction.AllSegments
                .Where(s => s.Embedding != null)
                .Select(s => (Segment: s, Score: ComputeCosineSimilarity(queryEmbedding, s.Embedding!)))
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .ToList();
            
            if (outputJson)
            {
                var jsonResults = scored.Select(x => new
                {
                    section = x.Segment.SectionTitle,
                    score = Math.Round(x.Score, 4),
                    preview = x.Segment.Text.Length > 200 ? x.Segment.Text[..200] + "..." : x.Segment.Text
                });
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { query, results = jsonResults }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                SpectreProgressService.WriteHeader("DocSummarizer", "Semantic Search");
                
                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .BorderColor(Color.Cyan1)
                    .Title("[cyan]Search Results[/]");
                
                table.AddColumn(new TableColumn("[cyan]#[/]").RightAligned());
                table.AddColumn(new TableColumn("[cyan]Score[/]").RightAligned());
                table.AddColumn(new TableColumn("[cyan]Section[/]").LeftAligned());
                table.AddColumn(new TableColumn("[cyan]Preview[/]").LeftAligned());
                
                var rank = 1;
                foreach (var (segment, score) in scored)
                {
                    var preview = segment.Text.Length > 60 ? segment.Text[..60].Replace("\n", " ") + "..." : segment.Text.Replace("\n", " ");
                    var scoreColor = score > 0.7 ? "green" : score > 0.5 ? "yellow" : "white";
                    table.AddRow(
                        $"{rank++}",
                        $"[{scoreColor}]{score:F3}[/]",
                        Markup.Escape(segment.SectionTitle ?? "[no section]"),
                        $"[dim]{Markup.Escape(preview)}[/]");
                }
                
                AnsiConsole.Write(table);
            }
            
            return 0;
        });
        
        rootCommand.Subcommands.Add(command);
    }

    private static void AddCacheCommand(RootCommand rootCommand)
    {
        var cacheCommand = new Command("cache", "Manage the document vector cache");
        
        // Stats subcommand
        var statsCommand = new Command("stats", "Show cache statistics");
        var statsConfigOpt = new Option<string?>("--config", "-c") { Description = "Configuration file" };
        statsCommand.Options.Add(statsConfigOpt);
        statsCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var configPath = parseResult.GetValue(statsConfigOpt);
            var config = ConfigurationLoader.Load(configPath);
            
            SpectreProgressService.WriteHeader("DocSummarizer", "Cache Statistics");
            
            var detected = await ServiceDetector.DetectSilentAsync(config);
            if (!detected.QdrantAvailable)
            {
                AnsiConsole.MarkupLine("[yellow]Qdrant not available[/] - no persistent cache");
                return 0;
            }
            
            var qdrant = new QdrantHttpClient(config.Qdrant.Host, config.Qdrant.Port, config.Qdrant.ApiKey);
            var collections = (await qdrant.ListCollectionsAsync()).ToList();
            
            if (collections.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No cached documents found[/]");
                return 0;
            }
            
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Cyan1)
                .Title("[cyan]Cached Documents[/]");
            
            table.AddColumn(new TableColumn("[cyan]Collection[/]").LeftAligned());
            table.AddColumn(new TableColumn("[cyan]Vectors[/]").RightAligned());
            
            long totalVectors = 0;
            foreach (var collection in collections.OrderBy(c => c))
            {
                try
                {
                    var info = await qdrant.GetCollectionInfoAsync(collection);
                    var vectors = info?.VectorsCount ?? 0;
                    totalVectors += vectors;
                    table.AddRow(Markup.Escape(collection), $"{vectors:N0}");
                }
                catch
                {
                    table.AddRow(Markup.Escape(collection), "[red]error[/]");
                }
            }
            
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"\n[cyan]Total:[/] {collections.Count} documents, {totalVectors:N0} vectors");
            return 0;
        });
        cacheCommand.Subcommands.Add(statsCommand);
        
        // List subcommand
        var listCommand = new Command("list", "List cached documents");
        var listConfigOpt = new Option<string?>("--config", "-c") { Description = "Configuration file" };
        listCommand.Options.Add(listConfigOpt);
        listCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var configPath = parseResult.GetValue(listConfigOpt);
            var config = ConfigurationLoader.Load(configPath);
            
            var detected = await ServiceDetector.DetectSilentAsync(config);
            if (!detected.QdrantAvailable)
            {
                AnsiConsole.MarkupLine("[yellow]Qdrant not available[/]");
                return 0;
            }
            
            var qdrant = new QdrantHttpClient(config.Qdrant.Host, config.Qdrant.Port, config.Qdrant.ApiKey);
            var collections = await qdrant.ListCollectionsAsync();
            
            foreach (var collection in collections.OrderBy(c => c))
                Console.WriteLine(collection);
            
            return 0;
        });
        cacheCommand.Subcommands.Add(listCommand);
        
        // Rm subcommand
        var rmCommand = new Command("rm", "Remove cached documents");
        var rmDocOpt = new Option<string?>("--doc", "-d") { Description = "Document pattern to remove" };
        var rmAllOpt = new Option<bool>("--all") { Description = "Remove all", DefaultValueFactory = _ => false };
        var rmConfigOpt = new Option<string?>("--config", "-c") { Description = "Configuration file" };
        rmCommand.Options.Add(rmDocOpt);
        rmCommand.Options.Add(rmAllOpt);
        rmCommand.Options.Add(rmConfigOpt);
        rmCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var doc = parseResult.GetValue(rmDocOpt);
            var removeAll = parseResult.GetValue(rmAllOpt);
            var configPath = parseResult.GetValue(rmConfigOpt);
            
            if (string.IsNullOrEmpty(doc) && !removeAll)
            {
                AnsiConsole.MarkupLine("[red]Error: Specify --doc or --all[/]");
                return 1;
            }
            
            var config = ConfigurationLoader.Load(configPath);
            var detected = await ServiceDetector.DetectSilentAsync(config);
            if (!detected.QdrantAvailable)
            {
                AnsiConsole.MarkupLine("[yellow]Qdrant not available[/]");
                return 0;
            }
            
            var qdrant = new QdrantHttpClient(config.Qdrant.Host, config.Qdrant.Port, config.Qdrant.ApiKey);
            var collections = await qdrant.ListCollectionsAsync();
            
            var toRemove = removeAll 
                ? collections.ToList()
                : collections.Where(c => c.Contains(doc!, StringComparison.OrdinalIgnoreCase)).ToList();
            
            if (toRemove.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No matching documents found[/]");
                return 0;
            }
            
            AnsiConsole.MarkupLine($"[yellow]Removing {toRemove.Count} document(s)[/]");
            
            if (!AnsiConsole.Confirm("Continue?", false))
                return 0;
            
            foreach (var collection in toRemove)
            {
                try
                {
                    await qdrant.DeleteCollectionAsync(collection);
                    AnsiConsole.MarkupLine($"[green]Removed:[/] {Markup.Escape(collection)}");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Failed:[/] {Markup.Escape(collection)} - {Markup.Escape(ex.Message)}");
                }
            }
            
            return 0;
        });
        cacheCommand.Subcommands.Add(rmCommand);
        
        rootCommand.Subcommands.Add(cacheCommand);
    }

    // ============================================================================
    // Helper: Cosine Similarity
    // ============================================================================

    private static double ComputeCosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        
        double dotProduct = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        
        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator > 0 ? dotProduct / denominator : 0;
    }
}
