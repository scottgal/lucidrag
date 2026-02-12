using System.Text.Json;
using LucidRAG.Filters;
using LucidRAG.Services;
using Microsoft.AspNetCore.Mvc;

namespace LucidRAG.Controllers.Api;

[ApiController]
[Route("api/chat")]
public class ChatController(
    IAgenticSearchService searchService,
    IConversationService conversationService,
    IPersonalMemoryService personalMemory,
    ILogger<ChatController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Chat([FromBody] ChatApiRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query)) return BadRequest(new { error = "Query is required" });

        try
        {
            // Parse search mode from string (default to Hybrid)
            var searchMode = request.SearchMode?.ToLowerInvariant() switch
            {
                "semantic" => SearchMode.Semantic,
                "keyword" => SearchMode.Keyword,
                _ => SearchMode.Hybrid
            };

            var chatRequest = new ChatRequest(
                request.Query,
                request.ConversationId,
                request.CollectionId,
                request.DocumentIds,
                request.SystemPrompt,
                searchMode);

            var response = await searchService.ChatAsync(chatRequest, ct);

            return Ok(new
            {
                answer = response.Answer,
                sources = response.Sources.Select(s => new
                {
                    number = s.Number,
                    documentId = s.DocumentId,
                    documentName = s.DocumentName,
                    text = s.Text,
                    pageOrSection = s.PageOrSection
                }),
                conversationId = response.ConversationId,
                askedForClarification = response.AskedForClarification,
                clarificationQuestion = response.ClarificationQuestion,
                isOffTopic = response.IsOffTopic,
                timestamp = response.Timestamp,
                // Query decomposition for UI display
                decomposition = response.Decomposition != null
                    ? new
                    {
                        confidence = response.Decomposition.Confidence,
                        needsApproval = response.Decomposition.NeedsApproval,
                        subQueries = response.Decomposition.SubQueries.Select(sq => new
                        {
                            query = sq.Query,
                            purpose = sq.Purpose,
                            priority = sq.Priority
                        })
                    }
                    : null,
                // Collapsible thinking note with search metrics
                thinkingNote = response.ThinkingNote,
                searchTimeMs = response.SearchTimeMs,
                segmentCount = response.SegmentCount,
                // Lens info
                lensId = response.LensId
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing chat request");
            return StatusCode(500, new { error = "Failed to process chat request" });
        }
    }

    [HttpPost("stream")]
    public async Task ChatStream([FromBody] ChatApiRequest request, CancellationToken ct = default)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        // Parse search mode from string (default to Hybrid)
        var searchMode = request.SearchMode?.ToLowerInvariant() switch
        {
            "semantic" => SearchMode.Semantic,
            "keyword" => SearchMode.Keyword,
            _ => SearchMode.Hybrid
        };

        var chatRequest = new ChatRequest(
            request.Query,
            request.ConversationId,
            request.CollectionId,
            request.DocumentIds,
            request.SystemPrompt,
            searchMode);

        await foreach (var chunk in searchService.ChatStreamAsync(chatRequest, ct))
        {
            var data = JsonSerializer.Serialize(new { text = chunk });
            await Response.WriteAsync($"data: {data}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        await Response.WriteAsync("data: [DONE]\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    [HttpPost("stream-with-sources")]
    public async Task ChatStreamWithSources([FromBody] ChatApiRequest request, CancellationToken ct = default)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        var searchMode = request.SearchMode?.ToLowerInvariant() switch
        {
            "semantic" => SearchMode.Semantic, "keyword" => SearchMode.Keyword, _ => SearchMode.Hybrid
        };
        var chatRequest = new ChatRequest(request.Query, request.ConversationId, request.CollectionId,
            request.DocumentIds, request.SystemPrompt, searchMode);
        await foreach (var chunk in searchService.ChatStreamWithSourcesAsync(chatRequest, ct))
        {
            var data = JsonSerializer.Serialize(new
            {
                type = chunk.Type, text = chunk.Text,
                sources = chunk.Sources?.Select(s => new
                {
                    number = s.Number, documentId = s.DocumentId, documentName = s.DocumentName, text = s.Text,
                    pageOrSection = s.PageOrSection
                }),
                conversationId = chunk.ConversationId, thinkingNote = chunk.ThinkingNote,
                searchTimeMs = chunk.SearchTimeMs, segmentCount = chunk.SegmentCount
            });
            await Response.WriteAsync($"data: {data}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        await Response.WriteAsync("data: [DONE]\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }


    [HttpGet("conversations")]
    public async Task<IActionResult> GetConversations([FromQuery] Guid? collectionId = null,
        CancellationToken ct = default)
    {
        var conversations = await conversationService.GetConversationsAsync(collectionId, ct);

        return Ok(new
        {
            conversations = conversations.Select(c => new
            {
                id = c.Id,
                title = c.Title,
                collectionId = c.CollectionId,
                collectionName = c.Collection?.Name,
                messageCount = c.Messages.Count,
                createdAt = c.CreatedAt,
                updatedAt = c.UpdatedAt
            })
        });
    }

    [HttpGet("conversations/{id:guid}")]
    public async Task<IActionResult> GetConversation(Guid id, CancellationToken ct = default)
    {
        var conversation = await conversationService.GetConversationAsync(id, ct);
        if (conversation is null) return NotFound(new { error = "Conversation not found" });

        return Ok(new
        {
            id = conversation.Id,
            title = conversation.Title,
            collectionId = conversation.CollectionId,
            messages = conversation.Messages.Select(m => new
            {
                id = m.Id,
                role = m.Role,
                content = m.Content,
                createdAt = m.CreatedAt
            }),
            createdAt = conversation.CreatedAt,
            updatedAt = conversation.UpdatedAt
        });
    }

    [HttpDelete("conversations/{id:guid}")]
    [DemoModeWriteBlock(Operation = "Conversation deletion")]
    public async Task<IActionResult> DeleteConversation(Guid id, CancellationToken ct = default)
    {
        await conversationService.DeleteConversationAsync(id, ct);
        return Ok(new { success = true });
    }

    // --- Personal Memory Endpoints ---

    [HttpPost("personal/remember")]
    public async Task<IActionResult> RememberFact([FromBody] RememberRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Fact)) return BadRequest(new { error = "Fact is required" });

        var userId = request.UserId ?? "default";
        var fact = await personalMemory.RememberAsync(userId, request.Fact, ct);

        return Ok(new { id = fact.Id, fact = fact.Fact, createdAt = fact.CreatedAt });
    }

    [HttpGet("personal/facts")]
    public async Task<IActionResult> GetFacts([FromQuery] string? userId = null, CancellationToken ct = default)
    {
        var facts = await personalMemory.GetFactsAsync(userId ?? "default", ct);
        return Ok(new
        {
            facts = facts.Select(f => new { id = f.Id, fact = f.Fact, createdAt = f.CreatedAt })
        });
    }

    [HttpDelete("personal/facts/{id:guid}")]
    public async Task<IActionResult> ForgetFact(Guid id, [FromQuery] string? userId = null,
        CancellationToken ct = default)
    {
        var deleted = await personalMemory.ForgetAsync(userId ?? "default", id, ct);
        if (!deleted) return NotFound(new { error = "Fact not found" });

        return Ok(new { success = true });
    }
}

public record RememberRequest(string Fact, string? UserId = null);

public record ChatApiRequest(
    string Query,
    Guid? ConversationId = null,
    Guid? CollectionId = null,
    Guid[]? DocumentIds = null,
    string? SystemPrompt = null,
    string? SearchMode = null); // "semantic", "keyword", or "hybrid" (default)