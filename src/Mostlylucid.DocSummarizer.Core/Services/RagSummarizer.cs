using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.Summarizer.Core.Utilities;


namespace Mostlylucid.DocSummarizer.Services;

public class RagSummarizer
{
    private const string CollectionPrefix = "docsummarizer_";

    /// <summary>
    ///     Default max parallelism for LLM calls. Ollama processes one request at a time per model,
    ///     so high values just queue requests. 8 is a good balance for throughput vs memory.
    /// </summary>
    public const int DefaultMaxParallelism = 8;
    
    /// <summary>
    /// Document type classification for adaptive prompts
    /// </summary>
    public enum DocumentType
    {
        Unknown,
        Fiction,        // Novels, short stories, creative writing
        Technical,      // Code docs, API refs, manuals, specs
        Academic,       // Research papers, theses, scholarly articles
        Business,       // Reports, proposals, memos, contracts
        Legal,          // Contracts, laws, regulations, legal briefs
        News,           // Articles, journalism, press releases
        Reference       // Encyclopedias, wikis, how-tos, tutorials
    }
    
    /// <summary>
    /// Document classification result with confidence
    /// </summary>
    private record DocumentClassification(
        DocumentType Type,
        double Confidence,
        string[] Indicators,
        string Method = "heuristic") // "heuristic" or "llm"
    {
        public bool IsHighConfidence => Confidence >= 0.7;
        public bool IsLowConfidence => Confidence < 0.5;
    }
    
    /// <summary>
    /// Classify document type using heuristics on early chunks
    /// No LLM call - purely statistical/pattern-based for speed
    /// </summary>
    private static DocumentClassification ClassifyDocument(List<DocumentChunk> chunks)
    {
        // Sample first ~5 chunks for classification (enough signal, fast)
        var sampleText = string.Join(" ", chunks.Take(5).Select(c => c.Content));
        var sampleLower = sampleText.ToLowerInvariant();
        var headings = chunks.Take(10).Select(c => c.Heading?.ToLowerInvariant() ?? "").ToList();
        var headingsText = string.Join(" ", headings);
        
        var scores = new Dictionary<DocumentType, (double score, List<string> indicators)>
        {
            [DocumentType.Fiction] = (0, []),
            [DocumentType.Technical] = (0, []),
            [DocumentType.Academic] = (0, []),
            [DocumentType.Business] = (0, []),
            [DocumentType.Legal] = (0, []),
            [DocumentType.News] = (0, []),
            [DocumentType.Reference] = (0, [])
        };
        
        // Fiction indicators
        var fictionPatterns = new (string pattern, double weight)[]
        {
            (@"\b(said|replied|whispered|shouted|asked)\b", 0.3),
            (@"\b(he|she|they)\s+(walked|ran|looked|felt|thought)\b", 0.25),
            (@"\b(chapter|prologue|epilogue)\s*\d*\b", 0.4),
            (@"[""'][^""']{20,}[""']", 0.2), // Dialogue
            (@"\b(mr\.|mrs\.|miss|lady|lord|sir)\s+[a-z]+\b", 0.25),
            (@"\b(smiled|frowned|sighed|laughed|cried)\b", 0.2),
            (@"\b(heart|soul|love|passion|desire)\b", 0.15),
        };
        
        // Technical indicators (software + hardware/electronics)
        var technicalPatterns = new (string pattern, double weight)[]
        {
            // Software patterns
            (@"\b(function|class|method|api|interface|module)\b", 0.35),
            (@"\b(parameter|argument|return|async|await)\b", 0.3),
            (@"```|\bcode\b|`[^`]+`", 0.4),
            (@"\b(install|configure|setup|deploy|build)\b", 0.25),
            (@"\b(error|exception|debug|log|trace)\b", 0.2),
            (@"\b(version|v\d+\.\d+|release)\b", 0.2),
            (@"\b(http|https|url|endpoint|request|response)\b", 0.25),
            (@"\b(json|xml|yaml|config|schema)\b", 0.25),
            // Hardware/Electronics patterns (VHDL, FPGA, Digital Logic)
            (@"\b(vhdl|verilog|hdl|rtl|fpga|asic|cpld)\b", 0.5),
            (@"\b(flip.?flop|latch|register|counter|decoder|multiplexer|mux)\b", 0.4),
            (@"\b(logic gate|and gate|or gate|nand|nor|xor|inverter)\b", 0.4),
            (@"\b(signal|port|entity|architecture|component|process)\b", 0.35),
            (@"\b(clock|reset|enable|input|output|inout)\b", 0.25),
            (@"\b(std_logic|std_logic_vector|integer|boolean)\b", 0.45),
            (@"\b(synthesis|simulation|testbench|waveform)\b", 0.35),
            (@"\b(timing|propagation delay|setup time|hold time)\b", 0.35),
            (@"\b(combinational|sequential|synchronous|asynchronous)\b", 0.3),
            (@"\b(truth table|karnaugh|boolean algebra|state machine|fsm)\b", 0.4),
            (@"\b(bit|byte|word|bus|address|memory|ram|rom)\b", 0.25),
            // React/Frontend patterns  
            (@"\b(react|component|props|state|hooks?|usestate|useeffect)\b", 0.45),
            (@"\b(jsx|tsx|webpack|babel|npm|yarn)\b", 0.35),
            (@"\b(dom|virtual dom|render|mount|unmount)\b", 0.3),
        };
        
        // Academic indicators
        var academicPatterns = new (string pattern, double weight)[]
        {
            (@"\b(abstract|introduction|methodology|conclusion|references)\b", 0.4),
            (@"\b(hypothesis|findings|results|analysis|discussion)\b", 0.3),
            (@"\b(study|research|paper|journal|publication)\b", 0.25),
            (@"\([a-z]+,?\s*\d{4}\)", 0.35), // Citations like (Smith, 2020)
            (@"\b(et al\.?|ibid|op\.?\s*cit)\b", 0.4),
            (@"\b(figure|table|appendix)\s*\d+", 0.25),
            (@"\b(significant|correlation|variable|sample|data)\b", 0.2),
        };
        
        // Business indicators
        var businessPatterns = new (string pattern, double weight)[]
        {
            (@"\b(executive summary|overview|objectives|deliverables)\b", 0.35),
            (@"\b(revenue|profit|margin|budget|cost|roi)\b", 0.3),
            (@"\b(stakeholder|client|vendor|partner|customer)\b", 0.25),
            (@"\b(q[1-4]|fy\d{2,4}|fiscal|quarter)\b", 0.3),
            (@"\b(strategy|initiative|roadmap|milestone)\b", 0.25),
            (@"\b(kpi|metric|target|goal|benchmark)\b", 0.25),
        };
        
        // Legal indicators  
        var legalPatterns = new (string pattern, double weight)[]
        {
            (@"\b(whereas|hereby|herein|thereof|pursuant)\b", 0.4),
            (@"\b(party|parties|agreement|contract|clause)\b", 0.3),
            (@"\b(shall|must|may not|is prohibited)\b", 0.25),
            (@"\b(liability|indemnify|warranty|damages)\b", 0.3),
            (@"\b(section|article|paragraph)\s*\d+", 0.25),
            (@"\b(plaintiff|defendant|court|jurisdiction)\b", 0.35),
        };
        
        // News indicators
        var newsPatterns = new (string pattern, double weight)[]
        {
            (@"\b(reported|announced|according to|sources say)\b", 0.35),
            (@"\b(yesterday|today|monday|tuesday|wednesday|thursday|friday)\b", 0.2),
            (@"\b(officials?|spokesperson|minister|president|ceo)\b", 0.25),
            (@"\b(breaking|update|developing|exclusive)\b", 0.3),
            (@"\b(interview|statement|press|conference)\b", 0.2),
        };
        
        // Reference/Tutorial indicators
        var referencePatterns = new (string pattern, double weight)[]
        {
            (@"\b(step\s*\d+|first,?|next,?|then,?|finally)\b", 0.25),
            (@"\b(how to|guide|tutorial|learn|example)\b", 0.35),
            (@"\b(tip|note|warning|important|see also)\b", 0.25),
            (@"\b(definition|overview|summary|quick\s*start)\b", 0.25),
            (@"\b(faq|frequently asked|common questions)\b", 0.3),
        };
        
        // Score each type
        void ScorePatterns((string pattern, double weight)[] patterns, DocumentType type)
        {
            var (score, indicators) = scores[type];
            foreach (var (pattern, weight) in patterns)
            {
                var matches = Regex.Matches(sampleLower, pattern, RegexOptions.IgnoreCase);
                if (matches.Count > 0)
                {
                    score += weight * Math.Min(matches.Count, 3); // Cap at 3 matches per pattern
                    if (indicators.Count < 3)
                        indicators.Add($"{pattern.TrimStart('\\', 'b', '(').Split('|')[0]}×{matches.Count}");
                }
            }
            // Also check headings
            foreach (var (pattern, weight) in patterns)
            {
                if (Regex.IsMatch(headingsText, pattern, RegexOptions.IgnoreCase))
                    score += weight * 0.5;
            }
            scores[type] = (score, indicators);
        }
        
        ScorePatterns(fictionPatterns, DocumentType.Fiction);
        ScorePatterns(technicalPatterns, DocumentType.Technical);
        ScorePatterns(academicPatterns, DocumentType.Academic);
        ScorePatterns(businessPatterns, DocumentType.Business);
        ScorePatterns(legalPatterns, DocumentType.Legal);
        ScorePatterns(newsPatterns, DocumentType.News);
        ScorePatterns(referencePatterns, DocumentType.Reference);
        
        // Find winner
        var winner = scores.OrderByDescending(kv => kv.Value.score).First();
        var totalScore = scores.Values.Sum(v => v.score);
        var confidence = totalScore > 0 ? winner.Value.score / totalScore : 0;
        
        // If no clear winner or very low scores, return Unknown
        if (winner.Value.score < 0.5 || confidence < 0.3)
            return new DocumentClassification(DocumentType.Unknown, confidence, [], "heuristic");
        
        return new DocumentClassification(
            winner.Key, 
            Math.Min(confidence, 0.95), // Cap at 95% confidence
            winner.Value.indicators.ToArray(),
            "heuristic");
    }
    
