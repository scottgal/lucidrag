using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using System.Text.Json;
using static Mostlylucid.DocSummarizer.Images.Models.Dynamic.ImageLedger;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Vision LLM Wave - Generates captions and extracts entities using vision-language models
/// Uses Ollama with LLaVA, MiniCPM-V, or similar multimodal models
/// Priority: 50 (runs after basic analysis, provides rich features for synthesis)
/// </summary>
public class VisionLlmWave : IAnalysisWave
{
    private readonly ImageConfig _config;
    private readonly ILogger<VisionLlmWave>? _logger;
    private readonly HttpClient _httpClient;

    public string Name => "VisionLlmWave";
    public int Priority => 50; // After basic analysis, before synthesis
    public IReadOnlyList<string> Tags => new[] { SignalTags.Content, "vision", "llm", "ml" };

    public VisionLlmWave(
        IOptions<ImageConfig> config,
        ILogger<VisionLlmWave>? logger = null,
        HttpClient? httpClient = null)
    {
        _config = config.Value;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        // Skip if vision LLM is disabled
        if (!_config.EnableVisionLlm)
        {
            signals.Add(new Signal
            {
                Key = "vision.llm.disabled",
                Value = true,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "vision", "config" }
            });
            return signals;
        }

        try
        {
            // Convert image to base64 for Ollama
            var imageBase64 = await ConvertImageToBase64(imagePath, ct);

            // Generate primary caption
            var caption = await GenerateCaptionAsync(imageBase64, ct);
            if (!string.IsNullOrEmpty(caption))
            {
                signals.Add(new Signal
                {
                    Key = "vision.llm.caption",
                    Value = caption,
                    Confidence = 0.9,
                    Source = Name,
                    Tags = new List<string> { "vision", "caption", "llm" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["model"] = _config.VisionLlmModel ?? "llava",
                        ["generation_method"] = "vision_llm"
                    }
                });
            }

            // Extract entities/objects
            var entities = await ExtractEntitiesAsync(imageBase64, ct);
            if (entities?.Any() == true)
            {
                signals.Add(new Signal
                {
                    Key = "vision.llm.entities",
                    Value = entities,
                    Confidence = 0.85,
                    Source = Name,
                    Tags = new List<string> { "vision", "entities", "llm" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["count"] = entities.Count,
                        ["extraction_method"] = "vision_llm_prompt"
                    }
                });

                // Individual entity signals for easy querying
                foreach (var entity in entities.Take(10))
                {
                    signals.Add(new Signal
                    {
                        Key = $"vision.llm.entity.{entity.Type}",
                        Value = entity.Label,
                        Confidence = entity.Confidence,
                        Source = Name,
                        Tags = new List<string> { "vision", "entity", entity.Type },
                        Metadata = new Dictionary<string, object>
                        {
                            ["attributes"] = entity.Attributes ?? new Dictionary<string, string>()
                        }
                    });
                }
            }

            // Scene classification
            var scene = await ClassifySceneAsync(imageBase64, ct);
            if (!string.IsNullOrEmpty(scene))
            {
                signals.Add(new Signal
                {
                    Key = "vision.llm.scene",
                    Value = scene,
                    Confidence = 0.8,
                    Source = Name,
                    Tags = new List<string> { "vision", "scene", "llm" }
                });
            }

            // Detailed description (for complex images)
            if (_config.VisionLlmGenerateDetailedDescription)
            {
                var description = await GenerateDetailedDescriptionAsync(imageBase64, ct);
                if (!string.IsNullOrEmpty(description))
                {
                    signals.Add(new Signal
                    {
                        Key = "vision.llm.detailed_description",
                        Value = description,
                        Confidence = 0.85,
                        Source = Name,
                        Tags = new List<string> { "vision", "description", "llm" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["length"] = description.Length,
                            ["use_case"] = "complex_image_understanding"
                        }
                    });
                }
            }

            _logger?.LogInformation(
                "Vision LLM analysis complete: Caption={HasCaption}, Entities={EntityCount}, Scene={Scene}",
                !string.IsNullOrEmpty(caption),
                entities?.Count ?? 0,
                scene ?? "none");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Vision LLM analysis failed");
            signals.Add(new Signal
            {
                Key = "vision.llm.error",
                Value = ex.Message,
                Confidence = 0,
                Source = Name,
                Tags = new List<string> { "vision", "error" }
            });
        }

