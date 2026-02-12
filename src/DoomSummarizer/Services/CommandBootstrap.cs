using DoomSummarizer.Helpers;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Microsoft.Data.Sqlite;
using Mostlylucid.DocSummarizer.Services;
using Spectre.Console;
using OllamaService = DoomSummarizer.Services.OllamaService;
#if FEATURE_LLAMASHARP
using Mostlylucid.DocSummarizer.LLamaSharp.Config;
using Mostlylucid.DocSummarizer.LLamaSharp.Services;
#endif

namespace DoomSummarizer.Commands;

/// <summary>
///     Shared bootstrap for CLI commands. Creates the common service stack
///     (config, storage, embedding) and provides opt-in methods for LLM,
///     entity stores, and circuit breaker initialization.
/// </summary>
public sealed class CommandBootstrap : IAsyncDisposable
{
    public DoomConfig Config { get; }
    public string DbPath { get; }
    public StorageService Storage { get; }
    public IEmbeddingService Embedding { get; }

    // Vibe resolver (lens-aware, loaded eagerly)
    public VibeResolver VibeResolver { get; }

    // Opt-in services (initialized via methods below)
    public OllamaService? Ollama { get; private set; }
    public ApiKeyService? ApiKeys { get; private set; }
    public ApiBudgetService? ApiBudget { get; private set; }
    public LlmRouter? LlmRouter { get; private set; }
    public CircuitBreakerService? CircuitBreaker { get; private set; }
    public IItemVectorStore? VectorStore { get; private set; }
    public IEntityGraphStore? EntityStore { get; private set; }
#if FEATURE_LLAMASHARP
    public LLamaSharpLlmService? LLamaSharp { get; private set; }
#endif

    private CommandBootstrap(DoomConfig config, string dbPath, StorageService storage, IEmbeddingService embedding,
        VibeResolver vibeResolver)
    {
        Config = config;
        DbPath = dbPath;
        Storage = storage;
        Embedding = embedding;
        VibeResolver = vibeResolver;
    }

    /// <summary>
    ///     Create the core service stack: config → storage → embedding.
    /// </summary>
    /// <param name="gpuDeviceId">CLI override for GPU device (--gpu flag). Overrides config file value.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<CommandBootstrap> CreateAsync(int? gpuDeviceId = null, CancellationToken ct = default)
    {
        var config = await ConfigService.LoadAsync();

        // CLI --gpu flag overrides config file gpu_device_id for both embedding and LLamaSharp
        if (gpuDeviceId.HasValue)
        {
            config = config with { Embedding = config.Embedding with { GpuDeviceId = gpuDeviceId.Value } };
            config = config with
            {
                LlamaSharp = config.LlamaSharp with { GpuDeviceId = gpuDeviceId.Value }
            };
        }

        var dbPath = ConfigService.GetDbPath(config);

        var storage = new StorageService(dbPath);
        try
        {
            await storage.InitializeAsync();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5 /* SQLITE_BUSY */
                                         || ex.Message.Contains("database is locked",
                                             StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[red]Error: Database is locked by another instance.[/]");
            AnsiConsole.MarkupLine("[yellow]DoomSummarizer uses SQLite which supports single-writer access.[/]");
            AnsiConsole.MarkupLine(
                "[yellow]Please close other running instances, or use LucidRAG (PostgreSQL) for multi-user access.[/]");
            throw;
        }

        var embedding = await EmbeddingFactory.CreateAsync(config.Embedding, ct: ct);

        var vibeResolver = new VibeResolver(config);
        vibeResolver.LoadLenses(typeof(CommandBootstrap).Assembly);

        // Configure the shared query classifier with thresholds from config
        PromptInterpreter.ConfigureClassifier(config.Classifier);

        // Wire learning logger for sentinel disagreement capture
        PromptInterpreter.LearningLogger = storage;

        return new CommandBootstrap(config, dbPath, storage, embedding, vibeResolver);
    }

    /// <summary>
    ///     List available GPUs and ONNX execution providers.
    ///     Called when --list-gpus is set. Prints info and returns true (caller should exit).
    /// </summary>
    public static async Task<bool> ListGpusAsync()
    {
        AnsiConsole.MarkupLine("[cyan]GPU & Execution Provider Information[/]");
        AnsiConsole.WriteLine();

        // ONNX Runtime available providers
        try
        {
            var providers = Microsoft.ML.OnnxRuntime.OrtEnv.Instance().GetAvailableProviders();
            AnsiConsole.MarkupLine("[yellow]ONNX Runtime Providers:[/]");
            foreach (var provider in providers)
            {
                var icon = provider.Contains("DML") || provider.Contains("CUDA") || provider.Contains("TensorRT")
                    ? "[green]\u2713[/]" : "[grey]-[/]";
                AnsiConsole.MarkupLine($"  {icon} {Markup.Escape(provider)}");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Could not query ONNX providers: {Markup.Escape(ex.Message)}[/]");
        }

        AnsiConsole.WriteLine();

        // System GPU enumeration (Windows: WMI, cross-platform: nvidia-smi fallback)
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-NoProfile -Command \"Get-CimInstance -ClassName Win32_VideoController | " +
                                "Select-Object @{N='ID';E={$_.DeviceID}}, Name, " +
                                "@{N='VRAM_MB';E={[math]::Round($_.AdapterRAM/1MB)}}, " +
                                "DriverVersion, Status | Format-Table -AutoSize | Out-String -Width 200\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    var output = await proc.StandardOutput.ReadToEndAsync();
                    await proc.WaitForExitAsync();
                    AnsiConsole.MarkupLine("[yellow]System GPUs (Windows):[/]");
                    AnsiConsole.Write(new Text(output.Trim()));
                    AnsiConsole.WriteLine();
                }
            }
            catch
            {
                // PowerShell not available — try basic approach
            }
        }

