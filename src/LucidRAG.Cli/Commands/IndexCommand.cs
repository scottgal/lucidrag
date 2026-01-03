using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;
using LucidRAG.Cli.Services;
using LucidRAG.Data;
using Spectre.Console;

namespace LucidRAG.Cli.Commands;

/// <summary>
/// Index files for RAG search
/// </summary>
public static class IndexCommand
{
    private static readonly Argument<string[]> FilesArg = new("files") { Description = "Files or directories to index", Arity = ArgumentArity.OneOrMore };
    private static readonly Option<string?> CollectionOpt = new("-c", "--collection") { Description = "Collection name" };
    private static readonly Option<bool> RecursiveOpt = new("-r", "--recursive") { Description = "Process directories recursively", DefaultValueFactory = _ => false };
    private static readonly Option<string?> DataDirOpt = new("--data-dir") { Description = "Data directory" };
    private static readonly Option<bool> VerboseOpt = new("-v", "--verbose") { Description = "Verbose output", DefaultValueFactory = _ => false };

    public static Command Create()
    {
        var command = new Command("index", "Index files for RAG search");
        command.Arguments.Add(FilesArg);
        command.Options.Add(CollectionOpt);
        command.Options.Add(RecursiveOpt);
        command.Options.Add(DataDirOpt);
        command.Options.Add(VerboseOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var files = parseResult.GetValue(FilesArg) ?? [];
            var collectionName = parseResult.GetValue(CollectionOpt);
            var recursive = parseResult.GetValue(RecursiveOpt);
            var dataDir = parseResult.GetValue(DataDirOpt);
            var verbose = parseResult.GetValue(VerboseOpt);

            var config = new CliConfig
            {
                DataDirectory = Program.EnsureDataDirectory(dataDir),
                Verbose = verbose
            };

            AnsiConsole.Write(new FigletText("LucidRAG").Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[dim]Indexing documents...[/]\n");

            await using var services = CliServiceRegistration.BuildServiceProvider(config, verbose);
            await CliServiceRegistration.EnsureDatabaseAsync(services);

            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
            var processor = scope.ServiceProvider.GetRequiredService<CliDocumentProcessor>();

            // Get or create collection
            Guid? collectionId = null;
            if (!string.IsNullOrEmpty(collectionName))
            {
                var collection = db.Collections.FirstOrDefault(c => c.Name == collectionName);
                if (collection == null)
                {
                    collection = new LucidRAG.Entities.CollectionEntity
                    {
                        Id = Guid.NewGuid(),
                        Name = collectionName
                    };
                    db.Collections.Add(collection);
                    await db.SaveChangesAsync(ct);
                    AnsiConsole.MarkupLine($"[green]Created collection:[/] {collectionName}");
                }
                collectionId = collection.Id;
            }

            var totalSuccess = 0;
            var totalFailed = 0;
            var totalSegments = 0;

            foreach (var path in files)
            {
                var fullPath = Path.GetFullPath(path);

                if (Directory.Exists(fullPath))
                {
                    AnsiConsole.MarkupLine($"\n[cyan]Indexing directory:[/] {fullPath}");
                    var results = await processor.IndexDirectoryAsync(fullPath, collectionId, recursive, ct);
                    totalSuccess += results.Count(r => r.Success);
                    totalFailed += results.Count(r => !r.Success);
                    totalSegments += results.Where(r => r.Success).Sum(r => r.SegmentCount);
                }
                else if (File.Exists(fullPath))
                {
                    AnsiConsole.MarkupLine($"\n[cyan]Indexing file:[/] {fullPath}");
                    var result = await processor.IndexFileAsync(fullPath, collectionId, ct);
                    if (result.Success)
                    {
                        totalSuccess++;
                        totalSegments += result.SegmentCount;
                        AnsiConsole.MarkupLine($"[green]✓[/] Indexed {result.SegmentCount} segments");
                    }
                    else
                    {
                        totalFailed++;
                        AnsiConsole.MarkupLine($"[red]✗[/] {result.Message}");
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]✗[/] Path not found: {path}");
                    totalFailed++;
                }
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]Completed:[/] {totalSuccess} files indexed, {totalSegments} segments");
            if (totalFailed > 0)
                AnsiConsole.MarkupLine($"[red]Failed:[/] {totalFailed} files");

            return totalFailed > 0 ? 1 : 0;
        });

        return command;
    }
}
