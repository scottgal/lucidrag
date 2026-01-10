using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Mostlylucid.DocSummarizer.Models;

public enum OutputFormat
{
    Console,
    Text,
    Markdown,
    Json
}

/// <summary>
/// Position of a chunk within the document structure
/// </summary>
public enum ChunkPosition
{
    /// <summary>First 10-15% of document - states purpose, scope, thesis</summary>
    Introduction,
    /// <summary>Middle 70-80% of document - details, examples, supporting content</summary>
    Body,
    /// <summary>Last 10-15% of document - summary, conclusions, implications</summary>
    Conclusion
}

/// <summary>
/// Document content type - affects position weighting strategy
/// </summary>
public enum ContentType
{
    /// <summary>Unknown or mixed content - use moderate position weighting</summary>
    Unknown,
    /// <summary>Fiction/narrative - uniform weighting, narrative continuity matters more</summary>
    Narrative,
    /// <summary>Technical/academic - strong position bias (intro/conclusion high importance)</summary>
    Expository
}

/// <summary>
/// Position weighting configuration based on document type.
/// Based on BERT summarization research (Liu and Lapata 2019) and BookSum (Kryscinski 2021).
/// </summary>
public static class PositionWeights
{
    /// <summary>
    /// Get position weight for a chunk based on document content type.
    /// 
    /// Technical/Expository (BERT-style):
    ///   - Intro (first 15%): 1.5x - thesis, purpose, scope
    ///   - Conclusion (last 15%): 1.3x - findings, implications  
    ///   - Body: 1.0x - supporting details
    ///   
    /// Fiction/Narrative (BookSum-style):
    ///   - Opening (first 10%): 1.2x - scene setting, character intro
    ///   - Resolution (last 10%): 1.15x - plot resolution
    ///   - Body: 1.0x - plot development (all important for continuity)
    /// </summary>
    public static double GetWeight(ChunkPosition position, ContentType contentType) => contentType switch
    {
        ContentType.Expository => position switch
        {
            ChunkPosition.Introduction => 1.5,  // Thesis, purpose, scope
            ChunkPosition.Conclusion => 1.3,    // Findings, implications
            ChunkPosition.Body => 1.0,
            _ => 1.0
        },
        ContentType.Narrative => position switch
        {
            ChunkPosition.Introduction => 1.2,  // Scene setting, but not too high
            ChunkPosition.Conclusion => 1.15,   // Resolution matters but whole arc important
            ChunkPosition.Body => 1.0,          // Narrative continuity - all matters
            _ => 1.0
        },
        _ => position switch  // Unknown/default - moderate weighting
        {
            ChunkPosition.Introduction => 1.3,
            ChunkPosition.Conclusion => 1.2,
            ChunkPosition.Body => 1.0,
            _ => 1.0
        }
    };
    
    /// <summary>
    /// Get the percentage of document that counts as introduction
    /// </summary>
    public static double GetIntroThreshold(ContentType contentType) => contentType switch
    {
        ContentType.Expository => 0.15,  // Technical docs: first 15%
        ContentType.Narrative => 0.10,   // Fiction: first 10% (opening scene)
        _ => 0.12                         // Default: 12%
    };
    
    /// <summary>
    /// Get the percentage at which conclusion starts
    /// </summary>
    public static double GetConclusionThreshold(ContentType contentType) => contentType switch
    {
        ContentType.Expository => 0.85,  // Technical docs: last 15%
        ContentType.Narrative => 0.90,   // Fiction: last 10% (resolution)
        _ => 0.88                         // Default: last 12%
    };
}

