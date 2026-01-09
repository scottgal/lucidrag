using LucidRAG.Entities;

namespace LucidRAG.Services.Sentinel;

/// <summary>
/// Schema context providing the Sentinel with awareness of available
/// fields, content types, and evidence in the current tenant/collection.
///
/// This enables guided decomposition - the Sentinel knows what's queryable.
/// </summary>
public record SchemaContext
{
    /// <summary>
    /// Available content types in the collection.
    /// </summary>
    public required HashSet<string> ContentTypes { get; init; }

    /// <summary>
    /// Available evidence types that have been stored.
    /// </summary>
    public required HashSet<string> EvidenceTypes { get; init; }

    /// <summary>
    /// Available entity types from GraphRAG.
    /// </summary>
    public required HashSet<string> EntityTypes { get; init; }

    /// <summary>
    /// Available relationship types from GraphRAG.
    /// </summary>
    public required HashSet<string> RelationshipTypes { get; init; }

    /// <summary>
    /// For tabular data: available column names with their types.
    /// </summary>
    public required Dictionary<string, ColumnInfo> Columns { get; init; }

    /// <summary>
    /// Collection IDs available to search.
    /// </summary>
    public required List<CollectionInfo> Collections { get; init; }

    /// <summary>
    /// Total document count.
    /// </summary>
    public int DocumentCount { get; init; }

    /// <summary>
    /// Date range of documents.
    /// </summary>
    public DateTimeOffset? EarliestDocument { get; init; }
    public DateTimeOffset? LatestDocument { get; init; }

    /// <summary>
    /// Sample document names (for context).
    /// </summary>
    public List<string> SampleDocumentNames { get; init; } = [];

    /// <summary>
    /// Create an empty schema context (for no-data scenarios).
    /// </summary>
    public static SchemaContext Empty => new()
    {
        ContentTypes = [],
        EvidenceTypes = [],
        EntityTypes = [],
        RelationshipTypes = [],
        Columns = new Dictionary<string, ColumnInfo>(),
        Collections = []
    };

    /// <summary>
    /// Generate a text description for LLM prompts.
    /// </summary>
    public string ToPromptDescription()
    {
        var parts = new List<string>();

        if (ContentTypes.Count > 0)
            parts.Add($"Content types: {string.Join(", ", ContentTypes)}");

        if (EvidenceTypes.Count > 0)
            parts.Add($"Evidence available: {string.Join(", ", EvidenceTypes.Take(10))}{(EvidenceTypes.Count > 10 ? "..." : "")}");

        if (EntityTypes.Count > 0)
            parts.Add($"Entity types: {string.Join(", ", EntityTypes.Take(15))}{(EntityTypes.Count > 15 ? "..." : "")}");

        if (RelationshipTypes.Count > 0)
            parts.Add($"Relationship types: {string.Join(", ", RelationshipTypes.Take(10))}{(RelationshipTypes.Count > 10 ? "..." : "")}");

        if (Columns.Count > 0)
        {
            var colSample = Columns.Take(10).Select(c => $"{c.Key} ({c.Value.DataType})");
            parts.Add($"Data columns: {string.Join(", ", colSample)}{(Columns.Count > 10 ? "..." : "")}");
        }

        parts.Add($"Documents: {DocumentCount}");

        if (EarliestDocument.HasValue && LatestDocument.HasValue)
            parts.Add($"Date range: {EarliestDocument.Value:yyyy-MM-dd} to {LatestDocument.Value:yyyy-MM-dd}");

        if (SampleDocumentNames.Count > 0)
            parts.Add($"Sample documents: {string.Join(", ", SampleDocumentNames.Take(5))}");

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Check if a field/column exists.
    /// </summary>
    public bool HasColumn(string name) =>
        Columns.ContainsKey(name) ||
        Columns.Keys.Any(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Check if a content type exists.
    /// </summary>
    public bool HasContentType(string type) =>
        ContentTypes.Contains(type) ||
        ContentTypes.Any(t => t.Equals(type, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Check if an entity type exists.
    /// </summary>
    public bool HasEntityType(string type) =>
        EntityTypes.Contains(type) ||
        EntityTypes.Any(t => t.Equals(type, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Check if evidence of a type exists.
    /// </summary>
    public bool HasEvidenceType(string type) =>
        EvidenceTypes.Contains(type) ||
        EvidenceTypes.Any(t => t.Equals(type, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Information about a data column.
/// </summary>
public record ColumnInfo
{
    /// <summary>
    /// Column name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Data type (string, number, date, boolean).
    /// </summary>
    public required string DataType { get; init; }

    /// <summary>
    /// Sample values (for context).
    /// </summary>
    public List<string> SampleValues { get; init; } = [];

    /// <summary>
    /// Is this column nullable?
    /// </summary>
    public bool IsNullable { get; init; }

    /// <summary>
    /// Source documents/tables containing this column.
    /// </summary>
    public List<string> Sources { get; init; } = [];
}

/// <summary>
/// Information about a collection.
/// </summary>
public record CollectionInfo
{
    /// <summary>
    /// Collection ID.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Collection name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Document count in collection.
    /// </summary>
    public int DocumentCount { get; init; }

    /// <summary>
    /// Brief description.
    /// </summary>
    public string? Description { get; init; }
}
