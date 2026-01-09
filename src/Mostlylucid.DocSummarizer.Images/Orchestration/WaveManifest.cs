namespace Mostlylucid.DocSummarizer.Images.Orchestration;

/// <summary>
///     Declarative wave manifest loaded from YAML.
///     Defines signal contracts for dynamic composition.
///     Follows the BotDetection DetectorManifest pattern.
/// </summary>
public sealed class WaveManifest
{
    public required string Name { get; init; }
    public int Priority { get; init; } = 50;
    public bool Enabled { get; init; } = true;
    public string? Description { get; init; }

    public SignalScope Scope { get; init; } = new();
    public TaxonomyConfig Taxonomy { get; init; } = new();
    public TriggerConfig Triggers { get; init; } = new();
    public EmissionConfig Emits { get; init; } = new();
    public ListenConfig Listens { get; init; } = new();
    public EscalationConfig? Escalation { get; init; }
    public LaneConfig Lane { get; init; } = new();
    public BudgetConfig? Budget { get; init; }
    public ConfigBindings Config { get; init; } = new();
    public List<string> Tags { get; init; } = new();

    /// <summary>
    ///     Default parameter values - no magic numbers!
    ///     These are the baseline defaults loaded from YAML.
    ///     Can be overridden via appsettings.json at runtime.
    /// </summary>
    public WaveDefaults Defaults { get; init; } = new();
}

/// <summary>
///     Signal scope context (three-level hierarchy from ephemeral patterns).
/// </summary>
public sealed class SignalScope
{
    public string Sink { get; init; } = "imageanalysis";
    public string Coordinator { get; init; } = "analysis";
    public string Atom { get; init; } = "";
}

/// <summary>
///     Taxonomy classification for the wave.
/// </summary>
public sealed class TaxonomyConfig
{
    /// <summary>
    ///     Wave kind: sensor, extractor, proposer, constrainer, ranker, etc.
    /// </summary>
    public string Kind { get; init; } = "sensor";

    /// <summary>
    ///     Determinism: deterministic or probabilistic.
    /// </summary>
    public string Determinism { get; init; } = "deterministic";

    /// <summary>
    ///     Persistence: ephemeral, escalatable, or direct_write.
    /// </summary>
    public string Persistence { get; init; } = "ephemeral";
}

/// <summary>
///     Trigger conditions - when should this wave run?
/// </summary>
public sealed class TriggerConfig
{
    /// <summary>
    ///     Required signals - ALL must be satisfied before wave runs.
    /// </summary>
    public List<SignalRequirement> Requires { get; init; } = new();

    /// <summary>
    ///     Run when ANY of these signals exist.
    /// </summary>
    public List<string> Signals { get; init; } = new();

    /// <summary>
    ///     Skip if ANY of these signals exist.
    /// </summary>
    public List<string> SkipWhen { get; init; } = new();
}

/// <summary>
///     Signal requirement with optional condition.
/// </summary>
public sealed class SignalRequirement
{
    public required string Signal { get; init; }
    public string? Condition { get; init; }
    public object? Value { get; init; }
}

/// <summary>
///     Signal emissions - what signals does this wave produce?
/// </summary>
public sealed class EmissionConfig
{
    public List<string> OnStart { get; init; } = new();
    public List<SignalDefinition> OnComplete { get; init; } = new();
    public List<string> OnFailure { get; init; } = new();
    public List<ConditionalSignal> Conditional { get; init; } = new();
}

/// <summary>
///     Signal definition with type and metadata.
/// </summary>
public class SignalDefinition
{
    public required string Key { get; init; }
    public string Type { get; init; } = "string";
    public string? Description { get; init; }
    public double[]? ConfidenceRange { get; init; }
}

/// <summary>
///     Conditional signal emitted based on runtime conditions.
/// </summary>
public sealed class ConditionalSignal : SignalDefinition
{
    public string? When { get; init; }
}

/// <summary>
///     Dependencies - signals this wave reads from other waves.
/// </summary>
public sealed class ListenConfig
{
    public List<string> Required { get; init; } = new();
    public List<string> Optional { get; init; } = new();
}

/// <summary>
///     Escalation rules - when to defer to more powerful processing.
/// </summary>
public sealed class EscalationConfig
{
    /// <summary>
    ///     Named escalation targets with conditions.
    /// </summary>
    public Dictionary<string, EscalationRule> Targets { get; init; } = new();
}

/// <summary>
///     Escalation rule with conditions.
/// </summary>
public sealed class EscalationRule
{
    public List<EscalationCondition> When { get; init; } = new();
    public List<EscalationCondition> SkipWhen { get; init; } = new();
    public string? Description { get; init; }
}

/// <summary>
///     Escalation condition.
/// </summary>
public sealed class EscalationCondition
{
    public required string Signal { get; init; }
    public object? Value { get; init; }
    public string? Condition { get; init; }
}

/// <summary>
///     Lane configuration for concurrency control.
/// </summary>
public sealed class LaneConfig
{
    public string Name { get; init; } = "fast";
    public int MaxConcurrency { get; init; } = 4;
    public int Priority { get; init; } = 0;
}

/// <summary>
///     Budget constraints for the wave.
/// </summary>
public sealed class BudgetConfig
{
    /// <summary>
    ///     Maximum duration as TimeSpan string (e.g., "00:00:10.000").
    /// </summary>
    public string? MaxDuration { get; init; }

    /// <summary>
    ///     Maximum tokens (for LLM waves).
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    ///     Maximum cost (for paid APIs).
    /// </summary>
    public decimal? MaxCost { get; init; }
}

/// <summary>
///     Configuration bindings.
/// </summary>
public sealed class ConfigBindings
{
    public List<ConfigBinding> Bindings { get; init; } = new();
}

