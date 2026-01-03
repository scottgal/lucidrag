using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer.Anthropic.Config;
using Mostlylucid.DocSummarizer.Anthropic.Services;
using Mostlylucid.DocSummarizer.Services;

namespace Mostlylucid.DocSummarizer.Anthropic.Extensions;

/// <summary>
/// Extension methods for registering Anthropic services
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Anthropic Claude as the LLM backend
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Action to configure Anthropic options</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddDocSummarizerAnthropic(
        this IServiceCollection services,
        Action<AnthropicConfig> configure)
    {
        services.Configure(configure);
        RegisterServices(services);
        return services;
    }

    /// <summary>
    /// Adds Anthropic Claude as the LLM backend with configuration section binding
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configurationSection">Configuration section containing Anthropic settings</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddDocSummarizerAnthropic(
        this IServiceCollection services,
        IConfigurationSection configurationSection)
    {
        services.Configure<AnthropicConfig>(configurationSection);
        RegisterServices(services);
        return services;
    }

    private static void RegisterServices(IServiceCollection services)
    {
        services.AddHttpClient<AnthropicLlmService>();
        services.AddSingleton<ILlmService, AnthropicLlmService>();
    }
}
