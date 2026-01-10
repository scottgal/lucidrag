using LucidRAG.Authorization;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Models;
using LucidRAG.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LucidRAG.Controllers.Api;

/// <summary>
/// Public API endpoints accessible without authentication.
/// Provides limited read-only access to tenant information.
/// </summary>
[ApiController]
[Route("api/public")]
[AllowAnonymous]
public class PublicController(
    RagDocumentsDbContext db,
    IAgenticSearchService searchService,
    ISalientTermsService? salientTermsService,
    ILogger<PublicController> logger) : ControllerBase
{
    /// <summary>
    /// Gets public tenant statistics (document counts, not actual documents).
    /// </summary>
    [HttpGet("stats")]
    public async Task<Ok<PublicStatsResponse>> GetStats(CancellationToken ct = default)
    {
        var totalDocuments = await db.Documents.CountAsync(ct);
        var completedDocuments = await db.Documents.CountAsync(d => d.Status == DocumentStatus.Completed, ct);
        var totalCollections = await db.Collections.CountAsync(ct);
        var totalEntities = await db.Entities.CountAsync(ct);

        return TypedResults.Ok(new PublicStatsResponse(
            TotalDocuments: totalDocuments,
            CompletedDocuments: completedDocuments,
            TotalCollections: totalCollections,
            TotalEntities: totalEntities,
            LastUpdated: DateTimeOffset.UtcNow
        ));
    }

    /// <summary>
    /// Lists collections with document counts (no document details).
    /// </summary>
    [HttpGet("collections")]
    public async Task<Ok<PagedResponse<PublicCollectionItem>>> ListCollections(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);

        var query = db.Collections.AsNoTracking();
        var total = await query.CountAsync(ct);

        var collections = await query
            .OrderBy(c => c.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new PublicCollectionItem(
                c.Id,
                c.Name,
                c.Description,
                c.Documents.Count,
                c.Documents.Count(d => d.Status == DocumentStatus.Completed)
            ))
            .ToListAsync(ct);

        return TypedResults.Ok(ApiResponseHelpers.Paged(
            collections, page, pageSize, total, "/api/public/collections"));
    }

    /// <summary>
    /// Gets entity type breakdown (without entity details).
    /// </summary>
    [HttpGet("entity-types")]
    public async Task<Ok<List<EntityTypeCount>>> GetEntityTypes(CancellationToken ct = default)
    {
        // Load just entity types and group in memory
        var entityTypes = await db.Entities
            .AsNoTracking()
            .Select(e => e.EntityType)
            .ToListAsync(ct);

        var grouped = entityTypes
            .GroupBy(t => t)
            .Select(g => new EntityTypeCount(g.Key, g.Count()))
            .OrderByDescending(e => e.Count)
            .Take(20)
            .ToList();

        return TypedResults.Ok(grouped);
    }

    /// <summary>
    /// Chat endpoint for public users (read-only, no document access).
    /// </summary>
    [HttpPost("chat")]
    public async Task<Results<Ok<PublicChatResponse>, BadRequest<ApiError>>> Chat(
        [FromBody] PublicChatRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return TypedResults.BadRequest(new ApiError("Message is required", "VALIDATION_ERROR"));
        }

        if (request.Message.Length > 2000)
        {
            return TypedResults.BadRequest(new ApiError("Message too long (max 2000 chars)", "VALIDATION_ERROR"));
        }

        try
        {
            // Use agentic search service for chat
            var chatRequest = new ChatRequest(
                Query: request.Message,
                ConversationId: request.ConversationId,
                CollectionId: request.CollectionId
            );

            var response = await searchService.ChatAsync(chatRequest, ct);

            // Return limited response (no full evidence, just summary)
            return TypedResults.Ok(new PublicChatResponse(
                ConversationId: response.ConversationId,
                Response: response.Answer,
                SourceCount: response.Sources?.Count ?? 0,
                EntityHints: [] // Don't expose entity details in public API
            ));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in public chat");
            return TypedResults.BadRequest(new ApiError("Chat service error", "CHAT_ERROR"));
        }
    }

    /// <summary>
    /// Public search endpoint (returns counts and entity hints, not full documents).
    /// </summary>
    [HttpGet("search")]
    public async Task<Results<Ok<PublicSearchResponse>, BadRequest<ApiError>>> Search(
        [FromQuery] string query,
        [FromQuery] Guid? collectionId = null,
        [FromQuery] int limit = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return TypedResults.BadRequest(new ApiError("Query is required", "VALIDATION_ERROR"));
        }

        try
        {
            // Use search service with limited results
            var searchRequest = new SearchRequest(
                Query: query,
                CollectionId: collectionId,
                TopK: limit
            );

            var results = await searchService.SearchAsync(searchRequest, ct);

            // Return limited info - counts only, no document content
            return TypedResults.Ok(new PublicSearchResponse(
                Query: query,
                ResultCount: results.TotalResults,
                TopEntityTypes: [], // Don't expose entity details in public API
                Relevance: results.Results.Count > 0
                    ? results.Results.Max(r => r.Score)
                    : 0
            ));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in public search");
            return TypedResults.BadRequest(new ApiError("Search service error", "SEARCH_ERROR"));
        }
    }

    /// <summary>
    /// Get autocomplete suggestions for a query prefix within a collection.
    /// Returns pre-computed salient terms from TF-IDF and entity analysis.
    /// </summary>
    [HttpGet("autocomplete")]
    public async Task<Results<Ok<List<AutocompleteSuggestion>>, BadRequest<ApiError>>> Autocomplete(
        [FromQuery] string query,
        [FromQuery] Guid? collectionId = null,
        [FromQuery] int limit = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
        {
            return TypedResults.BadRequest(new ApiError("Query must be at least 2 characters", "VALIDATION_ERROR"));
        }

        if (salientTermsService == null)
        {
            // Service not available, return empty suggestions
            return TypedResults.Ok(new List<AutocompleteSuggestion>());
        }

        try
        {
            // If no collection specified, try to get default collection
            if (collectionId == null)
            {
                var defaultCollection = await db.Collections
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.IsDefault, ct);

                if (defaultCollection != null)
                {
                    collectionId = defaultCollection.Id;
                }
            }

            if (collectionId == null)
            {
                // No collection available, return empty
                return TypedResults.Ok(new List<AutocompleteSuggestion>());
            }

            var suggestions = await salientTermsService.GetAutocompleteSuggestionsAsync(
                collectionId.Value,
                query,
                limit,
                ct);

            var response = suggestions
                .Select(s => new AutocompleteSuggestion(
                    s.Term,
                    s.Score,
                    s.Source))
                .ToList();

            return TypedResults.Ok(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in autocomplete");
            return TypedResults.BadRequest(new ApiError("Autocomplete service error", "AUTOCOMPLETE_ERROR"));
        }
    }
}

// DTOs for public API
public record PublicStatsResponse(
    int TotalDocuments,
    int CompletedDocuments,
    int TotalCollections,
    int TotalEntities,
    DateTimeOffset LastUpdated
);

public record PublicCollectionItem(
    Guid Id,
    string Name,
    string? Description,
    int TotalDocuments,
    int CompletedDocuments
);

public record EntityTypeCount(string Type, int Count);

public record PublicChatRequest(
    string Message,
    Guid? ConversationId = null,
    Guid? CollectionId = null
);

public record PublicChatResponse(
    Guid ConversationId,
    string Response,
    int SourceCount,
    List<string> EntityHints
);

public record PublicSearchResponse(
    string Query,
    int ResultCount,
    List<EntityTypeCount> TopEntityTypes,
    double Relevance
);

public record AutocompleteSuggestion(
    string Term,
    double Score,
    string Source
);
