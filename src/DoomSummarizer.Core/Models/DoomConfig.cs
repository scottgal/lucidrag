using System.Text.Json.Serialization;

namespace DoomSummarizer.Models;

public record DoomConfig
{
    public string? Profile { get; init; }
    public SourcesConfig Sources { get; init; } = new();
    public SourceFilterConfig SourceFilter { get; init; } = new();
    public OllamaConfig Ollama { get; init; } = new();
    public EmbeddingConfig Embedding { get; init; } = new();
    public OutputConfig Output { get; init; } = new();
    public StorageConfig Storage { get; init; } = new();
    public LinkFollowingConfig LinkFollowing { get; init; } = new();
    public EmailConfig Email { get; init; } = new();
    public PluginsConfig Plugins { get; init; } = new();
    public LlamaSharpConfigSection LlamaSharp { get; init; } = new();
    public IngestionConfig Ingestion { get; init; } = new();
    public ExpansionConfig Expansion { get; init; } = new();
    public ClassifierConfig Classifier { get; init; } = new();
    public LearningConfig Learning { get; init; } = new();
    public Dictionary<string, string> Vibes { get; init; } = new();
    public List<ApiKeyEntry> Keys { get; init; } = [];
    public ApiBudgetConfig ApiBudget { get; init; } = new();
}

/// <summary>
///     LLamaSharp local GGUF inference settings that can be overridden via config profiles.
///     Nullable fields indicate "use the LLamaSharpConfig defaults".
/// </summary>
public record LlamaSharpConfigSection
{
    public bool? Enabled { get; init; }
    public string? SynthesisModel { get; init; }
    public string? SentinelModel { get; init; }
    public uint? ContextSize { get; init; }
    public int? GpuLayerCount { get; init; }
    public int? GpuDeviceId { get; init; }
    public int? BatchSize { get; init; }
}

/// <summary>
///     Controls ingestion behavior: embedding rate, deduplication, and chunk limits.
///     The embedding rate is a device-profile-friendly percentage (0–100) that controls
///     what fraction of a document's chunks get embedded and indexed in HNSW.
///     100% = embed everything (desktop with good GPU), lower values save compute
///     by only embedding the highest-salience chunks (useful for Pi, laptop, etc.).
///     Non-embedded chunks are still stored in SQLite + FTS5 for keyword search.
/// </summary>
public record IngestionConfig
{
    /// <summary>
    ///     Percentage of chunks to embed (0–100). Controls compute vs coverage tradeoff.
    ///     100 = embed all chunks (desktop/server). 50 = embed top half by salience.
    ///     Non-embedded chunks remain searchable via FTS5 keywords.
    ///     Set per device profile: desktop=100, laptop=80, pi=40.
    /// </summary>
    public int EmbeddingRate { get; init; } = 100;

    /// <summary>Enable semantic dedup during ingestion (cosine >= threshold → merge).</summary>
    public bool DeduplicationEnabled { get; init; } = true;

    /// <summary>Cosine similarity threshold for near-duplicate detection during ingestion.</summary>
    public float DeduplicationThreshold { get; init; } = 0.90f;

    /// <summary>Boost surviving chunks' salience when they absorb near-duplicates.</summary>
    public bool SalienceBoostEnabled { get; init; } = true;

    /// <summary>Override max chunk survivors per document (0 = use adaptive default).</summary>
    public int MaxChunksOverride { get; init; } = 0;

    /// <summary>Override min chunk survivors per document (0 = use adaptive default).</summary>
    public int MinChunksOverride { get; init; } = 0;

    /// <summary>
    ///     Pre-embedding cheap dedup: eliminate obvious duplicates BEFORE embedding using
    ///     fast text signals (content hash, word Jaccard, trigrams, length). Saves 20-50%
    ///     of embedding compute on repetitive documents. Each signal has a configurable weight.
    ///     Set all weights to 0 to disable pre-dedup.
    /// </summary>
    public PreDedupWeights PreDedup { get; init; } = new();
}

/// <summary>
///     Configurable weights for pre-embedding cheap dedup signals.
///     Combined weighted score above <see cref="Threshold" /> eliminates the lower-salience chunk
///     before any embedding computation. All signals are O(N) per chunk — microseconds vs
///     embedding's milliseconds. Dial weights down for resampling (re-include previously disposed chunks).
/// </summary>
public record PreDedupWeights
{
    /// <summary>Weight for word-set Jaccard similarity (bag-of-words overlap). Most effective signal.</summary>
    public float WordJaccard { get; init; } = 0.50f;

