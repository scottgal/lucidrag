namespace Mostlylucid.DocSummarizer.Images.Config;

/// <summary>
/// Represents a configurable phase in an OCR pipeline
/// </summary>
public class PipelinePhase
{
    /// <summary>
    /// Unique identifier for this phase
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name of the phase
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Description of what this phase does
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether this phase is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Priority/order for execution (higher = earlier)
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Wave type that implements this phase
    /// </summary>
    public string WaveType { get; set; } = string.Empty;

    /// <summary>
    /// Phase-specific configuration (key-value pairs)
    /// </summary>
    public Dictionary<string, object>? Parameters { get; set; }

    /// <summary>
    /// Confidence threshold for early exit (0-1)
    /// If previous phases achieve this confidence, skip this phase
    /// </summary>
    public double? EarlyExitThreshold { get; set; }

    /// <summary>
    /// Maximum time in milliseconds this phase can run (0 = unlimited)
    /// </summary>
    public int MaxDurationMs { get; set; } = 0;

    /// <summary>
    /// Dependencies - other phase IDs that must run before this one
    /// </summary>
    public List<string>? DependsOn { get; set; }

    /// <summary>
    /// Tags for categorization and filtering
    /// </summary>
    public List<string>? Tags { get; set; }
}
