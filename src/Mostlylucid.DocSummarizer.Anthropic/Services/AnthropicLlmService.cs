using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Anthropic.Config;
using Mostlylucid.DocSummarizer.Services;

namespace Mostlylucid.DocSummarizer.Anthropic.Services;

/// <summary>
/// Anthropic Claude implementation of ILlmService
/// </summary>
public class AnthropicLlmService : ILlmService
{
    private readonly AnthropicConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AnthropicLlmService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public AnthropicLlmService(
        IOptions<AnthropicConfig> config,
        HttpClient httpClient,
        ILogger<AnthropicLlmService> logger)
    {
        _config = config.Value;
        _httpClient = httpClient;
        _logger = logger;

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // Configure HttpClient
        _httpClient.BaseAddress = new Uri(_config.BaseUrl);
        _httpClient.DefaultRequestHeaders.Add("x-api-key", ResolveApiKey(_config.ApiKey));
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", _config.ApiVersion);
        _httpClient.Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds);
    }

    /// <inheritdoc />
    public string ProviderName => "Anthropic";

    /// <inheritdoc />
    public async Task<string> GenerateAsync(string prompt, LlmOptions? options = null, CancellationToken ct = default)
    {
        var model = options?.Model ?? _config.Model;
        var maxTokens = options?.MaxTokens ?? _config.MaxTokens;
        var temperature = options?.Temperature ?? _config.Temperature;

        var request = new AnthropicRequest
        {
            Model = model,
            MaxTokens = maxTokens,
            Temperature = temperature,
            Messages =
            [
                new AnthropicMessage { Role = "user", Content = prompt }
            ]
        };

        // Add system prompt if provided
        if (!string.IsNullOrEmpty(options?.SystemPrompt))
        {
            request.System = options.SystemPrompt;
        }

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/v1/messages", request, _jsonOptions, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("Anthropic API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                throw new HttpRequestException($"Anthropic API error: {response.StatusCode} - {errorContent}");
            }

            var result = await response.Content.ReadFromJsonAsync<AnthropicResponse>(_jsonOptions, ct);

            if (result?.Content is { Count: > 0 })
            {
                return result.Content[0].Text ?? "";
            }

            return "";
        }
        catch (Exception ex) when (ex is not HttpRequestException)
        {
            _logger.LogError(ex, "Error calling Anthropic API");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<T?> GenerateJsonAsync<T>(string prompt, LlmOptions? options = null, CancellationToken ct = default)
        where T : class
    {
        // Wrap prompt to request JSON output
        var jsonPrompt = $"""
            {prompt}

            Respond with valid JSON only. No markdown, no code blocks, just the JSON object.
            """;

        var response = await GenerateAsync(jsonPrompt, options, ct);

        // Clean up response if needed
        response = CleanJsonResponse(response);

        try
        {
            return JsonSerializer.Deserialize<T>(response, _jsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse JSON response: {Response}", response);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
            return false;

        try
        {
            // Simple test call with minimal tokens
            var request = new AnthropicRequest
            {
                Model = _config.Model,
                MaxTokens = 1,
                Messages =
                [
                    new AnthropicMessage { Role = "user", Content = "Hi" }
                ]
            };

            var response = await _httpClient.PostAsJsonAsync("/v1/messages", request, _jsonOptions, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Anthropic availability check failed");
            return false;
        }
    }

    /// <inheritdoc />
    public Task<int> GetContextWindowAsync(CancellationToken ct = default)
    {
        // Return context window based on model
        var contextWindow = _config.Model switch
        {
            var m when m.Contains("opus") => 200000,
            var m when m.Contains("sonnet") => 200000,
            var m when m.Contains("haiku") => 200000,
            _ => 100000 // Conservative default
        };

        return Task.FromResult(contextWindow);
    }

    private static string ResolveApiKey(string apiKey)
    {
        // Support environment variable substitution
        if (apiKey.StartsWith("${") && apiKey.EndsWith("}"))
        {
            var envVar = apiKey[2..^1];
            return Environment.GetEnvironmentVariable(envVar) ?? "";
        }
        return apiKey;
    }

    private static string CleanJsonResponse(string response)
    {
        response = response.Trim();

        // Remove markdown code blocks if present
        if (response.StartsWith("```json"))
            response = response[7..];
        else if (response.StartsWith("```"))
            response = response[3..];

        if (response.EndsWith("```"))
            response = response[..^3];

        return response.Trim();
    }

    // Request/Response models
    private class AnthropicRequest
    {
        public required string Model { get; set; }
        public int MaxTokens { get; set; }
        public double Temperature { get; set; }
        public string? System { get; set; }
        public required List<AnthropicMessage> Messages { get; set; }
    }

    private class AnthropicMessage
    {
        public required string Role { get; set; }
        public required string Content { get; set; }
    }

    private class AnthropicResponse
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? Role { get; set; }
        public List<ContentBlock>? Content { get; set; }
        public string? Model { get; set; }
        public string? StopReason { get; set; }
        public UsageInfo? Usage { get; set; }
    }

    private class ContentBlock
    {
        public string? Type { get; set; }
        public string? Text { get; set; }
    }

    private class UsageInfo
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
    }
}