    /// <summary>Weight for character trigram Jaccard similarity. Catches minor edits and paraphrases.</summary>
    public float Trigram { get; init; } = 0.30f;

    /// <summary>Weight for normalized length similarity (1.0 when same length, decays as lengths diverge).</summary>
    public float Length { get; init; } = 0.10f;

    /// <summary>Weight for title/heading overlap (chunks sharing headings are more likely duplicates).</summary>
    public float Heading { get; init; } = 0.10f;

    /// <summary>Combined weighted score threshold: pairs above this are pre-disposed (lower-salience removed).</summary>
    public float Threshold { get; init; } = 0.80f;

    /// <summary>True if all weights are zero (pre-dedup disabled).</summary>
    public bool IsDisabled => WordJaccard <= 0 && Trigram <= 0 && Length <= 0 && Heading <= 0;
}

/// <summary>
///     Controls document concentration detection and on-demand expansion during retrieval.
///     When retrieval results concentrate on one document, automatically pulls more chunks from it.
/// </summary>
public record ExpansionConfig
{
    /// <summary>Minimum fraction of top-K from one source to trigger expansion (0.0–1.0).</summary>
    public float ConcentrationThreshold { get; init; } = 0.4f;

    /// <summary>Minimum average relevance score for the concentrated source.</summary>
    public float MinRelevanceForExpansion { get; init; } = 0.6f;

    /// <summary>Base number of extra chunks to pull from the concentrated source.</summary>
    public int ExpansionCount { get; init; } = 8;

    /// <summary>Enable on-demand embedding of low-salience chunks during expansion.</summary>
    public bool DeferredEmbedding { get; init; } = true;
}

/// <summary>
///     Semantic query classifier thresholds. Controls the embedding-based
///     multi-match weighted voting algorithm used for deterministic pre-LLM classification.
///     All thresholds can be tuned per device profile or user preference.
/// </summary>
public record ClassifierConfig
{
    /// <summary>Minimum cosine similarity for an exemplar to enter the candidate set.</summary>
    public float MinCandidateThreshold { get; init; } = 0.35f;

    /// <summary>Minimum weighted vote score to include a topic in the result.</summary>
    public float MinTopicThreshold { get; init; } = 0.35f;

    /// <summary>Minimum weighted vote score for a type to be considered.</summary>
    public float MinTypeThreshold { get; init; } = 0.30f;

    /// <summary>Base count boost multiplier in IDF-weighted voting: max_sim + CountBoost * log2(count) * idf.</summary>
    public double CountBoost { get; init; } = 0.05;

    /// <summary>Minimum raw cosine similarity for a complex exemplar to flag the query as complex.</summary>
    public double ComplexThreshold { get; init; } = 0.50;

    /// <summary>
    ///     Minimum ratio of bestComplexScore / bestOverallScore for complex flag to fire.
    ///     Prevents false complex escalation when a simple query matches complex exemplars
    ///     only because they share the same topic semantic space.
    /// </summary>
    public double ComplexMinRatio { get; init; } = 0.85;

    /// <summary>Minimum raw cosine similarity for a vibe exemplar to trigger vibe detection.</summary>
    public double VibeThreshold { get; init; } = 0.60;

    /// <summary>
    ///     Minimum ratio of bestVibeScore / bestOverallScore for vibe to fire.
    ///     Prevents false vibes when a neutral query is much closer to non-vibe exemplars.
    ///     0.85 = vibe exemplar must be within 85% of the best overall match.
    /// </summary>
    public double VibeMinRatio { get; init; } = 0.85;

    /// <summary>Minimum raw cosine similarity (consensus of top 2) for composite detection.</summary>
    public double CompositeRawThreshold { get; init; } = 0.82;

    // ── Short-Query Feature Decomposition ──

    /// <summary>Maximum word count to consider a query "short" (features apply more strongly).</summary>
    public int ShortQueryMaxWords { get; init; } = 4;

    /// <summary>Type score boost when a howto intent marker is detected on a short query.</summary>
    public double HowtoFeatureBoost { get; init; } = 0.12;

    /// <summary>Type score boost when a comparison intent marker is detected on a short query.</summary>
    public double ComparisonFeatureBoost { get; init; } = 0.12;

