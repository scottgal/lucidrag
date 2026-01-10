using System.Diagnostics;
using System.Text;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services.Onnx;
using Mostlylucid.Summarizer.Core.Utilities;


namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// BERT→RAG Summarizer: The production-grade summarization pipeline.
/// 
/// Architecture:
/// 1. Extract: Parse document → segments with embeddings + salience scores
/// 2. Retrieve: Dual-score ranking (query similarity + salience) 
/// 3. Synthesize: LLM generates fluent summary from retrieved segments
/// 
/// Key properties:
/// - LLM only at synthesis (no LLM-in-the-loop evaluation)
/// - Deterministic extraction (reproducible, debuggable)
/// - Perfect citations (every claim traceable to source segment)
/// - Scales to any document size (extraction is O(n), retrieval is O(log n))
/// - Cost-optimal (cheap CPU work first, expensive LLM last)
/// 
/// Vector Storage:
/// - Default: In-memory (no persistence, segments extracted each run)
/// - Optional: IVectorStore for persistent storage (Qdrant, etc.)
///   - Documents identified by content hash for stable reuse
///   - Avoids re-embedding when document content unchanged
/// </summary>
public class BertRagSummarizer : IDisposable, IAsyncDisposable
{
    private readonly SegmentExtractor _extractor;
    private readonly OnnxEmbeddingService _queryEmbedder;
    private readonly OllamaService _ollama;
    private readonly RetrievalConfig _retrievalConfig;
    private readonly bool _verbose;
    
    // Vector store support
    private readonly IVectorStore? _vectorStore;
    private readonly BertRagConfig _bertRagConfig;
    private readonly OnnxConfig _onnxConfig;
    
    public SummaryTemplate Template { get; private set; }

    public BertRagSummarizer(
        OnnxConfig onnxConfig,
        OllamaService ollama,
        ExtractionConfig? extractionConfig = null,
        RetrievalConfig? retrievalConfig = null,
        SummaryTemplate? template = null,
        bool verbose = false,
        IVectorStore? vectorStore = null,
        BertRagConfig? bertRagConfig = null)
    {
        _extractor = new SegmentExtractor(onnxConfig, extractionConfig, verbose);
        _queryEmbedder = new OnnxEmbeddingService(onnxConfig, verbose);
        _ollama = ollama;
        _retrievalConfig = retrievalConfig ?? new RetrievalConfig();
        Template = template ?? SummaryTemplate.Presets.Default;
        _verbose = verbose;
        _vectorStore = vectorStore;
        _bertRagConfig = bertRagConfig ?? new BertRagConfig();
        _onnxConfig = onnxConfig;
    }

    public void SetTemplate(SummaryTemplate template) => Template = template;
    
    // Pipeline version - increment when changing extraction/synthesis logic
    private const string PipelineVersion = "v1";
    
    /// <summary>
    /// Extract and retrieve segments without synthesis - for template benchmarking.
    /// Returns the extraction result and retrieved segments that can be reused across templates.
    /// </summary>
    public async Task<(ExtractionResult extraction, List<Segment> retrieved)> ExtractAndRetrieveAsync(
        string docId,
        string markdown,
        string? focusQuery = null,
        ContentType contentType = ContentType.Unknown,
        CancellationToken ct = default)
    {
        // Compute content hash for stable document identification
        var contentHash = ComputeContentHash(markdown);
        var stableDocId = CreateStableDocId(docId, contentHash);
        var collectionName = _bertRagConfig.CollectionName;
        
        ExtractionResult extraction;
        
        // === Phase 1: Extract (or load from store) ===
        if (_vectorStore != null && _bertRagConfig.ReuseExistingEmbeddings)
        {
            await _vectorStore.InitializeAsync(collectionName, 384, ct);
            
            var hasDoc = await _vectorStore.HasDocumentAsync(collectionName, stableDocId, ct);
            if (hasDoc)
            {
                VerboseHelper.Log(_verbose, "[bold cyan]Phase 1: Loading from store[/]");
                var storedSegments = await _vectorStore.GetDocumentSegmentsAsync(collectionName, stableDocId, ct);
                
                if (storedSegments.Count > 0)
                {
                    var topBySalience = storedSegments
                        .OrderByDescending(s => s.SalienceScore)
                        .Take(_retrievalConfig.TopK)
                        .ToList();
                    
                    extraction = new ExtractionResult
                    {
                        AllSegments = storedSegments,
                        TopBySalience = topBySalience,
                        ContentType = contentType,
                        ExtractionTime = TimeSpan.Zero
                    };
                    
                    VerboseHelper.Log(_verbose, $"[dim]Loaded {storedSegments.Count} segments from store[/]");
                }
                else
                {
                    extraction = await ExtractAndStoreAsync(stableDocId, markdown, contentType, ct);
                }
            }
            else
            {
                extraction = await ExtractAndStoreAsync(stableDocId, markdown, contentType, ct);
            }
        }
        else
        {
            VerboseHelper.Log(_verbose, "[bold cyan]Phase 1: Extraction[/]");
            extraction = await _extractor.ExtractAsync(stableDocId, markdown, contentType, ct);
        }
        
        if (extraction.AllSegments.Count == 0)
        {
            return (extraction, new List<Segment>());
        }
        
        // === Phase 2: Retrieve ===
        VerboseHelper.Log(_verbose, "[bold cyan]Phase 2: Retrieval[/]");
        var retrieved = await RetrieveAsync(extraction, focusQuery, ct);
        
        if (_verbose)
        {
            VerboseHelper.Log(_verbose, $"[dim]Retrieved {retrieved.Count} segments for synthesis[/]");
        }
        
        return (extraction, retrieved);
    }
    
    /// <summary>
    /// Synthesize summary from pre-extracted and pre-retrieved segments.
    /// Used for template benchmarking - allows running synthesis with different templates
    /// on the same extraction/retrieval results.
    /// </summary>
    public async Task<DocumentSummary> SynthesizeFromRetrievedAsync(
        string docId,
        ExtractionResult extraction,
        List<Segment> retrieved,
        string? focusQuery = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        
        if (retrieved.Count == 0)
        {
            return CreateEmptySummary(docId, "No segments found in document");
        }
        
        // === Phase 3: Synthesize (using current template) ===
        VerboseHelper.Log(_verbose, $"[bold cyan]Phase 3: Synthesis with template '{VerboseHelper.Escape(Template.Name)}'[/]");
        var summary = await SynthesizeAsync(docId, retrieved, extraction, focusQuery, ct);
        
        stopwatch.Stop();
        
        // Build trace with timing info
        var coverage = extraction.AllSegments.Count == 0
            ? 0
            : (double)retrieved.Count / extraction.AllSegments.Count;
        var trace = new SummarizationTrace(
            docId,
            extraction.AllSegments.Count,
            retrieved.Count,
            extraction.AllSegments
                .Where(s => s.Type == SegmentType.Heading)
                .Select(s => s.Text)
                .Take(10)
                .ToList(),
            stopwatch.Elapsed,
            CoverageScore: coverage,
            CitationRate: 1.0
        );
        
        return new DocumentSummary(
            summary.ExecutiveSummary,
            summary.TopicSummaries,
            summary.OpenQuestions,
            trace,
            summary.Entities
        );
    }
    
    /// <summary>
    /// Canonicalize text before hashing: normalize whitespace, trim, lowercase.
    /// This ensures trivial formatting changes don't bust the cache.
    /// </summary>
    private static string Canonicalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        
        // Normalize line endings
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        
        // Collapse multiple whitespace to single space
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ");
        