public record DocumentChunk(
    int Order,
    string Heading,
    int HeadingLevel,
    string Content,
    string Hash,
    int? PageStart = null,
    int? PageEnd = null,
    int TotalChunks = 0)
{
    public string Id => $"chunk-{Order}";
    
    /// <summary>
    /// Determine the position of this chunk in the document structure.
    /// Uses default thresholds (15% intro, 15% conclusion).
    /// For content-type-specific thresholds, use GetPosition(ContentType).
    /// </summary>
    public ChunkPosition Position => GetPosition(ContentType.Unknown);
    
    /// <summary>
    /// Determine the position of this chunk with content-type-specific thresholds.
    /// </summary>
    public ChunkPosition GetPosition(ContentType contentType)
    {
        if (TotalChunks <= 0) return ChunkPosition.Body;
        
        var position = (double)Order / TotalChunks;
        var introThreshold = PositionWeights.GetIntroThreshold(contentType);
        var conclusionThreshold = PositionWeights.GetConclusionThreshold(contentType);
        
        if (position < introThreshold) return ChunkPosition.Introduction;
        if (position >= conclusionThreshold) return ChunkPosition.Conclusion;
        
        return ChunkPosition.Body;
    }
    
    /// <summary>
    /// Get the default position weight for this chunk (assumes expository content).
    /// For content-type-specific weighting, use GetPositionWeight(ContentType).
    /// </summary>
    public double PositionWeight => GetPositionWeight(ContentType.Unknown);
    
    /// <summary>
    /// Get the position weight for this chunk based on content type.
    /// 
    /// Technical/Expository: Intro 1.5x, Conclusion 1.3x, Body 1.0x
    /// Fiction/Narrative: Intro 1.2x, Conclusion 1.15x, Body 1.0x (more uniform)
    /// </summary>
    public double GetPositionWeight(ContentType contentType)
    {
        var position = GetPosition(contentType);
        return PositionWeights.GetWeight(position, contentType);
    }
    
    /// <summary>
    /// Get a human-readable reference (page number if available, otherwise section number)
    /// </summary>
    public string Reference => PageStart.HasValue 
        ? (PageStart == PageEnd || !PageEnd.HasValue ? $"p.{PageStart}" : $"pp.{PageStart}-{PageEnd}")
        : $"§{Order + 1}";
    
    /// <summary>
    /// Get citation format for use in summaries
    /// </summary>
    public string Citation => PageStart.HasValue
        ? $"[p.{PageStart}]"
        : $"[§{Order + 1}]";
    
    /// <summary>
    /// Create a new chunk with the total chunks count set (for position calculation)
    /// </summary>
    public DocumentChunk WithTotalChunks(int totalChunks) => this with { TotalChunks = totalChunks };
}

#region Claim Ledger System

/// <summary>
/// Type of claim - determines weight in final synthesis
/// </summary>
public enum ClaimType
{
    /// <summary>Directly stated in source text - highest weight</summary>
    Fact = 3,
    /// <summary>Logical deduction from stated facts - medium weight</summary>
    Inference = 2,
    /// <summary>Incidental detail, local colour - lowest weight</summary>
    Colour = 1
}

/// <summary>
/// Confidence level for claims and entities
/// </summary>
public enum ConfidenceLevel
{
    High = 3,
    Medium = 2,
    Low = 1,
    Uncertain = 0
}

/// <summary>
/// A typed citation with chunk reference and optional span
/// </summary>
public record Citation(
    string ChunkId,
    int? StartOffset = null,
    int? EndOffset = null)
{
    public override string ToString() => $"[{ChunkId}]";
}

/// <summary>
/// A claim extracted from the document with evidence trail
/// </summary>
public record Claim(
    string Text,
    ClaimType Type,
    ConfidenceLevel Confidence,
    List<Citation> Evidence,
    string? Topic = null)
{
    /// <summary>
    /// Computed weight for ranking claims in synthesis
    /// Weight = Type * Confidence * EvidenceCount
    /// </summary>
    public double Weight => (int)Type * (int)Confidence * Math.Max(1, Evidence.Count);
    
    /// <summary>
    /// Render claim with citations for output
    /// </summary>
    public string Render() => Evidence.Count > 0 
        ? $"{Text} {string.Join(" ", Evidence.Select(e => e.ToString()))}"
        : Text;
}

/// <summary>
/// Collection of claims with operations for synthesis
/// </summary>
public class ClaimLedger
{
    private readonly List<Claim> _claims = [];
    
    public IReadOnlyList<Claim> Claims => _claims;
    
    public void Add(Claim claim) => _claims.Add(claim);
    
    public void AddRange(IEnumerable<Claim> claims) => _claims.AddRange(claims);
    
    /// <summary>
    /// Get top claims by weight, optionally filtered by type
    /// </summary>
    public List<Claim> GetTopClaims(int count, ClaimType? minType = null)
    {
        var query = _claims.AsEnumerable();
        if (minType.HasValue)
            query = query.Where(c => c.Type >= minType.Value);
        
        return query
            .OrderByDescending(c => c.Weight)
            .Take(count)
            .ToList();
    }
    
