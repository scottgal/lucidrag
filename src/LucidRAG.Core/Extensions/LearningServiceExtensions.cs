using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using LucidRAG.Core.Services.Learning;
using LucidRAG.Core.Services.Learning.Handlers;

namespace LucidRAG.Core.Extensions;

/// <summary>
/// Service registration for Learning system.
/// Pattern: Like BotDetection learning pipeline - singleton coordinator, background service.
/// </summary>
public static class LearningServiceExtensions
{
    /// <summary>
    /// Add Learning services to the service collection.
    /// Only enables in hosted mode (not CLI).
    /// </summary>
    public static IServiceCollection AddLearning(
        this IServiceCollection services,
        Action<LearningConfig>? configure = null)
    {
        // Configuration
        var config = new LearningConfig();
        configure?.Invoke(config);

        // Only add if enabled AND in hosted mode
        if (!config.Enabled || !config.HostedModeOnly)
        {
            return services;
        }

        services.AddSingleton(config);

        // Singleton coordinator (like BotDetection's SignatureCoordinator)
        services.AddSingleton<ILearningCoordinator, LearningCoordinator>();

        // Scanner for finding learning candidates
        services.AddScoped<ILearningScanner, LearningScanner>();

        // Domain-specific learning handlers
        services.AddScoped<ILearningHandler, DocumentLearningHandler>();
        // TODO: Add other handlers when implemented
        // services.AddScoped<ILearningHandler, ImageLearningHandler>();
        // services.AddScoped<ILearningHandler, AudioLearningHandler>();
        // services.AddScoped<ILearningHandler, DataLearningHandler>();

        // Background service that runs the scans
        services.AddHostedService<LearningBackgroundService>();

        return services;
    }

    /// <summary>
    /// Add learning for DocSummarizer (hosted mode only).
    /// </summary>
    public static IServiceCollection AddDocSummarizerLearning(
        this IServiceCollection services,
        Action<LearningConfig>? configure = null)
    {
        return services.AddLearning(config =>
        {
            config.Enabled = true;
            config.HostedModeOnly = true;  // Force hosted mode only
            config.ScanInterval = TimeSpan.FromMinutes(30);
            config.ConfidenceThreshold = 0.75;

            configure?.Invoke(config);
        });
    }

    /// <summary>
    /// Add learning for LucidRAG web (hosted mode only).
    /// </summary>
    public static IServiceCollection AddLucidRagLearning(
        this IServiceCollection services,
        Action<LearningConfig>? configure = null)
    {
        return services.AddLearning(config =>
        {
            config.Enabled = true;
            config.HostedModeOnly = true;  // Force hosted mode only
            config.ScanInterval = TimeSpan.FromMinutes(60); // Less frequent for web
            config.ConfidenceThreshold = 0.70; // Lower threshold for web
            config.MinDocumentAge = TimeSpan.FromHours(2); // Wait longer before learning

            configure?.Invoke(config);
        });
    }

    /// <summary>
    /// Disable learning (for CLI mode).
    /// </summary>
    public static IServiceCollection DisableLearning(this IServiceCollection services)
    {
        services.AddSingleton(new LearningConfig { Enabled = false });
        return services;
    }
}
