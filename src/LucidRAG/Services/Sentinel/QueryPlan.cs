namespace LucidRAG.Services.Sentinel;

/// <summary>
/// Decomposed query plan produced by the Sentinel.
/// Contains sub-queries, filters, and execution strategies.
/// </summary>
public record QueryPlan
{
    /// <summary>
    /// Original user query.
    /// </summary>
    public required string OriginalQuery { get; init; }

    /// <summary>
    /// Sentinel's interpretation of what the user wants.
    /// </summary>
    public required string Intent { get; init; }

    /// <summary>
    /// Confidence in the interpretation (0-1).
    /// If below threshold, may trigger clarification.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Sub-queries to execute against the vector store.
    /// Each targets a specific aspect of the user's question.
    /// </summary>
    public required List<SubQuery> SubQueries { get; init; }

    /// <summary>
    /// Filters to apply (content type, collection, date range, etc.).
    /// </summary>
    public QueryFilters Filters { get; init; } = new();

    /// <summary>
    /// Graph traversal paths for entity relationships.
    /// </summary>
    public List<GraphTraversal> GraphPaths { get; init; } = [];

    /// <summary>
    /// Operations to perform on results (compare, aggregate, etc.).
    /// </summary>
    public List<ResultOperation> Operations { get; init; } = [];

    /// <summary>
    /// If true, sentinel needs clarification before proceeding.
    /// </summary>
    public bool NeedsClarification { get; init; }

    /// <summary>
    /// Clarification question to ask the user.
    /// </summary>
    public string? ClarificationQuestion { get; init; }

    /// <summary>
    /// Assumptions made by the sentinel (to validate).
    /// </summary>
    public List<SentinelAssumption> Assumptions { get; init; } = [];

    /// <summary>
    /// Execution mode (llm-guided, embedding-only, hybrid).
    /// </summary>
    public ExecutionMode Mode { get; init; } = ExecutionMode.Hybrid;

    /// <summary>
    /// Model that produced this plan.
    /// </summary>
    public string? ProducerModel { get; init; }

    /// <summary>
    /// Time taken to produce the plan in milliseconds.
    /// </summary>
    public long PlanningTimeMs { get; init; }
}

/// <summary>
/// A sub-query targeting a specific aspect of the user's question.
/// </summary>
public record SubQuery
{
    /// <summary>
    /// The query text to embed and search.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// What aspect of the original question this addresses.
    /// </summary>
    public required string Purpose { get; init; }

    /// <summary>
    /// Priority for retrieval (higher = more important).
    /// </summary>
    public int Priority { get; init; } = 1;

    /// <summary>
    /// Number of results to retrieve for this sub-query.
    /// </summary>
    public int TopK { get; init; } = 5;

    /// <summary>
    /// Whether to use BM25 (sparse) search for this sub-query.
    /// Useful for exact term matching.
    /// </summary>
    public bool UseSparse { get; init; }

    /// <summary>
    /// Content types to target (null = all).
    /// </summary>
    public string[]? ContentTypes { get; init; }

    /// <summary>
    /// Specific fields/columns to search (for tabular data).
    /// </summary>
    public string[]? TargetFields { get; init; }
}

/// <summary>
/// Filters to apply to search results.
/// </summary>
public record QueryFilters
{
    /// <summary>
    /// Content types to include (document, image, audio, video, data).
    /// </summary>
    public string[]? ContentTypes { get; init; }

    /// <summary>
    /// Collection IDs to search within.
    /// </summary>
    public Guid[]? CollectionIds { get; init; }

    /// <summary>
    /// Document IDs to search within.
    /// </summary>
    public Guid[]? DocumentIds { get; init; }

    /// <summary>
    /// Date range filter (documents modified after).
    /// </summary>
    public DateTimeOffset? ModifiedAfter { get; init; }

    /// <summary>
    /// Date range filter (documents modified before).
    /// </summary>
    public DateTimeOffset? ModifiedBefore { get; init; }

    /// <summary>
    /// Minimum confidence/quality threshold.
    /// </summary>
    public double? MinConfidence { get; init; }

    /// <summary>
    /// Entity types to search (from GraphRAG).
    /// </summary>
    public string[]? EntityTypes { get; init; }

    /// <summary>
    /// Column names to filter on (for tabular data).
    /// </summary>
    public Dictionary<string, string>? ColumnFilters { get; init; }
}

