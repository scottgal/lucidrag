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
using LucidRAG.Services.Sentinel;

namespace LucidRAG.Services;

public class AgenticSearchService(
    RagDocumentsDbContext db,
    IDocumentSummarizer summarizer,
    IVectorStore vectorStore,
    IEmbeddingService embeddingService,
    IConversationService conversationService,
    ISentinelService sentinelService,
    ILlmService llmService,
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

        // Build schema context for Sentinel
        var schema = await sentinelService.BuildSchemaContextAsync(request.CollectionId, ct);

        // Decompose query using Sentinel
        var options = new SentinelOptions
        {
            CollectionId = request.CollectionId,
            DocumentIds = documentIds,
            ValidateAssumptions = true,
            Mode = _prompts.QueryDecomposition.Enabled ? ExecutionMode.Hybrid : ExecutionMode.Traditional
        };

        var queryPlan = await sentinelService.DecomposeAsync(request.Query, schema, options, ct);

        logger.LogInformation(
            "Sentinel decomposed query into {SubQueryCount} sub-queries (confidence: {Confidence:F2}, mode: {Mode})",
            queryPlan.SubQueries.Count, queryPlan.Confidence, queryPlan.Mode);

        // Execute sub-queries and merge results
        var allResults = new List<SearchResultItem>();
        var collectionName = _docSummarizerConfig.BertRag.CollectionName;

        foreach (var subQuery in queryPlan.SubQueries.OrderBy(sq => sq.Priority))
        {
            logger.LogDebug("Executing sub-query: {Query} (purpose: {Purpose})", subQuery.Query, subQuery.Purpose);

            var queryEmbedding = await embeddingService.EmbedAsync(subQuery.Query, ct);
            var segments = await vectorStore.SearchAsync(
                collectionName,
                queryEmbedding,
                subQuery.TopK,
                docId: null,
                ct);

            var subResults = segments
                .Select((s, i) => new SearchResultItem(
                    DocumentId: Guid.Empty,
                    DocumentName: s.SectionTitle ?? s.HeadingPath ?? "Unknown",
                    SegmentId: s.Id,
                    Text: s.Text,
                    Score: s.QuerySimilarity * (1.0 / subQuery.Priority), // Weight by priority (use QuerySimilarity from vector search)
                    SectionTitle: s.SectionTitle))
                .ToList();

            allResults.AddRange(subResults);
        }

        // Deduplicate and re-rank by aggregated score
        var mergedResults = allResults
            .GroupBy(r => r.SegmentId)
            .Select(g => g.OrderByDescending(r => r.Score).First() with
            {
                Score = g.Sum(r => r.Score) // RRF-like aggregation
            })
            .OrderByDescending(r => r.Score)
            .Take(request.TopK)
            .ToList();

        logger.LogInformation("Found {Count} merged results from {SubQueryCount} sub-queries",
            mergedResults.Count, queryPlan.SubQueries.Count);

        return new SearchResult(mergedResults, mergedResults.Count, sw.ElapsedMilliseconds)
        {
            QueryPlan = queryPlan
        };
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

        // Check if Sentinel needs clarification
        if (searchResult.QueryPlan?.NeedsClarification == true)
        {
            var clarificationQuestion = searchResult.QueryPlan.ClarificationQuestion
                ?? "Could you please clarify your question? I want to make sure I understand what you're looking for.";

            logger.LogInformation("Sentinel requesting clarification for query: {Query}", request.Query);

            await conversationService.AddMessageAsync(conversationId.Value, "assistant", clarificationQuestion, ct: ct);

            return new ChatResponse(
                clarificationQuestion,
                [],
                conversationId.Value,
                AskedForClarification: true,
                ClarificationQuestion: clarificationQuestion);
        }

        // Check if no results and low confidence - also ask for clarification
        if (searchResult.Results.Count == 0 || (searchResult.QueryPlan?.Confidence ?? 1.0) < 0.4)
        {
            var noResultsMessage = "I couldn't find relevant information in the uploaded documents. Could you try:\n" +
                "- Rephrasing your question\n" +
                "- Being more specific about what you're looking for\n" +
                "- Asking about topics covered in the documents";

            logger.LogInformation("Low confidence or no results for query: {Query} (confidence: {Confidence})",
                request.Query, searchResult.QueryPlan?.Confidence);

            await conversationService.AddMessageAsync(conversationId.Value, "assistant", noResultsMessage, ct: ct);

            return new ChatResponse(
                noResultsMessage,
                [],
                conversationId.Value,
                AskedForClarification: true,
                ClarificationQuestion: noResultsMessage);
        }

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

        // Build answer using LLM synthesis
        var answer = await BuildAnswerAsync(request.Query, sources, systemPrompt, ct);

        // Save assistant message
        var metadata = JsonSerializer.Serialize(new { sources = sources.Select(s => s.SegmentId) });
        await conversationService.AddMessageAsync(conversationId.Value, "assistant", answer, metadata, ct);

        // Build decomposition info for UI
        var decomposition = searchResult.QueryPlan != null
            ? new DecompositionInfo(
                Confidence: searchResult.QueryPlan.Confidence,
                SubQueries: searchResult.QueryPlan.SubQueries
                    .Select(sq => new SubQueryInfo(sq.Query, sq.Purpose ?? "", sq.Priority))
                    .ToList(),
                NeedsApproval: searchResult.QueryPlan.Confidence < 0.7)
            : null;

        return new ChatResponse(answer, sources, conversationId.Value)
        {
            QueryPlan = searchResult.QueryPlan,
            Decomposition = decomposition
        };
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

    private async Task<string> BuildAnswerAsync(string query, List<SourceCitation> sources, string systemPrompt, CancellationToken ct)
    {
        if (sources.Count == 0)
        {
            return "I couldn't find relevant information in the uploaded documents to answer your question. Please try rephrasing or upload more documents.";
        }

        // Build context from sources
        var sourceTexts = string.Join("\n\n", sources.Select(s => $"[{s.Number}] ({s.DocumentName}): {s.Text}"));

        // Create prompt for LLM synthesis
        var prompt = $@"{systemPrompt}

You are answering a user's question based on the following document excerpts.
Synthesize a clear, comprehensive answer using the information from the sources.
Cite sources using [N] notation where N is the source number.

USER QUESTION: {query}

SOURCES:
{sourceTexts}

INSTRUCTIONS:
- Provide a direct, helpful answer based on the sources
- Cite relevant sources using [N] notation
- If the sources don't fully answer the question, acknowledge limitations
- Be concise but thorough
- Do not make up information not present in the sources

ANSWER:";

        try
        {
            logger.LogDebug("Generating LLM answer for query: {Query}", query);
            var answer = await llmService.GenerateAsync(prompt, new LlmOptions { Temperature = 0.3 }, ct);
            return answer.Trim();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to generate LLM answer, falling back to simple response");
            // Fallback to simple response if LLM fails
            return $"Based on the documents:\n\n{sources[0].Text}\n\n" +
                   (sources.Count > 1 ? $"Additional context from sources [{string.Join(", ", sources.Skip(1).Select(s => s.Number))}]." : "");
        }
    }
}
