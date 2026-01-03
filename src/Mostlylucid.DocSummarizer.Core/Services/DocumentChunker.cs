using System.Text;
using System.Text.RegularExpressions;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Services;

public class DocumentChunker
{
    // Rough estimate: 1 token ≈ 4 characters for English text
    private const int CharsPerToken = 4;
    private readonly int _maxHeadingLevel;
    private readonly int _minChunkTokens;
    private readonly int _targetChunkTokens;

    /// <summary>
    ///     Creates a new document chunker.
    /// </summary>
    /// <param name="maxHeadingLevel">Maximum heading level to split on (1-6). Default is 2 (H1 and H2 only).</param>
    /// <param name="targetChunkTokens">
    ///     Target chunk size in tokens. Default is 4000 (~16KB).
    ///     Chunks smaller than this will be merged with adjacent sections.
    /// </param>
    /// <param name="minChunkTokens">Minimum chunk size before merging. Default is 500 (~2KB).</param>
    public DocumentChunker(int maxHeadingLevel = 2, int targetChunkTokens = 4000, int minChunkTokens = 500)
    {
        _maxHeadingLevel = Math.Clamp(maxHeadingLevel, 1, 6);
        _targetChunkTokens = targetChunkTokens;
        _minChunkTokens = minChunkTokens;
    }

    // Regex to extract page markers: <!-- PAGE:1-5 --> or <!-- PAGE:1 -->
    private static readonly Regex PageMarkerRegex = new(@"<!--\s*PAGE:(\d+)(?:-(\d+))?\s*-->", RegexOptions.Compiled);
    
    public List<DocumentChunk> ChunkByStructure(string markdown)
    {
        // Extract page markers before processing
        var pageMap = ExtractPageMarkers(markdown);
        
        // Determine if document has markdown headings (ignore markers for detection)
        var hasHeadings = HasMarkdownHeadings(PageMarkerRegex.Replace(markdown, ""));
 
        // First pass: split by structure (headings) or paragraphs for plain text
        var rawSections = hasHeadings
            ? SplitByHeadings(markdown)
            : SplitByParagraphs(markdown);
 
        // Second pass: merge small sections to approach target size
        var mergedSections = MergeSections(rawSections);


        // Convert to chunks with page info
        var chunks = new List<DocumentChunk>();
        var index = 0;
        var totalSections = mergedSections.Count;

        foreach (var section in mergedSections)
        {
            if (string.IsNullOrWhiteSpace(section.Content)) continue;
            
            // Try to find page info for this section
            var (pageStart, pageEnd) = GetPageInfoForSection(section, pageMap, index, totalSections);
            
            chunks.Add(new DocumentChunk(
                index++,
                section.Heading,
                section.Level,
                section.Content,
                HashHelper.ComputeHash(section.Content),
                pageStart,
                pageEnd));
        }

        return chunks;
    }
    
    /// <summary>
    /// Extract page markers from markdown into a position-to-page map
    /// </summary>
    private static Dictionary<int, (int start, int end)> ExtractPageMarkers(string markdown)
    {
        var map = new Dictionary<int, (int start, int end)>();
        var matches = PageMarkerRegex.Matches(markdown);
        
        foreach (Match match in matches)
        {
            var position = match.Index;
            var startPage = int.Parse(match.Groups[1].Value);
            var endPage = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : startPage;
            map[position] = (startPage, endPage);
        }
        
        return map;
    }
    
    /// <summary>
    /// Get page info for a section - uses section's page info if available, 
    /// falls back to pageMap estimation, or returns null for estimation by caller
    /// </summary>
    private static (int? pageStart, int? pageEnd) GetPageInfoForSection(
        RawSection section, 
        Dictionary<int, (int start, int end)> pageMap,
        int sectionIndex,
        int totalSections)
    {
        // If section already has page info (from marker parsing), use it
        if (section.PageStart.HasValue)
            return (section.PageStart, section.PageEnd ?? section.PageStart);
        
        // If we have page markers, estimate based on section index distribution
        if (pageMap.Count > 0)
        {
            var sortedMarkers = pageMap.OrderBy(kv => kv.Key).ToList();
            var totalPages = sortedMarkers.Max(m => m.Value.end);
            
            // Estimate page based on section's position in document
            var estimatedPage = totalSections > 0
                ? (int)Math.Ceiling((double)(sectionIndex + 1) / totalSections * totalPages)
                : 1;
            
            return (Math.Max(1, estimatedPage), Math.Max(1, estimatedPage));
        }
        
        // No page markers - return null (caller will estimate or use section number)
        return (null, null);
    }

