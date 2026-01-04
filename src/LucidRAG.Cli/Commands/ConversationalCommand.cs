using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using LucidRAG.Cli.Services;
using LucidRAG.Data;
using Spectre.Console;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Config;

namespace LucidRAG.Cli.Commands;

/// <summary>
/// Interactive conversational RAG interface with slash commands
/// </summary>
public static class ConversationalCommand
{
    public static Command Create()
    {
        var command = new Command("chat", "Interactive conversational RAG interface");

        var dataDirOpt = new Option<string?>("--data-dir") { Description = "Data directory" };
        var verboseOpt = new Option<bool>("-v", "--verbose") { Description = "Verbose output", DefaultValueFactory = _ => false };

        command.Options.Add(dataDirOpt);
        command.Options.Add(verboseOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var dataDir = parseResult.GetValue(dataDirOpt);
            var verbose = parseResult.GetValue(verboseOpt);

            var config = new CliConfig
            {
                DataDirectory = Program.EnsureDataDirectory(dataDir),
                Verbose = verbose
            };

            await RunConversationalModeAsync(config, verbose, ct);
            return 0;
        });

        return command;
    }

    public static async Task RunConversationalModeAsync(CliConfig config, bool verbose, CancellationToken ct)
    {
        // Show banner
        AnsiConsole.Clear();
        AnsiConsole.Write(new FigletText("LucidRAG").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[dim]Conversational RAG with intelligent query understanding[/]");
        AnsiConsole.WriteLine();

        // Build services
        await using var services = CliServiceRegistration.BuildServiceProvider(config, verbose);
        await CliServiceRegistration.EnsureDatabaseAsync(services);

        // Detect available services
        using var scope = services.CreateScope();
        var docSummarizerConfig = scope.ServiceProvider.GetRequiredService<DocSummarizerConfig>();

        AnsiConsole.MarkupLine("[cyan]Detecting services...[/]");
        var detectedServices = await ServiceDetector.DetectSilentAsync(docSummarizerConfig);

        DisplayServiceStatus(detectedServices);
        AnsiConsole.WriteLine();

        // Check if we have the minimum required for conversational mode
        if (!detectedServices.OllamaAvailable)
        {
            AnsiConsole.MarkupLine("[yellow]⚠[/]  Ollama not detected - conversational mode requires an LLM");
            AnsiConsole.MarkupLine("[dim]Start Ollama with: [green]ollama serve[/][/]");
            AnsiConsole.MarkupLine("[dim]Or use non-interactive commands: [green]lucidrag-cli index|search[/][/]");
            return;
        }

        // Show capabilities
        AnsiConsole.MarkupLine("[green]✓[/] Ready for conversational queries");
        ShowHelp();
        AnsiConsole.WriteLine();

        // Get services
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
        var processor = scope.ServiceProvider.GetRequiredService<CliDocumentProcessor>();
        var ollama = scope.ServiceProvider.GetRequiredService<OllamaService>();

        // REPL loop
        var session = new ConversationSession(db, processor, ollama, detectedServices, verbose);

        while (!ct.IsCancellationRequested)
        {
            // Prompt
            var input = AnsiConsole.Ask<string>("[cyan]>[/]");

            if (string.IsNullOrWhiteSpace(input))
                continue;

            // Handle exit commands
            if (input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                input.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                input.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase) ||
                input.Trim().Equals("/quit", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[dim]Goodbye![/]");
                break;
            }

            try
            {
                await session.ProcessInputAsync(input, ct);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
                if (verbose)
                {
                    AnsiConsole.MarkupLine($"[dim]{ex.StackTrace}[/]");
                }
            }

            AnsiConsole.WriteLine();
        }
    }

    private static void DisplayServiceStatus(DetectedServices services)
    {
        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("[bold]Service[/]").Centered());
        table.AddColumn(new TableColumn("[bold]Status[/]").Centered());
        table.AddColumn(new TableColumn("[bold]Details[/]"));

        // Ollama
        if (services.OllamaAvailable)
        {
            table.AddRow("Ollama", "[green]✓ Available[/]",
                $"Model: {services.OllamaModel ?? "default"}");
        }
        else
        {
            table.AddRow("Ollama", "[red]✗ Unavailable[/]",
                "[dim]Required for conversational mode[/]");
        }

        // ONNX Embeddings
        table.AddRow("ONNX Embeddings", "[green]✓ Available[/]",
            "[dim]Built-in, zero-config[/]");

        // Docling (optional)
        if (services.DoclingAvailable)
        {
            var gpuInfo = services.DoclingHasGpu ? $"GPU ({services.DoclingAccelerator})" : "CPU";
            table.AddRow("Docling", "[green]✓ Available[/]",
                $"PDF/DOCX support ({gpuInfo})");
        }
        else
        {
            table.AddRow("Docling", "[yellow]○ Optional[/]",
                "[dim]Markdown/text only[/]");
        }

        // Qdrant (optional)
        if (services.QdrantAvailable)
        {
            table.AddRow("Qdrant", "[green]✓ Available[/]",
                "[dim]Persistent vector storage[/]");
        }
        else
        {
            table.AddRow("Qdrant", "[yellow]○ Optional[/]",
                "[dim]Using in-memory vectors[/]");
        }

        AnsiConsole.Write(table);
    }

    public static void ShowHelp()
    {
        var helpPanel = new Panel(
            new Markup(
                "[cyan]Commands:[/]\n" +
                "  [green]/index <glob>[/]     Index files matching pattern (e.g., /index docs/**/*.md)\n" +
                "  [green]/describe <glob>[/]  Generate descriptions for images\n" +
                "  [green]/search <query>[/]   Search indexed documents\n" +
                "  [green]/collections[/]      List available collections\n" +
                "  [green]/stats[/]            Show database statistics\n" +
                "  [green]/help[/]             Show this help\n" +
                "  [green]/clear[/]            Clear screen\n" +
                "  [green]/exit[/]             Exit\n\n" +
                "[cyan]Natural queries:[/]\n" +
                "  Just type your question and I'll search for relevant information!"
            ));
        helpPanel.Header = new PanelHeader(" Quick Help ", Justify.Center);
        helpPanel.Padding = new Padding(1, 0, 1, 0);

        AnsiConsole.Write(helpPanel);
    }
}

