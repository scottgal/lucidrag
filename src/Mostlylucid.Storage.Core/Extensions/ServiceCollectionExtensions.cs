using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.Storage.Core.Abstractions;
using Mostlylucid.Storage.Core.Config;
using Mostlylucid.Storage.Core.Implementations;

namespace Mostlylucid.Storage.Core.Extensions;

/// <summary>
/// Dependency injection extensions for vector storage.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add vector store with default configuration (DuckDB for standalone mode).
    /// </summary>
    public static IServiceCollection AddVectorStore(this IServiceCollection services)
    {
        return services.AddVectorStore(_ => { });
    }

    /// <summary>
    /// Add vector store with custom configuration.
    /// </summary>
    public static IServiceCollection AddVectorStore(
        this IServiceCollection services,
        Action<VectorStoreOptions> configure)
    {
        // Register configuration
        services.Configure(configure);

        // Register factory that creates the appropriate implementation based on config
        services.AddSingleton<IVectorStore>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<VectorStoreOptions>>().Value;
            return CreateVectorStore(options, sp);
        });

        return services;
    }

    /// <summary>
    /// Add vector store for tool/MCP mode (InMemory, no persistence).
    /// </summary>
    public static IServiceCollection AddVectorStoreForToolMode(this IServiceCollection services)
    {
        return services.AddVectorStore(opt =>
        {
            var toolOptions = VectorStoreOptions.ForToolMode();
            opt.Backend = toolOptions.Backend;
            opt.PersistVectors = toolOptions.PersistVectors;
            opt.ReuseExistingEmbeddings = toolOptions.ReuseExistingEmbeddings;
            opt.CollectionName = toolOptions.CollectionName;
        });
    }

    /// <summary>
    /// Add vector store for standalone mode (DuckDB with persistence).
    /// </summary>
    public static IServiceCollection AddVectorStoreForStandaloneMode(
        this IServiceCollection services,
        string dataDirectory = "./data")
    {
        return services.AddVectorStore(opt =>
        {
            var standaloneOptions = VectorStoreOptions.ForStandaloneMode(dataDirectory);
            opt.Backend = standaloneOptions.Backend;
            opt.PersistVectors = standaloneOptions.PersistVectors;
            opt.ReuseExistingEmbeddings = standaloneOptions.ReuseExistingEmbeddings;
            opt.CollectionName = standaloneOptions.CollectionName;
            opt.DuckDB = standaloneOptions.DuckDB;
        });
    }

    /// <summary>
    /// Add vector store for production mode (Qdrant).
    /// </summary>
    public static IServiceCollection AddVectorStoreForProductionMode(
        this IServiceCollection services,
        string qdrantHost = "localhost",
        int qdrantPort = 6334)
    {
        return services.AddVectorStore(opt =>
        {
            var prodOptions = VectorStoreOptions.ForProductionMode(qdrantHost, qdrantPort);
            opt.Backend = prodOptions.Backend;
            opt.PersistVectors = prodOptions.PersistVectors;
            opt.ReuseExistingEmbeddings = prodOptions.ReuseExistingEmbeddings;
            opt.CollectionName = prodOptions.CollectionName;
            opt.Qdrant = prodOptions.Qdrant;
        });
    }

    /// <summary>
    /// Create the appropriate vector store implementation based on configuration.
    /// </summary>
    private static IVectorStore CreateVectorStore(VectorStoreOptions options, IServiceProvider serviceProvider)
    {
        return options.Backend switch
        {
            VectorStoreBackend.InMemory => CreateInMemoryStore(options, serviceProvider),
            VectorStoreBackend.DuckDB => CreateDuckDBStore(options, serviceProvider),
            VectorStoreBackend.Qdrant => CreateQdrantStore(options, serviceProvider),
            _ => throw new ArgumentException($"Unknown vector store backend: {options.Backend}")
        };
    }

    private static IVectorStore CreateInMemoryStore(VectorStoreOptions options, IServiceProvider serviceProvider)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<InMemoryVectorStore>>();
        var wrappedOptions = Options.Create(options);
        return new InMemoryVectorStore(wrappedOptions, logger);
    }

    private static IVectorStore CreateDuckDBStore(VectorStoreOptions options, IServiceProvider serviceProvider)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<DuckDBVectorStore>>();
        var wrappedOptions = Options.Create(options);
        return new DuckDBVectorStore(wrappedOptions, logger);
    }

    private static IVectorStore CreateQdrantStore(VectorStoreOptions options, IServiceProvider serviceProvider)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<QdrantVectorStore>>();
        var wrappedOptions = Options.Create(options);
        return new QdrantVectorStore(wrappedOptions, logger);
    }
}