    /// <summary>Type score boost for roundup when short query has no intent markers.</summary>
    public double DefaultRoundupBoost { get; init; } = 0.08;

    /// <summary>Type score boost when a QA intent marker (what is, who is) is detected on a short query.</summary>
    public double QaFeatureBoost { get; init; } = 0.10;

    /// <summary>Minimum confidence for search_only feature-based override.</summary>
    public double SearchOnlyFeatureThreshold { get; init; } = 0.60;

    /// <summary>Enable synonym expansion for abbreviations in short queries (MiniLM handles these well without expansion).</summary>
    public bool SynonymExpansionEnabled { get; init; } = false;

    /// <summary>Confidence scaling factor for short queries (applied to final type confidence).</summary>
    public double ShortQueryConfidenceScale { get; init; } = 0.85;
}

/// <summary>
///     Background learning configuration. Controls how the classifier learns
///     from sentinel disagreements over time.
/// </summary>
public record LearningConfig
{
    /// <summary>Enable automatic learning from sentinel disagreements.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    ///     How often to check for new disagreements and propose exemplars.
    ///     For CLI: checked on startup, learns if enough time has passed.
    ///     For server: used as the background service scan interval.
    /// </summary>
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Minimum cluster size to generate an exemplar proposal.</summary>
    public int MinClusterSize { get; init; } = 3;

    /// <summary>
    ///     Automatically merge proposals if all tests pass and improvement exceeds threshold.
    ///     false (default) = propose only, require manual --learn-apply.
    ///     true = auto-merge if no regressions detected.
    /// </summary>
    public bool AutoMerge { get; init; } = false;

    /// <summary>
    ///     Minimum net improvement (improvements - regressions) to auto-merge.
    ///     Only used when AutoMerge is true.
    /// </summary>
    public int AutoMergeMinImprovement { get; init; } = 1;
}

public record SourcesConfig
{
    public HackerNewsConfig HackerNews { get; init; } = new();
    public RedditConfig Reddit { get; init; } = new();
    public List<WebsiteConfig> Websites { get; init; } = [];
}

public record HackerNewsConfig
{
    public bool Enabled { get; init; } = true;
    public List<string> Sections { get; init; } = ["top", "best"];
    public int MaxStories { get; init; } = 30;
    public int MinScore { get; init; } = 50;
}

public record RedditConfig
{
    public bool Enabled { get; init; } = true;
    public List<string> Subreddits { get; init; } = ["programming", "csharp", "dotnet"];
    public string Sort { get; init; } = "hot";
    public int MaxPosts { get; init; } = 25;
    public int MinScore { get; init; } = 100;
}

public record WebsiteConfig
{
    public string Url { get; init; } = "";
    public string? Selector { get; init; }
    public bool UsePlaywright { get; init; }
}

/// <summary>
///     Global source filtering and reliability weighting.
///     Controls which domains are allowed/blocked and how sources are weighted in RRF scoring.
/// </summary>
public record SourceFilterConfig
{
    /// <summary>
    ///     If non-empty, ONLY items from these domains are kept (allowlist mode).
    ///     Useful for intranet/focused crawling. Matches domain suffix (e.g. "bbc.co.uk").
    /// </summary>
    public List<string> AllowedDomains { get; init; } = [];

    /// <summary>
    ///     Items from these domains are removed post-fetch.
    ///     Matches domain suffix (e.g. "medium.com" blocks all Medium articles).
    /// </summary>
    public List<string> BlockedDomains { get; init; } = [];

    /// <summary>
    ///     Source reliability weights applied as RRF score multipliers.
    ///     Key = source name (hn, reddit, bbc, gnews, search) or domain substring (reuters.com, bbc.co.uk).
    ///     Value = multiplier: 1.0 = neutral, >1 = boost, less than 1 = penalize, 0 = effectively block.
    ///     Unmatched sources default to 1.0.
    /// </summary>
    public Dictionary<string, double> Weights { get; init; } = new();
}

// OllamaConfig is now defined in LucidRAG.LLM (Models/OllamaConfig.cs)

public record EmbeddingConfig
{
    public string Backend { get; init; } = "onnx";