    /// <summary>
    ///     Check if document contains markdown headings
    /// </summary>
    private bool HasMarkdownHeadings(string text)
    {
        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            var level = GetHeadingLevel(line);
            if (level > 0 && level <= _maxHeadingLevel)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Split plain text by paragraphs (double newlines)
    /// </summary>
    private List<RawSection> SplitByParagraphs(string text)
    {
        var sections = new List<RawSection>();

        // Split on double newlines (blank lines) - handles \n\n and \r\n\r\n
        var paragraphs = Regex
            .Split(text, @"\r?\n\s*\r?\n")
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        if (paragraphs.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(text))
                sections.Add(new RawSection("Document", 1, text.Trim()));
            return sections;
        }

        int? currentPageStart = null;
        int? currentPageEnd = null;
        var paragraphIndex = 0;

        foreach (var rawPara in paragraphs)
        {
            var para = rawPara;
            var match = PageMarkerRegex.Match(para);
            if (match.Success)
            {
                currentPageStart = int.Parse(match.Groups[1].Value);
                currentPageEnd = match.Groups[2].Success
                    ? int.Parse(match.Groups[2].Value)
                    : currentPageStart;

                para = PageMarkerRegex.Replace(para, "").Trim();
                if (string.IsNullOrWhiteSpace(para))
                    continue;
            }

            paragraphIndex++;
            var heading = $"Paragraph {paragraphIndex}";

            var firstSentenceEnd = para.IndexOfAny(['.', '!', '?']);
            if (firstSentenceEnd > 0 && firstSentenceEnd < 100)
            {
                heading = para[..(firstSentenceEnd + 1)];
            }
            else if (para.Length < 100)
            {
                heading = para;
            }
            else
            {
                var cutoff = para.LastIndexOf(' ', Math.Min(80, para.Length - 1));
                if (cutoff < 20) cutoff = 80;
                heading = para[..Math.Min(cutoff, para.Length)] + "...";
            }

            sections.Add(new RawSection(heading, 1, para, currentPageStart, currentPageEnd));
        }

        return sections;
    }

    private List<RawSection> SplitByHeadings(string markdown)
    {
        var sections = new List<RawSection>();
        var lines = markdown.Split('\n');

        var content = new StringBuilder();
        string? heading = null;
        var level = 0;
        int? currentPageStart = null;
        int? currentPageEnd = null;

        foreach (var line in lines)
        {
            // Check for page marker
            var pageMatch = PageMarkerRegex.Match(line);
            if (pageMatch.Success)
            {
                currentPageStart = int.Parse(pageMatch.Groups[1].Value);
                currentPageEnd = pageMatch.Groups[2].Success 
                    ? int.Parse(pageMatch.Groups[2].Value) 
                    : currentPageStart;
                continue; // Don't include marker in content
            }
            
            var headingLevel = GetHeadingLevel(line);

            // Only split on headings up to the configured max level
            if (headingLevel > 0 && headingLevel <= _maxHeadingLevel)
            {
                // Flush previous section with current page info
                if (content.Length > 0 || heading != null)
                {
                    sections.Add(new RawSection(heading ?? "", level, content.ToString().Trim(), currentPageStart, currentPageEnd));
                    content.Clear();
                }

                heading = line.TrimStart('#', ' ');
                level = headingLevel;
            }
            else
            {
                content.AppendLine(line);
            }
        }

        // Flush final section
        if (content.Length > 0 || heading != null)
            sections.Add(new RawSection(heading ?? "", level, content.ToString().Trim(), currentPageStart, currentPageEnd));

        return sections;
    }

