using System.Security.Cryptography;
using System.Text;

namespace Mostlylucid.DocSummarizer.Models;

/// <summary>
/// A document segment: the atomic unit for extraction and retrieval.
/// Can be a sentence, list item, table row, code block, or other structural element.
/// 
/// This is the core data structure for the BERT→RAG pipeline:
/// Document → Segment → Embed + Score → Index → Retrieve → Synthesize
/// </summary>
public class Segment
{
    /// <summary>
    /// Unique identifier: doc_type_index (e.g., "mydoc_s_42" for sentence 42)
    /// </summary>
    public string Id { get; init; }
    
    /// <summary>
    /// The actual text content
    /// </summary>
    public string Text { get; set; }
    
    /// <summary>
    /// Type of segment for rendering and scoring
    /// </summary>
    public SegmentType Type { get; init; }
    
    /// <summary>
    /// 0-based index within document (preserves order)
    /// </summary>
    public int Index { get; init; }
    
    /// <summary>
    /// Character offset where this segment starts in the original document
    /// </summary>
    public int StartChar { get; init; }
    
    /// <summary>
    /// Character offset where this segment ends in the original document
    /// </summary>
    public int EndChar { get; init; }
    
    /// <summary>
    /// Section heading this segment belongs to (for context)
    /// </summary>
    public string SectionTitle { get; init; } = "";
    
    /// <summary>
    /// Full heading path (e.g., "Chapter 1 > Introduction > Overview")
    /// </summary>
    public string HeadingPath { get; init; } = "";
    
    /// <summary>
    /// Heading level (1-6) of the containing section
    /// </summary>
    public int HeadingLevel { get; init; }
    
    /// <summary>
    /// Page number if from PDF (null for markdown/text)
    /// </summary>
    public int? PageNumber { get; init; }
    
    /// <summary>
    /// Line number in source file (for text/markdown)
    /// </summary>
    public int? LineNumber { get; init; }
    
    /// <summary>
    /// Index of the source chunk (for pipelined processing)
    /// </summary>
    public int ChunkIndex { get; set; }
    
    // === Computed during extraction ===
    
    /// <summary>
    /// Embedding vector from sentence-transformer model (e.g., all-MiniLM-L6-v2)
    /// </summary>
    public float[]? Embedding { get; set; }
    
    /// <summary>
    /// Salience score from extraction (0-1): how important is this segment?
    /// Computed from: centroid similarity, position weight, MMR diversity
    /// </summary>
    public double SalienceScore { get; set; }
    
    /// <summary>
    /// Position weight (intro/body/conclusion) based on document type
    /// </summary>
    public double PositionWeight { get; set; } = 1.0;
    
    /// <summary>
    /// Stable hash of text content for citation stability across re-indexing
    /// </summary>
    public string ContentHash { get; init; }
    
    // === For retrieval ===
    
    /// <summary>
    /// Query similarity score (set during retrieval)
    /// </summary>
    public double QuerySimilarity { get; set; }
    
    /// <summary>
    /// Combined retrieval score: alpha * QuerySimilarity + (1-alpha) * SalienceScore
    /// </summary>
    public double RetrievalScore { get; set; }
    
    /// <summary>
    /// Citation reference for this segment (e.g., [s42])
    /// </summary>
    public string Citation => $"[{TypePrefix}{Index + 1}]";
    
    /// <summary>
    /// Short type prefix for citations
    /// </summary>
    private string TypePrefix => Type switch
    {
        SegmentType.Sentence => "s",
        SegmentType.ListItem => "li",
        SegmentType.TableRow => "tr",
        SegmentType.CodeBlock => "code",
        SegmentType.Quote => "q",
        SegmentType.Heading => "h",
        _ => "x"
    };

    public Segment(string docId, string text, SegmentType type, int index, int startChar, int endChar)
        : this(docId, text, type, index, startChar, endChar, null)
    {
    }
    
    /// <summary>
    /// Constructor with optional content hash override (used when restoring from database)
    /// </summary>
    public Segment(string docId, string text, SegmentType type, int index, int startChar, int endChar, string? contentHashOverride)
    {
        Text = text;
        Type = type;
        Index = index;
        StartChar = startChar;
        EndChar = endChar;
        ContentHash = !string.IsNullOrEmpty(contentHashOverride) ? contentHashOverride : ComputeHash(text);
        Id = $"{SanitizeDocId(docId)}_{TypePrefix}_{index}";
    }
    
    /// <summary>
    /// Compute stable SHA256 hash of content (first 16 chars)
    /// </summary>
    private static string ComputeHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
    
