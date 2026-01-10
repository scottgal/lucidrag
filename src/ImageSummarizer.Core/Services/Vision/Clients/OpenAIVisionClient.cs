using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.DocSummarizer.Images.Services.Vision.Clients;

/// <summary>
/// OpenAI GPT-4 Vision client for image analysis
/// Uses GPT-4 Vision (gpt-4-vision-preview, gpt-4o, etc.)
/// </summary>
public class OpenAIVisionClient : IVisionClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAIVisionClient> _logger;
    private readonly string _apiKey;
    private readonly string _defaultModel;

    public string Provider => "OpenAI";

    public OpenAIVisionClient(IConfiguration configuration, ILogger<OpenAIVisionClient> logger)
    {
        _logger = logger;
        _apiKey = configuration["OpenAI:ApiKey"] ??
                  Environment.GetEnvironmentVariable("OPENAI_API_KEY") ??
                  string.Empty;
        _defaultModel = configuration["OpenAI:VisionModel"] ?? "gpt-4o";

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com"),
            Timeout = TimeSpan.FromMinutes(5)
        };

        if (!string.IsNullOrEmpty(_apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        }
    }

    public async Task<(bool Available, string? Message)> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            return (false, "OpenAI API key not configured. Set OPENAI_API_KEY environment variable or configure in appsettings.json");
        }

        try
        {
            // List models to verify API key
            var response = await _httpClient.GetAsync("/v1/models", ct);

            if (response.IsSuccessStatusCode)
            {
                return (true, $"OpenAI ready with {_defaultModel}");
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                return (false, $"OpenAI API error: {response.StatusCode} - {errorContent}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    public async Task<VisionResult> AnalyzeImageAsync(
        string imagePath,
        string prompt,
        string? model = null,
        double? temperature = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            return new VisionResult(
                Success: false,
                Error: "OpenAI API key not configured",
                Caption: null,
                Provider: Provider);
        }

        try
        {
            // Read image and convert to base64
            var imageBytes = await File.ReadAllBytesAsync(imagePath, ct);
            var base64Image = Convert.ToBase64String(imageBytes);

            // Detect media type
            var extension = Path.GetExtension(imagePath).ToLowerInvariant();
            var mediaType = extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/jpeg"
            };

            var modelToUse = model ?? _defaultModel;

            // Build request according to OpenAI's Chat Completions API with vision
            // Temperature: 0.0 = factual/deterministic, 2.0 = very creative (default 1.0 for OpenAI)
            var request = new
            {
                model = modelToUse,
                temperature = temperature ?? 1.0, // OpenAI default
                max_tokens = 1000,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new
                            {
                                type = "text",
                                text = prompt
                            },
                            new
                            {
                                type = "image_url",
                                image_url = new
                                {
                                    url = $"data:{mediaType};base64,{base64Image}"
                                }
                            }
                        }
                    }
                }
            };

            var response = await _httpClient.PostAsJsonAsync("/v1/chat/completions", request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("OpenAI API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new VisionResult(
                    Success: false,
                    Error: $"API error: {response.StatusCode}",
                    Caption: null,
                    Model: modelToUse,
                    Provider: Provider);
            }

            var result = await response.Content.ReadFromJsonAsync<OpenAIResponse>(ct);

            if (result?.Choices == null || result.Choices.Count == 0)
            {
                return new VisionResult(
                    Success: false,
                    Error: "No response from OpenAI",
                    Caption: null,
                    Model: modelToUse,
                    Provider: Provider);
            }

            var responseText = result.Choices[0].Message.Content;

            // Try to parse as JSON with caption/claims structure
            string? caption = null;
            List<EvidenceClaim>? claims = null;

            // Clean up response: strip markdown code blocks and explanatory text
            var jsonText = responseText.Trim();

            // Strip markdown code blocks (```json ... ```)
            if (jsonText.StartsWith("```"))
            {
                var lines = jsonText.Split('\n');
                jsonText = string.Join('\n', lines.Skip(1).Reverse().Skip(1).Reverse()).Trim();
            }

            // Find JSON object (starts with { and ends with })
            var jsonStart = jsonText.IndexOf('{');
            var jsonEnd = jsonText.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                jsonText = jsonText.Substring(jsonStart, jsonEnd - jsonStart + 1);
            }

            try
            {
                var visionResponse = System.Text.Json.JsonSerializer.Deserialize<VisionJsonResponse>(jsonText);
                if (visionResponse?.Caption != null)
                {
                    caption = visionResponse.Caption;
                    claims = visionResponse.Claims?.Select(c => new EvidenceClaim(
                        c.Text,
                        c.Sources ?? new List<string>(),
                        c.Evidence)).ToList();
                    _logger.LogDebug("Parsed structured response with {ClaimCount} evidence claims", claims?.Count ?? 0);
                }
                else
                {
                    // Fallback to plain text
                    caption = responseText;
                }
            }
            catch (Exception ex)
            {
                // Not JSON, use as plain text caption
                _logger.LogDebug("Failed to parse JSON response: {Error}. Using plain text.", ex.Message);
                caption = responseText;
            }

            _logger.LogInformation("OpenAI vision analysis completed for {ImagePath} using {Model}", imagePath, modelToUse);

            return new VisionResult(
                Success: true,
                Error: null,
                Caption: caption,
                Model: modelToUse,
                Provider: Provider,
                Metadata: new Dictionary<string, object>
                {
                    { "finish_reason", result.Choices[0].FinishReason ?? "unknown" },
                    { "prompt_tokens", result.Usage?.PromptTokens ?? 0 },
                    { "completion_tokens", result.Usage?.CompletionTokens ?? 0 },
                    { "total_tokens", result.Usage?.TotalTokens ?? 0 }
                },
                Claims: claims);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI vision analysis failed for {ImagePath}", imagePath);
            return new VisionResult(
                Success: false,
                Error: $"Analysis failed: {ex.Message}",
                Caption: null,
                Provider: Provider);
        }
    }
}

// OpenAI API response models
internal record OpenAIResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] List<OpenAIChoice> Choices,
    [property: JsonPropertyName("usage")] OpenAIUsage? Usage);

internal record OpenAIChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("message")] OpenAIMessage Message,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

internal record OpenAIMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal record OpenAIUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);
