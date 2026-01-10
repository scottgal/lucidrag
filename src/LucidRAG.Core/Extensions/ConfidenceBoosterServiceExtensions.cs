using Microsoft.Extensions.DependencyInjection;
using LucidRAG.Core.Services.ConfidenceBooster;
using LucidRAG.Core.Services.ConfidenceBooster.Domain;

namespace LucidRAG.Core.Extensions;

/// <summary>
/// Service registration for ConfidenceBooster system.
/// </summary>
public static class ConfidenceBoosterServiceExtensions
{
    /// <summary>
    /// Add ConfidenceBooster services to the service collection.
    /// </summary>
    public static IServiceCollection AddConfidenceBooster(
        this IServiceCollection services,
        Action<ConfidenceBoosterConfig>? configure = null)
    {
        // Configuration
        var config = new ConfidenceBoosterConfig();
        configure?.Invoke(config);
        services.AddSingleton(config);

        // Core services
        services.AddScoped<ConfidenceBoosterCoordinator>();

        // Domain-specific boosters
        services.AddScoped<ImageConfidenceBooster>();
        // services.AddScoped<AudioConfidenceBooster>();  // Add when implemented
        // services.AddScoped<DocumentConfidenceBooster>();  // Add when implemented
        // services.AddScoped<DataConfidenceBooster>();  // Add when implemented

        // Background worker (if enabled)
        if (config.Enabled)
        {
            services.AddHostedService<ConfidenceBoosterBackgroundService>();
        }

        return services;
    }
}