    private static string SanitizeDocId(string docId)
    {
        // Keep only alphanumeric and underscores
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
    /// Create a rich citation with context for UI expansion
    /// </summary>
    public CitationInfo ToCitation() => new(
        Citation,
        Text,
        SectionTitle,
        HeadingPath,
        PageNumber,
        LineNumber,
        StartChar,
        EndChar,
        ContentHash,
        SalienceScore
    );
}

/// <summary>
/// Type of document segment
/// </summary>
public enum SegmentType
{
    /// <summary>Regular sentence from paragraph</summary>
    Sentence,
    
    /// <summary>List item (bullet or numbered)</summary>
    ListItem,
    
    /// <summary>Table row</summary>
    TableRow,
    
    /// <summary>Code block or inline code</summary>
    CodeBlock,
    
    /// <summary>Block quote</summary>
    Quote,
    
    /// <summary>Section heading</summary>
    Heading,
    
    /// <summary>Figure/image caption</summary>
    Caption
}

/// <summary>
/// Rich citation information for UI and traceability
/// </summary>
public record CitationInfo(
    string Reference,      // [s42]
    string Text,           // The actual text
    string SectionTitle,   // "Introduction"
    string HeadingPath,    // "Chapter 1 > Introduction"
    int? PageNumber,       // 5 (if PDF)
    int? LineNumber,       // 142 (if text)
    int StartChar,         // 5432
    int EndChar,           // 5567
    string ContentHash,    // "a1b2c3d4..." (stable across re-index)
    double SalienceScore   // 0.85
);

/// <summary>
/// Result of the extraction phase: segments with embeddings and salience scores
/// </summary>
public class ExtractionResult
{
    /// <summary>
    /// All segments from the document, with embeddings and salience scores
    /// </summary>
    public List<Segment> AllSegments { get; init; } = new();
    
    /// <summary>
    /// Top segments by salience (the "fallback bucket" for global coverage)
    /// </summary>
    public List<Segment> TopBySalience { get; init; } = new();
    
    /// <summary>
    /// Document centroid embedding (average of all segment embeddings)
    /// </summary>
    public float[]? Centroid { get; init; }
    
    /// <summary>
    /// Detected content type (fiction, technical, etc.)
    /// </summary>
    public ContentType ContentType { get; init; }
    
    /// <summary>
    /// Time taken for extraction
    /// </summary>
    public TimeSpan ExtractionTime { get; init; }
    
    /// <summary>
    /// Segment type breakdown
    /// </summary>
    public Dictionary<SegmentType, int> SegmentCounts => AllSegments
        .GroupBy(s => s.Type)
        .ToDictionary(g => g.Key, g => g.Count());
    
    // Lazy-initialized lookup dictionaries for O(1) segment access
    private Dictionary<string, Segment>? _segmentLookup;
    private Dictionary<int, Segment>? _segmentByIndex;
    
    /// <summary>
    /// Fast lookup of segments by ID. Useful for resolving citations like [s42].
    /// </summary>
    public Dictionary<string, Segment> SegmentsById => 
        _segmentLookup ??= AllSegments.ToDictionary(s => s.Id);
    
    /// <summary>
    /// Fast lookup of segments by their 0-based index in the document.
    /// </summary>
    public Dictionary<int, Segment> SegmentsByIndex =>
        _segmentByIndex ??= AllSegments.ToDictionary(s => s.Index);
    
    /// <summary>
    /// Get a segment by its ID. Returns null if not found.
    /// </summary>
    /// <param name="segmentId">The segment ID (e.g., "s42")</param>
    /// <returns>The segment, or null if not found</returns>
    public Segment? GetSegment(string segmentId) =>
        SegmentsById.TryGetValue(segmentId, out var segment) ? segment : null;
    
    /// <summary>
    /// Get a segment by its 0-based index in the document.
    /// </summary>
    /// <param name="index">The segment index (0, 1, 2, ...)</param>
    /// <returns>The segment, or null if not found</returns>
    public Segment? GetSegmentByIndex(int index) =>
        SegmentsByIndex.TryGetValue(index, out var segment) ? segment : null;
    
    /// <summary>
    /// Find the segment containing a specific character position in the original document.
    /// Useful for highlighting or linking back to source text.
    /// </summary>
    /// <param name="charPosition">Character offset in the original document</param>
    /// <returns>The segment containing that position, or null if not found</returns>
    public Segment? GetSegmentAtPosition(int charPosition) =>
        AllSegments.FirstOrDefault(s => charPosition >= s.StartChar && charPosition < s.EndChar);
    
