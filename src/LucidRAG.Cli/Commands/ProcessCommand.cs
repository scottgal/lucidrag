using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.Summarizer.Core.Pipeline;
using Spectre.Console;
using LucidRAG.Cli.Services;
using LucidRAG.Data;
using LucidRAG.Entities;

namespace LucidRAG.Cli.Commands;

/// <summary>
/// Unified processing command using the IPipelineRegistry.
/// Routes files to the appropriate pipeline based on extension.
/// </summary>
public static class ProcessCommand
{
    private static readonly Argument<string[]> FilesArg = new("files")
    {
        Description = "Files to process",
        Arity = ArgumentArity.OneOrMore
    };

    private static readonly Option<string?> PipelineOpt = new("--pipeline", "-p")
    {
        Description = "Force specific pipeline (doc, image, data) or 'auto' for auto-detection"
    };

    private static readonly Option<string?> CollectionOpt = new("--collection", "-c")
    {
        Description = "Target collection name"
    };

    private static readonly Option<bool> ExtractEntitiesOpt = new("--entities", "-e")
    {
        Description = "Extract entities (GraphRAG)",
        DefaultValueFactory = _ => true
    };

    private static readonly Option<bool> VerboseOpt = new("-v", "--verbose")
    {
        Description = "Verbose output",
        DefaultValueFactory = _ => false
    };

    private static readonly Option<string?> DataDirOpt = new("--data-dir")
    {
        Description = "Data directory for storage/cache"
    };

    private static readonly Option<bool> DryRunOpt = new("--dry-run")
    {
        Description = "Show what would be processed without actually processing",
        DefaultValueFactory = _ => false
    };

    private static readonly Option<bool> ListPipelinesOpt = new("--list-pipelines")
    {
        Description = "List all available pipelines and exit",
        DefaultValueFactory = _ => false
    };

    public static Command Create()
    {
        var command = new Command("process", "Process files through unified pipelines (doc, image, data)");
        command.Arguments.Add(FilesArg);
        command.Options.Add(PipelineOpt);
        command.Options.Add(CollectionOpt);
        command.Options.Add(ExtractEntitiesOpt);
        command.Options.Add(VerboseOpt);
        command.Options.Add(DataDirOpt);
        command.Options.Add(DryRunOpt);
        command.Options.Add(ListPipelinesOpt);

        // Add aliases
        command.Aliases.Add("ingest");

        command.SetAction(async (parseResult, ct) =>
        {
            var files = parseResult.GetValue(FilesArg) ?? [];
            var forcePipeline = parseResult.GetValue(PipelineOpt);
            var collection = parseResult.GetValue(CollectionOpt);
            var extractEntities = parseResult.GetValue(ExtractEntitiesOpt);
            var verbose = parseResult.GetValue(VerboseOpt);
            var dataDir = parseResult.GetValue(DataDirOpt);
            var dryRun = parseResult.GetValue(DryRunOpt);
            var listPipelines = parseResult.GetValue(ListPipelinesOpt);

            var config = new CliConfig
            {
                DataDirectory = Program.EnsureDataDirectory(dataDir),
                Verbose = verbose
            };

            AnsiConsole.Write(new FigletText("Process").Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[dim]Unified pipeline processing for documents, images, and data[/]\n");

            await using var services = CliServiceRegistration.BuildServiceProvider(config, verbose);
            using var scope = services.CreateScope();

            // Get the pipeline registry
            var registry = scope.ServiceProvider.GetRequiredService<IPipelineRegistry>();

            // List pipelines mode
            if (listPipelines)
            {
                ShowPipelines(registry);
                return;
            }

            // Expand file patterns and categorize by pipeline
            var filesByPipeline = CategorizeFilesByPipeline(files, registry, forcePipeline);

            // Show summary
            ShowSummary(filesByPipeline);

            if (dryRun)
            {
                AnsiConsole.MarkupLine("\n[cyan]Dry run - no files will be processed[/]");
                foreach (var (pipeline, pipelineFiles) in filesByPipeline.Where(kv => kv.Value.Count > 0))
                {
                    ShowFileList(pipeline?.Name ?? "Unknown", pipelineFiles,
                        pipeline != null ? GetPipelineColor(pipeline.PipelineId) : Color.Red);
                }
                return;
            }

            // Ensure database is created
            await CliServiceRegistration.EnsureDatabaseAsync(services);

            // Resolve collection
            Guid? collectionId = await ResolveCollectionAsync(scope.ServiceProvider, collection, ct);

            // Process files by pipeline
            var totalChunks = 0;
            var totalFiles = 0;
            var totalFailed = 0;

            foreach (var (pipeline, pipelineFiles) in filesByPipeline.Where(kv => kv.Key != null && kv.Value.Count > 0))
            {
                var (processed, chunks, failed) = await ProcessPipelineAsync(
                    pipeline!,
                    pipelineFiles,
                    collectionId,
                    verbose,
                    ct);

                totalFiles += processed;
                totalChunks += chunks;
                totalFailed += failed;
            }

            // Handle unknown files
            var unknownFiles = filesByPipeline.FirstOrDefault(kv => kv.Key == null).Value ?? [];
            if (unknownFiles.Count > 0)
            {
                AnsiConsole.MarkupLine($"\n[yellow]⚠ Skipped {unknownFiles.Count} files with no matching pipeline[/]");
                totalFailed += unknownFiles.Count;
            }

            // Final summary
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[bold]Summary[/]").RuleStyle("dim"));
            AnsiConsole.MarkupLine($"[green]Processed:[/] {totalFiles} files → {totalChunks} chunks");
            if (totalFailed > 0)
                AnsiConsole.MarkupLine($"[red]Failed:[/] {totalFailed} files");
            AnsiConsole.MarkupLine("[green bold]✓ Processing complete[/]");
        });

        return command;
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
                $"[{GetPipelineColor(pipeline.PipelineId)}]{pipeline.PipelineId}[/]",
                pipeline.Name,
                $"[dim]{extensions}[/]");
        }

