using System.CommandLine;
using System.CommandLine.Parsing;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using LucidRAG.Cli.Services;
using Spectre.Console;

// Use alias to avoid conflict with GraphRag.IndexCommand
using CliCommands = LucidRAG.Cli.Commands;

// ============================================================================
// LucidRAG CLI v1.0.0
// Multi-document RAG with GraphRAG entity extraction
// Zero-config local storage: SQLite + DuckDB + ONNX embeddings
// ============================================================================

namespace LucidRAG.Cli;

internal static class Program
{
    // Global options
    private static readonly Option<string?> DataDirOption = new("--data-dir") { Description = "Data directory (default: ~/.lucidrag or %APPDATA%/lucidrag)" };
    private static readonly Option<bool> VerboseOption = new("--verbose", "-v") { Description = "Show detailed output", DefaultValueFactory = _ => false };
    private static readonly Option<string?> ConfigOption = new("--config") { Description = "Path to configuration file" };

    public static async Task<int> Main(string[] args)
    {
        // If no arguments provided, try to start conversational mode
        if (args.Length == 0)
        {
            return await TryStartConversationalModeAsync();
        }

        // Show banner for help
        if (args.Contains("--help") || args.Contains("-h"))
        {
            ShowBanner();
        }

        var rootCommand = BuildRootCommand();
        return rootCommand.Parse(args).Invoke();
    }

    /// <summary>
    /// Try to start conversational mode if Ollama is available
    /// Falls back to showing help if not available
    /// </summary>
    private static async Task<int> TryStartConversationalModeAsync()
    {
        // Quick Ollama availability check
        var isOllamaAvailable = await CheckOllamaAvailableAsync();

        if (isOllamaAvailable)
        {
            // Start conversational mode directly
            var config = new CliConfig
            {
                DataDirectory = EnsureDataDirectory(),
                Verbose = false
            };

            await Commands.ConversationalCommand.RunConversationalModeAsync(config, verbose: false, CancellationToken.None);
            return 0;
        }
        else
        {
            // Show banner and help
            ShowBanner();
            AnsiConsole.MarkupLine("[yellow]⚠[/]  Conversational mode requires Ollama");
            AnsiConsole.MarkupLine("[dim]Start Ollama with:[/] [green]ollama serve[/]");
            AnsiConsole.MarkupLine("[dim]Or run a specific command:[/] [green]lucidrag-cli --help[/]");
            AnsiConsole.WriteLine();
            return 0;
        }
    }

    /// <summary>
    /// Quick check if Ollama is available
    /// </summary>
    private static async Task<bool> CheckOllamaAvailableAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await client.GetAsync("http://localhost:11434/api/tags");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static void ShowBanner()
    {
        AnsiConsole.Write(new FigletText("LucidRAG").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[dim]Multi-document RAG with GraphRAG entity extraction[/]");
        AnsiConsole.MarkupLine("[dim]Zero-config: ONNX embeddings + DuckDB vectors + SQLite storage[/]");
        AnsiConsole.WriteLine();
    }

    private static RootCommand BuildRootCommand()
    {
        var rootCommand = new RootCommand("LucidRAG - Multi-document RAG CLI with GraphRAG entity extraction");

        // Global options
        rootCommand.Options.Add(DataDirOption);
        rootCommand.Options.Add(VerboseOption);
        rootCommand.Options.Add(ConfigOption);

        // Add subcommands
        rootCommand.Subcommands.Add(CliCommands.ConversationalCommand.Create());
        rootCommand.Subcommands.Add(CliCommands.IndexCommand.Create());
        rootCommand.Subcommands.Add(CliCommands.SearchCommand.Create());
        rootCommand.Subcommands.Add(CliCommands.ChatCommand.Create());
        rootCommand.Subcommands.Add(CliCommands.OcrCommand.Create());
        rootCommand.Subcommands.Add(CliCommands.ServeCommand.Create());
        rootCommand.Subcommands.Add(CliCommands.CollectionsCommand.Create());
        rootCommand.Subcommands.Add(CliCommands.ConfigCommand.Create());
        rootCommand.Subcommands.Add(CliCommands.CheckCommand.Create());

        return rootCommand;
    }

    /// <summary>
    /// Get the default data directory for LucidRAG
    /// </summary>
    public static string GetDefaultDataDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "lucidrag");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".lucidrag");
        }
    }

    /// <summary>
    /// Ensure the data directory exists and return its path
    /// </summary>
    public static string EnsureDataDirectory(string? customPath = null)
    {
        var dataDir = customPath ?? GetDefaultDataDirectory();

        if (!Directory.Exists(dataDir))
        {
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(Path.Combine(dataDir, "uploads"));
            Directory.CreateDirectory(Path.Combine(dataDir, "logs"));
        }

        return dataDir;
    }
}
