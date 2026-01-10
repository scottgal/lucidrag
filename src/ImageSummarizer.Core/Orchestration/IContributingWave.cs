using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.DocSummarizer.Images.Orchestration;

/// <summary>
///     A wave that emits contributions (evidence) to the image analysis ledger.
///     Part of the blackboard architecture - waves contribute evidence,
///     the orchestrator aggregates into a final profile.
///
///     This follows the BotDetection IContributingDetector pattern.
/// </summary>
public interface IContributingWave
{
    /// <summary>
    ///     Unique name of this wave.
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Priority determines execution order when multiple waves can run.
    ///     Lower = runs first. Critical (0) runs before Normal (100).
    /// </summary>
    int Priority => 100;

    /// <summary>
    ///     Tags describing what category of analysis this wave provides.
    /// </summary>
    IReadOnlyList<string> Tags => Array.Empty<string>();

    /// <summary>
    ///     Whether this wave is enabled.
    ///     Checked before running - allows runtime disable.
    /// </summary>
    bool IsEnabled => true;

    /// <summary>
    ///     Trigger conditions that must be met before this wave runs.
    ///     Empty = no conditions, runs in the first wave group.
    /// </summary>
    IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    /// <summary>
    ///     Maximum time to wait for trigger conditions before skipping.
    ///     Default: 500ms
    /// </summary>
    TimeSpan TriggerTimeout => TimeSpan.FromMilliseconds(500);

    /// <summary>
    ///     Maximum time allowed for this wave to execute.
    ///     Default: 10 seconds (image analysis can be slow)
    /// </summary>
    TimeSpan ExecutionTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Whether this wave can be skipped if it times out or fails.
    ///     Default: true (most waves are optional)
    /// </summary>
    bool IsOptional => true;

    /// <summary>
    ///     Run analysis and return zero or more contributions.
    ///     Wave receives the blackboard state and can read signals from prior waves.
    /// </summary>
    Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        ImageBlackboardState state,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Condition that must be met for a wave to run.
/// </summary>
public abstract record TriggerCondition
{
    /// <summary>
    ///     Human-readable description of this condition.
    /// </summary>
    public abstract string Description { get; }

    /// <summary>
    ///     Check if this condition is satisfied given the current blackboard signals.
    /// </summary>
    public abstract bool IsSatisfied(IReadOnlyDictionary<string, object> signals);
}

/// <summary>
///     Trigger when a specific signal key exists.
/// </summary>
public sealed record SignalExistsTrigger(string SignalKey) : TriggerCondition
{
    public override string Description => $"Signal '{SignalKey}' exists";

    public override bool IsSatisfied(IReadOnlyDictionary<string, object> signals)
    {
        return signals.ContainsKey(SignalKey);
    }
}

/// <summary>
///     Trigger when a signal has a specific value.
/// </summary>
public sealed record SignalValueTrigger<T>(string SignalKey, T ExpectedValue) : TriggerCondition
{
    public override string Description => $"Signal '{SignalKey}' == {ExpectedValue}";

    public override bool IsSatisfied(IReadOnlyDictionary<string, object> signals)
    {
        return signals.TryGetValue(SignalKey, out var value) &&
               value is T typed &&
               EqualityComparer<T>.Default.Equals(typed, ExpectedValue);
    }
}

/// <summary>
///     Trigger when a signal satisfies a predicate.
/// </summary>
public sealed record SignalPredicateTrigger<T>(
    string SignalKey,
    Func<T, bool> Predicate,
    string PredicateDescription) : TriggerCondition
{
    public override string Description => $"Signal '{SignalKey}' {PredicateDescription}";

    public override bool IsSatisfied(IReadOnlyDictionary<string, object> signals)
    {
        return signals.TryGetValue(SignalKey, out var value) &&
               value is T typed &&
               Predicate(typed);
    }
}

/// <summary>
///     Trigger when any of the sub-conditions are met.
/// </summary>
public sealed record AnyOfTrigger(IReadOnlyList<TriggerCondition> Conditions) : TriggerCondition
{
    public override string Description => $"Any of: [{string.Join(", ", Conditions.Select(c => c.Description))}]";