        return signals;
    }

    private async Task<string> ConvertImageToBase64(string imagePath, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(imagePath, ct);
        return Convert.ToBase64String(bytes);
    }

    private async Task<string?> GenerateCaptionAsync(string imageBase64, CancellationToken ct)
    {
        var prompt = "Describe this image in one concise sentence (10-15 words). Focus on the main subject and action.";
        return await QueryVisionLlmAsync(imageBase64, prompt, ct);
    }

    private async Task<List<EntityDetection>?> ExtractEntitiesAsync(string imageBase64, CancellationToken ct)
    {
        var prompt = @"List all visible objects, people, animals, and text in this image.
Format: JSON array with [{""type"": ""person|animal|object|text"", ""label"": ""name"", ""confidence"": 0.0-1.0, ""attributes"": {}}]
Be comprehensive but concise.";

        var response = await QueryVisionLlmAsync(imageBase64, prompt, ct);
        if (string.IsNullOrEmpty(response)) return null;

        try
        {
            // Extract JSON from response (vision LLMs sometimes wrap in markdown)
            var jsonStart = response.IndexOf('[');
            var jsonEnd = response.LastIndexOf(']');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                return JsonSerializer.Deserialize<List<EntityDetection>>(json);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse entity extraction JSON, falling back to simple parsing");
        }

        // Fallback: Simple keyword extraction
        return ParseEntitiesFromText(response);
    }

    private List<EntityDetection> ParseEntitiesFromText(string text)
    {
        var entities = new List<EntityDetection>();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            // Simple heuristic: look for "person", "dog", "car", etc.
            var lowerLine = line.ToLowerInvariant();

            if (lowerLine.Contains("person") || lowerLine.Contains("human") || lowerLine.Contains("man") || lowerLine.Contains("woman"))
                entities.Add(new EntityDetection { Type = "person", Label = "person", Confidence = 0.7 });
            else if (lowerLine.Contains("dog") || lowerLine.Contains("cat") || lowerLine.Contains("animal"))
                entities.Add(new EntityDetection { Type = "animal", Label = ExtractFirstNoun(line), Confidence = 0.7 });
            else if (lowerLine.Contains("text") || lowerLine.Contains("word"))
                entities.Add(new EntityDetection { Type = "text", Label = "text_content", Confidence = 0.7 });
            else
                entities.Add(new EntityDetection { Type = "object", Label = ExtractFirstNoun(line), Confidence = 0.6 });
        }

        return entities.Take(10).ToList();
    }

    private string ExtractFirstNoun(string text)
    {
        // Very simple: take first word that looks like a noun
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.FirstOrDefault(w => w.Length > 2 && char.IsLower(w[0])) ?? "object";
    }

    private async Task<string?> ClassifySceneAsync(string imageBase64, CancellationToken ct)
    {
        var prompt = "What type of scene is this? Choose one: indoor, outdoor, food, nature, urban, document, screenshot, meme, art, other. Answer with just the category.";
        var response = await QueryVisionLlmAsync(imageBase64, prompt, ct);

        // Extract single word category
        if (!string.IsNullOrEmpty(response))
        {
            var words = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.FirstOrDefault()?.ToLowerInvariant();
        }

        return null;
    }

    private async Task<string?> GenerateDetailedDescriptionAsync(string imageBase64, CancellationToken ct)
    {
        var prompt = @"Provide a detailed description of this image including:
1. Main subjects and their actions
2. Setting/location
3. Notable details
4. Mood/atmosphere
Keep it under 100 words.";

        return await QueryVisionLlmAsync(imageBase64, prompt, ct);
    }

    private async Task<string?> QueryVisionLlmAsync(string imageBase64, string prompt, CancellationToken ct)
    {
        try
        {
            var ollamaUrl = _config.OllamaBaseUrl ?? "http://localhost:11434";
            var model = _config.VisionLlmModel ?? "llava";

            var requestBody = new
            {
                model = model,
                prompt = prompt,
                images = new[] { imageBase64 },
                stream = false
            };

            var requestJson = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{ollamaUrl}/api/generate", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Ollama vision LLM request failed: {Status}", response.StatusCode);
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<OllamaResponse>(responseJson);

            return result?.Response;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to query vision LLM");
            return null;
        }
    }
}

/// <summary>
/// Ollama API response
/// </summary>
file class OllamaResponse
{
    public string? Response { get; set; }
}
