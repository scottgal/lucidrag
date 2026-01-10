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

            // Extract caption from JSON response if present, otherwise use cleaned response
            var caption = ExtractCaptionFromResponse(result.Response);
            return new VisionLlmResult(true, null, caption);
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

    /// <summary>
    /// Extract caption text from LLM response, handling JSON or verbose plain text.
    /// Enforces WCAG-compliant length limits and strips prompt leakage.
    /// </summary>
    private string ExtractCaptionFromResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return "";

        // Try to extract from JSON format: {"caption": "..."}
        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonStr = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var doc = JsonDocument.Parse(jsonStr);
                // Check multiple possible property names
                foreach (var propName in new[] { "caption", "description", "scene", "summary" })
                {
                    if (doc.RootElement.TryGetProperty(propName, out var prop))
                    {
                        var val = prop.GetString();
                        if (!string.IsNullOrWhiteSpace(val))
                            return SanitizeAndTruncate(val);
                    }
                }
            }
        }
        catch
        {
            // JSON parsing failed, continue to regex fallback
        }

        // Fallback: Try regex extraction
        var match = System.Text.RegularExpressions.Regex.Match(
            response, @"""(?:caption|description)""\s*:\s*""([^""]+)""");
        if (match.Success && match.Groups.Count > 1)
        {
            return SanitizeAndTruncate(match.Groups[1].Value);
        }

        // Plain text - sanitize and truncate
        return SanitizeAndTruncate(response);
    }

    /// <summary>
    /// Sanitize caption by removing prompt leakage and truncating for WCAG.
    /// </summary>
    private static string SanitizeAndTruncate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var result = text.Trim();

        // Strip common prompt leakage patterns
        var leakagePatterns = new[]
        {
            @"^Based on (?:the |this )?(provided |given )?image.*?[:,]\s*",
            @"^According to the (?:image|guidelines).*?[:,]\s*",
            @"^(?:The |This )?image (?:shows|depicts|displays|features|contains|presents)\s*",
            @"^In (?:the |this )?image,?\s*",
            @"^(?:Here is|Here's) (?:a|the) (?:caption|description).*?:\s*",
            @"^For accessibility[:,]\s*",
            @"^(?:Caption|Description):\s*",
            @"^\{[^}]*\}\s*",
            @"^I (?:can )?see\s+",
            @"^(?:Looking at (?:the|this) image,?\s*)?",
        };

        foreach (var pattern in leakagePatterns)
        {
            result = System.Text.RegularExpressions.Regex.Replace(
                result, pattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // Clean up quotes and whitespace
        result = result.Trim('"', '\'', ' ');

        // Skip markdown or verbose preamble
        if (result.StartsWith("```") || result.StartsWith("**") || result.StartsWith("#"))
        {
            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim().TrimStart('*', '#', '-', ' ');
                if (trimmed.Length > 10 && char.IsLetter(trimmed[0]))
                {
                    result = trimmed;
                    break;
                }
            }
        }

        // Capitalize first letter
        if (result.Length > 0 && char.IsLower(result[0]))
        {
            result = char.ToUpper(result[0]) + result[1..];
        }

        return TruncateForWcag(result);
    }

    /// <summary>
    /// Truncate text to WCAG-compliant length (~125 chars max).
    /// Preserves complete sentences where possible.
    /// </summary>
    private static string TruncateForWcag(string text, int maxLength = 125)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        text = text.Trim();

        if (text.Length <= maxLength)
            return text;

        // Find last sentence boundary within limit
        var truncated = text[..maxLength];
        var lastPeriod = truncated.LastIndexOf('.');
        if (lastPeriod > 40) // Keep at least 40 chars
        {
            return truncated[..(lastPeriod + 1)];
        }

        // No good sentence boundary, truncate at word boundary
        var lastSpace = truncated.LastIndexOf(' ');
        if (lastSpace > 40)
        {
            return truncated[..lastSpace] + "...";
        }

        return truncated[..(maxLength - 3)] + "...";
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