        AnsiConsole.Write(table);
    }

    private static Dictionary<IPipeline?, List<string>> CategorizeFilesByPipeline(
        string[] files,
        IPipelineRegistry registry,
        string? forcePipelineId)
    {
        var result = new Dictionary<IPipeline?, List<string>>();
        IPipeline? forcedPipeline = null;

        if (!string.IsNullOrEmpty(forcePipelineId) && forcePipelineId != "auto")
        {
            forcedPipeline = registry.GetById(forcePipelineId);
            if (forcedPipeline == null)
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ Unknown pipeline '{forcePipelineId}', using auto-detection[/]");
            }
        }

        foreach (var pattern in files)
        {
            var expanded = ExpandGlob(pattern);

            foreach (var file in expanded)
            {
                if (!File.Exists(file))
                {
                    AddToDict(result, null, file);
                    continue;
                }

                var pipeline = forcedPipeline ?? registry.FindForFile(file);
                AddToDict(result, pipeline, file);
            }
        }

        return result;
    }

    private static void AddToDict(Dictionary<IPipeline?, List<string>> dict, IPipeline? key, string value)
    {
        if (!dict.TryGetValue(key, out var list))
        {
            list = [];
            dict[key] = list;
        }
        list.Add(value);
    }

    private static void ShowSummary(Dictionary<IPipeline?, List<string>> filesByPipeline)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[bold]Pipeline[/]")
            .AddColumn("[bold]Files[/]");

        foreach (var (pipeline, files) in filesByPipeline.OrderBy(kv => kv.Key?.Name ?? "zzz"))
        {
            if (files.Count > 0)
            {
                var name = pipeline?.Name ?? "[red]Unknown[/]";
                var color = pipeline != null ? GetPipelineColor(pipeline.PipelineId) : Color.Red;
                table.AddRow($"[{color}]{name}[/]", files.Count.ToString());
            }
        }

        AnsiConsole.Write(table);
    }

    private static Color GetPipelineColor(string pipelineId) => pipelineId.ToLower() switch
    {
        "doc" => Color.Blue,
        "image" => Color.Green,
        "data" => Color.Yellow,
        _ => Color.Grey
    };

    private static async Task<Guid?> ResolveCollectionAsync(
        IServiceProvider services,
        string? collectionName,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(collectionName))
            return null;

        var db = services.GetRequiredService<RagDocumentsDbContext>();
        var collection = await db.Collections.FirstOrDefaultAsync(c => c.Name == collectionName, ct);

        if (collection == null)
        {
            collection = new CollectionEntity
            {
                Id = Guid.NewGuid(),
                Name = collectionName,
                Description = "Collection created by CLI"
            };
            db.Collections.Add(collection);
            await db.SaveChangesAsync(ct);
            AnsiConsole.MarkupLine($"[dim]Created collection: {collectionName}[/]");
        }

        return collection.Id;
    }

    private static async Task<(int processed, int chunks, int failed)> ProcessPipelineAsync(
        IPipeline pipeline,
        List<string> files,
        Guid? collectionId,
        bool verbose,
        CancellationToken ct)
    {
        var color = GetPipelineColor(pipeline.PipelineId);
        AnsiConsole.MarkupLine($"\n[{color} bold]═══ {pipeline.Name} ═══[/]\n");

        var processed = 0;
        var totalChunks = 0;
        var failed = 0;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"[{color}]Processing {files.Count} files[/]", maxValue: files.Count);

                foreach (var file in files)
                {
                    task.Description = $"[{color}]{Path.GetFileName(file)}[/]";

                    try
                    {
                        var progress = new Progress<PipelineProgress>(p =>
                        {
                            if (verbose)
                            {
                                task.Description = $"[{color}]{p.Stage}: {p.Message}[/]";
                            }
                        });

                        var result = await pipeline.ProcessAsync(file, null, progress, ct);

                        if (result.Success)
                        {
                            processed++;
                            totalChunks += result.Chunks.Count;

                            if (verbose)
                            {
                                AnsiConsole.MarkupLine($"  [green]✓[/] {Path.GetFileName(file)} - {result.Chunks.Count} chunks ({result.ProcessingTime.TotalMilliseconds:F0}ms)");
                            }
                        }
                        else
                        {
                            failed++;
                            AnsiConsole.MarkupLine($"  [red]✗[/] {Path.GetFileName(file)} - {result.Error}");
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        AnsiConsole.MarkupLine($"  [red]✗[/] {Path.GetFileName(file)} - {ex.Message}");
                    }

                    task.Increment(1);
                }
            });

        // Summary for this pipeline
        AnsiConsole.MarkupLine($"[{color}]{pipeline.Name}:[/] {processed} processed → {totalChunks} chunks, {failed} failed");

        return (processed, totalChunks, failed);
    }

    private static void ShowFileList(string category, List<string> files, Color color)
    {
        if (files.Count == 0) return;

        AnsiConsole.MarkupLine($"\n[{color}]{category}:[/]");
        foreach (var f in files.Take(10))
            AnsiConsole.MarkupLine($"  {Path.GetFileName(f)}");
        if (files.Count > 10)
            AnsiConsole.MarkupLine($"  [dim]...and {files.Count - 10} more[/]");
    }

    private static IEnumerable<string> ExpandGlob(string pattern)
    {
        if (pattern.Contains('*') || pattern.Contains('?'))
        {
            var dir = Path.GetDirectoryName(pattern);
            if (string.IsNullOrEmpty(dir))
                dir = ".";

            var filePattern = Path.GetFileName(pattern);

            if (Directory.Exists(dir))
            {
                return Directory.GetFiles(dir, filePattern, SearchOption.TopDirectoryOnly);
            }
            return [];
        }

        return [Path.GetFullPath(pattern)];
    }
}
