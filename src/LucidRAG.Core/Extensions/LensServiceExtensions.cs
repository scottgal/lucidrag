using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LucidRAG.Services.Lenses;

namespace LucidRAG.Extensions;

/// <summary>
/// Service registration extensions for the lens system.
/// </summary>
public static class LensServiceExtensions
{
    /// <summary>
    /// Registers lens services (loader, registry, renderer).
    /// Lenses are initialized on application startup.
    /// </summary>
    public static IServiceCollection AddLensSystem(this IServiceCollection services, IConfiguration configuration)
    {
        // Register lens services
        services.AddSingleton<ILensLoader, LensLoader>();
        services.AddSingleton<ILensRegistry, LensRegistry>();
        services.AddScoped<ILensRenderService, LensRenderService>();

        // Initialize lens registry on startup
        services.AddHostedService<LensRegistryInitializer>();

        return services;
    }
}

/// <summary>
/// Background service to initialize lens registry on application startup.
/// </summary>
internal class LensRegistryInitializer : IHostedService
{
    private readonly ILensRegistry _lensRegistry;
    private readonly ILogger<LensRegistryInitializer> _logger;

    public LensRegistryInitializer(
        ILensRegistry lensRegistry,
        ILogger<LensRegistryInitializer> logger)
    {
        _lensRegistry = lensRegistry;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Initializing lens registry...");

        try
        {
            await _lensRegistry.InitializeAsync(ct);
            _logger.LogInformation("Lens registry initialized successfully with {Count} lens(es)",
                _lensRegistry.AvailableLenses.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize lens registry");
            // Don't throw - allow app to start even if lenses fail to load
            // The default lens fallback will be used
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