    public override bool IsSatisfied(IReadOnlyDictionary<string, object> signals)
    {
        return Conditions.Any(c => c.IsSatisfied(signals));
    }
}

/// <summary>
///     Trigger when all of the sub-conditions are met.
/// </summary>
public sealed record AllOfTrigger(IReadOnlyList<TriggerCondition> Conditions) : TriggerCondition
{
    public override string Description => $"All of: [{string.Join(", ", Conditions.Select(c => c.Description))}]";

    public override bool IsSatisfied(IReadOnlyDictionary<string, object> signals)
    {
        return Conditions.All(c => c.IsSatisfied(signals));
    }
}

/// <summary>
///     Trigger when a certain number of waves have completed.
/// </summary>
public sealed record WaveCountTrigger(int MinWaves) : TriggerCondition
{
    public const string CompletedWavesSignal = "_system.completed_waves";

    public override string Description => $"At least {MinWaves} waves completed";

    public override bool IsSatisfied(IReadOnlyDictionary<string, object> signals)
    {
        return signals.TryGetValue(CompletedWavesSignal, out var value) &&
               value is int count &&
               count >= MinWaves;
    }
}

/// <summary>
///     Trigger based on image content type signals.
/// </summary>
public sealed record ContentTypeTrigger(string ContentType) : TriggerCondition
{
    public const string ContentTypeSignal = "content.type";

    public override string Description => $"Content type is '{ContentType}'";

