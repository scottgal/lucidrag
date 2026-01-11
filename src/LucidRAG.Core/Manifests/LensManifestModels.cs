using YamlDotNet.Serialization;

namespace LucidRAG.Manifests;

/// <summary>
/// YAML manifest for a lens package (presentation/formatting layer).
/// Follows StyloFlow manifest pattern with underscored YAML keys.
/// </summary>
public sealed class LensManifest
{
    /// <summary>
    /// Unique lens identifier (e.g., "blog", "legal", "customer-support")
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Inherit from another lens (optional).
    /// Allows extending existing lenses with overrides.
    /// Format: lens name (e.g., "blog") or file path (e.g., "../blog.lens.yaml")
    /// </summary>
    [YamlMember(Alias = "inherits")]
    public string? Inherits { get; set; }

    /// <summary>
    /// Display name for UI
    /// </summary>
    [YamlMember(Alias = "display_name")]
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Lens description
    /// </summary>
    [YamlMember(Alias = "description")]
    public string Description { get; set; } = "";

    /// <summary>
    /// Semantic version (e.g., "1.0.0")
    /// </summary>
    [YamlMember(Alias = "version")]
    public string Version { get; set; } = "1.0.0";

    /// <summary>
    /// Author/maintainer
    /// </summary>
    [YamlMember(Alias = "author")]
    public string? Author { get; set; }

    /// <summary>
    /// Priority for lens ordering (higher = higher priority)
    /// </summary>
    [YamlMember(Alias = "priority")]
    public int Priority { get; set; } = 0;

    /// <summary>
    /// Whether this lens is enabled
    /// </summary>
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Tags for categorization
    /// </summary>
    [YamlMember(Alias = "tags")]
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Taxonomy classification
    /// </summary>
    [YamlMember(Alias = "taxonomy")]
    public LensTaxonomy Taxonomy { get; set; } = new();

    /// <summary>
    /// Scoring weights for RRF (must sum to 1.0)
    /// </summary>
    [YamlMember(Alias = "scoring")]
    public LensScoringConfig Scoring { get; set; } = new();

    /// <summary>
    /// Template file paths (relative to lens directory)
    /// </summary>
    [YamlMember(Alias = "templates")]
    public LensTemplatesConfig Templates { get; set; } = new();

    /// <summary>
    /// Optional styles configuration
    /// </summary>
    [YamlMember(Alias = "styles")]
    public LensStylesConfig? Styles { get; set; }

    /// <summary>
    /// Policies/guardrails for this lens
    /// </summary>
    [YamlMember(Alias = "policies")]
    public LensPoliciesConfig? Policies { get; set; }

    /// <summary>
    /// Default configuration values.
    /// CRITICAL: Must include source_text settings to prevent OCR text overload!
    /// </summary>
    [YamlMember(Alias = "defaults")]
    public Dictionary<string, object> Defaults { get; set; } = new();

    /// <summary>
    /// Helper method to get source text configuration.
    /// </summary>
    public SourceTextConfig? GetSourceTextConfig()
    {
        if (Defaults.TryGetValue("source_text", out var config) && config is Dictionary<string, object> dict)
        {
            return new SourceTextConfig
            {
                MaxCharsPerSource = GetInt(dict, "max_chars_per_source", 1000),
                PreferSummaryWhenAvailable = GetBool(dict, "prefer_summary_when_available", true),
                PreferLlmOverExtractive = GetBool(dict, "prefer_llm_over_extractive", true),
                UseSegmentsNotFullDocs = GetBool(dict, "use_segments_not_full_docs", true),
                MinCharsForSummary = GetInt(dict, "min_chars_for_summary", 500)
            };
        }
        return null;
    }

    private static int GetInt(Dictionary<string, object> dict, string key, int defaultValue)
    {
        if (dict.TryGetValue(key, out var value))
        {
            if (value is int i) return i;
            if (int.TryParse(value?.ToString(), out var parsed)) return parsed;
        }
        return defaultValue;
    }

    private static bool GetBool(Dictionary<string, object> dict, string key, bool defaultValue)
    {
        if (dict.TryGetValue(key, out var value))
        {
            if (value is bool b) return b;
            if (bool.TryParse(value?.ToString(), out var parsed)) return parsed;
        }
        return defaultValue;
    }

