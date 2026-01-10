using Mostlylucid.DocSummarizer.Images.Services.Vision.Clients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.DocSummarizer.Images.Services.Vision;

/// <summary>
/// Unified vision service that supports multiple providers (Ollama, Anthropic, OpenAI)
/// Allows dynamic provider/model selection for vision LLM analysis
/// </summary>
public class UnifiedVisionService
{
    private readonly Dictionary<string, IVisionClient> _clients;
    private readonly ILogger<UnifiedVisionService> _logger;
    private readonly string _defaultProvider;

    public UnifiedVisionService(
        IConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<UnifiedVisionService>();
        _defaultProvider = configuration["Vision:DefaultProvider"] ?? "Ollama";

        // Initialize all vision clients
        _clients = new Dictionary<string, IVisionClient>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ollama"] = new OllamaVisionClient(configuration, loggerFactory.CreateLogger<OllamaVisionClient>()),
            ["Anthropic"] = new AnthropicVisionClient(configuration, loggerFactory.CreateLogger<AnthropicVisionClient>()),
            ["OpenAI"] = new OpenAIVisionClient(configuration, loggerFactory.CreateLogger<OpenAIVisionClient>())
        };
    }

    /// <summary>
    /// Get a vision client by provider name
    /// </summary>
    public IVisionClient GetClient(string? provider = null)
    {
        var providerName = provider ?? _defaultProvider;

        if (_clients.TryGetValue(providerName, out var client))
        {
            return client;
        }

        throw new InvalidOperationException($"Vision provider '{providerName}' not found. Available providers: {string.Join(", ", _clients.Keys)}");
    }

    /// <summary>
    /// Analyze an image using the specified provider and model
    /// </summary>
    public async Task<VisionResult> AnalyzeImageAsync(
        string imagePath,
        string prompt,
        string? provider = null,
        string? model = null,
        double? temperature = null,
        CancellationToken ct = default)
    {
        var client = GetClient(provider);
        return await client.AnalyzeImageAsync(imagePath, prompt, model, temperature, ct);
    }

    /// <summary>
    /// Check availability of a specific provider
    /// </summary>
    public async Task<(bool Available, string? Message)> CheckProviderAvailabilityAsync(
        string? provider = null,
        CancellationToken ct = default)
    {
        var client = GetClient(provider);
        return await client.CheckAvailabilityAsync(ct);
    }

    /// <summary>
    /// Check availability of all providers
    /// </summary>
    public async Task<Dictionary<string, (bool Available, string? Message)>> CheckAllProvidersAsync(
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, (bool Available, string? Message)>();

        foreach (var (providerName, client) in _clients)
        {
            var result = await client.CheckAvailabilityAsync(ct);
            results[providerName] = result;
        }

        return results;
    }

    /// <summary>
    /// Get list of available providers
    /// </summary>
    public IEnumerable<string> GetAvailableProviders()
    {
        return _clients.Keys;
    }

    /// <summary>
    /// Parse a model specification in the format "provider:model" or just "model"
    /// Returns (provider, model) tuple
    /// </summary>
    public (string? Provider, string Model) ParseModelSpec(string modelSpec)
    {
        if (string.IsNullOrWhiteSpace(modelSpec))
        {
            return (null, string.Empty);
        }

        // Check if model spec includes provider (e.g., "anthropic:claude-3-5-sonnet")
        var parts = modelSpec.Split(':', 2, StringSplitOptions.TrimEntries);

        if (parts.Length == 2)
        {
            // Format: "provider:model"
            var provider = parts[0];
            var model = parts[1];

            // Validate provider exists
            if (_clients.ContainsKey(provider))
            {
                return (provider, model);
            }
            else
            {
                _logger.LogWarning("Unknown provider '{Provider}' in model spec '{ModelSpec}', treating as Ollama model", provider, modelSpec);
                return ("Ollama", modelSpec); // Fallback to Ollama
            }
        }
        else
        {
            // Format: "model" (use default provider)
            return (null, modelSpec);
        }
    }
}
