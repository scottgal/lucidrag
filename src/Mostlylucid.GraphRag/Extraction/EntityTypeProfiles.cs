namespace Mostlylucid.GraphRag.Extraction;

/// <summary>
/// Defines entity type profiles for different content types and domains.
///
/// Profiles provide context-aware entity extraction:
/// - Technical docs: technology, framework, library, api, pattern
/// - Legal docs: party, clause, term, obligation, date
/// - Business docs: company, person, product, metric, process
///
/// "Lenses" are domain-specific overlays that can be applied on top of base profiles.
/// </summary>
public static class EntityTypeProfiles
{
    /// <summary>
    /// Default profile for technical/software documentation.
    /// Used for markdown, code docs, technical blogs.
    /// </summary>
    public static readonly EntityProfile Technical = new()
    {
        ProfileId = "technical",
        DisplayName = "Technical Documentation",
        EntityTypes =
        [
            new("technology", "Technologies, platforms, and tech stacks", ["tech", "platform"]),
            new("framework", "Software frameworks and SDKs", ["sdk"]),
            new("library", "Libraries and packages", ["package", "module", "nuget", "npm"]),
            new("language", "Programming languages", ["lang"]),
            new("tool", "Development tools and utilities", ["utility", "cli"]),
            new("database", "Databases and data stores", ["datastore", "storage"]),
            new("api", "APIs, endpoints, and protocols", ["endpoint", "protocol", "rest", "graphql"]),
            new("pattern", "Design patterns and architectures", ["architecture", "design"]),
            new("concept", "Technical concepts and principles", ["principle", "methodology"]),
            new("service", "Services and cloud resources", ["cloud", "saas"])
        ],
        StructuralSignals = ["inline_code", "heading", "link_text"],
        MinIdfThreshold = 3.5,
        MinMentionCount = 3
    };

    /// <summary>
    /// Profile for source code files.
    /// Focuses on code-specific entities.
    /// </summary>
    public static readonly EntityProfile Code = new()
    {
        ProfileId = "code",
        DisplayName = "Source Code",
        EntityTypes =
        [
            new("class", "Classes, structs, and types", ["type", "struct", "interface", "enum"]),
            new("function", "Functions, methods, and procedures", ["method", "procedure", "handler"]),
            new("variable", "Variables, constants, and fields", ["field", "property", "constant"]),
            new("namespace", "Namespaces and modules", ["module", "package"]),
            new("dependency", "External dependencies and imports", ["import", "require", "using"]),
            new("pattern", "Design patterns implemented", ["architecture"]),
            new("api", "API endpoints and contracts", ["endpoint", "route", "controller"]),
            new("error", "Error types and exceptions", ["exception"])
        ],
        StructuralSignals = ["inline_code", "code_block"],
        MinIdfThreshold = 3.0,
        MinMentionCount = 2
    };

    /// <summary>
    /// Profile for legal documents (contracts, agreements, policies).
    /// </summary>
    public static readonly EntityProfile Legal = new()
    {
        ProfileId = "legal",
        DisplayName = "Legal Documents",
        EntityTypes =
        [
            new("party", "Parties to the agreement", ["signatory", "contractor", "vendor"]),
            new("person", "Named individuals", ["individual", "officer", "director"]),
            new("organization", "Companies and legal entities", ["company", "corporation", "llc"]),
            new("clause", "Contract clauses and sections", ["section", "article", "provision"]),
            new("term", "Defined terms", ["definition"]),
            new("obligation", "Duties and obligations", ["duty", "requirement", "shall"]),
            new("right", "Rights and permissions", ["permission", "entitlement"]),
            new("date", "Dates and deadlines", ["deadline", "effective_date", "termination"]),
            new("amount", "Monetary amounts and fees", ["fee", "payment", "compensation"]),
            new("jurisdiction", "Jurisdictions and governing law", ["venue", "law"])
        ],
        StructuralSignals = ["heading", "bold", "numbered_item"],
        MinIdfThreshold = 2.5,
        MinMentionCount = 1
    };

    /// <summary>
    /// Profile for business/corporate documents.
    /// </summary>
    public static readonly EntityProfile Business = new()
    {
        ProfileId = "business",
        DisplayName = "Business Documents",
        EntityTypes =
        [
            new("company", "Companies and organizations", ["organization", "firm", "corporation"]),
            new("person", "People and contacts", ["contact", "stakeholder", "executive"]),
            new("product", "Products and offerings", ["offering", "solution", "service"]),
            new("metric", "KPIs and measurements", ["kpi", "measure", "indicator"]),
            new("process", "Business processes", ["workflow", "procedure"]),
            new("department", "Teams and departments", ["team", "unit", "division"]),
            new("project", "Projects and initiatives", ["initiative", "program"]),
            new("location", "Locations and regions", ["region", "office", "site"]),
            new("date", "Important dates", ["deadline", "milestone"]),
            new("amount", "Financial figures", ["budget", "cost", "revenue"])
        ],
        StructuralSignals = ["heading", "table_cell", "bold"],
        MinIdfThreshold = 3.0,
        MinMentionCount = 2
    };

