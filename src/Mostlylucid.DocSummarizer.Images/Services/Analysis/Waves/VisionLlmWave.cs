using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
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
    private readonly IOptions<ImageConfig> _configOptions;
    private readonly ILogger<VisionLlmWave>? _logger;
    private readonly HttpClient _httpClient;

    // Access config at runtime to get latest values
    private ImageConfig Config => _configOptions.Value;

    public string Name => "VisionLlmWave";
    public int Priority => 50; // After basic analysis, before synthesis
    public IReadOnlyList<string> Tags => new[] { SignalTags.Content, "vision", "llm", "ml" };

    public VisionLlmWave(
        IOptions<ImageConfig> config,
        ILogger<VisionLlmWave>? logger = null,
        HttpClient? httpClient = null)
    {
        _configOptions = config;
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
        if (!Config.EnableVisionLlm)
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
                        ["model"] = Config.VisionLlmModel ?? "llava",
                        ["generation_method"] = "vision_llm"
                    }
                });
            }

            // Check if OCR was unreliable (garbled text or low quality)
            var ocrGarbled = context.GetValue<bool>("ocr.quality.is_garbled");
            var textLikeliness = context.GetValue<double>("content.text_likeliness");
            var ocrText = context.GetValue<string>("ocr.full_text") ?? context.GetValue<string>("ocr.voting.consensus_text");

            // If there's likely text and OCR is unreliable, use Vision LLM to read it
            if (textLikeliness > 0.3 || ocrGarbled || !string.IsNullOrEmpty(ocrText))
            {
                // For animated images, create a mosaic of all unique frames so LLM can read all text
                string textImageBase64 = imageBase64;
                string imageSource = "original";
                int frameCount = 1;

                var frames = context.GetCached<List<Image<Rgba32>>>("ocr.frames");
                if (frames != null && frames.Count > 1)
                {
                    try
                    {
                        // Create a horizontal strip of frames to preserve temporal order (subtitles read left-to-right)
                        using var strip = CreateFrameStrip(frames);
                        var tempPath = Path.Combine(Path.GetTempPath(), $"vllm_strip_{Guid.NewGuid()}.png");
                        await strip.SaveAsPngAsync(tempPath, ct);
                        textImageBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(tempPath, ct));
                        File.Delete(tempPath);
                        imageSource = "frame_strip";
                        frameCount = frames.Count;
                        _logger?.LogInformation("Created strip of {FrameCount} frames for Vision LLM text extraction", frameCount);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to create frame mosaic, using original image");
                    }
                }
                else
                {
                    // Fallback to temporal median if no frames cached
                    var temporalMedian = context.GetCached<Image<Rgba32>>("ocr.temporal_median");
                    if (temporalMedian != null)
                    {
                        try
                        {
                            var tempPath = Path.Combine(Path.GetTempPath(), $"vllm_composite_{Guid.NewGuid()}.png");
                            await temporalMedian.SaveAsPngAsync(tempPath, ct);
                            textImageBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(tempPath, ct));
                            File.Delete(tempPath);
                            imageSource = "temporal_median_composite";
                            _logger?.LogInformation("Using temporal median composite for Vision LLM text extraction");
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Failed to convert temporal median to base64, using original");
                        }
                    }
                }

                var llmText = await ExtractTextAsync(textImageBase64, frameCount, ct);
                if (!string.IsNullOrEmpty(llmText))
                {
                    signals.Add(new Signal
                    {
                        Key = "vision.llm.text",
                        Value = llmText,
                        Confidence = 0.95, // High confidence - Vision LLM is preferred over OCR for stylized text
                        Source = Name,
                        Tags = new List<string> { "vision", "text", "llm", SignalTags.Content },
                        Metadata = new Dictionary<string, object>
                        {
                            ["model"] = Config.VisionLlmModel ?? "llava",
                            ["ocr_was_garbled"] = ocrGarbled,
                            ["fallback_reason"] = ocrGarbled ? "ocr_quality_poor" : "text_likely",
                            ["image_source"] = imageSource
                        }
                    });
                }
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
            if (Config.VisionLlmGenerateDetailedDescription)
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

    private async Task<string?> ExtractTextAsync(string imageBase64, int frameCount, CancellationToken ct)
    {
        string prompt;
        if (frameCount > 1)
        {
            prompt = $@"This is a filmstrip showing {frameCount} sequential frames from a movie or TV show.
Look at each frame from left to right. There are yellow/white movie subtitles at the bottom of each frame.
Read ALL the subtitle text you can see in order from left to right. These are important lines of dialogue.
Return only the subtitle text, one line per frame. If a frame has no subtitle, skip it.";
        }
        else
        {
            prompt = @"Read the subtitle text at the bottom of this movie/TV frame.
Focus on the yellow or white text overlay. Read each word carefully.
Return ONLY the subtitle text, nothing else.
If no subtitle text exists, respond with 'NO_TEXT'.";
        }

        var response = await QueryVisionLlmAsync(imageBase64, prompt, ct);

        if (string.IsNullOrEmpty(response) ||
            response.Contains("NO_TEXT", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("<NO_TEXT>", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("no text", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("cannot see", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("no visible text", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("no readable text", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Deduplicate repeated lines (frames often show same subtitle)
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var uniqueLines = lines
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("["))  // Skip empty and meta-comments like "[dialogue...]"
            .Distinct()
            .ToList();

        return string.Join("\n", uniqueLines);
    }

    /// <summary>
    /// Creates a horizontal strip of frames for Vision LLM, preserving temporal order.
    /// Uses maximum resolution the model can handle for best text clarity.
    /// </summary>
    private Image<Rgba32> CreateFrameStrip(List<Image<Rgba32>> frames)
    {
        if (frames.Count == 0)
            throw new ArgumentException("No frames provided");

        if (frames.Count == 1)
            return frames[0].Clone();

        // Model-specific max dimensions (most vision models handle 1024-2048 well)
        // minicpm-v, llava, llama3.2-vision all support at least 1024px
        var model = Config.VisionLlmModel?.ToLowerInvariant() ?? "llava";
        int maxStripWidth = model switch
        {
            var m when m.Contains("minicpm") => 2048, // MiniCPM-V handles high res
            var m when m.Contains("llama3.2") => 1120, // Llama 3.2 vision uses 560x560 per tile
            _ => 1024 // Safe default for most vision models
        };

        // Calculate the best dimensions - NEVER upscale, only downscale if needed
        var firstFrame = frames[0];
        var nativeWidth = firstFrame.Width;
        var nativeHeight = firstFrame.Height;
        var aspectRatio = (double)nativeWidth / nativeHeight;

        // Start with native size
        int totalNativeWidth = nativeWidth * frames.Count;

        // Only scale DOWN if total exceeds model's max
        double scale = Math.Min(1.0, (double)maxStripWidth / totalNativeWidth);
        int finalFrameWidth = (int)(nativeWidth * scale);
        int finalFrameHeight = (int)(nativeHeight * scale);

        // Ensure we don't upscale
        finalFrameWidth = Math.Min(finalFrameWidth, nativeWidth);
        finalFrameHeight = Math.Min(finalFrameHeight, nativeHeight);

        // Recalculate total width
        int totalWidth = finalFrameWidth * frames.Count;

        _logger?.LogDebug("Frame strip: {FrameCount} frames, {Width}x{Height} each, total {Total}px",
            frames.Count, finalFrameWidth, finalFrameHeight, totalWidth);

        // Create strip image
        var strip = new Image<Rgba32>(totalWidth, finalFrameHeight);

        // Draw each frame
        int xOffset = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            var resized = frames[i].Clone();
            resized.Mutate(x => x.Resize(finalFrameWidth, finalFrameHeight));
            strip.Mutate(x => x.DrawImage(resized, new Point(xOffset, 0), 1f));
            resized.Dispose();
            xOffset += finalFrameWidth;
        }

        return strip;
    }

    private async Task<string?> QueryVisionLlmAsync(string imageBase64, string prompt, CancellationToken ct)
    {
        try
        {
            var ollamaUrl = Config.OllamaBaseUrl ?? "http://localhost:11434";
            var model = Config.VisionLlmModel ?? "llava";

            _logger?.LogDebug("VisionLlmWave: Calling {Url} with model {Model}, image size: {Size}KB",
                ollamaUrl, model, imageBase64.Length / 1024);

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
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger?.LogWarning("Ollama vision LLM request failed: {Status}, Body: {Body}",
                    response.StatusCode, errorBody?.Substring(0, Math.Min(200, errorBody?.Length ?? 0)));
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            _logger?.LogDebug("VisionLlmWave: Got response: {Preview}",
                responseJson.Substring(0, Math.Min(200, responseJson.Length)));
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
    [System.Text.Json.Serialization.JsonPropertyName("response")]
    public string? Response { get; set; }
}
