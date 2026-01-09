using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer.Images.Orchestration.Contributors;

namespace Mostlylucid.DocSummarizer.Images.Orchestration;

/// <summary>
///     DI registration for the new orchestration system.
/// </summary>
public static class OrchestrationServiceExtensions
{
    /// <summary>
    ///     Add the image analysis orchestration system with all contributors.
    /// </summary>
    public static IServiceCollection AddImageAnalysisOrchestration(
        this IServiceCollection services,
        Action<ImageAnalysisOptions>? configureOptions = null)
    {
        // Register options
        var options = new ImageAnalysisOptions();
        configureOptions?.Invoke(options);
        services.AddSingleton(options);

        // Register config provider
        services.AddSingleton<IWaveConfigProvider, WaveConfigProvider>();

        // Register orchestrator
        services.AddScoped<ImageAnalysisOrchestrator>();

        // Register all contributors (waves)
        RegisterContributors(services);

        return services;
    }

    /// <summary>
    ///     Add a custom contributing wave.
    /// </summary>
    public static IServiceCollection AddContributingWave<TWave>(this IServiceCollection services)
        where TWave : class, IContributingWave
    {
        services.AddScoped<IContributingWave, TWave>();
        return services;
    }

    private static void RegisterContributors(IServiceCollection services)
    {
        // Foundation waves (no dependencies)
        services.AddScoped<IContributingWave, ColorContributor>();

        // Vision waves (with early exit support)
        services.AddScoped<IContributingWave, Florence2Contributor>();

        // TODO: Add more contributors as they are converted:
        // services.AddScoped<IContributingWave, IdentityContributor>();
        // services.AddScoped<IContributingWave, StructureContributor>();
        // services.AddScoped<IContributingWave, TextDetectionContributor>();
        // services.AddScoped<IContributingWave, OcrContributor>();
        // services.AddScoped<IContributingWave, VisionLlmContributor>();
        // services.AddScoped<IContributingWave, MotionContributor>();
        // etc.
    }
}

/// <summary>
///     Extension to add singleton only if not already registered.
/// </summary>
internal static class ServiceCollectionExtensions
{
    public static void TryAddSingleton<TService>(this IServiceCollection services)
        where TService : class
    {
        if (!services.Any(sd => sd.ServiceType == typeof(TService)))
        {
            services.AddSingleton<TService>();
        }
    }
}
