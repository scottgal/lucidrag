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

public record SearchApiRequest(
    string Query,
    Guid? CollectionId = null,
    Guid[]? DocumentIds = null,
    int? TopK = null,
    string? SystemPrompt = null);

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