    private List<RawSection> MergeSections(List<RawSection> sections)
    {
        if (sections.Count <= 1)
            return sections;

        var merged = new List<RawSection>();
        var currentHeading = "";
        var currentLevel = 0;
        var currentContent = new StringBuilder();
        var currentTokens = 0;
        int? currentPageStart = null;
        int? currentPageEnd = null;

        void FlushCurrent()
        {
            if (currentContent.Length == 0) return;
            merged.Add(new RawSection(
                currentHeading,
                currentLevel,
                currentContent.ToString().Trim(),
                currentPageStart,
                currentPageEnd));
            currentContent.Clear();
            currentTokens = 0;
            currentHeading = "";
            currentLevel = 0;
            currentPageStart = null;
            currentPageEnd = null;
        }

        foreach (var section in sections)
        {
            var sectionTokens = EstimateTokens(section.Content);
            var sectionWithHeading = string.IsNullOrEmpty(section.Heading)
                ? section.Content
                : $"## {section.Heading}\n\n{section.Content}";
            var fullSectionTokens = EstimateTokens(sectionWithHeading);

            // If adding this section would exceed target, flush current and start new
            if (currentContent.Length > 0 && currentTokens + fullSectionTokens > _targetChunkTokens)
            {
                if (currentTokens >= _minChunkTokens)
                {
                    FlushCurrent();

                    currentHeading = section.Heading;
                    currentLevel = section.Level;
                    currentContent.AppendLine(section.Content);
                    currentTokens = sectionTokens;
                    currentPageStart = section.PageStart;
                    currentPageEnd = section.PageEnd ?? section.PageStart;
                }
                else
                {
                    if (currentContent.Length > 0) currentContent.AppendLine();
                    if (!string.IsNullOrEmpty(section.Heading))
                    {
                        currentContent.AppendLine($"## {section.Heading}");
                        currentContent.AppendLine();
                    }

                    currentContent.AppendLine(section.Content);
                    currentTokens += fullSectionTokens;

                    if (section.PageStart.HasValue)
                    {
                        currentPageStart = currentPageStart.HasValue
                            ? Math.Min(currentPageStart.Value, section.PageStart.Value)
                            : section.PageStart;
                        var sectionEnd = (section.PageEnd ?? section.PageStart)!.Value;
                        currentPageEnd = currentPageEnd.HasValue
                            ? Math.Max(currentPageEnd.Value, sectionEnd)
                            : sectionEnd;
                    }
                }
            }
            else
            {
                if (currentContent.Length == 0)
                {
                    currentHeading = section.Heading;
                    currentLevel = section.Level;
                    currentContent.AppendLine(section.Content);
                    currentTokens = sectionTokens;
                    currentPageStart = section.PageStart;
                    currentPageEnd = section.PageEnd ?? section.PageStart;
                }
                else
                {
                    if (currentContent.Length > 0) currentContent.AppendLine();
                    if (!string.IsNullOrEmpty(section.Heading))
                    {
                        currentContent.AppendLine($"## {section.Heading}");
                        currentContent.AppendLine();
                    }

                    currentContent.AppendLine(section.Content);
                    currentTokens += fullSectionTokens;

                    if (section.PageStart.HasValue)
                    {
                        currentPageStart = currentPageStart.HasValue
                            ? Math.Min(currentPageStart.Value, section.PageStart.Value)
                            : section.PageStart;
                        var sectionEnd = (section.PageEnd ?? section.PageStart)!.Value;
                        currentPageEnd = currentPageEnd.HasValue
                            ? Math.Max(currentPageEnd.Value, sectionEnd)
                            : sectionEnd;
                    }
                }
            }
        }

        FlushCurrent();
        return merged;
    }

    private int EstimateTokens(string text)

    {
        if (string.IsNullOrEmpty(text)) return 0;
        return text.Length / CharsPerToken;
    }

    private static int GetHeadingLevel(string line)
    {
        if (string.IsNullOrEmpty(line) || !line.StartsWith('#'))
            return 0;

        var level = 0;
        foreach (var c in line)
            if (c == '#') level++;
            else break;

        // Must have space after # marks to be a valid heading
        return line.Length > level && line[level] == ' ' ? level : 0;
    }

    private record RawSection(string Heading, int Level, string Content, int? PageStart = null, int? PageEnd = null);
}