    /// <summary>
    /// Sentinel LLM classification - sends first chunk with a simple binary question
    /// Uses a small/fast classifier model (e.g., tinyllama ~2K context) for speed
    /// </summary>
    private async Task<DocumentClassification> ClassifyDocumentWithLlmAsync(List<DocumentChunk> chunks)
    {
        // Take first chunk only - enough context for classification
        var firstChunk = chunks.FirstOrDefault();
        if (firstChunk == null)
            return new DocumentClassification(DocumentType.Unknown, 0, [], "sentinel-empty");
        
        // IMPORTANT: Small models like tinyllama have ~2K context
        // Keep sample to ~400 chars to leave room for prompt + response
        var sample = firstChunk.Content.Length > 400 
            ? firstChunk.Content[..400] 
            : firstChunk.Content;
        
        // Ultra-compact prompt for small context models
        var prompt = $"""
            Is this FICTION or TECHNICAL? Answer ONE word.
            
            {sample}
            
            Answer:
            """;
        
        try
        {
            // Use the classifier model (small/fast) instead of main model
            var classifierModel = _ollama.ClassifierModel;
            if (_verbose) Console.WriteLine($"[Sentinel] Using {classifierModel} for classification");
            
            var response = await _ollama.GenerateWithModelAsync(classifierModel, prompt, temperature: 0.1);
            var cleaned = response.Trim().ToUpperInvariant().Split('\n')[0].Trim();
            
            // Simple binary classification
            var isFiction = cleaned.Contains("FICTION") || cleaned.Contains("NOVEL") || cleaned.Contains("STORY");
            var isTechnical = cleaned.Contains("TECHNICAL") || cleaned.Contains("TEXTBOOK") || 
                              cleaned.Contains("MANUAL") || cleaned.Contains("DOCUMENT") ||
                              cleaned.Contains("EDUCATION") || cleaned.Contains("NON-FICTION") ||
                              cleaned.Contains("NONFICTION");
            
            DocumentType docType;
            if (isFiction && !isTechnical)
                docType = DocumentType.Fiction;
            else if (isTechnical && !isFiction)
                docType = DocumentType.Technical;
            else
                docType = DocumentType.Unknown;
            
            if (_verbose) Console.WriteLine($"[Sentinel] Response: '{cleaned}' → {docType}");
            
            return new DocumentClassification(
                docType,
                docType == DocumentType.Unknown ? 0.3 : 0.85,
                [$"{classifierModel}:{cleaned}"],
                "llm");
        }
        catch
        {
            // If LLM fails, return unknown
            return new DocumentClassification(DocumentType.Unknown, 0, [], "llm-error");
        }
    }
    
    /// <summary>
    /// Hybrid classification: fast heuristics first, LLM fallback if uncertain
    /// </summary>
    private async Task<DocumentClassification> ClassifyDocumentHybridAsync(List<DocumentChunk> chunks, bool useLlmFallback = true)
    {
        // First try fast heuristics
        var heuristicResult = ClassifyDocument(chunks);
        
        if (_verbose)
        {
            Console.WriteLine($"[Classify] Heuristic: {heuristicResult.Type} ({heuristicResult.Confidence:P0}) " +
                $"[{string.Join(", ", heuristicResult.Indicators.Take(3))}]");
        }
        
        // If high confidence heuristic, use it directly
        if (heuristicResult.IsHighConfidence)
            return heuristicResult;
        
        // For uncertain cases (low confidence OR Unknown), use sentinel LLM
        // The sentinel is fast (single chunk, simple question) and more accurate than heuristics
        if (useLlmFallback && (heuristicResult.IsLowConfidence || heuristicResult.Type == DocumentType.Unknown))
        {
            if (_verbose) Console.WriteLine("[Classify] Uncertain, asking sentinel LLM...");
            
            var llmResult = await ClassifyDocumentWithLlmAsync(chunks);
            
            if (_verbose)
            {
                Console.WriteLine($"[Classify] Sentinel: {llmResult.Type} ({llmResult.Confidence:P0})");
            }
            
            // If sentinel gives a clear answer, use it
            if (llmResult.Type != DocumentType.Unknown)
                return llmResult;
        }
        
        // Fall back to heuristic result (even if low confidence)
        return heuristicResult;
    }
    
    /// <summary>
    /// Override document type based on template selection when classification is uncertain
    /// This allows user intent (choosing bookreport = fiction, technical = technical) to guide processing
    /// </summary>
    private static DocumentClassification OverrideDocTypeFromTemplate(DocumentClassification classification, string? templateName)
    {
        if (string.IsNullOrEmpty(templateName))
            return classification;
        
        // Only override if classification is uncertain (low confidence) or Unknown
        // High-confidence classifications should be trusted
        if (classification.IsHighConfidence && classification.Type != DocumentType.Unknown)
            return classification;
        
        var templateLower = templateName.ToLowerInvariant();
        
        // Template → DocumentType mapping
        var inferredType = templateLower switch
        {
            "bookreport" or "book-report" or "book" => DocumentType.Fiction,
            "technical" or "tech" => DocumentType.Technical,
            "academic" => DocumentType.Academic,
            "meeting" or "meetingnotes" or "notes" => DocumentType.Business,
            _ => (DocumentType?)null
        };
        
        if (inferredType == null)
            return classification;
        
        // If we inferred a type and classification was uncertain, use the template-inferred type
        return new DocumentClassification(
            inferredType.Value,
            Math.Max(classification.Confidence, 0.6), // Boost confidence slightly
            [.. classification.Indicators, $"template:{templateName}"],
            "template-override");
    }
    
    /// <summary>
    /// Adaptive parameters derived from configured summary length tiers
    /// </summary>
    private record DocumentSizeProfile(
        SummaryLengthTier Tier,
        int WordCount,
        int ChunkCount)
    {
        public static DocumentSizeProfile FromConfig(SummaryLengthConfig config, int wordCount, int chunkCount)
        {
            var tier = config.GetOrderedTiers().FirstOrDefault(t => wordCount <= t.MaxWords) ?? config.VeryLarge;
            return new DocumentSizeProfile(tier, wordCount, chunkCount);
        }

        public string SizeCategory => Tier.Name;
        public int TopicCount => Math.Max(1, Math.Min(Tier.Topics, ChunkCount));
        public int ChunksPerTopic => Math.Max(1, Math.Min(Tier.ChunksPerTopic, ChunkCount));
        public int MaxCharacters => Math.Max(1, Tier.MaxCharacters);
        public int MaxLocations => Math.Max(0, Tier.MaxLocations);
        public int MaxOther => Math.Max(0, Tier.MaxOther);
        public int BulletCount => Math.Max(1, Tier.BulletCount);
        public int WordsPerBullet => Math.Max(8, Tier.WordsPerBullet);
        public int TopClaimsCount => Math.Max(3, Tier.TopClaims);
    }

    private readonly bool _deleteCollectionAfterSummarization;
    private readonly int _maxParallelism;
    private readonly OllamaService _ollama;
    private readonly IEmbeddingService _embedder;
    private readonly bool _useOnnxEmbedding;
    private readonly int _vectorSize;
    private readonly QdrantHttpClient _qdrant;
    private readonly bool _verbose;
    private readonly TextAnalysisService _textAnalysis;
    private readonly SummaryLengthConfig _lengthConfig;

    public RagSummarizer(
        OllamaService ollama,
        IEmbeddingService embedder,
        string qdrantHost = "localhost",
        bool verbose = false,
        int maxParallelism = DefaultMaxParallelism,
        QdrantConfig? qdrantConfig = null,
        SummaryTemplate? template = null,
        TextAnalysisService? textAnalysis = null,
        SummaryLengthConfig? lengthConfig = null)
    {
        _ollama = ollama;
        _embedder = embedder;
        _useOnnxEmbedding = embedder is not OllamaEmbeddingService;
        _textAnalysis = textAnalysis ?? new TextAnalysisService();
        _lengthConfig = lengthConfig ?? new SummaryLengthConfig();

        // Use HTTP client instead of gRPC - gRPC has AOT compatibility issues with System.Single marshalling
        var port = qdrantConfig?.Port ?? 6333; // REST port
        var apiKey = qdrantConfig?.ApiKey;
        _qdrant = new QdrantHttpClient(qdrantHost, port, apiKey);

        _verbose = verbose;
        _maxParallelism = maxParallelism > 0 ? maxParallelism : DefaultMaxParallelism;
        _deleteCollectionAfterSummarization = qdrantConfig?.DeleteCollectionAfterSummarization ?? true;
        _vectorSize = embedder.EmbeddingDimension;
        Template = template ?? SummaryTemplate.Presets.Default;
    }

    /// <summary>
    ///     Current template being used
    /// </summary>
    public SummaryTemplate Template { get; private set; }

    /// <summary>
    ///     Set the template for summarization
    /// </summary>
    public void SetTemplate(SummaryTemplate template)
    {
        Template = template;
    }

    /// <summary>
    ///     Generate a unique collection name for a document to prevent collisions
    /// </summary>
    private static string GetCollectionName(string docId)
    {
        // Create a short hash of the docId to ensure unique collection per document
        var hash = ContentHasher.ComputeHash(docId);
        return $"{CollectionPrefix}{hash[..12]}";
    }

    /// <summary>
    ///     Delete the collection for a document
    /// </summary>
    private async Task DeleteCollectionAsync(string docId)
    {
        var collectionName = GetCollectionName(docId);
        try
        {
            await _qdrant.DeleteCollectionAsync(collectionName);
            if (_verbose) Console.WriteLine($"[Cleanup] Deleted collection {collectionName}");
        }
        catch (Exception ex)
        {
            if (_verbose) Console.WriteLine($"[Cleanup] Failed to delete collection {collectionName}: {ex.Message}");
        }
    }

    public async Task IndexDocumentAsync(string docId, List<DocumentChunk> chunks)
    {
        var collectionName = GetCollectionName(docId);
        await EnsureCollectionAsync(collectionName);

        // Adaptive batch size based on document size:
        // - Larger batches for big docs (amortize API overhead)
        // - Smaller batches for small docs (faster feedback)
        var batchSize = chunks.Count switch
        {
            < 20 => 5,
            < 100 => 10,
            < 500 => 20,
            _ => 50
        };
        var batch = new List<QdrantPoint>(batchSize);

        // Initialize embedder (downloads ONNX models on first use)
        await _embedder.InitializeAsync();
        
        var backendName = _useOnnxEmbedding ? "ONNX" : "Ollama";
        
        if (_verbose)
        {
            VerboseHelper.Log($"Embedding {chunks.Count} chunks ({backendName})...");
        }
        
        // Use non-interactive embedding (no progress bar in library mode)
        await EmbedChunksWithoutProgressAsync(chunks, docId, collectionName, batch, batchSize);
    }
    
