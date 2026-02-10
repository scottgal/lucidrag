using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioSummarizer.Core.Extensions;
using LucidRAG.Authorization;
using LucidRAG.Config;
using LucidRAG.Core.Services.Caching;
using LucidRAG.Data;
using LucidRAG.Extensions;
using LucidRAG.GraphQL;
using LucidRAG.Hubs;
using LucidRAG.Identity;
using LucidRAG.LLM.Extensions;
using LucidRAG.Middleware;
using LucidRAG.Multitenancy;
using LucidRAG.Plugin.Postgres;
using LucidRAG.Services;
using LucidRAG.Services.Background;
using LucidRAG.Services.Sentinel;
using LucidRAG.Services.Storage;
using LucidRAG.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Mostlylucid.DocSummarizer.Anthropic.Extensions;
using Mostlylucid.DocSummarizer.Core.Services;
using Mostlylucid.DocSummarizer.Data.Extensions;
using Mostlylucid.DocSummarizer.Extensions;
using Mostlylucid.DocSummarizer.FullText.Lucene;
using Mostlylucid.DocSummarizer.Images.Extensions;
using Mostlylucid.DocSummarizer.OpenAI.Extensions;
using Mostlylucid.DocSummarizer.Search;
using Mostlylucid.Summarizer.Core.Extensions;
using Serilog;
using DomainClassifier.Core.Extensions;
using DomainClassifier.Financial.Extensions;
using DomainClassifier.Narrative.Extensions;
using DomainClassifier.Technical.Extensions;
using VideoSummarizer.Core.Extensions;

// Parse command line arguments for standalone mode
var standaloneMode = args.Contains("--standalone") || args.Contains("-s");
var port = 5080;
var portArg = args.FirstOrDefault(a => a.StartsWith("--port="));
if (portArg != null && int.TryParse(portArg.Split('=')[1], out var parsedPort))
    port = parsedPort;

// --dbpath=PATH: root directory for all local data (SQLite, Lucene index, uploads, evidence)
var dbPathArg = args.FirstOrDefault(a => a.StartsWith("--dbpath="));
var dataBasePath = dbPathArg != null
    ? Path.GetFullPath(dbPathArg.Split('=', 2)[1])
    : Path.Combine(AppContext.BaseDirectory, "data");

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel for large file uploads (streaming)
builder.WebHost.ConfigureKestrel(options =>
{
    // Allow large request bodies for file uploads (500MB default, streaming handles larger)
    options.Limits.MaxRequestBodySize = 500 * 1024 * 1024; // 500MB

    // Configure specific port for standalone mode
    if (standaloneMode)
        options.ListenLocalhost(port);
});

// Configure form options for large file uploads
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 500 * 1024 * 1024; // 500MB for form uploads
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

// Serilog
builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext());

// Configuration
builder.Services.Configure<RagDocumentsConfig>(
    builder.Configuration.GetSection(RagDocumentsConfig.SectionName));
builder.Services.Configure<PromptsConfig>(
    builder.Configuration.GetSection(PromptsConfig.SectionName));
builder.Services.Configure<RrfWeightsConfig>(
    builder.Configuration.GetSection(RrfWeightsConfig.SectionName));

var ragConfig = builder.Configuration
    .GetSection(RagDocumentsConfig.SectionName)
    .Get<RagDocumentsConfig>() ?? new RagDocumentsConfig();

// Multi-tenancy services (register before DbContext to make dependencies clear)
var multitenancyEnabled = builder.Configuration.GetValue<bool>("Multitenancy:Enabled");
if (!standaloneMode)
    builder.Services.AddMultitenancy(builder.Configuration);
else
    // Standalone mode: register null tenant accessor for compatibility
    builder.Services.AddScoped<ITenantAccessor, TenantAccessor>();