/// <summary>
/// Graph traversal path for entity relationships.
/// </summary>
public record GraphTraversal
{
    /// <summary>
    /// Starting entity name or type.
    /// </summary>
    public required string StartEntity { get; init; }

    /// <summary>
    /// Relationship types to follow.
    /// </summary>
    public string[]? RelationshipTypes { get; init; }

    /// <summary>
    /// Maximum traversal depth.
    /// </summary>
    public int MaxDepth { get; init; } = 2;

    /// <summary>
    /// End entity type to find (null = any).
    /// </summary>
    public string? EndEntityType { get; init; }
}

/// <summary>
/// Operations to perform on results.
/// </summary>
public record ResultOperation
{
    /// <summary>
    /// Operation type.
    /// </summary>
    public required ResultOperationType Type { get; init; }

    /// <summary>
    /// Fields/columns to operate on.
    /// </summary>
    public string[]? Fields { get; init; }

    /// <summary>
    /// Parameters for the operation.
    /// </summary>
    public Dictionary<string, object>? Parameters { get; init; }
}

/// <summary>
/// Types of operations to perform on results.
/// </summary>
public enum ResultOperationType
{
    /// <summary>Standard retrieval.</summary>
    Retrieve,

    /// <summary>Compare multiple documents/entities.</summary>
    Compare,

    /// <summary>Aggregate values (sum, avg, count).</summary>
    Aggregate,

    /// <summary>Find trends over time.</summary>
    Trend,

    /// <summary>Group results by field.</summary>
    GroupBy,

    /// <summary>Rank results by criteria.</summary>
    Rank,

    /// <summary>Find differences between items.</summary>
    Diff,

    /// <summary>Summarize across multiple sources.</summary>
    Synthesize
}

/// <summary>
/// An assumption made by the sentinel that can be validated.
/// </summary>
public record SentinelAssumption
{
    /// <summary>
    /// What the sentinel assumed.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// How to validate this assumption.
    /// </summary>
    public required AssumptionValidation Validation { get; init; }

    /// <summary>
    /// Confidence in the assumption (0-1).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Was this assumption validated?
    /// </summary>
    public bool? Validated { get; init; }

    /// <summary>
    /// Validation result details.
    /// </summary>
    public string? ValidationResult { get; init; }
}

/// <summary>
/// How to validate an assumption.
/// </summary>
public record AssumptionValidation
{
    /// <summary>
    /// Type of validation check.
    /// </summary>
    public required ValidationType Type { get; init; }

    /// <summary>
    /// Query or check to perform.
    /// </summary>
    public string? Query { get; init; }

    /// <summary>
    /// Field to check.
    /// </summary>
    public string? Field { get; init; }

    /// <summary>
    /// Expected value.
    /// </summary>
    public object? Expected { get; init; }
}

/// <summary>
/// Types of assumption validation.
/// </summary>
public enum ValidationType
{
    /// <summary>Check if a field exists in the schema.</summary>
    FieldExists,

    /// <summary>Check if a content type is available.</summary>
    ContentTypeExists,

    /// <summary>Check if an entity exists.</summary>
    EntityExists,

    /// <summary>Check if documents match a pattern.</summary>
    DocumentsExist,

    /// <summary>Check if relationship type exists.</summary>
    RelationshipExists,

    /// <summary>Check date range is valid.</summary>
    DateRangeValid,

    /// <summary>Quick embedding search to verify results exist.</summary>
    ResultsExist
}

/// <summary>
/// Query execution mode.
/// </summary>
public enum ExecutionMode
{
    /// <summary>
    /// Embedding-only search (no LLM synthesis).
    /// Fast, cheap, but less intelligent.
    /// </summary>
    EmbeddingOnly,

    /// <summary>
    /// Traditional keyword/BM25 search with embedding.
    /// No LLM interpretation.
    /// </summary>
    Traditional,

    /// <summary>
    /// Full hybrid search with LLM synthesis.
    /// Dense + sparse + graph + LLM.
    /// </summary>
    Hybrid,

    /// <summary>
    /// Graph-focused search following relationships.
    /// </summary>
    GraphTraversal,

    /// <summary>
    /// Multi-step search with intermediate reasoning.
    /// </summary>
    Agentic
}
