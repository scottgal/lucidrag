using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer.OpenAI.Config;
using Mostlylucid.DocSummarizer.OpenAI.Services;
using Mostlylucid.DocSummarizer.Services;

namespace Mostlylucid.DocSummarizer.OpenAI.Extensions;

/// <summary>
/// Extension methods for registering OpenAI services
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds OpenAI as the LLM and embedding backend
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Action to configure OpenAI options</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddDocSummarizerOpenAI(
        this IServiceCollection services,
        Action<OpenAIConfig> configure)
    {
        services.Configure(configure);
        RegisterServices(services);
        return services;
    }

    /// <summary>
    /// Adds OpenAI as the LLM and embedding backend with configuration section binding
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configurationSection">Configuration section containing OpenAI settings</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddDocSummarizerOpenAI(
        this IServiceCollection services,
        IConfigurationSection configurationSection)
    {
        services.Configure<OpenAIConfig>(configurationSection);
        RegisterServices(services);
        return services;
    }

    /// <summary>
    /// Adds only OpenAI embeddings (keeping another LLM backend)
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Action to configure OpenAI options</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddDocSummarizerOpenAIEmbeddings(
        this IServiceCollection services,
        Action<OpenAIConfig> configure)
    {
        services.Configure(configure);
        services.AddHttpClient<OpenAIEmbeddingService>();
        services.AddSingleton<IEmbeddingService, OpenAIEmbeddingService>();
        return services;
    }

    private static void RegisterServices(IServiceCollection services)
    {
        services.AddHttpClient<OpenAILlmService>();
        services.AddHttpClient<OpenAIEmbeddingService>();
        services.AddSingleton<ILlmService, OpenAILlmService>();
        services.AddSingleton<IEmbeddingService, OpenAIEmbeddingService>();
    }
}
