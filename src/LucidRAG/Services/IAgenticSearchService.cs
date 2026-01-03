namespace LucidRAG.Services;

public interface IAgenticSearchService
{
    Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken ct = default);
    Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default);
    IAsyncEnumerable<string> ChatStreamAsync(ChatRequest request, CancellationToken ct = default);
}

public record SearchRequest(
    string Query,
    Guid? CollectionId = null,
    Guid[]? DocumentIds = null,
    int TopK = 10,
    string? SystemPrompt = null);

public record SearchResult(
    List<SearchResultItem> Results,
    int TotalResults,
    long ResponseTimeMs);

public record SearchResultItem(
    Guid DocumentId,
    string DocumentName,
    string SegmentId,
    string Text,
    double Score,
    string? SectionTitle = null);

public record ChatRequest(
    string Query,
    Guid? ConversationId = null,
    Guid? CollectionId = null,
    Guid[]? DocumentIds = null,
    string? SystemPrompt = null);

public record ChatResponse(
    string Answer,
    List<SourceCitation> Sources,
    Guid ConversationId,
    bool AskedForClarification = false,
    string? ClarificationQuestion = null,
    bool IsOffTopic = false);

public record SourceCitation(
    int Number,
    Guid DocumentId,
    string DocumentName,
    string SegmentId,
    string Text,
    string? PageOrSection = null);
