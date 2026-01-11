using YamlDotNet.Serialization;

namespace LucidRAG.Manifests;

/// <summary>
/// YAML manifest for a wave (RAG pipeline stage).
/// Waves are retrieval/analysis components that can be chained together.
/// Follows StyloFlow manifest pattern.
/// </summary>
public sealed class WaveManifest
{
    /// <summary>
    /// Unique wave identifier (e.g., "dense_retrieval", "bm25_search", "entity_extraction")
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Display name for UI
    /// </summary>
    [YamlMember(Alias = "display_name")]
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Wave description
    /// </summary>
    [YamlMember(Alias = "description")]
    public string Description { get; set; } = "";

    /// <summary>
    /// Semantic version
    /// </summary>
    [YamlMember(Alias = "version")]
    public string Version { get; set; } = "1.0.0";

    /// <summary>
    /// Priority for execution ordering
    /// </summary>
    [YamlMember(Alias = "priority")]
    public int Priority { get; set; } = 0;

    /// <summary>
    /// Whether this wave is enabled
    /// </summary>
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Tags for categorization
    /// </summary>
    [YamlMember(Alias = "tags")]
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Taxonomy classification (kind, determinism, persistence)
    /// </summary>
    [YamlMember(Alias = "taxonomy")]
    public WaveTaxonomy Taxonomy { get; set; } = new();

    /// <summary>
    /// Input contract - what data this wave accepts
    /// </summary>
    [YamlMember(Alias = "input")]
    public WaveInputContract Input { get; set; } = new();

    /// <summary>
    /// Output contract - what data this wave produces
    /// </summary>
    [YamlMember(Alias = "output")]
    public WaveOutputContract Output { get; set; } = new();

    /// <summary>
    /// Trigger conditions - when this wave should execute
    /// </summary>
    [YamlMember(Alias = "triggers")]
    public WaveTriggerConfig? Triggers { get; set; }

    /// <summary>
    /// Signals emitted by this wave
    /// </summary>
    [YamlMember(Alias = "emits")]
    public WaveEmitsConfig? Emits { get; set; }

    /// <summary>
    /// Default configuration values
    /// </summary>
    [YamlMember(Alias = "defaults")]
    public WaveDefaultsConfig Defaults { get; set; } = new();

    /// <summary>
    /// Runtime type binding
    /// </summary>
    [YamlMember(Alias = "runtime")]
    public RuntimeConfig? Runtime { get; set; }
}

/// <summary>
/// Wave taxonomy classification
/// </summary>
public sealed class WaveTaxonomy
{
    /// <summary>
    /// Wave kind (sensor, analyzer, ranker, synthesizer, proposer)
    /// </summary>
    [YamlMember(Alias = "kind")]
    public WaveKind Kind { get; set; } = WaveKind.Analyzer;

    /// <summary>
    /// Determinism level
    /// </summary>
    [YamlMember(Alias = "determinism")]
    public WaveDeterminism Determinism { get; set; } = WaveDeterminism.Deterministic;

    /// <summary>
    /// Persistence strategy
    /// </summary>
    [YamlMember(Alias = "persistence")]
    public WavePersistence Persistence { get; set; } = WavePersistence.Ephemeral;
}

/// <summary>
/// Wave kind enumeration
/// </summary>
public enum WaveKind
{
    /// <summary>
    /// Entry point wave (e.g., query reception)
    /// </summary>
    Sensor,

    /// <summary>
    /// Analysis/processing wave (e.g., query expansion, entity extraction)
    /// </summary>
    Analyzer,

    /// <summary>
    /// Retrieval wave (e.g., dense search, BM25)
    /// </summary>
    Retriever,

    /// <summary>
    /// Ranking/filtering wave (e.g., RRF, reranking)
    /// </summary>
    Ranker,

    /// <summary>
    /// Synthesis wave (e.g., LLM answer generation)
    /// </summary>
    Synthesizer,

    /// <summary>
    /// Proposition/constraint wave
    /// </summary>
    Proposer
}

/// <summary>
/// Determinism level
/// </summary>
public enum WaveDeterminism
{
    /// <summary>
    /// Deterministic (same input = same output)
    /// </summary>
    Deterministic,

    /// <summary>
    /// Probabilistic (LLM-based, may vary)
    /// </summary>
    Probabilistic
}

/// <summary>
/// Persistence strategy
/// </summary>
public enum WavePersistence
{
    /// <summary>
    /// Ephemeral (not persisted)
    /// </summary>
    Ephemeral,

    /// <summary>
    /// Cached (in-memory or cache)
    /// </summary>
    Cached,

    /// <summary>
    /// Escalatable (can be persisted if needed)
    /// </summary>
    Escalatable,

    /// <summary>
    /// Direct write to database
    /// </summary>
    DirectWrite
}

/// <summary>
/// Wave input contract
/// </summary>
public sealed class WaveInputContract
{
    /// <summary>
    /// Entity types this wave accepts
    /// </summary>
    [YamlMember(Alias = "accepts")]
    public List<string> Accepts { get; set; } = new();

