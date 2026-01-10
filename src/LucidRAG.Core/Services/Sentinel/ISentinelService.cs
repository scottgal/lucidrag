namespace LucidRAG.Services.Sentinel;

/// <summary>
/// The Sentinel is the query decomposition and planning service.
///
/// It sits at the front of the search pipeline and:
/// 1. Analyzes the user's query to understand intent
/// 2. Decomposes complex queries into sub-queries
/// 3. Determines filters, graph traversals, and operations
/// 4. Validates assumptions against the schema
/// 5. Requests clarification when confidence is low
///
/// Supports multiple modes:
/// - Tiny model (TinyLlama, qwen2.5:1.5b) for fast decomposition
/// - No-LLM traditional mode for direct embedding search
/// - Full LLM mode for complex queries (escalation)
/// </summary>
public interface ISentinelService
{
    /// <summary>
    /// Decompose a user query into an executable plan.
    /// </summary>
    /// <param name="query">The user's natural language query.</param>
    /// <param name="schema">Schema context (available fields, types, etc.).</param>
    /// <param name="options">Planning options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A QueryPlan ready for execution.</returns>
    Task<QueryPlan> DecomposeAsync(
        string query,
        SchemaContext schema,
        SentinelOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Validate assumptions in a query plan against the actual data.
    /// </summary>
    /// <param name="plan">The query plan to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated plan with validation results.</returns>
    Task<QueryPlan> ValidateAssumptionsAsync(
        QueryPlan plan,
        CancellationToken ct = default);

    /// <summary>
    /// Build schema context from the current data state.
    /// </summary>
    /// <param name="collectionId">Optional collection to scope to.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SchemaContext> BuildSchemaContextAsync(
        Guid? collectionId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Simple decomposition without LLM (pattern-based).
    /// Falls back to this when no LLM available or for simple queries.
    /// </summary>
    QueryPlan DecomposeTraditional(string query, SchemaContext schema);
}

/// <summary>
/// Options for the Sentinel planning process.
/// </summary>
public record SentinelOptions
{
    /// <summary>
    /// Execution mode to use.
    /// Default: Hybrid (uses LLM if available, falls back to traditional).
    /// </summary>
    public ExecutionMode Mode { get; init; } = ExecutionMode.Hybrid;

    /// <summary>
    /// Confidence threshold below which to request clarification.
    /// </summary>
    public double ClarificationThreshold { get; init; } = 0.6;

    /// <summary>
    /// Maximum sub-queries to generate.
    /// </summary>
    public int MaxSubQueries { get; init; } = 5;

    /// <summary>
    /// Whether to validate assumptions automatically.
    /// </summary>
    public bool ValidateAssumptions { get; init; } = true;

    /// <summary>
    /// Force use of a specific model (null = auto-select).
    /// </summary>
    public string? ForceModel { get; init; }

    /// <summary>
    /// Maximum planning time before falling back to traditional.
    /// </summary>
    public TimeSpan MaxPlanningTime { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Allow escalation to larger model if tiny model fails.
    /// </summary>
    public bool AllowEscalation { get; init; } = true;

    /// <summary>
    /// Collection ID to scope the search to.
    /// </summary>
    public Guid? CollectionId { get; init; }

    /// <summary>
    /// Document IDs to scope the search to.
    /// </summary>
    public Guid[]? DocumentIds { get; init; }
}

/// <summary>
/// Configuration for the Sentinel service.
/// </summary>
public class SentinelConfig
{
    /// <summary>
    /// Tiny model for fast decomposition (e.g., "tinyllama", "qwen2.5:1.5b").
    /// </summary>
    public string TinyModel { get; set; } = "qwen2.5:1.5b";

    /// <summary>
    /// Escalation model for complex queries (e.g., "qwen2.5:7b", "llama3.2").
    /// </summary>
    public string EscalationModel { get; set; } = "qwen2.5:7b";

    /// <summary>
    /// Whether the Sentinel is enabled (false = always traditional).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Ollama base URL.
    /// </summary>
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Maximum tokens for decomposition.
    /// </summary>
    public int MaxTokens { get; set; } = 1024;

    /// <summary>
    /// Temperature for decomposition (lower = more deterministic).
    /// </summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>
    /// Confidence threshold below which to request clarification.
    /// </summary>
    public double ClarificationThreshold { get; set; } = 0.6;

    /// <summary>
    /// Cache query plans for similar queries.
    /// </summary>
    public bool CachePlans { get; set; } = true;

    /// <summary>
    /// Plan cache TTL.
    /// </summary>
    public TimeSpan PlanCacheTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Vector collection name for validation queries.
    /// </summary>
    public string CollectionName { get; set; } = "ragdocs";
}
