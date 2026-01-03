using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services;
using LucidRAG.Config;
using LucidRAG.Data;

namespace LucidRAG.Services;

public class AgenticSearchService(
    RagDocumentsDbContext db,
    IDocumentSummarizer summarizer,
    IVectorStore vectorStore,
    IEmbeddingService embeddingService,
    IConversationService conversationService,
    IOptions<PromptsConfig> promptsConfig,
    IOptions<DocSummarizerConfig> docSummarizerConfig,
    IOptions<RagDocumentsConfig> ragDocumentsConfig,
    ILogger<AgenticSearchService> logger) : IAgenticSearchService
{
    private readonly PromptsConfig _prompts = promptsConfig.Value;
    private readonly DocSummarizerConfig _docSummarizerConfig = docSummarizerConfig.Value;
    private readonly RagDocumentsConfig _ragConfig = ragDocumentsConfig.Value;

    public async Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // Get documents to search
        var documentIds = request.DocumentIds;
        if (documentIds is null || documentIds.Length == 0)
        {
            var docs = await db.Documents
                .Where(d => d.Status == Entities.DocumentStatus.Completed)
                .Where(d => !request.CollectionId.HasValue || d.CollectionId == request.CollectionId)
                .Select(d => d.Id)
                .ToListAsync(ct);
            documentIds = docs.ToArray();
        }

        if (documentIds.Length == 0)
        {
            return new SearchResult([], 0, sw.ElapsedMilliseconds);
        }

        // Generate query embedding
        var queryEmbedding = await embeddingService.EmbedAsync(request.Query, ct);

        // Search vector store directly
        var collectionName = _docSummarizerConfig.BertRag.CollectionName;
        logger.LogInformation("Searching collection '{Collection}' with query: {Query}", collectionName, request.Query);

        var segments = await vectorStore.SearchAsync(
            collectionName,
            queryEmbedding,
            request.TopK,
            docId: null, // Search all documents
            ct);

        logger.LogInformation("Found {Count} segments", segments.Count);

        var results = segments
            .Select((s, i) => new SearchResultItem(
                DocumentId: Guid.Empty, // Would need to track this via segment metadata
                DocumentName: s.SectionTitle ?? s.HeadingPath ?? "Unknown",
                SegmentId: s.Id,
                Text: s.Text,
                Score: s.RetrievalScore,
                SectionTitle: s.SectionTitle))
            .ToList();

        return new SearchResult(results, results.Count, sw.ElapsedMilliseconds);
    }

    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        // Get or create conversation
        var conversationId = request.ConversationId;
        if (!conversationId.HasValue)
        {
            var conv = await conversationService.CreateConversationAsync(request.CollectionId, ct: ct);
            conversationId = conv.Id;
        }

        // Add user message
        await conversationService.AddMessageAsync(conversationId.Value, "user", request.Query, ct: ct);

        // Build context from conversation history
        var context = await conversationService.BuildContextAsync(conversationId.Value, ct: ct);

        // Get system prompt
        var systemPromptKey = request.SystemPrompt ?? "Default";
        var systemPrompt = _prompts.SystemPrompts.GetValueOrDefault(systemPromptKey, _prompts.SystemPrompts["Default"]);

        // Search for relevant segments
        var searchResult = await SearchAsync(new SearchRequest(
            request.Query,
            request.CollectionId,
            request.DocumentIds), ct);

        // In demo mode, check if query is relevant to indexed documents
        if (_ragConfig.DemoMode.Enabled)
        {
            var hasRelevantResults = searchResult.Results.Any(r => r.Score >= _ragConfig.DemoMode.MinRelevanceScore);
            if (!hasRelevantResults)
            {
                logger.LogInformation("Demo mode: Query '{Query}' appears off-topic (no results above {Threshold} threshold)",
                    request.Query, _ragConfig.DemoMode.MinRelevanceScore);

                var offTopicAnswer = _ragConfig.DemoMode.OffTopicMessage;
                await conversationService.AddMessageAsync(conversationId.Value, "assistant", offTopicAnswer, ct: ct);
                return new ChatResponse(offTopicAnswer, [], conversationId.Value, IsOffTopic: true);
            }
        }

        // Build sources for response
        var sources = searchResult.Results
            .Take(5)
            .Select((r, i) => new SourceCitation(
                Number: i + 1,
                DocumentId: r.DocumentId,
                DocumentName: r.DocumentName,
                SegmentId: r.SegmentId,
                Text: r.Text.Length > 200 ? r.Text[..197] + "..." : r.Text,
                PageOrSection: r.SectionTitle))
            .ToList();

        // Build answer (simplified - in production would use LLM)
        var answer = BuildAnswer(request.Query, sources, systemPrompt);

        // Save assistant message
        var metadata = JsonSerializer.Serialize(new { sources = sources.Select(s => s.SegmentId) });
        await conversationService.AddMessageAsync(conversationId.Value, "assistant", answer, metadata, ct);

        return new ChatResponse(answer, sources, conversationId.Value);
    }

    public async IAsyncEnumerable<string> ChatStreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // For now, just return the full response in chunks
        // In production, this would stream from the LLM
        var response = await ChatAsync(request, ct);

        // Simulate streaming by yielding chunks
        var words = response.Answer.Split(' ');
        foreach (var word in words)
        {
            if (ct.IsCancellationRequested) yield break;
            yield return word + " ";
            await Task.Delay(20, ct); // Simulate typing
        }

        // Yield sources at the end
        if (response.Sources.Count > 0)
        {
            yield return "\n\n**Sources:**\n";
            foreach (var source in response.Sources)
            {
                yield return $"[{source.Number}] {source.DocumentName}";
                if (!string.IsNullOrEmpty(source.PageOrSection))
                    yield return $" ({source.PageOrSection})";
                yield return "\n";
            }
        }
    }

    private string BuildAnswer(string query, List<SourceCitation> sources, string systemPrompt)
    {
        if (sources.Count == 0)
        {
            return "I couldn't find relevant information in the uploaded documents to answer your question. Please try rephrasing or upload more documents.";
        }

        // Simple answer builder - in production would use LLM
        var sourceTexts = string.Join("\n\n", sources.Select(s => $"[{s.Number}]: {s.Text}"));

        return $"""
Based on the documents, here's what I found:

{sources[0].Text}

{(sources.Count > 1 ? $"Additional context from sources [{string.Join(", ", sources.Skip(1).Select(s => s.Number))}] provides more details on this topic." : "")}
""";
    }
}
