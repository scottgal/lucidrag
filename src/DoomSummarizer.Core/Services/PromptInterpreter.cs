using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Services;

/// <summary>
///     Interprets natural language prompts into actionable fetch commands
///     Uses a fast "sentinel" LLM for quick triage
/// </summary>
public partial class PromptInterpreter
{
    /// <summary>
    ///     Lazy-loaded source router for YAML-driven topic routing.
    ///     Initialized with semantic embeddings when EmbeddingService is available.
    /// </summary>
    private static readonly Lazy<SourceRouter> SharedRouter = new(() => SourceRouter.Load());

    /// <summary>
    ///     Shared embedding-based query classifier. Initialized once on first use.
    ///     Provides deterministic pre-LLM classification via cosine similarity.
    ///     Configure via <see cref="ConfigureClassifier" /> before first use.
    /// </summary>
    private static QueryClassifier SharedClassifier = new();

    /// <summary>
    ///     Optional learning logger for capturing sentinel disagreements.
    ///     When set, logs cases where the sentinel LLM corrects the embedding classifier.
    ///     Wired in CommandBootstrap after storage initialization.
    /// </summary>
    public static ILearningLogger? LearningLogger { get; set; }

    /// <summary>
    ///     Configure the shared classifier with custom thresholds from config.
    ///     Must be called before the first InterpretAsync call for settings to take effect.
    /// </summary>
    public static void ConfigureClassifier(Models.ClassifierConfig config)
    {
        if (!SharedClassifier.IsInitialized)
            SharedClassifier = new QueryClassifier(config);
    }

    private static readonly string[] ImagePatterns =
    [
        "show me an image for ", "show me an image of ", "show me a picture of ",
        "show image for ", "find an image for ", "find an image of ",
        "image of ", "picture of ", "show image of "
    ];

    private static readonly string[] SearchMarkers = ["about ", "for ", "search ", "find ", "regarding "];

    private readonly IEmbeddingService? _embedding;
    private readonly OllamaService _ollama;

    public PromptInterpreter(OllamaService ollama, IEmbeddingService? embedding = null)
    {
        _ollama = ollama;
        _embedding = embedding;
    }

    /// <summary>
    ///     Interpret a natural language prompt into fetch actions.
    ///     Overload without NER context — delegates to the full version.
    /// </summary>
    public async Task<InterpretedPrompt> InterpretAsync(string prompt, CancellationToken ct = default)
    {
        return await InterpretAsync(prompt, null, ct);
    }

    /// <summary>
    ///     Minimum embedding match score to skip the sentinel LLM entirely.
    ///     When embedding classification is this confident, we use it directly —
    ///     sentinel is only needed for decomposition or weak matches.
    /// </summary>
    private const double StrongMatchThreshold = 0.55;

