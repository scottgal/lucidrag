using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Mostlylucid.DocSummarizer.Extensions;
using Mostlylucid.DocSummarizer.Images.Extensions;
using Mostlylucid.DocSummarizer.Anthropic.Extensions;
using Mostlylucid.DocSummarizer.OpenAI.Extensions;
using Mostlylucid.DocSummarizer.Config;
using LucidRAG.Config;
using LucidRAG.Data;
using LucidRAG.Multitenancy;
using LucidRAG.Services;
using LucidRAG.Services.Background;
using LucidRAG.Services.Sentinel;
using LucidRAG.Services.Storage;
using LucidRAG.Hubs;
using Scalar.AspNetCore;
using Serilog;

// Parse command line arguments for standalone mode
var standaloneMode = args.Contains("--standalone") || args.Contains("-s");
var port = 5080;
var portArg = args.FirstOrDefault(a => a.StartsWith("--port="));
if (portArg != null && int.TryParse(portArg.Split('=')[1], out var parsedPort))
    port = parsedPort;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel for standalone mode
if (standaloneMode)
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenLocalhost(port);
    });
}

// Serilog
builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console());

// Configuration
builder.Services.Configure<RagDocumentsConfig>(
    builder.Configuration.GetSection(RagDocumentsConfig.SectionName));
builder.Services.Configure<PromptsConfig>(
    builder.Configuration.GetSection(PromptsConfig.SectionName));

var ragConfig = builder.Configuration
    .GetSection(RagDocumentsConfig.SectionName)
    .Get<RagDocumentsConfig>() ?? new();

// Database - use SQLite in standalone mode, PostgreSQL otherwise
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (standaloneMode)
{
    // Use SQLite for standalone mode (portable)
    var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
    Directory.CreateDirectory(dataDir);
    var sqliteConnectionString = $"Data Source={Path.Combine(dataDir, "ragdocs.db")}";
    builder.Services.AddDbContext<RagDocumentsDbContext>(options =>
        options.UseSqlite(sqliteConnectionString));
    connectionString = sqliteConnectionString; // Update for later checks
}
else
{
    builder.Services.AddDbContext<RagDocumentsDbContext>(options =>
        options.UseNpgsql(connectionString));
}

// DocSummarizer.Core
builder.Services.AddDocSummarizer(builder.Configuration.GetSection("DocSummarizer"));

// DocSummarizer.Images - always add for image handling
builder.Services.AddDocSummarizerImages(builder.Configuration.GetSection("Images"));

// LLM Backend selection based on configuration
var llmBackend = builder.Configuration.GetValue<string>("DocSummarizer:LlmBackend") ?? "Ollama";
switch (llmBackend.ToLowerInvariant())
{
    case "anthropic":
        builder.Services.AddDocSummarizerAnthropic(builder.Configuration.GetSection("Anthropic"));
        break;
    case "openai":
        builder.Services.AddDocSummarizerOpenAI(builder.Configuration.GetSection("OpenAI"));
        break;
    // Default: Ollama is already registered by AddDocSummarizer
}

// LFU cache for synthesis results
builder.Services.AddSingleton<SynthesisCacheService>();

// Application services
builder.Services.AddScoped<IDocumentProcessingService, DocumentProcessingService>();
builder.Services.AddScoped<IConversationService, ConversationService>();
builder.Services.AddScoped<IAgenticSearchService, AgenticSearchService>();
builder.Services.AddScoped<IEntityGraphService, EntityGraphService>();
builder.Services.AddScoped<ICommunityDetectionService, CommunityDetectionService>();
builder.Services.AddScoped<IRetrievalEntityService, RetrievalEntityService>();
builder.Services.AddSingleton<IQueryExpansionService, EmbeddingQueryExpansionService>();
builder.Services.AddSingleton<DocumentProcessingQueue>();
builder.Services.AddHostedService<DocumentQueueProcessor>();
builder.Services.AddHostedService<DemoContentSeeder>();
builder.Services.AddSingleton<IWebCrawlerService, WebCrawlerService>();
builder.Services.AddSingleton<IIngestionService, IngestionService>();

// Sentinel query decomposition service
builder.Services.Configure<SentinelConfig>(
    builder.Configuration.GetSection("Sentinel"));
builder.Services.AddScoped<ISentinelService, SentinelService>();

// Evidence storage for multimodal artifacts
builder.Services.Configure<EvidenceStorageOptions>(
    builder.Configuration.GetSection(EvidenceStorageOptions.SectionName));
builder.Services.AddSingleton<IEvidenceStorage, FilesystemEvidenceStorage>();
builder.Services.AddScoped<IEvidenceRepository, EvidenceRepository>();

