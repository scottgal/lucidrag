using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Core.Services;
using Mostlylucid.DocSummarizer.Pipeline;
using Mostlylucid.DocSummarizer.Scanning;
using Mostlylucid.DocSummarizer.Scanning.Waves;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Deduplication;
using Mostlylucid.DocSummarizer.Services.Onnx;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace Mostlylucid.DocSummarizer.Extensions;

/// <summary>
///     Extension methods for registering DocSummarizer services with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Adds DocSummarizer services to the service collection with default configuration.
    ///     Uses ONNX embeddings (local, no external services required) and BertRag mode.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    ///     <code>
    /// // In Program.cs or Startup.cs
    /// builder.Services.AddDocSummarizer();
    /// 
    /// // Then inject IDocumentSummarizer in your services
    /// public class MyService(IDocumentSummarizer summarizer)
    /// {
    ///     public async Task&lt;string&gt; SummarizeAsync(string markdown)
    ///     {
    ///         var result = await summarizer.SummarizeMarkdownAsync(markdown);
    ///         return result.ExecutiveSummary;
    ///     }
    /// }
    /// </code>
    /// </example>
    public static IServiceCollection AddDocSummarizer(this IServiceCollection services)
    {
        return services.AddDocSummarizer(_ => { });
    }

    /// <summary>
    ///     Adds DocSummarizer services with custom configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Action to configure the options.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    ///     <code>
    /// builder.Services.AddDocSummarizer(options =>
    /// {
    ///     // Use Ollama for embeddings instead of local ONNX
    ///     options.EmbeddingBackend = EmbeddingBackend.Ollama;
    ///     options.Ollama.BaseUrl = "http://localhost:11434";
    ///     options.Ollama.Model = "llama3.2:3b";
    /// 
    ///     // Configure vector storage (Qdrant recommended for production)
    ///     options.BertRag.VectorStore = VectorStoreBackend.Qdrant;
    ///     options.BertRag.ReindexOnStartup = false; // Production setting
    /// 
    ///     // Verbose logging during development
    ///     options.Output.Verbose = true;
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddDocSummarizer(
        this IServiceCollection services,
        Action<DocSummarizerConfig> configure)
    {
        // Register configuration
        services.Configure(configure);

        // Register core services
        services.TryAddSingleton<IEmbeddingService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DocSummarizerConfig>>().Value;
            var logger = sp.GetService<ILogger<IEmbeddingService>>();

            return config.EmbeddingBackend == EmbeddingBackend.Onnx
                ? CreateOnnxEmbeddingService(config.Onnx, config.Output.Verbose)
                : CreateOllamaEmbeddingService(config.Ollama);
        });

        services.TryAddSingleton<IVectorStore>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DocSummarizerConfig>>().Value;
            return CreateVectorStore(config);
        });

        // Register LLM service based on configured backend
        services.TryAddSingleton<ILlmService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DocSummarizerConfig>>().Value;
            return CreateLlmService(config);
        });

        services.TryAddSingleton<IDocumentSummarizer, DocumentSummarizerService>();

        // Register deduplication service with config from DocSummarizerConfig
        services.TryAddSingleton<IOptions<DeduplicationConfig>>(sp =>
        {
            var docConfig = sp.GetRequiredService<IOptions<DocSummarizerConfig>>().Value;
            return Options.Create(docConfig.Deduplication);
        });
        services.TryAddSingleton<IDeduplicationService, DeduplicationService>();

        // Register the startup initializer as a hosted service
        services.AddHostedService<DocSummarizerInitializer>();

        // Register the pipeline for unified pipeline registry
        services.TryAddSingleton<DocumentPipeline>();
        services.AddSingleton<IPipeline>(sp => sp.GetRequiredService<DocumentPipeline>());

        // Register PDF to Markdown conversion services
        services.TryAddSingleton<PdfPageRenderer>(sp =>
        {
            var logger = sp.GetService<ILogger<PdfPageRenderer>>();
            return new PdfPageRenderer(logger);
        });

        // Register VLM OCR service for vision-based document extraction
        services.TryAddSingleton<IVlmOcrService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DocSummarizerConfig>>().Value;
            var logger = sp.GetService<ILogger<VlmOcrService>>();
            return new VlmOcrService(config.VlmOcr, logger);
        });

        services.TryAddSingleton<DocumentToMarkdownService>(sp =>
        {
            var pdfRenderer = sp.GetRequiredService<PdfPageRenderer>();
            var tableFactory = sp.GetService<ITableExtractorFactory>();
            var logger = sp.GetService<ILogger<DocumentToMarkdownService>>();
            var vlmOcrService = sp.GetService<IVlmOcrService>();
            var config = sp.GetRequiredService<IOptions<DocSummarizerConfig>>().Value;

            var service = new DocumentToMarkdownService(pdfRenderer, tableFactory, logger)
            {
                MinTextDensityForNative = config.VlmOcr.MinTextDensityForNative
            };

            // Wire up VLM OCR callback if service is available
            if (vlmOcrService != null)
            {
                service.VlmOcrCallback = vlmOcrService.OcrImageToMarkdownAsync;
            }

            return service;
        });

        // Register document handler registry with enhanced handlers for PDF/image OCR
        // Docling handler is registered last with highest priority when enabled
        services.TryAddSingleton<IDocumentHandlerRegistry>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DocSummarizerConfig>>();
            var markdownService = sp.GetRequiredService<DocumentToMarkdownService>();
            var pdfLogger = sp.GetService<ILogger<EnhancedPdfDocumentHandler>>();
            var imageLogger = sp.GetService<ILogger<VlmImageOcrHandler>>();
            var doclingLogger = sp.GetService<ILogger<DoclingDocumentHandler>>();

            var registry = new DocumentHandlerRegistry();
            registry.RegisterDefaultHandlers();
            registry.RegisterEnhancedHandlers(markdownService, pdfLogger, imageLogger);
            registry.RegisterDoclingHandlers(config, doclingLogger); // Highest priority when enabled
            return registry;
        });

        // Register document wave scanning system
        RegisterDocumentWaveServices(services);

        return services;
    }

    /// <summary>
    ///     Adds DocSummarizer services bound to a configuration section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configurationSection">The configuration section containing DocSummarizer settings.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    ///     <code>
    /// // In appsettings.json:
    /// // {
    /// //   "DocSummarizer": {
    /// //     "EmbeddingBackend": "Onnx",
    /// //     "BertRag": {
    /// //       "ReindexOnStartup": false
    /// //     }
    /// //   }
    /// // }
    /// 
    /// builder.Services.AddDocSummarizer(
    ///     builder.Configuration.GetSection("DocSummarizer"));
    /// </code>
    /// </example>
    public static IServiceCollection AddDocSummarizer(
        this IServiceCollection services,
        IConfigurationSection configurationSection)
    {
        services.Configure<DocSummarizerConfig>(configurationSection);

        // Register core services (same as above)
        services.TryAddSingleton<IEmbeddingService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DocSummarizerConfig>>().Value;
            return config.EmbeddingBackend == EmbeddingBackend.Onnx
                ? CreateOnnxEmbeddingService(config.Onnx, config.Output.Verbose)
                : CreateOllamaEmbeddingService(config.Ollama);
        });

        services.TryAddSingleton<IVectorStore>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DocSummarizerConfig>>().Value;
            return CreateVectorStore(config);
        });

        // Register LLM service based on configured backend
        services.TryAddSingleton<ILlmService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DocSummarizerConfig>>().Value;
            return CreateLlmService(config);
        });

        services.TryAddSingleton<IDocumentSummarizer, DocumentSummarizerService>();

        // Register deduplication service with config from DocSummarizerConfig
        services.TryAddSingleton<IOptions<DeduplicationConfig>>(sp =>
        {
            var docConfig = sp.GetRequiredService<IOptions<DocSummarizerConfig>>().Value;
            return Options.Create(docConfig.Deduplication);
        });
        services.TryAddSingleton<IDeduplicationService, DeduplicationService>();

        services.AddHostedService<DocSummarizerInitializer>();

        // Register the pipeline for unified pipeline registry
        services.TryAddSingleton<DocumentPipeline>();
        services.AddSingleton<IPipeline>(sp => sp.GetRequiredService<DocumentPipeline>());

        // Register PDF to Markdown conversion services
        services.TryAddSingleton<PdfPageRenderer>(sp =>
        {
            var logger = sp.GetService<ILogger<PdfPageRenderer>>();
            return new PdfPageRenderer(logger);
        });

        // Register VLM OCR service for vision-based document extraction
        services.TryAddSingleton<IVlmOcrService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DocSummarizerConfig>>().Value;
            var logger = sp.GetService<ILogger<VlmOcrService>>();
            return new VlmOcrService(config.VlmOcr, logger);
        });

        services.TryAddSingleton<DocumentToMarkdownService>(sp =>
        {
            var pdfRenderer = sp.GetRequiredService<PdfPageRenderer>();
            var tableFactory = sp.GetService<ITableExtractorFactory>();
            var logger = sp.GetService<ILogger<DocumentToMarkdownService>>();
            var vlmOcrService = sp.GetService<IVlmOcrService>();
            var config = sp.GetRequiredService<IOptions<DocSummarizerConfig>>().Value;

            var service = new DocumentToMarkdownService(pdfRenderer, tableFactory, logger)
            {
                MinTextDensityForNative = config.VlmOcr.MinTextDensityForNative
            };

            // Wire up VLM OCR callback if service is available
            if (vlmOcrService != null)
            {
                service.VlmOcrCallback = vlmOcrService.OcrImageToMarkdownAsync;
            }

            return service;
        });

        // Register document handler registry with enhanced handlers for PDF/image OCR
        // Docling handler is registered last with highest priority when enabled
        services.TryAddSingleton<IDocumentHandlerRegistry>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DocSummarizerConfig>>();
            var markdownService = sp.GetRequiredService<DocumentToMarkdownService>();
            var pdfLogger = sp.GetService<ILogger<EnhancedPdfDocumentHandler>>();
            var imageLogger = sp.GetService<ILogger<VlmImageOcrHandler>>();
            var doclingLogger = sp.GetService<ILogger<DoclingDocumentHandler>>();

            var registry = new DocumentHandlerRegistry();
            registry.RegisterDefaultHandlers();
            registry.RegisterEnhancedHandlers(markdownService, pdfLogger, imageLogger);
            registry.RegisterDoclingHandlers(config, doclingLogger); // Highest priority when enabled
            return registry;
        });

        // Register document wave scanning system
        RegisterDocumentWaveServices(services);

        return services;
    }

    /// <summary>
    /// Registers the document wave scanning system services.
    /// Includes wave manifest loader, escalation service, orchestrator, and individual waves.
    /// </summary>
    private static void RegisterDocumentWaveServices(IServiceCollection services)
    {
        // Register wave manifest loader (singleton - loads YAML manifests once)
        services.TryAddSingleton<DocumentWaveManifestLoader>(sp =>
        {
            var loader = new DocumentWaveManifestLoader();

            // Load embedded manifests from assembly resources
            loader.LoadEmbeddedManifests();
            loader.LoadEmbeddedPipelines();

            return loader;
        });

        // Register document escalation configuration
        services.TryAddSingleton<IOptions<DocumentEscalationConfig>>(sp =>
        {
            var docConfig = sp.GetService<IOptions<DocSummarizerConfig>>()?.Value;
            var config = new DocumentEscalationConfig
            {
                Enabled = true,
                MinTextDensity = docConfig?.VlmOcr.MinTextDensityForNative ?? 100,
                PreferredVlmModel = docConfig?.VlmOcr.Model ?? "minicpm-v:8b"
            };
            return Options.Create(config);
        });

        // Register escalation service
        services.TryAddSingleton<IDocumentEscalationService, DocumentEscalationService>();

        // Register individual waves as IDocumentWave
        services.TryAddSingleton<NativeExtractionWave>();
        services.AddSingleton<IDocumentWave>(sp => sp.GetRequiredService<NativeExtractionWave>());

        services.TryAddSingleton<VlmOcrWave>(sp =>
        {
            var vlmOcrService = sp.GetRequiredService<IVlmOcrService>();
            var pdfRenderer = sp.GetRequiredService<PdfPageRenderer>();
            var escalationService = sp.GetRequiredService<IDocumentEscalationService>();
            var logger = sp.GetService<ILogger<VlmOcrWave>>();
            return new VlmOcrWave(vlmOcrService, pdfRenderer, escalationService, logger);
        });
        services.AddSingleton<IDocumentWave>(sp => sp.GetRequiredService<VlmOcrWave>());

        // Register wave orchestrator
        services.TryAddSingleton<DocumentWaveOrchestrator>(sp =>
        {
            var waves = sp.GetServices<IDocumentWave>();
            var escalationService = sp.GetRequiredService<IDocumentEscalationService>();
            var logger = sp.GetService<ILogger<DocumentWaveOrchestrator>>();
            return new DocumentWaveOrchestrator(waves, escalationService, logger);
        });
    }

    private static IEmbeddingService CreateOnnxEmbeddingService(OnnxConfig config, bool verbose)
    {
        return new OnnxEmbeddingService(config, verbose);
    }

    private static IEmbeddingService CreateOllamaEmbeddingService(OllamaConfig config)
    {
        var ollamaService = new OllamaService(
            config.Model,
            config.EmbedModel,
            config.BaseUrl,
            TimeSpan.FromSeconds(config.TimeoutSeconds),
            classifierModel: config.ClassifierModel
        );
        return new OllamaEmbeddingService(ollamaService);
    }

    private static IVectorStore CreateVectorStore(DocSummarizerConfig config)
    {
        return config.BertRag.VectorStore switch
        {
            VectorStoreBackend.InMemory => new InMemoryVectorStore(),
            VectorStoreBackend.Qdrant => CreateQdrantStore(config),
            _ => new InMemoryVectorStore()
        };
    }

    private static IVectorStore CreateQdrantStore(DocSummarizerConfig config)
    {
        return new QdrantVectorStore(config.Qdrant, config.Output.Verbose);
    }

    private static ILlmService CreateLlmService(DocSummarizerConfig config)
    {
        // For Ollama backend (default), create Ollama LLM service
        // Other backends (Anthropic, OpenAI) are registered by their respective extension projects
        if (config.LlmBackend == LlmBackend.Ollama)
        {
            var ollamaService = new OllamaService(
                config.Ollama.Model,
                config.Ollama.EmbedModel,
                config.Ollama.BaseUrl,
                TimeSpan.FromSeconds(config.Ollama.TimeoutSeconds),
                classifierModel: config.Ollama.ClassifierModel
            );
            return new OllamaLlmService(ollamaService, config.Ollama);
        }

        // For None or external backends, return a no-op service
        // External backends (Anthropic, OpenAI) will override this registration
        return new NullLlmService();
    }
}