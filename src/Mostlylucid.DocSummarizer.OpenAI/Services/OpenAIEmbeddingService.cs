using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.OpenAI.Config;
using Mostlylucid.DocSummarizer.Services;

namespace Mostlylucid.DocSummarizer.OpenAI.Services;

/// <summary>
/// OpenAI implementation of IEmbeddingService
/// </summary>
public class OpenAIEmbeddingService : IEmbeddingService
{
    private readonly OpenAIConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAIEmbeddingService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public OpenAIEmbeddingService(
        IOptions<OpenAIConfig> config,
        HttpClient httpClient,
        ILogger<OpenAIEmbeddingService> logger)
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
    public int EmbeddingDimension => _config.EmbeddingDimension;

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default)
    {
        // No initialization needed for OpenAI - it's a remote API
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var request = new EmbeddingRequest
        {
            Model = _config.EmbeddingModel,
            Input = text
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/embeddings", request, _jsonOptions, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("OpenAI Embedding API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                throw new HttpRequestException($"OpenAI Embedding API error: {response.StatusCode} - {errorContent}");
            }

            var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(_jsonOptions, ct);

            if (result?.Data is { Count: > 0 })
            {
                return result.Data[0].Embedding ?? [];
            }

            return [];
        }
        catch (Exception ex) when (ex is not HttpRequestException)
        {
            _logger.LogError(ex, "Error calling OpenAI Embedding API");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var textList = texts.ToList();
        if (textList.Count == 0)
            return [];

        var request = new BatchEmbeddingRequest
        {
            Model = _config.EmbeddingModel,
            Input = textList
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/embeddings", request, _jsonOptions, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("OpenAI Embedding API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                throw new HttpRequestException($"OpenAI Embedding API error: {response.StatusCode} - {errorContent}");
            }

            var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(_jsonOptions, ct);

            if (result?.Data != null)
            {
                return result.Data
                    .OrderBy(d => d.Index)
                    .Select(d => d.Embedding ?? [])
                    .ToArray();
            }

            return [];
        }
        catch (Exception ex) when (ex is not HttpRequestException)
        {
            _logger.LogError(ex, "Error calling OpenAI Embedding API (batch)");
            throw;
        }
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

    // Request/Response models
    private class EmbeddingRequest
    {
        public required string Model { get; set; }
        public required string Input { get; set; }
    }

    private class BatchEmbeddingRequest
    {
        public required string Model { get; set; }
        public required List<string> Input { get; set; }
    }

    private class EmbeddingResponse
    {
        public string? Object { get; set; }
        public List<EmbeddingData>? Data { get; set; }
        public string? Model { get; set; }
        public EmbeddingUsage? Usage { get; set; }
    }

    private class EmbeddingData
    {
        public string? Object { get; set; }
        public int Index { get; set; }
        public float[]? Embedding { get; set; }
    }

    private class EmbeddingUsage
    {
        public int PromptTokens { get; set; }
        public int TotalTokens { get; set; }
    }
}