    /// <summary>
    ///     Interpret a natural language prompt into fetch actions.
    ///     Strategy: embedding-first, sentinel-for-decomposition.
    ///     1. Always run embedding classifier (deterministic, fast ONNX cosine sim)
    ///     2. If strong match: use embedding categories + type directly, skip sentinel
    ///     3. If weak match or composite query likely: call sentinel for decomposition only
    ///     4. Sentinel output enriches with search_queries, subqueries, filter_keywords —
    ///        but categories come from embeddings.
    /// </summary>
    public async Task<InterpretedPrompt> InterpretAsync(string prompt, QueryNerContext? nerContext,
        CancellationToken ct = default)
    {
        // Ensure router + classifier are initialized before classification
        await GetRouterAsync();

        // Pre-LLM: deterministic embedding-based classification (always runs when available)
        Models.QueryClassification? embeddingClassification = null;
        if (SharedClassifier.IsInitialized)
        {
            embeddingClassification = await SharedClassifier.ClassifyAsync(prompt, ct);
            var vibeInfo = embeddingClassification.Vibe != null
                ? $" | vibe={embeddingClassification.Vibe} ({embeddingClassification.VibeConfidence:F2})"
                : "";
            var compositeInfo = embeddingClassification.IsComposite ? " | COMPOSITE" : "";
            var complexInfo = embeddingClassification.IsComplex ? " | COMPLEX" : "";
            Debug.WriteLine(
                $"Embedding classifier: {FormatCategories(embeddingClassification.Categories)} " +
                $"| type={embeddingClassification.QueryType} ({embeddingClassification.QueryTypeConfidence:F2})" +
                $"{vibeInfo}{compositeInfo}{complexInfo}" +
                $" | best={embeddingClassification.BestMatch} ({embeddingClassification.BestMatchScore:F2})");
        }

        // Strong embedding match + non-composite query → skip sentinel entirely
        var isStrongMatch = embeddingClassification is { BestMatchScore: >= StrongMatchThreshold }
                            && embeddingClassification.Categories.Count > 0;
        // Composite: embedding classifier already incorporates feature-based conjunction detection
        var looksComposite = embeddingClassification?.IsComposite == true
                             || prompt.Contains("; ", StringComparison.Ordinal);
        // Complex queries benefit from sentinel even with strong match
        var needsSentinel = looksComposite || embeddingClassification?.IsComplex == true;

        if (isStrongMatch && !needsSentinel)
        {
            Debug.WriteLine("Embedding match strong — skipping sentinel LLM");
            var router = await GetRouterAsync();
            var intent = BuildIntentFromClassification(embeddingClassification!, prompt);
            var result = SentinelSourceMapper.ToInterpretedPrompt(intent, router, prompt, nerContext);
            result.EmbeddingClassification = embeddingClassification;
            return result;
        }

        // Weak match or composite query → call sentinel for decomposition + refinement
        if (!await _ollama.IsAvailableAsync())
        {
            // No sentinel available — use embedding + keyword fallback
            var fallback = await FallbackInterpretAsync(prompt, embeddingClassification);
            if (nerContext != null)
                EnrichWithNerContext(fallback, nerContext);
            fallback.EmbeddingClassification = embeddingClassification;
            return fallback;
        }

        var systemPrompt = BuildStructuredSentinelPrompt(nerContext);

        // Build user prompt — include NER entities when available
        string userPrompt;
        if (nerContext?.HasEntities == true)
        {
            var entityInfo = string.Join(", ", nerContext.Entities
                .Select(e => $"{e.Text} ({e.Type}: {e.Type switch {
                    "PER" => "person",
                    "ORG" => "organization",
                    "LOC" => "location",
                    _ => "entity"
                }})"));
            userPrompt = $"""
                          Classify this request: "{prompt}"

                          DETECTED ENTITIES: {entityInfo}
                          Include entity names in search_queries with quotes for exact matching.
                          """;
        }
        else
        {
            userPrompt = $"Classify this request: \"{prompt}\"";
        }

