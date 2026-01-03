using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using LucidRAG.Data;

namespace LucidRAG.Tests.Integration;

/// <summary>
/// Web application factory for integration testing with real services
/// </summary>
public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Connection string for existing dev PostgreSQL.
    /// Reads from ConnectionStrings__DefaultConnection env var (CI), POSTGRES_PASSWORD env var, or .env file.
    /// </summary>
    public string PostgresConnectionString { get; set; } = GetConnectionString();

    private static string GetConnectionString()
    {
        // First check for full connection string (set by CI workflow)
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        if (!string.IsNullOrEmpty(connectionString))
        {
            return connectionString;
        }

        // Try POSTGRES_PASSWORD environment variable
        var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");

        // Fall back to .env file if not set
        if (string.IsNullOrEmpty(password))
        {
            var envPath = FindEnvFile();
            if (envPath != null && File.Exists(envPath))
            {
                var lines = File.ReadAllLines(envPath);
                foreach (var line in lines)
                {
                    if (line.StartsWith("POSTGRES_PASSWORD="))
                    {
                        password = line.Substring("POSTGRES_PASSWORD=".Length).Trim();
                        break;
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                "POSTGRES_PASSWORD not found. Set ConnectionStrings__DefaultConnection, POSTGRES_PASSWORD environment variable, or ensure .env file exists in solution root.");
        }

        return $"Host=localhost;Port=5432;Database=ragdocs_test;Username=postgres;Password={password}";
    }

    private static string? FindEnvFile()
    {
        // Search upwards from test bin directory to find .env in solution root
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var envPath = Path.Combine(dir.FullName, ".env");
            if (File.Exists(envPath))
                return envPath;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Ollama base URL
    /// </summary>
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Qdrant URL (if using Qdrant instead of DuckDB)
    /// </summary>
    public string QdrantUrl { get; set; } = "http://localhost:6333";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Remove existing DbContext registration
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<RagDocumentsDbContext>));
            if (descriptor != null)
            {
                services.Remove(descriptor);
            }

            // Add PostgreSQL for testing
            services.AddDbContext<RagDocumentsDbContext>(options =>
                options.UseNpgsql(PostgresConnectionString));
        });

        builder.ConfigureAppConfiguration((context, config) =>
        {
            // Use existing models directory from main project to avoid re-downloading
            var modelsPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "Mostlylucid", "bin", "Debug", "net9.0", "models"));

            // Override configuration for testing
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = PostgresConnectionString,
                ["DocSummarizer:Ollama:BaseUrl"] = OllamaBaseUrl,
                ["DocSummarizer:BertRag:VectorStore"] = "DuckDB",
                ["DocSummarizer:BertRag:ReindexOnStartup"] = "false",
                ["DocSummarizer:Onnx:ModelsPath"] = modelsPath,
                ["RagDocuments:RequireApiKey"] = "false",
                ["RagDocuments:UploadPath"] = "./test-uploads"
            });
        });
    }

    /// <summary>
    /// Ensure database is created and migrated
    /// </summary>
    public async Task EnsureDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    /// <summary>
    /// Clean up test data
    /// </summary>
    public async Task CleanupAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

        // Delete test data
        db.ConversationMessages.RemoveRange(db.ConversationMessages);
        db.Conversations.RemoveRange(db.Conversations);
        db.DocumentEntityLinks.RemoveRange(db.DocumentEntityLinks);
        db.EntityRelationships.RemoveRange(db.EntityRelationships);
        db.Entities.RemoveRange(db.Entities);
        db.Documents.RemoveRange(db.Documents);
        db.Collections.RemoveRange(db.Collections);

        await db.SaveChangesAsync();
    }
}
