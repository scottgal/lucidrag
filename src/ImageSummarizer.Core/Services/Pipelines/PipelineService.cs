using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Config;

namespace Mostlylucid.DocSummarizer.Images.Services.Pipelines;

/// <summary>
/// Service for loading and managing OCR pipeline configurations
/// </summary>
public class PipelineService
{
    private readonly ILogger<PipelineService>? _logger;
    private PipelinesConfig? _config;
    private readonly string _configPath;

    public PipelineService(string? customConfigPath = null, ILogger<PipelineService>? logger = null)
    {
        _logger = logger;

        // Default to embedded pipelines.json, but allow override
        _configPath = customConfigPath ?? GetDefaultConfigPath();
    }

    /// <summary>
    /// Load pipelines from configuration file
    /// Uses JSON source generation for zero-allocation deserialization
    /// </summary>
    public async Task<PipelinesConfig> LoadPipelinesAsync(CancellationToken ct = default)
    {
        if (_config != null)
            return _config;

        try
        {
            string json;

            // Try custom path first
            if (File.Exists(_configPath))
            {
                _logger?.LogInformation("Loading pipelines from {Path}", _configPath);
                json = await File.ReadAllTextAsync(_configPath, ct);
            }
            else
            {
                // Load embedded default
                _logger?.LogInformation("Loading default embedded pipelines");
                json = await LoadEmbeddedPipelinesAsync();
            }

            // Use source-generated JSON context for performance
            _config = JsonSerializer.Deserialize(json, PipelineJsonContext.Default.PipelinesConfig)
                ?? throw new InvalidOperationException("Failed to deserialize pipeline configuration");

            ValidatePipelines(_config);

            _logger?.LogInformation("Loaded {Count} pipelines (default: {Default})",
                _config.Pipelines.Count,
                _config.DefaultPipeline ?? "none");

            return _config;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load pipeline configuration");
            throw;
        }
    }

    /// <summary>
    /// Get a specific pipeline by name
    /// </summary>
    public async Task<PipelineConfig?> GetPipelineAsync(string name, CancellationToken ct = default)
    {
        var config = await LoadPipelinesAsync(ct);
        return config.Pipelines.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Get the default pipeline
    /// </summary>
    public async Task<PipelineConfig> GetDefaultPipelineAsync(CancellationToken ct = default)
    {
        var config = await LoadPipelinesAsync(ct);

        // Try explicit default pipeline name
        if (!string.IsNullOrEmpty(config.DefaultPipeline))
        {
            var pipeline = await GetPipelineAsync(config.DefaultPipeline, ct);
            if (pipeline != null)
                return pipeline;
        }

        // Try pipeline marked as default
        var defaultPipeline = config.Pipelines.FirstOrDefault(p => p.IsDefault);
        if (defaultPipeline != null)
            return defaultPipeline;

        // Fallback to first pipeline
        return config.Pipelines.FirstOrDefault()
            ?? throw new InvalidOperationException("No pipelines configured");
    }

    /// <summary>
    /// List all available pipeline names
    /// </summary>
    public async Task<List<string>> ListPipelineNamesAsync(CancellationToken ct = default)
    {
        var config = await LoadPipelinesAsync(ct);
        return config.Pipelines.Select(p => p.Name).ToList();
    }

    /// <summary>
    /// Save current configuration to file
    /// Uses JSON source generation for efficient serialization
    /// </summary>
    public async Task SavePipelinesAsync(PipelinesConfig config, string? outputPath = null, CancellationToken ct = default)
    {
        var path = outputPath ?? _configPath;

        ValidatePipelines(config);

        var json = JsonSerializer.Serialize(config, PipelineJsonContext.Default.PipelinesConfig);
        await File.WriteAllTextAsync(path, json, ct);

        _logger?.LogInformation("Saved pipeline configuration to {Path}", path);

        // Invalidate cache if saving to current config path
        if (path == _configPath)
            _config = null;
    }

    /// <summary>
    /// Reload configuration from disk (clears cache)
    /// </summary>
    public void Reload()
    {
        _config = null;
        _logger?.LogInformation("Pipeline configuration cache cleared");
    }

    private static string GetDefaultConfigPath()
    {
        // Check multiple locations
        var locations = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Config", "pipelines.json"),
            Path.Combine(AppContext.BaseDirectory, "pipelines.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LucidRAG", "pipelines.json")
        };

        return locations.FirstOrDefault(File.Exists)
            ?? locations[0]; // Default to first location
    }

    private async Task<string> LoadEmbeddedPipelinesAsync()
    {
        var assembly = typeof(PipelineService).Assembly;
        var resourceName = "Mostlylucid.DocSummarizer.Images.Config.pipelines.json";

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found. Available resources: " +
                string.Join(", ", assembly.GetManifestResourceNames()));
        }

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private void ValidatePipelines(PipelinesConfig config)
    {
        if (config.Pipelines.Count == 0)
            throw new InvalidOperationException("No pipelines defined in configuration");

        foreach (var pipeline in config.Pipelines)
        {
            if (string.IsNullOrWhiteSpace(pipeline.Name))
                throw new InvalidOperationException("Pipeline must have a name");

            if (pipeline.Phases.Count == 0)
                _logger?.LogWarning("Pipeline '{Name}' has no phases configured", pipeline.Name);

            // Validate phase dependencies
            var phaseIds = new HashSet<string>(pipeline.Phases.Select(p => p.Id));
            foreach (var phase in pipeline.Phases)
            {
                if (phase.DependsOn != null)
                {
                    foreach (var dep in phase.DependsOn)
                    {
                        if (!phaseIds.Contains(dep))
                        {
                            _logger?.LogWarning(
                                "Phase '{PhaseId}' depends on unknown phase '{Dependency}' in pipeline '{Pipeline}'",
                                phase.Id, dep, pipeline.Name);
                        }
                    }
                }
            }
        }

        _logger?.LogDebug("Pipeline configuration validated successfully");
    }
}
