namespace Mostlylucid.DocSummarizer.Images.Models.Dynamic;

/// <summary>
/// Defines a rule for detecting contradictions between signals.
/// Config-driven policy for signal validation.
/// </summary>
public record ContradictionRule
{
    /// <summary>
    /// Unique identifier for this rule.
    /// </summary>
    public required string RuleId { get; init; }

    /// <summary>
    /// Human-readable description of what contradiction this detects.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// First signal key to compare.
    /// </summary>
    public required string SignalKeyA { get; init; }

    /// <summary>
    /// Second signal key to compare (can be same as SignalKeyA for intra-signal rules).
    /// </summary>
    public required string SignalKeyB { get; init; }

    /// <summary>
    /// Type of contradiction check to perform.
    /// </summary>
    public ContradictionType Type { get; init; } = ContradictionType.ValueConflict;

    /// <summary>
    /// For numeric comparisons: the threshold difference that indicates contradiction.
    /// </summary>
    public double? Threshold { get; init; }

    /// <summary>
    /// Severity of the contradiction (affects whether it blocks or warns).
    /// </summary>
    public ContradictionSeverity Severity { get; init; } = ContradictionSeverity.Warning;

    /// <summary>
    /// Resolution strategy when contradiction is detected.
    /// </summary>
    public ResolutionStrategy Resolution { get; init; } = ResolutionStrategy.PreferHigherConfidence;

    /// <summary>
    /// Expected values for SignalKeyA that trigger contradiction check (null = any value).
    /// </summary>
    public List<object>? ExpectedValuesA { get; init; }

    /// <summary>
    /// Expected values for SignalKeyB that would be contradictory given ExpectedValuesA.
    /// </summary>
    public List<object>? ContradictoryValuesB { get; init; }

    /// <summary>
    /// Whether this rule is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Minimum confidence for signals to be checked (skip low-confidence signals).
    /// </summary>
    public double MinConfidenceThreshold { get; init; } = 0.5;
}

/// <summary>
/// Type of contradiction check to perform.
/// </summary>
public enum ContradictionType
{
    /// <summary>
    /// Values directly conflict (e.g., "has_text=true" vs "text_content=empty").
    /// </summary>
    ValueConflict,

    /// <summary>
    /// Numeric values are too far apart (e.g., different confidence scores).
    /// </summary>
    NumericDivergence,

    /// <summary>
    /// Boolean signals contradict (one true, one false).
    /// </summary>
    BooleanOpposite,

    /// <summary>
    /// Categorical values are mutually exclusive.
    /// </summary>
    MutuallyExclusive,

    /// <summary>
    /// One signal implies another should exist but doesn't.
    /// </summary>
    MissingImplied,

    /// <summary>
    /// Custom predicate (for complex rules).
    /// </summary>
    Custom
}

/// <summary>
/// Severity level of detected contradictions.
/// </summary>
public enum ContradictionSeverity
{
    /// <summary>
    /// Informational - log only, no action.
    /// </summary>
    Info,

    /// <summary>
    /// Warning - flag but allow processing to continue.
    /// </summary>
    Warning,

    /// <summary>
    /// Error - flag and potentially reject/quarantine the analysis.
    /// </summary>
    Error,

    /// <summary>
    /// Critical - halt processing, requires manual review.
    /// </summary>
    Critical
}

/// <summary>
/// Strategy for resolving detected contradictions.
/// </summary>
public enum ResolutionStrategy
{
    /// <summary>
    /// Keep the signal with higher confidence.
    /// </summary>
    PreferHigherConfidence,

    /// <summary>
    /// Keep the more recent signal.
    /// </summary>
    PreferMostRecent,

    /// <summary>
    /// Keep both but mark as conflicting.
    /// </summary>
    MarkConflicting,

    /// <summary>
    /// Remove both signals (neither is trusted).
    /// </summary>
    RemoveBoth,

    /// <summary>
    /// Escalate to vision LLM for resolution.
    /// </summary>
    EscalateToLlm,

    /// <summary>
    /// Flag for manual review.
    /// </summary>
    ManualReview
}

/// <summary>
/// Result of contradiction detection.
/// </summary>
public record ContradictionResult
{
    /// <summary>
    /// The rule that was triggered.
    /// </summary>
    public required ContradictionRule Rule { get; init; }

    /// <summary>
    /// The first signal involved in the contradiction.
    /// </summary>
    public required Signal SignalA { get; init; }

    /// <summary>
    /// The second signal involved in the contradiction (may be null for MissingImplied).
    /// </summary>
    public Signal? SignalB { get; init; }

    /// <summary>
    /// Human-readable explanation of the contradiction.
    /// </summary>
    public required string Explanation { get; init; }

    /// <summary>
    /// Calculated severity (may differ from rule default based on confidence).
    /// </summary>
    public ContradictionSeverity EffectiveSeverity { get; init; }

    /// <summary>
    /// Recommended resolution based on the signals and rule.
    /// </summary>
    public string? RecommendedResolution { get; init; }

    /// <summary>
    /// Timestamp when contradiction was detected.
    /// </summary>
    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;
}