    /// <summary>
    /// Runtime type binding (optional - for future custom renderers)
    /// </summary>
    [YamlMember(Alias = "runtime")]
    public RuntimeConfig? Runtime { get; set; }
}

/// <summary>
/// Lens taxonomy classification
/// </summary>
public sealed class LensTaxonomy
{
    /// <summary>
    /// Content domain (e.g., "blog", "legal", "technical", "customer_support")
    /// </summary>
    [YamlMember(Alias = "domain")]
    public string Domain { get; set; } = "general";

    /// <summary>
    /// Presentation style (e.g., "conversational", "formal", "technical")
    /// </summary>
    [YamlMember(Alias = "style")]
    public string Style { get; set; } = "conversational";

    /// <summary>
    /// Target audience (e.g., "general", "expert", "customer")
    /// </summary>
    [YamlMember(Alias = "audience")]
    public string Audience { get; set; } = "general";
}

/// <summary>
/// RRF scoring weights configuration
/// </summary>
public sealed class LensScoringConfig
{
    /// <summary>
    /// Dense embedding similarity weight (0.0 - 1.0)
    /// </summary>
    [YamlMember(Alias = "dense_weight")]
    public double DenseWeight { get; set; } = 0.3;

    /// <summary>
    /// BM25 keyword search weight (0.0 - 1.0)
    /// </summary>
    [YamlMember(Alias = "bm25_weight")]
    public double Bm25Weight { get; set; } = 0.3;

    /// <summary>
    /// Salient terms weight (0.0 - 1.0)
    /// </summary>
    [YamlMember(Alias = "salience_weight")]
    public double SalienceWeight { get; set; } = 0.2;

    /// <summary>
    /// Document freshness weight (0.0 - 1.0)
    /// </summary>
    [YamlMember(Alias = "freshness_weight")]
    public double FreshnessWeight { get; set; } = 0.2;

    /// <summary>
    /// RRF k parameter (constant for rank fusion)
    /// </summary>
    [YamlMember(Alias = "rrf_k")]
    public int RrfK { get; set; } = 60;

    /// <summary>
    /// Maximum number of results to return
    /// </summary>
    [YamlMember(Alias = "max_results")]
    public int MaxResults { get; set; } = 10;

    /// <summary>
    /// Minimum score threshold (0.0 - 1.0)
    /// </summary>
    [YamlMember(Alias = "min_score")]
    public double? MinScore { get; set; }

    /// <summary>
    /// Fine-grained signal weights for precise tuning.
    /// Keys can be specific signals (e.g., "retrieval.dense.similarity")
    /// or signal classes (e.g., "retrieval.*", "entity.*").
    /// </summary>
    [YamlMember(Alias = "signal_weights")]
    public Dictionary<string, SignalWeight> SignalWeights { get; set; } = new();

    /// <summary>
    /// Boost multipliers for specific entity types or categories.
    /// </summary>
    [YamlMember(Alias = "entity_boosts")]
    public Dictionary<string, double> EntityBoosts { get; set; } = new();

    /// <summary>
    /// Document type weights (e.g., boost PDFs over markdown).
    /// </summary>
    [YamlMember(Alias = "document_type_weights")]
    public Dictionary<string, double> DocumentTypeWeights { get; set; } = new();
}

/// <summary>
/// Signal weight configuration for fine-grained control.
/// </summary>
public sealed class SignalWeight
{
    /// <summary>
    /// Base weight for this signal (0.0 - 1.0)
    /// </summary>
    [YamlMember(Alias = "weight")]
    public double Weight { get; set; } = 1.0;

    /// <summary>
    /// Optional boost multiplier
    /// </summary>
    [YamlMember(Alias = "boost")]
    public double? Boost { get; set; }

    /// <summary>
    /// Minimum threshold for this signal to be considered
    /// </summary>
    [YamlMember(Alias = "min_threshold")]
    public double? MinThreshold { get; set; }

    /// <summary>
    /// Maximum cap for this signal's contribution
    /// </summary>
    [YamlMember(Alias = "max_cap")]
    public double? MaxCap { get; set; }

    /// <summary>
    /// Whether to normalize this signal
    /// </summary>
    [YamlMember(Alias = "normalize")]
    public bool Normalize { get; set; } = true;

