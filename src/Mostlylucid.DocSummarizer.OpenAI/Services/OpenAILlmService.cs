using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.OpenAI.Config;
using Mostlylucid.DocSummarizer.Services;

namespace Mostlylucid.DocSummarizer.OpenAI.Services;

/// <summary>
/// OpenAI implementation of ILlmService
/// </summary>
public class OpenAILlmService : ILlmService
{
    private readonly OpenAIConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAILlmService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public OpenAILlmService(
        IOptions<OpenAIConfig> config,
        HttpClient httpClient,
        ILogger<OpenAILlmService> logger)
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
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {ResolveApiKey(_config.ApiKey)}");
        _httpClient.Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds);
    }

    /// <inheritdoc />
    public string ProviderName => "OpenAI";

    /// <inheritdoc />
    public async Task<string> GenerateAsync(string prompt, LlmOptions? options = null, CancellationToken ct = default)
    {
        var model = options?.Model ?? _config.Model;
        var maxTokens = options?.MaxTokens ?? _config.MaxTokens;
        var temperature = options?.Temperature ?? _config.Temperature;

        var messages = new List<OpenAIMessage>();

        // Add system prompt if provided
        if (!string.IsNullOrEmpty(options?.SystemPrompt))
        {
            messages.Add(new OpenAIMessage { Role = "system", Content = options.SystemPrompt });
        }

        messages.Add(new OpenAIMessage { Role = "user", Content = prompt });

        var request = new OpenAIChatRequest
        {
            Model = model,
            MaxTokens = maxTokens,
            Temperature = temperature,
            Messages = messages
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/chat/completions", request, _jsonOptions, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("OpenAI API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                throw new HttpRequestException($"OpenAI API error: {response.StatusCode} - {errorContent}");
            }

            var result = await response.Content.ReadFromJsonAsync<OpenAIChatResponse>(_jsonOptions, ct);

            if (result?.Choices is { Count: > 0 })
            {
                return result.Choices[0].Message?.Content ?? "";
            }

            return "";
        }
        catch (Exception ex) when (ex is not HttpRequestException)
        {
            _logger.LogError(ex, "Error calling OpenAI API");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<T?> GenerateJsonAsync<T>(string prompt, LlmOptions? options = null, CancellationToken ct = default)
        where T : class
    {
        // Use JSON mode if model supports it
        var model = options?.Model ?? _config.Model;
        var supportsJsonMode = model.Contains("gpt-4") || model.Contains("gpt-3.5-turbo-1106");

        var jsonPrompt = supportsJsonMode
            ? prompt
            : $"""
              {prompt}

              Respond with valid JSON only. No markdown, no code blocks, just the JSON object.
              """;

        var messages = new List<OpenAIMessage>();

        if (!string.IsNullOrEmpty(options?.SystemPrompt))
        {
            messages.Add(new OpenAIMessage { Role = "system", Content = options.SystemPrompt });
        }

        messages.Add(new OpenAIMessage { Role = "user", Content = jsonPrompt });

        var request = new OpenAIChatRequest
        {
            Model = model,
            MaxTokens = options?.MaxTokens ?? _config.MaxTokens,
            Temperature = options?.Temperature ?? _config.Temperature,
            Messages = messages,
            ResponseFormat = supportsJsonMode ? new ResponseFormat { Type = "json_object" } : null
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/chat/completions", request, _jsonOptions, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("OpenAI API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                throw new HttpRequestException($"OpenAI API error: {response.StatusCode} - {errorContent}");
            }

            var result = await response.Content.ReadFromJsonAsync<OpenAIChatResponse>(_jsonOptions, ct);
            var content = result?.Choices?.FirstOrDefault()?.Message?.Content ?? "";

            // Clean up response if needed
            content = CleanJsonResponse(content);

            return JsonSerializer.Deserialize<T>(content, _jsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse JSON response");
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
            var request = new OpenAIChatRequest
            {
                Model = _config.Model,
                MaxTokens = 1,
                Messages = [new OpenAIMessage { Role = "user", Content = "Hi" }]
            };

            var response = await _httpClient.PostAsJsonAsync("/chat/completions", request, _jsonOptions, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OpenAI availability check failed");
            return false;
        }
    }

    /// <inheritdoc />
    public Task<int> GetContextWindowAsync(CancellationToken ct = default)
    {
        // Return context window based on model
        var contextWindow = _config.Model switch
        {
            var m when m.Contains("gpt-4o") => 128000,
            var m when m.Contains("gpt-4-turbo") => 128000,
            var m when m.Contains("gpt-4-32k") => 32768,
            var m when m.Contains("gpt-4") => 8192,
            var m when m.Contains("gpt-3.5-turbo-16k") => 16384,
            var m when m.Contains("gpt-3.5-turbo") => 16384,
            _ => 8192 // Conservative default
        };

        return Task.FromResult(contextWindow);
    }

    private static string ResolveApiKey(string apiKey)
    {
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

        if (response.StartsWith("```json"))
            response = response[7..];
        else if (response.StartsWith("```"))
            response = response[3..];

        if (response.EndsWith("```"))
            response = response[..^3];

        return response.Trim();
    }

    // Request/Response models
    private class OpenAIChatRequest
    {
        public required string Model { get; set; }
        public int MaxTokens { get; set; }
        public double Temperature { get; set; }
        public required List<OpenAIMessage> Messages { get; set; }
        public ResponseFormat? ResponseFormat { get; set; }
    }

    private class OpenAIMessage
    {
        public required string Role { get; set; }
        public required string Content { get; set; }
    }

    private class ResponseFormat
    {
        public required string Type { get; set; }
    }

    private class OpenAIChatResponse
    {
        public string? Id { get; set; }
        public string? Object { get; set; }
        public long Created { get; set; }
        public string? Model { get; set; }
        public List<Choice>? Choices { get; set; }
        public Usage? Usage { get; set; }
    }

    private class Choice
    {
        public int Index { get; set; }
        public OpenAIMessage? Message { get; set; }
        public string? FinishReason { get; set; }
    }

    private class Usage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
    }
}
