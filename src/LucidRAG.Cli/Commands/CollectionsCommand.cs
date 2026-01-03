using System.CommandLine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LucidRAG.Cli.Services;
using LucidRAG.Data;
using LucidRAG.Entities;
using Spectre.Console;

namespace LucidRAG.Cli.Commands;

/// <summary>
/// Manage document collections
/// </summary>
public static class CollectionsCommand
{
    public static Command Create()
    {
        var command = new Command("collections", "Manage document collections");

        command.Subcommands.Add(CreateListCommand());
        command.Subcommands.Add(CreateCreateCommand());
        command.Subcommands.Add(CreateDeleteCommand());
        command.Subcommands.Add(CreateStatsCommand());

        return command;
    }

    private static Command CreateListCommand()
    {
        var dataDirOpt = new Option<string?>("--data-dir") { Description = "Data directory" };

        var command = new Command("list", "List all collections") { dataDirOpt };

        command.SetAction(async (parseResult, ct) =>
        {
            var dataDir = parseResult.GetValue(dataDirOpt);
            var config = new CliConfig { DataDirectory = Program.EnsureDataDirectory(dataDir) };

            await using var services = CliServiceRegistration.BuildServiceProvider(config, false);
            await CliServiceRegistration.EnsureDatabaseAsync(services);

            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

            var collections = await db.Collections
                .Include(c => c.Documents)
                .OrderBy(c => c.Name)
                .ToListAsync(ct);

            if (collections.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No collections found. Use 'lucidrag collections create <name>' to create one.[/]");
                return 0;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Cyan1)
                .Title("[cyan]Collections[/]");

            table.AddColumn(new TableColumn("[cyan]Name[/]").LeftAligned());
            table.AddColumn(new TableColumn("[cyan]Documents[/]").RightAligned());
            table.AddColumn(new TableColumn("[cyan]Segments[/]").RightAligned());
            table.AddColumn(new TableColumn("[cyan]Created[/]").LeftAligned());

            foreach (var c in collections)
            {
                var docCount = c.Documents.Count;
                var segmentCount = c.Documents.Where(d => d.Status == DocumentStatus.Completed).Sum(d => d.SegmentCount);
                table.AddRow(
                    Markup.Escape(c.Name),
                    $"{docCount}",
                    $"{segmentCount}",
                    c.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
            }

            AnsiConsole.Write(table);
            return 0;
        });

        return command;
    }

    private static Command CreateCreateCommand()
    {
        var nameArg = new Argument<string>("name") { Description = "Collection name" };
        var descriptionOpt = new Option<string?>("-d", "--description") { Description = "Collection description" };
        var dataDirOpt = new Option<string?>("--data-dir") { Description = "Data directory" };

        var command = new Command("create", "Create a new collection");
        command.Arguments.Add(nameArg);
        command.Options.Add(descriptionOpt);
        command.Options.Add(dataDirOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var description = parseResult.GetValue(descriptionOpt);
            var dataDir = parseResult.GetValue(dataDirOpt);

            var config = new CliConfig { DataDirectory = Program.EnsureDataDirectory(dataDir) };

            await using var services = CliServiceRegistration.BuildServiceProvider(config, false);
            await CliServiceRegistration.EnsureDatabaseAsync(services);

            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

            var existing = await db.Collections.FirstOrDefaultAsync(c => c.Name == name, ct);
            if (existing != null)
            {
                AnsiConsole.MarkupLine($"[yellow]Collection '{Markup.Escape(name)}' already exists[/]");
                return 1;
            }

            var collection = new CollectionEntity
            {
                Id = Guid.NewGuid(),
                Name = name,
                Description = description
            };

            db.Collections.Add(collection);
            await db.SaveChangesAsync(ct);

            AnsiConsole.MarkupLine($"[green]✓[/] Created collection: {Markup.Escape(name)}");
            return 0;
        });

        return command;
    }

    private static Command CreateDeleteCommand()
    {
        var nameArg = new Argument<string>("name") { Description = "Collection name" };
        var forceOpt = new Option<bool>("-f", "--force") { Description = "Skip confirmation" };
        var dataDirOpt = new Option<string?>("--data-dir") { Description = "Data directory" };

        var command = new Command("delete", "Delete a collection and all its documents");
        command.Arguments.Add(nameArg);
        command.Options.Add(forceOpt);
        command.Options.Add(dataDirOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var force = parseResult.GetValue(forceOpt);
            var dataDir = parseResult.GetValue(dataDirOpt);

            var config = new CliConfig { DataDirectory = Program.EnsureDataDirectory(dataDir) };

            await using var services = CliServiceRegistration.BuildServiceProvider(config, false);
            await CliServiceRegistration.EnsureDatabaseAsync(services);

            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

            var collection = await db.Collections
                .Include(c => c.Documents)
                .FirstOrDefaultAsync(c => c.Name == name, ct);

            if (collection == null)
            {
                AnsiConsole.MarkupLine($"[red]Collection '{Markup.Escape(name)}' not found[/]");
                return 1;
            }

            var docCount = collection.Documents.Count;

            if (!force)
            {
                var confirm = AnsiConsole.Confirm(
                    $"Delete collection '{name}' and {docCount} documents?",
                    defaultValue: false);
                if (!confirm)
                {
                    AnsiConsole.MarkupLine("[dim]Cancelled[/]");
                    return 0;
                }
            }

            // Delete document files
            foreach (var doc in collection.Documents)
            {
                if (!string.IsNullOrEmpty(doc.FilePath))
                {
                    var dir = Path.GetDirectoryName(doc.FilePath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        try
                        {
                            Directory.Delete(dir, recursive: true);
                        }
                        catch { }
                    }
                }
            }

            db.Collections.Remove(collection);
            await db.SaveChangesAsync(ct);

            AnsiConsole.MarkupLine($"[green]✓[/] Deleted collection '{Markup.Escape(name)}' and {docCount} documents");
            return 0;
        });

