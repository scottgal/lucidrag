using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Motion Analysis Wave - Detects motion patterns in animated GIFs using optical flow.
/// Uses Farneback dense optical flow algorithm for comprehensive motion detection.
/// Priority: 40 (runs after VisionLLM to correlate motion with detected entities)
/// </summary>
public class MotionWave : IAnalysisWave
{
    private readonly MotionAnalyzer _motionAnalyzer;
    private readonly ImageConfig _config;
    private readonly ILogger<MotionWave>? _logger;
    private readonly HttpClient _httpClient;

    public string Name => "MotionWave";
    public int Priority => 48; // Just below VisionLLM (50), runs after to correlate with entities
    public IReadOnlyList<string> Tags => new[] { SignalTags.Content, SignalTags.Visual, "motion", "animation" };

    public MotionWave(
        MotionAnalyzer motionAnalyzer,
        IOptions<ImageConfig> config,
        HttpClient? httpClient = null,
        ILogger<MotionWave>? logger = null)
    {
        _motionAnalyzer = motionAnalyzer;
        _config = config.Value;
        _httpClient = httpClient ?? new HttpClient();
        _logger = logger;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        // Check if motion analysis is enabled
        if (!_config.Motion.Enabled)
        {
            signals.Add(new Signal
            {
                Key = "motion.disabled",
                Value = true,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "motion", "config" }
            });
            return signals;
        }

        // Check if this is an animated image
        var isAnimated = context.GetValue<bool>("identity.is_animated");
        var frameCount = context.GetValue<int>("identity.frame_count");

