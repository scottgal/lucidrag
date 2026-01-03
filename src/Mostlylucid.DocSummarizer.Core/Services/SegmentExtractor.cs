using System.Diagnostics;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services.Onnx;


namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Extracts segments from a document, computes embeddings and salience scores.
/// 
/// This is the "signal condenser" phase of the BERT→RAG pipeline:
/// - Parses document into atomic segments (sentences, list items, code, tables)
/// - Generates embeddings for each segment (sentence-transformer model)
/// - Computes salience scores using MMR (relevance to centroid + diversity)
/// - Produces a query-agnostic document representation ready for retrieval
/// </summary>
public class SegmentExtractor : IDisposable
{
    private readonly OnnxEmbeddingService _embeddingService;
    private readonly MarkdigDocumentParser _parser;
    private readonly ExtractionConfig _config;
    private readonly bool _verbose;

    public SegmentExtractor(OnnxConfig onnxConfig, ExtractionConfig? config = null, bool verbose = false)
    {
        _embeddingService = new OnnxEmbeddingService(onnxConfig, verbose);
        _parser = new MarkdigDocumentParser();
        _config = config ?? new ExtractionConfig();
        _verbose = verbose;
    }

    /// <summary>
    /// Extract segments from markdown content with embeddings and salience scores.
    /// For very long documents (novels), uses hierarchical extraction to manage memory.
    /// </summary>
    public async Task<ExtractionResult> ExtractAsync(
        string docId,
        string markdown,
        ContentType contentType = ContentType.Unknown,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        
        // 1. Parse document into segments
        ProgressService.WriteVerboseMarkup(_verbose, "[dim]Parsing document into segments...[/]");
        var segments = ParseToSegments(docId, markdown, contentType);
        
        if (segments.Count == 0)
        {
            return new ExtractionResult
            {
                AllSegments = new List<Segment>(),
                TopBySalience = new List<Segment>(),
                ContentType = contentType,
                ExtractionTime = stopwatch.Elapsed
            };
        }
        
        // 2. Detect content type from segments if unknown (more accurate than text heuristics)
        var effectiveContentType = contentType;
        if (contentType == ContentType.Unknown)
        {
            effectiveContentType = DetectContentTypeFromSegments(segments);
            if (ProgressService.ShouldShowVerbose(_verbose) && effectiveContentType != ContentType.Unknown)
                VerboseHelper.Log(_verbose, $"[dim]Detected content type: {effectiveContentType}[/]");
        }
        
        if (ProgressService.ShouldShowVerbose(_verbose))
        {
            var counts = segments.GroupBy(s => s.Type).Select(g => $"{g.Key}:{g.Count()}");
            VerboseHelper.Log($"[dim]Found {segments.Count} segments ({string.Join(", ", counts)})[/]");
        }
        
        // ALWAYS use semantic pre-filtering for large docs (> MaxSegmentsToEmbed)
        // This is much faster than hierarchical extraction which embeds everything
        var segmentsToEmbed = segments;
        float[]? centroid = null;
        
        if (_config.UseFastPreFilter && segments.Count > _config.MaxSegmentsToEmbed)
        {
            // For very large docs (books), increase the pre-filter budget proportionally
            // but cap it to avoid embedding thousands of segments
            var targetEmbedCount = Math.Min(
                _config.MaxSegmentsToEmbed * 2, // Allow 2x for large docs
                Math.Max(_config.MaxSegmentsToEmbed, segments.Count / 10) // Or 10% of doc
            );
            targetEmbedCount = Math.Min(targetEmbedCount, 500); // Hard cap at 500
            
            ProgressService.WriteVerboseMarkup(_verbose, $"[dim]Large document ({segments.Count} segments) - semantic pre-filtering to ~{targetEmbedCount}[/]");
            (segmentsToEmbed, centroid) = await SemanticPreFilterAsync(segments, targetEmbedCount, ct);
            ProgressService.WriteVerboseMarkup(_verbose, $"[dim]Pre-filtered to {segmentsToEmbed.Count} segments[/]");
        }
        else if (segments.Count > _config.MaxSegmentsToEmbed)
        {
            // Fallback: if pre-filter disabled but too many segments, use hierarchical
            ProgressService.WriteVerboseMarkup(_verbose, $"[dim]Large document ({segments.Count} segments) - using hierarchical extraction[/]");
            return await ExtractHierarchicalAsync(docId, segments, contentType, stopwatch, ct);
        }
        else
        {
            // Standard extraction for normal-sized documents
            ProgressService.WriteVerboseMarkup(_verbose, "[dim]Generating embeddings...[/]");
            await GenerateEmbeddingsAsync(segmentsToEmbed, ct);
            
            // Calculate document centroid
            centroid = CalculateCentroid(segmentsToEmbed);
        }
        
        // 4. Score segments by salience using MMR (with content-type adjustments)
        ProgressService.WriteVerboseMarkup(_verbose, "[dim]Computing salience scores with MMR...[/]");
        ComputeSalienceScores(segments, centroid, effectiveContentType);
        
        // 5. Build fallback bucket (top-N by salience for coverage guarantee)
        var targetCount = CalculateTargetCount(segments.Count);
        var topBySalience = segments
            .OrderByDescending(s => s.SalienceScore)
            .Take(Math.Max(_config.FallbackBucketSize, targetCount))
            .ToList();
        
        stopwatch.Stop();
        
        if (_verbose)
        {
            VerboseHelper.Log(_verbose, $"[dim]Extraction complete: {segments.Count} segments, top {topBySalience.Count} by salience[/]");
            VerboseHelper.Log(_verbose, $"[dim]Time: {stopwatch.Elapsed.TotalSeconds:F1}s[/]");
        }
        
        return new ExtractionResult
        {
            AllSegments = segments,
            TopBySalience = topBySalience,
            Centroid = centroid,
            ContentType = effectiveContentType,
            ExtractionTime = stopwatch.Elapsed
        };
    }

    /// <summary>
    /// Hierarchical extraction for very long documents (novels, legal documents, etc.)
    /// 
    /// Strategy:
    /// 1. Divide segments into batches
    /// 2. Extract top-K from each batch (local salience)
    /// 3. Merge all batch winners
    /// 4. Re-rank globally using MMR
    /// 
    /// This keeps memory bounded while maintaining quality.
    /// </summary>
    private async Task<ExtractionResult> ExtractHierarchicalAsync(
        string docId,
        List<Segment> allSegments,
        ContentType contentType,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var batchSize = _config.MaxBatchSize;
        var numBatches = (int)Math.Ceiling((double)allSegments.Count / batchSize);
        
        // Segments to keep from each batch (aggressive pruning)
        var keepPerBatch = Math.Max(50, _config.MaxSegments / numBatches);
        
        VerboseHelper.Log(_verbose, $"Hierarchical extraction: {numBatches} batches, ~{keepPerBatch} kept per batch");
        
        var batchWinners = new List<Segment>();
        float[]? globalCentroid = null;
        
        // Use non-interactive processing (no Spectre progress bar in library mode)
        globalCentroid = await ProcessBatchesWithoutProgressAsync(
            allSegments, numBatches, batchSize, keepPerBatch, contentType, batchWinners, ct);
        
        VerboseHelper.Log(_verbose, $"Batch phase complete: {batchWinners.Count} candidates");
        
        // Global re-ranking with MMR
        VerboseHelper.Log(_verbose, "[dim]Global re-ranking with MMR...[/]");
        
        // Re-normalize centroid
        if (globalCentroid != null)
        {
            var norm = MathF.Sqrt(globalCentroid.Sum(x => x * x));
            if (norm > 0)
            {
                for (int i = 0; i < globalCentroid.Length; i++)
                    globalCentroid[i] /= norm;
            }
        }
        
        // Final MMR scoring across all batch winners (with content-type adjustments)
        ComputeSalienceScores(batchWinners, globalCentroid ?? Array.Empty<float>(), contentType);
        
        // Build final results
        var targetCount = CalculateTargetCount(allSegments.Count);
        var topBySalience = batchWinners
            .OrderByDescending(s => s.SalienceScore)
            .Take(Math.Max(_config.FallbackBucketSize, targetCount))
            .ToList();
        
        stopwatch.Stop();
        
        if (_verbose)
        {
            VerboseHelper.Log(_verbose, $"[dim]Hierarchical extraction complete: {allSegments.Count} total, {batchWinners.Count} candidates, top {topBySalience.Count}[/]");
            VerboseHelper.Log(_verbose, $"[dim]Time: {stopwatch.Elapsed.TotalSeconds:F1}s[/]");
        }
        
        return new ExtractionResult
        {
            AllSegments = batchWinners, // Only keep batch winners to save memory
            TopBySalience = topBySalience,
            Centroid = globalCentroid,
            ContentType = contentType,
            ExtractionTime = stopwatch.Elapsed
        };
    }

