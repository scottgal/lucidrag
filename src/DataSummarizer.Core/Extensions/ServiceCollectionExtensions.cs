using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer.Data.Config;
using Mostlylucid.DocSummarizer.Data.Pipeline;
using Mostlylucid.DocSummarizer.Data.Services;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace Mostlylucid.DocSummarizer.Data.Extensions;

/// <summary>
/// Extension methods for registering DataSummarizer.Core services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add DataSummarizer.Core services for processing CSV, JSON, Excel, and Parquet files.
    /// </summary>
    public static IServiceCollection AddDataSummarizer(this IServiceCollection services)
    {
        return services.AddDataSummarizer(_ => { });
    }

    /// <summary>
    /// Add DataSummarizer.Core services with custom configuration.
    /// </summary>
    public static IServiceCollection AddDataSummarizer(
        this IServiceCollection services,
        Action<DataProcessorOptions> configure)
    {
        // Register configuration
        services.Configure(configure);

        // Core data processor service
        services.AddScoped<IDataProcessor, DataProcessorService>();

        // Register the pipeline for unified pipeline registry
        // Pipeline is scoped because it depends on scoped IDataProcessor
        services.AddScoped<DataPipeline>();
        services.AddScoped<IPipeline>(sp => sp.GetRequiredService<DataPipeline>());

        return services;
    }
}