    /// <summary>
    /// Profile for tabular/structured data.
    /// </summary>
    public static readonly EntityProfile Data = new()
    {
        ProfileId = "data",
        DisplayName = "Structured Data",
        EntityTypes =
        [
            new("column", "Data columns and fields", ["field", "attribute"]),
            new("table", "Tables and datasets", ["dataset", "sheet"]),
            new("key", "Primary and foreign keys", ["id", "identifier"]),
            new("metric", "Numeric metrics and measures", ["measure", "value"]),
            new("category", "Categorical values", ["type", "class", "group"]),
            new("date", "Date/time fields", ["timestamp", "datetime"]),
            new("entity", "Business entities in data", ["record", "row"])
        ],
        StructuralSignals = ["table_header", "column_name"],
        MinIdfThreshold = 2.0,
        MinMentionCount = 1
    };

    /// <summary>
    /// Profile for general/mixed content.
    /// Balanced approach when document type is unclear.
    /// </summary>
    public static readonly EntityProfile General = new()
    {
        ProfileId = "general",
        DisplayName = "General Content",
        EntityTypes =
        [
            new("person", "People and individuals", ["individual", "author"]),
            new("organization", "Organizations and companies", ["company", "institution"]),
            new("location", "Places and locations", ["place", "region", "country"]),
            new("concept", "Key concepts and ideas", ["idea", "topic", "theme"]),
            new("event", "Events and occurrences", ["meeting", "conference"]),
            new("date", "Dates and times", ["time", "period"]),
            new("product", "Products and services", ["service", "offering"]),
            new("technology", "Technologies mentioned", ["tool", "platform"])
        ],
        StructuralSignals = ["heading", "bold", "link_text"],
        MinIdfThreshold = 3.5,
        MinMentionCount = 3
    };

    /// <summary>
    /// Get the appropriate profile for a content type.
    /// </summary>
    public static EntityProfile GetForContentType(string contentType)
    {
        return contentType?.ToLowerInvariant() switch
        {
            "code" => Code,
            "data" => Data,
            "document" => Technical, // Default docs to technical (blog/markdown focus)
            _ => General
        };
    }

    /// <summary>
    /// Get profile by ID.
    /// </summary>
    public static EntityProfile? GetById(string profileId)
    {
        return profileId?.ToLowerInvariant() switch
        {
            "technical" => Technical,
            "code" => Code,
            "legal" => Legal,
            "business" => Business,
            "data" => Data,
            "general" => General,
            _ => null
        };
    }

    /// <summary>
    /// Get all available profiles.
    /// </summary>
    public static IReadOnlyList<EntityProfile> All =>
        [Technical, Code, Legal, Business, Data, General];
}

/// <summary>
/// Entity extraction profile defining types and thresholds for a content domain.
/// </summary>
public sealed class EntityProfile
{
    /// <summary>Unique identifier for this profile.</summary>
    public required string ProfileId { get; init; }

    /// <summary>Human-readable name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Entity types to extract with this profile.</summary>
    public required List<EntityTypeDefinition> EntityTypes { get; init; }

    /// <summary>Structural signals that boost confidence (e.g., "inline_code", "heading").</summary>
    public required string[] StructuralSignals { get; init; }

    /// <summary>Minimum IDF threshold for candidate extraction.</summary>
    public double MinIdfThreshold { get; init; } = 3.5;

    /// <summary>Minimum mention count for an entity to be significant.</summary>
    public int MinMentionCount { get; init; } = 3;

    /// <summary>Get the entity type names as a comma-separated list (for LLM prompts).</summary>
    public string TypeListForPrompt => string.Join(", ", EntityTypes.Select(t => t.Name));

    /// <summary>Check if a signal is relevant for this profile.</summary>
    public bool IsRelevantSignal(string signal) =>
        StructuralSignals.Contains(signal, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Definition of an entity type within a profile.
/// </summary>
/// <param name="Name">Primary type name (e.g., "technology").</param>
/// <param name="Description">Human-readable description.</param>
/// <param name="Aliases">Alternative names that map to this type.</param>
public sealed record EntityTypeDefinition(
    string Name,
    string Description,
    string[] Aliases)
{
    /// <summary>
    /// Check if a given type name matches this definition (including aliases).
    /// </summary>
    public bool Matches(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        var lower = typeName.ToLowerInvariant();
        return lower == Name || Aliases.Contains(lower, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalize a type name to the canonical form.
    /// </summary>
    public static string Normalize(string typeName, EntityProfile profile)
    {
        if (string.IsNullOrEmpty(typeName)) return "concept";
        var lower = typeName.ToLowerInvariant();

        foreach (var def in profile.EntityTypes)
        {
            if (def.Matches(lower))
                return def.Name;
        }

        return lower; // Keep as-is if no match
    }
}