    /// <summary>
    /// Parse markdown into typed segments with position tracking
    /// </summary>
    private List<Segment> ParseToSegments(string docId, string markdown, ContentType contentType)
    {
        var parsedDoc = _parser.Parse(markdown);
        var segments = new List<Segment>();
        var charOffset = 0;
        var headingPath = new Stack<string>();
        
        // DEBUG: Print first few sections
        if (_verbose)
        {
            VerboseHelper.Log(_verbose, $"[dim]Parsed {parsedDoc.Sections.Count} sections:[/]");
            foreach (var s in parsedDoc.Sections.Take(5))
            {
                var escapedHeading = VerboseHelper.Escape(s.Heading);
                VerboseHelper.Log($"[dim]  Level {s.Level}: \"{escapedHeading}\" ({s.Heading.Length} chars)[/]");
            }
        }
        
        // Convert parsed sections to segments
        foreach (var section in parsedDoc.Sections)
        {
            // Update heading path
            while (headingPath.Count >= section.Level && headingPath.Count > 0)
                headingPath.Pop();
            if (!string.IsNullOrEmpty(section.Heading))
                headingPath.Push(section.Heading);
            
            var currentHeadingPath = string.Join(" > ", headingPath.Reverse());
            
            // Add heading as a segment if substantial
            // EXCEPTION: First H1 heading is ALWAYS included (it's the document title)
            var isDocumentTitle = section.Level == 1 && segments.Count == 0;
            if (!string.IsNullOrEmpty(section.Heading) && 
                (section.Heading.Length >= _config.MinSegmentLength || isDocumentTitle))
            {
                var headingSegment = new Segment(docId, section.Heading, SegmentType.Heading, segments.Count, charOffset, charOffset + section.Heading.Length)
                {
                    SectionTitle = section.Heading,
                    HeadingPath = currentHeadingPath,
                    HeadingLevel = section.Level
                };
                // Headings get slight salience boost (they're summary-like)
                // Document title gets extra boost
                headingSegment.PositionWeight = isDocumentTitle ? 2.0 : 1.1;
                segments.Add(headingSegment);
                charOffset += section.Heading.Length + 1;
            }
            
            // Add sentences
            foreach (var sentenceInfo in section.Sentences)
            {
                if (sentenceInfo.Text.Length < _config.MinSegmentLength)
                    continue;
                    
                var segment = new Segment(docId, sentenceInfo.Text, SegmentType.Sentence, segments.Count, charOffset, charOffset + sentenceInfo.Text.Length)
                {
                    SectionTitle = section.Heading,
                    HeadingPath = currentHeadingPath,
                    HeadingLevel = section.Level,
                    PositionWeight = sentenceInfo.PositionWeight
                };
                segments.Add(segment);
                charOffset += sentenceInfo.Text.Length + 1;
            }
            
            // Add list items as segments
            if (_config.IncludeListItems)
            {
                foreach (var item in section.ListItems)
                {
                    if (item.Length < _config.MinSegmentLength)
                        continue;
                        
                    var segment = new Segment(docId, item, SegmentType.ListItem, segments.Count, charOffset, charOffset + item.Length)
                    {
                        SectionTitle = section.Heading,
                        HeadingPath = currentHeadingPath,
                        HeadingLevel = section.Level,
                        // List items often contain key points
                        PositionWeight = 1.05
                    };
                    segments.Add(segment);
                    charOffset += item.Length + 1;
                }
            }
            
            // Add code blocks as segments
            if (_config.IncludeCodeBlocks)
            {
                foreach (var codeBlock in section.CodeBlocks)
                {
                    // Only include if code has meaningful content
                    var codeText = $"[{codeBlock.Language}] {codeBlock.Code}";
                    if (codeBlock.Code.Length < 10)
                        continue;
                        
                    // Truncate very long code blocks
                    if (codeText.Length > 500)
                        codeText = codeText[..500] + "...";
                        
                    var segment = new Segment(docId, codeText, SegmentType.CodeBlock, segments.Count, charOffset, charOffset + codeText.Length)
                    {
                        SectionTitle = section.Heading,
                        HeadingPath = currentHeadingPath,
                        HeadingLevel = section.Level,
                        // Code blocks are important for technical docs
                        PositionWeight = contentType == ContentType.Expository ? 1.15 : 0.9
                    };
                    segments.Add(segment);
                    charOffset += codeText.Length + 1;
                }
            }
            
            // Add quotes as segments
            foreach (var quote in section.Quotes)
            {
                if (quote.Length < _config.MinSegmentLength)
                    continue;
                    
                var segment = new Segment(docId, quote, SegmentType.Quote, segments.Count, charOffset, charOffset + quote.Length)
                {
                    SectionTitle = section.Heading,
                    HeadingPath = currentHeadingPath,
                    HeadingLevel = section.Level,
                    // Quotes are often highlighted for importance
                    PositionWeight = 1.1
                };
                segments.Add(segment);
                charOffset += quote.Length + 1;
            }
        }
        
        // Apply position weights based on document structure
        ApplyPositionWeights(segments, contentType);
        
        return segments;
    }

