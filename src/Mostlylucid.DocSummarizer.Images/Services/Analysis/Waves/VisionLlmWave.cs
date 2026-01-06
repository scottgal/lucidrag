using System.Linq;
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

    /// <summary>
    /// Check if VisionLLM should run - this is expensive (LLM call).
    /// Only runs if enabled in config.
    /// </summary>
    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        // Skip if disabled in config
        return Config.EnableVisionLlm;
    }

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
            // Check if this is an animated image - if so, create a filmstrip for better caption generation
            var isAnimated = context.GetValue<bool>("identity.is_animated");
            var frameCount = context.GetValue<int>("identity.frame_count");

            string imageBase64;
            int framesUsed = 1;

            if (isAnimated && frameCount > 1)
            {
                // For animated GIFs, create a filmstrip showing key frames
                // This lets the LLM see the animation sequence and describe what happens
                var filmstripResult = await CreateFilmstripForCaptionAsync(imagePath, frameCount, ct);
                imageBase64 = filmstripResult.Base64;
                framesUsed = filmstripResult.FrameCount;
                _logger?.LogInformation("Created filmstrip with {FrameCount} frames for caption generation", framesUsed);
            }
            else
            {
                // Static image - use single frame
                imageBase64 = await ConvertImageToBase64(imagePath, ct);
            }

            // Generate primary caption (with motion context if available)
            var caption = await GenerateCaptionAsync(imageBase64, context, ct, framesUsed);
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
                int textFrameCount = 1;

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
                        textFrameCount = frames.Count;
                        _logger?.LogInformation("Created strip of {FrameCount} frames for Vision LLM text extraction", textFrameCount);
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

                var llmText = await ExtractTextAsync(textImageBase64, textFrameCount, ct);
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

            // Detailed description (for complex images or animations)
            if (Config.VisionLlmGenerateDetailedDescription)
            {
                var description = await GenerateDetailedDescriptionAsync(imageBase64, context, ct);
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
        // Adaptive image sizing based on model context
        // Most vision models work best with images under 1024x1024
        // This significantly reduces base64 size and leaves more context for prompts
        var maxDimension = GetMaxImageDimension();

        using var image = await SixLabors.ImageSharp.Image.LoadAsync(imagePath, ct);
        var originalWidth = image.Width;
        var originalHeight = image.Height;

        // Check if resizing needed
        if (image.Width > maxDimension || image.Height > maxDimension)
        {
            var scale = Math.Min(maxDimension / (double)image.Width, maxDimension / (double)image.Height);
            var newWidth = (int)(image.Width * scale);
            var newHeight = (int)(image.Height * scale);

            image.Mutate(x => x.Resize(newWidth, newHeight));
            _logger?.LogDebug("Resized image from {OldW}x{OldH} to {NewW}x{NewH} for LLM",
                originalWidth, originalHeight, newWidth, newHeight);
        }

        // Encode as JPEG for smaller base64 (PNG can be huge)
        using var ms = new MemoryStream();
        await image.SaveAsJpegAsync(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 85 }, ct);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>
    /// Creates a filmstrip of key frames from an animated GIF for caption generation.
    /// Extracts evenly-spaced frames to show the animation sequence.
    /// </summary>
    private async Task<(string Base64, int FrameCount)> CreateFilmstripForCaptionAsync(string imagePath, int totalFrames, CancellationToken ct)
    {
        // Target 4-8 frames for caption filmstrip (enough to show action without overwhelming)
        const int MinFrames = 4;
        const int MaxFrames = 8;
        const int MaxFrameHeight = 256; // Keep frames readable but compact

        var targetFrames = Math.Min(MaxFrames, Math.Max(MinFrames, totalFrames / 3));

        try
        {
            // Load the GIF and extract frames
            using var gif = await SixLabors.ImageSharp.Image.LoadAsync(imagePath, ct);
            var frameCount = gif.Frames.Count;

            if (frameCount <= 1)
            {
                // Not animated, return single frame
                return (await ConvertImageToBase64(imagePath, ct), 1);
            }

            // Calculate which frames to extract (evenly spaced)
            var frameIndices = new List<int>();
            var step = Math.Max(1, (double)(frameCount - 1) / (targetFrames - 1));
            for (int i = 0; i < targetFrames && i * step < frameCount; i++)
            {
                frameIndices.Add((int)(i * step));
            }

            // Extract frames
            var extractedFrames = new List<Image<Rgba32>>();
            foreach (var idx in frameIndices)
            {
                if (idx < frameCount)
                {
                    // CloneFrame returns Image, need to convert to Image<Rgba32>
                    using var genericFrame = gif.Frames.CloneFrame(idx);
                    var rgbaFrame = genericFrame.CloneAs<Rgba32>();
                    extractedFrames.Add(rgbaFrame);
                }
            }

            if (extractedFrames.Count == 0)
            {
                return (await ConvertImageToBase64(imagePath, ct), 1);
            }

            // Scale frames to reasonable size while maintaining aspect ratio
            var firstFrame = extractedFrames[0];
            var scale = Math.Min(1.0, (double)MaxFrameHeight / firstFrame.Height);
            var frameWidth = (int)(firstFrame.Width * scale);
            var frameHeight = (int)(firstFrame.Height * scale);

            // Create horizontal filmstrip
            var stripWidth = frameWidth * extractedFrames.Count + (extractedFrames.Count - 1) * 2; // 2px gap between frames
            var stripHeight = frameHeight;

            using var filmstrip = new Image<Rgba32>(stripWidth, stripHeight);
            filmstrip.Mutate(ctx => ctx.BackgroundColor(Color.Black));

            int xOffset = 0;
            foreach (var frame in extractedFrames)
            {
                // Resize frame if needed
                if (frame.Width != frameWidth || frame.Height != frameHeight)
                {
                    frame.Mutate(x => x.Resize(frameWidth, frameHeight));
                }

                filmstrip.Mutate(x => x.DrawImage(frame, new Point(xOffset, 0), 1f));
                xOffset += frameWidth + 2; // 2px gap

                frame.Dispose();
            }

            // Convert to base64
            using var ms = new MemoryStream();
            await filmstrip.SaveAsJpegAsync(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 85 }, ct);
            var base64 = Convert.ToBase64String(ms.ToArray());

            _logger?.LogDebug("Created filmstrip: {Width}x{Height}, {FrameCount} frames from {TotalFrames} total",
                stripWidth, stripHeight, extractedFrames.Count, totalFrames);

            return (base64, frameIndices.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create filmstrip for {Path}, falling back to single frame", imagePath);
            return (await ConvertImageToBase64(imagePath, ct), 1);
        }
    }

    // Cache for model context sizes
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _modelContextCache = new();

    /// <summary>
    /// Get maximum image dimension based on model context size.
    /// Queries Ollama for context window size to adapt image sizing.
    /// </summary>
    private int GetMaxImageDimension()
    {
        var model = Config.VisionLlmModel ?? "";
        var cacheKey = $"{Config.OllamaBaseUrl}:{model}";

        // Try cached value
        if (_modelContextCache.TryGetValue(cacheKey, out var cachedDim))
            return cachedDim;

        // Try to fetch from Ollama
        var contextSize = FetchContextSizeFromOllama(model);
        if (contextSize > 0)
        {
            var dim = ContextSizeToImageDimension(contextSize);
            _modelContextCache[cacheKey] = dim;
            return dim;
        }

        // Fallback to heuristic based on model name
        var modelLower = model.ToLowerInvariant();
        var fallbackDim = modelLower switch
        {
            var m when m.Contains("tiny") || m.Contains("phi") || m.Contains("moondream") => 512,
            var m when m.Contains("7b") || m.Contains("llava:7") || m.Contains("minicpm") => 768,
            var m when m.Contains("13b") || m.Contains("bakllava") => 1024,
            var m when m.Contains("34b") || m.Contains("opus") || m.Contains("gpt-4") => 1536,
            _ => 768
        };

        _modelContextCache[cacheKey] = fallbackDim;
        return fallbackDim;
    }

    /// <summary>
    /// Convert context window size to appropriate image dimension.
    /// Larger context = can handle larger images with more prompt space.
    /// </summary>
    private static int ContextSizeToImageDimension(int contextSize)
    {
        // Rule of thumb: base64 image takes ~1.33x the raw bytes in tokens
        // A 768x768 JPEG at 85% quality is ~50-100KB (~65-130K chars base64)
        // Most models tokenize ~4 chars per token = ~16-32K tokens for image alone
        // We want to leave ~50% of context for prompts and response
        return contextSize switch
        {
            < 4096 => 384,    // Very limited - tiny image
            < 8192 => 512,    // Small context
            < 16384 => 768,   // Standard context
            < 32768 => 1024,  // Large context
            < 65536 => 1280,  // Very large context
            _ => 1536         // Massive context (GPT-4V, Claude, etc.)
        };
    }

    /// <summary>
    /// Fetch context window size from Ollama API.
    /// </summary>
    private int FetchContextSizeFromOllama(string model)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var url = $"{Config.OllamaBaseUrl}/api/show";
            var content = new System.Net.Http.StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { name = model }),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = client.PostAsync(url, content).GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode)
            {
                var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var doc = System.Text.Json.JsonDocument.Parse(json);

                // Try to get context length from model info
                if (doc.RootElement.TryGetProperty("model_info", out var modelInfo))
                {
                    // Look for context length in various places
                    foreach (var prop in modelInfo.EnumerateObject())
                    {
                        var name = prop.Name.ToLowerInvariant();
                        if (name.Contains("context") && prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            return prop.Value.GetInt32();
                        }
                    }
                }

                // Try parameters
                if (doc.RootElement.TryGetProperty("parameters", out var parameters))
                {
                    var paramText = parameters.GetString() ?? "";
                    var match = System.Text.RegularExpressions.Regex.Match(paramText, @"num_ctx\s+(\d+)");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var ctx))
                    {
                        return ctx;
                    }
                }

                _logger?.LogDebug("Ollama model {Model} info retrieved but no context size found", model);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("Failed to fetch Ollama model info for {Model}: {Error}", model, ex.Message);
        }

        return 0; // Fall back to heuristics
    }

    private async Task<string?> GenerateCaptionAsync(string imageBase64, AnalysisContext context, CancellationToken ct, int filmstripFrames = 1)
    {
        // Gather motion and object context from prior waves
        // IMPORTANT: Use correct signal keys from IdentityWave and MotionWave
        var isAnimated = context.GetValue<bool>("identity.is_animated");
        var motionSummary = context.GetValue<string>("motion.summary");
        var motionType = context.GetValue<string>("motion.type"); // MotionWave emits "motion.type" not "motion.motion_type"
        var totalFrameCount = context.GetValue<int>("identity.frame_count"); // IdentityWave emits this, not MotionWave
        var movingObjects = context.GetValue<List<string>>("motion.moving_objects");
        var dominantColors = context.GetValue<List<object>>("color.dominant_colors");

        // Build factual, focused prompt - minimal but grounded
        // Key: avoid "describe" which invites hallucination; ask for specific observable facts
        var promptParts = new List<string>();

        // Check if this is a screenshot/document vs natural image
        var hasText = context.GetValue<double>("content.text_likeliness") > 0.3 ||
                      !string.IsNullOrEmpty(context.GetValue<string>("ocr.full_text"));
        var isDocument = context.GetValue<bool>("content.is_document");

        if (hasText || isDocument)
        {
            // For screenshots/documents, focus on what it IS and what text says
            promptParts.Add("What is this screenshot of? Include any visible text.");
        }
        else if (isAnimated && filmstripFrames > 1)
        {
            // FILMSTRIP MODE: The image shows multiple frames arranged horizontally
            // Tell the LLM what it's seeing and ask for action description
            promptParts.Add($"This is a FILMSTRIP of {filmstripFrames} sequential frames from an animated GIF ({totalFrameCount} total frames).");
            promptParts.Add("The frames are arranged LEFT to RIGHT showing the animation sequence.");
            promptParts.Add("DESCRIBE THE ACTION: What happens from start to finish? What moves or changes?");

            if (!string.IsNullOrEmpty(motionType))
                promptParts.Add($"Motion type detected: {motionType}.");

            if (movingObjects?.Any() == true)
                promptParts.Add($"Moving elements: {string.Join(", ", movingObjects.Take(3))}.");

            promptParts.Add("Write a concise caption describing the animation action:");
        }
        else if (isAnimated && totalFrameCount > 1)
        {
            // Single frame from animation (no filmstrip available)
            promptParts.Add($"Alt text for {totalFrameCount}-frame animation");

            if (!string.IsNullOrEmpty(motionType))
                promptParts[^1] += $" ({motionType})";

            if (movingObjects?.Any() == true)
                promptParts[^1] += $" showing {string.Join(", ", movingObjects.Take(3))}";

            promptParts.Add(":");
        }
        else
        {
            // For photos, ask for factual description
            promptParts.Add("Factual alt text (only describe what you actually see):");
        }

        var prompt = string.Join(" ", promptParts);
        var response = await QueryVisionLlmAsync(imageBase64, prompt, ct);

        // Clean and extract caption
        var cleaned = CleanCaptionResponse(response);

        // Apply temporal verb gate for static images
        // "Probability proposes, determinism persists" - don't claim motion without evidence
        if (!isAnimated && filmstripFrames <= 1)
        {
            cleaned = GateTemporalVerbs(cleaned);
        }

        return cleaned;
    }

    /// <summary>
    /// Gate temporal verbs in captions for static images.
    /// When is_animated=false, we have no temporal evidence, so action verbs
    /// like "moving", "dancing", "walking" must be converted to static equivalents.
    /// This enforces "probability proposes, determinism persists".
    /// </summary>
    private string? GateTemporalVerbs(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
            return caption;

        var result = caption;

        // Map temporal verbs to static equivalents
        // Order matters: longer phrases first to avoid partial matches
        var temporalToStatic = new (string Pattern, string Replacement)[]
        {
            // Continuous actions → static poses
            (@"\bis moving\b", "appears in motion"),
            (@"\bare moving\b", "appear in motion"),
            (@"\bis dancing\b", "is in a dance pose"),
            (@"\bare dancing\b", "are in dance poses"),
            (@"\bis walking\b", "is mid-stride"),
            (@"\bare walking\b", "are mid-stride"),
            (@"\bis running\b", "is in a running pose"),
            (@"\bare running\b", "are in running poses"),
            (@"\bis jumping\b", "is mid-jump"),
            (@"\bare jumping\b", "are mid-jump"),
            (@"\bis waving\b", "has arm raised"),
            (@"\bare waving\b", "have arms raised"),
            (@"\bis gesturing\b", "is mid-gesture"),
            (@"\bare gesturing\b", "are mid-gesture"),
            (@"\bis spinning\b", "is in a spin pose"),
            (@"\bis turning\b", "is mid-turn"),
            (@"\bis swinging\b", "is mid-swing"),
            (@"\bis nodding\b", "has head tilted"),
            (@"\bis shaking\b", "appears to shake"),
            (@"\bis bouncing\b", "is mid-bounce"),

            // Adverbs implying continuous motion
            (@"\brhythmically\b", "in a rhythmic pose"),
            (@"\bcontinuously\b", ""),
            (@"\brepeatedly\b", ""),

            // Present participles suggesting ongoing action
            (@"\bmoving their\b", "with their"),
            (@"\bswinging their\b", "with their"),
            (@"\braising their\b", "with their"),
            (@"\blowering their\b", "with their"),
        };

        foreach (var (pattern, replacement) in temporalToStatic)
        {
            result = System.Text.RegularExpressions.Regex.Replace(
                result, pattern, replacement,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // Clean up any double spaces from removals
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s{2,}", " ").Trim();

        // Log if we made changes
        if (result != caption)
        {
            _logger?.LogDebug("Temporal verb gate applied: '{Original}' → '{Gated}'",
                caption?.Substring(0, Math.Min(50, caption?.Length ?? 0)),
                result.Substring(0, Math.Min(50, result.Length)));
        }

        return result;
    }

    /// <summary>
    /// Clean LLM response, removing prompt leakage and extracting actual caption.
    /// </summary>
    private string? CleanCaptionResponse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return null;

        var cleaned = response.Trim();

        // Try to extract from JSON format: {"caption": "..."}
        try
        {
            var jsonStart = cleaned.IndexOf('{');
            var jsonEnd = cleaned.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = cleaned.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("caption", out var captionProp))
                {
                    var caption = captionProp.GetString();
                    if (!string.IsNullOrWhiteSpace(caption))
                        return SanitizeCaption(caption);
                }
                // Also check for "description", "scene" etc
                foreach (var propName in new[] { "description", "scene", "summary" })
                {
                    if (doc.RootElement.TryGetProperty(propName, out var prop))
                    {
                        var val = prop.GetString();
                        if (!string.IsNullOrWhiteSpace(val))
                            return SanitizeCaption(val);
                    }
                }
            }
        }
        catch
        {
            // JSON parsing failed
        }

        // Regex fallback for partial JSON
        var match = System.Text.RegularExpressions.Regex.Match(cleaned, @"""(?:caption|description)""\s*:\s*""([^""]+)""");
        if (match.Success && match.Groups.Count > 1)
        {
            return SanitizeCaption(match.Groups[1].Value);
        }

        // Plain text - sanitize it
        return SanitizeCaption(cleaned);
    }

    /// <summary>
    /// Remove prompt leakage and instruction text from captions.
    /// </summary>
    private string SanitizeCaption(string caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
            return "";

        var result = caption.Trim();

        // Common prompt leakage patterns to strip (order matters - longer patterns first)
        var leakagePatterns = new[]
        {
            // Long verbose patterns (check first - more specific)
            @"^Based on (?:the )?(?:provided |given )?(?:visual )?(?:information|image|analysis).*?(?:here's|here is).*?(?:description|caption|summary).*?[:,]\s*",
            @"^(?:Here is|Here's) (?:a |the )?(?:structured )?(?:output|description|caption|summary).*?(?:in )?(?:JSON )?(?:format)?.*?[:,]\s*",
            @"^.*?(?:in JSON format|JSON format that|structured description|structured output).*?[:,]\s*",
            @"^.*?captures the key.*?[:,]\s*",

            // "The provided/given image" patterns
            @"^(?:The |This )?(?:provided |given )?image (?:appears|seems) to (?:be |show |depict |display |feature |contain )?",
            @"^(?:The |This )?(?:provided |given )?image (?:shows|depicts|displays|features|contains|presents)\s*",
            @"^(?:The |This )?(?:provided |given )?image (?:is |appears to be )(?:a |an )?",

            // Standard patterns
            @"^Based on (?:the |this )?(provided |given )?image.*?[:,]\s*",
            @"^According to (?:the )?(?:provided |given )?(?:image|guidelines|analysis).*?[:,]\s*",
            @"^(?:The |This )?image (?:shows|depicts|displays|features|contains|presents)\s*",
            @"^In (?:the |this )?image,?\s*",
            @"^(?:Here is|Here's) (?:a|the) (?:caption|description).*?:\s*",
            @"^(?:Here is|Here's) (?:a |the )?(?:structured )?.*?[:,]\s*",
            @"^For accessibility[:,]\s*",
            @"^(?:Caption|Description|Summary):\s*",
            @"^\{[^}]*\}\s*", // Leading JSON
            @"^""[^""]*"":\s*""?", // Partial JSON key
            @"\s*\{[^}]*$", // Trailing incomplete JSON
            @"^```(?:json)?\s*", // Code block start
            @"\s*```$", // Code block end
            @"^I (?:can )?see\s+",
            @"^(?:Looking at (?:the|this) image,?\s*)?",
            @"^(?:Sure|Certainly|Of course)[!,.]?\s*",
            @"^(?:From|Given) (?:the|this) (?:image|visual|provided).*?[:,]\s*",
            @"^(?:Visual|Image) analysis (?:shows|indicates|reveals)[:,]?\s*",
            @"^Using (?:the )?(?:provided |given )?image.*?[:,]\s*",
            @"\*\*(?:Caption|Description|Summary)\*\*:?\s*",  // Markdown bold headers
            @"^(?:\*\*)?(?:Caption|Description|Summary)(?:\*\*)?:?\s*",  // With or without markdown
            @"^(?:The )?(?:JSON )?output (?:generated|produced|created).*?(?:includes|contains|describes).*?[:,]\s*",
            @"^(?:The )?(?:generated |produced )?(?:JSON |structured )?(?:output|response|result).*?[:,]\s*",
            @"^(?:The )?image (?:provided|given|shown) (?:seems|appears) to (?:be |show |depict )?",
            @"^.*?(?:does not|doesn't) provide (?:clear |enough )?(?:visual )?(?:information|details).*",
            @"^I (?:observed|noticed|can observe|can see) (?:several |some |many )?(?:notable |key |important )?(?:features|elements|things).*?[.:]\s*",
            @"^In this (?:outdoor |indoor )?(?:setting|scene|image).*?(?:featuring|showing|with)?\s*",
        };

        foreach (var pattern in leakagePatterns)
        {
            result = System.Text.RegularExpressions.Regex.Replace(
                result, pattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // Clean up quotes
        result = result.Trim('"', '\'', ' ');

        // Ensure first letter is capitalized
        if (result.Length > 0 && char.IsLower(result[0]))
        {
            result = char.ToUpper(result[0]) + result[1..];
        }

        // Truncate if too long (WCAG: ~125 chars)
        if (result.Length > 150)
        {
            var lastPeriod = result.LastIndexOf('.', 147);
            if (lastPeriod > 50)
                result = result[..(lastPeriod + 1)];
            else
            {
                var lastSpace = result.LastIndexOf(' ', 147);
                result = lastSpace > 50 ? result[..lastSpace] + "..." : result[..147] + "...";
            }
        }

        return result;
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

    private async Task<string?> GenerateDetailedDescriptionAsync(string imageBase64, AnalysisContext context, CancellationToken ct)
    {
        // Simplified prompt - let the LLM describe what it sees without forcing "animated" language
        var prompt = @"Provide a factual description of this image including:
1. Main subjects and their actions
2. Setting/location
3. Any visible text
4. Notable details
Be factual - only describe what you can see. Keep it under 100 words.";

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
            response.Contains("can't see", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("no visible text", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("no readable text", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("mistake in your request", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("please provide another", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("clarify what", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("I cannot", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("unable to", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("doesn't contain", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("does not contain", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("There seems to be", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Deduplicate repeated lines (frames often show same subtitle)
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var uniqueLines = lines
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("["))  // Skip empty and meta-comments like "[dialogue...]"
            .Distinct()
            .ToList();

        var result = string.Join("\n", uniqueLines);

        // Additional deduplication: remove repeated phrases within a line
        // e.g., "ARSE BISCUITS ARSE BISCUITS ARSE BISCUITS" -> "ARSE BISCUITS"
        result = DeduplicateRepeatedPhrases(result);

        return result;
    }

    /// <summary>
    /// Removes repeated phrases within text. E.g., "Hello World Hello World Hello World" -> "Hello World"
    /// </summary>
    private static string DeduplicateRepeatedPhrases(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // Process each line separately
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var deduplicatedLines = new List<string>();

        foreach (var line in lines)
        {
            var deduped = DeduplicateLineContent(line);
            if (!string.IsNullOrWhiteSpace(deduped))
                deduplicatedLines.Add(deduped);
        }

        return string.Join("\n", deduplicatedLines.Distinct());
    }

    /// <summary>
    /// Deduplicates repeated content within a single line.
    /// Tries to find repeating patterns and reduce to single instance.
    /// </summary>
    private static string DeduplicateLineContent(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length < 4)
            return line;

        // Try to find repeating patterns of various lengths
        for (int patternLen = 2; patternLen <= line.Length / 2; patternLen++)
        {
            var pattern = line.Substring(0, patternLen).Trim();
            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            // Check if this pattern repeats throughout the string
            var cleaned = line.Replace(pattern, "").Trim();

            // If removing the pattern leaves only whitespace/punctuation, it was repeated
            if (cleaned.Length < pattern.Length && string.IsNullOrWhiteSpace(cleaned.Replace(" ", "").Replace(",", "").Replace(".", "")))
            {
                return pattern;
            }
        }

        // Also try splitting by common separators and deduplicating
        var words = line.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length > 2)
        {
            // Check for repeated word sequences
            for (int phraseLen = 1; phraseLen <= words.Length / 2; phraseLen++)
            {
                var phrase = string.Join(" ", words.Take(phraseLen));
                var allMatch = true;

                for (int i = phraseLen; i < words.Length; i += phraseLen)
                {
                    var nextPhrase = string.Join(" ", words.Skip(i).Take(phraseLen));
                    if (!phrase.Equals(nextPhrase, StringComparison.OrdinalIgnoreCase))
                    {
                        allMatch = false;
                        break;
                    }
                }

                if (allMatch)
                    return phrase;
            }
        }

        return line;
    }

    /// <summary>
    /// Creates frame strips for Vision LLM, preserving temporal order.
    /// For long GIFs (>10 frames), splits into multiple chunks with overlap.
    /// Returns list of strips and whether chunking was used.
    /// </summary>
    private (List<Image<Rgba32>> Strips, bool Chunked) CreateFrameStrips(List<Image<Rgba32>> frames)
    {
        if (frames.Count == 0)
            throw new ArgumentException("No frames provided");

        if (frames.Count == 1)
            return (new List<Image<Rgba32>> { frames[0].Clone() }, false);

        // Chunking config
        const int MaxFramesPerStrip = 8; // Optimal for readability
        const int OverlapFrames = 2;     // Overlap between chunks for continuity

        // For short GIFs, create single strip
        if (frames.Count <= MaxFramesPerStrip)
        {
            return (new List<Image<Rgba32>> { CreateSingleStrip(frames) }, false);
        }

        // For long GIFs, create multiple strips with overlap
        _logger?.LogInformation(
            "Long GIF ({FrameCount} frames), splitting into chunks of {Max} with {Overlap} overlap",
            frames.Count, MaxFramesPerStrip, OverlapFrames);

        var strips = new List<Image<Rgba32>>();
        int start = 0;

        while (start < frames.Count)
        {
            int end = Math.Min(start + MaxFramesPerStrip, frames.Count);
            var chunk = frames.GetRange(start, end - start);

            strips.Add(CreateSingleStrip(chunk));
            _logger?.LogDebug("Created strip chunk {Index}: frames {Start}-{End}",
                strips.Count, start, end - 1);

            // Move start forward, accounting for overlap
            start = end - OverlapFrames;

            // Prevent infinite loop for edge cases
            if (start >= frames.Count - 1) break;
        }

        return (strips, true);
    }

    /// <summary>
    /// Creates a horizontal strip of frames for Vision LLM, preserving temporal order.
    /// Uses maximum resolution the model can handle for best text clarity.
    /// </summary>
    private Image<Rgba32> CreateFrameStrip(List<Image<Rgba32>> frames)
    {
        var (strips, _) = CreateFrameStrips(frames);
        // For backward compatibility, return first strip (caller handles multiple)
        if (strips.Count == 1) return strips[0];

        // If multiple strips, combine them vertically
        int totalHeight = strips.Sum(s => s.Height) + (strips.Count - 1) * 4; // 4px gap
        int maxWidth = strips.Max(s => s.Width);

        var combined = new Image<Rgba32>(maxWidth, totalHeight);
        combined.Mutate(ctx => ctx.BackgroundColor(Color.Black));

        int yOffset = 0;
        foreach (var strip in strips)
        {
            combined.Mutate(x => x.DrawImage(strip, new Point(0, yOffset), 1f));
            yOffset += strip.Height + 4;
            strip.Dispose();
        }

        return combined;
    }

    /// <summary>
    /// Creates a single horizontal strip from a subset of frames
    /// </summary>
    private Image<Rgba32> CreateSingleStrip(List<Image<Rgba32>> frames)
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

        // Start with native size
        int totalNativeWidth = nativeWidth * frames.Count;

        // Only scale DOWN if total exceeds model's max
        double scale = Math.Min(1.0, (double)maxStripWidth / totalNativeWidth);
        int finalFrameWidth = (int)(nativeWidth * scale);
        int finalFrameHeight = (int)(nativeHeight * scale);

        // Ensure we don't upscale
        finalFrameWidth = Math.Min(finalFrameWidth, nativeWidth);
        finalFrameHeight = Math.Min(finalFrameHeight, nativeHeight);

        // Minimum size for readability
        finalFrameWidth = Math.Max(finalFrameWidth, 80);
        finalFrameHeight = Math.Max(finalFrameHeight, 60);

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
