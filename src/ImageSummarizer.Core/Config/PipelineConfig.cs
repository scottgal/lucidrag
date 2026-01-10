namespace Mostlylucid.DocSummarizer.Images.Config;

/// <summary>
/// Represents a complete OCR pipeline configuration
/// </summary>
public class PipelineConfig
{
    /// <summary>
    /// Unique name for this pipeline
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Display name for UI/CLI
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Description of what this pipeline optimizes for
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Pipeline category (e.g., "speed", "quality", "balanced")
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Estimated processing time in seconds (for user guidance)
    /// </summary>
    public double EstimatedDurationSeconds { get; set; }

    /// <summary>
    /// Expected accuracy improvement over baseline (percentage)
    /// </summary>
    public double? AccuracyImprovement { get; set; }

    /// <summary>
    /// Ordered list of phases in this pipeline
    /// </summary>
    public List<PipelinePhase> Phases { get; set; } = new();

    /// <summary>
    /// Global pipeline configuration
    /// </summary>
    public PipelineGlobalSettings? GlobalSettings { get; set; }

    /// <summary>
    /// Tags for categorization
    /// </summary>
    public List<string>? Tags { get; set; }

    /// <summary>
    /// Whether this pipeline is the default
    /// </summary>
    public bool IsDefault { get; set; }
}

/// <summary>
/// Global settings that apply to all phases in a pipeline
/// </summary>
public class PipelineGlobalSettings
{
    /// <summary>
    /// Maximum total pipeline duration in milliseconds (0 = unlimited)
    /// </summary>
    public int MaxTotalDurationMs { get; set; } = 0;

    /// <summary>
    /// Global confidence threshold for early exit
    /// If any phase achieves this, skip remaining phases
    /// </summary>
    public double? GlobalEarlyExitThreshold { get; set; }

    /// <summary>
    /// Whether to parallelize independent phases
    /// </summary>
    public bool EnableParallelization { get; set; } = true;

    /// <summary>
    /// Maximum degree of parallelism
    /// </summary>
    public int MaxParallelism { get; set; } = -1; // -1 = use CPU count

    /// <summary>
    /// Whether to emit detailed performance signals
    /// </summary>
    public bool EmitPerformanceSignals { get; set; } = true;

    /// <summary>
    /// Whether to cache intermediate results
    /// </summary>
    public bool EnableCaching { get; set; } = true;
}

/// <summary>
/// Collection of pipeline configurations
/// </summary>
public class PipelinesConfig
{
    /// <summary>
    /// Schema version for forward compatibility
    /// </summary>
    public string SchemaVersion { get; set; } = "1.0";

    /// <summary>
    /// Available pipelines
    /// </summary>
    public List<PipelineConfig> Pipelines { get; set; } = new();

    /// <summary>
    /// Default pipeline name to use
    /// </summary>
    public string? DefaultPipeline { get; set; }

    /// <summary>
    /// Global defaults that apply to all pipelines unless overridden
    /// </summary>
    public PipelineGlobalSettings? Defaults { get; set; }
}
