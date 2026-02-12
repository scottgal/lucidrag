using System.Text.Json;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Middleware;
using LucidRAG.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LucidRAG.Controllers.Api;

[ApiController]
[Route("api/saas")]
public class SaasSearchController(
    IAgenticSearchService searchService,
    ISalientTermsService salientTermsService,
    RagDocumentsDbContext db,
    ILogger<SaasSearchController> logger) : ControllerBase
{
    // --- API-Key-Protected Endpoints ---

    [HttpPost("search")]
    public async Task<IActionResult> Search([FromBody] SaasSearchRequest request, CancellationToken ct)
    {
        var apiKey = HttpContext.GetApiKey();
        if (apiKey is null) return Unauthorized(new { error = "API key required" });
        if (!apiKey.AllowSearch) return Forbid();

        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(new { error = "Query is required" });

        var searchMode = request.SearchMode?.ToLowerInvariant() switch
        {
            "semantic" => SearchMode.Semantic,
            "keyword" => SearchMode.Keyword,
            _ => SearchMode.Hybrid
        };

        var searchRequest = new SearchRequest(
            request.Query,
            apiKey.CollectionId,
            TopK: request.TopK ?? 10,
            SearchMode: searchMode);

        var result = await searchService.SearchAsync(searchRequest, ct);

        return Ok(new
        {
            results = result.Results.Select(r => new
            {
                title = r.DocumentName,
                text = r.Text,
                score = r.Score,
                pageOrSection = r.SectionTitle
            }),
            totalResults = result.TotalResults,
            responseTimeMs = result.ResponseTimeMs
        });
    }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] SaasChatRequest request, CancellationToken ct)
    {
        var apiKey = HttpContext.GetApiKey();
        if (apiKey is null) return Unauthorized(new { error = "API key required" });
        if (!apiKey.AllowChat) return Forbid();

        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(new { error = "Query is required" });

        var chatRequest = new ChatRequest(
            request.Query,
            request.ConversationId,
            apiKey.CollectionId);

        var response = await searchService.ChatAsync(chatRequest, ct);

        return Ok(new
        {
            answer = response.Answer,
            sources = response.Sources.Select(s => new
            {
                number = s.Number,
                documentName = s.DocumentName,
                text = s.Text,
                pageOrSection = s.PageOrSection
            }),
            conversationId = response.ConversationId
        });
    }

    [HttpPost("chat/stream")]
    public async Task ChatStream([FromBody] SaasChatRequest request, CancellationToken ct)
    {
        var apiKey = HttpContext.GetApiKey();
        if (apiKey is null)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!apiKey.AllowChat)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        var chatRequest = new ChatRequest(
            request.Query,
            request.ConversationId,
            apiKey.CollectionId);

        await foreach (var chunk in searchService.ChatStreamWithSourcesAsync(chatRequest, ct))
        {
            var data = JsonSerializer.Serialize(new
            {
                type = chunk.Type,
                text = chunk.Text,
                sources = chunk.Sources?.Select(s => new
                {
                    number = s.Number,
                    documentName = s.DocumentName,
                    text = s.Text,
                    pageOrSection = s.PageOrSection
                }),
                conversationId = chunk.ConversationId
            });
            await Response.WriteAsync($"data: {data}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        await Response.WriteAsync("data: [DONE]\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    [HttpGet("autocomplete")]
    public async Task<IActionResult> Autocomplete([FromQuery] string q, [FromQuery] int limit = 5,
        CancellationToken ct = default)
    {
        var apiKey = HttpContext.GetApiKey();
        if (apiKey is null) return Unauthorized(new { error = "API key required" });
        if (apiKey.CollectionId is null) return Ok(new { suggestions = Array.Empty<string>() });

        var suggestions = await salientTermsService.GetAutocompleteSuggestionsAsync(
            apiKey.CollectionId.Value, q, limit, ct);

        return Ok(new
        {
            suggestions = suggestions.Select(s => s.Term)
        });
    }

    // --- Widget Config ---

    [HttpGet("widget/config")]
    public async Task<IActionResult> WidgetConfig(CancellationToken ct)
    {
        var apiKey = HttpContext.GetApiKey();
        if (apiKey is null) return Unauthorized(new { error = "API key required" });

        return Ok(new
        {
            allowSearch = apiKey.AllowSearch,
            allowChat = apiKey.AllowChat,
            plan = apiKey.Plan,
            hasDocuments = apiKey.CollectionId is not null &&
                           await db.Documents.AnyAsync(d => d.CollectionId == apiKey.CollectionId, ct)
        });
    }

    // --- Public Corpus Endpoints (no API key required) ---

    [HttpPost("public/search")]
    public async Task<IActionResult> PublicSearch([FromBody] SaasSearchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(new { error = "Query is required" });

        // Find the default public collection
        var publicCollection = await db.Collections
            .FirstOrDefaultAsync(c => c.IsDefault, ct);

        if (publicCollection is null)
            return Ok(new { results = Array.Empty<object>(), totalResults = 0, responseTimeMs = 0 });

        var searchMode = request.SearchMode?.ToLowerInvariant() switch
        {
            "semantic" => SearchMode.Semantic,
            "keyword" => SearchMode.Keyword,
            _ => SearchMode.Hybrid
        };

        var searchRequest = new SearchRequest(
            request.Query,
            publicCollection.Id,
            TopK: request.TopK ?? 10,
            SearchMode: searchMode);

        var result = await searchService.SearchAsync(searchRequest, ct);

        return Ok(new
        {
            results = result.Results.Select(r => new
            {
                title = r.DocumentName,
                text = r.Text,
                score = r.Score,
                pageOrSection = r.SectionTitle
            }),
            totalResults = result.TotalResults,
            responseTimeMs = result.ResponseTimeMs
        });
    }

    [HttpPost("public/chat")]
    public async Task<IActionResult> PublicChat([FromBody] SaasChatRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(new { error = "Query is required" });

        var publicCollection = await db.Collections
            .FirstOrDefaultAsync(c => c.IsDefault, ct);

        if (publicCollection is null)
            return BadRequest(new { error = "No public corpus available" });

        var chatRequest = new ChatRequest(
            request.Query,
            request.ConversationId,
            publicCollection.Id);

        var response = await searchService.ChatAsync(chatRequest, ct);

        return Ok(new
        {
            answer = response.Answer,
            sources = response.Sources.Select(s => new
            {
                number = s.Number,
                documentName = s.DocumentName,
                text = s.Text,
                pageOrSection = s.PageOrSection
            }),
            conversationId = response.ConversationId
        });
    }
}

public record SaasSearchRequest(string Query, int? TopK = null, string? SearchMode = null);
public record SaasChatRequest(string Query, Guid? ConversationId = null);