    /// <summary>
    ///     Embedding model name. Available models:
    ///     all-MiniLM-L6-v2 (default, fast general-purpose, 256 seq),
    ///     bge-small-en-v1.5 (best quality for size, 512 seq),
    ///     gte-small (good all-around, 512 seq),
    ///     multi-qa-MiniLM-L6-cos-v1 (QA-optimized, 512 seq),
    ///     paraphrase-MiniLM-L3-v2 (smallest/fastest, 128 seq).
    /// </summary>
    public string Model { get; init; } = "all-MiniLM-L6-v2";

    /// <summary>
    ///     Use quantized ONNX models (smaller, faster, ~1-2% quality loss).
    ///     true = INT8 quantized (recommended for most workloads).
    ///     false = FP32 full precision.
    /// </summary>
    public bool Quantized { get; init; } = true;

    public double SimilarityThreshold { get; init; } = 0.95;

    /// <summary>
    ///     GPU device ID for ONNX embedding inference.
    ///     0 = first GPU, 1 = second GPU, etc.
    ///     Use this to select your discrete GPU when you have integrated graphics.
    ///     Run --list-gpus or 'nvidia-smi -L' to list GPU device IDs.
    /// </summary>
    public int GpuDeviceId { get; init; } = 0;

    /// <summary>
    ///     ONNX execution provider: auto, cpu, cuda, directml.
    ///     auto (default) = try DirectML → CUDA → CPU.
    ///     Use --list-gpus to see available providers on your system.
    /// </summary>
    public string ExecutionProvider { get; init; } = "auto";
}

public record OutputConfig
{
    public string Format { get; init; } = "markdown";
    public int MaxSummaryLength { get; init; } = 500;
    public bool IncludeLinks { get; init; } = true;
    public bool GroupByTopic { get; init; } = true;

    /// <summary>
    ///     Default output template for document/file collections when no --template is specified.
    ///     "default" = concise 3-4 paragraph summary (recommended for CLI).
    ///     "blog-article" = long-form multi-section article.
    ///     "compact" = minimal bullet-point list.
    ///     See --list-templates for all available templates.
    /// </summary>
    public string DefaultTemplate { get; init; } = "default";
}

public record StorageConfig
{
    public string DbPath { get; init; } = "~/.doomsummarizer/doom.db";
    public string VectorDbPath { get; init; } = "~/.doomsummarizer/vectors.duckdb";
    public int RetentionDays { get; init; } = 30;

    /// <summary>
    ///     Vector store backend for item embeddings.
    ///     "duckdb" (default) — DuckDB with HNSW index, ~96MB, best for LucidResearch.
    ///     "sqlite-vec" — sqlite-vec brute-force cosine, ~369KB, best for DoomSummarizer CLI.
    ///     See docs/VECTOR_STORE_TIERS.md for the full three-tier architecture.
    /// </summary>
    public string VectorBackend { get; init; } = "duckdb";
}

public record LinkFollowingConfig
{
    /// <summary>Enable one-hop link following to enrich article content.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Maximum links to follow per article.</summary>
    public int MaxLinksPerArticle { get; init; } = 3;

    /// <summary>Maximum total linked pages to fetch across all articles.</summary>
    public int MaxTotalLinks { get; init; } = 15;

    /// <summary>Maximum content length (chars) to extract per linked page.</summary>
    public int MaxContentLength { get; init; } = 2000;

    /// <summary>Timeout in seconds for each linked page fetch.</summary>
    public int TimeoutSeconds { get; init; } = 10;

    /// <summary>Domains to never follow links to (social media, login, etc.).</summary>
    public List<string> BlockedDomains { get; init; } =
    [
        "facebook.com", "twitter.com", "x.com", "instagram.com",
        "linkedin.com", "youtube.com", "tiktok.com",
        "accounts.google.com", "login.", "auth.",
        "play.google.com", "apps.apple.com"
    ];

    /// <summary>File extensions to skip.</summary>
    public List<string> BlockedExtensions { get; init; } =
    [
        ".pdf", ".zip", ".tar", ".gz", ".exe", ".dmg",
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp",
        ".mp3", ".mp4", ".mov", ".avi", ".mkv"
    ];
}

// ApiKeyEntry is now defined in LucidRAG.LLM (Models/ApiKeyEntry.cs)

/// <summary>
///     Email delivery configuration. Supports SMTP (via MailKit) and SendGrid.
///     API keys can be stored in user secrets: dotnet user-secrets set "SendGrid" "SG.xxx"
/// </summary>
public record EmailConfig
{
    /// <summary>Email delivery provider: "smtp" or "sendgrid".</summary>
    public string Provider { get; init; } = "smtp";