    /// <summary>
    /// Find all segments on a specific page (for PDF documents).
    /// </summary>
    /// <param name="pageNumber">The 1-based page number</param>
    /// <returns>All segments on that page, ordered by position</returns>
    public IEnumerable<Segment> GetSegmentsOnPage(int pageNumber) =>
        AllSegments.Where(s => s.PageNumber == pageNumber).OrderBy(s => s.Index);
    
    /// <summary>
    /// Find all segments within a character range in the original document.
    /// </summary>
    /// <param name="startChar">Start of range (inclusive)</param>
    /// <param name="endChar">End of range (exclusive)</param>
    /// <returns>Segments that overlap with the range</returns>
    public IEnumerable<Segment> GetSegmentsInRange(int startChar, int endChar) =>
        AllSegments.Where(s => s.StartChar < endChar && s.EndChar > startChar).OrderBy(s => s.StartChar);
    
    /// <summary>
    /// Try to get a segment by its ID.
    /// </summary>
    /// <param name="segmentId">The segment ID</param>
    /// <param name="segment">The segment if found</param>
    /// <returns>True if found, false otherwise</returns>
    public bool TryGetSegment(string segmentId, out Segment? segment) =>
        SegmentsById.TryGetValue(segmentId, out segment);
    
    /// <summary>
    /// Get the source location of a segment for highlighting in the original document.
    /// Returns character offsets and line number that can be used to extract/highlight
    /// the exact text in the source.
    /// </summary>
    /// <param name="segmentId">The segment ID</param>
    /// <returns>Source location info, or null if segment not found</returns>
    public SourceLocation? GetSourceLocation(string segmentId)
    {
        if (!TryGetSegment(segmentId, out var segment) || segment == null)
            return null;
            
        return new SourceLocation(
            SegmentId: segment.Id,
            StartChar: segment.StartChar,
            EndChar: segment.EndChar,
            LineNumber: segment.LineNumber,
            PageNumber: segment.PageNumber,
            SectionTitle: segment.SectionTitle,
            HeadingPath: segment.HeadingPath
        );
    }
    
    /// <summary>
    /// Extract the highlighted text from the original document using segment location info.
    /// </summary>
    /// <param name="originalDocument">The original document text</param>
    /// <param name="segmentId">The segment ID to highlight</param>
    /// <param name="contextChars">Number of characters of context to include before/after (default 0)</param>
    /// <returns>The extracted text with optional context, or null if segment not found</returns>
    public HighlightedText? GetHighlightedText(string originalDocument, string segmentId, int contextChars = 0)
    {
        var location = GetSourceLocation(segmentId);
        if (location == null) return null;
        
        var contextStart = Math.Max(0, location.StartChar - contextChars);
        var contextEnd = Math.Min(originalDocument.Length, location.EndChar + contextChars);
        
        return new HighlightedText(
            FullText: originalDocument[contextStart..contextEnd],
            HighlightStart: location.StartChar - contextStart,
            HighlightEnd: location.EndChar - contextStart,
            BeforeContext: contextChars > 0 ? originalDocument[contextStart..location.StartChar] : "",
            SegmentText: originalDocument[location.StartChar..location.EndChar],
            AfterContext: contextChars > 0 ? originalDocument[location.EndChar..contextEnd] : "",
            Location: location
        );
    }
}

/// <summary>
/// Source location of a segment in the original document.
/// Use this to highlight or link back to the exact source text.
/// </summary>
/// <param name="SegmentId">The segment's unique ID</param>
/// <param name="StartChar">Character offset where segment starts (0-based)</param>
/// <param name="EndChar">Character offset where segment ends (exclusive)</param>
/// <param name="LineNumber">Line number in source (1-based, if available)</param>
/// <param name="PageNumber">Page number for PDFs (1-based, if available)</param>
/// <param name="SectionTitle">Section heading the segment belongs to</param>
/// <param name="HeadingPath">Full heading path (e.g., "Chapter 1 > Intro")</param>
public record SourceLocation(
    string SegmentId,
    int StartChar,
    int EndChar,
    int? LineNumber,
    int? PageNumber,
    string SectionTitle,
    string HeadingPath
)
{
    /// <summary>Length of the segment in characters</summary>
    public int Length => EndChar - StartChar;
}

/// <summary>
/// Extracted text from the original document with highlight positions.
/// Use this to display the segment with surrounding context.
/// </summary>
/// <param name="FullText">The full extracted text including context</param>
/// <param name="HighlightStart">Start position of the segment within FullText</param>
/// <param name="HighlightEnd">End position of the segment within FullText</param>
/// <param name="BeforeContext">Text before the segment (if context was requested)</param>
/// <param name="SegmentText">The actual segment text</param>
/// <param name="AfterContext">Text after the segment (if context was requested)</param>
/// <param name="Location">The source location info</param>
public record HighlightedText(
    string FullText,
    int HighlightStart,
    int HighlightEnd,
    string BeforeContext,
    string SegmentText,
    string AfterContext,
    SourceLocation Location
)
{
    /// <summary>
    /// Format as HTML with the segment wrapped in a highlight span.
    /// </summary>
    /// <param name="highlightClass">CSS class for the highlight span (default: "highlight")</param>
    public string ToHtml(string highlightClass = "highlight") =>
        $"{System.Net.WebUtility.HtmlEncode(BeforeContext)}<span class=\"{highlightClass}\">{System.Net.WebUtility.HtmlEncode(SegmentText)}</span>{System.Net.WebUtility.HtmlEncode(AfterContext)}";
    
    /// <summary>
    /// Format as Markdown with the segment wrapped in **bold**.
    /// </summary>
    public string ToMarkdown() =>
        $"{BeforeContext}**{SegmentText}**{AfterContext}";
}

/// <summary>
/// Configuration for the extraction phase
/// </summary>
public class ExtractionConfig
{
    /// <summary>
    /// MMR lambda: 0=diversity, 1=relevance. Default 0.7 (favor relevance slightly)
    /// </summary>
    public double MmrLambda { get; set; } = 0.7;
    
