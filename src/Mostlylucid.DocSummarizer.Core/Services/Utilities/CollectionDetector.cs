using System.Text.RegularExpressions;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Services.Utilities;

/// <summary>
/// Detects whether a document is a collection/anthology (multiple distinct works)
/// vs a single work with chapters, and extracts structure for hierarchical summarization.
/// 
/// KEY DISTINCTION:
/// - CHAPTERS: Sequential numbering ("Chapter 1", "Chapter 2", "I", "II")
///   → Single work, standard summarization
/// - COLLECTION: Distinct titled works ("The Tragedy of Hamlet", "Sonnet 18")
///   → Multiple works, hierarchical summarization
/// 
/// Signals that indicate a collection:
/// - Many H1 headings with DISTINCT titles (not sequential numbers)
/// - Table of Contents listing titled works
/// - Known patterns: "Complete Works", "Collected", "Anthology"
/// - Shakespeare-specific: "THE TRAGEDY OF", "THE COMEDY OF"
/// </summary>
public static class CollectionDetector
{
    /// <summary>
    /// Minimum distinct work titles to consider a document a collection
    /// </summary>
    private const int MinWorksForCollection = 3;

    /// <summary>
    /// Patterns that indicate a table of contents section
    /// </summary>
    private static readonly string[] TocPatterns =
    {
        "table of contents",
        "contents",
        "index",
        "list of plays",
        "list of works",
        "dramatis personae" // Shakespeare specific - character list
    };

    /// <summary>
    /// Title patterns that indicate a collection
    /// </summary>
    private static readonly string[] CollectionTitlePatterns =
    {
        "complete works",
        "collected works",
        "collected poems",
        "collected stories",
        "anthology",
        "selected works",
        "the works of",
        "plays of",
        "complete plays"
    };

