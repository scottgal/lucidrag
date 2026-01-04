using System.CommandLine;
using System.Reflection;
using LucidRAG.ImageCli.Commands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mostlylucid.DocSummarizer.Images.Extensions;
using Serilog;
using Spectre.Console;

namespace LucidRAG.ImageCli;

class Program
{
    static int Main(string[] args)
    {
        // Display banner
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            ShowBanner();
        }

        // Build configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddUserSecrets<Program>(optional: true) // Load API keys from user secrets
            .AddEnvironmentVariables("LUCIDRAG_")
            .Build();

        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            // Build root command
            var rootCommand = new RootCommand("LucidRAG Image CLI - Advanced image analysis and processing");

            // Global options
            var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Enable verbose logging", DefaultValueFactory = _ => false };
            var ollamaUrlOption = new Option<string?>("--ollama-url") { Description = "Ollama API base URL", DefaultValueFactory = _ => configuration["Ollama:BaseUrl"] ?? "http://localhost:11434" };

            rootCommand.Options.Add(verboseOption);
            rootCommand.Options.Add(ollamaUrlOption);

            // Add subcommands
            rootCommand.Subcommands.Add(AnalyzeCommand.Create());
            rootCommand.Subcommands.Add(BatchCommand.Create());
            rootCommand.Subcommands.Add(DedupeCommand.Create());
            rootCommand.Subcommands.Add(ExtractFramesCommand.Create());
            rootCommand.Subcommands.Add(PreviewCommand.Create());
            rootCommand.Subcommands.Add(ScoreCommand.Create());

            // Parse and execute
            return rootCommand.Parse(args).Invoke();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            AnsiConsole.MarkupLine($"[red]✗ Fatal error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void ShowBanner()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "1.0.0";

        AnsiConsole.Write(
            new FigletText("LucidRAG Image")
                .LeftJustified()
                .Color(Color.Cyan1));

        AnsiConsole.MarkupLine($"[dim]Version {version}[/]");
        AnsiConsole.MarkupLine("[dim]Advanced image analysis powered by DocSummarizer.Images[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Build a service provider with all required services.
    /// </summary>
    public static IServiceProvider BuildServiceProvider(IConfiguration configuration, bool verbose = false)
    {
        var services = new ServiceCollection();

        // Add configuration
        services.AddSingleton(configuration);

        // Add logging
        var logLevel = verbose ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information;
        services.AddLogging(builder =>
        {
            builder.AddSerilog(new LoggerConfiguration()
                .MinimumLevel.Is(logLevel)
                .WriteTo.Console()
                .CreateLogger());
        });

        // Add DocSummarizer.Images services
        services.AddDocSummarizerImages(configuration.GetSection("Images"));

        // Add Signal Database for caching (stores in user's app data directory)
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LucidRAG",
            "ImageCache");
        Directory.CreateDirectory(appDataPath);

        var dbPath = Path.Combine(appDataPath, "imageanalysis.db");
        services.AddSingleton<Mostlylucid.DocSummarizer.Images.Services.Storage.ISignalDatabase>(
            sp => new Mostlylucid.DocSummarizer.Images.Services.Storage.SignalDatabase(dbPath));

        // Add CLI-specific services
        services.AddSingleton<Services.OutputFormatters.TableFormatter>();
        services.AddSingleton<Services.OutputFormatters.JsonFormatter>();
        services.AddSingleton<Services.OutputFormatters.MarkdownFormatter>();

        // Add vision LLM services
        services.AddSingleton<Services.VisionLlmService>();
        services.AddSingleton<Services.UnifiedVisionService>();

        // Add escalation service
        services.AddSingleton<Services.EscalationService>();

        // Add batch processor
        services.AddSingleton<Services.ImageBatchProcessor>();

        // Add deduplication service
        services.AddSingleton<Services.DeduplicationService>();

        return services.BuildServiceProvider();
    }
}