        // nvidia-smi (works on all platforms with NVIDIA drivers)
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "-L",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                var output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();
                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    AnsiConsole.MarkupLine("[yellow]NVIDIA GPUs:[/]");
                    AnsiConsole.Write(new Text(output.Trim()));
                    AnsiConsole.WriteLine();
                }
            }
        }
        catch
        {
            // nvidia-smi not available
        }

        // Show current config
        AnsiConsole.WriteLine();
        var config = await ConfigService.LoadAsync();
        AnsiConsole.MarkupLine("[yellow]Current Config:[/]");
        AnsiConsole.MarkupLine($"  Embedding GPU device: [green]{config.Embedding.GpuDeviceId}[/]");
        AnsiConsole.MarkupLine($"  Embedding backend:    [green]{Markup.Escape(config.Embedding.Backend)}[/]");
        AnsiConsole.MarkupLine($"  Embedding rate:       [green]{config.Ingestion.EmbeddingRate}%[/]");
        if (config.LlamaSharp.GpuDeviceId.HasValue)
            AnsiConsole.MarkupLine($"  LLamaSharp GPU device:[green]{config.LlamaSharp.GpuDeviceId}[/]");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Tip: Use --gpu <id> to select a specific GPU device.[/]");
        AnsiConsole.MarkupLine("[grey]     Set embedding.gpu_device_id in config for persistent override.[/]");

        return true;
    }

    /// <summary>
    ///     Create an OllamaService wired to config.
    /// </summary>
    public OllamaService CreateOllama()
    {
        Ollama = new OllamaService(Config.Ollama);
        return Ollama;
    }

