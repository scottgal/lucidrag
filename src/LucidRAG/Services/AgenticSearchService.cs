using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    SynthesisCacheService synthesisCache,
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

        // Build lookup for VectorStoreDocId -> DocumentEntity (handle duplicates by taking most recent)
        var documentLookup = await db.Documents
            .Where(d => d.VectorStoreDocId != null)
            .GroupBy(d => d.VectorStoreDocId!)
            .Select(g => g.OrderByDescending(d => d.CreatedAt).First())
            .ToDictionaryAsync(d => d.VectorStoreDocId!, d => d, ct);

        foreach (var subQuery in queryPlan.SubQueries.OrderBy(sq => sq.Priority))
        {
            logger.LogDebug("Executing sub-query: {Query} (purpose: {Purpose})", subQuery.Query, subQuery.Purpose);

            var queryEmbedding = await embeddingService.EmbedAsync(subQuery.Query, ct);

            // Debug: Log embedding info
            logger.LogDebug("Query embedding dimension: {Dim}, first values: [{V0:F4}, {V1:F4}, {V2:F4}]",
                queryEmbedding.Length,
                queryEmbedding.Length > 0 ? queryEmbedding[0] : 0,
                queryEmbedding.Length > 1 ? queryEmbedding[1] : 0,
                queryEmbedding.Length > 2 ? queryEmbedding[2] : 0);

            var segments = await vectorStore.SearchAsync(
                collectionName,
                queryEmbedding,
                subQuery.TopK,
                docId: null,
                ct);

            // Debug: Log search results
            if (segments.Count > 0)
            {
                logger.LogDebug("Search returned {Count} segments. Top result: '{Text}' (score: {Score:F4})",
                    segments.Count,
                    segments[0].Text?.Substring(0, Math.Min(50, segments[0].Text?.Length ?? 0)),
                    segments[0].QuerySimilarity);
            }

            var subResults = segments
                .Select((s, i) =>
                {
                    // Extract docId from segment ID (format: {docId}_{type}_{index})
                    var segmentDocId = ExtractDocIdFromSegmentId(s.Id);
                    var doc = segmentDocId != null && documentLookup.TryGetValue(segmentDocId, out var d) ? d : null;

                    return new SearchResultItem(
                        DocumentId: doc?.Id ?? Guid.Empty,
                        DocumentName: doc?.Name ?? s.SectionTitle ?? s.HeadingPath ?? "Unknown",
                        SegmentId: s.Id,
                        Text: s.Text,
                        Score: s.QuerySimilarity * (1.0 / subQuery.Priority), // Weight by priority
                        SectionTitle: s.SectionTitle);
                })
                .ToList();

            allResults.AddRange(subResults);
        }

        // Extract significant keywords from query for boosting (ignore common stopwords)
        var queryKeywords = ExtractSignificantKeywords(request.Query);
        logger.LogInformation("Search mode: {Mode}, extracted keywords: [{Keywords}] from query '{Query}'",
            request.SearchMode, string.Join(", ", queryKeywords), request.Query);

        // Deduplicate and re-rank based on search mode
        var mergedResults = allResults
            .GroupBy(r => r.SegmentId)
            .Select(g =>
            {
                var best = g.OrderByDescending(r => r.Score).First();
                var baseScore = g.Sum(r => r.Score); // RRF-like aggregation

                // Pure semantic mode - no keyword boosting
                if (request.SearchMode == SearchMode.Semantic)
                {
                    return best with { Score = baseScore };
                }

                // Calculate keyword matches for hybrid/keyword modes
                var text = best.Text?.ToLowerInvariant() ?? "";
                var docName = best.DocumentName?.ToLowerInvariant() ?? "";
                var sectionTitle = best.SectionTitle?.ToLowerInvariant() ?? "";

                // Count matches in text, document name, and section title
                var textMatches = queryKeywords.Count(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase));
                var docNameMatches = queryKeywords.Count(kw => docName.Contains(kw, StringComparison.OrdinalIgnoreCase));
                var sectionMatches = queryKeywords.Count(kw => sectionTitle.Contains(kw, StringComparison.OrdinalIgnoreCase));
                var totalMatches = textMatches + docNameMatches + sectionMatches;

                double finalScore;

                if (request.SearchMode == SearchMode.Keyword)
                {
                    // Keyword mode: HEAVY boost for matches, severe penalty for no matches
                    // This prioritizes exact keyword matches over semantic similarity
                    var keywordBoost = (docNameMatches * 1.5) + (textMatches * 1.0) + (sectionMatches * 0.5);
                    var noMatchPenalty = (totalMatches == 0 && queryKeywords.Count > 0) ? -0.5 : 0.0;
                    finalScore = baseScore + keywordBoost + noMatchPenalty;
                }
                else // Hybrid mode (default)
                {
                    // Hybrid: balanced boost + mild penalty for completely irrelevant results
                    var keywordBoost = (docNameMatches * 0.5) + (textMatches * 0.3) + (sectionMatches * 0.2);
                    // Penalty for results with NO keyword matches when we have keywords to match
                    var noMatchPenalty = (totalMatches == 0 && queryKeywords.Count > 0) ? -0.2 : 0.0;
                    finalScore = baseScore + keywordBoost + noMatchPenalty;
                }

                return best with { Score = finalScore };
            })
            .OrderByDescending(r => r.Score)
            .Take(request.TopK)
            .ToList();

        // Log top results after boosting for debugging
        if (mergedResults.Count > 0)
        {
            logger.LogInformation("Top 3 results after {Mode} ranking:", request.SearchMode);
            foreach (var r in mergedResults.Take(3))
            {
                logger.LogInformation("  [{Score:F4}] {DocName}: {Text}",
                    r.Score, r.DocumentName, r.Text?.Substring(0, Math.Min(50, r.Text?.Length ?? 0)));
            }
        }

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

        // Build sources for response - filter by minimum relevance score
        const double minRelevanceScore = 0.4; // Minimum cosine similarity to include as evidence
        var relevantResults = searchResult.Results
            .Where(r => r.Score >= minRelevanceScore)
            .Take(5)
            .ToList();

        // Log if we filtered out low-relevance results
        if (searchResult.Results.Count > relevantResults.Count)
        {
            logger.LogDebug("Filtered {Removed} low-relevance results (below {Threshold})",
                searchResult.Results.Count - relevantResults.Count, minRelevanceScore);
        }

        var sources = relevantResults
            .Select((r, i) => new SourceCitation(
                Number: i + 1,
                DocumentId: r.DocumentId,
                DocumentName: r.DocumentName,
                SegmentId: r.SegmentId,
                Text: r.Text.Length > 200 ? r.Text[..197] + "..." : r.Text,
                PageOrSection: r.SectionTitle))
            .ToList();

        // Build thinking/transparency output
        var thinking = BuildThinkingOutput(request.Query, searchResult);

        // Check if this is a keyword query - skip synthesis
        var queryType = searchResult.QueryPlan?.QueryType ?? Sentinel.QueryType.Semantic;
        string answer;

        if (queryType == Sentinel.QueryType.Keyword || queryType == Sentinel.QueryType.Navigation)
        {
            // Keyword/navigation query - just list matching documents without synthesis
            logger.LogInformation("Query '{Query}' is {Type} - skipping synthesis", request.Query, queryType);
            answer = BuildKeywordResponse(sources, thinking);
        }
        else
        {
            // Semantic/comparison/aggregation - use LLM synthesis
            answer = await BuildAnswerAsync(request.Query, sources, systemPrompt, ct);
            answer = thinking + "\n\n" + answer;
        }

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

        // Create prompt for LLM synthesis - with strict anti-leak rules
        var prompt = $@"{systemPrompt}

