using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// OpenTelemetry configuration options for the CLI
/// </summary>
public class TelemetryOptions
{
    /// <summary>Whether telemetry is enabled</summary>
    public bool Enabled { get; set; }
    
    /// <summary>Export telemetry to console</summary>
    public bool ConsoleExporter { get; set; }
    
    /// <summary>OTLP endpoint for traces and metrics (e.g., http://localhost:4317)</summary>
    public string? OtlpEndpoint { get; set; }
    
    /// <summary>Service name for telemetry</summary>
    public string ServiceName { get; set; } = "docsummarizer";
    
    /// <summary>Service version (matches CLI version)</summary>
    public string ServiceVersion { get; set; } = "4.0.0";
}

/// <summary>
/// Extension methods for configuring OpenTelemetry in the CLI
/// </summary>
public static class TelemetryExtensions
{
    /// <summary>
    /// ActivitySource names used by the DocSummarizer Core library
    /// </summary>
    private static readonly string[] ActivitySourceNames = 
    [
        "Mostlylucid.DocSummarizer",
        "Mostlylucid.DocSummarizer.Ollama",
        "Mostlylucid.DocSummarizer.WebFetcher"
    ];
    
    /// <summary>
    /// Meter names used by the DocSummarizer Core library
    /// </summary>
    private static readonly string[] MeterNames = 
    [
        "Mostlylucid.DocSummarizer",
        "Mostlylucid.DocSummarizer.Ollama",
        "Mostlylucid.DocSummarizer.WebFetcher"
    ];
    
    /// <summary>
    /// Adds OpenTelemetry tracing and metrics to the service collection
    /// </summary>
    public static IServiceCollection AddDocSummarizerTelemetry(
        this IServiceCollection services,
        TelemetryOptions options)
    {
        if (!options.Enabled)
            return services;
        
        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: options.ServiceName, 
                serviceVersion: options.ServiceVersion)
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment"] = "cli",
                ["telemetry.sdk.language"] = "dotnet"
            });
        
        // Configure tracing
        services.AddOpenTelemetry()
            .WithTracing(builder =>
            {
                builder
                    .SetResourceBuilder(resourceBuilder)
                    .AddSource(ActivitySourceNames);
                
                if (options.ConsoleExporter)
                {
                    builder.AddConsoleExporter();
                }
                
                if (!string.IsNullOrEmpty(options.OtlpEndpoint))
                {
                    builder.AddOtlpExporter(otlp =>
                    {
                        otlp.Endpoint = new Uri(options.OtlpEndpoint);
                    });
                }
            })
            .WithMetrics(builder =>
            {
                builder
                    .SetResourceBuilder(resourceBuilder)
                    .AddMeter(MeterNames);
                
                if (options.ConsoleExporter)
                {
                    builder.AddConsoleExporter();
                }
                
                if (!string.IsNullOrEmpty(options.OtlpEndpoint))
                {
                    builder.AddOtlpExporter(otlp =>
                    {
                        otlp.Endpoint = new Uri(options.OtlpEndpoint);
                    });
                }
            });
        
        return services;
    }
    
    /// <summary>
    /// Parses telemetry options from command line arguments
    /// </summary>
    public static TelemetryOptions ParseTelemetryOptions(
        bool telemetryEnabled,
        bool consoleExporter,
        string? otlpEndpoint)
    {
        return new TelemetryOptions
        {
            Enabled = telemetryEnabled || consoleExporter || !string.IsNullOrEmpty(otlpEndpoint),
            ConsoleExporter = consoleExporter,
            OtlpEndpoint = otlpEndpoint
        };
    }
}
