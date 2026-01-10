using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Extensions;
using Mostlylucid.DocSummarizer.Images.Pipeline;
using Mostlylucid.Summarizer.Core.Extensions;
using Mostlylucid.Summarizer.Core.Pipeline;
using Spectre.Console;

namespace Mostlylucid.ImageSummarizer.Cli;

/// <summary>
/// Standalone image analysis and OCR CLI tool using the unified pipeline architecture.
/// Part of the LucidRAG Summarizer family.
/// </summary>
class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Image Intelligence - Heuristic analysis + Vision LLM escalation");

        // Arguments
        var imageArg = new Argument<string?>("image")
        {
            Description = "Path to image file",
            Arity = ArgumentArity.ZeroOrOne
        };

        // Options
        var outputOpt = new Option<string>("--output", "-o")
        {
            Description = "Output format: text, json, signals",
            DefaultValueFactory = _ => "text"
        };

        var verboseOpt = new Option<bool>("--verbose", "-v")
        {
            Description = "Enable verbose logging",
            DefaultValueFactory = _ => false
        };

        var modelsDirOpt = new Option<string?>("--models-dir")
        {
            Description = "Directory for model files"
        };

        var listPipelinesOpt = new Option<bool>("--list-pipelines")
        {
            Description = "List available pipelines and exit",
            DefaultValueFactory = _ => false
        };

        rootCommand.Arguments.Add(imageArg);
        rootCommand.Options.Add(outputOpt);
        rootCommand.Options.Add(verboseOpt);
        rootCommand.Options.Add(modelsDirOpt);
        rootCommand.Options.Add(listPipelinesOpt);

        rootCommand.SetAction(async (parseResult, ct) =>
        {
            var imagePath = parseResult.GetValue(imageArg);
            var output = parseResult.GetValue(outputOpt) ?? "text";
            var verbose = parseResult.GetValue(verboseOpt);
            var modelsDir = parseResult.GetValue(modelsDirOpt);
            var listPipelines = parseResult.GetValue(listPipelinesOpt);

            // Build services
            var services = BuildServices(verbose, modelsDir);
            using var scope = services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IPipelineRegistry>();

            // List pipelines mode
            if (listPipelines)
            {
                ShowPipelines(registry);
                return;
            }

            // Validate input
            if (string.IsNullOrEmpty(imagePath))
            {
                AnsiConsole.MarkupLine("[yellow]Usage: imagesummarizer <image-path> [options][/]");
                AnsiConsole.MarkupLine("[dim]Use --list-pipelines to see available pipelines[/]");
                return;
            }

            if (!File.Exists(imagePath))
            {
                AnsiConsole.MarkupLine($"[red]Error: File not found: {imagePath}[/]");
                return;
            }

            // Find appropriate pipeline
            var pipeline = registry.FindForFile(imagePath);
            if (pipeline == null)
            {
                AnsiConsole.MarkupLine($"[red]Error: No pipeline found for file type: {Path.GetExtension(imagePath)}[/]");
                return;
            }

            // Process the image
            await ProcessImageAsync(pipeline, imagePath, output, verbose, ct);
        });

        return await rootCommand.Parse(args).InvokeAsync();
    }

    private static ServiceProvider BuildServices(bool verbose, string? modelsDir)
    {
        var services = new ServiceCollection();

        // Logging
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning);
            builder.AddConsole();
        });

        // Image services with OCR pipeline
        services.AddDocSummarizerImages(opt =>
        {
            if (!string.IsNullOrEmpty(modelsDir))
                opt.ModelsDirectory = modelsDir;

            opt.EnableOcr = true;
            opt.Ocr.UseAdvancedPipeline = true;
            opt.Ocr.QualityMode = OcrQualityMode.Fast;
        });

        // Pipeline registry
        services.AddPipelineRegistry();

        return services.BuildServiceProvider();
    }

    private static void ShowPipelines(IPipelineRegistry registry)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[bold]ID[/]")
            .AddColumn("[bold]Name[/]")
            .AddColumn("[bold]Extensions[/]");

        foreach (var pipeline in registry.GetAll())
        {
            var extensions = string.Join(", ", pipeline.SupportedExtensions.Take(8));
            if (pipeline.SupportedExtensions.Count > 8)
                extensions += $" (+{pipeline.SupportedExtensions.Count - 8})";

            table.AddRow(
                $"[green]{pipeline.PipelineId}[/]",
                pipeline.Name,
                $"[dim]{extensions}[/]");
        }

        AnsiConsole.Write(table);
    }

    private static async Task ProcessImageAsync(
        IPipeline pipeline,
        string imagePath,
        string outputFormat,
        bool verbose,
        CancellationToken ct)
    {
        AnsiConsole.MarkupLine($"[cyan]Processing:[/] {Path.GetFileName(imagePath)}");
        AnsiConsole.MarkupLine($"[cyan]Pipeline:[/] {pipeline.Name}");
        AnsiConsole.WriteLine();

        var progress = verbose ? new Progress<PipelineProgress>(p =>
        {
            AnsiConsole.MarkupLine($"[dim]{p.Stage}: {p.Message}[/]");
        }) : null;

        var result = await pipeline.ProcessAsync(imagePath, null, progress, ct);

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {result.Error}");
            return;
        }

        // Output results based on format
        switch (outputFormat.ToLower())
        {
            case "json":
                OutputJson(result);
                break;

            case "signals":
                OutputSignals(result);
                break;

            default:
                OutputText(result);
                break;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Processed in {result.ProcessingTime.TotalMilliseconds:F0}ms[/]");
    }

    private static void OutputText(PipelineResult result)
    {
        foreach (var chunk in result.Chunks)
        {
            var typeLabel = chunk.ContentType switch
            {
                ContentType.ImageOcr => "[blue]OCR Text[/]",
                ContentType.ImageCaption => "[green]Caption[/]",
                ContentType.Entity => "[yellow]Entities[/]",
                _ => $"[grey]{chunk.ContentType}[/]"
            };

            AnsiConsole.MarkupLine(typeLabel);
            AnsiConsole.WriteLine(chunk.Text);
            AnsiConsole.WriteLine();
        }
    }

    private static void OutputJson(PipelineResult result)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        var output = new
        {
            result.FilePath,
            result.Success,
            result.ProcessingTime,
            Chunks = result.Chunks.Select(c => new
            {
                c.Id,
                c.Text,
                Type = c.ContentType.ToString(),
                c.Confidence,
                c.Metadata
            })
        };

        Console.WriteLine(JsonSerializer.Serialize(output, options));
    }

    private static void OutputSignals(PipelineResult result)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Chunk[/]")
            .AddColumn("[bold]Type[/]")
            .AddColumn("[bold]Confidence[/]")
            .AddColumn("[bold]Length[/]");

        foreach (var chunk in result.Chunks)
        {
            table.AddRow(
                chunk.Id,
                chunk.ContentType.ToString(),
                chunk.Confidence?.ToString("P1") ?? "-",
                chunk.Text.Length.ToString());
        }

        AnsiConsole.Write(table);
    }
}