// Database - use SQLite in standalone mode, PostgreSQL otherwise
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (standaloneMode)
{
    // Use SQLite for standalone mode (portable)
    Directory.CreateDirectory(dataBasePath);
    var sqliteConnectionString = $"Data Source={Path.Combine(dataBasePath, "ragdocs.db")}";
    builder.Services.AddDbContext<RagDocumentsDbContext>(options =>
        options.UseSqlite(sqliteConnectionString));
    connectionString = sqliteConnectionString; // Update for later checks
}
else if (multitenancyEnabled)
{
    // Multi-tenancy: register TenantSchemaInterceptor as scoped (depends on tenant context per request)
    builder.Services.AddScoped<TenantSchemaInterceptor>();

    // Register DbContext with interceptor for per-connection search_path switching
    // EnableRetryOnFailure handles transient network failures
    builder.Services.AddDbContext<RagDocumentsDbContext>((sp, options) =>
    {
        var interceptor = sp.GetRequiredService<TenantSchemaInterceptor>();
        options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                npgsqlOptions.UseVector();
                npgsqlOptions.EnableRetryOnFailure(
                    5,
                    TimeSpan.FromSeconds(30),
                    null);
            })
            .AddInterceptors(interceptor);
    });
}
else
{
    // EnableRetryOnFailure handles transient network failures
    builder.Services.AddDbContext<RagDocumentsDbContext>(options =>
        options.UseNpgsql(connectionString, npgsqlOptions =>
        {
            npgsqlOptions.UseVector();
            npgsqlOptions.EnableRetryOnFailure(
                5,
                TimeSpan.FromSeconds(30),
                null);
        }));
}

// DocSummarizer.Core
builder.Services.AddDocSummarizer(builder.Configuration.GetSection("DocSummarizer"));

// DocSummarizer.Images - always add for image handling
builder.Services.AddDocSummarizerImages(builder.Configuration.GetSection("Images"));

// VideoSummarizer - video processing pipeline (mp4, mkv, etc.)
builder.Services.AddVideoSummarizer();

// AudioSummarizer - audio processing pipeline (mp3, wav, m4a, flac, etc.)
builder.Services.AddAudioSummarizer(builder.Configuration);

// DataSummarizer - data file processing with wave-based analysis (csv, xlsx, json, parquet)
builder.Services.AddDataSummarizer(opt =>
{
    opt.SampleSize = 10000;
    opt.EnablePiiDetection = true;
    opt.EnableOutlierDetection = true;
    opt.EnableCorrelation = false; // Expensive, disabled by default
});

// Domain classifier plugins (optional - enrich pipeline output with domain-specific intelligence)
builder.Services.AddDomainFinancial();
builder.Services.AddDomainNarrative();
builder.Services.AddDomainTechnical();
// builder.Services.AddDomainLegal();      // Future
// builder.Services.AddDomainMedical();    // Future

// Pipeline registry for unified content processing (routes .gif, .png, etc. to ImagePipeline)
builder.Services.AddPipelineRegistry();

// Domain classifier registry (auto-discovers all registered domain plugins)
builder.Services.AddDomainClassifierRegistry();

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

// Unified LLM provider infrastructure (YAML-based configuration)
// Provides named providers (fast-local, general, smart, vision) with Polly resilience
builder.Services.AddLucidRagLlm(
    Path.Combine(AppContext.BaseDirectory, "Config", "llm-providers.yaml"),
    Path.Combine(AppContext.BaseDirectory, "Config", "prompts.yaml"));

// LFU cache for synthesis results
builder.Services.AddSingleton<SynthesisCacheService>();

// Per-conversation salient segment cache registry (in-memory, persists across requests)
builder.Services.AddSingleton<SalientSegmentCacheRegistry>();

// SaaS API key service
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IApiKeyService, ApiKeyService>();

// Personal memory service for user-scoped fact storage
builder.Services.AddScoped<IPersonalMemoryService, PersonalMemoryService>();

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

// File explorer services
builder.Services.AddScoped<IFolderService, FolderService>();
builder.Services.AddScoped<IExplorerSearchService, ExplorerSearchService>();

// YAML manifest-based lens system for customizable response formatting
builder.Services.AddYamlLenses(builder.Configuration);

// Full-text search: Lucene.NET is core default, PostgreSQL plugin overrides when available
var luceneIndexPath = standaloneMode
    ? Path.Combine(dataBasePath, "lucene-index")
    : Path.Combine(builder.Environment.ContentRootPath, "data", "lucene-index");
builder.Services.AddLuceneFullTextSearch(luceneIndexPath);
builder.Services.AddScoped<IBm25SearchService, LuceneBm25SearchService>();

// PostgreSQL FTS plugin: override Lucene when PostgreSQL is the backend (10-25x faster)
if (!standaloneMode && connectionString?.Contains("Host=") == true)
    builder.Services.AddScoped<IBm25SearchService, PostgresBm25Service>();

