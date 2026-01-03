using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Services;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.DocSummarizer.Services;

namespace Mostlylucid.DocSummarizer.Images.Extensions;

/// <summary>
/// Extension methods for registering DocSummarizer.Images services
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds image analysis services to the service collection with default configuration.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddDocSummarizerImages(this IServiceCollection services)
    {
        return services.AddDocSummarizerImages(_ => { });
    }

    /// <summary>
    /// Adds image analysis services with custom configuration.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Action to configure image options</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddDocSummarizerImages(
        this IServiceCollection services,
        Action<ImageConfig> configure)
    {
        services.Configure(configure);
        RegisterCoreServices(services);
        return services;
    }

    /// <summary>
    /// Adds image analysis services bound to a configuration section.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configurationSection">Configuration section containing image settings</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddDocSummarizerImages(
        this IServiceCollection services,
        IConfigurationSection configurationSection)
    {
        services.Configure<ImageConfig>(configurationSection);
        RegisterCoreServices(services);
        return services;
    }

    private static void RegisterCoreServices(IServiceCollection services)
    {
        // Sub-analyzers
        services.TryAddSingleton<ColorAnalyzer>();
        services.TryAddSingleton<EdgeAnalyzer>();
        services.TryAddSingleton<BlurAnalyzer>();
        services.TryAddSingleton<TextLikelinessAnalyzer>();

        // Main analyzer
        services.TryAddSingleton<IImageAnalyzer, ImageAnalyzer>();

        // Register image document handler with the handler registry
        services.AddSingleton<IDocumentHandler, ImageDocumentHandler>();
    }
}