    /// <summary>
    /// Patterns for Shakespeare play titles (H1 or H2)
    /// </summary>
    private static readonly Regex ShakespearePlayPattern = new(
        @"^(THE\s+)?(TRAGEDY|COMEDY|HISTORY|LIFE|FAMOUS HISTORY)\s+OF\s+|^(A\s+)?(MIDSUMMER|WINTER'?S|MERCHANT)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Patterns that indicate a CHAPTER heading (not a distinct work).
    /// These are sequential/numbered and belong to ONE work.
    /// </summary>
    private static readonly Regex ChapterPattern = new(
        @"^(chapter|part|book|act|scene|section|volume)\s*[ivxlcdm\d]+\b|" +  // "Chapter 1", "Part IV", "Act III"
        @"^[ivxlcdm]+\.?\s*$|" +                                                // Just "IV" or "III."
        @"^\d+\.?\s*$|" +                                                       // Just "1" or "2."
        @"^(prologue|epilogue|introduction|preface|conclusion)\s*$",            // Standard book sections
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Analyze a parsed document to detect if it's a collection.
    /// </summary>
    public static CollectionInfo Analyze(ParsedDocument document, string? title = null)
    {
        var sections = document.Sections;
        
        // Count H1 headings (top-level)
        var h1Sections = sections.Where(s => s.Level == 1).ToList();
        var h1Count = h1Sections.Count;
        
        // CRITICAL: Distinguish chapters from distinct works
        var chapterCount = h1Sections.Count(s => IsChapterHeading(s.Heading));
        var distinctWorkCount = h1Sections.Count(s => !IsChapterHeading(s.Heading) && !IsMeta(s.Heading));
        
        // If most H1s are chapters, this is a single work with chapters, NOT a collection
        var chapterRatio = h1Count > 0 ? (double)chapterCount / h1Count : 0;
        var isChapteredNovel = chapterRatio > 0.7 && chapterCount >= 3;
        
        // Look for TOC section
        var tocSection = FindTocSection(sections);
        var tocEntries = tocSection != null ? ExtractTocEntries(tocSection) : new List<string>();
        
        // Check for collection indicators in title
        var titleLower = title?.ToLowerInvariant() ?? "";
        var hasCollectionTitle = CollectionTitlePatterns.Any(p => titleLower.Contains(p));
        
        // Check for Shakespeare-specific patterns
        var shakespeareWorks = h1Sections
            .Where(s => ShakespearePlayPattern.IsMatch(s.Heading))
            .Select(s => s.Heading)
            .ToList();
        var isShakespeare = shakespeareWorks.Count >= 3;
        
        // Calculate confidence - but REDUCE if it looks like chapters
        var confidence = CalculateConfidence(
            distinctWorkCount,  // Use distinct works, not raw H1 count
            tocEntries.Count, 
            hasCollectionTitle, 
            isShakespeare);
        
        // Penalize confidence heavily if this looks like a chaptered novel
        if (isChapteredNovel)
        {
            confidence *= 0.2; // Reduce to 20% - almost certainly not a collection
        }
        
        // Determine if this is a collection
        // Must have distinct work titles, not just many chapters
        var isCollection = !isChapteredNovel && 
                          (confidence >= 0.6 || distinctWorkCount >= MinWorksForCollection);
        
        // Extract work titles (excluding chapters)
        var works = ExtractWorks(h1Sections, tocEntries, shakespeareWorks);
        
        return new CollectionInfo
        {
            IsCollection = isCollection,
            Confidence = confidence,
            WorkCount = works.Count,
            Works = works,
            HasTableOfContents = tocSection != null,
            TocEntries = tocEntries,
            CollectionTitle = title,
            IsShakespeare = isShakespeare,
            IsChapteredNovel = isChapteredNovel,
            ChapterCount = chapterCount,
            H1Count = h1Count,
            TotalSections = sections.Count,
            TotalSentences = document.SentenceCount
        };
    }
    
    /// <summary>
    /// Check if a heading looks like a chapter marker rather than a distinct work title.
    /// </summary>
    private static bool IsChapterHeading(string heading)
    {
        var trimmed = heading.Trim();
        return ChapterPattern.IsMatch(trimmed);
    }

    /// <summary>
    /// Analyze from raw markdown (convenience method).
    /// </summary>
    public static CollectionInfo AnalyzeMarkdown(string markdown, string? title = null)
    {
        var parser = new MarkdigDocumentParser();
        var document = parser.Parse(markdown);
        return Analyze(document, title);
    }

    /// <summary>
    /// Quick check - is this likely a collection? (fast, low confidence)
    /// </summary>
    public static bool QuickIsCollection(string markdown)
    {
        // Count H1 headings with simple regex
        var h1Count = Regex.Matches(markdown, @"^#\s+[^#]", RegexOptions.Multiline).Count;
        if (h1Count >= MinWorksForCollection)
            return true;

        // Check for collection title patterns
        var first500 = markdown.Length > 500 ? markdown[..500] : markdown;
        var lower = first500.ToLowerInvariant();
        
        return CollectionTitlePatterns.Any(p => lower.Contains(p));
    }

    private static ParsedSection? FindTocSection(List<ParsedSection> sections)
    {
        foreach (var section in sections)
        {
            var headingLower = section.Heading.ToLowerInvariant();
            if (TocPatterns.Any(p => headingLower.Contains(p)))
            {
                return section;
            }
        }
        return null;
    }

    private static List<string> ExtractTocEntries(ParsedSection tocSection)
    {
        var entries = new List<string>();
        
        // TOC entries are typically list items or short paragraphs
        foreach (var item in tocSection.ListItems)
        {
            var cleaned = CleanTocEntry(item);
            if (!string.IsNullOrWhiteSpace(cleaned))
                entries.Add(cleaned);
        }
        
        // Also check paragraphs - some TOCs are just lines of text
        foreach (var para in tocSection.Paragraphs)
        {
            // Split by newlines - each line might be an entry
            var lines = para.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var cleaned = CleanTocEntry(line);
                if (!string.IsNullOrWhiteSpace(cleaned) && cleaned.Length > 3 && cleaned.Length < 100)
                    entries.Add(cleaned);
            }
        }
        
        return entries.Distinct().ToList();
    }

    private static string CleanTocEntry(string entry)
    {
        // Remove page numbers, dots, leading numbers
        var cleaned = Regex.Replace(entry, @"\.{2,}\s*\d+$", ""); // Remove "... 123"
        cleaned = Regex.Replace(cleaned, @"^\d+\.\s*", ""); // Remove "1. "
        cleaned = Regex.Replace(cleaned, @"\s+\d+$", ""); // Remove trailing page number
        return cleaned.Trim();
    }

    private static double CalculateConfidence(int h1Count, int tocEntryCount, bool hasCollectionTitle, bool isShakespeare)
    {
        var confidence = 0.0;
        
        // H1 count is primary signal
        if (h1Count >= 10) confidence += 0.4;
        else if (h1Count >= 5) confidence += 0.3;
        else if (h1Count >= 3) confidence += 0.2;
        
        // TOC presence
        if (tocEntryCount >= 10) confidence += 0.3;
        else if (tocEntryCount >= 5) confidence += 0.2;
        else if (tocEntryCount > 0) confidence += 0.1;
        
        // Title pattern
        if (hasCollectionTitle) confidence += 0.2;
        
        // Shakespeare detection (very high confidence)
        if (isShakespeare) confidence += 0.3;
        
        return Math.Min(1.0, confidence);
    }

    private static List<WorkInfo> ExtractWorks(
        List<ParsedSection> h1Sections,
        List<string> tocEntries,
        List<string> shakespeareWorks)
    {
        var works = new List<WorkInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // Prefer H1 sections as work boundaries
        foreach (var section in h1Sections)
        {
            var title = section.Heading.Trim();
            if (string.IsNullOrWhiteSpace(title) || seen.Contains(title))
                continue;
                
            // Skip meta sections and chapter headings
            if (IsMeta(title) || IsChapterHeading(title))
                continue;
                
            seen.Add(title);
            works.Add(new WorkInfo
            {
                Title = title,
                HeadingLevel = section.Level,
                SentenceCount = section.Sentences.Count,
                IsShakespearePlay = ShakespearePlayPattern.IsMatch(title),
                IsChapter = false
            });
        }
        
        // If no H1 sections found but we have TOC entries, use those
        if (works.Count == 0 && tocEntries.Count > 0)
        {
            foreach (var entry in tocEntries)
            {
                if (!seen.Contains(entry) && !IsMeta(entry) && !IsChapterHeading(entry))
                {
                    seen.Add(entry);
                    works.Add(new WorkInfo
                    {
                        Title = entry,
                        HeadingLevel = 0, // Unknown
                        SentenceCount = 0,
                        IsShakespearePlay = ShakespearePlayPattern.IsMatch(entry),
                        IsChapter = false
                    });
                }
            }
        }
        
        return works;
    }

    private static bool IsMeta(string title)
    {
        var lower = title.ToLowerInvariant();
        var metaPatterns = new[]
        {
            "contents", "index", "preface", "introduction", "foreword",
            "copyright", "dedication", "acknowledgment", "about",
            "appendix", "notes", "bibliography", "glossary"
        };
        return metaPatterns.Any(p => lower.Contains(p));
    }
}

/// <summary>
/// Information about a detected collection (anthology, complete works, etc.)
/// </summary>
public class CollectionInfo
{
    /// <summary>
    /// Whether this document appears to be a collection of multiple works
    /// </summary>
    public bool IsCollection { get; init; }
    