        try
        {
            // Use JSON mode to force valid JSON output from Ollama
            var response = await _ollama.GenerateJsonAsync(userPrompt, systemPrompt, 0.1, ct);

            // With JSON mode, the response should be valid JSON directly
            if (string.IsNullOrWhiteSpace(response))
            {
                Debug.WriteLine("Sentinel returned empty response");
                throw new InvalidOperationException("Empty sentinel response");
            }

            var json = response.Trim();
            if (!json.StartsWith('{'))
            {
                // Fallback: extract JSON from response text
                var jsonStart = response.IndexOf('{');
                var jsonEnd = response.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                    json = response[jsonStart..(jsonEnd + 1)];
            }

            if (json.StartsWith('{'))
            {
                // Try structured SentinelIntent first
                SentinelIntent? intent = null;
                try
                {
                    intent = JsonSerializer.Deserialize(json, OllamaJsonContext.Default.SentinelIntent);
                }
                catch (JsonException ex)
                {
                    Debug.WriteLine(
                        $"Sentinel JSON parse error: {ex.Message}");
                    Debug.WriteLine(
                        $"Sentinel raw: {json[..Math.Min(json.Length, 400)]}");
                }

                if (intent != null &&
                    ((intent.Categories?.Count ?? 0) > 0
                     || (intent.SearchQueries?.Count ?? 0) > 0
                     || intent.HasSubqueries))
                {
                    // Debug: show composite query detection
                    if (intent.IsComposite || intent.HasSubqueries)
                    {
                        Debug.WriteLine(
                            $"Composite query detected: {intent.Subqueries?.Count ?? 0} subqueries");
                        foreach (var sq in intent.Subqueries ?? [])
                            Debug.WriteLine($"  - {sq}");
                    }

                    // Categories come from embeddings when available;
                    // sentinel provides decomposition (search_queries, subqueries, filter_keywords)
                    if (embeddingClassification?.Categories.Count > 0)
                    {
                        Debug.WriteLine(
                            $"Using embedding categories (sentinel for decomposition only)");
                        intent = intent with { Categories = embeddingClassification.Categories };

                        // Use embedding query type when it's confident
                        if (embeddingClassification.QueryTypeConfidence > 0.45)
                            intent = intent with { Intent = embeddingClassification.QueryType };
                    }

                    var router = await GetRouterAsync();
                    var result = SentinelSourceMapper.ToInterpretedPrompt(intent, router, prompt, nerContext);
                    result.EmbeddingClassification = embeddingClassification;

                    // Log sentinel disagreement for background learning
                    if (LearningLogger != null && embeddingClassification != null)
                    {
                        try
                        {
                            var queryEmb = _embedding != null
                                ? await _embedding.EmbedAsync(prompt, ct)
                                : null;
                            await LearningLogger.LogDisagreementAsync(
                                prompt, queryEmb, embeddingClassification, intent);
                        }
                        catch (Exception logEx)
                        {
                            Debug.WriteLine($"Learning log failed: {logEx.Message}");
                        }
                    }

                    return result;
                }

                // Log why SentinelIntent failed - show raw JSON for debugging
                if (intent != null)
                {
                    Debug.WriteLine(
                        $"Sentinel: parsed but no usable data. categories={intent.Categories?.Count ?? 0}, queries={string.Join(", ", intent.SearchQueries ?? [])}, subqueries={intent.Subqueries?.Count ?? 0}");
                    Debug.WriteLine(
                        $"Raw JSON: {json[..Math.Min(json.Length, 500)]}");
                }
                else
                {
                    Debug.WriteLine(
                        $"Sentinel: deserialized null. Raw: {json[..Math.Min(json.Length, 300)]}");
                }

            }
        }
        catch (Exception ex)
        {
            var trace = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
            Debug.WriteLine(
                $"Sentinel failed: {ex.GetType().Name}: {ex.Message}");
            if (!string.IsNullOrEmpty(trace))
                Debug.WriteLine($"  at {trace}");
        }

