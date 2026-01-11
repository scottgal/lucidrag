using YamlDotNet.Serialization;

namespace LucidRAG.Manifests;

/// <summary>
/// YAML manifest for a pipeline (orchestrates multiple waves).
/// Pipelines define the execution flow and dependencies between waves.
/// </summary>
public sealed class PipelineManifest
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "display_name")]
    public string DisplayName { get; set; } = "";

    [YamlMember(Alias = "description")]
    public string Description { get; set; } = "";

    [YamlMember(Alias = "version")]
    public string Version { get; set; } = "1.0.0";

    [YamlMember(Alias = "priority")]
    public int Priority { get; set; } = 0;

    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;

    [YamlMember(Alias = "tags")]
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Execution stages defining the wave orchestration.
    /// </summary>
    [YamlMember(Alias = "stages")]
    public List<PipelineStage> Stages { get; set; } = new();

    /// <summary>
    /// Lane configurations for concurrency management.
    /// </summary>
    [YamlMember(Alias = "lanes")]
    public Dictionary<string, PipelineLane> Lanes { get; set; } = new();

    /// <summary>
    /// Pipeline-level configuration.
    /// </summary>
    [YamlMember(Alias = "config")]
    public PipelineConfig Config { get; set; } = new();

    /// <summary>
    /// Default configuration values.
    /// </summary>
    [YamlMember(Alias = "defaults")]
    public Dictionary<string, object> Defaults { get; set; } = new();
}

/// <summary>
/// Pipeline execution stage.
/// </summary>
public sealed class PipelineStage
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "description")]
    public string Description { get; set; } = "";

    /// <summary>
    /// Waves to execute in this stage.
    /// </summary>
    [YamlMember(Alias = "waves")]
    public List<string> Waves { get; set; } = new();

    /// <summary>
    /// Maximum timeout for this stage (e.g., "00:00:02.000").
    /// </summary>
    [YamlMember(Alias = "timeout")]
    public string? Timeout { get; set; }

    /// <summary>
    /// Whether waves in this stage run in parallel.
    /// </summary>
    [YamlMember(Alias = "parallel")]
    public bool Parallel { get; set; } = false;

    /// <summary>
    /// Whether this stage can trigger early exit.
    /// </summary>
    [YamlMember(Alias = "early_exit")]
    public bool EarlyExit { get; set; } = false;

    /// <summary>
    /// Only run if condition is true.
    /// </summary>
    [YamlMember(Alias = "conditional")]
    public bool Conditional { get; set; } = false;

    /// <summary>
    /// Stages this stage depends on (must complete first).
    /// </summary>
    [YamlMember(Alias = "depends_on")]
    public List<string> DependsOn { get; set; } = new();
}

/// <summary>
/// Pipeline lane configuration (concurrency pool).
/// </summary>
public sealed class PipelineLane
{
    [YamlMember(Alias = "max_concurrency")]
    public int MaxConcurrency { get; set; } = 1;

    [YamlMember(Alias = "priority")]
    public int Priority { get; set; } = 100;

    [YamlMember(Alias = "description")]
    public string Description { get; set; } = "";
}

/// <summary>
/// Pipeline-level configuration.
/// </summary>
public sealed class PipelineConfig
{
    [YamlMember(Alias = "total_timeout")]
    public string? TotalTimeout { get; set; }

    [YamlMember(Alias = "early_exit_signals")]
    public PipelineEarlyExitConfig? EarlyExitSignals { get; set; }

    [YamlMember(Alias = "failure_handling")]
    public PipelineFailureHandling? FailureHandling { get; set; }
}

/// <summary>
/// Early exit signal configuration.
/// </summary>
public sealed class PipelineEarlyExitConfig
{
    [YamlMember(Alias = "cache_hit")]
    public List<string> CacheHit { get; set; } = new();

    [YamlMember(Alias = "no_results")]
    public List<string> NoResults { get; set; } = new();

    [YamlMember(Alias = "allow")]
    public List<string> Allow { get; set; } = new();

    [YamlMember(Alias = "block")]
    public List<string> Block { get; set; } = new();
}

/// <summary>
/// Failure handling configuration.
/// </summary>
public sealed class PipelineFailureHandling
{
    [YamlMember(Alias = "retry_on_timeout")]
    public bool RetryOnTimeout { get; set; } = false;

    [YamlMember(Alias = "fallback_to_cache")]
    public bool FallbackToCache { get; set; } = true;

    [YamlMember(Alias = "partial_results_allowed")]
    public bool PartialResultsAllowed { get; set; } = false;
}
