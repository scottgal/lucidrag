using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;
using LucidRAG.Config;
using LucidRAG.Data;
using LucidRAG.Services.Sentinel;
using StyloFlow.Retrieval;

namespace LucidRAG.Services;

public class AgenticSearchService(
    RagDocumentsDbContext db,
    IDocumentSummarizer summarizer,
    IVectorStore vectorStore,
    IEmbeddingService embeddingService,
    IConversationService conversationService,
    ISentinelService sentinelService,
    IQueryExpansionService queryExpansion,
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

        // Execute sub-queries and merge results using BM25 + RRF
        var allSegments = new List<(Segment Segment, double DenseScore, int Priority)>();
        var collectionName = _docSummarizerConfig.BertRag.CollectionName;

        // Build lookup for VectorStoreDocId -> DocumentEntity (handle duplicates by taking most recent)
        var documentLookup = await db.Documents
            .Where(d => d.VectorStoreDocId != null)
            .GroupBy(d => d.VectorStoreDocId!)
            .Select(g => g.OrderByDescending(d => d.CreatedAt).First())
            .ToDictionaryAsync(d => d.VectorStoreDocId!, d => d, ct);

        // Retrieve more candidates for BM25 re-ranking (3x the final count)
        var candidateCount = Math.Max(request.TopK * 3, 50);

        foreach (var subQuery in queryPlan.SubQueries.OrderBy(sq => sq.Priority))
        {
            logger.LogDebug("Executing sub-query: {Query} (purpose: {Purpose})", subQuery.Query, subQuery.Purpose);

            var queryEmbedding = await embeddingService.EmbedAsync(subQuery.Query, ct);

            var segments = await vectorStore.SearchAsync(
                collectionName,
                queryEmbedding,
                candidateCount,
                docId: null,
                ct);

            // Store with dense score and priority
            foreach (var s in segments)
            {
                allSegments.Add((s, s.QuerySimilarity, subQuery.Priority));
            }
        }

        // Deduplicate segments by ID, keeping best dense score
        var uniqueSegments = allSegments
            .GroupBy(x => x.Segment.Id)
            .Select(g => (Segment: g.First().Segment, DenseScore: g.Max(x => x.DenseScore)))
            .ToList();

        logger.LogInformation("Search mode: {Mode}, retrieved {Count} unique segments for BM25+RRF",
            request.SearchMode, uniqueSegments.Count);

        List<SearchResultItem> mergedResults;

        if (request.SearchMode == SearchMode.Semantic)
        {
            // Pure semantic mode - no BM25, just dense scores
            mergedResults = uniqueSegments
                .OrderByDescending(x => x.DenseScore)
                .Take(request.TopK)
                .Select(x => CreateSearchResultItem(x.Segment, x.DenseScore, documentLookup))
                .ToList();
        }
        else
        {
            // Hybrid/Keyword mode - use BM25 + RRF with query expansion
            // (4-way: dense + sparse + salience + freshness)
            // Query expansion: "golden" → "golden yellow gold amber"
            var rrfResults = await ApplyBm25RrfAsync(
                uniqueSegments,
                request.Query,
                request.SearchMode,
                request.TopK,
                documentLookup,
                ct);

            mergedResults = rrfResults
                .Select(x => CreateSearchResultItem(x.Segment, x.RrfScore, documentLookup))
                .ToList();
        }

        // Log top results for debugging
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
                ClarificationQuestion: clarificationQuestion,
                Timestamp: DateTimeOffset.UtcNow);
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
                ClarificationQuestion: noResultsMessage,
                Timestamp: DateTimeOffset.UtcNow);
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
                return new ChatResponse(offTopicAnswer, [], conversationId.Value, IsOffTopic: true, Timestamp: DateTimeOffset.UtcNow);
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

        return new ChatResponse(answer, sources, conversationId.Value, Timestamp: DateTimeOffset.UtcNow)
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
    /// Apply BM25 scoring and combine with dense scores using Reciprocal Rank Fusion.
    /// RRF(d) = 1/(k + rank_dense) + 1/(k + rank_bm25) + 1/(k + rank_salience) + 1/(k + rank_freshness)
    ///
    /// This four-way fusion captures:
    /// - Semantic similarity (dense embeddings)
    /// - Lexical matching (BM25 sparse retrieval with query expansion)
    /// - Document importance (salience from extraction)
    /// - Freshness (recent documents boosted)
    ///
    /// Query expansion uses ML embeddings to find synonyms:
    /// "golden sunset" → "golden yellow gold amber sunset sunrise evening"
    /// This enables semantic-ish matching on signals at BM25 speed.
    /// </summary>
    private async Task<List<(Segment Segment, double RrfScore)>> ApplyBm25RrfAsync(
        List<(Segment Segment, double DenseScore)> candidates,
        string query,
        SearchMode mode,
        int topK,
        Dictionary<string, Entities.DocumentEntity>? documentLookup = null,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0) return [];

        const int rrfK = 60; // RRF smoothing constant

        // Expand query terms using ML-based synonym detection
        // "golden" → ["golden", "yellow", "gold", "amber"]
        var expandedQuery = await queryExpansion.ExpandQueryAsync(query, maxExpansionsPerTerm: 3, ct);
        var queryForBm25 = expandedQuery.ExpandedQueryText;

        logger.LogDebug("Query expansion: '{Original}' → '{Expanded}'", query, queryForBm25);

        // Build BM25 corpus from candidate texts
        var corpus = Bm25Corpus.Build(candidates.Select(c => Bm25Scorer.Tokenize(c.Segment.Text)));
        var bm25 = new Bm25Scorer(corpus);

        // Score all candidates with BM25 (using expanded query) and get document freshness
        var scoredCandidates = candidates.Select(c =>
        {
            // BM25 with expanded query for synonym matching
            var bm25Score = bm25.Score(queryForBm25, c.Segment.Text);

            // Get document creation date for freshness scoring
            DateTimeOffset createdAt = DateTimeOffset.MinValue;
            var segmentDocId = ExtractDocIdFromSegmentId(c.Segment.Id);
            if (segmentDocId != null && documentLookup?.TryGetValue(segmentDocId, out var doc) == true)
            {
                createdAt = doc.CreatedAt;
            }

            return (c.Segment, c.DenseScore, Bm25Score: bm25Score, Salience: c.Segment.SalienceScore, CreatedAt: createdAt);
        }).ToList();

        // Rank by each signal
        var byDense = scoredCandidates.OrderByDescending(x => x.DenseScore).ToList();
        var byBm25 = scoredCandidates.OrderByDescending(x => x.Bm25Score).ToList();
        var bySalience = scoredCandidates.OrderByDescending(x => x.Salience).ToList();
        var byFreshness = scoredCandidates.OrderByDescending(x => x.CreatedAt).ToList(); // Most recent first

        // Compute RRF scores
        var rrfScores = new Dictionary<string, double>();
        var segmentLookup = candidates.ToDictionary(c => c.Segment.Id, c => c.Segment);

        // Weights based on search mode
        double denseWeight, bm25Weight, salienceWeight, freshnessWeight;
        if (mode == SearchMode.Keyword)
        {
            // Keyword mode: heavily favor BM25
            denseWeight = 0.3;
            bm25Weight = 1.5;
            salienceWeight = 0.2;
            freshnessWeight = 0.1;
        }
        else // Hybrid (default)
        {
            // Balanced weights with slight freshness bonus
            denseWeight = 1.0;
            bm25Weight = 1.0;
            salienceWeight = 0.3;
            freshnessWeight = 0.2;
        }

        // Dense ranking contribution
        for (int i = 0; i < byDense.Count; i++)
        {
            var id = byDense[i].Segment.Id;
            rrfScores[id] = denseWeight * (1.0 / (rrfK + i + 1));
        }

        // BM25 ranking contribution
        for (int i = 0; i < byBm25.Count; i++)
        {
            var id = byBm25[i].Segment.Id;
            rrfScores[id] = rrfScores.GetValueOrDefault(id) + bm25Weight * (1.0 / (rrfK + i + 1));
        }

        // Salience ranking contribution
        for (int i = 0; i < bySalience.Count; i++)
        {
            var id = bySalience[i].Segment.Id;
            rrfScores[id] = rrfScores.GetValueOrDefault(id) + salienceWeight * (1.0 / (rrfK + i + 1));
        }

        // Freshness ranking contribution (recent documents get boost)
        for (int i = 0; i < byFreshness.Count; i++)
        {
            var id = byFreshness[i].Segment.Id;
            rrfScores[id] = rrfScores.GetValueOrDefault(id) + freshnessWeight * (1.0 / (rrfK + i + 1));
        }

        // Return top-K by RRF score
        return rrfScores
            .OrderByDescending(kv => kv.Value)
            .Take(topK)
            .Select(kv => (segmentLookup[kv.Key], kv.Value))
            .ToList();
    }

    /// <summary>
    /// Create SearchResultItem from Segment with score.
    /// </summary>
    private static SearchResultItem CreateSearchResultItem(
        Segment segment,
        double score,
        Dictionary<string, Entities.DocumentEntity> documentLookup)
    {
        var segmentDocId = ExtractDocIdFromSegmentId(segment.Id);
        var doc = segmentDocId != null && documentLookup.TryGetValue(segmentDocId, out var d) ? d : null;

        return new SearchResultItem(
            DocumentId: doc?.Id ?? Guid.Empty,
            DocumentName: doc?.Name ?? segment.SectionTitle ?? segment.HeadingPath ?? "Unknown",
            SegmentId: segment.Id,
            Text: segment.Text,
            Score: score,
            SectionTitle: segment.SectionTitle);
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