        var fallbackResult = await FallbackInterpretAsync(prompt, embeddingClassification);
        if (nerContext != null)
            EnrichWithNerContext(fallbackResult, nerContext);
        fallbackResult.EmbeddingClassification = embeddingClassification;
        return fallbackResult;
    }

    /// <summary>
    ///     Build a SentinelIntent from embedding classification alone (no LLM needed).
    ///     Used when the embedding match is strong enough to skip the sentinel.
    /// </summary>
    private static SentinelIntent BuildIntentFromClassification(
        Models.QueryClassification classification, string prompt)
    {
        var searchTerms = SentinelSourceMapper.ExtractTopicTerms(prompt);
        var searchQueries = new List<string>();
        if (!string.IsNullOrEmpty(searchTerms))
            searchQueries.Add(searchTerms);

        // Use embedding vibe when detected, otherwise neutral
        var tone = classification.Vibe ?? "neutral";

        return new SentinelIntent
        {
            Intent = classification.QueryType,
            Categories = classification.Categories,
            Tone = tone,
            SearchQueries = searchQueries,
            FilterKeywords = searchTerms?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList() ?? [],
            ExplicitSources = classification.SourceHints ?? []
        };
    }

    private static string FormatCategories(Dictionary<string, double> categories)
    {
        return string.Join(", ", categories
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => $"{kv.Key}={kv.Value:F2}"));
    }

    /// <summary>
    ///     Build the structured sentinel system prompt.
    ///     Simplified for small local models — Ollama JSON mode ensures valid output.
    /// </summary>
    private static string BuildStructuredSentinelPrompt(QueryNerContext? nerContext)
    {
        var today = DateTime.Now.ToString("MMMM d, yyyy");
        return $$"""
                 Output JSON classifying the search query. Today is {{today}}.

                 REQUIRED fields:
                 {
                   "categories": {"topic": 0.8},  // Use: technology, ai, programming, science, health, business, finance, politics, world, entertainment, sports
                   "intent": "qa",  // news|roundup|research|howto|qa|search_only|trend|comparison
                   "search_queries": ["optimized search query"],  // 2-3 search queries
                   "filter_keywords": ["keyword1", "keyword2"],  // 3-5 key terms
                   "tone": "neutral"  // neutral|doom|hopeful|snarky|funny
                 }

                 COMPOSITE queries (multiple questions joined by "and"/"also"):
                 {
                   "is_composite": true,
                   "subqueries": ["First standalone question?", "Second standalone question?"]
                 }
                 Resolve pronouns in each subquery: "it" → the actual subject.

                 IMPORTANT: Use "qa" for factual/trivia/knowledge questions, NOT "news".
                 "How much can a swallow carry?" → qa (factual, not current events)
                 "What is the speed of light?" → qa (factual knowledge)
                 "What happened at the UN today?" → news (current events)
                 "Latest AI developments" → news (current events)

                 Use "search_only" for queries needing a direct web search answer, NOT news feeds:
                 weather, sports scores, stock prices, time zones, unit conversions, "what is X", definitions.

                 Example: "What happens in Wuthering Heights and when was the latest movie made?"
                 {
                   "categories": {"entertainment": 0.9},
                   "intent": "qa",
                   "search_queries": ["Wuthering Heights plot summary", "Wuthering Heights movie 2024"],
                   "filter_keywords": ["Wuthering Heights", "movie", "plot"],
                   "is_composite": true,
                   "subqueries": ["What is the plot of Wuthering Heights?", "When was the latest Wuthering Heights movie made?"],
                   "tone": "neutral"
                 }
                 """;
    }

    /// <summary>
    ///     Keyword-based fallback when LLM isn't available.
    ///     When embedding classification is available, uses it for topic routing
    ///     instead of keyword matching alone.
    /// </summary>
    private async Task<InterpretedPrompt> FallbackInterpretAsync(
        string prompt, Models.QueryClassification? embeddingClassification = null)
    {
        var lower = prompt.ToLowerInvariant();
        var result = new InterpretedPrompt
        {
            RawPrompt = prompt,
            Sources = [],
            Vibe = "neutral",
            Limit = 20,
            GraphScope = QueryTypeDetector.DetectGraphScope(prompt)
        };

        // Detect vibe
        if (lower.Contains("doom") || lower.Contains("pessimist") || lower.Contains("negative"))
            result.Vibe = "doom";
        else if (lower.Contains("hopeful") || lower.Contains("positive") || lower.Contains("optimist"))
            result.Vibe = "hopeful";
        else if (lower.Contains("snark") || lower.Contains("witty"))
            result.Vibe = "snarky";
        else if (lower.Contains("funny") || lower.Contains("humor") || lower.Contains("comedy"))
            result.Vibe = "funny";
        else if (lower.Contains("upbeat") || lower.Contains("excited") || lower.Contains("enthusiast"))
            result.Vibe = "upbeat";
        else if (lower.Contains("friendly") || lower.Contains("casual") || lower.Contains("chill"))
            result.Vibe = "friendly";
        else if (lower.Contains("toon") || lower.Contains("cartoon") || lower.Contains("comic"))
            result.Vibe = "toon";

        // Detect image queries: "show me an image for...", "image of...", "picture of..."
        foreach (var pattern in ImagePatterns)
            if (lower.Contains(pattern))
            {
                result.ShowImage = true;
                var idx = lower.IndexOf(pattern);
                var imageQuery = prompt[(idx + pattern.Length)..].Trim();
                // Clean up the query
                var endIdx = imageQuery.IndexOfAny(['.', ',', '!', '?']);
                if (endIdx > 0) imageQuery = imageQuery[..endIdx];
                result.ImageQuery = imageQuery.Trim();

                // Also add as search query
                if (!string.IsNullOrEmpty(result.ImageQuery))
                {
                    result.SearchQueries.Add(result.ImageQuery);
                    result.Topics.Add(result.ImageQuery);
                }

                break;
            }

        // Enrich vibe from embedding when keyword detection returned neutral
        if (result.Vibe == "neutral" && embeddingClassification?.Vibe != null
            && embeddingClassification.VibeConfidence > 0.50)
            result.Vibe = embeddingClassification.Vibe;

        // If embedding classification has categories, use them for routing (deterministic)
        var router = await GetRouterAsync();
        if (embeddingClassification?.Categories.Count > 0)
        {
            Debug.WriteLine("Fallback: using embedding categories for routing");
            var intent = BuildIntentFromClassification(embeddingClassification, prompt);
            var embeddingResult = SentinelSourceMapper.ToInterpretedPrompt(intent, router, prompt);
            // Merge embedding-derived sources and topics into our result
            foreach (var src in embeddingResult.Sources)
                if (!result.Sources.Contains(src))
                    result.Sources.Add(src);
            foreach (var topic in embeddingResult.Topics)
                if (!result.Topics.Contains(topic))
                    result.Topics.Add(topic);
            foreach (var sq in embeddingResult.SearchQueries)
                if (!result.SearchQueries.Contains(sq))
                    result.SearchQueries.Add(sq);
        }
        else
        {
            // No embedding — fall back to keyword-based topic detection
            var detectedTopic = await router.DetectTopicAsync(prompt);
            if (detectedTopic != "default")
            {
                var routing = router.RouteByTopic(detectedTopic);
                // Map YAML source names to CLI source identifiers
                foreach (var src in routing.Sources)
                {
                    var mapped = SentinelSourceMapper.MapYamlSourceToCli(src, routing, prompt);
                    if (mapped != null && !result.Sources.Contains(mapped))
                        result.Sources.Add(mapped);
                }

                result.Topics.Add(detectedTopic);
            }
        }

        // Detect sources
        if (lower.Contains("hacker news") || lower.Contains("hackernews"))
            result.Sources.Add("hn");
        else if (lower.Contains(" hn ") || lower.StartsWith("hn ") || lower.EndsWith(" hn"))
            result.Sources.Add("hn");

        if (lower.Contains("reddit"))
        {
            // Check for specific subreddit: "r/csharp", "reddit csharp", etc.
            var subredditMatch = SubredditPattern().Match(lower);
            if (subredditMatch.Success)
            {
                var sub = subredditMatch.Groups[1].Success
                    ? subredditMatch.Groups[1].Value
                    : subredditMatch.Groups[2].Value;
                result.Sources.Add($"reddit:{sub}");
            }
            else
            {
                result.Sources.Add("reddit");
            }
        }

        // StackOverflow
        if (lower.Contains("stackoverflow") || lower.Contains("stack overflow") || lower.Contains(" so "))
        {
            // Check for tag: "so c#", "stackoverflow python"
            var soTagMatch = StackOverflowTagPattern().Match(lower);
            if (soTagMatch.Success)
                result.Sources.Add($"so:{soTagMatch.Groups[1].Value}");
            else
                result.Sources.Add("so");
        }

        // Detect source names mentioned by the user (YAML-driven + legacy non-YAML sources)
        var yamlFeedSources = router.AllSources
            .Where(s => router.GetSource(s) is { Search: false });
        var legacySources = new[] { "hackernoon", "slashdot", "arstechnica", "engadget", "zdnet", "thenextweb", "mostlylucid", "medium", "substack" };
        foreach (var source in yamlFeedSources.Concat(legacySources))
            if (lower.Contains(source) && !result.Sources.Contains(source))
            {
                result.Sources.Add(source);

                // Extract topic terms and also add as search query for better coverage
                var topicTerms = SentinelSourceMapper.ExtractTopicTermsExcluding(prompt, [source]);
                if (!string.IsNullOrEmpty(topicTerms) && !result.SearchQueries.Contains(topicTerms))
                {
                    result.SearchQueries.Add(topicTerms);
                    if (!result.Topics.Contains(topicTerms))
                        result.Topics.Add(topicTerms);
                }
            }

        // If "search" or "find" with a topic, add search query
        if ((lower.Contains("search") || lower.Contains("find") || lower.Contains("about")) &&
            !result.Sources.Any() && !result.Websites.Any())
        {
            // Extract likely search terms - words after "about", "for", "search"
            var searchTerms = ExtractSearchTerms(prompt);
            if (!string.IsNullOrEmpty(searchTerms)) result.SearchQueries.Add(searchTerms);
        }

        // If nothing was detected, treat the prompt as a topic search with YAML routing
        if (!result.Sources.Any() && !result.Websites.Any() && !result.SearchQueries.Any())
        {
            var topicTerms = SentinelSourceMapper.ExtractTopicTerms(prompt);
            if (!string.IsNullOrEmpty(topicTerms))
            {
                result.Topics.Add(topicTerms);
                result.SearchQueries.Add(topicTerms);

                // Use SourceRouter to get topic-appropriate sources
                var routing = await router.RouteAsync(topicTerms);
                foreach (var src in routing.Sources)
                {
                    var mapped = SentinelSourceMapper.MapYamlSourceToCli(src, routing, prompt);
                    if (mapped != null && !result.Sources.Contains(mapped))
                        result.Sources.Add(mapped);
                }

                // Always include Google News search for non-default topics
                if (!result.Sources.Any(s => s.StartsWith("gnews")))
                    result.Sources.Insert(0, $"gnews:{topicTerms}");
            }
            else
            {
                // Pure default - use default routing from YAML
                var defaultRouting = router.RouteByTopic("default");
                foreach (var src in defaultRouting.Sources)
                {
                    var mapped = SentinelSourceMapper.MapYamlSourceToCli(src, defaultRouting, null);
                    if (mapped != null && !result.Sources.Contains(mapped))
                        result.Sources.Add(mapped);
                }
            }
        }

        // Always ensure Google News is included for topic-based queries
        if (result.Topics.Count > 0 && !result.Sources.Any(s => s.StartsWith("gnews")))
        {
            var topicQuery = string.Join(" ", result.Topics);
            result.Sources.Insert(0, $"gnews:{topicQuery}");
        }

        return result;
    }

    private async Task<SourceRouter> GetRouterAsync()
    {
        var router = SharedRouter.Value;
        // Initialize semantic embeddings if not yet done and embedding service is available
        if (!router.HasEmbeddings && _embedding != null)
            await router.InitializeEmbeddingsAsync(_embedding);
        // Initialize query classifier if not yet done
        if (!SharedClassifier.IsInitialized && _embedding != null)
            await SharedClassifier.InitializeAsync(_embedding);
        return router;
    }

    /// <summary>
    ///     Enrich an interpreted prompt with NER-extracted entity queries.
    ///     Only adds precise entity search queries (gnews + search) — does NOT add
    ///     news outlet preferences (bbc:business, reuters, etc.) because NER entity type
    ///     (ORG/PER/LOC) doesn't determine topic category (e.g., "SNL" is ORG but entertainment).
    ///     Topic-based source routing is handled by the sentinel LLM and YAML routing.
    /// </summary>
    private static void EnrichWithNerContext(InterpretedPrompt result, QueryNerContext nerContext)
    {
        if (!nerContext.HasEntities) return;

        foreach (var eq in nerContext.EntityQueries)
        {
            // Add entity-specific quoted queries as search queries
            if (!result.SearchQueries.Any(q =>
                    q.Contains(eq.EntityText, StringComparison.OrdinalIgnoreCase)))
                result.SearchQueries.Add(eq.Query);

            // Add entity-specific gnews queries
            var gnewsQuery = $"gnews:{eq.EntityText}";
            if (!result.Sources.Any(s =>
                    s.Equals(gnewsQuery, StringComparison.OrdinalIgnoreCase) ||
                    (s.StartsWith("gnews:", StringComparison.OrdinalIgnoreCase) &&
                     s.Contains(eq.EntityText, StringComparison.OrdinalIgnoreCase))))
            {
                // Insert after existing gnews sources (don't displace them)
                var lastGnews = result.Sources.FindLastIndex(s =>
                    s.StartsWith("gnews", StringComparison.OrdinalIgnoreCase));
                result.Sources.Insert(lastGnews + 1, gnewsQuery);
            }
        }

        // Store NER context on the prompt for downstream use
        result.NerContext = nerContext;
    }

    private static string ExtractSearchTerms(string prompt)
    {
        var lower = prompt.ToLowerInvariant();

        foreach (var marker in SearchMarkers)
        {
            var idx = lower.IndexOf(marker);
            if (idx >= 0)
            {
                var rest = prompt[(idx + marker.Length)..].Trim();
                // Take up to the next punctuation or common stop word
                var endIdx = rest.IndexOfAny(['.', ',', '!', '?']);
                if (endIdx > 0) rest = rest[..endIdx];
                return rest.Trim();
            }
        }

        return prompt;
    }

    [GeneratedRegex(@"(?:r/|reddit\s+)(\w+)|reddit[:\s]+(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex SubredditPattern();

    [GeneratedRegex(@"(?:stackoverflow|stack overflow|so)[:\s]+(\w+(?:#|\+\+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex StackOverflowTagPattern();
}

public record InterpretedPrompt
{
    public required string RawPrompt { get; init; }
    public List<string> Sources { get; set; } = [];
    public string Vibe { get; set; } = "neutral";
    public List<string> SearchQueries { get; set; } = [];
    public List<string> Websites { get; set; } = [];
    public int Limit { get; set; } = 20;
    public List<string> Topics { get; set; } = [];

    /// <summary>
    ///     True if user wants to see an image (e.g., "show me an image for...")
    /// </summary>
    public bool ShowImage { get; set; }

    /// <summary>
    ///     Specific image query extracted from prompt
    /// </summary>
    public string? ImageQuery { get; set; }

    /// <summary>
    ///     NER-extracted entity context from query preprocessing.
    ///     Available when NER model is installed and query contains named entities.
    /// </summary>
    public QueryNerContext? NerContext { get; set; }

    /// <summary>
    ///     Structured sentinel intent (when using structured JSON sentinel).
    ///     Contains category weights, intent type, tone, time sensitivity.
    /// </summary>
    public SentinelIntent? SentinelIntent { get; set; }

    /// <summary>
    ///     GraphRAG scope: Local (specific), Global (sensemaking), Connective (DRIFT).
    ///     Auto-detected from query patterns and sentinel intent.
    ///     When Global or Connective, entity graph enrichment is auto-enabled.
    /// </summary>
    public GraphScope GraphScope { get; set; } = GraphScope.Local;

    /// <summary>
    ///     Embedding-based classification result (deterministic, pre-LLM).
    ///     Available when QueryClassifier is initialized with exemplars.
    ///     Contains topic categories, query type, and best-matching exemplar.
    /// </summary>
    public Models.QueryClassification? EmbeddingClassification { get; set; }
}

