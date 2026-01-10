using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace Mostlylucid.Summarizer.Core.Extensions;

/// <summary>
/// Extension methods for registering Summarizer.Core services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add the pipeline registry. Call this after registering all IPipeline implementations.
    /// </summary>
    public static IServiceCollection AddPipelineRegistry(this IServiceCollection services)
    {
        services.AddSingleton<IPipelineRegistry>(sp =>
        {
            var pipelines = sp.GetServices<IPipeline>();
            return new PipelineRegistry(pipelines);
        });

        return services;
    }

    /// <summary>
    /// Register a pipeline implementation.
    /// </summary>
    public static IServiceCollection AddPipeline<TPipeline>(this IServiceCollection services)
        where TPipeline : class, IPipeline
    {
        services.AddSingleton<IPipeline, TPipeline>();
        services.AddSingleton<TPipeline>();
        return services;
    }
}