    /// <summary>
    /// Get claims grouped by topic
    /// </summary>
    public Dictionary<string, List<Claim>> GetByTopic()
    {
        return _claims
            .Where(c => !string.IsNullOrEmpty(c.Topic))
            .GroupBy(c => c.Topic!)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Weight).ToList());
    }
    
    /// <summary>
    /// Get only fact-type claims (highest confidence)
    /// </summary>
    public List<Claim> GetFacts() => _claims
        .Where(c => c.Type == ClaimType.Fact)
        .OrderByDescending(c => c.Weight)
        .ToList();
    
    /// <summary>
    /// Total citation count across all claims
    /// </summary>
    public int TotalCitations => _claims.Sum(c => c.Evidence.Count);
    
    /// <summary>
    /// Citation rate: claims with evidence / total claims
    /// </summary>
    public double CitationRate => _claims.Count > 0 
        ? (double)_claims.Count(c => c.Evidence.Count > 0) / _claims.Count 
        : 0;
}

#endregion

#region Enhanced Entity System

/// <summary>
/// A normalized entity with canonical name and aliases
/// </summary>
public record NormalizedEntity(
    string CanonicalName,
    List<string> Aliases,
    string EntityType, // "character", "location", "date", "event", "organization"
    ConfidenceLevel Confidence,
    List<string> SourceChunks)
{
    /// <summary>
    /// Check if a name matches this entity (canonical or alias)
    /// </summary>
    public bool Matches(string name) =>
        CanonicalName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        Aliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Entities extracted from a chunk of text
/// </summary>
public record ExtractedEntities(
    List<string> Characters,
    List<string> Locations,
    List<string> Dates,
    List<string> Events,
    List<string> Organizations)
{
    public static ExtractedEntities Empty => new([], [], [], [], []);
    
    /// <summary>
    /// Merge multiple entity sets with fuzzy deduplication
    /// </summary>
    public static ExtractedEntities Merge(IEnumerable<ExtractedEntities> entities)
    {
        var all = entities.ToList();
        return new ExtractedEntities(
            NormalizeAndDedupe(all.SelectMany(e => e.Characters)),
            NormalizeAndDedupe(all.SelectMany(e => e.Locations)),
            NormalizeAndDedupe(all.SelectMany(e => e.Dates)),
            NormalizeAndDedupe(all.SelectMany(e => e.Events)),
            NormalizeAndDedupe(all.SelectMany(e => e.Organizations))
        );
    }
    
    /// <summary>
    /// Normalize and deduplicate entity names with fuzzy matching
    /// </summary>
    private static List<string> NormalizeAndDedupe(IEnumerable<string> names)
    {
        var normalized = new List<string>();
        
        foreach (var name in names)
        {
            var cleanName = CleanEntityName(name);
            if (string.IsNullOrWhiteSpace(cleanName) || cleanName.Length < 2)
                continue;
            
            // Check if this is a fuzzy match to an existing entity
            var existingMatch = normalized.FirstOrDefault(n => IsFuzzyMatch(n, cleanName));
            if (existingMatch == null)
            {
                normalized.Add(cleanName);
            }
            else
            {
                // Keep the longer/more complete version
                if (cleanName.Length > existingMatch.Length)
                {
                    normalized.Remove(existingMatch);
                    normalized.Add(cleanName);
                }
            }
        }
        
        return normalized;
    }
    
    /// <summary>
    /// Clean up entity name - remove titles, honorifics that cause duplicates
    /// </summary>
    private static string CleanEntityName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        
        var clean = name.Trim()
            .Trim('*', '[', ']', '"', '\'', '-', '(', ')')
            .Trim();
        
        // Skip obviously bad entries
        if (clean.Length < 2 || 
            clean.StartsWith("no ", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("unnamed", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("not mentioned", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
        
        return clean;
    }
    
    /// <summary>
    /// Check if two names are fuzzy matches (same person/place with different formats)
    /// </summary>
    private static bool IsFuzzyMatch(string a, string b)
    {
        // Exact match (case insensitive)
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
            return true;
        
        // One contains the other (e.g., "Holmes" vs "Sherlock Holmes")
        if (a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
            b.Contains(a, StringComparison.OrdinalIgnoreCase))
            return true;
        
        // Handle title variations: "Mr. X" vs "X"
        var titlesToStrip = new[] { "Mr.", "Mrs.", "Ms.", "Dr.", "Sir", "Lady", "Lord", "Miss", "Captain", "Major", "Colonel", "Inspector" };
        var cleanA = a;
        var cleanB = b;
        foreach (var title in titlesToStrip)
        {
            cleanA = cleanA.Replace(title, "", StringComparison.OrdinalIgnoreCase).Trim();
            cleanB = cleanB.Replace(title, "", StringComparison.OrdinalIgnoreCase).Trim();
        }
        
        if (!string.IsNullOrEmpty(cleanA) && !string.IsNullOrEmpty(cleanB))
        {
            if (cleanA.Equals(cleanB, StringComparison.OrdinalIgnoreCase))
                return true;
            if (cleanA.Contains(cleanB, StringComparison.OrdinalIgnoreCase) ||
                cleanB.Contains(cleanA, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        
        return false;
    }
    
    public bool HasAny => Characters.Count > 0 || Locations.Count > 0 || Dates.Count > 0 || 
                          Events.Count > 0 || Organizations.Count > 0;
}

#endregion

public record ChunkSummary(
    string ChunkId,
    string Heading,
    string Summary,
    int Order,
    ExtractedEntities? Entities = null);

#region Structured MapReduce Output Schema

/// <summary>
/// Structured map output for better reduce merging.
/// Produces facts + uncertainty instead of prose.
/// </summary>
/// <param name="ChunkId">Chunk identifier</param>
/// <param name="Order">Order in document</param>
/// <param name="Heading">Section heading</param>
/// <param name="Entities">Named entities: types, classes, modules, people, organizations</param>
/// <param name="Functions">Functions, methods, or actions described in this chunk</param>
/// <param name="KeyFlows">Key flows or relationships: A to B to C</param>
/// <param name="Facts">Facts with confidence levels</param>
/// <param name="Uncertainties">Explicit not enough context flags</param>
/// <param name="Quotables">Very short quotable excerpts (optional)</param>
public record StructuredMapOutput(
    string ChunkId,
    int Order,
    string Heading,
    List<EntityReference> Entities,
    List<FunctionReference> Functions,
    List<FlowReference> KeyFlows,
    List<FactClaim> Facts,
    List<UncertaintyFlag> Uncertainties,
    List<string> Quotables);

/// <summary>
/// Entity reference with type classification
/// </summary>
public record EntityReference(
    string Name,
    EntityType Type,
    string? Description = null,
    List<string>? Aliases = null);

public enum EntityType
{
    Unknown,
    Class,
    Interface,
    Module,
    Function,
    Variable,
    Config,
    Person,
    Organization,
    Location,
    Concept,
    Technology,
    Event
}

/// <summary>
/// Function or method reference
/// </summary>
public record FunctionReference(
    string Name,
    string? Purpose = null,
    List<string>? Inputs = null,
    List<string>? Outputs = null,
    List<string>? SideEffects = null,
    string? SourceChunk = null);

/// <summary>
/// Key flow or relationship
/// </summary>
public record FlowReference(
    List<string> Steps,
    FlowType Type,
    string? Description = null);

public enum FlowType
{
    DataFlow,
    ControlFlow,
    Dependency,
    Inheritance,
    Composition,
    Communication,
    Workflow
}

/// <summary>
/// Fact claim with confidence and evidence
/// </summary>
public record FactClaim(
    string Statement,
    ConfidenceLevel Confidence,
    string? Evidence = null,
    string? SourceChunk = null);

// Note: ConfidenceLevel enum is defined in Claim Ledger System region above

/// <summary>
/// Explicit uncertainty or missing context flag
/// </summary>
public record UncertaintyFlag(
    string Description,
    UncertaintyType Type,
    List<string>? AffectedEntities = null);

public enum UncertaintyType
{
    MissingContext,
    Ambiguous,
    Contradictory,
    IncompleteData,
    ExternalDependency
}

#endregion

#region Stitcher Output (Pre-Reduce Deduplication)

/// <summary>
/// Result of the stitcher pass that dedupes and resolves entities before final reduce
/// </summary>
/// <param name="Entities">Deduplicated entities with merge info</param>
/// <param name="References">Adjacency list: who calls/uses what</param>
/// <param name="Collisions">Name collisions that could not be auto-resolved</param>
/// <param name="CoverageMap">Coverage map: which chunks cover which topics</param>
public record StitcherOutput(
    List<MergedEntity> Entities,
    Dictionary<string, List<string>> References,
    List<NameCollision> Collisions,
    Dictionary<string, List<string>> CoverageMap);

/// <summary>
/// Entity merged from multiple chunks
/// </summary>
public record MergedEntity(
    string CanonicalName,
    EntityType Type,
    List<string> SourceChunks,
    List<string> Aliases,
    string? Description = null);

/// <summary>
/// Name collision that needs manual resolution
/// </summary>
public record NameCollision(
    string Name,
    List<string> ConflictingChunks,
    string? SuggestedResolution = null);

#endregion

#region Loss-Aware Reduce Output

/// <summary>
/// Reduce output that preserves contradictions and tracks coverage
/// </summary>
/// <param name="ExecutiveSummary">The executive summary text</param>
/// <param name="Contradictions">Contradictions found (not resolved, preserved)</param>
/// <param name="Coverage">What parts are definitely covered vs inferred</param>
/// <param name="RetrievalQuestions">Questions that need retrieval to answer</param>
/// <param name="OverallConfidence">Confidence in overall synthesis</param>
public record LossAwareReduceOutput(
    string ExecutiveSummary,
    List<Contradiction> Contradictions,
    CoverageReport Coverage,
    List<string> RetrievalQuestions,
    ConfidenceLevel OverallConfidence);

/// <summary>
/// Contradiction found during reduce (preserved, not resolved)
/// </summary>
public record Contradiction(
    string Description,
    List<string> ConflictingChunks,
    string? PossibleReason = null);

/// <summary>
/// Coverage report showing what's covered vs inferred
/// </summary>
/// <param name="DirectlyCovered">Topics with direct evidence</param>
/// <param name="Inferred">Topics inferred from context</param>
/// <param name="NotCovered">Topics with no coverage</param>
/// <param name="CoverageRatio">Overall coverage ratio (0-1)</param>
public record CoverageReport(
    List<string> DirectlyCovered,
    List<string> Inferred,
    List<string> NotCovered,
    double CoverageRatio);

#endregion

public record TopicSummary(
    string Topic,
    string Summary,
    List<string> SourceChunks);

public record DocumentSummary(
    string ExecutiveSummary,
    List<TopicSummary> TopicSummaries,
    List<string> OpenQuestions,
    SummarizationTrace Trace,
    ExtractedEntities? Entities = null);

public record SummarizationTrace(
    string DocumentId,
    int TotalChunks,
    int ChunksProcessed,
    List<string> Topics,
    TimeSpan TotalTime,
    double CoverageScore,
    double CitationRate,
    List<ChunkIndexEntry>? ChunkIndex = null);

/// <summary>
/// Index entry for a document chunk - provides overview without full content
/// </summary>
public record ChunkIndexEntry(
    string Id,
    int Order,
    string Heading,
    int HeadingLevel,
    string Preview,
    int TokenEstimate)
{
    /// <summary>
    /// Create a chunk index entry from a DocumentChunk
    /// </summary>
    public static ChunkIndexEntry FromChunk(DocumentChunk chunk, int previewLength = 80)
    {
        var preview = chunk.Content.Length > previewLength
            ? chunk.Content[..previewLength].Replace('\n', ' ').Replace('\r', ' ').Trim() + "..."
            : chunk.Content.Replace('\n', ' ').Replace('\r', ' ').Trim();
        
        // Estimate tokens (~4 chars per token)
        var tokenEstimate = chunk.Content.Length / 4;
        
        return new ChunkIndexEntry(
            chunk.Id,
            chunk.Order,
            chunk.Heading,
            chunk.HeadingLevel,
            preview,
            tokenEstimate);
    }
}

public record ValidationResult(
    int TotalCitations,
    int InvalidCount,
    bool IsValid,
    List<string> InvalidCitations);

public enum SummarizationMode
{
    /// <summary>
    /// Automatic mode selection based on document size, LLM availability, and query presence.
    /// Picks the optimal mode for each situation.
    /// </summary>
    Auto,
    
    /// <summary>
    /// Pure BERT extractive summarization using local ONNX models.
    /// No LLM required - fastest, deterministic, perfect citation grounding.
    /// Best for: offline use, quick summaries, when LLM unavailable.
    /// </summary>
    Bert,
    
    /// <summary>
    /// Hybrid: BERT extracts key sentences, LLM polishes into fluent prose.
    /// Combines grounding (no hallucination) with fluency.
    /// Best for: large documents, balanced speed/quality.
    /// </summary>
    BertHybrid,
    
    /// <summary>
    /// Simple iterative summarization - sends chunks to LLM sequentially.
    /// Best for: small documents that fit in context window.
    /// </summary>
    Iterative,
    
    /// <summary>
    /// Map-Reduce: parallel chunk summarization then synthesis.
    /// Best for: large documents, when you need full coverage.
    /// </summary>
    MapReduce,
    
    /// <summary>
    /// RAG-based: semantic search + focused summarization.
    /// Best for: focus queries, question-answering, when you need specific information.
    /// </summary>
    Rag,
    
    /// <summary>
    /// BERT→RAG pipeline: production-grade summarization.
    /// 1. Extract: Parse into segments with embeddings + salience scores
    /// 2. Retrieve: Dual-score ranking (query similarity + salience)
    /// 3. Synthesize: LLM generates fluent summary from retrieved segments
    /// 
    /// Properties:
    /// - LLM only at synthesis (no LLM-in-the-loop evaluation)
    /// - Deterministic extraction (reproducible, debuggable)
    /// - Perfect citations (every claim traceable to source segment)
    /// - Scales to any document size
    /// - Cost-optimal (cheap CPU work first, expensive LLM last)
    /// 
    /// Best for: large documents, production systems, when you need both quality and traceability.
    /// </summary>
    BertRag,
    
    /// <summary>
    /// Hierarchical collection summarization for anthologies and complete works.
    /// 
    /// Strategy (Map-Reduce with sampling):
    /// 1. DETECT: Identify collection structure (Shakespeare plays, story anthologies, etc.)
    /// 2. PARTITION: Split into individual works using H1 boundaries
    /// 3. SAMPLE: For large collections, sample representative works from each category
    /// 4. MAP: Summarize each work independently with progress indicator
    /// 5. REDUCE: Synthesize work summaries into collection overview
    /// 
    /// Properties:
    /// - Avoids the "only saw one play" problem
    /// - Ensures coverage across all works/genres
    /// - Produces hierarchical output (collection + individual work summaries)
    /// 
    /// Best for: Shakespeare complete works, story anthologies, essay collections, multi-book series.
    /// </summary>
    Hierarchical
}

public static class HashHelper
{
    /// <summary>
    /// Compute content hash using XxHash64 (fast, consistent).
    /// </summary>
    public static string ComputeHash(string content)
        => Mostlylucid.Summarizer.Core.Utilities.ContentHasher.ComputeHash(content);
}

public static class CitationValidator
{
    // Flexible: matches any bracketed reference like [chunk-0], [chunk-12], etc.
    private static readonly Regex CitationPattern = new(@"\[([^\]]+)\]", RegexOptions.Compiled);

    public static ValidationResult Validate(string summary, HashSet<string> validChunkIds)
    {
        var matches = CitationPattern.Matches(summary);
        var citations = matches
            .Select(m => m.Groups[1].Value)
            .Where(id => id.StartsWith("chunk-", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var invalid = citations.Where(c => !validChunkIds.Contains(c)).ToList();

        return new ValidationResult(
            citations.Count,
            invalid.Count,
            invalid.Count == 0 && citations.Count > 0,
            invalid);
    }
}

public record BatchResult(
    string FilePath,
    bool Success,
    DocumentSummary? Summary,
    string? Error,
    TimeSpan ProcessingTime,
    string? StackTrace = null);

public record BatchSummary(
    int TotalFiles,
    int SuccessCount,
    int FailureCount,
    List<BatchResult> Results,
    TimeSpan TotalTime)
{
    public double SuccessRate => TotalFiles > 0 ? (double)SuccessCount / TotalFiles : 0;
}

#region Tool Mode Output Models

/// <summary>
/// Structured output for LLM tool integration.
/// Designed to be machine-readable and grounded with evidence.
/// </summary>
public record ToolOutput
{
    /// <summary>Whether the operation succeeded</summary>
    public required bool Success { get; init; }
    
    /// <summary>Error message if failed</summary>
    public string? Error { get; init; }
    
    /// <summary>Source URL or file path</summary>
    public required string Source { get; init; }
    
    /// <summary>Content type fetched</summary>
    public string? ContentType { get; init; }
    
    /// <summary>The summary result (null if failed or in QA mode)</summary>
    public ToolSummary? Summary { get; init; }
    
    /// <summary>The QA answer result (null if failed or in summary mode)</summary>
    public ToolAnswer? Answer { get; init; }
    
    /// <summary>Processing metadata</summary>
    public ToolMetadata? Metadata { get; init; }
}

/// <summary>
/// QA answer for tool output
/// </summary>
public record ToolAnswer
{
    /// <summary>The question that was asked</summary>
    public required string Question { get; init; }
    
    /// <summary>The answer generated from the document</summary>
    public required string Response { get; init; }
    
    /// <summary>Mode used (RAG, etc.)</summary>
    public required string Mode { get; init; }
    
    /// <summary>Relevant chunk IDs that informed the answer</summary>
    public List<string>? SourceChunks { get; init; }
}

/// <summary>
/// Summary output with evidence-grounded claims
/// </summary>
public record ToolSummary
{
    /// <summary>Executive summary (1-3 sentences)</summary>
    public required string Executive { get; init; }
    
    /// <summary>Key facts extracted with evidence IDs</summary>
    public required List<GroundedClaim> KeyFacts { get; init; }
    
    /// <summary>Topics covered with summaries</summary>
    public required List<ToolTopic> Topics { get; init; }
    
    /// <summary>Named entities found</summary>
    public ToolEntities? Entities { get; init; }
    
    /// <summary>Questions the document doesn't answer</summary>
    public List<string>? OpenQuestions { get; init; }
}

/// <summary>
/// A claim grounded with evidence references
/// </summary>
public record GroundedClaim
{
    /// <summary>The claim/fact</summary>
    public required string Claim { get; init; }
    
    /// <summary>Confidence: high, medium, low</summary>
    public required string Confidence { get; init; }
    
    /// <summary>Evidence chunk IDs that support this claim</summary>
    public required List<string> Evidence { get; init; }
    
    /// <summary>Type: fact, inference, or color</summary>
    public string Type { get; init; } = "fact";
}

/// <summary>
/// Topic with grounded summary
/// </summary>
public record ToolTopic
{
    /// <summary>Topic name</summary>
    public required string Name { get; init; }
    
    /// <summary>Summary of this topic</summary>
    public required string Summary { get; init; }
    
    /// <summary>Evidence chunk IDs</summary>
    public required List<string> Evidence { get; init; }
}

/// <summary>
/// Extracted entities for tool output
/// </summary>
public record ToolEntities
{
    /// <summary>People mentioned</summary>
    public List<string>? People { get; init; }
    
    /// <summary>Organizations mentioned</summary>
    public List<string>? Organizations { get; init; }
    
    /// <summary>Locations mentioned</summary>
    public List<string>? Locations { get; init; }
    
    /// <summary>Dates/times mentioned</summary>
    public List<string>? Dates { get; init; }
    
    /// <summary>Technical terms/concepts</summary>
    public List<string>? Concepts { get; init; }
    
    /// <summary>URLs/links found</summary>
    public List<string>? Links { get; init; }
}

/// <summary>
/// Processing metadata for tool output
/// </summary>
public record ToolMetadata
{
    /// <summary>Time taken to process</summary>
    public required double ProcessingSeconds { get; init; }
    
    /// <summary>Number of chunks processed</summary>
    public required int ChunksProcessed { get; init; }
    
    /// <summary>Model used for summarization</summary>
    public required string Model { get; init; }
    
    /// <summary>Summarization mode used</summary>
    public required string Mode { get; init; }
    
    /// <summary>Coverage score (0-1)</summary>
    public double CoverageScore { get; init; }
    
    /// <summary>Citation rate (0-1)</summary>
    public double CitationRate { get; init; }
    
    /// <summary>Fetch timestamp (ISO 8601)</summary>
    public string? FetchedAt { get; init; }
    
    /// <summary>Final URL after redirects</summary>
    public string? FinalUrl { get; init; }
}

#endregion