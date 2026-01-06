using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Services.Ocr;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Analyzes OCR output to detect document structure (headings, paragraphs, lists).
/// Uses font size (via bounding box height), spacing, and text patterns.
///
/// Approach based on research:
/// - Font size detection via bounding box height (larger = heading)
/// - Line spacing analysis (extra space = section break)
/// - Indentation detection (for lists and quotes)
/// - Case analysis (UPPERCASE/Title Case = likely heading)
/// - Position heuristics (top/centered = title)
/// </summary>
public class DocumentStructureAnalyzer
{
    private readonly ILogger<DocumentStructureAnalyzer>? _logger;

    public DocumentStructureAnalyzer(ILogger<DocumentStructureAnalyzer>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyze OCR regions and detect document structure.
    /// </summary>
    public DocumentStructure Analyze(List<OcrTextRegion> regions, int imageWidth, int imageHeight)
    {
        if (regions.Count == 0)
        {
            return new DocumentStructure();
        }

        // Step 1: Group words into lines (by Y coordinate clustering)
        var lines = GroupWordsIntoLines(regions);

        // Step 2: Calculate font size statistics
        var fontStats = CalculateFontStatistics(lines);

        // Step 3: Classify each line
        var elements = ClassifyLines(lines, fontStats, imageWidth, imageHeight);

        // Step 4: Post-process to detect section boundaries
        PostProcessElements(elements);

        return new DocumentStructure
        {
            Elements = elements,
            FontStatistics = fontStats,
            DetectedHeadingCount = elements.Count(e => e.Type is StructureType.Title or StructureType.Heading),
            DetectedParagraphCount = elements.Count(e => e.Type == StructureType.Paragraph),
            DetectedListItemCount = elements.Count(e => e.Type == StructureType.ListItem)
        };
    }

    /// <summary>
    /// Group words into lines based on Y coordinate proximity.
    /// </summary>
    private List<TextLine> GroupWordsIntoLines(List<OcrTextRegion> regions)
    {
        // Sort by Y, then X
        var sorted = regions.OrderBy(r => r.BoundingBox.Y1).ThenBy(r => r.BoundingBox.X1).ToList();

        var lines = new List<TextLine>();
        TextLine? currentLine = null;

        foreach (var region in sorted)
        {
            if (currentLine == null)
            {
                currentLine = new TextLine { Words = new List<OcrTextRegion> { region } };
                continue;
            }

            // Check if this word is on the same line (Y overlap)
            var lastWord = currentLine.Words[^1];
            var yOverlap = CalculateYOverlap(lastWord.BoundingBox, region.BoundingBox);

            // If significant Y overlap, same line
            if (yOverlap > 0.5)
            {
                currentLine.Words.Add(region);
            }
            else
            {
                // New line
                lines.Add(currentLine);
                currentLine = new TextLine { Words = new List<OcrTextRegion> { region } };
            }
        }

        if (currentLine != null && currentLine.Words.Count > 0)
        {
            lines.Add(currentLine);
        }

        // Calculate line properties
        foreach (var line in lines)
        {
            line.Text = string.Join(" ", line.Words.Select(w => w.Text));
            line.BoundingBox = new BoundingBox
            {
                X1 = line.Words.Min(w => w.BoundingBox.X1),
                Y1 = line.Words.Min(w => w.BoundingBox.Y1),
                X2 = line.Words.Max(w => w.BoundingBox.X2),
                Y2 = line.Words.Max(w => w.BoundingBox.Y2),
                Width = line.Words.Max(w => w.BoundingBox.X2) - line.Words.Min(w => w.BoundingBox.X1),
                Height = line.Words.Max(w => w.BoundingBox.Y2) - line.Words.Min(w => w.BoundingBox.Y1)
            };
            line.AverageFontHeight = line.Words.Average(w => w.BoundingBox.Height);
            line.Confidence = line.Words.Average(w => w.Confidence);
        }

        return lines;
    }

    /// <summary>
    /// Calculate Y overlap ratio between two bounding boxes.
    /// </summary>
    private double CalculateYOverlap(BoundingBox a, BoundingBox b)
    {
        var overlapTop = Math.Max(a.Y1, b.Y1);
        var overlapBottom = Math.Min(a.Y2, b.Y2);
        var overlap = Math.Max(0, overlapBottom - overlapTop);

        var minHeight = Math.Min(a.Height, b.Height);
        return minHeight > 0 ? (double)overlap / minHeight : 0;
    }

    /// <summary>
    /// Calculate font size statistics from all lines.
    /// </summary>
    private FontStatistics CalculateFontStatistics(List<TextLine> lines)
    {
        if (lines.Count == 0)
        {
            return new FontStatistics();
        }

        var heights = lines.Select(l => l.AverageFontHeight).OrderBy(h => h).ToList();

        // Calculate median and percentiles
        var median = heights[heights.Count / 2];
        var p75 = heights[(int)(heights.Count * 0.75)];
        var p90 = heights[(int)(heights.Count * 0.90)];
        var max = heights.Max();
        var min = heights.Min();

        // Body text is typically around the median
        var bodyTextHeight = median;

        // Headings are typically 1.2x+ body text
        var headingThreshold = bodyTextHeight * 1.2;
        var titleThreshold = bodyTextHeight * 1.5;

        return new FontStatistics
        {
            MedianHeight = median,
            MinHeight = min,
            MaxHeight = max,
            P75Height = p75,
            P90Height = p90,
            EstimatedBodyTextHeight = bodyTextHeight,
            HeadingThreshold = headingThreshold,
            TitleThreshold = titleThreshold
        };
    }

    /// <summary>
    /// Classify each line as heading, paragraph, list item, etc.
    /// </summary>
    private List<StructureElement> ClassifyLines(
        List<TextLine> lines,
        FontStatistics fontStats,
        int imageWidth,
        int imageHeight)
    {
        var elements = new List<StructureElement>();
        var averageIndent = lines.Where(l => l.BoundingBox.X1 > 0).Average(l => l.BoundingBox.X1);

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var prevLine = i > 0 ? lines[i - 1] : null;
            var nextLine = i < lines.Count - 1 ? lines[i + 1] : null;

            var element = new StructureElement
            {
                Text = line.Text,
                BoundingBox = line.BoundingBox,
                LineIndex = i,
                FontHeight = line.AverageFontHeight,
                Confidence = line.Confidence
            };

            // Classify based on multiple signals
            element.Type = ClassifyLineType(line, prevLine, nextLine, fontStats, imageWidth, imageHeight, averageIndent);

            // Estimate markdown heading level (1-6)
            if (element.Type == StructureType.Title)
            {
                element.HeadingLevel = 1;
            }
            else if (element.Type == StructureType.Heading)
            {
                // Scale based on font size relative to body
                var sizeRatio = line.AverageFontHeight / fontStats.EstimatedBodyTextHeight;
                element.HeadingLevel = sizeRatio switch
                {
                    >= 1.8 => 1,
                    >= 1.5 => 2,
                    >= 1.3 => 3,
                    >= 1.2 => 4,
                    _ => 5
                };
            }

            elements.Add(element);
        }

        return elements;
    }

