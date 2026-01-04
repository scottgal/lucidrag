using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Extensions;
using Mostlylucid.DocSummarizer.Services;
using LucidRAG.Cli.Services;
using LucidRAG.Data;
using LucidRAG.Entities;
using Scalar.AspNetCore;
using Serilog;
using Spectre.Console;

namespace LucidRAG.Cli.Commands;

/// <summary>
/// REST API server command with optional interactive CLI
/// </summary>
public static class ServeCommand
{
    public static Command Create()
    {
        var portOpt = new Option<int>("--port", "-p") { Description = "Port to listen on", DefaultValueFactory = _ => 5080 };
        var noBrowserOpt = new Option<bool>("--no-browser") { Description = "Don't open browser automatically" };
        var interactiveOpt = new Option<bool>("--interactive", "-i") { Description = "Enable interactive CLI mode" };
        var dataDirOpt = new Option<string?>("--data-dir") { Description = "Data directory" };
        var verboseOpt = new Option<bool>("-v", "--verbose") { Description = "Verbose output" };

        var command = new Command("serve", "Start REST API server")
        {
            portOpt,
            noBrowserOpt,
            interactiveOpt,
            dataDirOpt,
            verboseOpt
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var port = parseResult.GetValue(portOpt);
            var noBrowser = parseResult.GetValue(noBrowserOpt);
            var interactive = parseResult.GetValue(interactiveOpt);
            var dataDir = parseResult.GetValue(dataDirOpt);
            var verbose = parseResult.GetValue(verboseOpt);

            var cliConfig = new CliConfig
            {
                DataDirectory = Program.EnsureDataDirectory(dataDir),
                Verbose = verbose
            };

            AnsiConsole.Write(new FigletText("LucidRAG").Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[dim]Starting REST API server...[/]\n");

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [$"--urls=http://localhost:{port}"]
            });

            // Serilog - suppress DocSummarizer internal messages unless verbose
            var logLevel = verbose ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information;
            var docSummarizerLogLevel = verbose ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Warning;
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(logLevel)
                // Suppress all DocSummarizer.* namespace logs unless verbose
                .MinimumLevel.Override("Mostlylucid.DocSummarizer", docSummarizerLogLevel)
                .MinimumLevel.Override("Mostlylucid.DocSummarizer.Services", docSummarizerLogLevel)
                .MinimumLevel.Override("Mostlylucid.DocSummarizer.Services.DocSummarizerInitializer", docSummarizerLogLevel)
                .MinimumLevel.Override("Mostlylucid.DocSummarizer.Services.Onnx", docSummarizerLogLevel)
                .WriteTo.Console(outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
            builder.Host.UseSerilog();

            // Database - SQLite
            var dbPath = Path.Combine(cliConfig.DataDirectory, "lucidrag.db");
            builder.Services.AddDbContext<RagDocumentsDbContext>(options =>
                options.UseSqlite($"Data Source={dbPath}"));

            // DocSummarizer.Core with in-memory vectors
            builder.Services.AddDocSummarizer(opt =>
            {
                opt.EmbeddingBackend = EmbeddingBackend.Onnx;
                opt.Onnx.EmbeddingModel = OnnxEmbeddingModel.AllMiniLmL6V2;
                opt.BertRag.VectorStore = VectorStoreBackend.InMemory;
                opt.BertRag.CollectionName = "ragdocuments";
                opt.BertRag.ReindexOnStartup = false;
                opt.Output.Verbose = verbose;
            });

            // CLI services for interactive mode
            builder.Services.AddSingleton(cliConfig);
            builder.Services.AddScoped<CliDocumentProcessor>();

            // OpenAPI
            builder.Services.AddOpenApi();

            var app = builder.Build();

            // Ensure database exists
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
                await db.Database.EnsureCreatedAsync(ct);
            }

            // API documentation
            app.MapOpenApi();
            app.MapScalarApiReference();

            // Health check
            app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }))
                .WithName("Health")
                .WithOpenApi();

            // Documents API
            app.MapGet("/api/documents", async (RagDocumentsDbContext db, Guid? collectionId, CancellationToken ct) =>
            {
                var query = db.Documents.Include(d => d.Collection).AsQueryable();
                if (collectionId.HasValue)
                    query = query.Where(d => d.CollectionId == collectionId);

                // SQLite doesn't support DateTimeOffset in ORDER BY - do it client-side
                var docs = (await query.ToListAsync(ct)).OrderByDescending(d => d.CreatedAt).ToList();
                return Results.Ok(docs.Select(d => new
                {
                    d.Id,
                    d.Name,
                    d.OriginalFilename,
                    Status = d.Status.ToString().ToLowerInvariant(),
                    d.SegmentCount,
                    d.FileSizeBytes,
                    d.CreatedAt,
                    d.ProcessedAt,
                    CollectionId = d.CollectionId,
                    CollectionName = d.Collection?.Name
                }));
            }).WithName("ListDocuments").WithOpenApi();

            app.MapGet("/api/documents/{id:guid}", async (Guid id, RagDocumentsDbContext db, CancellationToken ct) =>
            {
                var doc = await db.Documents.Include(d => d.Collection).FirstOrDefaultAsync(d => d.Id == id, ct);
                if (doc == null)
                    return Results.NotFound(new { error = "Document not found" });

                return Results.Ok(new
                {
                    doc.Id,
                    doc.Name,
                    doc.OriginalFilename,
                    Status = doc.Status.ToString().ToLowerInvariant(),
                    doc.StatusMessage,
                    doc.SegmentCount,
                    doc.EntityCount,
                    doc.FileSizeBytes,
                    doc.MimeType,
                    doc.CreatedAt,
                    doc.ProcessedAt,
                    CollectionId = doc.CollectionId,
                    CollectionName = doc.Collection?.Name
                });
            }).WithName("GetDocument").WithOpenApi();

            // Collections API
            app.MapGet("/api/collections", async (RagDocumentsDbContext db, CancellationToken ct) =>
            {
                var collections = await db.Collections
                    .Include(c => c.Documents)
                    .OrderBy(c => c.Name)
                    .ToListAsync(ct);

                return Results.Ok(collections.Select(c => new
                {
                    c.Id,
                    c.Name,
                    c.Description,
                    DocumentCount = c.Documents.Count,
                    SegmentCount = c.Documents.Where(d => d.Status == DocumentStatus.Completed).Sum(d => d.SegmentCount),
                    c.CreatedAt
                }));
            }).WithName("ListCollections").WithOpenApi();

            app.MapPost("/api/collections", async (HttpContext context, RagDocumentsDbContext db, CancellationToken ct) =>
            {
                var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
                var name = json.RootElement.GetProperty("name").GetString();
                var description = json.RootElement.TryGetProperty("description", out var desc) ? desc.GetString() : null;

                if (string.IsNullOrWhiteSpace(name))
                    return Results.BadRequest(new { error = "Name is required" });

                var existing = await db.Collections.FirstOrDefaultAsync(c => c.Name == name, ct);
                if (existing != null)
                    return Results.Conflict(new { error = "Collection already exists" });

                var collection = new CollectionEntity
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    Description = description
                };

                db.Collections.Add(collection);
                await db.SaveChangesAsync(ct);

                return Results.Created($"/api/collections/{collection.Id}", new { collection.Id, collection.Name });
            }).WithName("CreateCollection").WithOpenApi();

            // Search API
            app.MapPost("/api/search", async (HttpContext context, IVectorStore vectorStore, IEmbeddingService embedder, RagDocumentsDbContext db, CancellationToken ct) =>
            {
                var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
                var query = json.RootElement.GetProperty("query").GetString();
                var topK = json.RootElement.TryGetProperty("topK", out var k) ? k.GetInt32() : 10;

                if (string.IsNullOrWhiteSpace(query))
                    return Results.BadRequest(new { error = "Query is required" });

                var docCount = await db.Documents.CountAsync(d => d.Status == DocumentStatus.Completed, ct);
                if (docCount == 0)
                    return Results.Ok(new { query, results = Array.Empty<object>(), message = "No documents indexed" });

                var queryEmbedding = await embedder.EmbedAsync(query, ct);
                var segments = await vectorStore.SearchAsync("ragdocuments", queryEmbedding, topK, docId: null, ct);

                var results = segments.Select((s, i) => new
                {
                    rank = i + 1,
                    score = Math.Round(s.QuerySimilarity, 4),
                    section = s.SectionTitle ?? s.HeadingPath,
                    text = s.Text
                });

                return Results.Ok(new { query, results, documentCount = docCount });
            }).WithName("Search").WithOpenApi();

            // Stats API
            app.MapGet("/api/stats", async (RagDocumentsDbContext db, CancellationToken ct) =>
            {
                var docs = await db.Documents.ToListAsync(ct);
                var collections = await db.Collections.CountAsync(ct);

                return Results.Ok(new
                {
                    documents = new
                    {
                        total = docs.Count,
                        completed = docs.Count(d => d.Status == DocumentStatus.Completed),
                        failed = docs.Count(d => d.Status == DocumentStatus.Failed),
                        processing = docs.Count(d => d.Status == DocumentStatus.Processing)
                    },
                    segments = docs.Where(d => d.Status == DocumentStatus.Completed).Sum(d => d.SegmentCount),
                    collections,
                    totalSizeBytes = docs.Sum(d => d.FileSizeBytes)
                });
            }).WithName("GetStats").WithOpenApi();

            var url = $"http://localhost:{port}";
            var docsUrl = $"{url}/scalar";
            var healthUrl = $"{url}/healthz";
            AnsiConsole.WriteLine("╔════════════════════════════════════════════════════════╗");
            AnsiConsole.WriteLine("║              LucidRAG REST API Server                  ║");
            AnsiConsole.WriteLine("╠════════════════════════════════════════════════════════╣");
            AnsiConsole.WriteLine($"║  API:    {url,-47}║");
            AnsiConsole.WriteLine($"║  Docs:   {docsUrl,-47}║");
            AnsiConsole.WriteLine($"║  Health: {healthUrl,-47}║");
            if (interactive)
            {
                AnsiConsole.WriteLine("║  Mode:   Interactive CLI enabled                       ║");
            }
            AnsiConsole.WriteLine("║  Press Ctrl+C to stop                                  ║");
            AnsiConsole.WriteLine("╚════════════════════════════════════════════════════════╝");
            AnsiConsole.WriteLine();

            if (!noBrowser)
            {
                try
                {
                    OpenBrowser(docsUrl);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[dim]Could not open browser: {Markup.Escape(ex.Message)}[/]");
                }
            }

            if (interactive)
            {
                // Start server in background
                var serverTask = app.RunAsync(ct);

                // Run interactive CLI loop
                await RunInteractiveLoopAsync(app.Services, cliConfig, ct);

                // Wait for server to complete (if Ctrl+C pressed)
                await serverTask;
            }
            else
            {
                await app.RunAsync(ct);
            }

            return 0;
        });

        return command;
    }

    private static async Task RunInteractiveLoopAsync(IServiceProvider services, CliConfig config, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("\n[cyan]Interactive mode enabled. Type /help for commands.[/]\n");

        while (!ct.IsCancellationRequested)
        {
            AnsiConsole.Markup("[cyan]lucidrag>[/] ");
            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input))
                continue;

            if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("/quit", StringComparison.OrdinalIgnoreCase))
                break;

            try
            {
                await ProcessInteractiveCommandAsync(services, config, input, ct);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
            }

            AnsiConsole.WriteLine();
        }
    }

    private static async Task ProcessInteractiveCommandAsync(IServiceProvider services, CliConfig config, string input, CancellationToken ct)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        var args = parts.Length > 1 ? parts[1] : "";

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
        var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        var embedder = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();

        switch (command)
        {
            case "/help":
                ShowInteractiveHelp();
                break;

            case "/add":
                await AddDocumentAsync(scope.ServiceProvider, config, args, ct);
                break;

            case "/search":
            case "/s":
                await SearchAsync(vectorStore, embedder, db, args, ct);
                break;

            case "/list":
            case "/ls":
                await ListDocumentsAsync(db, ct);
                break;

            case "/collections":
            case "/c":
                await ListCollectionsAsync(db, ct);
                break;

            case "/stats":
                await ShowStatsAsync(db, ct);
                break;

            case "/delete":
            case "/del":
                await DeleteDocumentAsync(db, args, ct);
                break;

            default:
                // If it doesn't start with /, treat as search query
                if (!input.StartsWith('/'))
                {
                    await SearchAsync(vectorStore, embedder, db, input, ct);
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]Unknown command: {Markup.Escape(command)}. Type /help for commands.[/]");
                }
                break;
        }
    }

    private static void ShowInteractiveHelp()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .Title("[cyan]Interactive Commands[/]");

        table.AddColumn(new TableColumn("[cyan]Command[/]").LeftAligned());
        table.AddColumn(new TableColumn("[cyan]Description[/]").LeftAligned());

        table.AddRow("/add <file|folder>", "Add and index a file or folder");
        table.AddRow("/search <query>", "Search indexed documents");
        table.AddRow("/s <query>", "Shorthand for /search");
        table.AddRow("/list, /ls", "List all documents");
        table.AddRow("/collections, /c", "List all collections");
        table.AddRow("/stats", "Show index statistics");
        table.AddRow("/delete <id>", "Delete a document by ID");
        table.AddRow("/exit, /quit", "Exit interactive mode");
        table.AddRow("<query>", "Search (no command prefix)");

        AnsiConsole.Write(table);
    }

    private static async Task AddDocumentAsync(IServiceProvider services, CliConfig config, string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            AnsiConsole.MarkupLine("[yellow]Usage: /add <file|folder>[/]");
            return;
        }

        var fullPath = Path.GetFullPath(path);

        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            AnsiConsole.MarkupLine($"[red]Path not found: {Markup.Escape(path)}[/]");
            return;
        }

        var processor = services.GetRequiredService<CliDocumentProcessor>();

        if (Directory.Exists(fullPath))
        {
            AnsiConsole.MarkupLine($"[cyan]Indexing directory:[/] {Markup.Escape(fullPath)}");
            var results = await processor.IndexDirectoryAsync(fullPath, null, recursive: true, ct);
            var success = results.Count(r => r.Success);
            var failed = results.Count(r => !r.Success);
            AnsiConsole.MarkupLine($"[green]Indexed {success} files, {failed} failed[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[cyan]Indexing file:[/] {Markup.Escape(fullPath)}");
            var result = await processor.IndexFileAsync(fullPath, null, ct);
            if (result.Success)
                AnsiConsole.MarkupLine($"[green]✓ Indexed {result.SegmentCount} segments[/]");
            else
                AnsiConsole.MarkupLine($"[red]✗ {result.Message}[/]");
        }
    }

    private static async Task SearchAsync(IVectorStore vectorStore, IEmbeddingService embedder, RagDocumentsDbContext db, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            AnsiConsole.MarkupLine("[yellow]Usage: /search <query>[/]");
            return;
        }

        var docCount = await db.Documents.CountAsync(d => d.Status == DocumentStatus.Completed, ct);
        if (docCount == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No documents indexed. Use /add to index files.[/]");
            return;
        }

        var queryEmbedding = await embedder.EmbedAsync(query, ct);
        var segments = await vectorStore.SearchAsync("ragdocuments", queryEmbedding, 5, docId: null, ct);

        if (segments.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No results found.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1);

        table.AddColumn(new TableColumn("[cyan]#[/]").RightAligned());
        table.AddColumn(new TableColumn("[cyan]Score[/]").RightAligned());
        table.AddColumn(new TableColumn("[cyan]Section[/]").LeftAligned());
        table.AddColumn(new TableColumn("[cyan]Preview[/]").LeftAligned());

        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            var preview = s.Text.Length > 60 ? s.Text[..57].Replace("\n", " ") + "..." : s.Text.Replace("\n", " ");
            var scoreColor = s.QuerySimilarity > 0.7 ? "green" : s.QuerySimilarity > 0.5 ? "yellow" : "white";
            var section = (s.SectionTitle ?? s.HeadingPath ?? "Document");
            section = section.Length > 25 ? section[..22] + "..." : section;

            table.AddRow(
                $"{i + 1}",
                $"[{scoreColor}]{s.QuerySimilarity:F3}[/]",
                Markup.Escape(section),
                $"[dim]{Markup.Escape(preview)}[/]");
        }

        AnsiConsole.Write(table);
    }

    private static async Task ListDocumentsAsync(RagDocumentsDbContext db, CancellationToken ct)
    {
        // SQLite doesn't support DateTimeOffset in ORDER BY - do it client-side
        var docs = (await db.Documents
            .Include(d => d.Collection)
            .ToListAsync(ct))
            .OrderByDescending(d => d.CreatedAt)
            .Take(20)
            .ToList();

        if (docs.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No documents found.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .Title("[cyan]Documents[/]");

        table.AddColumn(new TableColumn("[cyan]ID[/]").LeftAligned());
        table.AddColumn(new TableColumn("[cyan]Name[/]").LeftAligned());
        table.AddColumn(new TableColumn("[cyan]Status[/]").Centered());
        table.AddColumn(new TableColumn("[cyan]Segments[/]").RightAligned());

        foreach (var doc in docs)
        {
            var statusColor = doc.Status switch
            {
                DocumentStatus.Completed => "green",
                DocumentStatus.Failed => "red",
                _ => "yellow"
            };

            table.AddRow(
                doc.Id.ToString()[..8],
                Markup.Escape(doc.Name.Length > 30 ? doc.Name[..27] + "..." : doc.Name),
                $"[{statusColor}]{doc.Status}[/]",
                $"{doc.SegmentCount}");
        }

        AnsiConsole.Write(table);
    }

    private static async Task ListCollectionsAsync(RagDocumentsDbContext db, CancellationToken ct)
    {
        var collections = await db.Collections
            .Include(c => c.Documents)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        if (collections.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No collections found.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .Title("[cyan]Collections[/]");

        table.AddColumn(new TableColumn("[cyan]Name[/]").LeftAligned());
        table.AddColumn(new TableColumn("[cyan]Documents[/]").RightAligned());
        table.AddColumn(new TableColumn("[cyan]Segments[/]").RightAligned());

        foreach (var c in collections)
        {
            table.AddRow(
                Markup.Escape(c.Name),
                $"{c.Documents.Count}",
                $"{c.Documents.Where(d => d.Status == DocumentStatus.Completed).Sum(d => d.SegmentCount)}");
        }

        AnsiConsole.Write(table);
    }

    private static async Task ShowStatsAsync(RagDocumentsDbContext db, CancellationToken ct)
    {
        var docs = await db.Documents.ToListAsync(ct);
        var collections = await db.Collections.CountAsync(ct);

        var completed = docs.Count(d => d.Status == DocumentStatus.Completed);
        var segments = docs.Where(d => d.Status == DocumentStatus.Completed).Sum(d => d.SegmentCount);

        AnsiConsole.MarkupLine($"[cyan]Documents:[/] {docs.Count} total, {completed} indexed");
        AnsiConsole.MarkupLine($"[cyan]Segments:[/]  {segments}");
        AnsiConsole.MarkupLine($"[cyan]Collections:[/] {collections}");
    }

    private static async Task DeleteDocumentAsync(RagDocumentsDbContext db, string idStr, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idStr) || !Guid.TryParse(idStr, out var id))
        {
            AnsiConsole.MarkupLine("[yellow]Usage: /delete <document-id>[/]");
            return;
        }

        var doc = await db.Documents.FindAsync([id], ct);
        if (doc == null)
        {
            AnsiConsole.MarkupLine($"[red]Document not found: {idStr}[/]");
            return;
        }

        // Delete file
        if (!string.IsNullOrEmpty(doc.FilePath))
        {
            var dir = Path.GetDirectoryName(doc.FilePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        db.Documents.Remove(doc);
        await db.SaveChangesAsync(ct);

        AnsiConsole.MarkupLine($"[green]✓ Deleted: {Markup.Escape(doc.Name)}[/]");
    }

    private static void OpenBrowser(string url)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Process.Start("xdg-open", url);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start("open", url);
        }
    }
}
