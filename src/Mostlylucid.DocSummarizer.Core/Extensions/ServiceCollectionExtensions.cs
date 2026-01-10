using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Pipeline;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace Mostlylucid.DocSummarizer.Extensions;

/// <summary>
/// Extension methods for registering DocSummarizer services with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds DocSummarizer services to the service collection with default configuration.
    /// Uses ONNX embeddings (local, no external services required) and BertRag mode.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    /// <code>
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
    /// Adds DocSummarizer services with custom configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Action to configure the options.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    /// <code>
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

        // Register document handler registry (extensibility point for new file types)
        services.TryAddSingleton<IDocumentHandlerRegistry, DocumentHandlerRegistry>();

        services.TryAddSingleton<IDocumentSummarizer, DocumentSummarizerService>();

        // Register the startup initializer as a hosted service
        services.AddHostedService<DocSummarizerInitializer>();

        // Register the pipeline for unified pipeline registry
        services.TryAddSingleton<DocumentPipeline>();
        services.AddSingleton<IPipeline>(sp => sp.GetRequiredService<DocumentPipeline>());

        return services;
    }

    /// <summary>
    /// Adds DocSummarizer services bound to a configuration section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configurationSection">The configuration section containing DocSummarizer settings.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    /// <code>
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
        Microsoft.Extensions.Configuration.IConfigurationSection configurationSection)
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

        // Register document handler registry (extensibility point for new file types)
        services.TryAddSingleton<IDocumentHandlerRegistry, DocumentHandlerRegistry>();

        services.TryAddSingleton<IDocumentSummarizer, DocumentSummarizerService>();
        services.AddHostedService<DocSummarizerInitializer>();

        // Register the pipeline for unified pipeline registry
        services.TryAddSingleton<DocumentPipeline>();
        services.AddSingleton<IPipeline>(sp => sp.GetRequiredService<DocumentPipeline>());

        return services;
    }

    private static IEmbeddingService CreateOnnxEmbeddingService(OnnxConfig config, bool verbose)
    {
        return new Services.Onnx.OnnxEmbeddingService(config, verbose);
    }

    private static IEmbeddingService CreateOllamaEmbeddingService(OllamaConfig config)
    {
        var ollamaService = new OllamaService(
            model: config.Model,
            embedModel: config.EmbedModel,
            baseUrl: config.BaseUrl,
            timeout: TimeSpan.FromSeconds(config.TimeoutSeconds),
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
                model: config.Ollama.Model,
                embedModel: config.Ollama.EmbedModel,
                baseUrl: config.Ollama.BaseUrl,
                timeout: TimeSpan.FromSeconds(config.Ollama.TimeoutSeconds),
                classifierModel: config.Ollama.ClassifierModel
            );
            return new OllamaLlmService(ollamaService, config.Ollama);
        }

        // For None or external backends, return a no-op service
        // External backends (Anthropic, OpenAI) will override this registration
        return new NullLlmService();
    }
}