/// <summary>
/// Manages conversational session state and command routing
/// </summary>
internal class ConversationSession
{
    private readonly RagDocumentsDbContext _db;
    private readonly CliDocumentProcessor _processor;
    private readonly OllamaService _ollama;
    private readonly DetectedServices _services;
    private readonly bool _verbose;
    private readonly List<(string role, string content)> _conversationHistory = new();

    public ConversationSession(
        RagDocumentsDbContext db,
        CliDocumentProcessor processor,
        OllamaService ollama,
        DetectedServices services,
        bool verbose)
    {
        _db = db;
        _processor = processor;
        _ollama = ollama;
        _services = services;
        _verbose = verbose;
    }

    public async Task ProcessInputAsync(string input, CancellationToken ct)
    {
        var trimmed = input.Trim();

        // Slash commands
        if (trimmed.StartsWith('/'))
        {
            await ProcessSlashCommandAsync(trimmed, ct);
            return;
        }

        // Natural language query - use sentinel LLM for spell-check and clarification
        await ProcessNaturalQueryAsync(trimmed, ct);
    }

    private async Task ProcessSlashCommandAsync(string command, CancellationToken ct)
    {
        var parts = command.Split(' ', 2, StringSplitOptions.TrimEntries);
        var cmd = parts[0].ToLowerInvariant();
        var args = parts.Length > 1 ? parts[1] : "";

        switch (cmd)
        {
            case "/help":
            case "/h":
            case "/?":
                ConversationalCommand.ShowHelp();
                break;

            case "/clear":
            case "/cls":
                AnsiConsole.Clear();
                AnsiConsole.Write(new FigletText("LucidRAG").Color(Color.Cyan1));
                AnsiConsole.WriteLine();
                break;

            case "/index":
                await ProcessIndexCommandAsync(args, ct);
                break;

            case "/describe":
                await ProcessDescribeCommandAsync(args, ct);
                break;

            case "/search":
                await ProcessSearchCommandAsync(args, ct);
                break;

            case "/collections":
                await ProcessCollectionsCommandAsync(ct);
                break;

            case "/stats":
                await ProcessStatsCommandAsync(ct);
                break;

            default:
                AnsiConsole.MarkupLine($"[red]Unknown command:[/] {cmd}");
                AnsiConsole.MarkupLine("[dim]Type /help for available commands[/]");
                break;
        }
    }