    /// <summary>
    /// Confidence score (0-1) that this is a collection
    /// </summary>
    public double Confidence { get; init; }
    
    /// <summary>
    /// Number of distinct works detected
    /// </summary>
    public int WorkCount { get; init; }
    
    /// <summary>
    /// List of detected works with metadata
    /// </summary>
    public List<WorkInfo> Works { get; init; } = new();
    
    /// <summary>
    /// Whether a table of contents was found
    /// </summary>
    public bool HasTableOfContents { get; init; }
    
    /// <summary>
    /// Entries extracted from the table of contents
    /// </summary>
    public List<string> TocEntries { get; init; } = new();
    
    /// <summary>
    /// Title of the collection (if detected)
    /// </summary>
    public string? CollectionTitle { get; init; }
    
    /// <summary>
    /// Whether this appears to be Shakespeare's works
    /// </summary>
    public bool IsShakespeare { get; init; }
    
    /// <summary>
    /// Whether this appears to be a single novel with chapters (NOT a collection)
    /// </summary>
    public bool IsChapteredNovel { get; init; }
    
    /// <summary>
    /// Number of chapter-style headings detected (Chapter 1, Part II, etc.)
    /// </summary>
    public int ChapterCount { get; init; }
    
    /// <summary>
    /// Count of H1 headings
    /// </summary>
    public int H1Count { get; init; }
    
