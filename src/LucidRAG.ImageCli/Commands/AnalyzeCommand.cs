using System.CommandLine;
using System.CommandLine.Parsing;
using LucidRAG.ImageCli.Services;
using LucidRAG.ImageCli.Services.OutputFormatters;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace LucidRAG.ImageCli.Commands;

/// <summary>
/// Command for analyzing a single image file.
/// </summary>
public static class AnalyzeCommand
{
    private static readonly Argument<string> ImagePathArg = new("image-path") { Description = "Path to the image file to analyze" };

    private static readonly Option<OutputFormat> FormatOpt = new("--format", "-f") { Description = "Output format", DefaultValueFactory = _ => OutputFormat.Table };

    private static readonly Option<string?> OutputOpt = new("--output", "-o") { Description = "Save output to file" };

    private static readonly Option<bool> IncludeOcrOpt = new("--include-ocr") { Description = "Extract text using OCR if text detected", DefaultValueFactory = _ => false };

    private static readonly Option<bool> IncludeClipOpt = new("--include-clip") { Description = "Generate CLIP embeddings for similarity search", DefaultValueFactory = _ => false };

    private static readonly Option<bool> UseLlmOpt = new("--use-llm", "--llm") { Description = "Use vision LLM for enhanced description (requires Ollama)", DefaultValueFactory = _ => false };

    private static readonly Option<string?> ModelOpt = new("--model", "-m") { Description = "Vision model to use. Format: 'model' or 'provider:model' (e.g., minicpm-v:8b, anthropic:claude-3-5-sonnet-20241022, openai:gpt-4o)" };

    private static readonly Option<string?> ThumbnailOpt = new("--thumbnail") { Description = "Generate and save thumbnail to specified path" };

    private static readonly Option<bool> VerboseOpt = new("--verbose", "-v") { Description = "Show detailed analysis information", DefaultValueFactory = _ => false };

    private static readonly Option<bool> SkipCacheOpt = new("--skip-cache") { Description = "Skip cache and force fresh analysis (useful for model comparison)", DefaultValueFactory = _ => false };

    public static Command Create()
    {
        var command = new Command("analyze", "Analyze a single image file");
        command.Arguments.Add(ImagePathArg);
        command.Options.Add(FormatOpt);
        command.Options.Add(OutputOpt);
        command.Options.Add(IncludeOcrOpt);
        command.Options.Add(IncludeClipOpt);
        command.Options.Add(UseLlmOpt);
        command.Options.Add(ModelOpt);
        command.Options.Add(ThumbnailOpt);
        command.Options.Add(VerboseOpt);
        command.Options.Add(SkipCacheOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var imagePath = parseResult.GetValue(ImagePathArg)!;
            var format = parseResult.GetValue(FormatOpt);
            var output = parseResult.GetValue(OutputOpt);
            var includeOcr = parseResult.GetValue(IncludeOcrOpt);
            var includeClip = parseResult.GetValue(IncludeClipOpt);
            var useLlm = parseResult.GetValue(UseLlmOpt);
            var model = parseResult.GetValue(ModelOpt);
            var thumbnail = parseResult.GetValue(ThumbnailOpt);
            var verbose = parseResult.GetValue(VerboseOpt);
            var skipCache = parseResult.GetValue(SkipCacheOpt);

            // Validate image path
            if (!File.Exists(imagePath))
            {
                AnsiConsole.MarkupLine($"[red]✗ Error:[/] Image file not found: {Markup.Escape(imagePath)}");
                return 1;
            }

            // Build service provider with user secrets for API keys
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .AddUserSecrets<Program>(optional: true) // Load API keys from user secrets
                .AddEnvironmentVariables("LUCIDRAG_")
                .Build();

            var services = Program.BuildServiceProvider(configuration, verbose);

            // Get services
            var escalationService = services.GetRequiredService<EscalationService>();
            var imageAnalyzer = services.GetRequiredService<Mostlylucid.DocSummarizer.Images.Services.Analysis.IImageAnalyzer>();

            IOutputFormatter formatter = format switch
            {
                OutputFormat.Json => services.GetRequiredService<JsonFormatter>(),
                OutputFormat.Markdown => services.GetRequiredService<MarkdownFormatter>(),
                _ => services.GetRequiredService<TableFormatter>()
            };

            try
            {
                // Analyze image with optional escalation
                var result = await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(Style.Parse("cyan"))
                    .StartAsync($"Analyzing [cyan]{Markup.Escape(Path.GetFileName(imagePath))}[/]...", async ctx =>
                    {
                        if (useLlm)
                        {
                            // Check if Ollama is available
                            var visionService = services.GetRequiredService<VisionLlmService>();
                            var (available, message) = await visionService.CheckAvailabilityAsync(ct);

                            if (!available)
                            {
                                AnsiConsole.MarkupLine($"[yellow]⚠ Warning:[/] {Markup.Escape(message ?? "Ollama not available")}");
                                AnsiConsole.MarkupLine("[dim]Continuing with deterministic analysis only...[/]");
                                useLlm = false;
                            }
                        }

                        return await escalationService.AnalyzeWithEscalationAsync(
                            imagePath,
                            forceEscalate: useLlm,
                            enableOcr: includeOcr,
                            bypassCache: skipCache,
                            visionModel: model,
                            ct: ct);
                    });

                // Generate thumbnail if requested
                if (!string.IsNullOrEmpty(thumbnail))
                {
                    var thumbnailBytes = await imageAnalyzer.GenerateThumbnailAsync(imagePath, 256, ct);
                    await File.WriteAllBytesAsync(thumbnail, thumbnailBytes, ct);
                    AnsiConsole.MarkupLine($"[green]✓[/] Thumbnail saved to: {Markup.Escape(thumbnail)}");
                }

                // Display results
                if (result.WasEscalated && result.LlmCaption != null)
                {
                    AnsiConsole.MarkupLine($"[yellow]↗[/] Analysis escalated to vision LLM: {result.EscalationReason}");
                }

                var formattedOutput = formatter.FormatSingle(
                    imagePath,
                    result.Profile,
                    result.LlmCaption,
                    result.ExtractedText,
                    result.GifMotion,
                    result.EvidenceClaims);

                await formatter.WriteAsync(formattedOutput, output);

                if (verbose)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[dim]Analysis completed successfully[/]");
                }

                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Error:[/] {Markup.Escape(ex.Message)}");
                if (verbose)
                {
                    AnsiConsole.WriteException(ex);
                }
                return 1;
            }
        });

        return command;
    }
}

/// <summary>
/// Output format options.
/// </summary>
public enum OutputFormat
{
    Table,
    Json,
    Markdown
}

// Extension method for getting required service
file static class ServiceProviderExtensions
{
    public static T GetRequiredService<T>(this IServiceProvider services) where T : notnull
    {
        return (T)(services.GetService(typeof(T)) ??
            throw new InvalidOperationException($"Service of type {typeof(T)} not found"));
    }
}