        return normalized.Trim().ToLowerInvariant();
    }
    
    /// <summary>
    /// Compute a stable content hash for document identification.
    /// Canonicalizes text first to avoid cache busting on trivial changes.
    /// </summary>
    private static string ComputeContentHash(string content)
    {
        var canonical = Canonicalize(content);
        return ContentHasher.ComputeHash(canonical);
    }
    
    /// <summary>
    /// Generate a versioned cache key that includes all factors affecting output.
    /// Called BEFORE retrieval - uses config-based factors only.
    /// 
    /// Includes:
    /// - Pipeline version (invalidates on algorithm changes)
    /// - Document content hash
    /// - Query hash (or "noquery")
    /// - Template settings hash
    /// - Retrieval config hash (TopK, Alpha, RRF, HybridSearch, AdaptiveTopK, MaxTopK)
    /// - LLM model name hash
    /// - Embedding model hash (model name + quantization)
    /// </summary>
    private string GeneratePreRetrievalCacheKey(string contentHash, string? focusQuery, string modelName)
    {
        var queryHash = string.IsNullOrEmpty(focusQuery) 
            ? "noquery" 
            : ComputeContentHash(focusQuery);
        
        // Include all template settings that affect output
        var templateConfig = $"{Template.Name}_{Template.TargetWords}_{Template.OutputStyle}_{Template.MaxBullets}";
        var templateHash = ComputeContentHash(templateConfig);
        
        // Include retrieval config (all parameters that affect which segments are retrieved)
        var retrievalConfig = $"{_retrievalConfig.TopK}_{_retrievalConfig.Alpha}_{_retrievalConfig.UseRRF}_{_retrievalConfig.UseHybridSearch}_{_retrievalConfig.AdaptiveTopK}_{_retrievalConfig.MaxTopK}";
        var retrievalHash = ComputeContentHash(retrievalConfig);
        
        // Include embedding model (different embeddings = different retrieval results)
        var embeddingConfig = $"{_onnxConfig.EmbeddingModel}_{_onnxConfig.UseQuantized}";
        var embeddingHash = ComputeContentHash(embeddingConfig);
        
        var modelHash = ComputeContentHash(modelName);
        
        return $"{PipelineVersion}_{contentHash}_{queryHash}_{templateHash}_{retrievalHash}_{embeddingHash}_{modelHash}";
    }
    
    /// <summary>
    /// Generate the final cache key using actual retrieved segment content hashes.
    /// This ensures the cache is invalidated if retrieval returns different segments
    /// (due to embedding drift, config changes, or document updates).
    /// 
    /// Key design:
    /// - Order-insensitive: sorted by hash to ensure stability regardless of retrieval order
    /// - Content-based: uses ContentHash to detect actual content changes
    /// - Includes segment count: different TopK values produce different keys
    /// </summary>
    private string GenerateSynthesisCacheKey(string preRetrievalKey, List<Segment> retrievedSegments)
    {
        // Sort by content hash (order-insensitive) - ensures same evidence set = same key
        // regardless of retrieval order or score tie-breaking
        var sortedHashes = retrievedSegments
            .Select(s => s.ContentHash)
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToList();
        
        // Include count to differentiate e.g. TopK=5 vs TopK=10 that happen to overlap
        var segmentSignature = $"n={sortedHashes.Count}:{string.Join("_", sortedHashes)}";
        var retrievedHash = ComputeContentHash(segmentSignature);
        
        return $"{preRetrievalKey}_{retrievedHash}";
    }
    
    /// <summary>
    /// Create a stable document ID from filename and content hash.
    /// Format: {sanitized_filename}_{content_hash}
    /// This allows reuse when content unchanged, but re-indexes on changes.
    /// </summary>
    private static string CreateStableDocId(string docId, string contentHash)
    {
        var sanitized = SanitizeDocId(docId);
        return $"{sanitized}_{contentHash}";
    }
    
    private static string SanitizeDocId(string docId)
    {
        var sb = new StringBuilder();
        foreach (var c in docId)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
            else if (c == '.' || c == '-' || c == ' ')
                sb.Append('_');
        }
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Full pipeline: Extract → Retrieve → Synthesize
    /// With optional vector store persistence and summary caching.
    /// 
    /// Cache strategy:
    /// 1. Pre-retrieval key: config-based (fast lookup for exact same request)
    /// 2. Post-retrieval key: includes actual segment hashes (ensures correctness)
    /// </summary>
    public async Task<DocumentSummary> SummarizeAsync(
        string docId,
        string markdown,
        string? focusQuery = null,
        ContentType contentType = ContentType.Unknown,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        
        // Compute content hash for stable document identification
        var contentHash = ComputeContentHash(markdown);
        var stableDocId = CreateStableDocId(docId, contentHash);
        var collectionName = _bertRagConfig.CollectionName;
        
        // Get model name for cache key (from Ollama service)
        var modelName = _ollama.Model ?? "unknown";
        
        // Pre-retrieval cache key (fast path for exact same request)
        var preRetrievalKey = GeneratePreRetrievalCacheKey(contentHash, focusQuery, modelName);
        
        ExtractionResult extraction;
        
        // === Phase 1: Extract (or load from store) ===
        if (_vectorStore != null && _bertRagConfig.ReuseExistingEmbeddings)
        {
            await _vectorStore.InitializeAsync(collectionName, 384, ct); // 384 = all-MiniLM-L6-v2 dimension
            
            var hasDoc = await _vectorStore.HasDocumentAsync(collectionName, stableDocId, ct);
            if (hasDoc)
            {
                VerboseHelper.Log(_verbose, "[bold cyan]Phase 1: Loading from store[/]");
                var storedSegments = await _vectorStore.GetDocumentSegmentsAsync(collectionName, stableDocId, ct);
                
                if (storedSegments.Count > 0)
                {
                    // Reconstruct extraction result from stored segments
                    var topBySalience = storedSegments
                        .OrderByDescending(s => s.SalienceScore)
                        .Take(_retrievalConfig.TopK)
                        .ToList();
                    
                    extraction = new ExtractionResult
                    {
                        AllSegments = storedSegments,
                        TopBySalience = topBySalience,
                        ContentType = contentType,
                        ExtractionTime = TimeSpan.Zero
                    };
                    
                    VerboseHelper.Log(_verbose, $"[dim]Loaded {storedSegments.Count} segments from store[/]");
                }
                else
                {
                    // Fallback to extraction if store returned empty
                    extraction = await ExtractAndStoreAsync(stableDocId, markdown, contentType, ct);
                }
            }
            else
            {
                // Document not in store - extract and store
                extraction = await ExtractAndStoreAsync(stableDocId, markdown, contentType, ct);
            }
        }
        else
        {
            // No vector store - extract normally
            VerboseHelper.Log(_verbose, "[bold cyan]Phase 1: Extraction[/]");
            extraction = await _extractor.ExtractAsync(stableDocId, markdown, contentType, ct);
        }
        
        if (extraction.AllSegments.Count == 0)
        {
            return CreateEmptySummary(docId, "No segments found in document");
        }
        
        // === Phase 2: Retrieve ===
        VerboseHelper.Log(_verbose, "[bold cyan]Phase 2: Retrieval[/]");
        var retrieved = await RetrieveAsync(extraction, focusQuery, ct);
        
        if (_verbose)
        {
            VerboseHelper.Log(_verbose, $"[dim]Retrieved {retrieved.Count} segments for synthesis[/]");
        }
        
        // Generate final cache key using actual retrieved segment hashes
        // This ensures we don't serve stale cache if retrieval changes
        var synthesisCacheKey = GenerateSynthesisCacheKey(preRetrievalKey, retrieved);
        
        // === Check Synthesis Cache (post-retrieval) ===
        if (_vectorStore != null && _bertRagConfig.ReuseExistingEmbeddings)
        {
            var cached = await _vectorStore.GetCachedSummaryAsync(collectionName, synthesisCacheKey, ct);
            if (cached != null)
            {
                VerboseHelper.Log(_verbose, "[green]Using cached synthesis (segment match)[/]");
                stopwatch.Stop();
                return cached;
            }
        }
        
        // === Phase 3: Synthesize ===
        VerboseHelper.Log(_verbose, "[bold cyan]Phase 3: Synthesis[/]");
        var summary = await SynthesizeAsync(docId, retrieved, extraction, focusQuery, ct);
        
        stopwatch.Stop();
        
        // Build trace with citation map
        // IMPORTANT: Use stableDocId so downstream code can find segments in vector store
        var coverage = extraction.AllSegments.Count == 0
            ? 0
            : (double)retrieved.Count / extraction.AllSegments.Count;
        var trace = new SummarizationTrace(
            stableDocId,  // Use stableDocId to match vector store document IDs
            extraction.AllSegments.Count,
            retrieved.Count,
            extraction.AllSegments
                .Where(s => s.Type == SegmentType.Heading)
                .Select(s => s.Text)
                .Take(10)
                .ToList(),
            stopwatch.Elapsed,
            CoverageScore: coverage,
            CitationRate: 1.0 // BERT-RAG always has perfect citations
        );
        
        var result = new DocumentSummary(
            summary.ExecutiveSummary,
            summary.TopicSummaries,
            summary.OpenQuestions,
            trace,
            summary.Entities
        );
        
        // === Cache the summary using segment-based key ===
        if (_vectorStore != null && _bertRagConfig.PersistVectors)
        {
            await _vectorStore.CacheSummaryAsync(collectionName, synthesisCacheKey, result, ct);
        }
        
        return result;
    }
    
    /// <summary>
    /// Extract segments and store them in the vector store
    /// </summary>
    private async Task<ExtractionResult> ExtractAndStoreAsync(
        string stableDocId,
        string markdown,
        ContentType contentType,
        CancellationToken ct)
    {
        VerboseHelper.Log(_verbose, "[bold cyan]Phase 1: Extraction[/]");
        var extraction = await _extractor.ExtractAsync(stableDocId, markdown, contentType, ct);
        
        if (_vectorStore != null && _bertRagConfig.PersistVectors && extraction.AllSegments.Count > 0)
        {
            VerboseHelper.Log(_verbose, "[dim]Storing segments in vector store...[/]");
            await _vectorStore.UpsertSegmentsAsync(_bertRagConfig.CollectionName, extraction.AllSegments, ct);
        }
        
        return extraction;
    }

    /// <summary>
    /// Calculate adaptive TopK based on document size and content type.
    /// Larger documents and narrative content get more segments to maintain quality.
    /// </summary>
    private int CalculateAdaptiveTopK(int totalSegments, ContentType contentType)
    {
        if (!_retrievalConfig.AdaptiveTopK)
            return _retrievalConfig.TopK;
        
        // Calculate TopK to meet minimum coverage percentage
        var coverageTopK = (int)Math.Ceiling(totalSegments * _retrievalConfig.MinCoveragePercent / 100.0);
        
        // Apply narrative boost for fiction/stories (they need more context to avoid hallucinations)
        if (contentType == ContentType.Narrative)
        {
            coverageTopK = (int)Math.Ceiling(coverageTopK * _retrievalConfig.NarrativeBoost);
        }
        
        // Clamp to configured min/max
        var adaptiveTopK = Math.Max(_retrievalConfig.MinTopK, Math.Min(_retrievalConfig.MaxTopK, coverageTopK));
        
        if (_verbose && adaptiveTopK != _retrievalConfig.TopK)
        {
            var contentLabel = contentType == ContentType.Narrative ? "narrative" : "expository";
            VerboseHelper.Log($"[dim]Adaptive TopK: {adaptiveTopK} (base {_retrievalConfig.TopK}, {totalSegments} segments, {contentLabel})[/]");
        }
        
        return adaptiveTopK;
    }

    /// <summary>
    /// Retrieve segments using hybrid search: Dense + BM25 (sparse) + Salience via RRF.
    /// 
    /// Three-way RRF combines:
    ///   1. Dense similarity (semantic meaning from embeddings)
    ///   2. BM25 sparse (lexical/keyword matching)
    ///   3. Salience score (document importance from extraction)
    /// 
    /// RRF(d) = 1/(k + rank_dense) + 1/(k + rank_bm25) + 1/(k + rank_salience)
    /// 
    /// Benefits:
    /// - Catches both semantic AND lexical matches
    /// - Scale-invariant (no weight tuning needed)
    /// - Proven effective (Elasticsearch, Vespa, MS-RAG all use this pattern)
    /// </summary>
    private async Task<List<Segment>> RetrieveAsync(
        ExtractionResult extraction,
        string? focusQuery,
        CancellationToken ct)
    {
        var segments = extraction.AllSegments;
        var topBySalience = extraction.TopBySalience;
        
        // Calculate adaptive TopK based on document size and type
        var effectiveTopK = CalculateAdaptiveTopK(segments.Count, extraction.ContentType);
        
        // If no query, just return top by salience (generic summary)
        if (string.IsNullOrWhiteSpace(focusQuery))
        {
            VerboseHelper.Log(_verbose, "[dim]No focus query - using salience-only ranking[/]");
            return topBySalience.Take(effectiveTopK).ToList();
        }
        
        // Embed the query for dense retrieval
        await _queryEmbedder.InitializeAsync(ct);
        var queryEmbedding = await _queryEmbedder.EmbedAsync(focusQuery, ct);
        
        // Score all segments by dense (semantic) similarity
        foreach (var segment in segments)
        {
            if (segment.Embedding == null) continue;
            segment.QuerySimilarity = CosineSimilarity(queryEmbedding, segment.Embedding);
        }
        
        List<Segment> topByRetrieval;
        
        if (_retrievalConfig.UseRRF)
        {
            if (_retrievalConfig.UseHybridSearch)
            {
                // Hybrid: Dense + BM25 + Salience via three-way RRF
                var bm25 = new BM25Scorer();
                bm25.Initialize(segments);
                
                topByRetrieval = HybridRRF.Fuse(
                    segments, 
                    focusQuery, 
                    bm25, 
                    _retrievalConfig.RrfK, 
                    effectiveTopK);
                
                VerboseHelper.Log(_verbose, $"[dim]Using Hybrid RRF: Dense + BM25 + Salience (k={_retrievalConfig.RrfK}, topK={effectiveTopK})[/]");
            }
            else
            {
                // Standard: Dense + Salience via two-way RRF
                topByRetrieval = RetrieveWithRRF(segments, _retrievalConfig.RrfK, effectiveTopK);
                VerboseHelper.Log(_verbose, $"[dim]Using RRF: Dense + Salience (k={_retrievalConfig.RrfK}, topK={effectiveTopK})[/]");
            }
        }
        else
        {
            // Legacy: Weighted sum
            var alpha = _retrievalConfig.Alpha;
            foreach (var segment in segments)
            {
                segment.RetrievalScore = alpha * segment.QuerySimilarity + (1 - alpha) * segment.SalienceScore;
            }
            
            topByRetrieval = segments
                .Where(s => s.QuerySimilarity >= _retrievalConfig.MinSimilarity)
                .OrderByDescending(s => s.RetrievalScore)
                .Take(effectiveTopK)
                .ToList();
            
            VerboseHelper.Log(_verbose, $"[dim]Using weighted sum (alpha={alpha}, topK={effectiveTopK})[/]");
        }
        
        // Merge with fallback bucket (ensures global coverage even if query is narrow)
        var fallbackNeeded = _retrievalConfig.FallbackCount;
        var fallbackToAdd = topBySalience
            .Where(s => !topByRetrieval.Contains(s))
            .Take(fallbackNeeded);
        
        var result = topByRetrieval.Concat(fallbackToAdd)
            .OrderBy(s => s.Index) // Restore document order for coherent synthesis
            .ToList();
        
        if (_verbose)
        {
            VerboseHelper.Log($"[dim]Query: \"{VerboseHelper.Escape(focusQuery ?? "")}\"[/]");
            VerboseHelper.Log(_verbose, $"[dim]Top by retrieval: {topByRetrieval.Count}, fallback added: {result.Count - topByRetrieval.Count}[/]");
        }
        
        return result;
    }

    /// <summary>
    /// Reciprocal Rank Fusion: combine multiple rankings into a single ranking.
    /// 
    /// Formula: RRF(d) = Σ 1/(k + rank_i(d))
    /// 
    /// Where:
    /// - k = 60 (standard, prevents division by small numbers)
    /// - rank_i(d) = position in ranking i (1-based)
    /// 
    /// We fuse two rankings:
    /// 1. By query similarity (relevance to user's focus)
    /// 2. By salience score (importance to document)
    /// </summary>
    private static List<Segment> RetrieveWithRRF(List<Segment> segments, int k, int topK)
    {
        // Create rankings
        var byQuerySim = segments
            .Where(s => s.Embedding != null)
            .OrderByDescending(s => s.QuerySimilarity)
            .ToList();
        
        var bySalience = segments
            .Where(s => s.Embedding != null)
            .OrderByDescending(s => s.SalienceScore)
            .ToList();
        
        // Compute RRF scores
        var rrfScores = new Dictionary<Segment, double>();
        
        for (int i = 0; i < byQuerySim.Count; i++)
        {
            var segment = byQuerySim[i];
            var rank = i + 1; // 1-based
            rrfScores[segment] = 1.0 / (k + rank);
        }
        
        for (int i = 0; i < bySalience.Count; i++)
        {
            var segment = bySalience[i];
            var rank = i + 1; // 1-based
            if (rrfScores.ContainsKey(segment))
                rrfScores[segment] += 1.0 / (k + rank);
            else
                rrfScores[segment] = 1.0 / (k + rank);
        }
        
        // Store RRF score in segment for debugging/tracing
        foreach (var (segment, score) in rrfScores)
        {
            segment.RetrievalScore = score;
        }
        
        // Return top-K by RRF score
        return rrfScores
            .OrderByDescending(kv => kv.Value)
            .Take(topK)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>
    /// Synthesize a fluent summary from retrieved segments.
    /// 
    /// The LLM's job is ONLY to:
    /// - Organize the information coherently
    /// - Write fluent prose
    /// - Preserve citations
    /// 
    /// The LLM does NOT:
    /// - Judge importance (already done by extraction)
    /// - Retrieve information (already done by retrieval)
    /// - Add new information (not in the segments)
    /// </summary>
    private async Task<DocumentSummary> SynthesizeAsync(
        string docId,
        List<Segment> retrieved,
        ExtractionResult extraction,
        string? focusQuery,
        CancellationToken ct)
    {
        // Filter out heavy code; keep mostly prose
        var synthesisSegments = FilterForSynthesis(retrieved, includeCodeFallback: true);
        
        // Group segments by section for structure
        var bySection = synthesisSegments
            .GroupBy(s => s.SectionTitle)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .ToList();
        
        // Build context for synthesis
        var contextBuilder = new StringBuilder();
        contextBuilder.AppendLine("# Retrieved Segments");
        contextBuilder.AppendLine();
        
        foreach (var segment in synthesisSegments)
        {
            contextBuilder.AppendLine($"{segment.Citation} [{segment.Type}] {segment.Text}");
        }
        
        // Build section structure
        var sectionStructure = new StringBuilder();
        if (bySection.Count > 1)
        {
            sectionStructure.AppendLine("# Document Structure");
            foreach (var section in bySection)
            {
                sectionStructure.AppendLine($"- {section.Key}: {section.Count()} segments");
            }
            sectionStructure.AppendLine();
        }
        
        // Coverage gating
        var coverage = extraction.AllSegments.Count == 0
            ? 0
            : (double)synthesisSegments.Count / extraction.AllSegments.Count;
        var targetWords = Template.TargetWords > 0 ? Template.TargetWords : 300;
        
        // Extract document title from the first H1 heading - this is CRITICAL
        // In markdown, the first H1 (# Title) is always the document/article title
        var headings = extraction.AllSegments
            .Where(s => s.Type == SegmentType.Heading)
            .OrderBy(s => s.Index)
            .ToList();
        
        if (_verbose)
        {
            VerboseHelper.Log(_verbose, $"[dim]Found {headings.Count} headings in AllSegments[/]");
            foreach (var h in headings.Take(5))
            {
                var escapedText = VerboseHelper.Escape(h.Text);
                VerboseHelper.Log($"[dim]  H{h.HeadingLevel}: \"{escapedText}\" (idx={h.Index})[/]");
            }
        }
        
        var documentTitle = headings
            .Where(s => s.HeadingLevel == 1)
            .Select(s => s.Text.TrimStart('#', ' '))
            .FirstOrDefault();
        
        // If no H1 heading found, try to extract title from text content
        if (string.IsNullOrEmpty(documentTitle))
        {
            documentTitle = ExtractTitleFromContent(synthesisSegments, docId);
        }
        
        if (_verbose)
        {
            var escapedTitle = VerboseHelper.Escape(documentTitle ?? "(not found)");
            VerboseHelper.Log(_verbose, $"[dim]Document title: {escapedTitle}[/]");
        }
        
        var synthesisPrompt = BuildSynthesisPrompt(
            extraction.ContentType, 
            targetWords, 
            focusQuery, 
            sectionStructure.ToString(), 
            contextBuilder.ToString(),
            coverage,
            documentTitle);
        
        // Check if Ollama is available for LLM synthesis
        string executiveSummary;
        var ollamaAvailable = await _ollama.IsAvailableAsync();
        
        if (ollamaAvailable)
        {
            var rawSummary = await _ollama.GenerateAsync(synthesisPrompt, temperature: 0.3);
            executiveSummary = CleanSynthesisResponse(rawSummary);
        }
        else
        {
            // Fallback to extractive summary (no LLM polishing)
            VerboseHelper.Log(_verbose, "[yellow]Ollama not available - using extractive summary only[/]");
            executiveSummary = BuildExtractiveSummary(synthesisSegments, targetWords, documentTitle);
        }
        
        // === FACT SANITY PASS: Verify key claims against evidence ===
        // For narrative content, run a quick fact-check to catch "Captain Morstan's father" type drift
        // Only run if Ollama is available (fact-check uses LLM)
        if (ollamaAvailable && extraction.ContentType == ContentType.Narrative && synthesisSegments.Count >= 5)
        {
            executiveSummary = await FactCheckSummaryAsync(executiveSummary, synthesisSegments, ct);
        }
        
        // Only add coverage metadata if template wants it
        if (Template.IncludeCoverageMetadata)
        {
            executiveSummary = ApplyCoverageGuard(executiveSummary, coverage);
            executiveSummary = AppendCoverageFooter(executiveSummary, coverage);
        }
        
        // Build topic summaries from sections with lightweight annotations
        var topicSummaries = new List<TopicSummary>();
        foreach (var section in bySection.Take(10))
        {
            var sectionSegments = section.OrderBy(s => s.Index).ToList();
            var sectionText = string.Join(" ", sectionSegments.Select(s => $"{s.Text} {s.Citation}"));
            var sourceRefs = sectionSegments.Select(s => s.Id).ToList();
            var annotation = BuildSectionAnnotation(section.Key, sectionSegments);
            var annotatedText = string.IsNullOrEmpty(annotation)
                ? sectionText
                : $"Annotation: {annotation}\n{sectionText}";
            
            topicSummaries.Add(new TopicSummary(section.Key, annotatedText, sourceRefs));
        }
        
        // Extract entities from filtered segments
        var entities = ExtractEntities(synthesisSegments, extraction.ContentType);
        
        return new DocumentSummary(
            executiveSummary,
            topicSummaries,
            new List<string>(), // Open questions could be added via another LLM call if needed
            new SummarizationTrace(docId, 0, 0, new List<string>(), TimeSpan.Zero, coverage, 0),
            entities
        );
    }

    private static List<Segment> FilterForSynthesis(List<Segment> segments, bool includeCodeFallback)
    {
        var prose = segments.Where(s => s.Type != SegmentType.CodeBlock).ToList();
        if (prose.Count >= 8) return prose;
        if (!includeCodeFallback) return prose;
        var needed = 8 - prose.Count;
        var code = segments.Where(s => s.Type == SegmentType.CodeBlock).Take(needed).ToList();
        return prose.Concat(code).ToList();
    }
    
    /// <summary>
    /// Fact-check the executive summary against retrieved evidence.
    /// Extracts key facts from early segments (setup/premise) and patches the summary if it contradicts them.
    /// This catches "Captain Morstan's father" type drift where the LLM confuses character relationships.
    /// </summary>
    private async Task<string> FactCheckSummaryAsync(
        string summary, 
        List<Segment> segments, 
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summary) || segments.Count < 3)
            return summary;
        
        // Focus on early segments (setup/premise) where key facts are established
        var earlySegments = segments
            .OrderBy(s => s.Index)
            .Take(Math.Min(8, segments.Count))
            .ToList();
        
        var evidenceText = string.Join("\n", earlySegments.Select(s => s.Text));
        
        // Prompt for fact extraction - keep it small and focused
        var factPrompt = $"""
            From this text excerpt, extract ONLY factual claims about:
            1. Main character's name and their central problem/goal
            2. Key relationships (who is related to whom, how)
            3. Central mystery or conflict (what is missing/sought/at stake)
            
            Text:
            {evidenceText}
            
            Output 3-5 short factual statements, one per line. Be precise about names and relationships.
            Example format:
            - Mary Morstan's father disappeared ten years ago
            - Major Sholto was Captain Morstan's colleague
            - The treasure is connected to the Agra fort
            
            Facts:
            """;
        
        string facts;
        try
        {
            facts = await _ollama.GenerateAsync(factPrompt, temperature: 0.1);
        }
        catch
        {
            // If fact extraction fails, return original summary
            return summary;
        }
        
        if (string.IsNullOrWhiteSpace(facts))
            return summary;
        
        // Check for contradictions between summary and facts
        var checkPrompt = $"""
            Compare this summary against the verified facts below.
            If the summary contradicts any fact (wrong names, wrong relationships, wrong events), 
            output a corrected version of the summary.
            If there are no contradictions, output the summary unchanged.
            
            VERIFIED FACTS:
            {facts}
            
            SUMMARY TO CHECK:
            {summary}
            
            RULES:
            - Only fix factual errors (wrong names, relationships, events)
            - Keep the same style, length, and structure
            - Do NOT add new information not in the original summary
            - Do NOT change opinions or interpretations, only facts
            - Do NOT explain what you changed - just output the corrected summary
            - Do NOT add preamble like "Here is the corrected summary" - just output it
            - Do NOT add a list of corrections at the end
            
            Output ONLY the corrected summary text (nothing else):
            """;
        
        try
        {
            var corrected = await _ollama.GenerateAsync(checkPrompt, temperature: 0.1);
            
            // Clean the response - remove meta-commentary
            corrected = CleanFactCheckResponse(corrected);
            
            // Basic sanity check - corrected should be similar length to original
            if (!string.IsNullOrWhiteSpace(corrected) && 
                corrected.Length >= summary.Length * 0.5 &&
                corrected.Length <= summary.Length * 1.5)
            {
                var cleaned = CleanSynthesisResponse(corrected);
                if (_verbose && cleaned != summary)
                {
                    VerboseHelper.Log(_verbose, "[dim yellow]Fact-check pass made corrections[/]");
                }
                return cleaned;
            }
        }
        catch
        {
            // If correction fails, return original summary
        }
        
        return summary;
    }
    
    /// <summary>
    /// Clean fact-check response to remove meta-commentary that LLMs tend to add.
    /// </summary>
    private static string CleanFactCheckResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return response;
        
        var lines = response.Split('\n').ToList();
        var cleaned = new List<string>();
        var inContent = false;
        var skipRest = false;
        
        // Preamble patterns to skip (LLM meta-commentary before the actual content)
        var preamblePatterns = new[]
        {
            "after comparing",
            "here is the corrected",
            "here's the corrected",
            "the corrected summary",
            "i found",
            "i corrected",
            "comparing the summary",
            "here is the revised",
            "here's the revised",
            "below is the corrected",
            "the following is the corrected",
            "i have corrected",
            "i've corrected",
            "a few contradictions",
            "few contradictions",
            "some contradictions",
            "no contradictions",
            "corrected version",
            "revised version",
            "updated summary",
            "the summary with corrections"
        };
        
        // Trailing patterns to stop at
        var trailingPatterns = new[]
        {
            "i corrected the following",
            "corrections made:",
            "changes made:",
            "* removed",
            "* changed",
            "* corrected",
            "- removed",
            "- changed",
            "- corrected",
            "note:",
            "notes:",
            "key corrections:",
            "the changes i made",
            "changes include:"
        };
        
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var lower = trimmed.ToLowerInvariant();
            
            // Skip preamble lines - keep scanning until we hit real content
            if (!inContent)
            {
                // Skip blank lines in preamble
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;
                
                // Check if this line contains any preamble pattern
                var isPreamble = preamblePatterns.Any(p => lower.Contains(p));
                if (isPreamble)
                    continue;
                
                // We've hit real content - this line and following are content
                inContent = true;
            }
            
            // Skip trailing meta-commentary
            if (inContent && !skipRest)
            {
                var isTrailing = trailingPatterns.Any(p => lower.StartsWith(p));
                if (isTrailing)
                {
                    skipRest = true;
                    continue;
                }
            }
            
            if (!skipRest)
                cleaned.Add(line);
        }
        
        return string.Join("\n", cleaned).Trim();
    }

    /// <summary>
    /// Build a pure extractive summary when LLM is not available.
    /// Concatenates the top segments with their citations.
    /// </summary>
    private static string BuildExtractiveSummary(List<Segment> segments, int targetWords, string? documentTitle)
    {
        var sb = new StringBuilder();
        
        if (!string.IsNullOrEmpty(documentTitle))
        {
            sb.AppendLine($"**{documentTitle}**");
            sb.AppendLine();
        }
        
        var wordCount = 0;
        foreach (var segment in segments.OrderByDescending(s => s.SalienceScore))
        {
            var text = segment.Text.Trim();
            var segmentWords = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            
            if (wordCount + segmentWords > targetWords * 1.2 && wordCount > 0)
                break;
                
            sb.AppendLine($"- {text} {segment.Citation}");
            wordCount += segmentWords;
        }
        
        return sb.ToString().Trim();
    }
    
    /// <summary>
    /// Entity extraction from retrieved segments with aggressive filtering to reduce noise.
    /// Filters: stopwords, pronouns, determiners, sentence adverbs, requires frequency ≥2, titlecase validation.
    /// </summary>
    private static ExtractedEntities ExtractEntities(List<Segment> segments, ContentType contentType)
    {
        // Skip entity extraction for expository/technical content - produces noise (code tokens)
        if (contentType == ContentType.Expository)
            return ExtractedEntities.Empty;
        
        // For unknown content type, check if it looks like code-heavy content
        var text = string.Join(" ", segments.Select(s => s.Text));
        if (contentType == ContentType.Unknown && LooksLikeCodeContent(text))
            return ExtractedEntities.Empty;
            
        var tokens = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim(
                ',', '.', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']', '{', '}', '—', '–'))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        
        var honorifics = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
        { 
            "Mr.", "Mr", "Mrs.", "Mrs", "Miss", "Ms.", "Ms", "Dr.", "Dr", 
            "Captain", "Inspector", "Professor", "Sir", "Lady", "Lord", "Colonel", "Major"
        };
        
        var placeStop = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
        { 
            "Street", "St", "Wharf", "Yard", "Road", "Lane", "Court", "Avenue", "Place", "Square"
        };
        
        var dayStop = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
        { 
            "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"
        };
        
        var monthStop = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
        { 
            "January", "February", "March", "April", "May", "June", 
            "July", "August", "September", "October", "November", "December"
        };
        
        // Stopwords: pronouns, determiners, sentence adverbs, conjunctions, prepositions
        var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Pronouns
            "I", "Me", "My", "Mine", "Myself",
            "You", "Your", "Yours", "Yourself", "Yourselves",
            "He", "Him", "His", "Himself",
            "She", "Her", "Hers", "Herself",
            "It", "Its", "Itself",
            "We", "Us", "Our", "Ours", "Ourselves",
            "They", "Them", "Their", "Theirs", "Themselves",
            "Who", "Whom", "Whose", "Which", "What", "That", "This", "These", "Those",
            "One", "Ones", "Someone", "Anyone", "Everyone", "No one", "Nobody",
            
            // Determiners and articles
            "The", "A", "An", "Some", "Any", "No", "Every", "Each", "Either", "Neither",
            "All", "Both", "Half", "Several", "Many", "Much", "Few", "Little", "Other", "Another",
            
            // Sentence adverbs and conjunctions (often start sentences)
            "However", "Therefore", "Moreover", "Furthermore", "Nevertheless", "Nonetheless",
            "Meanwhile", "Otherwise", "Instead", "Indeed", "Thus", "Hence", "Accordingly",
            "Yet", "Still", "Also", "Too", "Even", "Just", "Only", "Perhaps", "Maybe",
            "Although", "Though", "While", "When", "Where", "Because", "Since", "Unless",
            "But", "And", "Or", "Nor", "So", "For", "After", "Before", "Until", "During",
            
            // Common non-name capitalized words
            "Chapter", "Part", "Book", "Section", "Volume", "Page", "Note", "Notes",
            "Introduction", "Conclusion", "Summary", "Appendix", "Index", "Contents",
            "Here", "There", "Now", "Then", "Today", "Tomorrow", "Yesterday",
            "Yes", "No", "Well", "Oh", "Ah", "Alas",
            
            // Misc place/time words
            "Baker", "Street", "Wharf", "East", "West", "North", "South",
            "Morning", "Afternoon", "Evening", "Night", "Day", "Week", "Month", "Year",
            
            // Project Gutenberg boilerplate (common in public domain texts)
            "Project", "Gutenberg", "Foundation", "Archive", "Literary", "License", "Ebook",
            "Copyright", "Trademark", "Donations", "Volunteers", "How", "Why", "Whether",
            "Let", "Could", "Would", "Should", "Must", "Shall", "Will", "May", "Might",
            
            // Generic words that look like names but aren't
            "Island", "River", "Lake", "Mountain", "Valley", "Forest", "Garden", "Park",
            "Castle", "Palace", "House", "Hall", "Tower", "Bridge", "Gate", "Door"
        };
        
        // Code/technical keywords that should not be treated as character names
        var codeStop = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
        { 
            // Assembly/low-level
            "MOV", "MOVX", "CALL", "RET", "JMP", "JNZ", "JZ", "PUSH", "POP", "ADD", "SUB", "MUL", "DIV",
            // VB/Basic
            "Dim", "Sub", "Function", "End", "If", "Then", "Else", "For", "Next", "Do", "Loop", "While",
            // C-style
            "Int", "Void", "Char", "Float", "Double", "Bool", "String", "Null", "True", "False",
            // Common programming
            "API", "HTTP", "JSON", "XML", "HTML", "CSS", "URL", "URI", "SQL", "REST", "GET", "POST", "PUT", "DELETE",
            "CPU", "GPU", "RAM", "ROM", "BIOS", "FPGA", "ASIC", "VHDL", "HDL", "RTL",
            // General technical acronyms
            "ID", "IO", "UI", "UX", "OS", "VM", "SDK", "IDE", "CLI", "GUI"
        };
        
        var candidates = new List<string>();
        int i = 0;
        while (i < tokens.Count)
        {
            var tok = tokens[i];
            if (IsProper(tok))
            {
                var span = new List<string> { tok };
                var j = i + 1;
                while (j < tokens.Count && IsContinuation(tokens[j]))
                {
                    span.Add(tokens[j]);
                    j++;
                }
                var spanText = string.Join(" ", span);
                candidates.Add(spanText);
                i = j;
            }
            else
            {
                i++;
            }
        }
        
        // Group by normalized form and count occurrences
        var grouped = candidates
            .Select(c => c.Trim())
            .Where(c => c.Length > 2)
            .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .ToList();
        
        var final = new List<string>();
        foreach (var (name, count) in grouped.OrderByDescending(g => g.Count))
        {
            // Skip single-word stopwords, pronouns, determiners
            var words = name.Split(' ');
            if (words.Length == 1 && stopwords.Contains(name)) continue;
            
            // Skip if first word is a stopword and there's no honorific
            if (words.Length > 0 && stopwords.Contains(words[0]) && !honorifics.Contains(words[0])) continue;
            
            if (dayStop.Contains(name) || monthStop.Contains(name)) continue;
            if (placeStop.Contains(name)) continue;
            if (honorifics.Contains(name)) continue; // Just honorific alone (e.g., "Mr.")
            if (codeStop.Contains(name)) continue;
            
            // Skip all-uppercase words (likely acronyms/code)
            if (name.Length <= 5 && name.All(char.IsUpper)) continue;
            
            // FREQUENCY FILTER: Require at least 2 occurrences for single words
            // (multi-word names with honorifics can appear once)
            bool hasHonorific = words.Length > 1 && honorifics.Contains(words[0]);
            if (words.Length == 1 && count < 2 && !hasHonorific) continue;
            
            // TITLECASE VALIDATION: Must be titlecase and alphabetic (allow spaces, apostrophes, hyphens)
            if (!IsValidEntityName(name)) continue;
            
            final.Add(name);
            if (final.Count >= 12) break;
        }
        
        return new ExtractedEntities(final, new List<string>(), new List<string>(), new List<string>(), new List<string>());
        
        bool IsProper(string token)
        {
            // Reject stopwords immediately
            if (stopwords.Contains(token)) return false;
            // Reject code keywords
            if (codeStop.Contains(token)) return false;
            // Reject all-caps short words (likely acronyms/code)
            if (token.Length <= 5 && token.All(char.IsUpper)) return false;
            // Accept honorifics or proper nouns (capital start, rest lowercase letters)
            return honorifics.Contains(token) || 
                   (token.Length > 1 && char.IsUpper(token[0]) && token.Skip(1).All(c => char.IsLower(c) || c == '\''));
        }
        
        bool IsContinuation(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            if (placeStop.Contains(token)) return true;
            if (honorifics.Contains(token)) return true;
            // Only continue with proper nouns (capital start, rest lowercase)
            if (token.Length > 1 && char.IsUpper(token[0]) && token.Skip(1).All(c => char.IsLower(c) || c == '\'')) return true;
            return false;
        }
        
        bool IsValidEntityName(string name)
        {
            // Must have at least one letter
            if (!name.Any(char.IsLetter)) return false;
            
            var words = name.Split(' ');
            foreach (var word in words)
            {
                if (string.IsNullOrWhiteSpace(word)) continue;
                // Allow honorifics as-is
                if (honorifics.Contains(word)) continue;
                // Allow place suffixes in multi-word names
                if (placeStop.Contains(word) && words.Length > 1) continue;
                
                // Must be titlecase: first char upper, rest lower (allow apostrophes, hyphens)
                if (word.Length < 1) continue;
                if (!char.IsUpper(word[0])) return false;
                if (word.Length > 1 && !word.Skip(1).All(c => char.IsLower(c) || c == '\'' || c == '-')) return false;
            }
            return true;
        }
    }
    
    /// <summary>
    /// Check if text appears to be code-heavy content (FPGA, assembly, programming)
    /// </summary>
    private static bool LooksLikeCodeContent(string text)
    {
        var lower = text.ToLowerInvariant();
        var codeIndicators = new[] { "```", "function", "class", "void", "int ", "return", "if (", "for (", "while (", 
            "0x", "mov ", "call ", "push ", "pop ", "fpga", "vhdl", "register", "memory address" };
        var codeCount = codeIndicators.Count(ind => lower.Contains(ind));
        return codeCount >= 3;
    }
    
    /// <summary>
    /// Extract document title from content when no H1 heading is found.
    /// Looks for Project Gutenberg format, "Title:" lines, or falls back to filename.
    /// </summary>
    private static string? ExtractTitleFromContent(List<Segment> segments, string docId)
    {
        if (segments.Count == 0) return FallbackToFilename(docId);
        
        // Get first few segments to search for title
        var earlyText = string.Join("\n", segments.Take(5).Select(s => s.Text));
        var lines = earlyText.Split('\n').Take(50).ToList();
        
        string? title = null;
        string? author = null;
        
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            
            // Skip empty or very short lines
            if (trimmed.Length < 3) continue;
            
            // Project Gutenberg format: "Title: X"
            if (trimmed.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
            {
                title = trimmed[6..].Trim();
                continue;
            }
            
            // Author line
            if (trimmed.StartsWith("Author:", StringComparison.OrdinalIgnoreCase))
            {
                author = trimmed[7..].Trim();
                continue;
            }
            
            // "by Author Name" pattern
            if (trimmed.StartsWith("by ", StringComparison.OrdinalIgnoreCase) && trimmed.Length < 50)
            {
                author = trimmed[3..].Trim();
                continue;
            }
            
            // Look for book title patterns (all caps, or titlecase line by itself)
            if (title == null && trimmed.Length > 5 && trimmed.Length < 100)
            {
                // Skip Project Gutenberg boilerplate
                if (trimmed.Contains("Project Gutenberg", StringComparison.OrdinalIgnoreCase)) continue;
                if (trimmed.Contains("EBook", StringComparison.OrdinalIgnoreCase)) continue;
                if (trimmed.Contains("www.", StringComparison.OrdinalIgnoreCase)) continue;
                
                // Check if it looks like a title (titlecase, no sentence-ending punctuation mid-line)
                var words = trimmed.Split(' ');
                var titlecaseCount = words.Count(w => w.Length > 0 && char.IsUpper(w[0]));
                if (titlecaseCount >= words.Length * 0.6 && !trimmed.Contains(". "))
                {
                    // Likely a title - check it's not a sentence
                    if (!trimmed.EndsWith(",") && !trimmed.Contains(" is ") && !trimmed.Contains(" are "))
                    {
                        title = trimmed;
                    }
                }
            }
        }
        
        // Fall back to filename
        if (string.IsNullOrEmpty(title))
        {
            return FallbackToFilename(docId);
        }
        
        if (title != null && author != null)
            return $"{title} by {author}";
        return title;
    }
    
    /// <summary>
    /// Convert docId/filename to a readable title
    /// </summary>
    private static string? FallbackToFilename(string docId)
    {
        var fileName = Path.GetFileNameWithoutExtension(docId);
        if (string.IsNullOrEmpty(fileName) || fileName.Length <= 3) return null;
        
        // Clean up filename: replace separators with spaces, titlecase
        var cleaned = fileName
            .Replace('-', ' ')
            .Replace('_', ' ')
            .Replace(".", " ");
        
        // Remove common suffixes like _summary, _final, etc.
        var suffixes = new[] { " summary", " final", " draft", " v1", " v2" };
        foreach (var suffix in suffixes)
        {
            if (cleaned.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned[..^suffix.Length];
        }
        
        return cleaned.Trim();
    }

    /// <summary>
    /// Cosine similarity between query embedding and segment embedding
    /// </summary>
    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        
        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom > 0 ? dot / denom : 0;
    }

    private static DocumentSummary CreateEmptySummary(string docId, string message)
    {
        return new DocumentSummary(
            message,
            new List<TopicSummary>(),
            new List<string>(),
            new SummarizationTrace(docId, 0, 0, new List<string>(), TimeSpan.Zero, 0, 0),
            ExtractedEntities.Empty
        );
    }

    /// <summary>
    /// Build content-type-aware synthesis prompt.
    /// 
    /// For NARRATIVE (fiction): Focus on plot, characters, setting, themes
    /// For EXPOSITORY (technical): Focus on key points, structure, citations
    /// 
    /// If template has a custom ExecutivePrompt, use that instead.
    /// </summary>
    private string BuildSynthesisPrompt(
        ContentType contentType,
        int targetWords,
        string? focusQuery,
        string sectionStructure,
        string context,
        double coverage,
        string? documentTitle = null)
    {
        var focusLine = string.IsNullOrEmpty(focusQuery) ? "" : $"FOCUS: {focusQuery}\n";
        
        // Build document title line - this is CRITICAL for accurate summaries
        // The first H1 heading typically contains the document name/subject
        var titleLine = string.IsNullOrEmpty(documentTitle) 
            ? "" 
            : $"DOCUMENT TITLE: {documentTitle}\nYou are summarizing a document titled \"{documentTitle}\". Use this title to accurately identify the subject.\n\n";
        
        // For short templates, don't add coverage warnings - they bloat the output
        var isCompact = targetWords <= 100;
        var coverageLine = isCompact ? "" : (coverage < 0.05
            ? "Note: Coverage is low. Focus on what IS present, avoid definitive conclusions about the whole."
            : "");
        
        // Check if template has custom prompt - if so, use it with context
        if (!string.IsNullOrEmpty(Template.ExecutivePrompt))
        {
            // Build topic summaries string from context for template placeholder
            var topicSummaries = context;
            var customPrompt = Template.GetExecutivePrompt(topicSummaries, focusQuery);
            
            // Add document title at the start for custom prompts too
            var titlePrefix = string.IsNullOrEmpty(documentTitle) ? "" : $"DOCUMENT: {documentTitle}\n\n";
            
            // Add strict word limit for compact templates
            var wordLimit = isCompact 
                ? $"\n\nSTRICT WORD LIMIT: Maximum {targetWords} words. Do NOT exceed this. Be extremely concise."
                : "";
            
            return titlePrefix + customPrompt + wordLimit;
        }
        
        // Build word limit instruction - stricter for small targets
        var wordInstruction = targetWords switch
        {
            <= 30 => $"STRICT: Write EXACTLY 1 sentence, maximum {targetWords} words. No preamble.",
            <= 60 => $"STRICT: Write 2-3 sentences maximum, no more than {targetWords} words total.",
            <= 100 => $"STRICT: Maximum {targetWords} words. Be extremely concise.",
            <= 200 => $"Write approximately {targetWords} words (±20%).",
            _ => $"Write approximately {targetWords} words in 3-6 paragraphs."
        };
        
        if (contentType == ContentType.Narrative)
        {
            // FICTION/NARRATIVE prompt - book report style
            return $"""
                {titleLine}You are writing a book report / plot summary. Your job is to describe WHAT HAPPENS, not transcribe dialogue.

                INSTRUCTIONS:
                1. Write at the SCENE level, not the dialogue level
                2. Describe ACTIONS: who did what, where, when
                3. Identify KEY CHARACTERS and their roles (detective, narrator, client, villain)
                4. Describe the SETTING (place, time period, atmosphere)
                5. Identify the INCITING INCIDENT (what starts the plot)
                6. Note any THEMES or central conflicts
                7. Use past tense, third person
                8. {wordInstruction}

                IMPORTANT:
                - Do NOT include citation references like [s1], [s2] in your text - write clean prose
                - Do NOT quote dialogue verbatim (paraphrase instead)
                - Do NOT list every conversation ("He said X, she replied Y")
                - Do NOT treat all moments as equally important
                - Do NOT invent characters or events not in the segments below

                {coverageLine}

                {focusLine}
                {sectionStructure}
                {context}

                Write a book report that describes what happens:
                """;
        }
        else
        {
            // EXPOSITORY/TECHNICAL prompt - improved synthesis
            return $"""
                {titleLine}You are generating a factual executive summary from retrieved document segments.
                Your goals are: accuracy, clarity, non-redundancy, readable human prose.
                This is NOT a rewrite of source text. It is a synthesis.

                RULES:
                1. Do NOT mention segment IDs, citations, or list indices in the prose.
                   Evidence tracking is handled separately - write clean readable text.
                2. Do NOT repeat the same idea more than once, even if it appears in multiple segments.
                   If several segments say the same thing, summarize the idea once.
                3. Prefer the author's intent over literal phrasing.
                   If the author corrects themselves, describe the correction clearly and briefly.
                4. Write like a technical blog summary, not an academic paper.
                   Natural language. No parenthetical references. No hedging unless uncertainty is explicit.
                5. If the document is reflective or corrective, capture the arc:
                   - what was originally claimed
                   - why it was wrong or outdated  
                   - what the author now believes or intends to do
                6. Avoid absolutist or textbook language unless the source explicitly uses it.
                   Good: "the author realised compression doesn't remove encryption"
                   Bad: "compression and encryption are exclusive processes"
                7. Assume the reader is technical but not reading the source.
                   Be concise, but explanatory where it helps understanding.
                8. {wordInstruction}

                {coverageLine}

                ANTI-REDUNDANCY: Before writing, mentally group the retrieved segments by meaning.
                Each group should appear AT MOST ONCE in the summary.

                {focusLine}
                {sectionStructure}
                {context}

                Write a clean executive summary (no citations in text):
                """;
        }
    }

    /// <summary>
    /// Remove common LLM preamble patterns from synthesis responses
    /// </summary>
    private static string CleanSynthesisResponse(string response)
    {
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var cleaned = new List<string>();
        var foundContent = false;
        
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            
            // Skip common preamble patterns at the start
            if (!foundContent)
            {
                if (trimmed.StartsWith("Here is", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Here are", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Here's", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Below is", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("The following", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Based on", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("I'll", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Let me", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Sure", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Certainly", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(trimmed))
                    continue;
                    
                foundContent = true;
            }
            
            cleaned.Add(line);
        }
        
        // If nothing left, return original (might have been all content)
        if (cleaned.Count == 0)
            return response.Trim();
            
        return string.Join("\n", cleaned).Trim();
    }

    private static string ApplyCoverageGuard(string summary, double coverage)
    {
        if (coverage >= 0.05) return summary;
        var banned = new[] { "ultimately", "as the story unfolds", "it becomes clear", "finally", "in the end" };
        var guarded = summary;
        foreach (var term in banned)
        {
            guarded = System.Text.RegularExpressions.Regex.Replace(
                guarded,
                System.Text.RegularExpressions.Regex.Escape(term),
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        // Prepend disclaimer
        var disclaimer = $"Based on sampled sections only (~{coverage:P1}), the excerpt suggests:";
        return $"{disclaimer}\n{guarded.Trim()}";
    }

    private static string AppendCoverageFooter(string summary, double coverage)
    {
        var scope = coverage < 0.05 ? "sampled scenes" : "document segments covered";
        var confidence = coverage < 0.05 ? "Low" : coverage < 0.15 ? "Medium" : "High";
        var footer = $"\n\nCoverage: {coverage:P1} ({scope})\nConfidence: {confidence}";
        return summary.TrimEnd() + footer;
    }

    private static string BuildSectionAnnotation(string sectionTitle, List<Segment> sectionSegments)
    {
        if (!sectionSegments.Any()) return string.Empty;
        var first = sectionSegments.First();
        var who = first.SectionTitle;
        var what = first.Type.ToString();
        return $"Focuses on {sectionTitle}; showcases {what.ToLowerInvariant()} tied to this thread.";
    }

    public void Dispose()
    {
        _extractor.Dispose();
        _queryEmbedder.Dispose();
    }
    
    public async ValueTask DisposeAsync()
    {
        _extractor.Dispose();
        _queryEmbedder.Dispose();
        
        if (_vectorStore != null)
        {
            await _vectorStore.DisposeAsync();
        }
    }
}