#if FEATURE_LLAMASHARP
    /// <summary>
    /// Create a LLamaSharp local LLM service for zero-config GGUF inference.
    /// Applies DoomConfig.LlamaSharp overrides from config profiles.
    /// Returns null if LLamaSharp is disabled in config or fails to initialize.
    /// </summary>
    public LLamaSharpLlmService? CreateLLamaSharp()
    {
        try
        {
            var llamaConfig = ApplyLlamaSharpOverrides(new LLamaSharpConfig(), Config.LlamaSharp);
            if (!llamaConfig.Enabled) return null;

            var downloader = new LLamaSharpModelDownloader(llamaConfig);
            LLamaSharp = new LLamaSharpLlmService(llamaConfig, downloader);
            return LLamaSharp;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LLamaSharp init failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Apply DoomConfig.LlamaSharp profile overrides onto a LLamaSharpConfig.
    /// Only non-null fields in the override section are applied.
    /// </summary>
    private static LLamaSharpConfig ApplyLlamaSharpOverrides(LLamaSharpConfig baseConfig, Models.LlamaSharpConfigSection overrides)
    {
        return baseConfig with
        {
            Enabled = overrides.Enabled ?? baseConfig.Enabled,
            SynthesisModel = overrides.SynthesisModel ?? baseConfig.SynthesisModel,
            SentinelModel = overrides.SentinelModel ?? baseConfig.SentinelModel,
            ContextSize = overrides.ContextSize ?? baseConfig.ContextSize,
            GpuLayerCount = overrides.GpuLayerCount ?? baseConfig.GpuLayerCount,
            GpuDeviceId = overrides.GpuDeviceId ?? baseConfig.GpuDeviceId,
            BatchSize = overrides.BatchSize ?? baseConfig.BatchSize,
        };
    }
#endif

    /// <summary>
    ///     Initialize the full LLM stack: API keys → rate limiter → budget → router.
    ///     Provider priority: Ollama (if running) → LLamaSharp (GPU fallback, complete builds only) → Cloud.
    /// </summary>
    public async Task<LlmRouter> InitializeLlmStackAsync(
        CircuitBreakerService? circuitBreaker = null,
        CancellationToken ct = default)
    {
        ApiKeys = ApiKeyService.Load(Config.Keys);
        ApiRateLimiter.Configure(ApiKeys);

        ApiBudget = new ApiBudgetService(Config.ApiBudget, ApiKeys, DbPath);
        await ApiBudget.InitializeAsync();

#if FEATURE_LLAMASHARP
        // Create LLamaSharp as fallback provider (used when Ollama isn't running)
        var llamaSharp = LLamaSharp ?? CreateLLamaSharp();
#else
        ILlmService? llamaSharp = null;

        // Only mention LLamaSharp when no Ollama model is configured (user might need it)
        if (string.IsNullOrEmpty(Config.Ollama.Model))
        {
            var ls = Config.LlamaSharp;
            if (ls.Enabled != false &&
                (ls.ContextSize != null || ls.GpuLayerCount != null || ls.SynthesisModel != null))
                AnsiConsole.MarkupLine(
                    "[yellow]Note:[/] Config has LLamaSharp settings but this build doesn't include it. Use [bold cyan]lucidrag[/] for local GGUF support.");
        }
#endif

        LlmRouter = await LlmRouter.BuildAsync(
            Config.Ollama, ApiKeys, ApiBudget, circuitBreaker,
            llamaSharp, ct);

        if (Ollama != null)
            Ollama.Router = LlmRouter;

        return LlmRouter;
    }

    /// <summary>
    ///     Initialize persistent circuit breaker and wire into rate limiter.
    /// </summary>
    public async Task<CircuitBreakerService> InitializeCircuitBreakerAsync()
    {
        CircuitBreaker = new CircuitBreakerService(DbPath);
        await CircuitBreaker.InitializeAsync();
        ApiRateLimiter.SetCircuitBreaker(CircuitBreaker);
        return CircuitBreaker;
    }

    /// <summary>
    ///     Initialize vector store and entity graph store.
    ///     Backend selected by Config.Storage.VectorBackend:
    ///     "sqlite-vec" → SqliteVecItemVectorStore + SQLite entity graph (StorageService)
    ///     "duckdb" (default) → DuckDbVectorStore + DuckDbEntityGraphStore (shared connection)
    /// </summary>
    public async Task InitializeEntityStoresAsync()
    {
        var backend = Config.Storage.VectorBackend;

        if (string.Equals(backend, "sqlite-vec", StringComparison.OrdinalIgnoreCase))
        {
            // sqlite-vec: lightweight brute-force vector search
            var sqliteVecPath = Path.ChangeExtension(
                ConfigService.GetVectorDbPath(Config), ".vec.db");
            var sqliteVecStore = new SqliteVecItemVectorStore(sqliteVecPath);
            await sqliteVecStore.InitializeAsync();

            // One-time migration from DuckDB if needed
            var duckDbPath = ConfigService.GetVectorDbPath(Config);
            var migrated = await VectorStoreMigration.MigrateIfNeededAsync(duckDbPath, sqliteVecStore);
            if (migrated > 0)
                AnsiConsole.MarkupLine($"[green]Migrated {migrated} embeddings from DuckDB to sqlite-vec[/]");

            VectorStore = sqliteVecStore;
            // Entity graph uses SQLite (StorageService) — no DuckDB connection sharing needed
            EntityStore = new SqliteEntityGraphStore(Storage);
            await EntityStore.InitializeAsync();
        }
        else
        {
            // DuckDB: HNSW-indexed vector search (default)
            var vectorDbPath = ConfigService.GetVectorDbPath(Config);
            var duckStore = new DuckDbVectorStore(vectorDbPath);
            await duckStore.InitializeAsync();
            VectorStore = duckStore;
            // Share the VectorStore's connection — DuckDB.NET doesn't support
            // multiple connections to the same file in the same process.
            EntityStore = new DuckDbEntityGraphStore(duckStore.Connection!);
            await EntityStore.InitializeAsync();
        }
    }

    /// <summary>
    ///     Initialize only the entity graph store.
    ///     Shares VectorStore's connection if available; opens its own otherwise.
    /// </summary>
    public async Task<IEntityGraphStore> InitializeEntityGraphStoreAsync()
    {
        var backend = Config.Storage.VectorBackend;

        if (string.Equals(backend, "sqlite-vec", StringComparison.OrdinalIgnoreCase))
        {
            EntityStore = new SqliteEntityGraphStore(Storage);
        }
        else if (VectorStore?.GetUnderlyingConnection() is DuckDB.NET.Data.DuckDBConnection duckConn)
        {
            EntityStore = new DuckDbEntityGraphStore(duckConn);
        }
        else
        {
            var vectorDbPath = ConfigService.GetVectorDbPath(Config);
            EntityStore = new DuckDbEntityGraphStore(vectorDbPath);
        }

        await EntityStore.InitializeAsync();
        return EntityStore;
    }

    /// <summary>
    ///     Safely initialize entity graph store. Returns null if unavailable (non-fatal).
    /// </summary>
    public async Task<IEntityGraphStore?> TryInitializeEntityGraphStoreAsync()
    {
        try
        {
            return await InitializeEntityGraphStoreAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Entity graph store init failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Initialize LLM stack, entity store, print availability warnings, and run the ask loop.
    ///     Consolidates the repeated pattern across AskCommand, ManCommand, and CrawlCommand.
    /// </summary>
    public async Task<int> StartAskLoopAsync(
        InteractiveAskOptions options,
        CancellationToken ct = default)
    {
        var ollama = CreateOllama();
        var llmRouter = await InitializeLlmStackAsync(ct: ct);

        await TryInitializeEntityGraphStoreAsync();

        var ollamaAvailable = await ollama.IsAvailableAsync();
        var hasCloudLlm = llmRouter.HasCloudProvider;
#if FEATURE_LLAMASHARP
        var hasLlamaSharp = LLamaSharp != null && await LLamaSharp.IsAvailableAsync();
#else
        var hasLlamaSharp = false;
#endif
        if (ollamaAvailable)
        {
            AnsiConsole.MarkupLine($"[green]LLM:[/] {FormattingHelpers.Esc(llmRouter.StatusDescription)}");
        }
        else if (hasLlamaSharp)
        {
            AnsiConsole.MarkupLine(
                "[cyan]Ollama not running — using local GGUF (LLamaSharp, first call loads model)[/]");
        }
        else if (hasCloudLlm)
        {
            AnsiConsole.MarkupLine("[cyan]Ollama not available — using cloud LLM provider[/]");
        }
        else
        {
            AnsiConsole.MarkupLine(
                "[yellow]No LLM available (Ollama down, no cloud keys).[/] Answers will be limited to evidence listing.");
            AnsiConsole.MarkupLine("[grey]Start Ollama: ollama serve  —or—  set OPENAI_API_KEY / ANTHROPIC_API_KEY[/]");
        }

        var loop = new InteractiveAskLoop(this, ollama, llmRouter, ollamaAvailable, options);
        return await loop.RunAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (EntityStore != null) await EntityStore.DisposeAsync();
        if (VectorStore != null) await VectorStore.DisposeAsync();
        if (CircuitBreaker != null) await CircuitBreaker.DisposeAsync();
        if (ApiBudget != null) await ApiBudget.DisposeAsync();
#if FEATURE_LLAMASHARP
        LLamaSharp?.Dispose();
#endif
        (Embedding as IDisposable)?.Dispose();
        await Storage.DisposeAsync();
    }
}