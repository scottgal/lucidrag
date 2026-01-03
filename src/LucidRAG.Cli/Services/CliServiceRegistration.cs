using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Extensions;
using Mostlylucid.DocSummarizer.Services;
using LucidRAG.Data;
using Serilog;

namespace LucidRAG.Cli.Services;

/// <summary>
/// Service registration for CLI-specific DI container
/// Uses SQLite + DuckDB for zero-dependency local storage
/// </summary>
public static class CliServiceRegistration
{
    /// <summary>
    /// Build a service provider configured for CLI usage with local storage
    /// </summary>
    public static ServiceProvider BuildServiceProvider(CliConfig config, bool verbose = false)
    {
        var services = new ServiceCollection();

        // Logging via Serilog - suppress DocSummarizer internal messages unless verbose
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

        services.AddLogging(builder => builder.AddSerilog(dispose: true));

        // Database - SQLite for local storage
        var dbPath = Path.Combine(config.DataDirectory, "lucidrag.db");
        services.AddDbContext<RagDocumentsDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        // DocSummarizer.Core with DuckDB vector store
        var vectorDbPath = Path.Combine(config.DataDirectory, "vectors.duckdb");
        services.AddDocSummarizer(opt =>
        {
            // Use ONNX for embeddings (no external service required)
            opt.EmbeddingBackend = EmbeddingBackend.Onnx;
            opt.Onnx.EmbeddingModel = OnnxEmbeddingModel.AllMiniLmL6V2;

            // Use DuckDB for vector storage
            opt.BertRag.VectorStore = VectorStoreBackend.DuckDB;
            opt.BertRag.CollectionName = "ragdocuments";
            opt.BertRag.ReindexOnStartup = false;

            // Verbose output
            opt.Output.Verbose = verbose;

            // Configure Ollama if available
            if (!string.IsNullOrEmpty(config.OllamaUrl))
            {
                opt.Ollama.BaseUrl = config.OllamaUrl;
                opt.Ollama.Model = config.OllamaModel;
            }
        });

        // CLI-specific services
        services.AddSingleton(config);
        services.AddSingleton<CliProgressRenderer>();
        services.AddScoped<CliDocumentProcessor>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Ensure database is created and up to date
    /// </summary>
    public static async Task EnsureDatabaseAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
        await db.Database.EnsureCreatedAsync();
    }
}

/// <summary>
/// CLI configuration
/// </summary>
public class CliConfig
{
    public string DataDirectory { get; set; } = Program.GetDefaultDataDirectory();
    public string? OllamaUrl { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "llama3.2:3b";
    public bool Verbose { get; set; }
}