        if (!isAnimated || frameCount < 2)
        {
            signals.Add(new Signal
            {
                Key = "motion.not_animated",
                Value = true,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "motion", "skip" },
                Metadata = new Dictionary<string, object>
                {
                    ["reason"] = frameCount < 2 ? "single_frame" : "not_animated",
                    ["frame_count"] = frameCount
                }
            });
            return signals;
        }

        try
        {
            _logger?.LogInformation("Starting motion analysis for {ImagePath} ({FrameCount} frames)",
                imagePath, frameCount);

            var result = await _motionAnalyzer.AnalyzeAsync(
                imagePath,
                _config.Motion.MaxFramesToAnalyze,
                ct);

            if (result.Error != null)
            {
                signals.Add(new Signal
                {
                    Key = "motion.error",
                    Value = result.Error,
                    Confidence = 0,
                    Source = Name,
                    Tags = new List<string> { "motion", "error" }
                });
                return signals;
            }

            // Core motion signals
            signals.Add(new Signal
            {
                Key = "motion.has_motion",
                Value = result.HasMotion,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "motion", "detection" }
            });

            signals.Add(new Signal
            {
                Key = "motion.type",
                Value = result.MotionType,
                Confidence = result.HasMotion ? 0.85 : 1.0,
                Source = Name,
                Tags = new List<string> { "motion", "classification" },
                Metadata = new Dictionary<string, object>
                {
                    ["types"] = new[] { "static", "panning", "radial", "rotating", "oscillating", "object_motion", "general" }
                }
            });

            signals.Add(new Signal
            {
                Key = "motion.direction",
                Value = result.DominantDirection,
                Confidence = result.DominantDirectionConfidence,
                Source = Name,
                Tags = new List<string> { "motion", "direction" },
                Metadata = new Dictionary<string, object>
                {
                    ["consistency"] = result.DirectionConsistency
                }
            });

            signals.Add(new Signal
            {
                Key = "motion.magnitude",
                Value = result.AverageMagnitude,
                Confidence = 0.9,
                Source = Name,
                Tags = new List<string> { "motion", "magnitude" },
                Metadata = new Dictionary<string, object>
                {
                    ["average"] = result.AverageMagnitude,
                    ["max"] = result.MaxMagnitude,
                    ["unit"] = "pixels_per_frame"
                }
            });

            signals.Add(new Signal
            {
                Key = "motion.activity",
                Value = result.MotionActivity,
                Confidence = 0.9,
                Source = Name,
                Tags = new List<string> { "motion", "activity" },
                Metadata = new Dictionary<string, object>
                {
                    ["description"] = "fraction of image with motion (0-1)"
                }
            });

            signals.Add(new Signal
            {
                Key = "motion.temporal_consistency",
                Value = result.TemporalConsistency,
                Confidence = 0.85,
                Source = Name,
                Tags = new List<string> { "motion", "consistency" },
                Metadata = new Dictionary<string, object>
                {
                    ["description"] = "how consistent motion is over time (0-1)"
                }
            });

            // Motion regions (if any)
            if (result.MotionRegions.Count > 0)
            {
                signals.Add(new Signal
                {
                    Key = "motion.regions",
                    Value = result.MotionRegions,
                    Confidence = 0.8,
                    Source = Name,
                    Tags = new List<string> { "motion", "regions" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["count"] = result.MotionRegions.Count,
                        ["top_region"] = result.MotionRegions.FirstOrDefault()!
                    }
                });

                // Add individual region signals for the top regions
                for (int i = 0; i < Math.Min(3, result.MotionRegions.Count); i++)
                {
                    var region = result.MotionRegions[i];
                    signals.Add(new Signal
                    {
                        Key = $"motion.region.{i + 1}",
                        Value = $"({region.X},{region.Y}) {region.Width}x{region.Height}",
                        Confidence = 0.75,
                        Source = Name,
                        Tags = new List<string> { "motion", "region" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["x"] = region.X,
                            ["y"] = region.Y,
                            ["width"] = region.Width,
                            ["height"] = region.Height,
                            ["magnitude"] = region.Magnitude
                        }
                    });
                }
            }

            // Summary signal for quick access
            signals.Add(new Signal
            {
                Key = "motion.summary",
                Value = GenerateMotionSummary(result),
                Confidence = 0.85,
                Source = Name,
                Tags = new List<string> { "motion", "summary" }
            });

            // Identify WHAT is moving using Vision LLM (if motion detected and LLM enabled)
            _logger?.LogDebug("Motion identification check: HasMotion={HasMotion}, EnableVisionLlm={EnableVisionLlm}, EnableMotionIdentification={EnableMotionIdentification}",
                result.HasMotion, _config.EnableVisionLlm, _config.Motion.EnableMotionIdentification);

            if (result.HasMotion && _config.EnableVisionLlm && _config.Motion.EnableMotionIdentification)
            {
                _logger?.LogInformation("Attempting to identify moving objects for {ImagePath}", imagePath);
                var (movingObjects, isInferred) = await IdentifyMovingObjectsAsync(imagePath, result, context, ct);
                _logger?.LogDebug("IdentifyMovingObjectsAsync returned {Count} objects (inferred={Inferred})",
                    movingObjects?.Count ?? 0, isInferred);

                if (movingObjects != null && movingObjects.Count > 0)
                {
                    // Reduce confidence if inferred from entities rather than directly observed
                    var confidence = isInferred ? 0.65 : 0.85;

                    signals.Add(new Signal
                    {
                        Key = "motion.moving_objects",
                        Value = movingObjects,
                        Confidence = confidence,
                        Source = Name,
                        Tags = isInferred
                            ? new List<string> { "motion", "objects", "inferred" }
                            : new List<string> { "motion", "objects", "llm" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["count"] = movingObjects.Count,
                            ["primary_object"] = movingObjects.FirstOrDefault() ?? "unknown",
                            ["inferred_from_entities"] = isInferred,
                            ["method"] = isInferred ? "entity_correlation" : "vision_llm"
                        }
                    });

                    // Add individual moving object signals
                    foreach (var obj in movingObjects.Take(5))
                    {
                        signals.Add(new Signal
                        {
                            Key = $"motion.moving.{obj.Replace(" ", "_").ToLowerInvariant()}",
                            Value = obj,
                            Confidence = isInferred ? 0.6 : 0.8,
                            Source = Name,
                            Tags = new List<string> { "motion", "object", "moving" }
                        });
                    }

                    _logger?.LogInformation("Identified moving objects ({Method}): {Objects}",
                        isInferred ? "inferred" : "observed", string.Join(", ", movingObjects));
                }
            }

            _logger?.LogInformation(
                "Motion analysis complete: type={Type}, direction={Direction}, magnitude={Magnitude:F2}",
                result.MotionType, result.DominantDirection, result.AverageMagnitude);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Motion analysis failed for {ImagePath}", imagePath);
            signals.Add(new Signal
            {
                Key = "motion.error",
                Value = ex.Message,
                Confidence = 0,
                Source = Name,
                Tags = new List<string> { "motion", "error" }
            });
        }

        return signals;
    }

    // Shared classification helpers (DRY)
    // Only filter generic CV placeholder labels, NOT language stopwords (breaks non-English)
    private static readonly HashSet<string> InvalidGenericLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        // Generic CV placeholders that aren't meaningful as moving objects
        "object", "unknown", "unidentified", "undefined", "none", "null", "n/a",
        "thing", "item", "element", "entity", "instance"
    };

    private static bool IsValidMovingObjectLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return false;
        // Too short or too long labels are likely garbage
        if (label.Length < 2 || label.Length > 100)
            return false;
        if (InvalidGenericLabels.Contains(label))
            return false;
        // Skip numeric-only labels
        if (label.All(c => char.IsDigit(c) || char.IsWhiteSpace(c) || c == '.' || c == ','))
            return false;
        return true;
    }

    private static string ClassifyIntensity(double magnitude) => magnitude switch
    {
        < 2 => "subtle",
        < 5 => "moderate",
        < 10 => "significant",
        _ => "rapid"
    };

    private static string ClassifyCoverage(double activity) => activity switch
    {
        < 0.1 => "localized",
        < 0.3 => "partial",
        < 0.6 => "widespread",
        _ => "full-frame"
    };

    private string GenerateMotionSummary(MotionAnalysisResult result)
    {
        if (!result.HasMotion)
            return "Static image with no significant motion";

        var direction = result.DominantDirection != "stationary"
            ? $"{result.DominantDirection} "
            : "";

        var intensity = ClassifyIntensity(result.AverageMagnitude);
        var coverage = ClassifyCoverage(result.MotionActivity);

        return $"{intensity.ToUpperInvariant()} {direction}{result.MotionType} motion ({coverage} coverage)";
    }

    /// <summary>
    /// Identify what objects are moving using Vision LLM.
    /// Combines optical flow data with vision understanding.
    /// Returns a tuple of (moving objects list, whether the result was inferred from entities).
    /// </summary>
    private async Task<(List<string>? MovingObjects, bool IsInferred)> IdentifyMovingObjectsAsync(
        string imagePath,
        MotionAnalysisResult motionResult,
        AnalysisContext context,
        CancellationToken ct)
    {
        try
        {
            // Gather salient signals from context to provide rich context to the LLM
            var salientContext = GatherSalientSignals(context);

            _logger?.LogDebug("Motion identification context: {SignalCount} salient signals gathered",
                salientContext.Count);

            // Query Vision LLM specifically about what's moving
            var imageBytes = await File.ReadAllBytesAsync(imagePath, ct);
            var imageBase64 = Convert.ToBase64String(imageBytes);

            var prompt = BuildMotionIdentificationPrompt(motionResult, salientContext);
            var response = await QueryVisionLlmAsync(imageBase64, prompt, ct);

            if (string.IsNullOrEmpty(response))
            {
                _logger?.LogWarning("Vision LLM returned empty response for motion identification");
                return (null, false);
            }

            // Parse the response to extract moving objects
            var parsed = ParseMovingObjects(response);
            _logger?.LogDebug("Parsed {Count} moving objects from Vision LLM response", parsed.Count);

            // If LLM found moving objects directly, return them
            if (parsed.Count > 0)
            {
                return (parsed, false);
            }

            // If LLM couldn't see motion (single frame limitation), fall back to salient signals
            var entities = salientContext.GetValueOrDefault("entities") as List<EntityDetection>;
            if (entities != null && entities.Count > 0)
            {
                _logger?.LogInformation("Vision LLM couldn't see animation (single frame), inferring from {Count} detected entities",
                    entities.Count);

                // Use the entities detected by VisionLlmWave as likely moving objects
                // Filter out generic terms and stopwords
                var inferred = entities
                    .Select(e => !string.IsNullOrWhiteSpace(e.Label) ? e.Label : e.Type)
                    .Where(IsValidMovingObjectLabel)
                    .Distinct()
                    .Take(5)
                    .ToList();

                if (inferred.Count > 0)
                {
                    return (inferred, true);
                }
            }

            return (null, false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to identify moving objects");
            return (null, false);
        }
    }

    /// <summary>
    /// Gather salient signals from context based on confidence and importance.
    /// Only passes structured features (types, counts, measurements) to avoid compounding errors
    /// from free-form text like captions/descriptions which may contain inaccuracies.
    /// </summary>
    private Dictionary<string, object?> GatherSalientSignals(AnalysisContext context)
    {
        var salient = new Dictionary<string, object?>();

        // Entity TYPES only (not labels/descriptions which may compound errors)
        var entities = context.GetValue<List<EntityDetection>>("vision.llm.entities");
        if (entities != null && entities.Count > 0)
        {
            // Store full entities for fallback, but prompt will only use types
            salient["entities"] = entities;
            salient["entity_types"] = entities
                .Select(e => e.Type)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct()
                .ToList();
        }

        // NOTE: We deliberately DON'T pass caption or description
        // Free-form text can compound errors - LLM should see the image fresh

        // Has OCR text (boolean) - indicates text-heavy content without passing potentially garbled text
        var ocrText = context.GetValue<string>("ocr.final.corrected_text")
            ?? context.GetValue<string>("ocr.text.voting_consensus")
            ?? context.GetValue<string>("ocr.text.raw");
        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            salient["has_text"] = true;
            salient["text_word_count"] = ocrText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        }

        // Text likeliness score
        var textLikeliness = context.GetValue<double>("content.text_likeliness");
        if (textLikeliness > 0.3)
        {
            salient["text_likeliness"] = textLikeliness;
        }

        // Dominant color names only (structured)
        var dominantColors = context.GetValue<List<DominantColor>>("color.dominant_colors");
        if (dominantColors != null && dominantColors.Count > 0)
        {
            salient["dominant_colors"] = dominantColors.Take(3).Select(c => c.Name).ToList();
        }

        // Face count (structured measurement)
        var hasFaces = context.GetValue<bool>("faces.detected");
        var faceCount = context.GetValue<int>("faces.count");
        if (hasFaces || faceCount > 0)
        {
            salient["face_count"] = faceCount;
        }

        // Image dimensions
        var width = context.GetValue<int>("identity.width");
        var height = context.GetValue<int>("identity.height");
        if (width > 0 && height > 0)
        {
            salient["dimensions"] = $"{width}x{height}";
            salient["aspect_ratio"] = Math.Round((double)width / height, 2);
        }

        // Frame count (for context about animation length)
        var frameCount = context.GetValue<int>("identity.frame_count");
        if (frameCount > 1)
        {
            salient["frame_count"] = frameCount;
        }

        return salient;
    }

    /// <summary>
    /// Build a context-aware prompt for motion identification.
    /// Only includes structured features (not free-form text) to avoid compounding errors.
    /// Adapts based on available context budget.
    /// </summary>
    private string BuildMotionIdentificationPrompt(
        MotionAnalysisResult motion,
        Dictionary<string, object?> salientContext)
    {
        // Estimate available context: vision models typically have 2k-4k context for prompts
        // Keep prompt concise - the image itself takes most of the context
        const int maxPromptChars = 800;

        var prompt = new System.Text.StringBuilder();

        // Core task (always included - ~200 chars)
        prompt.AppendLine("Animated image. What objects/elements are MOVING?");
        prompt.AppendLine();

        // Motion facts (compact, ~150 chars) - use shared classifiers
        var intensity = ClassifyIntensity(motion.AverageMagnitude);
        var coverage = ClassifyCoverage(motion.MotionActivity);
        prompt.AppendLine($"Motion: {motion.MotionType}, {motion.DominantDirection}, {intensity}, {coverage}");

        // Track remaining budget
        var remainingBudget = maxPromptChars - prompt.Length - 150; // Reserve 150 for instructions

        // Add structured hints based on budget priority:
        // 1. Entity types (highest priority - tells LLM what to look for)
        // 2. Face count (people often move)
        // 3. Has text (scrolling text)
        // 4. Dimensions/frames (for context)

        if (remainingBudget > 100 && salientContext.TryGetValue("entity_types", out var typesObj) && typesObj is List<string> types && types.Count > 0)
        {
            var typeStr = $"Objects: {string.Join(", ", types.Take(5))}";
            if (typeStr.Length < remainingBudget)
            {
                prompt.AppendLine(typeStr);
                remainingBudget -= typeStr.Length + 2;
            }
        }

        if (remainingBudget > 30 && salientContext.TryGetValue("face_count", out var faceObj) && faceObj is int faces && faces > 0)
        {
            prompt.AppendLine($"Faces: {faces}");
            remainingBudget -= 15;
        }

        if (remainingBudget > 30 && salientContext.TryGetValue("has_text", out var hasTextObj) && hasTextObj is bool hasText && hasText)
        {
            var wordCount = salientContext.GetValueOrDefault("text_word_count") as int? ?? 0;
            prompt.AppendLine(wordCount > 10 ? "Contains text (may be scrolling)" : "Has text");
            remainingBudget -= 35;
        }

        if (remainingBudget > 40 && salientContext.TryGetValue("frame_count", out var frameObj) && frameObj is int frameCount)
        {
            prompt.AppendLine($"Frames: {frameCount}");
        }

        // Compact instructions
        prompt.AppendLine();
        prompt.AppendLine("List MOVING items only, one per line. Be specific.");

        return prompt.ToString();
    }

    private List<string> ParseMovingObjects(string response)
    {
        var objects = new List<string>();

        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            // Skip meta-commentary and LLM echoing the prompt context
            if (line.StartsWith("Based on", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("The", StringComparison.OrdinalIgnoreCase) && line.Contains("moving") ||
                line.StartsWith("I can see", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("In this", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("No motion", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("There is no", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("All objects", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Objects detected", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Scene description", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Motion analysis", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("stationary", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("no visible motion", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("appears to be still", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("nothing listed", StringComparison.OrdinalIgnoreCase))
                continue;

            // Clean up common prefixes
            var cleanedLine = line
                .TrimStart('-', '*', '•', '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '.', ' ', '\t')
                .Trim();

            // Skip very short or generic labels
            if (string.IsNullOrEmpty(cleanedLine) || cleanedLine.Length <= 2 || cleanedLine.Length >= 100)
                continue;

            // Skip lines that are just category labels without specifics
            if (cleanedLine.Equals("object", StringComparison.OrdinalIgnoreCase) ||
                cleanedLine.Equals("person", StringComparison.OrdinalIgnoreCase) ||
                cleanedLine.Equals("text", StringComparison.OrdinalIgnoreCase) ||
                cleanedLine.Equals("animal", StringComparison.OrdinalIgnoreCase))
                continue;

            objects.Add(cleanedLine);
        }

        return objects.Distinct().Take(10).ToList();
    }

    private async Task<string?> QueryVisionLlmAsync(string imageBase64, string prompt, CancellationToken ct)
    {
        try
        {
            var ollamaUrl = _config.OllamaBaseUrl ?? "http://localhost:11434";
            var model = _config.VisionLlmModel ?? "llava";

            _logger?.LogDebug("Querying Vision LLM for motion: url={Url}, model={Model}, timeout={Timeout}ms",
                ollamaUrl, model, _config.VisionLlmTimeout);

            var requestBody = new
            {
                model = model,
                prompt = prompt,
                images = new[] { imageBase64 },
                stream = false
            };

            var requestJson = System.Text.Json.JsonSerializer.Serialize(requestBody);
            var content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(_config.VisionLlmTimeout));

            var response = await _httpClient.PostAsync($"{ollamaUrl}/api/generate", content, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger?.LogWarning("Ollama motion identification request failed: {Status}, body: {Body}",
                    response.StatusCode, errorBody);
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            _logger?.LogDebug("Ollama response received: {Length} chars", responseJson.Length);

            var result = System.Text.Json.JsonSerializer.Deserialize<OllamaMotionResponse>(responseJson);

            return result?.Response;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Motion identification timed out after {Timeout}ms", _config.VisionLlmTimeout);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "HTTP error querying vision LLM - is Ollama running?");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to query vision LLM for motion identification");
            return null;
        }
    }
}

/// <summary>
/// Ollama API response for motion identification
/// </summary>
file class OllamaMotionResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("response")]
    public string? Response { get; set; }
}