    /// <summary>Whether email delivery is enabled.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>Sender email address (From).</summary>
    public string FromAddress { get; init; } = "";

    /// <summary>Sender display name.</summary>
    public string FromName { get; init; } = "DoomSummarizer";

    /// <summary>Default recipient(s). Comma-separated for multiple.</summary>
    public string ToAddresses { get; init; } = "";

    /// <summary>Email subject line template. Use {{DATE}} and {{QUERY}} placeholders.</summary>
    public string SubjectTemplate { get; init; } = "Doom Scroll Digest — {{DATE}}";

    /// <summary>Output template to use for email body (e.g., "email", "newsletter").</summary>
    public string Template { get; init; } = "email";

    /// <summary>SMTP settings (used when Provider = "smtp").</summary>
    public SmtpConfig Smtp { get; init; } = new();

    /// <summary>SendGrid API key (prefer user secrets or env var DOOM_SENDGRID).</summary>
    public string? SendGridApiKey { get; init; }
}

/// <summary>SMTP connection settings for MailKit.</summary>
public record SmtpConfig
{
    public string Host { get; init; } = "smtp.gmail.com";
    public int Port { get; init; } = 587;
    public bool UseSsl { get; init; } = true;
    public string? Username { get; init; }
    public string? Password { get; init; }
}

/// <summary>
///     Plugin management configuration. Controls which plugins are enabled,
///     auto-install behavior, and per-plugin overrides.
/// </summary>
public record PluginsConfig
{
    /// <summary>Enable runtime plugin loading from ~/.doomsummarizer/plugins/.</summary>
    public bool EnableRuntimePlugins { get; init; } = true;

    /// <summary>Auto-install these plugins on first run (shorthand or full NuGet ID).</summary>
    public List<string> AutoInstall { get; init; } = [];

    /// <summary>Disabled plugin keys — these won't be loaded even if installed.</summary>
    public List<string> Disabled { get; init; } = [];

    /// <summary>
    ///     Per-plugin settings. Key = plugin primary key (e.g., "hn", "reddit", "search").
    ///     Values are passed to the plugin's InitializeAsync as configuration overrides.
    /// </summary>
    public Dictionary<string, PluginSettings> Settings { get; init; } = new();
}

/// <summary>
///     Per-plugin configuration overrides. Stored in config under plugins.settings.[key].
/// </summary>
public record PluginSettings
{
    /// <summary>Whether this plugin is enabled. Overrides the global disabled list.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Maximum items to fetch per invocation (overrides --limit for this source).</summary>
    public int? MaxItems { get; init; }

    /// <summary>Plugin-specific key-value options. Interpretation depends on the plugin.</summary>
    public Dictionary<string, string> Options { get; init; } = new();
}

// ApiBudgetConfig is now defined in Mostlylucid.DocSummarizer.Resilience.
// Re-exported via global using in Services/ApiBudgetService.cs.

// JSON serialization context for AOT
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DoomConfig))]
[JsonSerializable(typeof(SourcesConfig))]
[JsonSerializable(typeof(SourceFilterConfig))]
[JsonSerializable(typeof(HackerNewsConfig))]
[JsonSerializable(typeof(RedditConfig))]
[JsonSerializable(typeof(WebsiteConfig))]
[JsonSerializable(typeof(OllamaConfig))]
[JsonSerializable(typeof(EmbeddingConfig))]
[JsonSerializable(typeof(OutputConfig))]
[JsonSerializable(typeof(StorageConfig))]
[JsonSerializable(typeof(LinkFollowingConfig))]
[JsonSerializable(typeof(ApiKeyEntry))]
[JsonSerializable(typeof(ApiBudgetConfig))]
[JsonSerializable(typeof(EmailConfig))]
[JsonSerializable(typeof(SmtpConfig))]
[JsonSerializable(typeof(PluginsConfig))]
[JsonSerializable(typeof(PluginSettings))]
[JsonSerializable(typeof(LlamaSharpConfigSection))]
[JsonSerializable(typeof(IngestionConfig))]
[JsonSerializable(typeof(PreDedupWeights))]
[JsonSerializable(typeof(ExpansionConfig))]
[JsonSerializable(typeof(ClassifierConfig))]
[JsonSerializable(typeof(LearningConfig))]
[JsonSerializable(typeof(List<ApiKeyEntry>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
public partial class DoomConfigContext : JsonSerializerContext;