// Multi-tenancy services (always register for API, middleware only when enabled)
var multitenancyEnabled = builder.Configuration.GetValue<bool>("Multitenancy:Enabled");
if (!standaloneMode)
{
    builder.Services.AddMultitenancy(builder.Configuration);
}

// HttpClient for external API calls (RSS feeds, etc.)
builder.Services.AddHttpClient();

// SignalR for real-time updates
builder.Services.AddSignalR();
builder.Services.AddSingleton<IProcessingNotificationService, ProcessingNotificationService>();

// MVC + Razor
builder.Services.AddControllersWithViews();

// OpenAPI
builder.Services.AddOpenApi();

// Health checks
if (!string.IsNullOrEmpty(connectionString) && !connectionString.StartsWith("Data Source="))
{
    builder.Services.AddHealthChecks()
        .AddNpgSql(connectionString);
}
else
{
    builder.Services.AddHealthChecks();
}

// Data Protection - persist keys for antiforgery tokens to survive restarts
var keysDir = standaloneMode
    ? Path.Combine(AppContext.BaseDirectory, "data", "keys")
    : Directory.Exists("/app")
        ? "/app/data/keys"
        : Path.Combine(Path.GetTempPath(), "lucidrag", "keys");
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
    .SetApplicationName("LucidRAG");

// Antiforgery for HTMX
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-XSRF-TOKEN";
    options.Cookie.Name = "XSRF-TOKEN";
    options.Cookie.SameSite = SameSiteMode.Strict;
});

var app = builder.Build();

// Serilog request logging
app.UseSerilogRequestLogging();

// API documentation always available
app.MapOpenApi();
app.MapScalarApiReference();

// Static files
app.UseStaticFiles();

// Routing
app.UseRouting();

// Multi-tenancy middleware (if enabled)
if (multitenancyEnabled)
{
    app.UseMultitenancy();
}

// Antiforgery
app.UseAntiforgery();

// Health check
app.MapHealthChecks("/healthz");

// SignalR hubs
app.MapHub<DocumentProcessingHub>("/hubs/processing");

// Controllers
app.MapControllers();

// Tenant-scoped routes: /t/{tenantId}/...
app.MapControllerRoute(
    name: "tenant-default",
    pattern: "t/{tenantId}/{controller=Home}/{action=Index}/{id?}");

// Default route
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Ensure database is created/migrated
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
    if (standaloneMode && (connectionString?.StartsWith("Data Source=") ?? false))
    {
        // SQLite - just ensure created
        await db.Database.EnsureCreatedAsync();
    }
    else
    {
        await db.Database.MigrateAsync();

        // Also migrate tenant management tables (PostgreSQL only)
        var tenantDb = scope.ServiceProvider.GetService<TenantDbContext>();
        if (tenantDb != null)
        {
            await tenantDb.Database.MigrateAsync();
        }
    }
}

// Ensure upload directory exists
var uploadPath = standaloneMode
    ? Path.Combine(AppContext.BaseDirectory, "uploads")
    : ragConfig.UploadPath;
Directory.CreateDirectory(uploadPath);

// Ensure evidence storage directory exists
var evidenceConfig = builder.Configuration
    .GetSection(EvidenceStorageOptions.SectionName)
    .Get<EvidenceStorageOptions>() ?? new EvidenceStorageOptions();
var evidencePath = evidenceConfig.BasePath
    ?? (standaloneMode
        ? Path.Combine(AppContext.BaseDirectory, "evidence")
        : Path.Combine(uploadPath, "evidence"));
Directory.CreateDirectory(evidencePath);

// Open browser in standalone mode
if (standaloneMode)
{
    var url = $"http://localhost:{port}";
    Log.Information("LucidRAG starting in standalone mode at {Url}", url);
    Console.WriteLine();
    Console.WriteLine("╔════════════════════════════════════════════════════════╗");
    Console.WriteLine("║             lucidRAG - Standalone Mode                 ║");
    Console.WriteLine("╠════════════════════════════════════════════════════════╣");
    Console.WriteLine($"║  URL: {url,-49}║");
    Console.WriteLine("║  Press Ctrl+C to stop                                  ║");
    Console.WriteLine("╚════════════════════════════════════════════════════════╝");
    Console.WriteLine();

    // Open browser
    try
    {
        OpenBrowser(url);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Could not open browser automatically");
    }
}

app.Run();

// Helper to open browser cross-platform
static void OpenBrowser(string url)
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

// Make Program accessible for WebApplicationFactory in tests
public partial class Program { }