    /// <summary>
    /// Embed chunks without Spectre progress display (for batch mode)
    /// </summary>
    private async Task EmbedChunksWithoutProgressAsync(
        List<DocumentChunk> chunks,
        string docId,
        string collectionName,
        List<QdrantPoint> batch,
        int batchSize)
    {
        for (var i = 0; i < chunks.Count; i++)
        {
            // Only add delays for Ollama - ONNX is local and fast
            if (!_useOnnxEmbedding && i > 0)
            {
                var baseDelay = 500;
                var jitter = Random.Shared.Next(0, 500);
                await Task.Delay(baseDelay + jitter);
            }

            var chunk = chunks[i];
            var embedding = await _embedder.EmbedAsync(chunk.Content);

            if (embedding.Length != _vectorSize)
                throw new InvalidOperationException(
                    $"Embedding dimension mismatch: expected {_vectorSize}, got {embedding.Length}.");

            var pointId = GenerateStableId(docId, chunk.Hash, chunk.Order);
            var truncatedContent = chunk.Content.Length > 2000
                ? chunk.Content[..2000]
                : chunk.Content;

            batch.Add(new QdrantPoint
            {
                Id = pointId.ToString(),
                Vector = embedding,
                Payload = new Dictionary<string, object>
                {
                    ["docId"] = docId,
                    ["chunkId"] = chunk.Id,
                    ["heading"] = chunk.Heading ?? "",
                    ["headingLevel"] = chunk.HeadingLevel,
                    ["order"] = chunk.Order,
                    ["content"] = truncatedContent,
                    ["hash"] = chunk.Hash,
                    ["pageStart"] = chunk.PageStart?.ToString() ?? string.Empty,
                    ["pageEnd"] = chunk.PageEnd?.ToString() ?? string.Empty
                }
            });

            if (batch.Count >= batchSize)
            {
                await _qdrant.UpsertAsync(collectionName, batch);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await _qdrant.UpsertAsync(collectionName, batch);
            batch.Clear();
        }
    }

    public async Task<DocumentSummary> SummarizeAsync(
        string docId,
        List<DocumentChunk> chunks,
        string? focusQuery = null)
    {
        var sw = Stopwatch.StartNew();
        var collectionName = GetCollectionName(docId);
        
        // Get adaptive parameters based on document size
        var totalWords = CountWords(chunks);
        var profile = DocumentSizeProfile.FromConfig(_lengthConfig, totalWords, chunks.Count);
        if (_verbose)
        {
            Console.WriteLine($"[Profile] {profile.SizeCategory} document ({chunks.Count} chunks / {totalWords:N0} words) → {profile.TopicCount} topics, {profile.ChunksPerTopic} chunks/topic");
        }

        var chunkLookup = chunks.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

        // Use sentinel LLM for classification when heuristics are uncertain
        // The sentinel is fast (tiny model + minimal prompt) so always enable it
        var docType = await ClassifyDocumentHybridAsync(chunks, useLlmFallback: true);
        
        // Template can override document type when there's a strong semantic match
        // This handles cases where user explicitly chooses bookreport for fiction or technical for docs
        docType = OverrideDocTypeFromTemplate(docType, Template.Name);
        
        if (_verbose) Console.WriteLine($"[DocType] {docType.Type} ({docType.Confidence:P0}, {docType.Method})");

        try
        {
            // Extract document title/author from early content for grounding
            var documentTitle = ExtractDocumentTitle(chunks, docId);
            if (_verbose && !string.IsNullOrEmpty(documentTitle)) 
                Console.WriteLine($"[Title] Detected: {documentTitle}");
            
            // Build TF-IDF index from all chunks to identify distinctive vs common terms
            // This helps us classify claims as fact/inference/colour later
            if (_verbose) Console.WriteLine("[TF-IDF] Building term frequency index...");
            _textAnalysis.BuildTfIdfIndex(chunks.Select(c => c.Content));
            
            // Index first
            await IndexDocumentAsync(docId, chunks);

            // Extract topics - constrain to actual headings where possible
            var headings = chunks.Select(c => c.Heading).Where(h => !string.IsNullOrEmpty(h)).ToList();
            var topics = await ExtractTopicsAsync(headings, profile.TopicCount, docType.Type);
            
            // Fallback: if topic extraction failed, use headings directly
            if (topics.Count == 0 && headings.Count > 0)
            {
                if (_verbose) Console.WriteLine("[Topics] Extraction failed, falling back to headings");
                topics = headings.Take(profile.TopicCount).ToList();
            }

            if (_verbose) Console.WriteLine($"[Topics] Using {topics.Count} topics: {string.Join(", ", topics.Take(3))}{(topics.Count > 3 ? "..." : "")}");

            // Retrieve and summarize per topic - run in parallel for speed
            var allRetrievedChunks = new HashSet<string>();
            var allEntities = new List<ExtractedEntities>();
            var claimLedger = new ClaimLedger();

            // First, retrieve chunks for all topics in parallel (embeddings are fast)
            var retrievalTasks = topics.Select(async topic =>
            {
                var query = focusQuery != null ? $"{topic} {focusQuery}" : topic;
                var retrieved = await RetrieveChunksAsync(collectionName, query, profile.ChunksPerTopic, chunkLookup);
                return (topic, retrieved);
            }).ToList();

            var retrievalResults = await Task.WhenAll(retrievalTasks);

            // Now synthesize topics in parallel (LLM calls - this is the slow part)
            var templateName = Template.Name;
            var synthesizeTasks = retrievalResults.Select(async result =>
            {
                var topic = result.topic;
                var retrieved = result.retrieved;
                var (summary, entities, claims) = await SynthesizeTopicWithClaimsAsync(topic, retrieved, focusQuery, docType.Type, templateName);

                if (_verbose) Console.WriteLine($"  [{topic}] Retrieved {retrieved.Count} chunks, {claims.Count} claims");

                return (topic, summary, entities, claims, chunkIds: retrieved.Select(c => c.Id).ToList());
            }).ToList();

            var synthesisResults = await Task.WhenAll(synthesizeTasks);

            // Build results maintaining topic order - clean up meta-commentary from summaries
            var topicSummaries = synthesisResults
                .Select(r => new TopicSummary(CleanTopicName(r.topic), CleanTopicSummary(r.summary), r.chunkIds))
                .ToList();


            foreach (var result in synthesisResults)
            {
                if (result.entities != null)
                    allEntities.Add(result.entities);
                
                // Add claims to ledger for weighted synthesis
                claimLedger.AddRange(result.claims);
            }

            foreach (var result in retrievalResults)
            foreach (var c in result.retrieved)
                allRetrievedChunks.Add(c.Id);

            // Clear retrieval results to free memory - we've extracted what we need
            retrievalResults = null!;
            synthesisResults = null!;
            
            // Deduplicate claims using semantic similarity
            var deduplicatedClaims = _textAnalysis.DeduplicateClaims(claimLedger.Claims.ToList());
            if (_verbose) Console.WriteLine($"[Claims] {claimLedger.Claims.Count} claims → {deduplicatedClaims.Count} after dedup");
            
            // Merge all extracted entities with fuzzy deduplication BEFORE executive summary
            // so we can use them for grounding
            var mergedEntities = allEntities.Count > 0 
                ? NormalizeAndMergeEntities(allEntities) 
                : null;
            
            if (_verbose && mergedEntities != null)
            {
                Console.WriteLine($"[Entities] Extracted: {mergedEntities.Characters.Count} characters, " +
                    $"{mergedEntities.Locations.Count} locations, {mergedEntities.Events.Count} events");
            }
            
            // Create executive summary using weighted claims AND entities for grounding
            var validChunkIds = chunkLookup.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var executiveRaw = await CreateGroundedExecutiveSummaryAsync(
                topicSummaries,
                deduplicatedClaims,
                mergedEntities,
                focusQuery,
                profile,
                documentTitle ?? docId,
                docType.Type,
                validChunkIds);


            // Clear intermediate data to free memory before building result
            deduplicatedClaims.Clear();
            allEntities.Clear();
            
            sw.Stop();

            // Coverage = % of top-level headings that appear in at least one retrieved chunk
            var topLevelHeadings = chunks
                .Where(c => c.HeadingLevel <= 2 && !string.IsNullOrEmpty(c.Heading))
                .Select(c => c.Heading)
                .ToList();
            var retrievedHeadings = chunks
                .Where(c => allRetrievedChunks.Contains(c.Id))
                .Select(c => c.Heading)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var coverage = topLevelHeadings.Count > 0
                ? (double)topLevelHeadings.Count(h => retrievedHeadings.Contains(h)) / topLevelHeadings.Count
                : 1.0;
            var citationRate = CalculateCitationRate(executiveRaw);

            // Convert chunk citations to user-friendly page references
            var executive = ReplaceChunkCitations(executiveRaw, chunkLookup);
            var finalTopicSummaries = topicSummaries
                .Select(ts => ts with { Summary = ReplaceChunkCitations(ts.Summary, chunkLookup) })
                .ToList();
            
            // Build chunk index for output - cap for very large docs to save memory
            var chunkIndex = chunks.Count > 500 
                ? chunks.Take(100).Concat(chunks.Skip(chunks.Count - 100)).Select(ChunkIndexEntry.FromChunk).ToList()
                : chunks.Select(ChunkIndexEntry.FromChunk).ToList();

            // Apply entity caps from profile to final output
            var cappedEntities = mergedEntities != null 
                ? CapEntities(mergedEntities, profile)
                : null;

            return new DocumentSummary(
                executive,
                finalTopicSummaries,
                [],
                new SummarizationTrace(
                    docId, chunks.Count, allRetrievedChunks.Count,
                    topics, sw.Elapsed, coverage, citationRate, chunkIndex),
                cappedEntities);
        }
        finally
        {
            // Clean up collection after summarization (unless configured to keep it)
            if (_deleteCollectionAfterSummarization) await DeleteCollectionAsync(docId);
        }
    }
    
    /// <summary>
    /// Apply entity caps from profile to prevent bloated output
    /// </summary>
    private static ExtractedEntities CapEntities(ExtractedEntities entities, DocumentSizeProfile profile)
    {
        return new ExtractedEntities(
            entities.Characters.Take(profile.MaxCharacters).ToList(),
            entities.Locations.Take(profile.MaxLocations).ToList(),
            entities.Dates.Take(profile.MaxOther).ToList(),
            entities.Events.Take(profile.MaxOther).ToList(),
            entities.Organizations.Take(profile.MaxOther).ToList());
    }

    private async Task<List<string>> ExtractTopicsAsync(List<string> headings, int maxTopics = 5, DocumentType docType = DocumentType.Unknown)
    {
        // For small docs or technical content, just use the headings directly
        // Topic extraction adds value mainly for fiction/narrative where chapters don't map to themes
        if (headings.Count <= maxTopics || docType is DocumentType.Technical or DocumentType.Reference)
        {
            return headings.Take(maxTopics).ToList();
        }
        
        // For small models, limit headings to avoid overwhelming context
        var limitedHeadings = headings.Take(15).ToList();

        var prompt = docType == DocumentType.Fiction 
            ? $"""
              Extract {maxTopics} main THEMES from these chapter headings:
              {string.Join(", ", limitedHeadings)}

              Rules:
              - Output ONE theme per line
              - Each theme max 6 words
              - No bullets, numbers, or explanations
              - Focus on abstract themes, not plot events
              
              Example: "Social class and marriage", "Family duty vs personal desire"
              """
            : $"""
              List {maxTopics} KEY TOPICS from these section headings:
              {string.Join(", ", limitedHeadings)}

              Output ONE topic per line. Max 6 words each. No bullets or numbers.
              """;

        var response = await _ollama.GenerateAsync(prompt);
        return response.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().TrimStart('-', '*', '•', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ')', ' '))
            .Where(t => t.Length > 2 && t.Length < 60)
            .Where(t => !IsTopicMetaCommentary(t))
            .Take(maxTopics)
            .ToList();
    }
    
