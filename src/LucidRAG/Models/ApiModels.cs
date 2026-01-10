namespace LucidRAG.Models;

// ═══════════════════════════════════════════════════════════════════════════
// Collections API
// ═══════════════════════════════════════════════════════════════════════════

public record CreateCollectionRequest(
    string Name,
    string? Description = null,
    string? Settings = null);

public record UpdateCollectionRequest(
    string? Name = null,
    string? Description = null,
    string? Settings = null);

public record AddDocumentsRequest(Guid[] DocumentIds);

public record RemoveDocumentsRequest(Guid[] DocumentIds);

// ═══════════════════════════════════════════════════════════════════════════
// Search API
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Search API request model.
/// </summary>
/// <param name="Query">The search query text</param>
/// <param name="CollectionId">Optional: limit search to a specific collection</param>
/// <param name="DocumentIds">Optional: limit search to specific document IDs</param>
/// <param name="TopK">Number of results to return (default 10)</param>
/// <param name="SystemPrompt">Optional system prompt for answer synthesis</param>
/// <param name="SearchMode">Search mode: "hybrid" (default), "semantic", or "keyword"</param>
public record SearchApiRequest(
    string Query,
    Guid? CollectionId = null,
    Guid[]? DocumentIds = null,
    int? TopK = null,
    string? SystemPrompt = null,
    string? SearchMode = null);

// ═══════════════════════════════════════════════════════════════════════════
// Config API
// ═══════════════════════════════════════════════════════════════════════════

public record ExtractionModeInfo(
    string Value,
    string Label,
    string Description,
    bool Available,
    bool IsDefault);

public record LlmModelInfo(
    string Value,
    string Label,
    string Backend,
    bool IsDefault);

public record CurrentConfig(
    string ExtractionMode,
    string LlmModel,
    bool DemoMode);

public record SetExtractionModeRequest(string Mode);
