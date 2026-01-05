using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.DocSummarizer.Images.Services.Vision.Clients;

/// <summary>
/// Anthropic Claude Vision client for image analysis
/// Uses Claude 3.5 Sonnet or Claude 3 Opus with vision capabilities
/// </summary>
public class AnthropicVisionClient : IVisionClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AnthropicVisionClient> _logger;
    private readonly string _apiKey;
    private readonly string _defaultModel;

    public string Provider => "Anthropic";

    public AnthropicVisionClient(IConfiguration configuration, ILogger<AnthropicVisionClient> logger)
    {
        _logger = logger;
        _apiKey = configuration["Anthropic:ApiKey"] ??
                  Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ??
                  string.Empty;
        _defaultModel = configuration["Anthropic:VisionModel"] ?? "claude-3-5-sonnet-20241022";

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.anthropic.com"),
            Timeout = TimeSpan.FromMinutes(5)
        };

        if (!string.IsNullOrEmpty(_apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("x-api-key", _apiKey);
            _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        }
    }

    public async Task<(bool Available, string? Message)> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            return (false, "Anthropic API key not configured. Set ANTHROPIC_API_KEY environment variable or configure in appsettings.json");
        }

        try
        {
            // Simple test request to verify API key
            var testRequest = new
            {
                model = _defaultModel,
                max_tokens = 10,
                messages = new[]
                {
                    new { role = "user", content = "Hi" }
                }
            };

            var response = await _httpClient.PostAsJsonAsync("/v1/messages", testRequest, ct);

            if (response.IsSuccessStatusCode)
            {
                return (true, $"Anthropic ready with {_defaultModel}");
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                return (false, $"Anthropic API error: {response.StatusCode} - {errorContent}");
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
                Error: "Anthropic API key not configured",
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

            // Build request according to Anthropic's Messages API
            // Temperature: 0.0 = factual/deterministic, 1.0 = creative (default 1.0 for Anthropic)
            var request = new
            {
                model = modelToUse,
                max_tokens = 1024,
                temperature = temperature ?? 1.0, // Anthropic default
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new
                            {
                                type = "image",
                                source = new
                                {
                                    type = "base64",
                                    media_type = mediaType,
                                    data = base64Image
                                }
                            },
                            new
                            {
                                type = "text",
                                text = prompt
                            }
                        }
                    }
                }
            };

            var response = await _httpClient.PostAsJsonAsync("/v1/messages", request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("Anthropic API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new VisionResult(
                    Success: false,
                    Error: $"API error: {response.StatusCode}",
                    Caption: null,
                    Model: modelToUse,
                    Provider: Provider);
            }

            var result = await response.Content.ReadFromJsonAsync<AnthropicResponse>(ct);

            if (result?.Content == null || result.Content.Count == 0)
            {
                return new VisionResult(
                    Success: false,
                    Error: "No response from Anthropic",
                    Caption: null,
                    Model: modelToUse,
                    Provider: Provider);
            }

            var responseText = result.Content[0].Text;

            // Try to parse as JSON with caption/claims structure
            string? caption = null;
            List<EvidenceClaim>? claims = null;
            VisionMetadata? enhancedMetadata = null;

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

                    // Extract enhanced metadata
                    if (visionResponse.Metadata != null)
                    {
                        enhancedMetadata = new VisionMetadata
                        {
                            Tone = visionResponse.Metadata.Tone,
                            Sentiment = visionResponse.Metadata.Sentiment,
                            Complexity = visionResponse.Metadata.Complexity,
                            AestheticScore = visionResponse.Metadata.AestheticScore,
                            PrimarySubject = visionResponse.Metadata.PrimarySubject,
                            Purpose = visionResponse.Metadata.Purpose,
                            TargetAudience = visionResponse.Metadata.TargetAudience,
                            Confidence = visionResponse.Metadata.Confidence ?? 1.0
                        };
                        _logger.LogWarning("✓ Parsed enhanced metadata: Tone={Tone}, Sentiment={Sentiment}, Complexity={Complexity}, Purpose={Purpose}",
                            enhancedMetadata.Tone, enhancedMetadata.Sentiment, enhancedMetadata.Complexity, enhancedMetadata.Purpose);
                    }
                    else
                    {
                        _logger.LogWarning("✗ JSON response parsed but metadata field is null");
                    }

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
                _logger.LogWarning("Failed to parse JSON response: {Error}. Response preview: {Preview}",
                    ex.Message, responseText.Length > 200 ? responseText.Substring(0, 200) + "..." : responseText);
                caption = responseText;
            }

            _logger.LogInformation("Anthropic vision analysis completed for {ImagePath} using {Model}", imagePath, modelToUse);

            return new VisionResult(
                Success: true,
                Error: null,
                Caption: caption,
                Model: modelToUse,
                Provider: Provider,
                Metadata: new Dictionary<string, object>
                {
                    { "stop_reason", result.StopReason ?? "unknown" },
                    { "input_tokens", result.Usage?.InputTokens ?? 0 },
                    { "output_tokens", result.Usage?.OutputTokens ?? 0 }
                },
                Claims: claims,
                EnhancedMetadata: enhancedMetadata);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Anthropic vision analysis failed for {ImagePath}", imagePath);
            return new VisionResult(
                Success: false,
                Error: $"Analysis failed: {ex.Message}",
                Caption: null,
                Provider: Provider);
        }
    }
}

// Anthropic API response models
internal record AnthropicResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] List<AnthropicContent> Content,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("stop_reason")] string? StopReason,
    [property: JsonPropertyName("usage")] AnthropicUsage? Usage);

internal record AnthropicContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

internal record AnthropicUsage(
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens);

// Vision JSON response models (structured evidence format)
internal record VisionJsonResponse(
    [property: JsonPropertyName("caption")] string? Caption,
    [property: JsonPropertyName("claims")] List<VisionJsonClaim>? Claims,
    [property: JsonPropertyName("metadata")] VisionJsonMetadata? Metadata);

internal record VisionJsonClaim(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("sources")] List<string>? Sources,
    [property: JsonPropertyName("evidence")] List<string>? Evidence);

internal record VisionJsonMetadata(
    [property: JsonPropertyName("tone")] string? Tone,
    [property: JsonPropertyName("sentiment")] double? Sentiment,
    [property: JsonPropertyName("complexity")] double? Complexity,
    [property: JsonPropertyName("aesthetic_score")] double? AestheticScore,
    [property: JsonPropertyName("primary_subject")] string? PrimarySubject,
    [property: JsonPropertyName("purpose")] string? Purpose,
    [property: JsonPropertyName("target_audience")] string? TargetAudience,
    [property: JsonPropertyName("confidence")] double? Confidence);
