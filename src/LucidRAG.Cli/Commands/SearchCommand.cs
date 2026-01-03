using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer.Services;
using LucidRAG.Cli.Services;
using LucidRAG.Data;
using Spectre.Console;

namespace LucidRAG.Cli.Commands;

/// <summary>
/// Search indexed documents
/// </summary>
public static class SearchCommand
{
    private static readonly Argument<string> QueryArg = new("query") { Description = "Search query" };
    private static readonly Option<string?> CollectionOpt = new("-c", "--collection") { Description = "Collection to search" };
    private static readonly Option<int> TopKOpt = new("-k", "--top") { Description = "Number of results", DefaultValueFactory = _ => 10 };
    private static readonly Option<bool> JsonOpt = new("--json") { Description = "Output as JSON", DefaultValueFactory = _ => false };
    private static readonly Option<string?> DataDirOpt = new("--data-dir") { Description = "Data directory" };
    private static readonly Option<bool> VerboseOpt = new("-v", "--verbose") { Description = "Verbose output", DefaultValueFactory = _ => false };

    public static Command Create()
    {
        var command = new Command("search", "Search indexed documents");
        command.Arguments.Add(QueryArg);
        command.Options.Add(CollectionOpt);
        command.Options.Add(TopKOpt);
        command.Options.Add(JsonOpt);
        command.Options.Add(DataDirOpt);
        command.Options.Add(VerboseOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var query = parseResult.GetValue(QueryArg)!;
            var collectionName = parseResult.GetValue(CollectionOpt);
            var topK = parseResult.GetValue(TopKOpt);
            var outputJson = parseResult.GetValue(JsonOpt);
            var dataDir = parseResult.GetValue(DataDirOpt);
            var verbose = parseResult.GetValue(VerboseOpt);

            var config = new CliConfig
            {
                DataDirectory = Program.EnsureDataDirectory(dataDir),
                Verbose = verbose
            };

            if (!outputJson)
            {
                AnsiConsole.Write(new FigletText("LucidRAG").Color(Color.Cyan1));
                AnsiConsole.MarkupLine($"[dim]Searching for:[/] {Markup.Escape(query)}\n");
            }

            await using var services = CliServiceRegistration.BuildServiceProvider(config, verbose);
            await CliServiceRegistration.EnsureDatabaseAsync(services);

            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var embedder = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();

            // Check for documents
            var docCount = await db.Documents.CountAsync(d => d.Status == LucidRAG.Entities.DocumentStatus.Completed, ct);
            if (docCount == 0)
            {
                if (outputJson)
                {
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { query, results = Array.Empty<object>(), error = "No documents indexed" }));
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]No documents indexed yet. Use 'lucidrag index' first.[/]");
                }
                return 1;
            }

            // Embed query
            var queryEmbedding = await embedder.EmbedAsync(query, ct);

            // Search
            var segments = await vectorStore.SearchAsync(
                "ragdocuments",
                queryEmbedding,
                topK,
                docId: null,
                ct);

            // Use QuerySimilarity as the score (DuckDB sets this, not RetrievalScore)
            foreach (var s in segments)
                s.RetrievalScore = s.QuerySimilarity;

            if (outputJson)
            {
                var jsonResults = segments.Select((s, i) => new
                {
                    rank = i + 1,
                    score = Math.Round(s.QuerySimilarity, 4),
                    section = s.SectionTitle ?? s.HeadingPath,
                    text = s.Text.Length > 300 ? s.Text[..297] + "..." : s.Text
                });
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { query, results = jsonResults }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                if (segments.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No results found.[/]");
                    return 0;
                }

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .BorderColor(Color.Cyan1)
                    .Title("[cyan]Search Results[/]");

                table.AddColumn(new TableColumn("[cyan]#[/]").RightAligned());
                table.AddColumn(new TableColumn("[cyan]Score[/]").RightAligned());
                table.AddColumn(new TableColumn("[cyan]Section[/]").LeftAligned());
                table.AddColumn(new TableColumn("[cyan]Preview[/]").LeftAligned());

                for (var i = 0; i < segments.Count; i++)
                {
                    var s = segments[i];
                    var preview = s.Text.Length > 80 ? s.Text[..77].Replace("\n", " ") + "..." : s.Text.Replace("\n", " ");
                    var scoreColor = s.QuerySimilarity > 0.7 ? "green" : s.QuerySimilarity > 0.5 ? "yellow" : "white";
                    var section = s.SectionTitle ?? s.HeadingPath ?? "[no section]";

                    table.AddRow(
                        $"{i + 1}",
                        $"[{scoreColor}]{s.QuerySimilarity:F3}[/]",
                        Markup.Escape(section.Length > 30 ? section[..27] + "..." : section),
                        $"[dim]{Markup.Escape(preview)}[/]");
                }

                AnsiConsole.Write(table);
                AnsiConsole.MarkupLine($"\n[dim]Searched {docCount} documents[/]");
            }

            return 0;
        });

        return command;
    }
}
