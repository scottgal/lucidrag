using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using static Mostlylucid.DocSummarizer.Images.Models.Dynamic.ImageLedger;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Scene Detection Wave - Detects scene boundaries in animated images early in the pipeline.
/// Priority: 15 (after Identity, before most other waves)
///
/// Purpose:
/// 1. Inform escalation decisions - more scenes = more complex = may need LLM
/// 2. Provide frame indices for other waves (Florence2, VisionLLM, MlOcr)
/// 3. Reduce redundant processing by identifying key frames upfront
///
/// Only runs for animated images (GIFs, APNGs, etc.)
/// </summary>
public class SceneDetectionWave : IAnalysisWave
{
    private readonly SceneDetectionService _sceneService;
    private readonly ILogger<SceneDetectionWave>? _logger;

    public string Name => "SceneDetectionWave";
    public int Priority => 15; // Early - after Identity (10), before Color (20)
    public IReadOnlyList<string> Tags => new[] { "scene", "animation", "motion", "frames" };

    public SceneDetectionWave(
        SceneDetectionService sceneService,
        ILogger<SceneDetectionWave>? logger = null)
    {
        _sceneService = sceneService;
        _logger = logger;
    }

    /// <summary>
    /// Only run for animated images with multiple frames.
    /// </summary>
    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        var isAnimated = context.GetValue<bool>("identity.is_animated");
        var frameCount = context.GetValue<int>("identity.frame_count");

        return isAnimated && frameCount > 1;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        try
        {
            // Load image and detect scenes with text awareness
            using var image = await Image.LoadAsync<Rgba32>(imagePath, ct);
            var result = _sceneService.DetectScenesWithTextAwareness(image, maxScenes: 4);

            // Emit scene detection signals
            signals.Add(new Signal
            {
                Key = "scene.count",
                Value = result.SceneCount,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "scene", "animation" }
            });

            signals.Add(new Signal
            {
                Key = "scene.frame_indices",
                Value = result.SceneEndFrameIndices,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "scene", "frames" },
                Metadata = new Dictionary<string, object>
                {
                    ["total_frames"] = result.TotalFrames,
                    ["used_motion_detection"] = result.UsedMotionDetection
                }
            });

            signals.Add(new Signal
            {
                Key = "scene.last_frame",
                Value = result.LastSceneFrameIndex,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "scene", "frames" }
            });

            signals.Add(new Signal
            {
                Key = "scene.avg_motion",
                Value = result.AverageMotion,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "scene", "motion" }
            });

            // Emit escalation hint based on scene complexity
            signals.Add(new Signal
            {
                Key = "scene.suggest_escalation",
                Value = result.SuggestEscalation,
                Confidence = 0.85,
                Source = Name,
                Tags = new List<string> { "scene", "escalation" }
            });

            // Emit text change detection signals
            signals.Add(new Signal
            {
                Key = "scene.text_change_count",
                Value = result.TextChangeFrameCount,
                Confidence = 0.9,
                Source = Name,
                Tags = new List<string> { "scene", "text", "subtitles" }
            });

            signals.Add(new Signal
            {
                Key = "scene.suggest_text_extraction",
                Value = result.SuggestTextExtraction,
                Confidence = 0.85,
                Source = Name,
                Tags = new List<string> { "scene", "text", "ocr" }
            });

            _logger?.LogInformation(
                "Scene detection: {SceneCount} scenes from {TotalFrames} frames (avgMotion={AvgMotion:F3}, textChanges={TextChanges}, escalate={Escalate})",
                result.SceneCount, result.TotalFrames, result.AverageMotion, result.TextChangeFrameCount, result.SuggestEscalation);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Scene detection failed for {Path}", imagePath);
            signals.Add(new Signal
            {
                Key = "scene.error",
                Value = ex.Message,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "scene", "error" }
            });
        }

        return signals;
    }
}