    /// <summary>
    /// Fraction of segments to keep in salience ranking (0.15 = top 15%)
    /// </summary>
    public double ExtractionRatio { get; set; } = 0.15;
    
    /// <summary>
    /// Minimum segments to extract regardless of ratio
    /// </summary>
    public int MinSegments { get; set; } = 10;
    
    /// <summary>
    /// Maximum segments to extract regardless of ratio
    /// </summary>
    public int MaxSegments { get; set; } = 100;
    
    /// <summary>
    /// Size of fallback bucket (top-N by salience, always included for coverage)
    /// </summary>
    public int FallbackBucketSize { get; set; } = 10;
    
    /// <summary>
    /// Include code blocks as segments
    /// </summary>
    public bool IncludeCodeBlocks { get; set; } = true;
    
    /// <summary>
    /// Include list items as segments
    /// </summary>
    public bool IncludeListItems { get; set; } = true;
    
    /// <summary>
    /// Include table rows as segments (if tables detected)
    /// </summary>
    public bool IncludeTableRows { get; set; } = true;
    
    /// <summary>
    /// Minimum text length for a segment (shorter = skip)
    /// </summary>
    public int MinSegmentLength { get; set; } = 15;
    
    /// <summary>
    /// Maximum segments to process in one batch for memory efficiency.
    /// For very long documents (novels), segments are processed in batches.
    /// Default: 1000 (balances speed with memory)
    /// </summary>
    public int MaxBatchSize { get; set; } = 1000;
    
    /// <summary>
    /// For very long documents, use hierarchical extraction:
    /// First pass extracts top segments from each batch, second pass re-ranks globally.
    /// Default: true
    /// </summary>
    public bool UseHierarchicalExtraction { get; set; } = true;
    
    /// <summary>
    /// Maximum segments to embed. If document has more segments than this,
    /// pre-filter using cheap heuristics (position, length) before expensive embedding.
    /// This dramatically speeds up processing for medium-sized documents.
    /// Default: 200 (covers most articles, ~20-30s embedding time)
    /// </summary>
    public int MaxSegmentsToEmbed { get; set; } = 200;
    
    /// <summary>
    /// Use fast pre-filtering when segment count exceeds MaxSegmentsToEmbed.
    /// Pre-filtering uses semantic sampling: embed a small sample, compute centroid,
    /// then score all segments by similarity to centroid before full embedding.
    /// Default: true
    /// </summary>
    public bool UseFastPreFilter { get; set; } = true;
    
    /// <summary>
    /// Size of stratified sample for semantic pre-filtering centroid estimation.
    /// Larger samples = better centroid accuracy but slower pre-filtering.
    /// Default: 60 (good balance of speed and accuracy)
    /// </summary>
    public int PreFilterSampleSize { get; set; } = 60;
    
    /// <summary>
    /// Weight for semantic similarity (vs heuristic score) in pre-filter scoring.
    /// Range 0-1. Higher = more weight on actual semantic similarity.
    /// Default: 0.6 (60% semantic, 40% heuristic)
    /// </summary>
    public double PreFilterSemanticWeight { get; set; } = 0.6;
    
    // === Length-based quality scoring ===
    
