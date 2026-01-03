using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LucidRAG.Cli.Services;
using LucidRAG.Data;
using Spectre.Console;

namespace LucidRAG.Cli.Commands;

/// <summary>
/// Check service availability and status
/// </summary>
public static class CheckCommand
{
    private static readonly Option<string?> DataDirOpt = new("--data-dir") { Description = "Data directory" };
    private static readonly Option<bool> VerboseOpt = new("-v", "--verbose") { Description = "Verbose output", DefaultValueFactory = _ => false };

    public static Command Create()
    {
        var command = new Command("check", "Check service availability and status");
        command.Options.Add(DataDirOpt);
        command.Options.Add(VerboseOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var dataDir = parseResult.GetValue(DataDirOpt);
            var verbose = parseResult.GetValue(VerboseOpt);

            var config = new CliConfig
            {
                DataDirectory = Program.EnsureDataDirectory(dataDir),
                Verbose = verbose
            };

            AnsiConsole.Write(new FigletText("LucidRAG").Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[dim]Checking service status...[/]\n");

            // Status table
            var statusTable = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Blue)
                .Title("[cyan]Service Status[/]");

            statusTable.AddColumn(new TableColumn("[blue]Service[/]").LeftAligned());
            statusTable.AddColumn(new TableColumn("[blue]Status[/]").Centered());
            statusTable.AddColumn(new TableColumn("[blue]Details[/]").LeftAligned());

            // Check data directory
            var dbPath = Path.Combine(config.DataDirectory, "lucidrag.db");
            var vectorPath = Path.Combine(config.DataDirectory, "vectors.duckdb");
            var dbExists = File.Exists(dbPath);
            var vectorsExist = File.Exists(vectorPath);

            statusTable.AddRow(
                "[cyan]Data Directory[/]",
                "[green]OK[/]",
                config.DataDirectory);

            statusTable.AddRow(
                "[cyan]SQLite Database[/]",
                dbExists ? "[green]OK[/]" : "[yellow]NEW[/]",
                dbExists ? $"{new FileInfo(dbPath).Length / 1024:N0} KB" : "Will be created");

            statusTable.AddRow(
                "[cyan]DuckDB Vectors[/]",
                vectorsExist ? "[green]OK[/]" : "[yellow]NEW[/]",
                vectorsExist ? $"{new FileInfo(vectorPath).Length / 1024:N0} KB" : "Will be created");

            statusTable.AddRow(
                "[cyan]ONNX Embeddings[/]",
                "[green]OK[/]",
                "Built-in (AllMiniLmL6V2)");

            // Check Ollama
            var ollamaStatus = "[yellow]Optional[/]";
            var ollamaDetails = "Not configured";
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var response = await http.GetAsync($"{config.OllamaUrl}/api/tags", ct);
                if (response.IsSuccessStatusCode)
                {
                    ollamaStatus = "[green]OK[/]";
                    var json = await response.Content.ReadAsStringAsync(ct);
                    var models = System.Text.Json.JsonDocument.Parse(json);
                    var modelCount = models.RootElement.GetProperty("models").GetArrayLength();
                    ollamaDetails = $"{modelCount} models available";
                }
            }
            catch
            {
                ollamaDetails = "Not running (chat features disabled)";
            }

            statusTable.AddRow("[cyan]Ollama[/]", ollamaStatus, ollamaDetails);

            AnsiConsole.Write(statusTable);
            AnsiConsole.WriteLine();

            // Stats table
            if (dbExists)
            {
                await using var services = CliServiceRegistration.BuildServiceProvider(config, false);
                await CliServiceRegistration.EnsureDatabaseAsync(services);

                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

                var docCount = await db.Documents.CountAsync(ct);
                var completedCount = await db.Documents.CountAsync(d => d.Status == LucidRAG.Entities.DocumentStatus.Completed, ct);
                var segmentCount = await db.Documents.Where(d => d.Status == LucidRAG.Entities.DocumentStatus.Completed).SumAsync(d => d.SegmentCount, ct);
                var collectionCount = await db.Collections.CountAsync(ct);

                var statsTable = new Table()
                    .Border(TableBorder.Rounded)
                    .BorderColor(Color.Green)
                    .Title("[green]Index Statistics[/]");

                statsTable.AddColumn(new TableColumn("[green]Metric[/]").LeftAligned());
                statsTable.AddColumn(new TableColumn("[green]Value[/]").RightAligned());

                statsTable.AddRow("Documents", $"{docCount}");
                statsTable.AddRow("Indexed", $"{completedCount}");
                statsTable.AddRow("Segments", $"{segmentCount}");
                statsTable.AddRow("Collections", $"{collectionCount}");

                AnsiConsole.Write(statsTable);
                AnsiConsole.WriteLine();
            }

            // Features table
            var featuresTable = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Cyan1)
                .Title("[cyan]Available Features[/]");

            featuresTable.AddColumn(new TableColumn("[cyan]Feature[/]").LeftAligned());
            featuresTable.AddColumn(new TableColumn("[cyan]Status[/]").Centered());
            featuresTable.AddColumn(new TableColumn("[cyan]Requires[/]").LeftAligned());

            featuresTable.AddRow("File indexing", "[green]✓[/]", "ONNX (built-in)");
            featuresTable.AddRow("Semantic search", "[green]✓[/]", "DuckDB (built-in)");
            featuresTable.AddRow("PDF/DOCX support", "[green]✓[/]", "DocSummarizer.Core");
            featuresTable.AddRow("Chat/Q&A", ollamaStatus.Contains("OK") ? "[green]✓[/]" : "[yellow]○[/]", "Ollama");
            featuresTable.AddRow("REST API", "[green]✓[/]", "lucidrag serve");

            AnsiConsole.Write(featuresTable);
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine("[green]Ready to use![/]");
            return 0;
        });

        return command;
    }
}
