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

    public static int Main(string[] args)
    {
        // Show banner for help
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            ShowBanner();
        }

        var rootCommand = BuildRootCommand();
        return rootCommand.Parse(args).Invoke();
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
        rootCommand.Subcommands.Add(CliCommands.IndexCommand.Create());
        rootCommand.Subcommands.Add(CliCommands.SearchCommand.Create());
        rootCommand.Subcommands.Add(CliCommands.ChatCommand.Create());
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
