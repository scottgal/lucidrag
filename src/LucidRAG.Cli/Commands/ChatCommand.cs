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
/// Interactive chat with documents
/// </summary>
public static class ChatCommand
{
    private static readonly Option<string?> CollectionOpt = new("-c", "--collection") { Description = "Collection to chat with" };
    private static readonly Option<string?> ModelOpt = new("--model") { Description = "Ollama model to use" };
    private static readonly Option<int> TopKOpt = new("-k", "--top") { Description = "Number of context segments", DefaultValueFactory = _ => 5 };
    private static readonly Option<string?> DataDirOpt = new("--data-dir") { Description = "Data directory" };
    private static readonly Option<bool> VerboseOpt = new("-v", "--verbose") { Description = "Verbose output", DefaultValueFactory = _ => false };

    public static Command Create()
    {
        var command = new Command("rag-chat", "Interactive RAG chat with documents (legacy)");
        command.Options.Add(CollectionOpt);
        command.Options.Add(ModelOpt);
        command.Options.Add(TopKOpt);
        command.Options.Add(DataDirOpt);
        command.Options.Add(VerboseOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var collectionName = parseResult.GetValue(CollectionOpt);
            var model = parseResult.GetValue(ModelOpt);
            var topK = parseResult.GetValue(TopKOpt);
            var dataDir = parseResult.GetValue(DataDirOpt);
            var verbose = parseResult.GetValue(VerboseOpt);

            var config = new CliConfig
            {
                DataDirectory = Program.EnsureDataDirectory(dataDir),
                Verbose = verbose
            };

            if (!string.IsNullOrEmpty(model))
                config.OllamaModel = model;

            AnsiConsole.Write(new FigletText("LucidRAG").Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[dim]Interactive RAG Chat[/]");
            AnsiConsole.MarkupLine("[dim]Type 'exit' or 'quit' to end the session[/]\n");

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
                AnsiConsole.MarkupLine("[yellow]No documents indexed yet. Use 'lucidrag index' first.[/]");
                return 1;
            }

            AnsiConsole.MarkupLine($"[green]Loaded {docCount} documents[/]");

            // Check Ollama
            var ollamaAvailable = false;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var response = await http.GetAsync($"{config.OllamaUrl}/api/tags", ct);
                ollamaAvailable = response.IsSuccessStatusCode;
            }
            catch { }

            if (!ollamaAvailable)
            {
                AnsiConsole.MarkupLine("[yellow]Ollama not available - using search-only mode[/]");
                AnsiConsole.MarkupLine("[dim]Start Ollama for full chat capabilities[/]\n");
            }
            else
            {
                AnsiConsole.MarkupLine($"[green]Using model: {config.OllamaModel}[/]\n");
            }

            // Chat loop
            while (!ct.IsCancellationRequested)
            {
                AnsiConsole.Markup("[cyan]You:[/] ");
                var input = Console.ReadLine()?.Trim();

                if (string.IsNullOrEmpty(input))
                    continue;

                if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                    input.Equals("quit", StringComparison.OrdinalIgnoreCase))
                    break;

                // Search for relevant context
                var queryEmbedding = await embedder.EmbedAsync(input, ct);
                var segments = await vectorStore.SearchAsync(
                    "ragdocuments",
                    queryEmbedding,
                    topK,
                    docId: null,
                    ct);

                if (segments.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No relevant information found in the documents.[/]\n");
                    continue;
                }

                if (ollamaAvailable)
                {
                    // Build context and query Ollama
                    var context = string.Join("\n\n", segments.Select((s, i) =>
                        $"[Source {i + 1}]: {s.Text}"));

                    var prompt = $"""
                        Based on the following context from the documents, answer the user's question.
                        If the answer isn't in the context, say you don't have enough information.
                        Always cite which source(s) you used.

                        Context:
                        {context}

                        Question: {input}

                        Answer:
                        """;

                    await AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots)
                        .SpinnerStyle(Style.Parse("cyan"))
                        .StartAsync("Thinking...", async ctx =>
                        {
                            try
                            {
                                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                                var requestBody = System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    model = config.OllamaModel,
                                    prompt,
                                    stream = false
                                });

                                var response = await http.PostAsync(
                                    $"{config.OllamaUrl}/api/generate",
                                    new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json"),
                                    ct);

                                if (response.IsSuccessStatusCode)
                                {
                                    var json = await response.Content.ReadAsStringAsync(ct);
                                    var result = System.Text.Json.JsonDocument.Parse(json);
                                    var answer = result.RootElement.GetProperty("response").GetString();

                                    AnsiConsole.MarkupLine($"\n[green]Assistant:[/] {Markup.Escape(answer ?? "No response")}");
                                }
                                else
                                {
                                    AnsiConsole.MarkupLine($"[red]Ollama error: {response.StatusCode}[/]");
                                }
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
                            }
                        });
                }
                else
                {
                    // Search-only mode - just show relevant segments
                    AnsiConsole.MarkupLine("\n[green]Relevant context found:[/]");
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var s = segments[i];
                        var section = s.SectionTitle ?? s.HeadingPath ?? "Document";
                        AnsiConsole.MarkupLine($"\n[cyan][{i + 1}][/] [dim]{Markup.Escape(section)}[/] (score: {s.QuerySimilarity:F3})");
                        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(s.Text.Length > 200 ? s.Text[..197] + "..." : s.Text)}[/]");
                    }
                }

                AnsiConsole.WriteLine();
            }

            AnsiConsole.MarkupLine("[dim]Goodbye![/]");
            return 0;
        });

        return command;
    }
}