    /// <summary>
    /// Detect content type from parsed segments - more accurate than text heuristics.
    /// 
    /// Uses multiple signals:
    /// - Segment type distribution (dialogue quotes, code blocks)
    /// - Speech tag density ("said", "replied", "asked")
    /// - Pronoun + action patterns (he walked, she looked)
    /// - Technical markers (function, class, API)
    /// </summary>
    public static ContentType DetectContentTypeFromSegments(List<Segment> segments)
    {
        if (segments.Count == 0) return ContentType.Unknown;
        
        var sentences = segments.Where(s => s.Type == SegmentType.Sentence).ToList();
        var codeBlocks = segments.Count(s => s.Type == SegmentType.CodeBlock);
        
        // Sample text for analysis (first 100 sentences or all)
        var sampleSentences = sentences.Take(100).ToList();
        var sampleText = string.Join(" ", sampleSentences.Select(s => s.Text));
        var sampleLower = sampleText.ToLowerInvariant();
        
        // === FICTION SIGNALS ===
        var fictionScore = 0.0;
        
        // 1. Dialogue density - sentences starting with quotes
        var dialogueCount = sampleSentences.Count(s => 
            s.Text.StartsWith('"') || s.Text.StartsWith('"') || s.Text.StartsWith("'"));
        var dialogueRatio = sampleSentences.Count > 0 ? (double)dialogueCount / sampleSentences.Count : 0;
        fictionScore += dialogueRatio * 10; // High dialogue = fiction
        
        // 2. Speech tags
        var speechTagPattern = new System.Text.RegularExpressions.Regex(
            @"\b(said|replied|asked|answered|cried|shouted|whispered|muttered|exclaimed|remarked|observed)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var speechTagCount = speechTagPattern.Matches(sampleText).Count;
        fictionScore += Math.Min(5, speechTagCount / 3.0);
        
        // 3. Pronoun + action patterns (narrative prose)
        var narrativePattern = new System.Text.RegularExpressions.Regex(
            @"\b(he|she|they|I)\s+(walked|ran|looked|felt|thought|saw|heard|went|came|stood|sat)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var narrativeCount = narrativePattern.Matches(sampleText).Count;
        fictionScore += Math.Min(5, narrativeCount / 2.0);
        
        // 4. Chapter markers
        if (sampleLower.Contains("chapter")) fictionScore += 3;
        
        // 5. Character name patterns (Mr./Mrs./Miss + Name)
        var titlePattern = new System.Text.RegularExpressions.Regex(
            @"\b(Mr\.|Mrs\.|Miss|Dr\.|Captain|Inspector)\s+[A-Z][a-z]+",
            System.Text.RegularExpressions.RegexOptions.None);
        if (titlePattern.IsMatch(sampleText)) fictionScore += 2;
        
        // === TECHNICAL SIGNALS ===
        var technicalScore = 0.0;
        
        // 1. Code blocks present
        technicalScore += Math.Min(5, codeBlocks);
        
        // 2. Programming keywords
        var techKeywords = new[] { "function", "class", "method", "api", "http", "json", 
            "install", "configure", "docker", "kubernetes", "database", "server", "client" };
        var techKeywordCount = techKeywords.Count(kw => sampleLower.Contains(kw));
        technicalScore += techKeywordCount;
        
        // 3. Code-like patterns (camelCase, snake_case, brackets)
        var codePattern = new System.Text.RegularExpressions.Regex(
            @"\b[a-z]+[A-Z][a-z]+\b|[a-z]+_[a-z]+|\(\)|\[\]|\{\}|=>|->",
            System.Text.RegularExpressions.RegexOptions.None);
        var codePatternCount = codePattern.Matches(sampleText).Count;
        technicalScore += Math.Min(5, codePatternCount / 5.0);
        
        // 4. Section structure keywords
        if (sampleLower.Contains("introduction") || sampleLower.Contains("conclusion") ||
            sampleLower.Contains("overview") || sampleLower.Contains("requirements"))
            technicalScore += 2;
        
        // === DECISION ===
        // Need clear margin to classify
        if (fictionScore > technicalScore + 3) return ContentType.Narrative;
        if (technicalScore > fictionScore + 3) return ContentType.Expository;
        
        // If uncertain, use segment type distribution as tiebreaker
        if (dialogueRatio > 0.15) return ContentType.Narrative;
        if (codeBlocks > 2) return ContentType.Expository;
        
        return ContentType.Unknown;
    }

    /// <summary>
    /// Apply position-based weights (intro/conclusion boost for expository content)
    /// </summary>
    private static void ApplyPositionWeights(List<Segment> segments, ContentType contentType)
    {
        if (segments.Count == 0) return;
        
        var introThreshold = PositionWeights.GetIntroThreshold(contentType);
        var conclusionThreshold = PositionWeights.GetConclusionThreshold(contentType);
        
        for (int i = 0; i < segments.Count; i++)
        {
            var position = (double)i / segments.Count;
            var baseWeight = segments[i].PositionWeight;
            
            double positionMultiplier;
            if (position < introThreshold)
                positionMultiplier = PositionWeights.GetWeight(ChunkPosition.Introduction, contentType);
            else if (position >= conclusionThreshold)
                positionMultiplier = PositionWeights.GetWeight(ChunkPosition.Conclusion, contentType);
            else
                positionMultiplier = PositionWeights.GetWeight(ChunkPosition.Body, contentType);
            
            segments[i].PositionWeight = baseWeight * positionMultiplier;
        }
    }

    /// <summary>
    /// Quality-preserving pre-filtering before expensive embedding.
    /// 
    /// Strategy (designed to maintain quality while reducing embedding count):
    /// 1. ALWAYS keep all headings (they define structure, cheap to embed)
    /// 2. ALWAYS keep first 2 sentences of each section (topic sentences)
    /// 3. Stratified sampling: proportional representation from each section
    /// 4. Top-scored fill: remaining budget goes to highest-scored segments
    /// 
    /// This ensures:
    /// - Full document structure preserved (all headings)
    /// - Section coverage guaranteed (topic sentences + proportional sampling)
    /// - Important content captured (position/length/signal word scoring)
    /// </summary>
    private List<Segment> FastPreFilter(List<Segment> segments, int maxToKeep)
    {
        var result = new HashSet<Segment>();
        
        // 1. ALWAYS keep all headings (they're short, define structure)
        var headings = segments.Where(s => s.Type == SegmentType.Heading).ToList();
        foreach (var h in headings)
            result.Add(h);
        
        // 2. ALWAYS keep first 2 sentences of each section (topic sentences)
        var sections = segments
            .Where(s => s.Type == SegmentType.Sentence)
            .GroupBy(s => s.SectionTitle)
            .ToList();
        
        foreach (var section in sections)
        {
            foreach (var sentence in section.OrderBy(s => s.Index).Take(2))
                result.Add(sentence);
        }
        
        // 3. Stratified sampling: allocate remaining budget proportionally to sections
        var remainingBudget = maxToKeep - result.Count;
        if (remainingBudget > 0 && sections.Count > 0)
        {
            var perSection = Math.Max(2, remainingBudget / sections.Count);
            
            foreach (var section in sections)
            {
                // Score remaining segments in this section
                var candidates = section
                    .Where(s => !result.Contains(s))
                    .Select(s => new { Segment = s, Score = ComputePreFilterScore(s, segments.Count) })
                    .OrderByDescending(x => x.Score)
                    .Take(perSection)
                    .Select(x => x.Segment);
                
                foreach (var seg in candidates)
                    result.Add(seg);
            }
        }
        
        // 4. Top-scored fill: if still under budget, add highest-scored remaining
        if (result.Count < maxToKeep)
        {
            var remaining = segments
                .Where(s => !result.Contains(s))
                .Select(s => new { Segment = s, Score = ComputePreFilterScore(s, segments.Count) })
                .OrderByDescending(x => x.Score)
                .Take(maxToKeep - result.Count)
                .Select(x => x.Segment);
            
            foreach (var seg in remaining)
                result.Add(seg);
        }
        
        if (_verbose)
        {
            var typeCounts = result.GroupBy(s => s.Type).Select(g => $"{g.Key}:{g.Count()}");
            VerboseHelper.Log($"[dim]Pre-filter kept: {string.Join(", ", typeCounts)}[/]");
        }
        
        // Re-sort by original document order for coherent processing
        return result.OrderBy(s => s.Index).ToList();
    }
    
    /// <summary>
    /// Compute cheap pre-filter score for a segment.
    /// Higher score = more likely to be important.
    /// Tuned to avoid dropping semantically important content.
    /// </summary>
    private static double ComputePreFilterScore(Segment segment, int totalSegments)
    {
        double score = 0;
        
        // 1. Position weight (already computed: intro/conclusion boosted)
        score += segment.PositionWeight * 2.0;
        
        // 2. Text length (normalized, longer is better up to a point)
        // But don't penalize short impactful sentences too much
        var lengthScore = Math.Min(segment.Text.Length / 150.0, 2.0);
        score += lengthScore;
        
        // 3. Segment type priority (adjusted for quality)
        score += segment.Type switch
        {
            SegmentType.Heading => 3.0,    // Headings are summary-like (but already guaranteed)
            SegmentType.Quote => 2.0,      // Quotes are highlighted for importance
            SegmentType.Sentence => 1.5,   // Core content - don't underweight!
            SegmentType.ListItem => 1.2,   // Often key points
            SegmentType.CodeBlock => 0.8,  // Important for technical, but verbose
            _ => 0.5
        };
        
        // 4. First/last in section bonus (topic sentences, conclusions)
        if (segment.HeadingLevel > 0 && segment.HeadingLevel <= 2)
            score += 0.5;
        
        // 5. Contains key signal words (cheap string check)
        var lowerText = segment.Text.ToLowerInvariant();
        var signalWords = new[] { "important", "key", "summary", "conclusion", 
            "overview", "main", "primary", "essential", "core", "fundamental",
            "therefore", "thus", "consequently", "in summary", "to summarize" };
        
        if (signalWords.Any(w => lowerText.Contains(w)))
            score += 1.0;
        
        // 6. Negation/contrast signals (often contain important distinctions)
        var contrastWords = new[] { "however", "but", "although", "unlike", "whereas", "instead" };
        if (contrastWords.Any(w => lowerText.Contains(w)))
            score += 0.5;
        
        return score;
    }

    /// <summary>
    /// Recall-safe semantic pre-filtering using multi-anchor approach.
    /// 
    /// PROBLEM with single-centroid: It captures "dominant theme" and systematically
    /// down-ranks important-but-rare content (constraints, exceptions, conclusions,
    /// recommendations, numbers, section outliers, novel findings).
    /// 
    /// SOLUTION: Multi-anchor + guaranteed structural coverage
    /// 
    /// Pass 0: Build guaranteed coverage set G (can't be filtered out)
    ///   - First/last 2 sentences per section (topic sentences + conclusions)
    ///   - Sentences with numbers/dates/must/shall/should (constraints/facts)
    ///   - All headings (structure)
    /// 
    /// Pass 1: Cheap semantic sketch
    ///   - Sample ~60 segments stratified across sections
    ///   - Embed sample → cluster into k=5 topic anchors
    /// 
    /// Pass 2: BM25 candidate generation
    ///   - Build pseudo-query from sample TF-IDF terms + section titles
    ///   - BM25 retrieve candidates with per-section caps
    /// 
    /// Pass 3: Embed candidates ∪ G
    /// </summary>
    private async Task<(List<Segment> filtered, float[] centroid)> SemanticPreFilterAsync(
        List<Segment> segments, 
        int maxToKeep, 
        CancellationToken ct)
    {
        var sampleSize = _config.PreFilterSampleSize;
        
        // === Pass 0: Guaranteed structural coverage (can't be filtered out) ===
        var guaranteed = BuildGuaranteedCoverageSet(segments);
        if (_verbose)
            VerboseHelper.Log(_verbose, $"[dim]Guaranteed coverage: {guaranteed.Count} segments[/]");
        
        // === Pass 1: Embed stratified sample for topic anchors ===
        var sample = SelectStratifiedSample(segments, sampleSize);
        
        if (_verbose) 
            VerboseHelper.Log(_verbose, $"[dim]Pass 1: Embedding {sample.Count} sample segments for topic anchors...[/]");
        
        await GenerateEmbeddingsAsync(sample, ct);
        
        // Cluster sample into k topic anchors (simple k-means-ish)
        var embeddedSample = sample.Where(s => s.Embedding != null).ToList();
        var topicAnchors = ComputeTopicAnchors(embeddedSample, k: 5);
        
        // Also compute overall centroid for fallback
        var centroid = CalculateCentroid(embeddedSample);
        
        if (_verbose)
            VerboseHelper.Log(_verbose, $"[dim]Computed {topicAnchors.Count} topic anchors[/]");
        
        // === Pass 2: BM25 candidate generation with pseudo-query ===
        // Build pseudo-query from sample TF-IDF terms + section titles
        var pseudoQuery = BuildPseudoQuery(sample, segments);
        
        if (_verbose)
        {
            var queryPreview = pseudoQuery.Length > 80 ? pseudoQuery[..80] + "..." : pseudoQuery;
            // Escape markup characters
            queryPreview = queryPreview.Replace("[", "[[").Replace("]", "]]");
            VerboseHelper.Log(_verbose, $"[dim]Pseudo-query: {queryPreview}[/]");
        }
        
        // Score all segments by BM25 to pseudo-query
        var bm25Scorer = new SimpleBM25(segments.Select(s => s.Text));
        var bm25Scores = segments
            .Select((s, i) => new { Segment = s, Index = i, Score = bm25Scorer.Score(i, pseudoQuery) })
            .ToList();
        
        // Apply per-section caps to prevent one section from dominating
        var perSectionCap = Math.Max(10, maxToKeep / Math.Max(1, segments.Select(s => s.SectionTitle).Distinct().Count()));
        var bm25Candidates = bm25Scores
            .GroupBy(x => x.Segment.SectionTitle)
            .SelectMany(g => g.OrderByDescending(x => x.Score).Take(perSectionCap))
            .OrderByDescending(x => x.Score)
            .Take(maxToKeep * 2) // Over-retrieve for diversity
            .Select(x => x.Segment)
            .ToHashSet();
        
        // === Combine: guaranteed ∪ bm25 candidates ∪ sample ===
        var result = new HashSet<Segment>(guaranteed);
        foreach (var s in sample) result.Add(s);
        foreach (var s in bm25Candidates)
        {
            if (result.Count >= maxToKeep) break;
            result.Add(s);
        }
        
        // If still under budget, add by multi-anchor similarity
        if (result.Count < maxToKeep && topicAnchors.Count > 0)
        {
            var remaining = segments
                .Where(s => !result.Contains(s))
                .Select(s => new
                {
                    Segment = s,
                    // Max similarity to any topic anchor (covers minority topics)
                    Score = s.Embedding != null
                        ? topicAnchors.Max(anchor => CosineSimilarity(s.Embedding, anchor))
                        : EstimateSemanticSimilarity(s.Text, BuildVocabulary(sample.Select(x => x.Text)))
                })
                .OrderByDescending(x => x.Score)
                .Take(maxToKeep - result.Count)
                .Select(x => x.Segment);
            
            foreach (var s in remaining)
                result.Add(s);
        }
        
        // === Pass 3: Embed remaining candidates ===
        var toEmbed = result.Where(s => s.Embedding == null).ToList();
        if (toEmbed.Count > 0)
        {
            if (_verbose)
                VerboseHelper.Log(_verbose, $"[dim]Pass 2: Embedding {toEmbed.Count} additional candidates...[/]");
            await GenerateEmbeddingsAsync(toEmbed, ct);
        }
        
        if (_verbose)
        {
            var typeCounts = result.GroupBy(s => s.Type).Select(g => $"{g.Key}:{g.Count()}");
            VerboseHelper.Log($"[dim]Recall-safe pre-filter kept: {string.Join(", ", typeCounts)}[/]");
        }
        
        return (result.OrderBy(s => s.Index).ToList(), centroid ?? Array.Empty<float>());
    }
    
    /// <summary>
    /// Build guaranteed coverage set - segments that can NEVER be filtered out.
    /// 
    /// TUNED FOR RECALL WITHOUT BLOAT:
    /// - Per-section: first 1 sentence (topic) + last 1 sentence (conclusion)  
    /// - Global: real headings only (not every blank line)
    /// - Specials: constraints/warnings (tight pattern), top code blocks
    /// 
    /// Target: ~10-15% of segments, NOT 40%
    /// </summary>
    private HashSet<Segment> BuildGuaranteedCoverageSet(List<Segment> segments)
    {
        var guaranteed = new HashSet<Segment>();
        var totalSegments = segments.Count;
        
        // Cap guaranteed coverage at 15% of total segments
        var maxGuaranteed = Math.Max(50, totalSegments / 7);
        
        // 1. REAL headings only - filter out pseudo-headings from plain text
        // Real headings: short, likely contain "Chapter", numbered, etc.
        var headings = segments.Where(s => s.Type == SegmentType.Heading).ToList();
        var realHeadings = headings
            .Where(h => h.Text.Length < 100 && // Real headings are short
                        (h.Text.Contains("Chapter", StringComparison.OrdinalIgnoreCase) ||
                         h.Text.Contains("Section", StringComparison.OrdinalIgnoreCase) ||
                         h.Text.StartsWith("#") ||
                         System.Text.RegularExpressions.Regex.IsMatch(h.Text, @"^\d+[\.\)]\s") ||
                         h.HeadingLevel <= 3))
            .Take(50) // Cap at 50 headings max
            .ToList();
        
        // If we don't find many "real" headings, just take first 20 heading-type segments
        if (realHeadings.Count < 5 && headings.Count > 0)
            realHeadings = headings.Take(20).ToList();
            
        foreach (var h in realHeadings)
            guaranteed.Add(h);
        
        // 2. First and last sentence per DISTINCT section (not per paragraph)
        // Group by section title, but only if there are real distinct sections
        var sentenceSections = segments
            .Where(s => s.Type == SegmentType.Sentence)
            .GroupBy(s => s.SectionTitle)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .ToList();
        
        // If too many "sections" (probably bad parsing), just use position-based selection
        if (sentenceSections.Count > 50)
        {
            // Fall back to evenly-spaced sentences across the document
            var sentences = segments.Where(s => s.Type == SegmentType.Sentence).ToList();
            var step = Math.Max(1, sentences.Count / 30);
            for (int i = 0; i < sentences.Count && guaranteed.Count < maxGuaranteed; i += step)
            {
                guaranteed.Add(sentences[i]);
            }
        }
        else
        {
            foreach (var section in sentenceSections)
            {
                if (guaranteed.Count >= maxGuaranteed) break;
                
                var ordered = section.OrderBy(s => s.Index).ToList();
                if (ordered.Count > 0)
                {
                    guaranteed.Add(ordered[0]); // First (topic sentence)
                    if (ordered.Count > 1)
                        guaranteed.Add(ordered[^1]); // Last (conclusion)
                }
            }
        }
        
        // 3. Constraint/warning sentences - TIGHT pattern (not just any number)
        // Only match: percentages, dollar amounts, must/shall/should, warnings
        var constraintPattern = new System.Text.RegularExpressions.Regex(
            @"\b(\d+%|\$\d+|must\s+\w|shall\s+\w|should\s+\w|warning|caution|important:|note:|require[sd]?\s)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        foreach (var s in segments.Where(s => s.Type == SegmentType.Sentence))
        {
            if (constraintPattern.IsMatch(s.Text))
                guaranteed.Add(s);
        }
        
        // 4. Quotes (often highlighted for importance) - but cap at 5
        var quotes = segments.Where(s => s.Type == SegmentType.Quote).Take(5);
        foreach (var q in quotes)
            guaranteed.Add(q);
        
        // 5. TOP CODE BLOCKS - critical for technical docs!
        // Keep code blocks that contain key technical terms
        var codeKeywords = new[] { "dockerfile", "docker", "from ", "run ", "copy ", "cmd ", 
            "entrypoint", "compose", "services:", "dotnet", "npm", "apt-get", "pip" };
        
        var importantCodeBlocks = segments
            .Where(s => s.Type == SegmentType.CodeBlock)
            .Where(s => codeKeywords.Any(kw => s.Text.Contains(kw, StringComparison.OrdinalIgnoreCase)))
            .Take(10); // Cap at 10 code blocks
        
        foreach (var cb in importantCodeBlocks)
            guaranteed.Add(cb);
        
        // Also keep first code block per section (often the key example)
        var codeSections = segments
            .Where(s => s.Type == SegmentType.CodeBlock)
            .GroupBy(s => s.SectionTitle)
            .Where(g => !string.IsNullOrEmpty(g.Key));
        
        foreach (var section in codeSections)
        {
            var first = section.OrderBy(s => s.Index).FirstOrDefault();
            if (first != null)
                guaranteed.Add(first);
        }
        
        return guaranteed;
    }
    
    /// <summary>
    /// Compute k topic anchors by clustering sample embeddings.
    /// Uses simple greedy farthest-point sampling (fast, deterministic).
    /// </summary>
    private static List<float[]> ComputeTopicAnchors(List<Segment> embeddedSample, int k)
    {
        if (embeddedSample.Count == 0) return new List<float[]>();
        if (embeddedSample.Count <= k)
            return embeddedSample.Select(s => s.Embedding!).ToList();
        
        var anchors = new List<float[]>();
        var used = new HashSet<int>();
        
        // Start with first segment
        anchors.Add(embeddedSample[0].Embedding!);
        used.Add(0);
        
        // Greedily add farthest points
        while (anchors.Count < k)
        {
            var bestIdx = -1;
            var bestMinDist = -1.0;
            
            for (int i = 0; i < embeddedSample.Count; i++)
            {
                if (used.Contains(i)) continue;
                
                // Distance to nearest anchor
                var minDist = anchors.Min(a => 1.0 - CosineSimilarity(embeddedSample[i].Embedding!, a));
                
                if (minDist > bestMinDist)
                {
                    bestMinDist = minDist;
                    bestIdx = i;
                }
            }
            
            if (bestIdx < 0) break;
            
            anchors.Add(embeddedSample[bestIdx].Embedding!);
            used.Add(bestIdx);
        }
        
        return anchors;
    }
    
    /// <summary>
    /// Build pseudo-query for BM25 from sample TF-IDF terms + section titles.
    /// This is a principled query, not "similarity to a blob".
    /// </summary>
    private static string BuildPseudoQuery(List<Segment> sample, List<Segment> allSegments)
    {
        // Get section titles as high-weight terms
        var sectionTitles = allSegments
            .Select(s => s.SectionTitle)
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct()
            .ToList();
        
        // Build TF-IDF-like term weights from sample
        var termFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var docFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with",
            "is", "are", "was", "were", "be", "been", "being", "have", "has", "had",
            "this", "that", "these", "those", "it", "its", "as", "by", "from", "can", "will",
            "would", "could", "should", "may", "might", "must", "do", "does", "did"
        };
        
        foreach (var segment in sample)
        {
            var seenInDoc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var words = segment.Text.ToLowerInvariant()
                .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']' },
                       StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var word in words)
            {
                if (word.Length <= 2 || stopWords.Contains(word)) continue;
                
                termFreq.TryGetValue(word, out var tf);
                termFreq[word] = tf + 1;
                
                if (!seenInDoc.Contains(word))
                {
                    docFreq.TryGetValue(word, out var df);
                    docFreq[word] = df + 1;
                    seenInDoc.Add(word);
                }
            }
        }
        
        // TF-IDF score: terms that appear often but not in every doc
        var tfidf = termFreq
            .Where(kv => docFreq.TryGetValue(kv.Key, out var df) && df < sample.Count * 0.8)
            .Select(kv => new
            {
                Term = kv.Key,
                Score = kv.Value * Math.Log((double)sample.Count / (docFreq[kv.Key] + 1))
            })
            .OrderByDescending(x => x.Score)
            .Take(30)
            .Select(x => x.Term)
            .ToList();
        
        // Combine: section titles (boosted) + top TF-IDF terms
        var queryTerms = sectionTitles
            .SelectMany(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Concat(tfidf);
        
        return string.Join(" ", queryTerms);
    }
    
    /// <summary>
    /// Select stratified sample ensuring coverage across all sections
    /// </summary>
    private static List<Segment> SelectStratifiedSample(List<Segment> segments, int sampleSize)
    {
        var result = new List<Segment>();
        
        // Always include all headings (they're summary-like and short)
        result.AddRange(segments.Where(s => s.Type == SegmentType.Heading));
        
        // Group by section
        var sections = segments
            .Where(s => s.Type != SegmentType.Heading)
            .GroupBy(s => s.SectionTitle)
            .ToList();
        
        if (sections.Count == 0)
            return result.Take(sampleSize).ToList();
        
        // Allocate remaining budget proportionally
        var remaining = sampleSize - result.Count;
        var perSection = Math.Max(2, remaining / Math.Max(1, sections.Count));
        
        foreach (var section in sections)
        {
            // Take first, middle, and last segments from each section
            var sectionList = section.OrderBy(s => s.Index).ToList();
            
            if (sectionList.Count <= perSection)
            {
                result.AddRange(sectionList);
            }
            else
            {
                // First (topic sentence)
                result.Add(sectionList[0]);
                
                // Last (conclusion)
                result.Add(sectionList[^1]);
                
                // Evenly spaced from middle
                var step = sectionList.Count / (perSection - 2 + 1);
                for (int i = 1; i < perSection - 1 && i * step < sectionList.Count - 1; i++)
                {
                    result.Add(sectionList[i * step]);
                }
            }
        }
        
        return result.Distinct().Take(sampleSize).ToList();
    }
    
    /// <summary>
    /// Build vocabulary (word frequencies) from text collection
    /// </summary>
    private static Dictionary<string, int> BuildVocabulary(IEnumerable<string> texts)
    {
        var vocab = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with",
            "is", "are", "was", "were", "be", "been", "being", "have", "has", "had",
            "this", "that", "these", "those", "it", "its", "as", "by", "from"
        };
        
        foreach (var text in texts)
        {
            var words = text.ToLowerInvariant()
                .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':', '"', '\'', '(', ')' }, 
                       StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var word in words)
            {
                if (word.Length > 2 && !stopWords.Contains(word))
                {
                    vocab.TryGetValue(word, out var count);
                    vocab[word] = count + 1;
                }
            }
        }
        
        return vocab;
    }
    
    /// <summary>
    /// Estimate semantic similarity using vocabulary overlap (cheap proxy for embedding similarity)
    /// </summary>
    private static double EstimateSemanticSimilarity(string text, Dictionary<string, int> coreVocabulary)
    {
        if (coreVocabulary.Count == 0) return 0.5;
        
        var words = text.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':', '"', '\'', '(', ')' }, 
                   StringSplitOptions.RemoveEmptyEntries);
        
        double score = 0;
        int matches = 0;
        
        foreach (var word in words)
        {
            if (coreVocabulary.TryGetValue(word, out var freq))
            {
                // Weight by frequency in core vocabulary
                score += Math.Log(1 + freq);
                matches++;
            }
        }
        
        // Normalize by text length
        var normalized = words.Length > 0 ? score / Math.Sqrt(words.Length) : 0;
        
        // Scale to 0-1 range (empirically tuned)
        return Math.Min(1.0, normalized / 3.0);
    }
    
    /// <summary>
    /// Cosine similarity between two vectors
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

    /// <summary>
    /// Generate embeddings for all segments using sentence-transformer model
    /// </summary>
    private async Task GenerateEmbeddingsAsync(List<Segment> segments, CancellationToken ct)
    {
        // Use non-interactive mode (no Spectre progress bar in library mode)
        await GenerateEmbeddingsWithoutProgressAsync(segments, ct);
    }
    
    /// <summary>
    /// Process batches without Spectre progress display (for batch mode)
    /// </summary>
    private async Task<float[]?> ProcessBatchesWithoutProgressAsync(
        List<Segment> allSegments,
        int numBatches,
        int batchSize,
        int keepPerBatch,
        ContentType contentType,
        List<Segment> batchWinners,
        CancellationToken ct)
    {
        float[]? globalCentroid = null;
        
        for (int batch = 0; batch < numBatches; batch++)
        {
            ct.ThrowIfCancellationRequested();
            
            var batchStart = batch * batchSize;
            var batchSegments = allSegments.Skip(batchStart).Take(batchSize).ToList();
            
            // Generate embeddings without progress display
            await GenerateEmbeddingsWithoutProgressAsync(batchSegments, ct);
            
            // Calculate batch centroid
            var batchCentroid = CalculateCentroid(batchSegments);
            
            // Accumulate for global centroid
            if (globalCentroid == null)
                globalCentroid = batchCentroid.ToArray();
            else
            {
                for (int i = 0; i < globalCentroid.Length && i < batchCentroid.Length; i++)
                    globalCentroid[i] = (globalCentroid[i] * batch + batchCentroid[i]) / (batch + 1);
            }
            
            // Score batch segments using MMR
            ComputeSalienceScores(batchSegments, batchCentroid, contentType);
            
            // Keep top-K from this batch
            var batchTop = batchSegments
                .OrderByDescending(s => s.SalienceScore)
                .Take(keepPerBatch)
                .ToList();
            
            batchWinners.AddRange(batchTop);
            
            // Clear embeddings from non-winners to free memory
            foreach (var segment in batchSegments.Where(s => !batchTop.Contains(s)))
            {
                segment.Embedding = null;
            }
        }
        
        return globalCentroid;
    }
    
    /// <summary>
    /// Generate embeddings without Spectre progress display (for batch mode)
    /// </summary>
    private async Task GenerateEmbeddingsWithoutProgressAsync(List<Segment> segments, CancellationToken ct)
    {
        await _embeddingService.InitializeAsync(ct);
        
        const int batchSize = 64;
        var total = segments.Count;
        
        for (int i = 0; i < total; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            
            var batch = segments.Skip(i).Take(batchSize).ToList();
            var texts = batch.Select(s => s.Text).ToList();
            
            var embeddings = await _embeddingService.EmbedBatchAsync(texts, ct);
            
            for (int j = 0; j < batch.Count; j++)
            {
                batch[j].Embedding = embeddings[j];
            }
        }
    }

    /// <summary>
    /// Calculate document centroid (L2-normalized average of all embeddings)
    /// </summary>
    private static float[] CalculateCentroid(List<Segment> segments)
    {
        var withEmbeddings = segments.Where(s => s.Embedding != null).ToList();
        if (withEmbeddings.Count == 0)
            return Array.Empty<float>();
        
        var dim = withEmbeddings[0].Embedding!.Length;
        var centroid = new float[dim];
        
        foreach (var segment in withEmbeddings)
        {
            for (int i = 0; i < dim; i++)
                centroid[i] += segment.Embedding![i];
        }
        
        // Average
        for (int i = 0; i < dim; i++)
            centroid[i] /= withEmbeddings.Count;
        
        // L2 normalize
        var norm = MathF.Sqrt(centroid.Sum(x => x * x));
        if (norm > 0)
        {
            for (int i = 0; i < dim; i++)
                centroid[i] /= norm;
        }
        
        return centroid;
    }

    /// <summary>
    /// Compute salience scores using Maximal Marginal Relevance (MMR).
    /// 
    /// Salience = lambda * sim(segment, centroid) * position_weight * content_weight
    ///          - (1 - lambda) * max_sim(segment, higher_ranked_segments)
    /// 
    /// This balances:
    /// - Relevance: how representative is this segment of the document?
    /// - Diversity: does this add new information vs already-selected segments?
    /// - Position: is this in intro/conclusion (more important for expository)?
    /// - Content-type: de-weight dialogue for fiction, upweight narrative/action
    /// </summary>
    private void ComputeSalienceScores(List<Segment> segments, float[] centroid, ContentType contentType = ContentType.Unknown)
    {
        if (centroid.Length == 0) return;
        
        var candidates = new HashSet<Segment>(segments.Where(s => s.Embedding != null));
        var ranked = new List<Segment>();
        
        // Pre-compute centroid similarities with content-type adjustments
        foreach (var segment in candidates)
        {
            var baseSim = CosineSimilarity(segment.Embedding!, centroid);
            var contentWeight = ComputeContentTypeWeight(segment, contentType);
            segment.SalienceScore = baseSim * segment.PositionWeight * contentWeight;
        }
        
        // Greedy MMR selection to compute final salience ranks
        while (candidates.Count > 0)
        {
            Segment? best = null;
            double bestScore = double.MinValue;
            
            foreach (var candidate in candidates)
            {
                var relevance = candidate.SalienceScore;
                
                // Diversity penalty: similarity to already-ranked segments
                double maxSimToRanked = 0;
                foreach (var rankedSeg in ranked)
                {
                    var sim = CosineSimilarity(candidate.Embedding!, rankedSeg.Embedding!);
                    maxSimToRanked = Math.Max(maxSimToRanked, sim);
                }
                
                // MMR score
                var mmrScore = _config.MmrLambda * relevance - (1 - _config.MmrLambda) * maxSimToRanked;
                
                if (mmrScore > bestScore)
                {
                    bestScore = mmrScore;
                    best = candidate;
                }
            }
            
            if (best != null)
            {
                // Final salience score incorporates MMR ranking
                // Higher rank = higher salience (normalized to 0-1)
                best.SalienceScore = 1.0 - ((double)ranked.Count / segments.Count);
                ranked.Add(best);
                candidates.Remove(best);
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    /// Calculate target number of segments based on config
    /// </summary>
    private int CalculateTargetCount(int totalSegments)
    {
        var target = (int)(totalSegments * _config.ExtractionRatio);
        return Math.Clamp(target, _config.MinSegments, _config.MaxSegments);
    }

    /// <summary>
    /// Compute content-type specific weight for a segment.
    /// 
    /// Applies multiple scoring factors:
    /// 1. LENGTH QUALITY: Substantive content (80+ chars) scores higher than short headings
    /// 2. CONTENT-TYPE: Fiction vs technical content adjustments
    /// 3. SECTION IMPORTANCE: Abstract, intro, conclusion get boosted
    /// 4. HEADING AWARENESS: Headings still get a boost, but proportional to length
    /// 
    /// For NARRATIVE (fiction):
    /// - De-weight short dialogue ("Yes, indeed", "No, no")
    /// - Upweight narrative description (action, scene-setting)
    /// - Upweight character introductions (first mentions of names)
    /// - Upweight plot-relevant sentences (contains action verbs)
    /// 
    /// For EXPOSITORY (technical/academic):
    /// - De-weight code blocks (appendix material)
    /// - De-weight reference lists
    /// - Upweight abstract, introduction, conclusion sections
    /// </summary>
    private double ComputeContentTypeWeight(Segment segment, ContentType contentType)
    {
        // === LENGTH-BASED QUALITY SCORING ===
        // Short segments (headings, one-liners) are penalized; substantive content is rewarded
        var lengthQuality = ComputeLengthQuality(segment.Text.Length);
        
        // === EXPOSITORY/TECHNICAL ADJUSTMENTS ===
        if (contentType == ContentType.Expository || contentType == ContentType.Unknown)
        {
            var weight = lengthQuality; // Start with length quality as base
            var text = segment.Text;
            var heading = segment.HeadingPath.ToLowerInvariant();
            var sectionTitle = segment.SectionTitle.ToLowerInvariant();
            
            // 1. HEAVILY DE-WEIGHT CODE BLOCKS (usually appendix/implementation details)
            if (segment.Type == SegmentType.CodeBlock)
            {
                weight *= 0.2; // Code is rarely summary-worthy
            }
            
            // 2. DE-WEIGHT REFERENCE/BIBLIOGRAPHY SECTIONS
            if (heading.Contains("reference") || heading.Contains("bibliograph") || 
                sectionTitle.Contains("reference") || sectionTitle.Contains("bibliograph"))
            {
                weight *= 0.1; // References are boilerplate
            }
            
            // Also de-weight list items that look like citations [1] Author, "Title"...
            if (segment.Type == SegmentType.ListItem)
            {
                var looksLikeCitation = System.Text.RegularExpressions.Regex.IsMatch(
                    text, @"^\[\d+\]|^\d+\.\s+[A-Z]\.\s*[A-Z]|Proceedings|Conference|Journal|IEEE|ACM",
                    System.Text.RegularExpressions.RegexOptions.None);
                if (looksLikeCitation)
                    weight *= 0.15;
            }
            
            // 3. DE-WEIGHT APPENDIX/VITA/ACKNOWLEDGEMENTS
            if (heading.Contains("appendix") || heading.Contains("vita") || 
                heading.Contains("acknowledgement") || heading.Contains("acknowledgment") ||
                sectionTitle.Contains("appendix") || sectionTitle.Contains("vita"))
            {
                weight *= 0.2;
            }
            
            // 4. UPWEIGHT KEY ACADEMIC SECTIONS
            // Abstract - highest priority
            if (heading.Contains("abstract") || sectionTitle.Contains("abstract"))
            {
                weight *= 2.5;
            }
            // Introduction/Conclusion - high priority
            else if (heading.Contains("introduction") || heading.Contains("conclusion") ||
                     sectionTitle.Contains("introduction") || sectionTitle.Contains("conclusion"))
            {
                weight *= 1.8;
            }
            // Summary/Overview - high priority
            else if (heading.Contains("summary") || heading.Contains("overview") ||
                     sectionTitle.Contains("summary") || sectionTitle.Contains("overview"))
            {
                weight *= 1.6;
            }
            // Results/Discussion - moderate priority
            else if (heading.Contains("result") || heading.Contains("discussion") ||
                     sectionTitle.Contains("result") || sectionTitle.Contains("discussion"))
            {
                weight *= 1.3;
            }
            
            // 5. HEADINGS: Moderate boost, configurable
            // The boost is reduced from 4.5x to ~1.8x for document title
            // This ensures substantive sentences can outrank short headings
            if (segment.Type == SegmentType.Heading && weight > 0.3)
            {
                weight *= _config.HeadingBoost; // Default 1.15 (was 1.5)
                
                // Document title still gets extra boost but much less than before
                if (segment.HeadingLevel == 1 && segment.Index <= 5)
                {
                    weight *= _config.DocumentTitleBoost; // Default 1.8 (was 3.0)
                }
            }
            
            return weight;
        }
        
        // === NARRATIVE (fiction) uses existing logic below ===
        if (contentType != ContentType.Narrative)
            return lengthQuality; // Return length quality for unknown content types
        
        // === FICTION-SPECIFIC ADJUSTMENTS ===
        var narrativeText = segment.Text;
        var narrativeWeight = lengthQuality; // Start with length quality
        
        // 1. De-weight SHORT DIALOGUE (conversational filler)
        // Dialogue starts with quote, is short, ends with speech tag
        var isDialogue = narrativeText.StartsWith('"') || narrativeText.StartsWith('"') || narrativeText.StartsWith("'");
        var isShort = narrativeText.Length < 60;
        var hasSimpleSpeechTag = System.Text.RegularExpressions.Regex.IsMatch(
            narrativeText, @"\b(said|asked|replied|answered|cried|shouted|whispered|muttered)\b", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        if (isDialogue && isShort)
        {
            // Very short dialogue ("Yes", "No, indeed") - heavily de-weight
            if (narrativeText.Length < 30)
                narrativeWeight *= 0.2;
            else
                narrativeWeight *= 0.5;
        }
        else if (isDialogue && hasSimpleSpeechTag && narrativeText.Length < 100)
        {
            // Short dialogue with speech tag - moderate de-weight
            narrativeWeight *= 0.6;
        }
        
        // 2. Upweight NARRATIVE DESCRIPTION (non-dialogue)
        if (!isDialogue)
        {
            // Narrative prose - slight upweight
            narrativeWeight *= 1.2;
            
            // Action verbs indicate plot-relevant content
            var hasActionVerbs = System.Text.RegularExpressions.Regex.IsMatch(
                narrativeText, @"\b(walked|ran|entered|left|opened|closed|found|discovered|saw|heard|felt|took|gave|came|went|arrived|departed|killed|died|attacked|escaped|hid|searched|followed)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            if (hasActionVerbs)
                narrativeWeight *= 1.3;
            
            // Scene-setting (place/time indicators)
            var hasSceneSetting = System.Text.RegularExpressions.Regex.IsMatch(
                narrativeText, @"\b(morning|evening|night|day|room|house|street|door|window|chapter|later|earlier|meanwhile|suddenly|finally)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            if (hasSceneSetting)
                narrativeWeight *= 1.2;
        }
        
        // 3. Upweight CHARACTER INTRODUCTIONS
        // Look for "Mr./Mrs./Miss/Dr. + Name" or "Name, a/the [role]"
        var hasCharacterIntro = System.Text.RegularExpressions.Regex.IsMatch(
            narrativeText, @"\b(Mr\.|Mrs\.|Miss|Dr\.|Captain|Inspector|Professor)\s+[A-Z][a-z]+\b",
            System.Text.RegularExpressions.RegexOptions.None);
        
        if (hasCharacterIntro)
            narrativeWeight *= 1.4;
        
        // 4. Upweight LONGER SUBSTANTIVE CONTENT
        // Longer sentences in fiction often contain more plot/description
        if (narrativeText.Length > 150 && !isDialogue)
            narrativeWeight *= 1.2;
        
        return narrativeWeight;
    }

    public void Dispose()
    {
        _embeddingService.Dispose();
    }
    
    /// <summary>
    /// Compute length-based quality score for a segment.
    /// Short segments (below IdealMinLength) are penalized proportionally.
    /// Segments at or above IdealMinLength receive full quality score (1.0).
    /// Very long segments get a small boost (up to 1.1x).
    /// This ensures substantive sentences rank higher than short headings
    /// while still allowing document titles to appear in results.
    /// </summary>
    private double ComputeLengthQuality(int length)
    {
        var minLen = _config.IdealMinLength;      // Default 80
        var maxLen = _config.IdealMaxLength;      // Default 500
        var minScore = _config.MinLengthQualityScore; // Default 0.3
        
        if (length >= minLen)
        {
            // Full quality for ideal length, small boost for longer content
            if (length >= maxLen)
                return 1.1; // Cap at 1.1x for very long content
                
            // Linear interpolation from 1.0 to 1.1 as length increases
            var ratio = (double)(length - minLen) / (maxLen - minLen);
            return 1.0 + (ratio * 0.1);
        }
        
        // Below ideal: linear scale from minScore to 1.0
        // e.g., a 40-char heading gets ~0.65 if minLen=80, minScore=0.3
        var quality = minScore + ((double)length / minLen) * (1.0 - minScore);
        return Math.Clamp(quality, minScore, 1.0);
    }
    
    /// <summary>
    /// Lightweight BM25 implementation for pre-filtering.
    /// Only used internally - not the full BM25Scorer from the Services namespace.
    /// </summary>
    private class SimpleBM25
    {
        private readonly List<Dictionary<string, int>> _docTermFreqs;
        private readonly Dictionary<string, int> _docFreqs;
        private readonly List<int> _docLengths;
        private readonly double _avgDocLength;
        private readonly int _corpusSize;
        private const double K1 = 1.5;
        private const double B = 0.75;
        
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with",
            "is", "are", "was", "were", "be", "been", "being", "have", "has", "had",
            "this", "that", "these", "those", "it", "its", "as", "by", "from"
        };
        
        public SimpleBM25(IEnumerable<string> documents)
        {
            _docTermFreqs = new List<Dictionary<string, int>>();
            _docFreqs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _docLengths = new List<int>();
            
            foreach (var doc in documents)
            {
                var terms = Tokenize(doc);
                var termFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                
                foreach (var term in terms)
                {
                    termFreq.TryGetValue(term, out var count);
                    termFreq[term] = count + 1;
                    
                    if (!seen.Contains(term))
                    {
                        _docFreqs.TryGetValue(term, out var df);
                        _docFreqs[term] = df + 1;
                        seen.Add(term);
                    }
                }
                
                _docTermFreqs.Add(termFreq);
                _docLengths.Add(terms.Count);
            }
            
            _corpusSize = _docTermFreqs.Count;
            _avgDocLength = _docLengths.Count > 0 ? _docLengths.Average() : 1;
        }
        
        public double Score(int docIndex, string query)
        {
            if (docIndex < 0 || docIndex >= _docTermFreqs.Count) return 0;
            
            var queryTerms = Tokenize(query);
            var docTermFreq = _docTermFreqs[docIndex];
            var docLength = _docLengths[docIndex];
            
            double score = 0;
            foreach (var term in queryTerms.Distinct())
            {
                if (!docTermFreq.TryGetValue(term, out var tf)) continue;
                if (!_docFreqs.TryGetValue(term, out var df)) continue;
                
                // IDF with smoothing
                var idf = Math.Log((_corpusSize - df + 0.5) / (df + 0.5) + 1);
                
                // BM25 TF component
                var tfNorm = (tf * (K1 + 1)) / (tf + K1 * (1 - B + B * docLength / _avgDocLength));
                
                score += idf * tfNorm;
            }
            
            return score;
        }
        
        private static List<string> Tokenize(string text)
        {
            return text.ToLowerInvariant()
                .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']', '{', '}' },
                       StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 2 && !StopWords.Contains(t))
                .ToList();
        }
    }
}
