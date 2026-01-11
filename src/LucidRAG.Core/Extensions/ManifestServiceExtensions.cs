using LucidRAG.Manifests;
using LucidRAG.Services.Lenses;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace LucidRAG.Extensions;

/// <summary>
/// Service registration extensions for YAML manifest-based systems.
/// Supports lenses, waves, and processors.
/// </summary>
public static class ManifestServiceExtensions
{
    /// <summary>
    /// Registers the YAML-based lens system.
    /// Loads lens manifests from filesystem or embedded resources.
    /// </summary>
    public static IServiceCollection AddYamlLenses(
        this IServiceCollection services,
        IConfiguration configuration,
        bool useEmbedded = false,
        Assembly[]? embeddedAssemblies = null)
    {
        if (useEmbedded)
        {
            // Register embedded manifest loader
            services.AddSingleton<IManifestLoader<LensManifest>>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<EmbeddedManifestLoader<LensManifest>>>();
                var assemblies = embeddedAssemblies ?? [Assembly.GetExecutingAssembly()];
                return new EmbeddedManifestLoader<LensManifest>(logger, assemblies, ".lens.yaml");
            });
        }
        else
        {
            // Register filesystem manifest loader
            services.AddSingleton<IManifestLoader<LensManifest>>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<FileSystemManifestLoader<LensManifest>>>();
                var lensDirectory = configuration["Lenses:Directory"] ?? "./manifests/lenses";

                // Make path absolute if relative
                if (!Path.IsPathRooted(lensDirectory))
                    lensDirectory = Path.GetFullPath(lensDirectory);

                return new FileSystemManifestLoader<LensManifest>(
                    logger,
                    [lensDirectory],
                    "*.lens.yaml");
            });
        }

        // Register YAML-based lens loader
        services.AddSingleton<ILensLoader, YamlLensLoader>();

        // Register lens registry
        services.AddSingleton<ILensRegistry, LensRegistry>();

        // Register lens render service
        services.AddScoped<ILensRenderService, LensRenderService>();

        // Register background initializer (defined in LensServiceExtensions.cs)
        services.AddHostedService<LucidRAG.Extensions.LensRegistryInitializer>();

        return services;
    }

    /// <summary>
    /// Registers the YAML-based wave system for RAG pipeline orchestration.
    /// </summary>
    public static IServiceCollection AddYamlWaves(
        this IServiceCollection services,
        IConfiguration configuration,
        bool useEmbedded = false,
        Assembly[]? embeddedAssemblies = null)
    {
        if (useEmbedded)
        {
            services.AddSingleton<IManifestLoader<WaveManifest>>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<EmbeddedManifestLoader<WaveManifest>>>();
                var assemblies = embeddedAssemblies ?? [Assembly.GetExecutingAssembly()];
                return new EmbeddedManifestLoader<WaveManifest>(logger, assemblies, ".wave.yaml");
            });
        }
        else
        {
            services.AddSingleton<IManifestLoader<WaveManifest>>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<FileSystemManifestLoader<WaveManifest>>>();
                var waveDirectory = configuration["Waves:Directory"] ?? "./manifests/waves";

                if (!Path.IsPathRooted(waveDirectory))
                    waveDirectory = Path.GetFullPath(waveDirectory);

                return new FileSystemManifestLoader<WaveManifest>(
                    logger,
                    [waveDirectory],
                    "*.wave.yaml");
            });
        }

        // TODO: Register wave registry and orchestrator

        return services;
    }

    /// <summary>
    /// Registers the YAML-based processor system for document processing.
    /// </summary>
    public static IServiceCollection AddYamlProcessors(
        this IServiceCollection services,
        IConfiguration configuration,
        bool useEmbedded = false,
        Assembly[]? embeddedAssemblies = null)
    {
        if (useEmbedded)
        {
            services.AddSingleton<IManifestLoader<ProcessorManifest>>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<EmbeddedManifestLoader<ProcessorManifest>>>();
                var assemblies = embeddedAssemblies ?? [Assembly.GetExecutingAssembly()];
                return new EmbeddedManifestLoader<ProcessorManifest>(logger, assemblies, ".processor.yaml");
            });
        }
        else
        {
            services.AddSingleton<IManifestLoader<ProcessorManifest>>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<FileSystemManifestLoader<ProcessorManifest>>>();
                var processorDirectory = configuration["Processors:Directory"] ?? "./manifests/processors";

                if (!Path.IsPathRooted(processorDirectory))
                    processorDirectory = Path.GetFullPath(processorDirectory);

                return new FileSystemManifestLoader<ProcessorManifest>(
                    logger,
                    [processorDirectory],
                    "*.processor.yaml");
            });
        }

        // TODO: Register processor registry

        return services;
    }

    /// <summary>
    /// Registers all YAML manifest systems (lenses, waves, processors).
    /// </summary>
    public static IServiceCollection AddYamlManifestSystem(
        this IServiceCollection services,
        IConfiguration configuration,
        bool useEmbedded = false,
        Assembly[]? embeddedAssemblies = null)
    {
        services.AddYamlLenses(configuration, useEmbedded, embeddedAssemblies);
        services.AddYamlWaves(configuration, useEmbedded, embeddedAssemblies);
        services.AddYamlProcessors(configuration, useEmbedded, embeddedAssemblies);

        return services;
    }
}
