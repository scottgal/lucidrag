using Microsoft.AspNetCore.Mvc;
using LucidRAG.Models;
using LucidRAG.Services;

namespace LucidRAG.Controllers.Api;

/// <summary>
/// Standalone search API for programmatic access without conversation memory.
/// Use this for single queries where you don't need chat history.
/// For conversational use cases, see /api/chat.
/// </summary>
[ApiController]
[Route("api/search")]
public class SearchController(
    IAgenticSearchService searchService,
    ILogger<SearchController> logger) : ControllerBase
{
    /// <summary>
    /// Search documents with hybrid retrieval (BM25 + BERT).
    /// Returns matching segments without LLM synthesis.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Search([FromBody] SearchApiRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return BadRequest(new { error = "Query is required" });
        }

        try
        {
            // Parse search mode from string (default to Hybrid)
            var searchMode = request.SearchMode?.ToLowerInvariant() switch
            {
                "semantic" => Services.SearchMode.Semantic,
                "keyword" => Services.SearchMode.Keyword,
                _ => Services.SearchMode.Hybrid
            };

            var searchRequest = new SearchRequest(
                Query: request.Query,
                CollectionId: request.CollectionId,
                DocumentIds: request.DocumentIds,
                TopK: request.TopK ?? 10,
                SearchMode: searchMode);

            var result = await searchService.SearchAsync(searchRequest, ct);

            return Ok(new
            {
                query = request.Query,
                searchMode = searchMode.ToString().ToLowerInvariant(),
                results = result.Results.Select(r => new
                {
                    documentId = r.DocumentId,
                    documentName = r.DocumentName,
                    segmentId = r.SegmentId,
                    text = r.Text,
                    score = r.Score,
                    sectionTitle = r.SectionTitle
                }),
                totalResults = result.TotalResults,
                responseTimeMs = result.ResponseTimeMs
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing search request");
            return StatusCode(500, new { error = "Failed to process search request" });
        }
    }

    /// <summary>
    /// Search with LLM-synthesized answer (single query, no conversation memory).
    /// Similar to /api/chat but stateless - no conversation is created or maintained.
    /// </summary>
    [HttpPost("answer")]
    public async Task<IActionResult> SearchWithAnswer([FromBody] SearchApiRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return BadRequest(new { error = "Query is required" });
        }

        try
        {
            // Create a chat request without conversation ID to get a one-shot answer
            var chatRequest = new ChatRequest(
                Query: request.Query,
                ConversationId: null, // No conversation memory
                CollectionId: request.CollectionId,
                DocumentIds: request.DocumentIds,
                SystemPrompt: request.SystemPrompt);

            var response = await searchService.ChatAsync(chatRequest, ct);

            return Ok(new
            {
                query = request.Query,
                answer = response.Answer,
                sources = response.Sources.Select(s => new
                {
                    number = s.Number,
                    documentId = s.DocumentId,
                    documentName = s.DocumentName,
                    text = s.Text,
                    pageOrSection = s.PageOrSection
                })
                // Note: conversationId is intentionally omitted for stateless search
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing search with answer request");
            return StatusCode(500, new { error = "Failed to process search request" });
        }
    }
}
