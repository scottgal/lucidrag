using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
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

    private string GenerateMotionSummary(MotionAnalysisResult result)
    {
        if (!result.HasMotion)
            return "Static image with no significant motion";

        var direction = result.DominantDirection != "stationary"
            ? $"{result.DominantDirection} "
            : "";

        var intensity = result.AverageMagnitude switch
        {
            < 2 => "subtle",
            < 5 => "moderate",
            < 10 => "significant",
            _ => "rapid"
        };

        var coverage = result.MotionActivity switch
        {
            < 0.1 => "localized",
            < 0.3 => "partial",
            < 0.6 => "widespread",
            _ => "full-frame"
        };

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
            // First, check if VisionLLM already detected entities we can correlate with
            var existingEntities = context.GetValue<List<EntityDetection>>("vision.llm.entities");
            var caption = context.GetValue<string>("vision.llm.caption");

            _logger?.LogDebug("Motion identification context: entities={EntityCount}, caption={HasCaption}",
                existingEntities?.Count ?? 0, !string.IsNullOrEmpty(caption));

            // Query Vision LLM specifically about what's moving
            var imageBytes = await File.ReadAllBytesAsync(imagePath, ct);
            var imageBase64 = Convert.ToBase64String(imageBytes);

            var prompt = BuildMotionIdentificationPrompt(motionResult, existingEntities, caption);
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

            // If LLM couldn't see motion (single frame limitation), fall back to entities
            if (existingEntities != null && existingEntities.Count > 0)
            {
                _logger?.LogInformation("Vision LLM couldn't see animation (single frame), inferring from {Count} detected entities",
                    existingEntities.Count);

                // Use the entities detected by VisionLlmWave as likely moving objects
                // Since we know from optical flow that there IS motion, and we have entities,
                // those entities are likely what's moving
                var inferred = existingEntities
                    .Select(e => !string.IsNullOrWhiteSpace(e.Label) ? e.Label : e.Type)
                    .Where(s => !string.IsNullOrEmpty(s))
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

    private string BuildMotionIdentificationPrompt(
        MotionAnalysisResult motion,
        List<EntityDetection>? entities,
        string? caption)
    {
        var prompt = new System.Text.StringBuilder();

        prompt.AppendLine("This is an animated GIF/image. Analyze the motion and tell me WHAT is moving.");
        prompt.AppendLine();

        // Add motion analysis context
        prompt.AppendLine($"Motion analysis detected: {motion.MotionType} motion");
        prompt.AppendLine($"Direction: {motion.DominantDirection}");
        prompt.AppendLine($"Intensity: {(motion.AverageMagnitude < 2 ? "subtle" : motion.AverageMagnitude < 5 ? "moderate" : "significant")}");
        prompt.AppendLine($"Coverage: {(motion.MotionActivity < 0.3 ? "localized" : "widespread")}");

        if (motion.MotionRegions.Count > 0)
        {
            prompt.AppendLine($"Motion is concentrated in {motion.MotionRegions.Count} region(s)");
        }

        prompt.AppendLine();

        // Add known entities for context
        if (entities != null && entities.Count > 0)
        {
            prompt.AppendLine("Objects detected in the image:");
            foreach (var entity in entities.Take(5))
            {
                prompt.AppendLine($"  - {entity.Type}: {entity.Label}");
            }
            prompt.AppendLine();
        }

        if (!string.IsNullOrEmpty(caption))
        {
            prompt.AppendLine($"Scene description: {caption}");
            prompt.AppendLine();
        }

        prompt.AppendLine("Based on the animation, list the specific objects/people/elements that are MOVING.");
        prompt.AppendLine("Format: One object per line, be specific (e.g., 'person waving hand', 'car driving', 'text scrolling').");
        prompt.AppendLine("Only list things that are actually moving, not static elements.");

        return prompt.ToString();
    }

    private List<string> ParseMovingObjects(string response)
    {
        var objects = new List<string>();

        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            // Skip meta-commentary
            if (line.StartsWith("Based on", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("The", StringComparison.OrdinalIgnoreCase) && line.Contains("moving") ||
                line.StartsWith("I can see", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("In this", StringComparison.OrdinalIgnoreCase))
                continue;

            // Clean up common prefixes
            var cleanedLine = line
                .TrimStart('-', '*', '•', '1', '2', '3', '4', '5', '.', ' ', '\t')
                .Trim();

            if (!string.IsNullOrEmpty(cleanedLine) && cleanedLine.Length > 2 && cleanedLine.Length < 100)
            {
                objects.Add(cleanedLine);
            }
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
