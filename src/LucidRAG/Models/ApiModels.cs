namespace LucidRAG.Models;

// ═══════════════════════════════════════════════════════════════════════════
// Standard API Response Envelopes
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Standard API response envelope for single items.
/// </summary>
public record ApiResponse<T>(
    T Data,
    ApiMeta? Meta = null,
    Dictionary<string, string>? Links = null);

/// <summary>
/// Standard API response envelope for paginated lists.
/// </summary>
public record PagedResponse<T>(
    IEnumerable<T> Data,
    PaginationMeta Pagination,
    Dictionary<string, string>? Links = null);

/// <summary>
/// Standard API error response.
/// </summary>
public record ApiError(
    string Error,
    string? Code = null,
    Dictionary<string, string[]>? Validation = null);

/// <summary>
/// Metadata for API responses.
/// </summary>
public record ApiMeta(
    DateTimeOffset Timestamp,
    double? DurationMs = null,
    string? RequestId = null);

/// <summary>
/// Pagination metadata for list responses.
/// </summary>
public record PaginationMeta(
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages)
{
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;
}

/// <summary>
/// Standard async job response for long-running operations.
/// </summary>
public record JobResponse(
    Guid JobId,
    string Status,
    string? Message = null,
    string? StatusUrl = null);

/// <summary>
/// API response helpers for controllers.
/// </summary>
public static class ApiResponseHelpers
{
    public static ApiResponse<T> Success<T>(T data, double? durationMs = null)
        => new(data, new ApiMeta(DateTimeOffset.UtcNow, durationMs));

    public static PagedResponse<T> Paged<T>(
        IEnumerable<T> items,
        int page,
        int pageSize,
        int totalItems,
        string? baseUrl = null)
    {
        var totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
        var pagination = new PaginationMeta(page, pageSize, totalItems, totalPages);

        Dictionary<string, string>? links = null;
        if (!string.IsNullOrEmpty(baseUrl))
        {
            links = new Dictionary<string, string>
            {
                ["self"] = $"{baseUrl}?page={page}&pageSize={pageSize}"
            };
            if (pagination.HasPrevious)
                links["prev"] = $"{baseUrl}?page={page - 1}&pageSize={pageSize}";
            if (pagination.HasNext)
                links["next"] = $"{baseUrl}?page={page + 1}&pageSize={pageSize}";
            links["first"] = $"{baseUrl}?page=1&pageSize={pageSize}";
            links["last"] = $"{baseUrl}?page={totalPages}&pageSize={pageSize}";
        }

        return new PagedResponse<T>(items, pagination, links);
    }

    public static JobResponse Job(Guid jobId, string status, string? message = null, string? baseUrl = null)
        => new(jobId, status, message, baseUrl != null ? $"{baseUrl}/{jobId}" : null);
}

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

// ═══════════════════════════════════════════════════════════════════════════
// Document API Response Models (for TypedResults)
// ═══════════════════════════════════════════════════════════════════════════

public record DemoStatusResponse(
    bool DemoMode,
    string? Message,
    bool UploadsEnabled);

public record DocumentUploadResponse(
    Guid DocumentId,
    string Filename,
    string Status,
    string Message);

public record DocumentResponse(
    Guid Id,
    string Name,
    string? OriginalFilename,
    string Status,
    string? StatusMessage,
    float Progress,
    int SegmentCount,
    int EntityCount,
    long? FileSizeBytes,
    string? MimeType,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt,
    Guid? CollectionId,
    string? CollectionName,
    string? SourceUrl);

public record DocumentListItem(
    Guid Id,
    string Name,
    string? OriginalFilename,
    string Status,
    string? StatusMessage,
    float Progress,
    int SegmentCount,
    DateTimeOffset CreatedAt,
    Guid? CollectionId,
    string? CollectionName,
    string? SourceUrl);

public record BatchUploadResult(
    Guid? DocumentId,
    string Filename,
    string Status,
    string? Error);

public record BatchUploadResponse(
    List<BatchUploadResult> Documents);

public record ImportResponse(
    Guid DocumentId,
    string Filename,
    string SourcePath,
    string Action,
    int Version,
    string Message);

public record BatchImportSummary(int Created, int Updated, int Unchanged, int Total);

public record BatchImportResult(
    Guid? DocumentId,
    string Filename,
    string Action,
    string? Error);

public record BatchImportResponse(
    BatchImportSummary Summary,
    List<BatchImportResult> Documents);

public record DeleteResponse(bool Success, int? Deleted = null, string? Message = null);

public record BulkDeleteResponse(int Deleted, bool VectorsCleared, string Message);

// ═══════════════════════════════════════════════════════════════════════════
// Collection API Response Models
// ═══════════════════════════════════════════════════════════════════════════

public record CollectionResponse(
    Guid Id,
    string Name,
    string? Description,
    string? Settings,
    int DocumentCount,
    int EntityCount,
    int SegmentCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public record CollectionListItem(
    Guid Id,
    string Name,
    string? Description,
    int DocumentCount,
    int CompletedCount,
    int ProcessingCount,
    int PendingCount,
    int FailedCount,
    int EntityCount,
    int SegmentCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// ═══════════════════════════════════════════════════════════════════════════
// Community API Response Models
// ═══════════════════════════════════════════════════════════════════════════

public record CommunityDetectionResponse(
    int CommunitiesDetected,
    int EntitiesAssigned,
    double Modularity,
    double DurationSeconds,
    ApiMeta Meta);

public record CommunitySummaryResponse(
    int Summarized,
    ApiMeta Meta);

public record CommunityListItem(
    Guid Id,
    string? Name,
    string? Summary,
    string Algorithm,
    int Level,
    int EntityCount,
    double Cohesion,
    Guid? ParentCommunityId,
    DateTimeOffset CreatedAt);

// ═══════════════════════════════════════════════════════════════════════════
// Graph API Response Models
// ═══════════════════════════════════════════════════════════════════════════

public record GraphStatsResponse(
    int Nodes,
    int Edges,
    string EntityCoverage,
    int Orphans,
    int Documents,
    int DocumentsWithEntities);

public record GraphNodeResponse(
    Guid Id,
    string Name,
    string? Type,
    string? Description,
    string[]? Aliases,
    int MentionCount,
    bool IsCenter = false);

public record GraphLinkResponse(
    Guid Source,
    Guid Target,
    string Type,
    float Strength);

public record GraphResponse(
    List<GraphNodeResponse> Nodes,
    List<GraphLinkResponse> Links);

public record SubgraphResponse(
    string Center,
    List<GraphNodeResponse> Nodes,
    List<GraphLinkResponse> Links);
