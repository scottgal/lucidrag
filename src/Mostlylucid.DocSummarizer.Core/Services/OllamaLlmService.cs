using System.Text.Json;
using Mostlylucid.DocSummarizer.Config;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// ILlmService implementation that wraps OllamaService for text generation.
/// </summary>
public class OllamaLlmService : ILlmService
{
    private readonly OllamaService _ollamaService;
    private readonly OllamaConfig _config;

    public OllamaLlmService(OllamaService ollamaService, OllamaConfig config)
    {
        _ollamaService = ollamaService;
        _config = config;
    }

    /// <inheritdoc />
    public string ProviderName => "Ollama";

    /// <inheritdoc />
    public async Task<string> GenerateAsync(string prompt, LlmOptions? options = null, CancellationToken ct = default)
    {
        options ??= LlmOptions.Default;
        var temperature = options.Temperature ?? _config.Temperature;
        var model = options.Model ?? _config.Model;

        // If a system prompt is provided, prepend it
        var fullPrompt = prompt;
        if (!string.IsNullOrEmpty(options.SystemPrompt))
        {
            fullPrompt = $"{options.SystemPrompt}\n\n{prompt}";
        }

        return await _ollamaService.GenerateWithModelAsync(model, fullPrompt, temperature, ct);
    }

    /// <inheritdoc />
    public async Task<T?> GenerateJsonAsync<T>(string prompt, LlmOptions? options = null, CancellationToken ct = default) where T : class
    {
        // Add JSON instruction to prompt
        var jsonPrompt = $"{prompt}\n\nRespond with valid JSON only, no markdown formatting or explanation.";

        var response = await GenerateAsync(jsonPrompt, options, ct);

        // Clean up response - remove markdown code blocks if present
        response = CleanJsonResponse(response);

        try
        {
            return JsonSerializer.Deserialize<T>(response);
        }
        catch (JsonException)
        {
            // Try to extract JSON from response
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonPart = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                return JsonSerializer.Deserialize<T>(jsonPart);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        return await _ollamaService.IsAvailableAsync();
    }

    /// <inheritdoc />
    public async Task<int> GetContextWindowAsync(CancellationToken ct = default)
    {
        return await _ollamaService.GetContextWindowAsync();
    }

    /// <summary>
    /// Clean JSON response by removing markdown code blocks
    /// </summary>
    private static string CleanJsonResponse(string response)
    {
        response = response.Trim();

        // Remove ```json ... ``` wrapper
        if (response.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            response = response[7..]; // Remove ```json
        }
        else if (response.StartsWith("```"))
        {
            response = response[3..]; // Remove ```
        }

        if (response.EndsWith("```"))
        {
            response = response[..^3]; // Remove trailing ```
        }

        return response.Trim();
    }
}