    /// <summary>
    /// Required signals that must be present
    /// </summary>
    [YamlMember(Alias = "required_signals")]
    public List<string> RequiredSignals { get; set; } = new();

    /// <summary>
    /// Optional signals that enhance behavior
    /// </summary>
    [YamlMember(Alias = "optional_signals")]
    public List<string> OptionalSignals { get; set; } = new();
}

/// <summary>
/// Wave output contract
/// </summary>
public sealed class WaveOutputContract
{
    /// <summary>
    /// Entity types this wave produces
    /// </summary>
    [YamlMember(Alias = "produces")]
    public List<string> Produces { get; set; } = new();

    /// <summary>
    /// Signals this wave emits
    /// </summary>
    [YamlMember(Alias = "signals")]
    public List<WaveSignalSpec> Signals { get; set; } = new();
}

/// <summary>
/// Signal specification
/// </summary>
public sealed class WaveSignalSpec
{
    /// <summary>
    /// Signal key (e.g., "dense.similarity", "bm25.score")
    /// </summary>
    [YamlMember(Alias = "key")]
    public string Key { get; set; } = "";

    /// <summary>
    /// Entity type (e.g., "number", "text.segment", "vector.embedding")
    /// </summary>
    [YamlMember(Alias = "entity_type")]
    public string EntityType { get; set; } = "";

    /// <summary>
    /// Salience score (0.0 - 1.0)
    /// </summary>
    [YamlMember(Alias = "salience")]
    public double Salience { get; set; } = 1.0;
}

/// <summary>
/// Wave trigger configuration
/// </summary>
public sealed class WaveTriggerConfig
{
    /// <summary>
    /// Required signals for execution
    /// </summary>
    [YamlMember(Alias = "requires")]
    public List<WaveTriggerCondition> Requires { get; set; } = new();

    /// <summary>
    /// Skip conditions
    /// </summary>
    [YamlMember(Alias = "skip_when")]
    public List<string> SkipWhen { get; set; } = new();
}

/// <summary>
/// Trigger condition
/// </summary>
public sealed class WaveTriggerCondition
{
    /// <summary>
    /// Signal to check
    /// </summary>
    [YamlMember(Alias = "signal")]
    public string Signal { get; set; } = "";

    /// <summary>
    /// Optional condition (e.g., "> 0", "== true")
    /// </summary>
    [YamlMember(Alias = "condition")]
    public string? Condition { get; set; }
}

/// <summary>
/// Wave emits configuration
/// </summary>
public sealed class WaveEmitsConfig
{
    /// <summary>
    /// Signals emitted on completion
    /// </summary>
    [YamlMember(Alias = "on_complete")]
    public List<WaveEmitSpec> OnComplete { get; set; } = new();

    /// <summary>
    /// Conditional signal emissions
    /// </summary>
    [YamlMember(Alias = "conditional")]
    public List<WaveConditionalEmit> Conditional { get; set; } = new();
}

/// <summary>
/// Emit specification
/// </summary>
public sealed class WaveEmitSpec
{
    /// <summary>
    /// Signal key
    /// </summary>
    [YamlMember(Alias = "key")]
    public string Key { get; set; } = "";

    /// <summary>
    /// Entity type
    /// </summary>
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "";

    /// <summary>
    /// Optional confidence range
    /// </summary>
    [YamlMember(Alias = "confidence_range")]
    public List<double>? ConfidenceRange { get; set; }
}

/// <summary>
/// Conditional emit
/// </summary>
public sealed class WaveConditionalEmit
{
    /// <summary>
    /// Signal key
    /// </summary>
    [YamlMember(Alias = "key")]
    public string Key { get; set; } = "";

    /// <summary>
    /// Condition expression
    /// </summary>
    [YamlMember(Alias = "when")]
    public string When { get; set; } = "";
}

/// <summary>
/// Wave defaults configuration (all parameters go here)
/// </summary>
public sealed class WaveDefaultsConfig
{
    /// <summary>
    /// Retrieval parameters
    /// </summary>
    [YamlMember(Alias = "retrieval")]
    public Dictionary<string, object> Retrieval { get; set; } = new();

    /// <summary>
    /// Scoring parameters
    /// </summary>
    [YamlMember(Alias = "scoring")]
    public Dictionary<string, object> Scoring { get; set; } = new();

    /// <summary>
    /// Timing parameters
    /// </summary>
    [YamlMember(Alias = "timing")]
    public Dictionary<string, object> Timing { get; set; } = new();

    /// <summary>
    /// Feature flags
    /// </summary>
    [YamlMember(Alias = "features")]
    public Dictionary<string, object> Features { get; set; } = new();

    /// <summary>
    /// Custom parameters
    /// </summary>
    [YamlMember(Alias = "parameters")]
    public Dictionary<string, object> Parameters { get; set; } = new();
}