/// <summary>
///     Single configuration binding.
/// </summary>
public sealed class ConfigBinding
{
    public required string ConfigKey { get; init; }
    public bool SkipIfFalse { get; init; }
    public string? MapsTo { get; init; }
}

/// <summary>
///     Default parameter values for a wave.
///     These come from YAML and can be overridden via appsettings.json.
///     Key principle: NO MAGIC NUMBERS in code - all configurable values here.
/// </summary>
public sealed class WaveDefaults
{
    /// <summary>
    ///     Base weights for scoring contributions.
    /// </summary>
    public WeightDefaults Weights { get; init; } = new();

    /// <summary>
    ///     Confidence thresholds and deltas.
    /// </summary>
    public ConfidenceDefaults Confidence { get; init; } = new();

    /// <summary>
    ///     Timing and rate parameters.
    /// </summary>
    public TimingDefaults Timing { get; init; } = new();

    /// <summary>
    ///     Feature flags and behavior switches.
    /// </summary>
    public FeatureDefaults Features { get; init; } = new();

    /// <summary>
    ///     Wave-specific parameters (freeform key-value).
    /// </summary>
    public Dictionary<string, object> Parameters { get; init; } = new();
}

/// <summary>
///     Weight configuration for wave scoring.
/// </summary>
public sealed class WeightDefaults
{
    /// <summary>
    ///     Base weight when wave contributes (default: 1.0).
    /// </summary>
    public double Base { get; init; } = 1.0;

    /// <summary>
    ///     Weight multiplier for high-confidence signals.
    /// </summary>
    public double HighConfidence { get; init; } = 1.5;

    /// <summary>
    ///     Weight multiplier for low-confidence signals.
    /// </summary>
    public double LowConfidence { get; init; } = 0.5;

    /// <summary>
    ///     Weight multiplier for verified/validated results.
    /// </summary>
    public double Verified { get; init; } = 2.0;
}

/// <summary>
///     Confidence threshold configuration.
/// </summary>
public sealed class ConfidenceDefaults
{
    /// <summary>
    ///     Default confidence when no specific signal.
    /// </summary>
    public double Neutral { get; init; } = 0.5;

    /// <summary>
    ///     Confidence for high-quality results.
    /// </summary>
    public double High { get; init; } = 0.9;

    /// <summary>
    ///     Confidence for medium-quality results.
    /// </summary>
    public double Medium { get; init; } = 0.7;

    /// <summary>
    ///     Confidence for low-quality results.
    /// </summary>
    public double Low { get; init; } = 0.5;

    /// <summary>
    ///     Threshold above which result is considered reliable.
    /// </summary>
    public double HighThreshold { get; init; } = 0.8;

    /// <summary>
    ///     Threshold below which result should be escalated.
    /// </summary>
    public double EscalationThreshold { get; init; } = 0.5;
}

/// <summary>
///     Timing and rate configuration.
/// </summary>
public sealed class TimingDefaults
{
    /// <summary>
    ///     Timeout for wave execution (milliseconds).
    /// </summary>
    public int TimeoutMs { get; init; } = 10000;

    /// <summary>
    ///     Cache TTL (seconds).
    /// </summary>
    public int CacheTtlSec { get; init; } = 3600;
}

/// <summary>
///     Feature flags for wave behavior.
/// </summary>
public sealed class FeatureDefaults
{
    /// <summary>
    ///     Enable detailed logging for this wave.
    /// </summary>
    public bool DetailedLogging { get; init; } = false;

    /// <summary>
    ///     Enable caching of results.
    /// </summary>
    public bool EnableCache { get; init; } = true;

    /// <summary>
    ///     Can this wave trigger early exit?
    /// </summary>
    public bool CanEarlyExit { get; init; } = false;

    /// <summary>
    ///     Can this wave escalate to AI processing?
    /// </summary>
    public bool CanEscalate { get; init; } = false;
}

/// <summary>
///     Signal contract extracted from manifest for documentation.
/// </summary>
public sealed class WaveSignalContract
{
    public required string WaveName { get; init; }
    public required string Kind { get; init; }
    public required IReadOnlySet<string> Emits { get; init; }
    public required IReadOnlySet<string> Listens { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }

    public static WaveSignalContract FromManifest(WaveManifest manifest)
    {
        var emits = new HashSet<string>();
        emits.UnionWith(manifest.Emits.OnStart);
        emits.UnionWith(manifest.Emits.OnComplete.Select(s => s.Key));
        emits.UnionWith(manifest.Emits.OnFailure);
        emits.UnionWith(manifest.Emits.Conditional.Select(s => s.Key));

        var listens = new HashSet<string>();
        listens.UnionWith(manifest.Listens.Required);
        listens.UnionWith(manifest.Listens.Optional);
        listens.UnionWith(manifest.Triggers.Requires.Select(r => r.Signal));

        return new WaveSignalContract
        {
            WaveName = manifest.Name,
            Kind = manifest.Taxonomy.Kind,
            Emits = emits,
            Listens = listens,
            Tags = manifest.Tags
        };
    }

    public string ToDescription()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {WaveName} ({Kind})");
        sb.AppendLine();
        sb.AppendLine("**Emits:**");
        foreach (var signal in Emits.OrderBy(s => s))
            sb.AppendLine($"  - {signal}");
        sb.AppendLine();
        sb.AppendLine("**Listens:**");
        foreach (var signal in Listens.OrderBy(s => s))
            sb.AppendLine($"  - {signal}");
        if (Tags.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Tags:** {string.Join(", ", Tags)}");
        }

        sb.AppendLine();
        return sb.ToString();
    }
}