    /// <summary>
    /// Minimum character length for a segment to receive full quality score.
    /// Segments shorter than this are penalized proportionally.
    /// Default: 80 characters (~15-20 words, a substantive sentence)
    /// </summary>
    public int IdealMinLength { get; set; } = 80;
    
    /// <summary>
    /// Maximum character length for quality scoring. Segments beyond this length
    /// receive no additional benefit. Default: 500 characters (~80-100 words)
    /// </summary>
    public int IdealMaxLength { get; set; } = 500;
    
    /// <summary>
    /// Minimum quality score for very short segments. This prevents short
    /// headings from being completely excluded but de-prioritizes them.
    /// Range 0-1. Default: 0.3 (short segments score at least 30% of full)
    /// </summary>
    public double MinLengthQualityScore { get; set; } = 0.3;
    
    /// <summary>
    /// Boost for headings. Reduced from 1.5 to prevent short headings from
    /// dominating the top segments. Default: 1.15
    /// </summary>
    public double HeadingBoost { get; set; } = 1.15;
    
    /// <summary>
    /// Boost for the document title (first H1 heading). Still important but
    /// balanced with content. Default: 1.8
    /// </summary>
    public double DocumentTitleBoost { get; set; } = 1.8;
}

/// <summary>
/// Configuration for the retrieval phase
/// </summary>
public class RetrievalConfig
{
    /// <summary>
    /// Use RRF (Reciprocal Rank Fusion) instead of weighted sum.
    /// RRF is scale-invariant and proven effective (used by Elasticsearch, Vespa).
    /// Default: true
    /// </summary>
    public bool UseRRF { get; set; } = true;
    
    /// <summary>
    /// RRF k parameter. Standard value is 60.
    /// Higher k = more weight to lower-ranked items.
    /// RRF(d) = sum(1 / (k + rank_i(d)))
    /// </summary>
    public int RrfK { get; set; } = 60;
    
    /// <summary>
    /// Weight for query similarity in combined score (1-alpha = salience weight)
    /// Only used when UseRRF = false
    /// </summary>
    public double Alpha { get; set; } = 0.6;
    
    /// <summary>
    /// Number of segments to retrieve for synthesis
    /// </summary>
    public int TopK { get; set; } = 20;
    
    /// <summary>
    /// Always include top-N salient segments regardless of query match
    /// </summary>
    public int FallbackCount { get; set; } = 5;
    
    /// <summary>
    /// Minimum similarity threshold to include a segment (only for non-RRF mode)
    /// </summary>
    public double MinSimilarity { get; set; } = 0.3;
    
    /// <summary>
    /// Use hybrid search (BM25 sparse + dense embeddings + salience).
    /// When true, combines lexical matching with semantic similarity for better recall.
    /// Default: true (recommended for documents with specific terminology)
    /// </summary>
    public bool UseHybridSearch { get; set; } = true;
    
    /// <summary>
    /// Weight for BM25 in hybrid RRF fusion.
    /// Only used when UseHybridSearch = true.
    /// Higher values give more weight to exact keyword matches.
    /// </summary>
    public double Bm25Weight { get; set; } = 1.0;
    
    /// <summary>
    /// Weight for dense (embedding) similarity in hybrid RRF fusion.
    /// </summary>
    public double DenseWeight { get; set; } = 1.0;
    
    /// <summary>
    /// Weight for salience scores in hybrid RRF fusion.
    /// </summary>
    public double SalienceWeight { get; set; } = 0.5;
    
    /// <summary>
    /// Enable adaptive TopK scaling based on document size and type.
    /// When enabled, TopK is automatically increased for longer documents
    /// and narrative content to maintain minimum coverage.
    /// Default: true
    /// </summary>
    public bool AdaptiveTopK { get; set; } = true;
    
    /// <summary>
    /// Minimum coverage percentage to aim for (e.g., 5.0 = retrieve ~5% of segments).
    /// Only used when AdaptiveTopK = true.
    /// Default: 5.0 (5% of document for better narrative coverage)
    /// </summary>
    public double MinCoveragePercent { get; set; } = 5.0;
    
    /// <summary>
    /// Maximum TopK regardless of document size (limited by LLM context).
    /// Default: 100
    /// </summary>
    public int MaxTopK { get; set; } = 100;
    
    /// <summary>
    /// Minimum TopK regardless of document size.
    /// Default: 15
    /// </summary>
    public int MinTopK { get; set; } = 15;
    
    /// <summary>
    /// Boost multiplier for narrative content (fiction needs more context).
    /// e.g., 1.5 = retrieve 50% more segments for narrative content.
    /// Default: 1.5
    /// </summary>
    public double NarrativeBoost { get; set; } = 1.5;
}