    public override bool IsSatisfied(IReadOnlyDictionary<string, object> signals)
    {
        return signals.TryGetValue(ContentTypeSignal, out var value) &&
               value is string type &&
               type.Equals(ContentType, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
///     Helper class for building trigger conditions fluently.
/// </summary>
public static class Triggers
{
    /// <summary>
    ///     Common: trigger when text was detected in the image.
    /// </summary>
    public static TriggerCondition WhenTextDetected =>
        WhenSignalExists(ImageSignalKeys.TextDetected);

    /// <summary>
    ///     Common: trigger when OCR has completed.
    /// </summary>
    public static TriggerCondition WhenOcrComplete =>
        WhenSignalExists(ImageSignalKeys.OcrText);

    /// <summary>
    ///     Common: trigger when color analysis is complete.
    /// </summary>
    public static TriggerCondition WhenColorAnalyzed =>
        WhenSignalExists(ImageSignalKeys.ColorDominant);

    /// <summary>
    ///     Common: trigger when image is animated (GIF).
    /// </summary>
    public static TriggerCondition WhenAnimated =>
        WhenSignalEquals(ImageSignalKeys.IsAnimated, true);

    /// <summary>
    ///     Trigger when a signal exists.
    /// </summary>
    public static TriggerCondition WhenSignalExists(string signalKey)
    {
        return new SignalExistsTrigger(signalKey);
    }

    /// <summary>
    ///     Trigger when a signal has a specific value.
    /// </summary>
    public static TriggerCondition WhenSignalEquals<T>(string signalKey, T value)
    {
        return new SignalValueTrigger<T>(signalKey, value);
    }

    /// <summary>
    ///     Trigger when a signal satisfies a predicate.
    /// </summary>
    public static TriggerCondition WhenSignal<T>(
        string signalKey,
        Func<T, bool> predicate,
        string description)
    {
        return new SignalPredicateTrigger<T>(signalKey, predicate, description);
    }

    /// <summary>
    ///     Trigger when any condition is met.
    /// </summary>
    public static TriggerCondition AnyOf(params TriggerCondition[] conditions)
    {
        return new AnyOfTrigger(conditions);
    }

    /// <summary>
    ///     Trigger when all conditions are met.
    /// </summary>
    public static TriggerCondition AllOf(params TriggerCondition[] conditions)
    {
        return new AllOfTrigger(conditions);
    }

    /// <summary>
    ///     Trigger when enough waves have completed.
    /// </summary>
    public static TriggerCondition WhenWaveCount(int min)
    {
        return new WaveCountTrigger(min);
    }

    /// <summary>
    ///     Trigger when content type matches.
    /// </summary>
    public static TriggerCondition WhenContentType(string contentType)
    {
        return new ContentTypeTrigger(contentType);
    }
}

/// <summary>
///     Well-known signal keys for cross-wave communication.
/// </summary>
public static class ImageSignalKeys
{
    // Identity signals (from IdentityWave)
    public const string ImageHash = "identity.hash";
    public const string ImageFormat = "identity.format";
    public const string ImageWidth = "identity.width";
    public const string ImageHeight = "identity.height";
    public const string ImageSize = "identity.size";
    public const string IsAnimated = "identity.is_animated";
    public const string FrameCount = "identity.frame_count";

    // Color signals (from ColorWave)
    public const string ColorDominant = "color.dominant";
    public const string ColorPalette = "color.palette";
    public const string ColorTemperature = "color.temperature";
    public const string ColorSaturation = "color.saturation";
    public const string ColorContrast = "color.contrast";
    public const string IsGrayscale = "color.is_grayscale";

    // Text detection signals (from TextDetectionWave)
    public const string TextDetected = "text.detected";
    public const string TextCoverage = "text.coverage";
    public const string TextRegionCount = "text.region_count";
    public const string TextConfidence = "text.confidence";

    // OCR signals (from OcrWave)
    public const string OcrText = "ocr.text";
    public const string OcrConfidence = "ocr.confidence";
    public const string OcrWordCount = "ocr.word_count";
    public const string OcrLanguage = "ocr.language";

    // Structure signals (from StructureWave)
    public const string StructureType = "structure.type";
    public const string StructureEdgeDensity = "structure.edge_density";
    public const string StructureComplexity = "structure.complexity";

    // Motion signals (from MotionWave)
    public const string MotionIntensity = "motion.intensity";
    public const string MotionType = "motion.type";
    public const string MotionFrameDelta = "motion.frame_delta";

    // Vision/AI signals (from Florence2Wave, VisionLlmWave)
    public const string Caption = "vision.caption";
    public const string DetailedCaption = "vision.detailed_caption";
    public const string Objects = "vision.objects";
    public const string Entities = "vision.entities";
    public const string VisionConfidence = "vision.confidence";

    // Forensics signals
    public const string ExifPresent = "forensics.exif_present";
    public const string ExifCamera = "forensics.exif_camera";
    public const string ExifDateTime = "forensics.exif_datetime";
    public const string TamperDetected = "forensics.tamper_detected";
    public const string TamperConfidence = "forensics.tamper_confidence";

    // Routing signals (from AutoRoutingWave)
    public const string RouteSelected = "route.selected";
    public const string RouteSkipPrefix = "route.skip.";

    // System signals
    public const string CompletedWaves = "_system.completed_waves";
    public const string ProcessingTimeMs = "_system.processing_time_ms";
}

/// <summary>
///     Base class for contributing waves with common functionality.
/// </summary>
public abstract class ContributingWaveBase : IContributingWave
{
    public abstract string Name { get; }

    public virtual int Priority => 100;
    public virtual IReadOnlyList<string> Tags => Array.Empty<string>();
    public virtual bool IsEnabled => true;
    public virtual IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();
    public virtual TimeSpan TriggerTimeout => TimeSpan.FromMilliseconds(500);
    public virtual TimeSpan ExecutionTimeout => TimeSpan.FromSeconds(10);
    public virtual bool IsOptional => true;

    public abstract Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        ImageBlackboardState state,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Helper to return a single contribution.
    /// </summary>
    protected static IReadOnlyList<DetectionContribution> Single(DetectionContribution contribution)
    {
        return new[] { contribution };
    }

    /// <summary>
    ///     Helper to return multiple contributions.
    /// </summary>
    protected static IReadOnlyList<DetectionContribution> Multiple(params DetectionContribution[] contributions)
    {
        return contributions;
    }

    /// <summary>
    ///     Helper to return no contributions.
    /// </summary>
    protected static IReadOnlyList<DetectionContribution> None()
    {
        return Array.Empty<DetectionContribution>();
    }

    /// <summary>
    ///     Create an info contribution with signals.
    /// </summary>
    protected DetectionContribution Info(
        string category,
        string reason,
        Dictionary<string, object>? signals = null)
    {
        return DetectionContribution.Info(Name, category, reason, signals);
    }
}