    /// <summary>
    /// Classify a single line based on multiple signals.
    /// </summary>
    private StructureType ClassifyLineType(
        TextLine line,
        TextLine? prevLine,
        TextLine? nextLine,
        FontStatistics fontStats,
        int imageWidth,
        int imageHeight,
        double averageIndent)
    {
        var text = line.Text.Trim();

        // Empty or very short lines
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2)
        {
            return StructureType.Unknown;
        }

        // Check for list item patterns
        if (IsListItem(text))
        {
            return StructureType.ListItem;
        }

        // Check for page header/footer (top/bottom 10% of page)
        if (line.BoundingBox.Y1 < imageHeight * 0.08)
        {
            return StructureType.PageHeader;
        }
        if (line.BoundingBox.Y2 > imageHeight * 0.92)
        {
            return StructureType.PageFooter;
        }

        // Check font size signals
        var isTitleSize = line.AverageFontHeight >= fontStats.TitleThreshold;
        var isHeadingSize = line.AverageFontHeight >= fontStats.HeadingThreshold;

        // Check case signals
        var isUpperCase = text == text.ToUpperInvariant() && text.Any(char.IsLetter);
        var isTitleCase = IsTitleCase(text);

        // Check position signals (centered = more likely heading)
        var leftMargin = line.BoundingBox.X1;
        var rightMargin = imageWidth - line.BoundingBox.X2;
        var isCentered = Math.Abs(leftMargin - rightMargin) < imageWidth * 0.15;

