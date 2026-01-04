using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LucidRAG.ImageCli.Services;

/// <summary>
/// Parses natural language queries into structured filter criteria using a small LLM.
/// Enables queries like "show me sunset images with the sea" or "green abstract images".
/// </summary>
public class NaturalLanguageQueryParser
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly ILogger<NaturalLanguageQueryParser>? _logger;

    public NaturalLanguageQueryParser(
        HttpClient httpClient,
        string model = "tinyllama",
        ILogger<NaturalLanguageQueryParser>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _model = model;
        _logger = logger;
    }

    /// <summary>
    /// Parse a natural language query into structured filter criteria.
    /// </summary>
    public async Task<ImageQueryCriteria> ParseQueryAsync(string query, CancellationToken ct = default)
    {
        var prompt = $$"""
            Parse this image search query into structured criteria. Extract:
            - Keywords: Important visual elements (sunset, sea, mountains, person, etc.)
            - Colors: Dominant colors mentioned (red, blue, green, etc.)
            - Image type: Photo, Screenshot, Diagram, Chart, Abstract, Icon, Logo
            - Quality filters: High resolution, blurry, sharp, etc.
            - Content type: Has text, no text, etc.

            Query: "{{query}}"

            Respond with JSON only, no explanation:
            {
              "keywords": ["keyword1", "keyword2"],
              "colors": ["color1", "color2"],
              "imageType": "Photo|Screenshot|Diagram|Chart|Abstract|Icon|Logo|null",
              "hasText": true|false|null,
              "minSharpness": 0.0-1.0|null,
              "minResolution": "low|medium|high|null"
            }
            """;

        try
        {
            var request = new
            {
                model = _model,
                prompt = prompt,
                stream = false,
                format = "json"
            };

            var response = await _httpClient.PostAsJsonAsync("/api/generate", request, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Query parsing failed: {Status}", response.StatusCode);
                return ImageQueryCriteria.FromKeywords(ExtractKeywordsSimple(query));
            }

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(ct);
            if (result?.Response == null)
            {
                return ImageQueryCriteria.FromKeywords(ExtractKeywordsSimple(query));
            }

            var criteria = JsonSerializer.Deserialize<ImageQueryCriteria>(result.Response);
            return criteria ?? ImageQueryCriteria.FromKeywords(ExtractKeywordsSimple(query));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse query with LLM, falling back to simple keyword extraction");
            return ImageQueryCriteria.FromKeywords(ExtractKeywordsSimple(query));
        }
    }

    /// <summary>
    /// Simple fallback keyword extraction when LLM is unavailable.
    /// </summary>
    private static List<string> ExtractKeywordsSimple(string query)
    {
        // Remove common words and split
        var stopWords = new HashSet<string> { "show", "me", "all", "images", "with", "a", "an", "the", "that", "are", "is" };

        return query.ToLowerInvariant()
            .Split(new[] { ' ', ',', '.', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !stopWords.Contains(w))
            .ToList();
    }

    private record OllamaGenerateResponse(
        [property: JsonPropertyName("response")] string Response,
        [property: JsonPropertyName("done")] bool Done);
}

/// <summary>
/// Structured image query criteria parsed from natural language.
/// </summary>
public class ImageQueryCriteria
{
    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = new();

    [JsonPropertyName("colors")]
    public List<string> Colors { get; set; } = new();

    [JsonPropertyName("imageType")]
    public string? ImageType { get; set; }

    [JsonPropertyName("hasText")]
    public bool? HasText { get; set; }

    [JsonPropertyName("minSharpness")]
    public double? MinSharpness { get; set; }

    [JsonPropertyName("minResolution")]
    public string? MinResolution { get; set; }

    public static ImageQueryCriteria FromKeywords(List<string> keywords)
    {
        return new ImageQueryCriteria { Keywords = keywords };
    }

    /// <summary>
    /// Check if an image profile matches these criteria.
    /// Uses weighted scoring with multiple signals.
    /// </summary>
    public double CalculateMatchScore(dynamic profile)
    {
        double score = 0.0;
        int criteriaCount = 0;

        // Check keywords (if we have OCR or vision LLM description)
        if (Keywords.Any())
        {
            criteriaCount++;
            var ocrText = profile.GetValue<string>("ocr.full_text") ?? "";
            var description = profile.GetValue<string>("content.vision_llm_description") ?? "";
            var combinedText = $"{ocrText} {description}".ToLowerInvariant();

            var matchedKeywords = Keywords.Count(kw => combinedText.Contains(kw.ToLowerInvariant()));
            score += (double)matchedKeywords / Keywords.Count;
        }

        // Check colors
        if (Colors.Any())
        {
            criteriaCount++;
            var dominantColors = profile.GetValue<List<object>>("color.dominant_colors");
            if (dominantColors != null)
            {
                var colorNames = ((IEnumerable<object>)dominantColors)
                    .Select(c => c.ToString()?.ToLowerInvariant() ?? "")
                    .ToList();

                var matchedColors = Colors.Count(c =>
                    ((IEnumerable<string>)colorNames).Any(cn => cn.Contains(c.ToLowerInvariant())));

                score += (double)matchedColors / Colors.Count;
            }
        }

        // Check image type
        if (!string.IsNullOrWhiteSpace(ImageType))
        {
            criteriaCount++;
            var detectedType = profile.GetValue<string>("content.type");
            if (detectedType?.Equals(ImageType, StringComparison.OrdinalIgnoreCase) == true)
            {
                score += 1.0;
            }
        }

        // Check text presence
        if (HasText.HasValue)
        {
            criteriaCount++;
            var textLikeliness = profile.GetValue<double>("content.text_likeliness");
            var hasTextDetected = textLikeliness > 0.4;

            if (hasTextDetected == HasText.Value)
            {
                score += 1.0;
            }
        }

        // Check sharpness
        if (MinSharpness.HasValue)
        {
            criteriaCount++;
            var sharpness = profile.GetValue<double>("quality.sharpness");
            if (sharpness >= MinSharpness.Value)
            {
                score += 1.0;
            }
        }

        // Check resolution
        if (!string.IsNullOrWhiteSpace(MinResolution))
        {
            criteriaCount++;
            var width = profile.GetValue<int>("identity.width");
            var height = profile.GetValue<int>("identity.height");
            var pixels = width * height;

            var meetsResolution = MinResolution.ToLowerInvariant() switch
            {
                "low" => pixels >= 320 * 240,
                "medium" => pixels >= 1280 * 720,
                "high" => pixels >= 1920 * 1080,
                _ => true
            };

            if (meetsResolution)
            {
                score += 1.0;
            }
        }

        // Return normalized score (0.0 - 1.0)
        return criteriaCount > 0 ? score / criteriaCount : 0.0;
    }
}