    /// <summary>
    /// Detect meta-commentary in topic extraction that should be filtered out
    /// </summary>
    private static bool IsTopicMetaCommentary(string line)
    {
        var metaPatterns = new[]
        {
            @"^(here are|here is|the following|below are)",
            @"^(example|e\.g\.|for example|such as)",
            @"(key topics?|main themes?|extracted|identified)\s*[:.]?\s*$",
            @"^\d+\s+(key|main|important)",
            @"^(based on|from the|according to)",
            @"^(note:|disclaimer|caveat)",
        };
        
        var lower = line.ToLowerInvariant();
        return metaPatterns.Any(p => Regex.IsMatch(lower, p, RegexOptions.IgnoreCase));
    }

    private async Task<List<DocumentChunk>> RetrieveChunksAsync(
        string collectionName,
        string query,
        int topK,
        IReadOnlyDictionary<string, DocumentChunk> chunkLookup)
    {
        var queryEmbedding = await _embedder.EmbedAsync(query);

        // Request extra chunks to account for boilerplate filtering
        var results = await _qdrant.SearchAsync(collectionName, queryEmbedding, topK + 3);
        var retrieved = new List<DocumentChunk>();

        foreach (var result in results)
        {
            if (retrieved.Count >= topK)
                break;
                
            var payload = result.GetPayloadStrings();
            var chunkId = payload.GetValueOrDefault("chunkId", "");
            var content = payload.GetValueOrDefault("content", "");
            
            // Skip boilerplate content (Project Gutenberg headers, licenses, etc.)
            if (MapReduceSummarizer.IsBoilerplate(content))
                continue;

            if (!string.IsNullOrEmpty(chunkId) && chunkLookup.TryGetValue(chunkId, out var existing))
            {
                // Also check the full content from lookup for boilerplate
                if (!MapReduceSummarizer.IsBoilerplate(existing.Content))
                    retrieved.Add(existing);
                continue;
            }

            var heading = payload.GetValueOrDefault("heading", "");
            var hash = payload.GetValueOrDefault("hash", "");
            var orderStr = payload.GetValueOrDefault("order", "0");
            var headingLevelStr = payload.GetValueOrDefault("headingLevel", "1");
            var pageStartStr = payload.GetValueOrDefault("pageStart", "");
            var pageEndStr = payload.GetValueOrDefault("pageEnd", "");

            int.TryParse(orderStr, out var order);
            int.TryParse(headingLevelStr, out var headingLevel);
            int? pageStart = int.TryParse(pageStartStr, out var ps) ? ps : null;
            int? pageEnd = int.TryParse(pageEndStr, out var pe) ? pe : null;

            retrieved.Add(new DocumentChunk(order, heading, headingLevel, content, hash, pageStart, pageEnd));
        }

        return retrieved;
    }
    
    /// <summary>
    /// Build chunk context for synthesis prompts. Includes chunk identifiers for citations
    /// and page hints for better grounding.
    /// </summary>
    private static string BuildChunkContext(List<DocumentChunk> chunks, int maxContentPerChunk)
    {
        return string.Join("\n", chunks.Select(chunk =>
        {
            var truncated = chunk.Content.Length > maxContentPerChunk
                ? chunk.Content[..maxContentPerChunk] + "..."
                : chunk.Content;
            var pageHint = chunk.PageStart.HasValue ? $" (p.{chunk.PageStart})" : string.Empty;
            return $"[{chunk.Id}]{pageHint}: {truncated}";
        }));
    }

    private async Task<(string summary, ExtractedEntities? entities)> SynthesizeTopicWithEntitiesAsync(
        string topic,
        List<DocumentChunk> chunks,
        string? focus,
        DocumentType docType,
        string? templateName = null)
    {
        var maxContentPerChunk = chunks.Count switch
        {
            <= 2 => 800,
            <= 4 => 500,
            _ => 350
        };

        var context = BuildChunkContext(chunks, maxContentPerChunk);
        
        // Get summary with tight prompt (no entity extraction mixed in)
        var summaryPrompt = GetTopicSynthesisPrompt(topic, context, focus, docType, templateName);
        var summaryResponse = await _ollama.GenerateAsync(summaryPrompt);
        var summary = CleanSynthesisResponse(summaryResponse);
        
        // Extract entities separately with focused prompt (only for fiction/bookreport)
        ExtractedEntities? entities = null;
        if (templateName?.Equals("bookreport", StringComparison.OrdinalIgnoreCase) == true ||
            docType == DocumentType.Fiction)
        {
            entities = await ExtractEntitiesFromChunksAsync(chunks, maxContentPerChunk);
        }
        
        return (summary, entities);
    }
    
    /// <summary>
    /// Clean synthesis response - strip any meta-commentary the LLM added despite instructions
    /// </summary>
    private static string CleanSynthesisResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return "";
        
        // Take only the first substantive line (ignore any headers or meta)
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Skip meta-commentary lines
            if (IsMetaCommentary(trimmed)) continue;
            // Skip empty or very short lines
            if (trimmed.Length < 15) continue;
            // Skip lines that look like entity headers
            if (Regex.IsMatch(trimmed, @"^(Characters|Locations|Events|ENTITIES):", RegexOptions.IgnoreCase)) continue;
            