        return command;
    }

    private static Command CreateStatsCommand()
    {
        var nameArg = new Argument<string?>("name") { Description = "Collection name (optional)", Arity = ArgumentArity.ZeroOrOne };
        var dataDirOpt = new Option<string?>("--data-dir") { Description = "Data directory" };

        var command = new Command("stats", "Show collection statistics");
        command.Arguments.Add(nameArg);
        command.Options.Add(dataDirOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var name = parseResult.GetValue(nameArg);
            var dataDir = parseResult.GetValue(dataDirOpt);

            var config = new CliConfig { DataDirectory = Program.EnsureDataDirectory(dataDir) };

            await using var services = CliServiceRegistration.BuildServiceProvider(config, false);
            await CliServiceRegistration.EnsureDatabaseAsync(services);

            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

            IQueryable<DocumentEntity> docsQuery = db.Documents.Include(d => d.Collection);
            string title;

            if (!string.IsNullOrEmpty(name))
            {
                var collection = await db.Collections.FirstOrDefaultAsync(c => c.Name == name, ct);
                if (collection == null)
                {
                    AnsiConsole.MarkupLine($"[red]Collection '{Markup.Escape(name)}' not found[/]");
                    return 1;
                }
                docsQuery = docsQuery.Where(d => d.CollectionId == collection.Id);
                title = $"Collection: {name}";
            }
            else
            {
                title = "All Documents";
            }

            var docs = await docsQuery.ToListAsync(ct);

            var completed = docs.Where(d => d.Status == DocumentStatus.Completed).ToList();
            var failed = docs.Where(d => d.Status == DocumentStatus.Failed).ToList();
            var processing = docs.Where(d => d.Status == DocumentStatus.Processing).ToList();

            var statsTable = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Cyan1)
                .Title($"[cyan]{Markup.Escape(title)}[/]");

            statsTable.AddColumn(new TableColumn("[cyan]Metric[/]").LeftAligned());
            statsTable.AddColumn(new TableColumn("[cyan]Value[/]").RightAligned());

            statsTable.AddRow("Total Documents", $"{docs.Count}");
            statsTable.AddRow("Completed", $"[green]{completed.Count}[/]");
            statsTable.AddRow("Failed", failed.Count > 0 ? $"[red]{failed.Count}[/]" : "0");
            statsTable.AddRow("Processing", processing.Count > 0 ? $"[yellow]{processing.Count}[/]" : "0");
            statsTable.AddRow("Total Segments", $"{completed.Sum(d => d.SegmentCount)}");
            statsTable.AddRow("Total Size", FormatBytes(completed.Sum(d => d.FileSizeBytes ?? 0)));

            AnsiConsole.Write(statsTable);

            if (docs.Count > 0)
            {
                AnsiConsole.WriteLine();

                var docsTable = new Table()
                    .Border(TableBorder.Rounded)
                    .BorderColor(Color.Green)
                    .Title("[green]Documents[/]");

                docsTable.AddColumn(new TableColumn("[green]Name[/]").LeftAligned());
                docsTable.AddColumn(new TableColumn("[green]Status[/]").Centered());
                docsTable.AddColumn(new TableColumn("[green]Segments[/]").RightAligned());
                docsTable.AddColumn(new TableColumn("[green]Size[/]").RightAligned());

                foreach (var doc in docs.OrderByDescending(d => d.CreatedAt).Take(20))
                {
                    var statusColor = doc.Status switch
                    {
                        DocumentStatus.Completed => "green",
                        DocumentStatus.Failed => "red",
                        DocumentStatus.Processing => "yellow",
                        _ => "dim"
                    };

                    docsTable.AddRow(
                        Markup.Escape(doc.Name.Length > 40 ? doc.Name[..37] + "..." : doc.Name),
                        $"[{statusColor}]{doc.Status}[/]",
                        $"{doc.SegmentCount}",
                        FormatBytes(doc.FileSizeBytes ?? 0));
                }

                AnsiConsole.Write(docsTable);

                if (docs.Count > 20)
                    AnsiConsole.MarkupLine($"[dim]...and {docs.Count - 20} more[/]");
            }

            return 0;
        });

        return command;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
            return $"{bytes / 1024.0 / 1024.0:F1} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}