    /// <summary>
    /// Apply decay function (none | linear | exponential | logarithmic)
    /// </summary>
    [YamlMember(Alias = "decay")]
    public string? Decay { get; set; }
}

/// <summary>
/// Template file paths
/// </summary>
public sealed class LensTemplatesConfig
{
    /// <summary>
    /// System prompt template file (Liquid format)
    /// </summary>
    [YamlMember(Alias = "system_prompt")]
    public string SystemPrompt { get; set; } = "system-prompt.liquid";

    /// <summary>
    /// Citation format template file (Liquid format)
    /// </summary>
    [YamlMember(Alias = "citation")]
    public string Citation { get; set; } = "citation.liquid";

    /// <summary>
    /// Optional response wrapper template file (Liquid format)
    /// </summary>
    [YamlMember(Alias = "response")]
    public string? Response { get; set; }

    /// <summary>
    /// Optional user message rewrite template (Liquid format)
    /// </summary>
    [YamlMember(Alias = "user_prompt")]
    public string? UserPrompt { get; set; }
}

/// <summary>
/// Styles configuration
/// </summary>
public sealed class LensStylesConfig
{
    /// <summary>
    /// CSS file path (relative to lens directory)
    /// </summary>
    [YamlMember(Alias = "css_file")]
    public string? CssFile { get; set; }

    /// <summary>
    /// Inline CSS (alternative to css_file)
    /// </summary>
    [YamlMember(Alias = "inline_css")]
    public string? InlineCss { get; set; }

    /// <summary>
    /// Theme name (e.g., "light", "dark", "auto")
    /// </summary>
    [YamlMember(Alias = "theme")]
    public string? Theme { get; set; }
}

/// <summary>
/// Policies/guardrails configuration
/// </summary>
public sealed class LensPoliciesConfig
{
    /// <summary>
    /// Redact or refuse to include PII
    /// </summary>
    [YamlMember(Alias = "no_pii")]
    public bool NoPii { get; set; } = false;

    /// <summary>
    /// Never promise refunds without manager approval
    /// </summary>
    [YamlMember(Alias = "no_refunds")]
    public bool NoRefunds { get; set; } = false;

    /// <summary>
    /// Require citations for legal/medical claims
    /// </summary>
    [YamlMember(Alias = "require_citations_for_claims")]
    public bool RequireCitationsForClaims { get; set; } = false;

    /// <summary>
    /// Filter profanity and inappropriate content
    /// </summary>
    [YamlMember(Alias = "family_friendly")]
    public bool FamilyFriendly { get; set; } = false;

    /// <summary>
    /// Maximum response length in characters
    /// </summary>
    [YamlMember(Alias = "max_response_length")]
    public int? MaxResponseLength { get; set; }

    /// <summary>
    /// Custom policy rules (free-form text injected into system prompt)
    /// </summary>
    [YamlMember(Alias = "custom_rules")]
    public List<string> CustomRules { get; set; } = new();
}

/// <summary>
/// Runtime type binding
/// </summary>
public sealed class RuntimeConfig
{
    /// <summary>
    /// Fully-qualified type name (e.g., "LucidRAG.Lenses.BlogLensRenderer")
    /// </summary>
    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    /// <summary>
    /// Assembly name (if not in current assembly)
    /// </summary>
    [YamlMember(Alias = "assembly")]
    public string? Assembly { get; set; }
}

/// <summary>
/// Source text configuration for smart text selection.
/// </summary>
public sealed class SourceTextConfig
{
    /// <summary>
    /// Maximum characters per source before using summary.
    /// </summary>
    public int MaxCharsPerSource { get; set; } = 1000;

    /// <summary>
    /// Prefer summary when available (vs. full text).
    /// </summary>
    public bool PreferSummaryWhenAvailable { get; set; } = true;

    /// <summary>
    /// Prefer LLM summary over extractive summary.
    /// </summary>
    public bool PreferLlmOverExtractive { get; set; } = true;

    /// <summary>
    /// Always use segments, never full documents.
    /// </summary>
    public bool UseSegmentsNotFullDocs { get; set; } = true;

    /// <summary>
    /// Minimum characters for summary to be required.
    /// </summary>
    public int MinCharsForSummary { get; set; } = 500;
}