        // Check spacing signals (extra space before = section break)
        var hasExtraSpaceBefore = prevLine != null &&
            (line.BoundingBox.Y1 - prevLine.BoundingBox.Y2) > fontStats.EstimatedBodyTextHeight * 1.5;

        // Check if short (headings are typically shorter than paragraphs)
        var isShort = text.Length < 80 && !text.EndsWith(".");

        // Score-based classification
        var headingScore = 0;

        if (isTitleSize) headingScore += 3;
        else if (isHeadingSize) headingScore += 2;

        if (isUpperCase) headingScore += 2;
        if (isTitleCase && isShort) headingScore += 1;
        if (isCentered && isShort) headingScore += 1;
        if (hasExtraSpaceBefore) headingScore += 1;
        if (isShort && !text.Contains('.')) headingScore += 1;

        // Title detection (first heading on page, large, prominent)
        if (line.LineIndex == 0 && headingScore >= 3)
        {
            return StructureType.Title;
        }

        // Heading detection
        if (headingScore >= 3)
        {
            return StructureType.Heading;
        }

        // Caption detection (small text near images, short, italic/different style)
        if (line.AverageFontHeight < fontStats.EstimatedBodyTextHeight * 0.9 && isShort)
        {
            return StructureType.Caption;
        }

        // Default to paragraph
        return StructureType.Paragraph;
    }

    /// <summary>
    /// Check if text is a list item (bullet, number, etc.)
    /// </summary>
    private bool IsListItem(string text)
    {
        var trimmed = text.TrimStart();

        // Bullet patterns
        var bulletPatterns = new[]
        {
            "• ", "- ", "* ", "→ ", "► ", "○ ", "● ", "◦ ",
            "✓ ", "✔ ", "☑ ", "□ ", "■ "
        };

        if (bulletPatterns.Any(p => trimmed.StartsWith(p)))
        {
            return true;
        }

        // Numbered list patterns (1. , 1) , (1) , i. , a. , etc.)
        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^(\d+[.)]\s)|^(\(\d+\)\s)|^([a-z][.)]\s)|^([ivx]+[.)]\s)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Check if text is in Title Case.
    /// </summary>
    private bool IsTitleCase(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;

        // Title case: most words start with uppercase
        var titleCaseWords = words.Count(w => w.Length > 0 && char.IsUpper(w[0]));
        return titleCaseWords >= words.Length * 0.7;
    }

    /// <summary>
    /// Post-process elements to refine classifications.
    /// </summary>
    private void PostProcessElements(List<StructureElement> elements)
    {
        // Detect consecutive paragraphs that should be merged
        for (int i = 0; i < elements.Count - 1; i++)
        {
            var current = elements[i];
            var next = elements[i + 1];

            // If current paragraph ends mid-sentence and next starts lowercase, mark for merge
            if (current.Type == StructureType.Paragraph &&
                next.Type == StructureType.Paragraph &&
                !current.Text.EndsWith('.') &&
                next.Text.Length > 0 && char.IsLower(next.Text[0]))
            {
                current.ShouldMergeWithNext = true;
            }
        }

        // Assign section IDs based on headings
        var sectionId = 0;
        foreach (var element in elements)
        {
            if (element.Type is StructureType.Title or StructureType.Heading)
            {
                sectionId++;
            }
            element.SectionId = sectionId;
        }
    }

    /// <summary>
    /// Convert detected structure to Markdown.
    /// </summary>
    public string ToMarkdown(DocumentStructure structure)
    {
        var sb = new System.Text.StringBuilder();

        foreach (var element in structure.Elements)
        {
            switch (element.Type)
            {
                case StructureType.Title:
                    sb.AppendLine($"# {element.Text}");
                    sb.AppendLine();
                    break;

                case StructureType.Heading:
                    var prefix = new string('#', element.HeadingLevel ?? 2);
                    sb.AppendLine($"{prefix} {element.Text}");
                    sb.AppendLine();
                    break;

                case StructureType.Paragraph:
                    sb.AppendLine(element.Text);
                    if (!element.ShouldMergeWithNext)
                    {
                        sb.AppendLine();
                    }
                    break;

                case StructureType.ListItem:
                    sb.AppendLine($"- {element.Text.TrimStart('-', '*', '•', ' ')}");
                    break;

                case StructureType.Caption:
                    sb.AppendLine($"*{element.Text}*");
                    sb.AppendLine();
                    break;

                case StructureType.PageHeader:
                case StructureType.PageFooter:
                    // Skip page headers/footers in markdown
                    break;

                default:
                    sb.AppendLine(element.Text);
                    break;
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Represents a line of text (grouped words).
/// </summary>
public class TextLine
{
    public List<OcrTextRegion> Words { get; set; } = new();
    public string Text { get; set; } = string.Empty;
    public BoundingBox BoundingBox { get; set; } = new();
    public double AverageFontHeight { get; set; }
    public double Confidence { get; set; }
    public int LineIndex { get; set; }
}

/// <summary>
/// Font size statistics for the document.
/// </summary>
public class FontStatistics
{
    public double MedianHeight { get; set; }
    public double MinHeight { get; set; }
    public double MaxHeight { get; set; }
    public double P75Height { get; set; }
    public double P90Height { get; set; }
    public double EstimatedBodyTextHeight { get; set; }
    public double HeadingThreshold { get; set; }
    public double TitleThreshold { get; set; }
}

/// <summary>
/// Detected document structure.
/// </summary>
public class DocumentStructure
{
    public List<StructureElement> Elements { get; set; } = new();
    public FontStatistics FontStatistics { get; set; } = new();
    public int DetectedHeadingCount { get; set; }
    public int DetectedParagraphCount { get; set; }
    public int DetectedListItemCount { get; set; }
}

/// <summary>
/// A detected structure element.
/// </summary>
public class StructureElement
{
    public string Text { get; set; } = string.Empty;
    public StructureType Type { get; set; }
    public int? HeadingLevel { get; set; }
    public BoundingBox BoundingBox { get; set; } = new();
    public int LineIndex { get; set; }
    public int SectionId { get; set; }
    public double FontHeight { get; set; }
    public double Confidence { get; set; }
    public bool ShouldMergeWithNext { get; set; }
}

/// <summary>
/// Types of document structure elements.
/// Based on DocLayNet labels.
/// </summary>
public enum StructureType
{
    Unknown,
    Title,          // Overall document title (H1)
    Heading,        // Section heading (H2-H6)
    Paragraph,      // Regular text paragraph
    ListItem,       // Bullet or numbered list item
    Caption,        // Image/table caption
    PageHeader,     // Repeating page header
    PageFooter,     // Repeating page footer
    Table,          // Table (detected separately)
    Figure,         // Figure/image placeholder
    Formula,        // Math formula
    Code,           // Code block
    Footnote,       // Footnote text
    Quote           // Block quote
}