Answer the question using ONLY the evidence below.

QUESTION: {query}

EVIDENCE:
{sourceTexts}

RULES:
1. If evidence answers the question: explain clearly with [N] citations after each point
2. If evidence is NOT relevant: say ""I don't have specific information about [topic] in the documents.""
3. Write complete sentences - never output just citation numbers alone
4. NO meta phrases like ""based on sources"" or ""the documents show""
5. NO system terms: models, pipelines, embeddings, vectors

ANSWER:";

        try
        {
            // Compute evidence hash and get source document IDs
            var evidenceHash = SynthesisCacheService.ComputeHash(sourceTexts);
            var sourceDocumentIds = sources.Select(s => s.DocumentId).Distinct().ToArray();

            // Check synthesis cache first
            if (synthesisCache.TryGetSynthesis(query, evidenceHash, out var cachedAnswer))
            {
                logger.LogDebug("Returning cached synthesis for query: {Query}", query);
                return cachedAnswer!;
            }

            logger.LogDebug("Generating LLM answer for query: {Query}", query);
            var answer = await llmService.GenerateAsync(prompt, new LlmOptions { Temperature = 0.3 }, ct);
            var trimmedAnswer = answer.Trim();

            // Store in cache with document IDs for invalidation tracking
            synthesisCache.SetSynthesis(query, sourceTexts, trimmedAnswer, sourceDocumentIds);

            return trimmedAnswer;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to generate LLM answer, falling back to simple response");
            // Fallback to simple response if LLM fails
            return $"Based on the documents:\n\n{sources[0].Text}\n\n" +
                   (sources.Count > 1 ? $"Additional context from sources [{string.Join(", ", sources.Skip(1).Select(s => s.Number))}]." : "");
        }
    }

    /// <summary>
    /// Extract docId from segment ID (format: {docId}_{type}_{index})
    /// Example: "10_da69a3ca5838716d_s_42" -> "10_da69a3ca5838716d"
    /// </summary>
    private static string? ExtractDocIdFromSegmentId(string segmentId)
    {
        var parts = segmentId.Split('_');
        if (parts.Length >= 3)
        {
            // Take all parts except the last two (type and index)
            return string.Join("_", parts.Take(parts.Length - 2));
        }
        return null;
    }

    /// <summary>
    /// Extract significant keywords from query, filtering out common stopwords.
    /// Includes stemming and compound term splitting for better matching.
    /// </summary>
    private static HashSet<string> ExtractSignificantKeywords(string query)
    {
        // Common English stopwords to filter out
        var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could", "should",
            "may", "might", "must", "shall", "can", "need", "dare", "ought", "used",
            "to", "of", "in", "for", "on", "with", "at", "by", "from", "as", "into",
            "through", "during", "before", "after", "above", "below", "between",
            "under", "again", "further", "then", "once", "here", "there", "when",
            "where", "why", "how", "all", "each", "few", "more", "most", "other",
            "some", "such", "no", "nor", "not", "only", "own", "same", "so", "than",
            "too", "very", "just", "also", "now", "and", "but", "or", "if", "because",
            "until", "while", "what", "which", "who", "whom", "this", "that", "these",
            "those", "am", "it", "its", "about", "tell", "me", "explain", "describe"
        };

        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Extract all words (including compound terms)
        var rawWords = Regex.Matches(query, @"\b[a-zA-Z]{3,}\b")
            .Select(m => m.Value)
            .Where(w => !stopwords.Contains(w))
            .ToList();

        foreach (var word in rawWords)
        {
            var lower = word.ToLowerInvariant();

            // Add the original word
            keywords.Add(lower);

            // Split compound terms (GraphRAG -> graph, rag; EntityFramework -> entity, framework)
            var parts = SplitCompoundTerm(word);
            foreach (var part in parts)
            {
                if (part.Length >= 3 && !stopwords.Contains(part))
                {
                    keywords.Add(part.ToLowerInvariant());
                }
            }

            // Add stemmed version (simple suffix stripping)
            var stemmed = SimpleStem(lower);
            if (stemmed.Length >= 3 && stemmed != lower)
            {
                keywords.Add(stemmed);
            }
        }

        return keywords;
    }

    /// <summary>
    /// Split compound terms like GraphRAG, EntityFramework into components.
    /// </summary>
    private static IEnumerable<string> SplitCompoundTerm(string term)
    {
        // Split on case boundaries (GraphRAG -> Graph, RAG)
        var parts = Regex.Split(term, @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")
            .Where(p => p.Length >= 2)
            .ToList();

        // If we got meaningful splits, return them
        if (parts.Count > 1)
        {
            return parts;
        }

        // Also try splitting on common boundaries (like numbers or known suffixes)
        return [term];
    }

    /// <summary>
    /// Simple stemming by removing common suffixes.
    /// Not a full Porter stemmer, but handles common cases.
    /// </summary>
    private static string SimpleStem(string word)
    {
        if (word.Length < 5) return word;

        // Order matters - check longer suffixes first
        string[] suffixes = ["ization", "isation", "ational", "fulness", "ousness",
                            "iveness", "ements", "ically", "ations", "abling",
                            "izing", "ising", "ating", "ities", "ments", "ness",
                            "ings", "tion", "sion", "ally", "ible", "able", "ment",
                            "ive", "ful", "ous", "ing", "ies", "ied", "ion", "ers",
                            "est", "ity", "ed", "ly", "er", "es", "s"];

        foreach (var suffix in suffixes)
        {
            if (word.EndsWith(suffix) && word.Length - suffix.Length >= 3)
            {
                var stem = word[..^suffix.Length];
                // Handle doubling (e.g., running -> run)
                if (stem.Length >= 3 && stem[^1] == stem[^2])
                {
                    stem = stem[..^1];
                }
                return stem;
            }
        }

        return word;
    }

    /// <summary>
    /// Build thinking/transparency output showing what the system is doing.
    /// </summary>
    private static string BuildThinkingOutput(string query, SearchResult searchResult)
    {
        var plan = searchResult.QueryPlan;
        if (plan == null) return "";

        var thinking = new StringBuilder();
        thinking.AppendLine("*Thinking...*");
        thinking.AppendLine($"- Query type: **{plan.QueryType}** ({plan.Mode})");
        thinking.AppendLine($"- Interpreted as: {plan.Intent}");
        thinking.AppendLine($"- Confidence: {plan.Confidence:P0}");

        if (plan.SubQueries.Count > 0)
        {
            thinking.AppendLine($"- Searching with {plan.SubQueries.Count} sub-queries");
        }

        thinking.AppendLine($"- Found **{searchResult.Results.Count}** relevant segments");

        if (searchResult.ResponseTimeMs > 0)
        {
            thinking.AppendLine($"- Search completed in {searchResult.ResponseTimeMs}ms");
        }

        return thinking.ToString();
    }

    /// <summary>
    /// Build response for keyword/navigation queries (no synthesis needed).
    /// </summary>
    private static string BuildKeywordResponse(List<SourceCitation> sources, string thinking)
    {
        if (sources.Count == 0)
        {
            return thinking + "\n\nNo documents found matching your search.";
        }

        var response = new StringBuilder(thinking);
        response.AppendLine();
        response.AppendLine($"Found **{sources.Count}** matching documents:");
        response.AppendLine();

        foreach (var source in sources)
        {
            response.AppendLine($"**[{source.Number}] {source.DocumentName}**");
            if (!string.IsNullOrEmpty(source.PageOrSection))
            {
                response.AppendLine($"   *{source.PageOrSection}*");
            }
            response.AppendLine($"   {source.Text}");
            response.AppendLine();
        }

        return response.ToString();
    }
}
