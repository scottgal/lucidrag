using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.DocSummarizer.Images.Services.Vision;

/// <summary>
/// Service for interacting with Ollama vision LLMs (MiniCPM-V 2.6 recommended).
/// </summary>
public class VisionLlmService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<VisionLlmService> _logger;
    private readonly string _baseUrl;
    private readonly string _visionModel;
    private readonly string _tinyModel;

    public VisionLlmService(IConfiguration configuration, ILogger<VisionLlmService> logger)
    {
        _logger = logger;
        _baseUrl = configuration["Ollama:BaseUrl"] ?? "http://localhost:11434";
        _visionModel = configuration["Ollama:VisionModel"] ?? "minicpm-v:8b";
        _tinyModel = configuration["Ollama:TinyModel"] ?? "tinyllama:latest";

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    /// <summary>
    /// Analyze an image using a vision LLM and return a detailed description.
    /// </summary>
    public async Task<VisionLlmResult> AnalyzeImageAsync(
        string imagePath,
        string? customPrompt = null,
        string? modelOverride = null,
        CancellationToken ct = default)
    {
        try
        {
            // Read image and convert to base64
            var imageBytes = await File.ReadAllBytesAsync(imagePath, ct);
            var base64Image = Convert.ToBase64String(imageBytes);

            var prompt = customPrompt ?? "Analyze this image in detail. Describe what you see, including objects, people, setting, mood, colors, and any text visible. Be specific and comprehensive.";

            // Use model override if provided, otherwise use default
            var modelToUse = modelOverride ?? _visionModel;

            var request = new
            {
                model = modelToUse,
                prompt,
                images = new[] { base64Image },
                stream = false
            };

            var response = await _httpClient.PostAsJsonAsync("/api/generate", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(ct);

            if (result == null || string.IsNullOrWhiteSpace(result.Response))
            {
                return new VisionLlmResult(false, "No response from vision model", null);
            }

            _logger.LogInformation("Vision LLM analysis completed for {ImagePath}", imagePath);

            return new VisionLlmResult(true, null, result.Response);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to Ollama at {BaseUrl}", _baseUrl);
            return new VisionLlmResult(false, $"Connection failed: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vision LLM analysis failed for {ImagePath}", imagePath);
            return new VisionLlmResult(false, $"Analysis failed: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Use a tiny sentinel LLM to decompose a filter query into structured filters.
    /// </summary>
    public async Task<FilterDecomposition> DecomposeFilterQueryAsync(
        string query,
        CancellationToken ct = default)
    {
        try
        {
            var prompt = $@"You are a query analyzer for image search. Break down the following natural language query into structured filters.

Query: ""{query}""

Extract filters in this JSON format:
{{
  ""filters"": [
    {{""property"": ""country"", ""operator"": ""equals"", ""value"": ""UK""}},
    {{""property"": ""resolution"", ""operator"": ""equals"", ""value"": ""high""}},
    {{""property"": ""type"", ""operator"": ""equals"", ""value"": ""photo""}}
  ]
}}

Supported properties: country, resolution (low/medium/high/4k/8k), type (photo/screenshot/diagram/chart), orientation (portrait/landscape/square), text_score (0.0-1.0), sharpness (blurry/soft/sharp), has_text (true/false), is_grayscale (true/false), dominant_color

Supported operators: equals, not_equals, greater_than, less_than, contains

Only return valid JSON, nothing else.";

            var request = new
            {
                model = _tinyModel,
                prompt,
                stream = false,
                format = "json"
            };

            var response = await _httpClient.PostAsJsonAsync("/api/generate", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(ct);

            if (result == null || string.IsNullOrWhiteSpace(result.Response))
            {
                return new FilterDecomposition { Filters = [] };
            }

            var decomposition = JsonSerializer.Deserialize<FilterDecomposition>(result.Response);
            return decomposition ?? new FilterDecomposition { Filters = [] };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Filter query decomposition failed for: {Query}", query);
            return new FilterDecomposition { Filters = [] };
        }
    }

    /// <summary>
    /// Check if Ollama is available and the vision model is installed.
    /// </summary>
    public async Task<(bool Available, string? Message)> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        try
        {
            // Check if Ollama is running
            var response = await _httpClient.GetAsync("/api/tags", ct);
            if (!response.IsSuccessStatusCode)
            {
                return (false, $"Ollama not responding at {_baseUrl}");
            }

            var tags = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(ct);
            if (tags?.Models == null)
            {
                return (false, "Could not retrieve model list from Ollama");
            }

            // Check if vision model is installed
            var hasVisionModel = tags.Models.Any(m =>
                m.Name.StartsWith(_visionModel.Split(':')[0], StringComparison.OrdinalIgnoreCase));

            if (!hasVisionModel)
            {
                return (false, $"Vision model '{_visionModel}' not found. Install with: ollama pull {_visionModel}");
            }

            return (true, $"Ollama ready with {_visionModel}");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Result of a vision LLM analysis.
/// </summary>
public record VisionLlmResult(
    bool Success,
    string? Error,
    string? Caption);

/// <summary>
/// Filter decomposition from natural language query.
/// </summary>
public class FilterDecomposition
{
    [JsonPropertyName("filters")]
    public List<ImageFilter> Filters { get; set; } = [];
}

/// <summary>
/// Individual filter extracted from query.
/// </summary>
public class ImageFilter
{
    [JsonPropertyName("property")]
    public string Property { get; set; } = string.Empty;

    [JsonPropertyName("operator")]
    public string Operator { get; set; } = "equals";

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

// Ollama API response models
internal record OllamaGenerateResponse(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("response")] string Response,
    [property: JsonPropertyName("done")] bool Done);

internal record OllamaTagsResponse(
    [property: JsonPropertyName("models")] List<OllamaModel>? Models);

internal record OllamaModel(
    [property: JsonPropertyName("name")] string Name);