// Table extraction services
// TableExtractorFactory must be Singleton to avoid DI scope issues with DocumentToMarkdownService
builder.Services.AddSingleton<ITableExtractorFactory, TableExtractorFactory>();
builder.Services.AddScoped<TableProcessingService>();

// Sentinel query decomposition service
builder.Services.Configure<SentinelConfig>(
    builder.Configuration.GetSection("Sentinel"));
builder.Services.AddScoped<ISentinelService, SentinelService>();
builder.Services.AddScoped<ICollectionRouter, CollectionRouter>();

// Per-tenant LFU cache for evidence and entities (5-10x faster text hydration)
builder.Services.Configure<LfuCacheConfig>(
    builder.Configuration.GetSection("LfuCache"));
builder.Services.AddSingleton<ITenantLfuCacheService, TenantLfuCacheService>();

// Salient terms service for autocomplete (TF-IDF + RRF)
builder.Services.AddScoped<ISalientTermsService, SalientTermsService>();
builder.Services.AddHostedService<SalientTermsUpdaterService>();

// Evidence storage for multimodal artifacts
builder.Services.Configure<EvidenceStorageOptions>(
    builder.Configuration.GetSection(EvidenceStorageOptions.SectionName));
builder.Services.AddSingleton<IEvidenceStorage, FilesystemEvidenceStorage>();
builder.Services.AddScoped<IEvidenceRepository, EvidenceRepository>();

// HttpClient for external API calls (RSS feeds, etc.) with Polly resilience
builder.Services.AddHttpClient("Resilient")
    .AddStandardResilienceHandler();

// SignalR for real-time updates
builder.Services.AddSignalR();
builder.Services.AddSingleton<IProcessingNotificationService, ProcessingNotificationService>();

// MVC + Razor
builder.Services.AddControllersWithViews();

// OpenAPI
builder.Services.AddOpenApi();

// GraphQL for knowledge graph queries
builder.Services
    .AddGraphQLServer()
    .AddQueryType<KnowledgeGraphQuery>()
    .AddFiltering()
    .AddSorting()
    .ModifyRequestOptions(opt => opt.IncludeExceptionDetails = builder.Environment.IsDevelopment());

// Health checks
if (!string.IsNullOrEmpty(connectionString) && !connectionString.StartsWith("Data Source="))
    builder.Services.AddHealthChecks()
        .AddNpgSql(connectionString);
else
    builder.Services.AddHealthChecks();

// ASP.NET Core Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        // Password settings
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 8;

        // Lockout settings
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.MaxFailedAccessAttempts = 5;

        // User settings
        options.User.RequireUniqueEmail = true;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<RagDocumentsDbContext>();

// Demo admin seeder for development mode
builder.Services.AddHostedService<DemoAdminSeeder>();

// Authorization policies
builder.Services.AddLucidRagAuthorization();

// Cookie settings for auth
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/auth/login";
    options.LogoutPath = "/auth/logout";
    options.AccessDeniedPath = "/auth/access-denied";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromDays(7);
    options.SlidingExpiration = true;
});

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