            // Found a good line - clean it up and return
            return RemoveHedgingInline(trimmed);
        }
        
        return response.Trim();
    }
    
    /// <summary>
    /// Extract entities from chunks with a separate, focused prompt
    /// </summary>
    private async Task<ExtractedEntities?> ExtractEntitiesFromChunksAsync(List<DocumentChunk> chunks, int maxContentPerChunk)
    {
        // Use smaller content sample for entity extraction
        var sample = string.Join("\n", chunks.Take(3).Select(c => 
            c.Content.Length > maxContentPerChunk / 2 
                ? c.Content[..(maxContentPerChunk / 2)] 
                : c.Content));
        
        var prompt = $"""
            Text:
            {sample}
            ---
            Extract from text above. One line each, comma-separated:
            Characters: [names]
            Locations: [places]
            Write "none" if not found. No other text.
            """;
        
        var response = await _ollama.GenerateAsync(prompt);
        return ParseCompactEntityResponse(response);
    }
    
    /// <summary>
    /// Parse compact entity response format - handles both | and newline separators
    /// </summary>
    private static ExtractedEntities? ParseCompactEntityResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;
        
        var characters = new List<string>();
        var locations = new List<string>();
        var events = new List<string>();
        
        // First try to split by newline (more common), then by |
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx < 0) continue;
            
            var type = trimmed[..colonIdx].Trim().ToLowerInvariant();
            var valuesRaw = trimmed[(colonIdx + 1)..];
            
            // Clean up common LLM artifacts
            valuesRaw = Regex.Replace(valuesRaw, @"\[|\]|\*\*?", "");
            valuesRaw = Regex.Replace(valuesRaw, @"\s*-\s*", ", ");
            
            var values = valuesRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim().Trim('"', '\'', '*', '[', ']', '-'))
                .Where(v => v.Length > 1 && 
                       !v.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                       !v.StartsWith("Location", StringComparison.OrdinalIgnoreCase) &&
                       !v.StartsWith("Event", StringComparison.OrdinalIgnoreCase) &&
                       !v.StartsWith("Character", StringComparison.OrdinalIgnoreCase))
                .Where(IsValidEntityEntry)
                .ToList();
            
            if (type.Contains("character")) characters.AddRange(values);
            else if (type.Contains("location")) locations.AddRange(values);
            else if (type.Contains("event")) events.AddRange(values);
        }
        
        if (characters.Count == 0 && locations.Count == 0 && events.Count == 0)
            return null;
        
        return new ExtractedEntities(
            characters.Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList(),
            locations.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList(),
            [],
            events.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList(),
            []);
    }
    
    /// <summary>
    /// Generate ultra-tight prompts for topic synthesis - minimal tokens, maximum signal
    /// Enforces strict grounding in source text
    /// </summary>
    private static string GetTopicSynthesisPrompt(string topic, string context, string? focus, DocumentType docType, string? templateName = null)
    {
        var focusLine = focus != null ? $"(Focus:{focus})" : "";
        
        // Get document-type-specific instruction
        var instruction = GetDocTypeInstruction(docType, templateName);
        
        // Ultra-compact prompt with strict grounding
        return $"""
            Topic:{topic} {focusLine}
            ---
            {context}
            ---
            Write 1 sentence: {instruction} Use [chunk-N] citation.
            ONLY facts from text above. Third person. NO lists. NO headers.
            """;
    }
    
    /// <summary>
    /// Get compact instruction based on document type
    /// </summary>
    private static string GetDocTypeInstruction(DocumentType docType, string? templateName)
    {
        // Bookreport: extract plot from THIS text only
        if (templateName?.Equals("bookreport", StringComparison.OrdinalIgnoreCase) == true)
            return "What happens in THIS text? Name characters, describe action.";
        
        return docType switch
        {
            DocumentType.Fiction => "Theme insight from THIS text.",
            DocumentType.Technical => "What it does (from text).",
            DocumentType.Academic => "Key finding stated in text.",
            DocumentType.Business => "Business impact from text.",
            DocumentType.Legal => "Legal effect stated.",
            DocumentType.News => "What happened (from text).",
            DocumentType.Reference => "Key concept from text.",
            _ => "Main point from text."
        };
    }
    
    private static (string summary, ExtractedEntities? entities) ParseSummaryAndEntities(string response)
    {
        var summary = response;
        ExtractedEntities? entities = null;
        
        // Try to extract structured entities from response
        // Look for SUMMARY: followed by content, stopping at ENTITIES:, **ENTITIES, or end
        var summaryMatch = Regex.Match(response, 
            @"SUMMARY:\s*(.+?)(?=\n\s*\*{0,2}ENTITIES|\n\s*Characters:|\n\s*\*\*Characters|$)", 
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        
        // Match ENTITIES block - can start with ENTITIES:, **ENTITIES**, or go straight to entity types
        var entitiesMatch = Regex.Match(response, 
            @"(?:ENTITIES:\s*|^\s*\*{0,2}ENTITIES\*{0,2}\s*\n?)(.+)$", 
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        
        if (summaryMatch.Success)
        {
            summary = summaryMatch.Groups[1].Value.Trim();
            // Clean up any trailing ** or markdown artifacts
            summary = Regex.Replace(summary, @"\s*\*{2,}\s*$", "").Trim();
        }
        else
        {
            // No explicit SUMMARY: marker - try to find content before ENTITIES or entity type markers
            var beforeEntities = Regex.Match(response, 
                @"^(.+?)(?=\n\s*\*{0,2}ENTITIES|\n\s*\*{0,2}Characters:|\n\s*- \*\*Characters)", 
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (beforeEntities.Success)
            {
                summary = beforeEntities.Groups[1].Value.Trim();
            }
        }
        
        if (entitiesMatch.Success)
        {
            var entitiesText = entitiesMatch.Groups[1].Value;
            entities = ParseEntitiesBlock(entitiesText);
        }
        else
        {
            // Try direct entity type matching without ENTITIES: header
            var directEntityMatch = Regex.Match(response, 
                @"(?:\n|^)\s*\*{0,2}Characters\*{0,2}:\s*.+", 
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (directEntityMatch.Success)
            {
                entities = ParseEntitiesBlock(directEntityMatch.Value);
            }
        }
        
        // Final cleanup: remove any ENTITIES text that leaked into summary
        summary = Regex.Replace(summary, @"\n\s*\*{0,2}(Characters|Locations|Dates|Events|Organizations)\*{0,2}:.*$", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        summary = summary.Trim();
        
        return (summary, entities);
    }
    
    private static ExtractedEntities ParseEntitiesBlock(string entitiesText)
    {
        var characters = ExtractEntityList(entitiesText, "Characters");
        var locations = ExtractEntityList(entitiesText, "Locations");
        var dates = ExtractEntityList(entitiesText, "Dates");
        var events = ExtractEntityList(entitiesText, "Events");
        var organizations = ExtractEntityList(entitiesText, "Organizations");
        
        return new ExtractedEntities(characters, locations, dates, events, organizations);
    }
    
    private static List<string> ExtractEntityList(string text, string entityType)
    {
        // Define the list of all entity types for boundary detection
        var allEntityTypes = new[] { "Characters", "Locations", "Dates", "Events", "Organizations" };
        var otherTypes = allEntityTypes.Where(t => !t.Equals(entityType, StringComparison.OrdinalIgnoreCase)).ToArray();
        var boundaryPattern = string.Join("|", otherTypes);
        
        // Try multiple patterns - LLMs format these inconsistently
        // The boundary now explicitly stops at other entity type labels
        var patterns = new[]
        {
            $@"{entityType}:\s*\**\s*(.+?)(?=\n\**\s*({boundaryPattern}):|\n---|\nRules:|\nENTITIES|$)",
            $@"\*\*{entityType}:\*\*\s*(.+?)(?=\n\*\*\s*({boundaryPattern})|\n---|\nRules:|\nENTITIES|$)",
            $@"- \*\*{entityType}\*\*:\s*(.+?)(?=\n- \*\*\s*({boundaryPattern})|\n---|\nRules:|\nENTITIES|$)"
        };
        
        string? value = null;
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (match.Success)
            {
                value = match.Groups[1].Value.Trim();
                break;
            }
        }
        
        if (string.IsNullOrWhiteSpace(value)) return [];
        
        // Handle "none" or empty
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("n/a", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }
        
        // Clean up markdown artifacts and common parser failures
        value = Regex.Replace(value, @"\*\*|\[\d+\]|\[chunk-\d+\]|\[chunk-N\]?", ""); // Remove ** and citations
        value = Regex.Replace(value, @"\n\s*-\s*", ", "); // Convert bullet lists to comma-separated
        value = Regex.Replace(value, @"\d+\.\s*(Locations?|Events?|Characters?|Dates?|Organizations?|Orgs?):\s*", ""); // Remove section headers
        value = Regex.Replace(value, @"Citation:\s*", ""); // Remove Citation: prefix
        value = Regex.Replace(value, @"\(not specified[^)]*\)", ""); // Remove "(not specified...)"
        value = Regex.Replace(value, @"but not specified.*$", "", RegexOptions.IgnoreCase); // Remove trailing qualifiers
        
        // Split by comma, semicolon, or newline and clean up
        var rawEntities = value.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().Trim('[', ']', '"', '\'', '*', '-', '+', ' '))
            .Where(s => s.Length > 1) // Filter out single chars
            .ToList();
        
        // Apply strict filtering to remove parser failures
        return rawEntities
            .Where(IsValidEntityEntry)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
    
    /// <summary>
    /// Strict validation for entity entries - rejects pronouns, relational phrases, parser artifacts
    /// </summary>
    private static bool IsValidEntityEntry(string entity)
    {
        if (string.IsNullOrWhiteSpace(entity) || entity.Length < 2 || entity.Length > 100)
            return false;
        
        // Reject common parser failures and non-entity text
        var invalidPatterns = new[]
        {
            @"^(his|her|their|its|my|your|our)\s",    // Possessive pronouns  
            @"^(he|she|they|it|we|you)\s",            // Subject pronouns
            @"^(a|an|the|some|any|this|that)\s",      // Articles at start
            @"\b(brother|sister|mother|father|son|daughter|wife|husband|uncle|aunt|cousin)\b(?!\s+\w)", // Relationships without full name
            @"^(young|old|younger|older|eldest)\s",   // Adjective-only entries
            @"^\d+$",                                  // Just numbers
            @"^\[.*\]$",                              // Bracketed parser artifacts
            @"^(none|n/a|unknown|unnamed|not specified|not mentioned)$",
            @"(mentioned|described|referred|appears|seems)", // Meta-language
            @"^(the|a|an)\s.*family$",                // "the X family" - too vague
            @"^\w+\s+family\s*\([^)]*$",              // Incomplete parenthetical like "Lucas family (Sir William"
            // New patterns for parser artifacts
            @"^\d+\.\s*(Locations?|Events?|Characters?|Dates?|Organizations?):", // Numbered section headers
            @"^Citation:", // Citation headers
            @"\[chunk-", // Chunk references leaked through
            @"^\+\s*$", // Just a plus sign
            @"^but\s+not\s+specified", // Incomplete statements
        };
        
        return !invalidPatterns.Any(p => 
            Regex.IsMatch(entity, p, RegexOptions.IgnoreCase));
    }

    private async Task<string> CreateExecutiveSummaryAsync(
        List<TopicSummary> topicSummaries, string? focus)
    {
        // Truncate each topic summary for small models
        const int maxSummaryLength = 300;
        var summariesText = string.Join("\n", topicSummaries.Select(t =>
        {
            var truncated = t.Summary.Length > maxSummaryLength
                ? t.Summary[..maxSummaryLength] + "..."
                : t.Summary;
            return $"- {t.Topic}: {truncated}";
        }));

        var prompt = Template.GetExecutivePrompt(summariesText, focus);

        return await _ollama.GenerateAsync(prompt);
    }
    
    /// <summary>
    /// Create executive summary using weighted claims AND extracted entities for grounding
    /// Enforces strict grounding - only information from the source text
    /// </summary>
    private async Task<string> CreateGroundedExecutiveSummaryAsync(
        List<TopicSummary> topicSummaries,
        List<Claim> weightedClaims,
        ExtractedEntities? entities,
        string? focus,
        DocumentSizeProfile? profile = null,
        string? documentLabel = null,
        DocumentType? documentType = null,
        HashSet<string>? validChunkIds = null)
    {
        profile ??= DocumentSizeProfile.FromConfig(_lengthConfig, 4000, 50);
        validChunkIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var topClaims = weightedClaims
            .OrderByDescending(c => c.Weight)
            .Take(profile.TopClaimsCount)
            .ToList();

        var topicContext = string.Join("\n", topicSummaries.Select(t =>
        {
            var truncated = t.Summary.Length > 200 ? t.Summary[..200] + "..." : t.Summary;
            return $"- {t.Topic}: {truncated}";
        }));

        var claimsByType = topClaims.GroupBy(c => c.Type).OrderByDescending(g => (int)g.Key);
        var claimContext = new StringBuilder();

        foreach (var group in claimsByType)
        {
            var typeLabel = group.Key switch
            {
                ClaimType.Fact => "Key Facts",
                ClaimType.Inference => "Key Inferences",
                _ => "Supporting Details"
            };

            claimContext.AppendLine($"\n{typeLabel}:");
            foreach (var claim in group.Take(3))
            {
                claimContext.AppendLine($"  - {claim.Render()}");
            }
        }

        var claimContextText = claimContext.ToString().Trim();
        var entityContext = BuildEntityContext(entities, profile);
        
        // Build evidence block with document title for grounding
        var titleLine = !string.IsNullOrEmpty(documentLabel) ? $"DOCUMENT: {documentLabel}\n\n" : "";
        
        // Tell the LLM which chunk IDs are valid
        var chunkHint = validChunkIds.Count > 0 
            ? $"Valid citations: [chunk-0] to [chunk-{validChunkIds.Count - 1}]\n\n"
            : "";
        
        var evidenceBlock = titleLine + chunkHint + BuildEvidenceBlock(topicContext, claimContextText, entityContext);

        // Add strict grounding instruction
        var groundingRule = $"""
            
            CRITICAL: Use ONLY information from the text above. Do NOT add:
            - Characters, events, or plot points not mentioned above
            - Details from other books or stories
            - Your own interpretation or speculation
            If unsure, omit. Write in third person.
            Only use citations [chunk-0] to [chunk-{Math.Max(0, validChunkIds.Count - 1)}].
            """;

        var templatePrompt = Template.GetExecutivePrompt(evidenceBlock, focus);
        var adaptiveGuide = Template.ExecutivePrompt == null
            ? BuildAdaptiveFormatGuide(Template, profile)
            : string.Empty;

        var prompt = string.IsNullOrWhiteSpace(adaptiveGuide)
            ? $"{templatePrompt}{groundingRule}"
            : $"{templatePrompt}\n\n{adaptiveGuide}{groundingRule}";

        var rawResponse = await _ollama.GenerateAsync(prompt);

        var cleanedSummary = CleanExecutiveSummary(rawResponse, Template, profile);
        
        // Validate citations against actual chunks - remove any hallucinated chunk IDs
        cleanedSummary = ValidateChunkCitations(cleanedSummary, validChunkIds);
        
        var expectedBullets = GetExpectedBulletCount(Template, profile);
        var summaryWithCitations = Template.IncludeCitations
            ? EnsureUniqueCitations(cleanedSummary, topClaims, topicSummaries, expectedBullets, validChunkIds)
            : cleanedSummary;

        return summaryWithCitations;
    }

    private static string BuildEntityContext(ExtractedEntities? entities, DocumentSizeProfile profile)
    {
        if (entities == null || !entities.HasAny)
            return string.Empty;

        // Compact format: single line per entity type, saves ~20 tokens
        var parts = new List<string>();
        
        if (entities.Characters.Count > 0)
            parts.Add($"Characters:{string.Join(",", entities.Characters.Take(profile.MaxCharacters))}");
        if (entities.Locations.Count > 0)
            parts.Add($"Locations:{string.Join(",", entities.Locations.Take(profile.MaxLocations))}");
        if (entities.Organizations.Count > 0)
            parts.Add($"Orgs:{string.Join(",", entities.Organizations.Take(profile.MaxOther))}");
        if (entities.Events.Count > 0)
            parts.Add($"Events:{string.Join(",", entities.Events.Take(profile.MaxOther))}");

        return parts.Count > 0 ? $"ENTITIES: {string.Join(" | ", parts)}" : string.Empty;
    }

    private static string BuildEvidenceBlock(string topicContext, string claimContext, string entityContext)
    {
        var builder = new StringBuilder();
        builder.AppendLine("TOPICS:");
        builder.AppendLine(topicContext);

        if (!string.IsNullOrWhiteSpace(claimContext))
        {
            builder.AppendLine();
            builder.AppendLine("CLAIMS:");
            builder.AppendLine(claimContext);
        }

        if (!string.IsNullOrWhiteSpace(entityContext))
        {
            builder.AppendLine();
            builder.AppendLine(entityContext);
        }

        return builder.ToString().Trim();
    }

    private static string BuildAdaptiveFormatGuide(SummaryTemplate template, DocumentSizeProfile profile)
    {
        // Compact format guide - saves ~30 tokens
        var bulletCount = GetExpectedBulletCount(template, profile);
        
        return template.OutputStyle switch
        {
            OutputStyle.Bullets or OutputStyle.CitationsOnly => 
                $"Format: {bulletCount} bullets, ≤{profile.WordsPerBullet} words each. Cite [chunk-N].",
            OutputStyle.Mixed => 
                $"Format: 1 paragraph (≤60 words) + {bulletCount} bullets. Cite [chunk-N].",
            _ when template.Paragraphs > 0 => 
                $"Format: {template.Paragraphs} paragraph(s), ≤{profile.BulletCount * profile.WordsPerBullet} words total.",
            _ => 
                $"Format: ≤{profile.BulletCount * profile.WordsPerBullet} words. Cite [chunk-N]."
        };
    }

    private static int GetExpectedBulletCount(SummaryTemplate template, DocumentSizeProfile profile)
    {
        if (template.MaxBullets > 0)
            return template.MaxBullets;

        return template.OutputStyle is OutputStyle.Bullets or OutputStyle.Mixed or OutputStyle.CitationsOnly
            ? profile.BulletCount
            : Math.Max(profile.BulletCount, 3);
    }

    /// <summary>
    /// Ensure each executive summary bullet has a unique chunk citation; fill in missing ones from claims/topics
    /// Only uses citations that reference actual chunks in the document
    /// </summary>
    private static string EnsureUniqueCitations(
        string summary,
        List<Claim> claims,
        List<TopicSummary> topics,
        int maxBullets,
        HashSet<string>? validChunkIds = null)
    {
        validChunkIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        var lines = summary.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd())
            .ToList();
        var processed = new List<string>();
        var usedChunkIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Only accept chunk IDs that exist in the document
        Queue<string> BuildCandidateQueue(IEnumerable<string> source) =>
            new(source
                .Where(IsValidChunkId)
                .Where(id => validChunkIds.Count == 0 || validChunkIds.Contains(id))
                .Select(id => id.Trim())
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.OrdinalIgnoreCase));

        var claimCandidates = BuildCandidateQueue(claims.SelectMany(c => c.Evidence).Select(e => e.ChunkId));
        var topicCandidates = BuildCandidateQueue(topics.SelectMany(t => t.SourceChunks ?? Enumerable.Empty<string>()));

        string? NextCandidate()
        {
            while (claimCandidates.Count > 0)
            {
                var id = claimCandidates.Dequeue();
                if (!usedChunkIds.Contains(id))
                    return id;
            }

            while (topicCandidates.Count > 0)
            {
                var id = topicCandidates.Dequeue();
                if (!usedChunkIds.Contains(id))
                    return id;
            }

            return null;
        }

        for (var i = 0; i < lines.Count && processed.Count < maxBullets; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var match = Regex.Match(line, @"\[(chunk-\d+)\]");
            string? assignedId = null;

            if (match.Success)
            {
                var existingId = match.Groups[1].Value;
                var isValidExisting = validChunkIds.Count == 0 || validChunkIds.Contains(existingId);
                
                if (isValidExisting && !usedChunkIds.Contains(existingId))
                {
                    usedChunkIds.Add(existingId);
                    assignedId = existingId;
                }
                else
                {
                    // Either invalid chunk ID or already used - replace with valid one
                    var replacement = NextCandidate();
                    if (!string.IsNullOrEmpty(replacement))
                    {
                        var replaced = false;
                        line = Regex.Replace(line, @"\[(chunk-\d+)\]", _ =>
                        {
                            if (replaced)
                                return _.Value;
                            replaced = true;
                            return $"[{replacement}]";
                        });
                        usedChunkIds.Add(replacement);
                        assignedId = replacement;
                    }
                    else
                    {
                        // No valid replacement - remove the invalid citation
                        line = Regex.Replace(line, @"\s*\[(chunk-\d+)\]", "");
                    }
                }
            }

            if (assignedId == null)
            {
                var newId = NextCandidate();
                if (!string.IsNullOrEmpty(newId))
                {
                    line = line.TrimEnd() + $" [{newId}]";
                    usedChunkIds.Add(newId);
                }
            }

            processed.Add(line);
        }

        return string.Join("\n", processed);
    }
    
    private static bool IsValidChunkId(string? chunkId)
    {
        if (string.IsNullOrWhiteSpace(chunkId))
            return false;
        if (!chunkId.StartsWith("chunk-", StringComparison.OrdinalIgnoreCase))
            return false;
        return int.TryParse(chunkId[6..], out _);
    }
    
    /// <summary>
    /// Post-process executive summary to remove meta-commentary and keep formatting consistent with the template
    /// </summary>
    private static string CleanExecutiveSummary(string summary, SummaryTemplate template, DocumentSizeProfile profile)
    {
        var (paragraphs, bullets) = SplitSummarySegments(summary);

        var formatted = template.OutputStyle switch
        {
            OutputStyle.Bullets or OutputStyle.CitationsOnly =>
                string.Join("\n", CleanBulletLines(bullets, template, profile)),
            OutputStyle.Mixed =>
                CleanMixedSummary(paragraphs, bullets, template, profile),
            _ =>
                CleanProseParagraphs(
                    paragraphs.Count > 0 ? paragraphs : bullets.Select(StripBulletPrefix).ToList(),
                    template)
        };

        return formatted.Trim();
    }

    private static (List<string> paragraphs, List<string> bullets) SplitSummarySegments(string summary)
    {
        var paragraphs = new List<string>();
        var bullets = new List<string>();
        var currentParagraph = new List<string>();

        foreach (var rawLine in summary.Split('\n'))
        {
            var trimmed = rawLine.Trim();

            if (string.IsNullOrEmpty(trimmed))
            {
                FlushParagraph();
                continue;
            }

            if (IsMetaCommentary(trimmed))
                continue;

            trimmed = Regex.Replace(trimmed, @"\[(\d{1,2})\](?!\d)", "");
            trimmed = RemoveHedgingInline(trimmed);

            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (IsBulletLine(trimmed))
            {
                FlushParagraph();
                bullets.Add(trimmed);
                continue;
            }

            currentParagraph.Add(trimmed);
        }

        FlushParagraph();
        return (paragraphs, bullets);

        void FlushParagraph()
        {
            if (currentParagraph.Count == 0) return;
            var paragraph = string.Join(" ", currentParagraph).Trim();
            if (paragraph.Length >= 10)
                paragraphs.Add(paragraph);
            currentParagraph.Clear();
        }
    }

    private static bool IsBulletLine(string line)
    {
        if (line.StartsWith('•') || line.StartsWith('-') || line.StartsWith('*') || line.StartsWith('+'))
            return true;

        return Regex.IsMatch(line, @"^\d+[\.\)]\s");
    }

    private static List<string> CleanBulletLines(List<string> bulletLines, SummaryTemplate template, DocumentSizeProfile profile)
    {
        if (bulletLines.Count == 0)
            return new List<string>();

        var cleaned = new List<string>();
        var seenContent = new List<string>();
        var maxBullets = GetExpectedBulletCount(template, profile);

        foreach (var line in bulletLines)
        {
            var normalized = NormalizeBullet(line);
            if (string.IsNullOrWhiteSpace(normalized) || normalized.Length < 5)
                continue;

            var dedupeKey = Regex.Replace(normalized.ToLowerInvariant(), @"[^\w\s]", "");
            if (seenContent.Any(s => ComputeSimpleSimilarity(s, dedupeKey) > 0.7))
                continue;

            seenContent.Add(dedupeKey);
            cleaned.Add(normalized);

            if (cleaned.Count >= maxBullets)
                break;
        }

        return cleaned;
    }

    private static string CleanMixedSummary(List<string> paragraphs, List<string> bullets, SummaryTemplate template, DocumentSizeProfile profile)
    {
        var sections = new List<string>();
        var prose = CleanProseParagraphs(paragraphs, template, maxParagraphsOverride: 1);
        if (!string.IsNullOrWhiteSpace(prose))
            sections.Add(prose);

        var bulletSection = CleanBulletLines(bullets, template, profile);
        if (bulletSection.Count > 0)
            sections.Add(string.Join("\n", bulletSection));

        if (sections.Count == 0 && bullets.Count > 0)
            sections.Add(string.Join("\n", CleanBulletLines(bullets, template, profile)));

        return string.Join("\n\n", sections.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private static string CleanProseParagraphs(List<string> paragraphs, SummaryTemplate template, int? maxParagraphsOverride = null)
    {
        if (paragraphs.Count == 0)
            return string.Empty;

        var maxParagraphs = maxParagraphsOverride ?? (template.Paragraphs > 0 ? template.Paragraphs : paragraphs.Count);
        var cleaned = new List<string>();
        var seenContent = new List<string>();

        foreach (var paragraph in paragraphs)
        {
            var normalized = Regex.Replace(paragraph.ToLowerInvariant(), @"[^\w\s]", "");
            if (seenContent.Any(s => ComputeSimpleSimilarity(s, normalized) > 0.75))
                continue;

            seenContent.Add(normalized);
            cleaned.Add(paragraph);

            if (cleaned.Count >= maxParagraphs)
                break;
        }

        return string.Join("\n\n", cleaned);
    }

    private static string StripBulletPrefix(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return string.Empty;

        var withoutNumbering = Regex.Replace(line, @"^\d+[\.\)]\s*", "");
        return withoutNumbering.TrimStart('•', '-', '*', '+', ' ', '\t');
    }

    
    /// <summary>
    /// Remove hedging phrases and filler language inline while preserving the core claim
    /// </summary>
    private static string RemoveHedgingInline(string text)
    {
        var patternsToRemove = new[]
        {
            // Hedging
            @"\b(appears to|seems to|possibly|likely|probably)\s+",
            @"\b(may be|might be|could be)\s+",
            @"\b(it is possible that|assuming|apparently)\s+",
            @"\b(presumably|potentially|suggests that)\s+",
            
            // Filler phrases that add no information
            @",?\s*as (seen|evidenced|revealed|shown|demonstrated) (in|by)\b",
            @",?\s*as (explored|examined|discussed) (in|by)\b",
            @"\b(the text|the novel|the book|the story|the author)\s+(shows|reveals|demonstrates|explores|examines)\s+(that\s+)?",
            @"\bJane Austen'?s?\s+(exploration|examination|portrayal|depiction)\s+of\s+",
            @"\bin\s+[""']?Pride and Prejudice[""']?\s*",
            @"\bthe author'?s?\s+(exploration|examination)\s+of\s+",
            
            // Redundant citations to the work itself
            @",?\s*as\s+revealed\s+in\s+[""'][^""']+[""']\s*",
        };
        
        var result = text;
        foreach (var pattern in patternsToRemove)
        {
            result = Regex.Replace(result, pattern, "", RegexOptions.IgnoreCase);
        }
        
        // Clean up resulting double spaces and trailing commas
        result = Regex.Replace(result, @"\s{2,}", " ");
        result = Regex.Replace(result, @",\s*\.", ".");
        result = Regex.Replace(result, @",\s*$", "");
        
        return result.Trim();
    }
    
    /// <summary>
    /// Normalize bullet point format to consistent • prefix
    /// </summary>
    private static string NormalizeBullet(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return string.Empty;

        var withoutNumbering = Regex.Replace(line, @"^\d+[\.\)]\s*", "");
        var content = withoutNumbering.TrimStart('•', '-', '*', '+', ' ', '\t');

        return string.IsNullOrWhiteSpace(content) ? string.Empty : "• " + content;
    }

    
    /// <summary>
    /// Simple word overlap similarity for deduplication
    /// </summary>
    private static double ComputeSimpleSimilarity(string a, string b)
    {
        var wordsA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var wordsB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        
        if (wordsA.Count == 0 || wordsB.Count == 0) return 0;
        
        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();
        
        return union > 0 ? (double)intersection / union : 0;
    }
    
    /// <summary>
    /// Clean topic name to remove meta-commentary prefixes
    /// </summary>
    private static string CleanTopicName(string topic)
    {
        // Remove common prefixes the LLM adds
        var cleaned = Regex.Replace(topic, @"^(Here are the|The following|Theme \d+:?|Topic \d+:?)\s*", "", RegexOptions.IgnoreCase);
        return cleaned.Trim().TrimEnd(':');
    }
    
    /// <summary>
    /// Clean topic summary to remove meta-commentary, filler, and leaked entity blocks
    /// </summary>
    private static string CleanTopicSummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return "";
        
        // First, strip any ENTITIES block that leaked into the summary
        summary = Regex.Replace(summary, 
            @"\n?\s*\*{0,2}ENTITIES\*{0,2}:?\s*\n?.*$", 
            "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        
        // Strip individual entity type lines that might have leaked
        summary = Regex.Replace(summary, 
            @"\n?\s*\*{0,2}(Characters|Locations|Dates|Events|Organizations|Orgs)\*{0,2}:\s*[^\n]*", 
            "", RegexOptions.IgnoreCase);
        
        // Strip "Rules:" section and anything after
        summary = Regex.Replace(summary, 
            @"\n?\s*Rules?:.*$", 
            "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        
        // Strip "Full Names:" sections which LLM adds
        summary = Regex.Replace(summary,
            @"\*{0,2}Full Names\*{0,2}:.*$",
            "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        
        // Strip "Plot Events" meta-sections 
        summary = Regex.Replace(summary,
            @"\*{0,2}Plot Events( and Character Actions)?\*{0,2}:.*$",
            "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        
        var lines = summary.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var cleanedLines = new List<string>();
        var hitEntitySection = false;
        
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            
            // Stop if we hit an entity section marker (including numbered variants)
            if (Regex.IsMatch(trimmed, @"^\*{0,2}(ENTITIES|Characters|Locations|Dates|Events|Organizations|Full Names)\*{0,2}:", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(trimmed, @"^\d+\.\s*(Characters|Locations|Events|Dates|Organizations):", RegexOptions.IgnoreCase))
            {
                hitEntitySection = true;
                continue;
            }
            
            // Skip everything after entity section starts
            if (hitEntitySection)
                continue;
            
            // Skip meta-commentary
            if (IsMetaCommentary(trimmed))
                continue;
            
            // Skip lines that look like entity lists (start with + or have entity-like patterns)
            if (trimmed.StartsWith("+") || 
                Regex.IsMatch(trimmed, @"^\*\s*\*\*[A-Z]") ||
                Regex.IsMatch(trimmed, @"^-\s*\*\*[A-Z]"))
                continue;
            
            // Remove filler phrases
            var cleaned = RemoveHedgingInline(trimmed);
            
            if (!string.IsNullOrWhiteSpace(cleaned) && cleaned.Length > 10)
                cleanedLines.Add(cleaned);
        }
        
        return string.Join(" ", cleanedLines).Trim();
    }
    
    /// <summary>
    /// Detect meta-commentary that should be stripped
    /// </summary>
    private static bool IsMetaCommentary(string line)
    {
        var metaPatterns = new[]
        {
            @"^(Here is|Here are|Below is|The following)",
            @"^(This summary|This document|Based on the)",
            @"^(I have|I've|Let me|I will|I'll)",
            @"^(Note:|Note that|Please note)",
            @"(executive summary|bullet points?|extracted themes?)[:.]?\s*$",
            @"^(In summary|To summarize|In conclusion)",
            @"^(According to|From the text|The text shows|The text reveals)",
            @"^\*\*Executive Summary\*\*",
            @"^Executive Summary:?\s*$",
            @"^(The novel|The book|The story|The author)\s+(shows|reveals|demonstrates|explores)",
            @"^Here are the",
            // New patterns for topic synthesis meta-commentary
            @"^\*\*SUMMARY\*\*\s*$",
            @"^\*\*Summary\s*\(\d+\s*words?\)\*\*",
            @"^SUMMARY\s*\(\d+\s*words?\)",
            @"^Key events and character actions:",
            @"^(Characters|Locations|Events|Dates|Organizations):\s*$",
            @"^Rules:",
            @"in the requested format",
            @"in the given section",
            @"for the provided text",
            @"entities,?\s*and\s*rules",
        };
        
        return metaPatterns.Any(p => 
            Regex.IsMatch(line, p, RegexOptions.IgnoreCase));
    }
    
    /// <summary>
    /// Legacy method - redirects to grounded version
    /// </summary>
    private async Task<string> CreateWeightedExecutiveSummaryAsync(
        List<TopicSummary> topicSummaries,
        List<Claim> weightedClaims,
        string? focus)
    {
        return await CreateGroundedExecutiveSummaryAsync(topicSummaries, weightedClaims, null, focus);
    }
    
    /// <summary>
    /// Synthesize topic with claim extraction and classification
    /// Returns summary, entities, AND typed claims for weighted synthesis
    /// </summary>
    private async Task<(string summary, ExtractedEntities? entities, List<Claim> claims)> SynthesizeTopicWithClaimsAsync(
        string topic,
        List<DocumentChunk> chunks,
        string? focus,
        DocumentType docType,
        string? templateName = null)
    {
        var (summary, entities) = await SynthesizeTopicWithEntitiesAsync(topic, chunks, focus, docType, templateName);
        var claims = ExtractAndClassifyClaims(summary, topic, chunks);
        return (summary, entities, claims);
    }
    
    /// <summary>
    /// Extract claims from summary text and classify using TF-IDF
    /// </summary>
    private List<Claim> ExtractAndClassifyClaims(
        string summary,
        string topic,
        List<DocumentChunk> sourceChunks)
    {
        var claims = new List<Claim>();
        var lines = summary.Split('\n')
            .Select(l => l.Trim().TrimStart('-', '*', '•').Trim())
            .Where(l => l.Length > 10)
            .ToList();

        var sourceText = string.Join(" ", sourceChunks.Select(c => c.Content));

        foreach (var line in lines)
        {
            var citations = ExtractCitations(line);
            var claimType = ClassifyClaimType(line, sourceText);
            var confidence = AssessClaimConfidence(line, citations, sourceChunks);

            claims.Add(new Claim(
                Text: RemoveCitations(line),
                Type: claimType,
                Confidence: confidence,
                Evidence: citations,
                Topic: topic));
        }

        return claims;
    }

    /// <summary>
    /// Classify claim type using TF-IDF to detect "colour" vs "fact"
    /// High TF-IDF terms that appear rarely = likely colour (incidental details)
    /// Terms that appear across many chunks = likely plot-critical facts
    /// </summary>
    private ClaimType ClassifyClaimType(
        string claimText,
        string fullSourceText)
    {
        var terms = _textAnalysis.Tokenize(claimText);
        if (terms.Count == 0) return ClaimType.Colour;

        var distinctiveTerms = terms
            .Select(t => (term: t, type: _textAnalysis.ClassifyTermImportance(t)))
            .ToList();

        var factTerms = distinctiveTerms.Count(t => t.type == ClaimType.Fact);
        var colourTerms = distinctiveTerms.Count(t => t.type == ClaimType.Colour);

        if (colourTerms > factTerms * 2)
            return ClaimType.Colour;

        if (factTerms > colourTerms)
            return ClaimType.Fact;

        var similarity = _textAnalysis.ComputeCombinedSimilarity(
            _textAnalysis.NormalizeForComparison(claimText),
            _textAnalysis.NormalizeForComparison(fullSourceText));

        return similarity > 0.7 ? ClaimType.Fact : ClaimType.Inference;
    }

    /// <summary>
    /// Assess confidence level based on evidence quality
    /// </summary>
    private ConfidenceLevel AssessClaimConfidence(
        string claimText,
        List<Citation> citations,
        List<DocumentChunk> sourceChunks)
    {
        if (citations.Count == 0)
            return ConfidenceLevel.Low;

        if (citations.Count >= 2)
            return ConfidenceLevel.High;

        var citedChunk = sourceChunks.FirstOrDefault(c => c.Id.Equals(citations[0].ChunkId, StringComparison.OrdinalIgnoreCase));
        if (citedChunk == null)
            return ConfidenceLevel.Uncertain;

        var claimTerms = _textAnalysis.Tokenize(claimText);
        var chunkTerms = _textAnalysis.Tokenize(citedChunk.Content);
        var overlap = claimTerms.Intersect(chunkTerms, StringComparer.OrdinalIgnoreCase).Count();
        var overlapRatio = claimTerms.Count > 0 ? (double)overlap / claimTerms.Count : 0;

        return overlapRatio switch
        {
            > 0.5 => ConfidenceLevel.High,
            > 0.3 => ConfidenceLevel.Medium,
            _ => ConfidenceLevel.Low
        };
    }

    
    /// <summary>
    /// Extract citation references from text
    /// </summary>
    private static List<Citation> ExtractCitations(string text)
    {
        var citations = new List<Citation>();
        var matches = Regex.Matches(text, @"\[(chunk-\d+)\]");
        
        foreach (Match match in matches)
        {
            citations.Add(new Citation(match.Groups[1].Value));
        }
        
        return citations;
    }
    
    /// <summary>
    /// Remove citation markers from text for clean display
    /// </summary>
    private static string RemoveCitations(string text)
    {
        return Regex.Replace(text, @"\s*\[chunk-\d+\]", "").Trim();
    }
    
    /// <summary>
    /// Normalize and merge entities using fuzzy deduplication
    /// Addresses issues like "Thaddeus Sholto" vs "Mr. Thaddeus Sholto"
    /// </summary>
    private ExtractedEntities NormalizeAndMergeEntities(List<ExtractedEntities> allEntities)
    {
        // First do basic merge
        var merged = ExtractedEntities.Merge(allEntities);
        
        // Then apply advanced normalization with fuzzy matching
        var normalizedCharacters = _textAnalysis.NormalizeEntities(merged.Characters, "character")
            .Select(e => e.CanonicalName)
            .ToList();
        
        var normalizedLocations = _textAnalysis.NormalizeEntities(merged.Locations, "location")
            .Select(e => e.CanonicalName)
            .ToList();
        
        // Dates and events don't need fuzzy matching typically
        return new ExtractedEntities(
            normalizedCharacters,
            normalizedLocations,
            merged.Dates,
            merged.Events,
            merged.Organizations);
    }

    private async Task EnsureCollectionAsync(string collectionName)
    {
        var collections = await _qdrant.ListCollectionsAsync();
        if (!collections.Any(c => c == collectionName))
        {
            await _qdrant.CreateCollectionAsync(collectionName, _vectorSize);
            if (_verbose) Console.WriteLine($"[Index] Created collection {collectionName}");
        }
    }

    /// <summary>
    /// Generate a stable, unique ID for a chunk point.
    /// Includes order to handle duplicate content (same hash) in documents with repeated sections.
    /// </summary>
    private static Guid GenerateStableId(string docId, string chunkHash, int order)
        => ContentHasher.ComputeGuid($"{docId}:{chunkHash}:{order}");

    private static string ReplaceChunkCitations(string text, IReadOnlyDictionary<string, DocumentChunk> chunkLookup)
    {
        return Regex.Replace(text, @"\[chunk-(\d+)\]", match =>
        {
            var id = $"chunk-{match.Groups[1].Value}";
            if (chunkLookup.TryGetValue(id, out var chunk))
            {
                return $"[{FormatChunkCitation(chunk)}]";
            }
            // Invalid chunk ID - remove it entirely rather than leaving hallucinated reference
            return "";
        });
    }
    
    /// <summary>
    /// Validate and fix chunk citations - removes invalid IDs, ensures citations reference real chunks
    /// </summary>
    private static string ValidateChunkCitations(string text, HashSet<string> validChunkIds)
    {
        if (string.IsNullOrEmpty(text) || validChunkIds.Count == 0)
            return text;
        
        return Regex.Replace(text, @"\[chunk-(\d+)\]", match =>
        {
            var id = $"chunk-{match.Groups[1].Value}";
            // Keep valid citations, remove invalid ones
            return validChunkIds.Contains(id) ? match.Value : "";
        });
    }

    private static string FormatChunkCitation(DocumentChunk chunk)
    {
        if (chunk.PageStart.HasValue)
        {
            if (chunk.PageEnd.HasValue && chunk.PageEnd != chunk.PageStart)
            {
                return $"pp.{chunk.PageStart}-{chunk.PageEnd}";
            }
            return $"p.{chunk.PageStart}";
        }

        return $"§{chunk.Order + 1}";
    }

    private static double CalculateCitationRate(string summary)
    {
        var bullets = summary.Split('\n').Count(l => l.TrimStart().StartsWith('-'));
        if (bullets == 0) return 0;
        var citations = Regex.Matches(summary, @"\[chunk-\d+\]").Count;
        return (double)citations / bullets;
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var count = 0;
        var inWord = false;

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch == '\'' || ch == '’')
            {
                if (!inWord)
                {
                    count++;
                    inWord = true;
                }
            }
            else if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch))
            {
                inWord = false;
            }
            else
            {
                inWord = false;
            }
        }

        return count;
    }

    /// <summary>
    /// Extract document title and author from early content (e.g., Project Gutenberg headers)
    /// </summary>
    private static string? ExtractDocumentTitle(List<DocumentChunk> chunks, string docId)
    {
        if (chunks.Count == 0) return null;
        
        var firstChunk = chunks[0].Content;
        var lines = firstChunk.Split('\n').Take(30).ToList();
        
        string? title = null;
        string? author = null;
        
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            
            // Project Gutenberg format: "Title: X"
            if (trimmed.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
            {
                title = trimmed[6..].Trim();
            }
            // Author line
            else if (trimmed.StartsWith("Author:", StringComparison.OrdinalIgnoreCase))
            {
                author = trimmed[7..].Trim();
            }
            // "by Author Name" pattern
            else if (trimmed.StartsWith("by ", StringComparison.OrdinalIgnoreCase) && trimmed.Length < 50)
            {
                author = trimmed[3..].Trim();
            }
            // Look for title in first heading
            else if (title == null && chunks[0].Heading != null && 
                     !chunks[0].Heading.StartsWith("Project", StringComparison.OrdinalIgnoreCase))
            {
                title = chunks[0].Heading;
            }
        }
        
        // Fall back to filename
        if (string.IsNullOrEmpty(title))
        {
            var fileName = Path.GetFileNameWithoutExtension(docId);
            if (!string.IsNullOrEmpty(fileName) && fileName.Length > 3)
                title = fileName.Replace('-', ' ').Replace('_', ' ');
        }
        
        if (title != null && author != null)
            return $"{title} by {author}";
        return title;
    }

    private static int CountWords(IEnumerable<DocumentChunk> chunks)
    {
        var total = 0;
        foreach (var chunk in chunks)
        {
            total += CountWords(chunk.Content);
        }
        return total;
    }
}