    private async Task ProcessIndexCommandAsync(string glob, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(glob))
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] /index <glob>");
            AnsiConsole.MarkupLine("[dim]Example: /index docs/**/*.md[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[cyan]Indexing files matching:[/] {glob}");

        // Expand glob pattern
        var files = await Task.Run(() =>
        {
            var matcher = new Microsoft.Extensions.FileSystemGlobbing.Matcher();
            matcher.AddInclude(glob);
            var result = matcher.Execute(new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(
                new DirectoryInfo(Directory.GetCurrentDirectory())));
            return result.Files.Select(f => Path.Combine(Directory.GetCurrentDirectory(), f.Path)).ToList();
        }, ct);

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No files matched pattern[/]");
            return;
        }

        // Confirm
        var confirm = AnsiConsole.Confirm($"Index {files.Count} file(s)?", defaultValue: true);
        if (!confirm)
        {
            AnsiConsole.MarkupLine("[dim]Cancelled[/]");
            return;
        }

        // Process files
        var success = 0;
        var failed = 0;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
            })
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[cyan]Indexing files...[/]", maxValue: files.Count);

                foreach (var file in files)
                {
                    task.Description = $"[cyan]Processing:[/] {Path.GetFileName(file)}";

                    var result = await _processor.IndexFileAsync(file, collectionId: null, ct);
                    if (result.Success)
                        success++;
                    else
                        failed++;

                    task.Increment(1);
                }
            });

        AnsiConsole.MarkupLine($"[green]✓[/] Indexed {success} files");
        if (failed > 0)
            AnsiConsole.MarkupLine($"[red]✗[/] Failed: {failed}");
    }

    private async Task ProcessDescribeCommandAsync(string glob, CancellationToken ct)
    {
        AnsiConsole.MarkupLine($"[cyan]Describing images:[/] {glob}");
        AnsiConsole.MarkupLine("[yellow]Image description not yet implemented[/]");
        await Task.CompletedTask;
    }

    private async Task ProcessSearchCommandAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] /search <query>");
            return;
        }

        AnsiConsole.MarkupLine($"[cyan]Searching for:[/] {query}");
        AnsiConsole.MarkupLine("[yellow]Search not yet implemented in conversational mode[/]");
        await Task.CompletedTask;
    }

    private async Task ProcessCollectionsCommandAsync(CancellationToken ct)
    {
        var collections = await _db.Collections.ToListAsync(ct);

        if (collections.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No collections found[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Collection")
            .AddColumn("Created");

        foreach (var collection in collections)
        {
            table.AddRow(collection.Name, collection.CreatedAt.ToString("g"));
        }

        AnsiConsole.Write(table);
    }

    private async Task ProcessStatsCommandAsync(CancellationToken ct)
    {
        var docCount = await _db.Documents.CountAsync(ct);
        var collectionCount = await _db.Collections.CountAsync(ct);
        var entityCount = await _db.Entities.CountAsync(ct);
        var relationshipCount = await _db.EntityRelationships.CountAsync(ct);

        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.AddColumn("Metric");
        table.AddColumn("Count");

        table.AddRow("Documents", docCount.ToString());
        table.AddRow("Collections", collectionCount.ToString());
        table.AddRow("Entities (GraphRAG)", entityCount.ToString());
        table.AddRow("Relationships", relationshipCount.ToString());

        AnsiConsole.Write(table);
    }

    private async Task ProcessNaturalQueryAsync(string query, CancellationToken ct)
    {
        // Step 1: Spell-check and clarify query using sentinel LLM
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("[dim]Understanding query...[/]", ctx =>
            {
                // Simulated thinking
                Thread.Sleep(100);
            });

        var clarifiedQuery = await ClarifyQueryWithLlmAsync(query, ct);

        if (!string.Equals(query, clarifiedQuery, StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine($"[yellow]Corrected:[/] {Markup.Escape(clarifiedQuery)}");
        }

        // Step 1.5: Validate query doesn't attempt config-only operations
        var validation = ValidateQuerySafety(clarifiedQuery);
        if (!validation.IsValid)
        {
            AnsiConsole.MarkupLine($"[red]Security:[/] {Markup.Escape(validation.Reason)}");
            AnsiConsole.MarkupLine("[yellow]Tip:[/] Configuration settings can only be changed via appsettings.json or CLI arguments, not via natural language prompts.");
            return;
        }

        // Step 2: Decompose query into actionable steps
        AnsiConsole.MarkupLine("[dim]Planning actions...[/]");
        var plan = await DecomposeQueryIntoStepsAsync(clarifiedQuery, ct);

        if (plan.Steps.Count > 0)
        {
            // Show the plan
            var planPanel = new Panel(
                string.Join("\n", plan.Steps.Select((s, i) => $"[cyan]{i + 1}.[/] {Markup.Escape(s)}"))
            );
            planPanel.Header = new PanelHeader(" Execution Plan ", Justify.Left);
            planPanel.Padding = new Padding(1, 0, 1, 0);

            AnsiConsole.Write(planPanel);

            var confirm = AnsiConsole.Confirm("Execute this plan?", defaultValue: true);
            if (!confirm)
            {
                AnsiConsole.MarkupLine("[dim]Cancelled[/]");
                return;
            }

            // Execute each step
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                AnsiConsole.MarkupLine($"\n[cyan]Step {i + 1}/{plan.Steps.Count}:[/] {Markup.Escape(plan.Steps[i])}");
                await ExecutePlanStepAsync(plan.Steps[i], plan.StepCommands[i], ct);
            }
        }
        else
        {
            // Simple search query
            AnsiConsole.MarkupLine($"[cyan]Searching for:[/] {Markup.Escape(clarifiedQuery)}");
            AnsiConsole.MarkupLine("[yellow]Natural query search not yet implemented[/]");
        }

        // Add to conversation history
        _conversationHistory.Add(("user", query));
        _conversationHistory.Add(("assistant", "Query processed"));
    }

    private async Task ExecutePlanStepAsync(string stepDescription, string command, CancellationToken ct)
    {
        // Execute the decomposed command
        if (command.StartsWith('/'))
        {
            await ProcessSlashCommandAsync(command, ct);
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Step execution: {Markup.Escape(command)}[/]");
        }
    }

    private record QueryPlan(List<string> Steps, List<string> StepCommands);

    private async Task<QueryPlan> DecomposeQueryIntoStepsAsync(string query, CancellationToken ct)
    {
        var prompt = @$"You are a query decomposition assistant for a document RAG system.

Task: Break down the user's query into a sequence of actionable steps using slash commands.

Available commands:
- /index <glob> - Index files matching glob pattern
- /describe <glob> - Generate descriptions for images
- /search <query> - Search indexed documents

User query: ""{query}""

Examples:
Input: ""Show me all the text in the gifs in this directory F:\Gifs""
Output:
1. Index all GIF files in F:\Gifs directory | /index F:\Gifs\**\*.gif
2. Search for extracted text from GIFs | /search ""text from gif files""

Input: ""Index the markdown files in docs and search for authentication""
Output:
1. Index markdown files | /index docs\**\*.md
2. Search for authentication | /search authentication

Now decompose this query into steps. Return format:
Step number. Description | /command

If the query is a simple search, return empty (no steps needed).";

        try
        {
            var response = await _ollama.GenerateAsync(
                prompt,
                temperature: 0.2,
                cancellationToken: ct
            );

            var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var steps = new List<string>();
            var commands = new List<string>();

            foreach (var line in lines)
            {
                // Parse format: "1. Description | /command"
                var parts = line.Split('|', 2);
                if (parts.Length == 2)
                {
                    var desc = parts[0].Trim();
                    // Remove leading number and dot
                    if (desc.Length > 0 && char.IsDigit(desc[0]))
                    {
                        var dotIndex = desc.IndexOf('.');
                        if (dotIndex > 0 && dotIndex < desc.Length - 1)
                        {
                            desc = desc.Substring(dotIndex + 1).Trim();
                        }
                    }

                    steps.Add(desc);
                    commands.Add(parts[1].Trim());
                }
            }

            return new QueryPlan(steps, commands);
        }
        catch
        {
            // If LLM fails, return empty plan (fallback to simple search)
            return new QueryPlan(new List<string>(), new List<string>());
        }
    }

    private async Task<string> ClarifyQueryWithLlmAsync(string query, CancellationToken ct)
    {
        var prompt = @$"You are a query clarification assistant for a document search system.

Task: Analyze the user's query and correct any spelling mistakes or obvious typos. Keep the intent the same.

User query: ""{query}""

Return ONLY the corrected query, nothing else. If the query is already correct, return it unchanged.";

        try
        {
            var response = await _ollama.GenerateAsync(
                prompt,
                temperature: 0.1, // Low temperature for consistent corrections
                cancellationToken: ct
            );

            return response.Trim().Trim('"');
        }
        catch
        {
            // If LLM fails, return original query
            return query;
        }
    }

    /// <summary>
    /// Validate that query doesn't attempt config-only operations.
    /// Allows output format flags but prevents directory/URL changes.
    /// </summary>
    private static QueryValidation ValidateQuerySafety(string query)
    {
        var lowerQuery = query.ToLowerInvariant();

        // Forbidden: Changing directories
        var directoryPatterns = new[]
        {
            "output directory",
            "output dir",
            "save to",
            "write to directory",
            "set directory",
            "change directory",
            "output path",
            "set path"
        };

        foreach (var pattern in directoryPatterns)
        {
            if (lowerQuery.Contains(pattern))
            {
                return new QueryValidation(false, $"Cannot change output directory via prompts. Use --output-dir CLI argument or appsettings.json.");
            }
        }

        // Forbidden: Changing URLs/endpoints
        var urlPatterns = new[]
        {
            "ollama url",
            "ollama endpoint",
            "set url",
            "change url",
            "base url",
            "api url",
            "qdrant url",
            "configure url"
        };

        foreach (var pattern in urlPatterns)
        {
            if (lowerQuery.Contains(pattern))
            {
                return new QueryValidation(false, $"Cannot change service URLs via prompts. Use appsettings.json configuration.");
            }
        }

        // Forbidden: API keys or secrets
        var secretPatterns = new[]
        {
            "api key",
            "api token",
            "secret",
            "password",
            "credentials"
        };

        foreach (var pattern in secretPatterns)
        {
            if (lowerQuery.Contains(pattern))
            {
                return new QueryValidation(false, $"Cannot configure secrets via prompts. Use environment variables or appsettings.json.");
            }
        }

        // Allowed: Output format flags (e.g., "and output json")
        // These are fine because they don't change config, just response format
        var allowedFormatPatterns = new[]
        {
            "output json",
            "output xml",
            "output csv",
            "format json",
            "format xml",
            "show as json"
        };

        // Query is valid
        return new QueryValidation(true, string.Empty);
    }

    private record QueryValidation(bool IsValid, string Reason);
}
