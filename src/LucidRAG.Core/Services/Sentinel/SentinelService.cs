using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using LucidRAG.Data;
using LucidRAG.Entities;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.Services.Sentinel;

/// <summary>
/// The Sentinel query decomposition service.
///
/// Analyzes user queries and creates executable plans by:
/// 1. Understanding intent through tiny LLM or pattern matching
/// 2. Decomposing complex queries into sub-queries
/// 3. Determining filters and graph traversals
/// 4. Validating assumptions against schema
/// 5. Requesting clarification when needed
/// </summary>
public class SentinelService : ISentinelService
{
    private readonly RagDocumentsDbContext _db;
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingService _embedding;
    private readonly SentinelConfig _config;
    private readonly IMemoryCache _cache;
    private readonly HttpClient _http;
    private readonly ILogger<SentinelService> _logger;

    // Pattern matchers for traditional decomposition
    private static readonly Regex ComparisonPattern = new(
        @"\b(compare|difference|differ|vs|versus|between)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AggregationPattern = new(
        @"\b(total|sum|average|count|how many|how much)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TimePattern = new(
        @"\b(when|date|year|month|time|before|after|during|since)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RelationshipPattern = new(
        @"\b(related|connected|linked|relationship|associated|between)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ListPattern = new(
        @"\b(list|all|every|each|show me)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SpecificPattern = new(
        @"""([^""]+)""|'([^']+)'",
        RegexOptions.Compiled);

    // Pattern to detect question words (semantic queries)
    private static readonly Regex QuestionPattern = new(
        @"\b(what|how|why|explain|describe|tell me|can you|could you|would you|is it|are there|do|does|did|will|should)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Pattern for short keyword-only queries (no verbs, no question words)
    private static readonly Regex KeywordOnlyPattern = new(
        @"^[\w\s\.\-#\+]+$",
        RegexOptions.Compiled);

    public SentinelService(
        RagDocumentsDbContext db,
        IVectorStore vectorStore,
        IEmbeddingService embedding,
        IOptions<SentinelConfig> config,
        IMemoryCache cache,
        IHttpClientFactory httpFactory,
        ILogger<SentinelService> logger)
    {
        _db = db;
        _vectorStore = vectorStore;
        _embedding = embedding;
        _config = config.Value;
        _cache = cache;
        _http = httpFactory.CreateClient();
        _http.BaseAddress = new Uri(_config.OllamaBaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<QueryPlan> DecomposeAsync(
        string query,
        SchemaContext schema,
        SentinelOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new SentinelOptions();
        var sw = Stopwatch.StartNew();

        // Check cache first
        var cacheKey = $"sentinel:plan:{query.GetHashCode()}:{schema.GetHashCode()}:{options.Mode}";
        if (_config.CachePlans && _cache.TryGetValue<QueryPlan>(cacheKey, out var cached))
        {
            _logger.LogDebug("Using cached query plan for: {Query}", query);
            return cached!;
        }

        QueryPlan plan;

        // Determine mode
        var mode = options.Mode;
        if (mode == ExecutionMode.Traditional || !_config.Enabled)
        {
            plan = DecomposeTraditional(query, schema);
            plan = plan with { PlanningTimeMs = sw.ElapsedMilliseconds };
        }
        else
        {
            // Try tiny model first
            try
            {
                plan = await DecomposeWithLlmAsync(query, schema, options, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LLM decomposition failed, falling back to traditional");
                plan = DecomposeTraditional(query, schema);
            }

            plan = plan with { PlanningTimeMs = sw.ElapsedMilliseconds };
        }

        // Validate assumptions if requested
        if (options.ValidateAssumptions && plan.Assumptions.Count > 0)
        {
            plan = await ValidateAssumptionsAsync(plan, ct);

            // Check if any critical assumptions failed
            var failedAssumptions = plan.Assumptions
                .Where(a => a.Validated == false && a.Confidence > 0.7)
                .ToList();

            if (failedAssumptions.Count > 0)
            {
                _logger.LogInformation("Critical assumptions failed: {Assumptions}",
                    string.Join(", ", failedAssumptions.Select(a => a.Description)));

                // Lower confidence and potentially ask for clarification
                var newConfidence = plan.Confidence * 0.5;
                if (newConfidence < options.ClarificationThreshold)
                {
                    plan = plan with
                    {
                        Confidence = newConfidence,
                        NeedsClarification = true,
                        ClarificationQuestion = BuildClarificationQuestion(query, failedAssumptions)
                    };
                }
            }
        }

        // Cache the plan
        if (_config.CachePlans)
        {
            _cache.Set(cacheKey, plan, _config.PlanCacheTtl);
        }

        return plan;
    }

    /// <inheritdoc />
    public QueryPlan DecomposeTraditional(string query, SchemaContext schema)
    {
        var subQueries = new List<SubQuery>();
        var operations = new List<ResultOperation>();
        var assumptions = new List<SentinelAssumption>();
        var filters = new QueryFilters();
        var confidence = 0.7; // Base confidence for traditional

        // Analyze query patterns
        var isComparison = ComparisonPattern.IsMatch(query);
        var isAggregation = AggregationPattern.IsMatch(query);
        var hasTimeRef = TimePattern.IsMatch(query);
        var hasRelationship = RelationshipPattern.IsMatch(query);
        var isList = ListPattern.IsMatch(query);

        // Extract quoted terms (specific entities/values)
        var specificTerms = SpecificPattern.Matches(query)
            .Select(m => m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        // Primary query - the full query for semantic search
        subQueries.Add(new SubQuery
        {
            Query = query,
            Purpose = "Primary semantic search",
            Priority = 1,
            TopK = 10,
            UseSparse = true // Also use BM25
        });

        // If comparison, extract entities to compare
        if (isComparison)
        {
            operations.Add(new ResultOperation
            {
                Type = ResultOperationType.Compare,
                Fields = specificTerms.ToArray()
            });

            // Create sub-queries for each specific term
            foreach (var term in specificTerms.Take(3))
            {
                subQueries.Add(new SubQuery
                {
                    Query = term,
                    Purpose = $"Find information about '{term}'",
                    Priority = 2,
                    TopK = 5,
                    UseSparse = true // Exact match important
                });

                assumptions.Add(new SentinelAssumption
                {
                    Description = $"Entity '{term}' exists in the data",
                    Validation = new AssumptionValidation
                    {
                        Type = ValidationType.ResultsExist,
                        Query = term
                    },
                    Confidence = 0.8
                });
            }

            confidence = 0.65; // Comparison queries need more care
        }

        // If aggregation, note the operation
        if (isAggregation)
        {
            operations.Add(new ResultOperation
            {
                Type = ResultOperationType.Aggregate
            });

            // Check if we have tabular data
            if (schema.ContentTypes.Contains(ContentTypes.Data))
            {
                assumptions.Add(new SentinelAssumption
                {
                    Description = "Tabular data available for aggregation",
                    Validation = new AssumptionValidation
                    {
                        Type = ValidationType.ContentTypeExists,
                        Expected = ContentTypes.Data
                    },
                    Confidence = 0.9
                });
            }
            else
            {
                confidence *= 0.7; // Lower confidence without tabular data
            }
        }

        // If time-based, try to extract date references
        if (hasTimeRef)
        {
            // Add time-focused sub-query
            subQueries.Add(new SubQuery
            {
                Query = $"timeline chronology {query}",
                Purpose = "Temporal context",
                Priority = 2,
                TopK = 5
            });
        }

        // If relationship-focused, add graph traversal
        if (hasRelationship && schema.RelationshipTypes.Count > 0)
        {
            // Try to identify entities for graph traversal
            foreach (var term in specificTerms.Take(2))
            {
                subQueries.Add(new SubQuery
                {
                    Query = term,
                    Purpose = $"Find entity '{term}' for relationship traversal",
                    Priority = 3,
                    TopK = 3
                });
            }
        }

        // If listing, ensure broader search
        if (isList)
        {
            subQueries[0] = subQueries[0] with { TopK = 20 };
        }

        // Build intent description
        var intentParts = new List<string>();
        if (isComparison) intentParts.Add("comparison");
        if (isAggregation) intentParts.Add("aggregation");
        if (hasTimeRef) intentParts.Add("time-based");
        if (hasRelationship) intentParts.Add("relationship");
        if (isList) intentParts.Add("listing");
        if (intentParts.Count == 0) intentParts.Add("information retrieval");

        // Classify query type
        var queryType = ClassifyQueryType(query, isComparison, isAggregation, isList);

        return new QueryPlan
        {
            OriginalQuery = query,
            Intent = $"User wants: {string.Join(", ", intentParts)}",
            Confidence = confidence,
            SubQueries = subQueries,
            Filters = filters,
            Operations = operations,
            Assumptions = assumptions,
            Mode = ExecutionMode.Traditional,
            QueryType = queryType,
            ProducerModel = "pattern-based"
        };
    }

    /// <summary>
    /// Classify the query type to determine if synthesis is needed.
    /// Keyword queries skip synthesis and just show matching documents.
    /// </summary>
    private QueryType ClassifyQueryType(string query, bool isComparison, bool isAggregation, bool isList)
    {
        // Comparisons need synthesis
        if (isComparison) return QueryType.Comparison;

        // Aggregations need synthesis
        if (isAggregation) return QueryType.Aggregation;

        // List/navigation queries don't need synthesis
        if (isList) return QueryType.Navigation;

        // Check for question words - semantic queries need synthesis
        if (QuestionPattern.IsMatch(query)) return QueryType.Semantic;

        // Short keyword-only queries (no question marks, no verbs)
        var words = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 3 && KeywordOnlyPattern.IsMatch(query) && !query.Contains('?'))
        {
            _logger.LogDebug("Query '{Query}' classified as keyword (short, no question words)", query);
            return QueryType.Keyword;
        }

        // Default to semantic for longer or complex queries
        return QueryType.Semantic;
    }

    /// <inheritdoc />
    public async Task<QueryPlan> ValidateAssumptionsAsync(QueryPlan plan, CancellationToken ct = default)
    {
        var validatedAssumptions = new List<SentinelAssumption>();

        foreach (var assumption in plan.Assumptions)
        {
            var validated = await ValidateAssumptionAsync(assumption, ct);
            validatedAssumptions.Add(validated);
        }

        return plan with { Assumptions = validatedAssumptions };
    }

    /// <inheritdoc />
    public async Task<SchemaContext> BuildSchemaContextAsync(Guid? collectionId = null, CancellationToken ct = default)
    {
        var contentTypes = new HashSet<string>();
        var evidenceTypes = new HashSet<string>();
        var entityTypes = new HashSet<string>();
        var relationshipTypes = new HashSet<string>();
        var columns = new Dictionary<string, ColumnInfo>();
        var collections = new List<CollectionInfo>();
        var sampleNames = new List<string>();

        // Get collections
        var collectionsQuery = _db.Collections.AsQueryable();
        if (collectionId.HasValue)
            collectionsQuery = collectionsQuery.Where(c => c.Id == collectionId.Value);

        var dbCollections = await collectionsQuery
            .Select(c => new { c.Id, c.Name, c.Description })
            .ToListAsync(ct);

        foreach (var c in dbCollections)
        {
            var docCount = await _db.Documents
                .Where(d => d.CollectionId == c.Id)
                .CountAsync(ct);

            collections.Add(new CollectionInfo
            {
                Id = c.Id,
                Name = c.Name,
                Description = c.Description,
                DocumentCount = docCount
            });
        }

        // Get documents
        var docsQuery = _db.Documents.AsQueryable();
        if (collectionId.HasValue)
            docsQuery = docsQuery.Where(d => d.CollectionId == collectionId.Value);

        var documents = await docsQuery
            .OrderByDescending(d => d.CreatedAt)
            .Take(100)
            .Select(d => new { d.Name, d.MimeType, d.CreatedAt })
            .ToListAsync(ct);

        foreach (var doc in documents)
        {
            if (!string.IsNullOrEmpty(doc.MimeType))
            {
                var ct_ = Entities.ContentTypes.FromMimeType(doc.MimeType);
                contentTypes.Add(ct_);
            }
        }

        sampleNames.AddRange(documents.Take(10).Select(d => d.Name));

        // Get entities from retrieval records
        var entities = await _db.RetrievalEntities
            .Where(e => !collectionId.HasValue || e.CollectionId == collectionId.Value)
            .Select(e => new { e.ContentType, e.SourceModalities })
            .Take(500)
            .ToListAsync(ct);

        foreach (var e in entities)
        {
            if (!string.IsNullOrEmpty(e.ContentType))
                contentTypes.Add(e.ContentType);
        }

        // Get GraphRAG entities and relationships
        var graphEntities = await _db.Entities
            .Select(e => e.EntityType)
            .Distinct()
            .ToListAsync(ct);
        entityTypes.UnionWith(graphEntities);

        var graphRelationships = await _db.EntityRelationships
            .Select(r => r.RelationshipType)
            .Distinct()
            .ToListAsync(ct);
        relationshipTypes.UnionWith(graphRelationships);

        // Get evidence types
        var evidence = await _db.EvidenceArtifacts
            .Select(e => e.ArtifactType)
            .Distinct()
            .ToListAsync(ct);
        evidenceTypes.UnionWith(evidence);

        // Date range
        DateTimeOffset? earliest = null, latest = null;
        if (documents.Count > 0)
        {
            earliest = documents.Min(d => d.CreatedAt);
            latest = documents.Max(d => d.CreatedAt);
        }

        return new SchemaContext
        {
            ContentTypes = contentTypes,
            EvidenceTypes = evidenceTypes,
            EntityTypes = entityTypes,
            RelationshipTypes = relationshipTypes,
            Columns = columns,
            Collections = collections,
            DocumentCount = documents.Count,
            EarliestDocument = earliest,
            LatestDocument = latest,
            SampleDocumentNames = sampleNames
        };
    }

    private async Task<QueryPlan> DecomposeWithLlmAsync(
        string query,
        SchemaContext schema,
        SentinelOptions options,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var model = options.ForceModel ?? _config.TinyModel;

        var prompt = BuildDecompositionPrompt(query, schema);

        try
        {
            var response = await CallOllamaAsync(model, prompt, ct);
            var plan = ParseLlmResponse(query, response, schema);

            return plan with
            {
                ProducerModel = model,
                PlanningTimeMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex) when (options.AllowEscalation)
        {
            _logger.LogWarning(ex, "Tiny model failed, escalating to {Model}", _config.EscalationModel);

            var response = await CallOllamaAsync(_config.EscalationModel, prompt, ct);
            var plan = ParseLlmResponse(query, response, schema);

            return plan with
            {
                ProducerModel = _config.EscalationModel,
                PlanningTimeMs = sw.ElapsedMilliseconds
            };
        }
    }

    private string BuildDecompositionPrompt(string query, SchemaContext schema)
    {
        var schemaDesc = schema.ToPromptDescription();
        return $@"You are a query decomposition assistant. Analyze the user query and output a JSON plan.

AVAILABLE DATA:
{schemaDesc}

USER QUERY: {query}

OUTPUT a JSON object with:
- intent: what the user wants (1 sentence)
- confidence: 0.0-1.0 how confident you are
- subQueries: array of {{ ""query"": ""..."", ""purpose"": ""..."", ""priority"": 1-3, ""topK"": 3-10, ""useSparse"": bool }}
- operations: array of {{ ""type"": ""retrieve""|""compare""|""aggregate""|""trend"", ""fields"": [] }}
- assumptions: array of {{ ""description"": ""..."", ""confidence"": 0.0-1.0 }}
- needsClarification: bool
- clarificationQuestion: string if needs clarification

Rules:
1. Break complex queries into 2-5 focused sub-queries
2. Set useSparse=true for exact terms, proper nouns, codes
3. If comparing, create sub-queries for each item
4. If query is ambiguous, set needsClarification=true
5. List assumptions about what data exists

JSON only, no explanation:";
    }

    private async Task<string> CallOllamaAsync(string model, string prompt, CancellationToken ct)
    {
        var request = new
        {
            model = model,
            prompt = prompt,
            stream = false,
            options = new
            {
                temperature = _config.Temperature,
                num_predict = _config.MaxTokens
            }
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var response = await _http.PostAsJsonAsync("/api/generate", request, cts.Token);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaResponse>(cts.Token);
        return result?.Response ?? "";
    }

    private QueryPlan ParseLlmResponse(string originalQuery, string response, SchemaContext schema)
    {
        try
        {
            // Find JSON in response
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');

            if (jsonStart < 0 || jsonEnd < 0)
                return DecomposeTraditional(originalQuery, schema);

            var json = response[jsonStart..(jsonEnd + 1)];
            var parsed = JsonSerializer.Deserialize<LlmQueryPlan>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (parsed == null)
                return DecomposeTraditional(originalQuery, schema);

            // Convert to QueryPlan
            var subQueries = parsed.SubQueries?.Select(sq => new SubQuery
            {
                Query = sq.Query ?? originalQuery,
                Purpose = sq.Purpose ?? "Search",
                Priority = sq.Priority,
                TopK = Math.Clamp(sq.TopK, 3, 20),
                UseSparse = sq.UseSparse
            }).ToList() ?? [new SubQuery { Query = originalQuery, Purpose = "Primary", Priority = 1, TopK = 10 }];

            var operations = parsed.Operations?.Select(op => new ResultOperation
            {
                Type = Enum.TryParse<ResultOperationType>(op.Type, true, out var t) ? t : ResultOperationType.Retrieve,
                Fields = op.Fields?.ToArray()
            }).ToList() ?? [];

            var assumptions = parsed.Assumptions?.Select(a => new SentinelAssumption
            {
                Description = a.Description ?? "",
                Confidence = a.Confidence,
                Validation = new AssumptionValidation { Type = ValidationType.ResultsExist }
            }).ToList() ?? [];

            // Classify query type using the same logic as traditional mode
            var isComparison = ComparisonPattern.IsMatch(originalQuery);
            var isAggregation = AggregationPattern.IsMatch(originalQuery);
            var isList = ListPattern.IsMatch(originalQuery);
            var queryType = ClassifyQueryType(originalQuery, isComparison, isAggregation, isList);

            return new QueryPlan
            {
                OriginalQuery = originalQuery,
                Intent = parsed.Intent ?? "Information retrieval",
                Confidence = Math.Clamp(parsed.Confidence, 0.1, 1.0),
                SubQueries = subQueries,
                Operations = operations,
                Assumptions = assumptions,
                NeedsClarification = parsed.NeedsClarification,
                ClarificationQuestion = parsed.ClarificationQuestion,
                Mode = ExecutionMode.Hybrid,
                QueryType = queryType
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse LLM response, falling back to traditional");
            return DecomposeTraditional(originalQuery, schema);
        }
    }

    private async Task<SentinelAssumption> ValidateAssumptionAsync(SentinelAssumption assumption, CancellationToken ct)
    {
        bool validated;
        string? result;

        switch (assumption.Validation.Type)
        {
            case ValidationType.ContentTypeExists:
                var contentType = assumption.Validation.Expected?.ToString();
                validated = await _db.RetrievalEntities
                    .AnyAsync(e => e.ContentType == contentType, ct);
                result = validated ? "Content type exists" : "Content type not found";
                break;

            case ValidationType.ResultsExist:
                var searchQuery = assumption.Validation.Query ?? assumption.Description;
                var embedding = await _embedding.EmbedAsync(searchQuery, ct);
                var results = await _vectorStore.SearchAsync(_config.CollectionName, embedding, 1, ct: ct);
                validated = results.Count > 0 && results[0].QuerySimilarity > 0.3;
                result = validated ? $"Found {results.Count} results" : "No relevant results found";
                break;

            case ValidationType.EntityExists:
                var entityName = assumption.Validation.Expected?.ToString();
                validated = await _db.Entities
                    .AnyAsync(e => e.CanonicalName.Contains(entityName ?? ""), ct);
                result = validated ? "Entity exists" : "Entity not found";
                break;

            case ValidationType.FieldExists:
                // Would check data profiles for column existence
                validated = true; // Simplified
                result = "Field validation not implemented";
                break;

            default:
                validated = true;
                result = "Validation type not supported";
                break;
        }

        return assumption with
        {
            Validated = validated,
            ValidationResult = result
        };
    }

    private string BuildClarificationQuestion(string query, List<SentinelAssumption> failedAssumptions)
    {
        var issues = string.Join("; ", failedAssumptions.Select(a => a.ValidationResult ?? a.Description));
        return $"I need some clarification about your query \"{query}\". {issues}. Could you rephrase or provide more context?";
    }

    // DTOs for JSON parsing
    private record OllamaResponse(string Response);

    private record LlmQueryPlan
    {
        public string? Intent { get; init; }
        public double Confidence { get; init; }
        public List<LlmSubQuery>? SubQueries { get; init; }
        public List<LlmOperation>? Operations { get; init; }
        public List<LlmAssumption>? Assumptions { get; init; }
        public bool NeedsClarification { get; init; }
        public string? ClarificationQuestion { get; init; }
    }

    private record LlmSubQuery
    {
        public string? Query { get; init; }
        public string? Purpose { get; init; }
        public int Priority { get; init; } = 1;
        public int TopK { get; init; } = 5;
        public bool UseSparse { get; init; }
    }

    private record LlmOperation
    {
        public string? Type { get; init; }
        public List<string>? Fields { get; init; }
    }

    private record LlmAssumption
    {
        public string? Description { get; init; }
        public double Confidence { get; init; }
    }
}