// CORS for SaaS widget (dynamic origins handled by ApiKeyAuthMiddleware,
// but we need a base policy for the CORS middleware to activate)
builder.Services.AddCors(options =>
{
    options.AddPolicy("SaasWidget", policy =>
    {
        policy.AllowAnyOrigin() // Actual origin validation is in ApiKeyAuthMiddleware
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

// Initialize Lucene FTS index
using (var scope = app.Services.CreateScope())
{
    var fts = scope.ServiceProvider.GetRequiredService<IFullTextSearch>();
    await fts.InitializeAsync();
}

// Serilog request logging
app.UseSerilogRequestLogging();

// API documentation always available
app.MapOpenApi();
// app.MapScalarApiReference(); // TODO: restore when Scalar.AspNetCore package is added

// Static files
app.UseStaticFiles();

// Routing
app.UseRouting();

// CORS for SaaS widget endpoints
app.UseCors("SaasWidget");

// Authentication & Authorization
app.UseAuthentication();

// SaaS API key authentication (for /api/saas/ endpoints)
app.UseApiKeyAuth();

app.UseAuthorization();

// Auto-login as demo admin in development mode
app.UseDevAutoLogin();

// Multi-tenancy middleware (if enabled)
if (multitenancyEnabled) app.UseMultitenancy();

// Antiforgery
app.UseAntiforgery();

// Health check
app.MapHealthChecks("/healthz");

// SignalR hubs
app.MapHub<DocumentProcessingHub>("/hubs/processing");

// GraphQL endpoint
app.MapGraphQL();

// Controllers
app.MapControllers();

// Tenant-scoped routes: /t/{tenantId}/...
app.MapControllerRoute(
    "tenant-default",
    "t/{tenantId}/{controller=Home}/{action=Index}/{id?}");

// Default route
app.MapControllerRoute(
    "default",
    "{controller=Home}/{action=Index}/{id?}");

// Database setup - use EnsureCreated for development simplicity
Log.Information("Setting up database...");
try
{
    if (multitenancyEnabled && !standaloneMode)
    {
        // Multi-tenancy mode: ensure tenant tables exist and provision default tenant
        Log.Information("Multi-tenancy enabled, ensuring tenant infrastructure...");

        // Step 1: Ensure tenant management tables exist (tenants table in public schema)
        await app.Services.EnsureTenantTablesAsync();
        Log.Information("Tenant management tables verified");

        // Step 2: Provision "default" tenant if it doesn't exist
        using var scope = app.Services.CreateScope();
        var provisioningService = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
        var defaultTenantExists = await provisioningService.ExistsAsync(TenantConstants.DefaultTenantId);

        if (!defaultTenantExists)
        {
            Log.Information("Provisioning default tenant schema...");
            await provisioningService.ProvisionAsync(
                TenantConstants.DefaultTenantId,
                "Default Tenant",
                plan: TenantPlans.Free);
            Log.Information("Default tenant provisioned successfully");
        }
        else
        {
            // Verify tenant is provisioned (schema exists)
            var tenant = await provisioningService.GetTenantAsync(TenantConstants.DefaultTenantId);
            if (tenant != null && !tenant.IsProvisioned)
            {
                Log.Information("Default tenant exists but not provisioned, migrating...");
                await provisioningService.MigrateTenantAsync(TenantConstants.DefaultTenantId);
            }

            Log.Information("Default tenant schema verified");
        }
    }
    else
    {
        // Non-multi-tenancy or standalone mode: use simple EnsureCreated
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

        // Check if documents table exists - use different query for SQLite vs PostgreSQL
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = standaloneMode
            ? "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='documents'"
            : "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'documents'";
        var tableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
        await conn.CloseAsync();

        if (!tableExists)
        {
            Log.Information("Documents table not found, creating schema...");
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            Log.Information("Database schema created");
        }
        else
        {
            Log.Information("Database schema verified");
        }
    }

    Log.Information("Database setup complete");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Failed to setup database");
    throw;
}

// Ensure upload directory exists
var uploadPath = standaloneMode
    ? Path.Combine(dataBasePath, "uploads")
    : ragConfig.UploadPath;
Directory.CreateDirectory(uploadPath);

// Ensure evidence storage directory exists
var evidenceConfig = builder.Configuration
    .GetSection(EvidenceStorageOptions.SectionName)
    .Get<EvidenceStorageOptions>() ?? new EvidenceStorageOptions();
var evidencePath = evidenceConfig.BasePath
                   ?? (standaloneMode
                       ? Path.Combine(dataBasePath, "evidence")
                       : Path.Combine(uploadPath, "evidence"));
Directory.CreateDirectory(evidencePath);

// Open browser in standalone mode
if (standaloneMode)
{
    var url = $"http://localhost:{port}";
    Log.Information("LucidRAG starting in standalone mode at {Url} with data path {DataPath}", url, dataBasePath);
    Console.WriteLine();
    Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║               lucidRAG - Standalone Mode                  ║");
    Console.WriteLine("╠════════════════════════════════════════════════════════════╣");
    Console.WriteLine($"║  URL:  {url,-52}║");
    Console.WriteLine($"║  Data: {dataBasePath,-52}║");
    Console.WriteLine("║  Press Ctrl+C to stop                                    ║");
    Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
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

// NOTE: ImagePipeline pre-warming disabled due to blocking DI issues with wave dependencies
// TODO: Fix the blocking issue in ImageSummarizer.Core wave resolution

app.Run();

// Helper to open browser cross-platform
static void OpenBrowser(string url)
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        Process.Start("xdg-open", url);
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) Process.Start("open", url);
}

// Make Program accessible for WebApplicationFactory in tests
public partial class Program
{
}