    /// <summary>
    /// Total section count
    /// </summary>
    public int TotalSections { get; init; }
    
    /// <summary>
    /// Total sentence count in the document
    /// </summary>
    public int TotalSentences { get; init; }
    
    /// <summary>
    /// Get recommended summarization strategy
    /// </summary>
    public CollectionStrategy GetRecommendedStrategy()
    {
        if (!IsCollection)
            return CollectionStrategy.SingleDocument;
            
        if (WorkCount > 20)
            return CollectionStrategy.HierarchicalWithIndex; // Too many works - need index-based sampling
            
        if (WorkCount > 10)
            return CollectionStrategy.HierarchicalSampled; // Sample representative works
            
        return CollectionStrategy.HierarchicalFull; // Summarize each work
    }
    
    /// <summary>
    /// Get a summary of the detection results
    /// </summary>
    public override string ToString()
    {
        if (!IsCollection)
            return "Single document (not a collection)";
            
        var strategy = GetRecommendedStrategy();
        return $"Collection detected: {WorkCount} works, {Confidence:P0} confidence, " +
               $"strategy: {strategy}" +
               (IsShakespeare ? " [Shakespeare]" : "") +
               (HasTableOfContents ? " [Has TOC]" : "");
    }
}

/// <summary>
/// Information about a single work within a collection
/// </summary>
public class WorkInfo
{
    public string Title { get; init; } = "";
    public int HeadingLevel { get; init; }
    public int SentenceCount { get; init; }
    public bool IsShakespearePlay { get; init; }
    
    /// <summary>
    /// Whether this is a chapter heading (vs a distinct work)
    /// </summary>
    public bool IsChapter { get; init; }
    
    /// <summary>
    /// Work type inferred from title
    /// </summary>
    public WorkType InferredType => InferType();
    
    private WorkType InferType()
    {
        var lower = Title.ToLowerInvariant();
        
        if (lower.Contains("tragedy")) return WorkType.Tragedy;
        if (lower.Contains("comedy")) return WorkType.Comedy;
        if (lower.Contains("history")) return WorkType.History;
        if (lower.Contains("sonnet")) return WorkType.Poetry;
        if (lower.Contains("poem")) return WorkType.Poetry;
        
        return WorkType.Unknown;
    }
}

/// <summary>
/// Type of work (for Shakespeare and similar)
/// </summary>
public enum WorkType
{
    Unknown,
    Tragedy,
    Comedy,
    History,
    Poetry,
    Essay,
    Story
}

/// <summary>
/// Recommended summarization strategy for collections
/// </summary>
public enum CollectionStrategy
{
    /// <summary>
    /// Not a collection - use standard summarization
    /// </summary>
    SingleDocument,
    
    /// <summary>
    /// Summarize each work independently, then synthesize
    /// </summary>
    HierarchicalFull,
    
    /// <summary>
    /// Sample representative works from each category, summarize, synthesize
    /// </summary>
    HierarchicalSampled,
    
    /// <summary>
    /// Use table of contents to select key works, summarize those
    /// </summary>
    HierarchicalWithIndex
}
