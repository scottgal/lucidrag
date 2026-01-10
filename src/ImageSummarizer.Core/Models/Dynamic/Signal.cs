namespace Mostlylucid.DocSummarizer.Images.Models.Dynamic;

/// <summary>
/// Represents a single analytical signal contributed by an analysis wave.
/// Signals are atomic units of information with confidence and provenance.
/// </summary>
public record Signal
{
    /// <summary>
    /// Unique key identifying what this signal measures (e.g., "color.dominant", "forensics.exif_tampered").
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// The measured value. Can be any serializable type.
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    /// Confidence score (0.0 - 1.0) indicating reliability of this signal.
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    /// Source analyzer that produced this signal (e.g., "ColorAnalyzer", "ForensicsWave").
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// When this signal was generated.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Optional metadata about how this signal was computed.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Data type of the value for serialization/deserialization.
    /// </summary>
    public string? ValueType { get; init; }

    /// <summary>
    /// Optional tags for categorization (e.g., "visual", "forensic", "metadata").
    /// </summary>
    public List<string>? Tags { get; init; }
}

/// <summary>
/// Aggregation strategy for combining multiple signals for the same key.
/// </summary>
public enum AggregationStrategy
{
    /// <summary>
    /// Take the signal with highest confidence.
    /// </summary>
    HighestConfidence,

    /// <summary>
    /// Take the most recent signal.
    /// </summary>
    MostRecent,

    /// <summary>
    /// Average numeric values weighted by confidence.
    /// </summary>
    WeightedAverage,

    /// <summary>
    /// Majority vote for categorical values.
    /// </summary>
    MajorityVote,

    /// <summary>
    /// Merge all signals into a collection.
    /// </summary>
    Collect,

    /// <summary>
    /// Use custom aggregation logic.
    /// </summary>
    Custom
}

/// <summary>
/// Tags for categorizing signals.
/// </summary>
public static class SignalTags
{
    public const string Visual = "visual";
    public const string Color = "color";
    public const string Forensic = "forensic";
    public const string Metadata = "metadata";
    public const string Quality = "quality";
    public const string Content = "content";
    public const string Identity = "identity";
}
