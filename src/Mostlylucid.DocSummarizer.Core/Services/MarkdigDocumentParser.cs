using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Markdig-based document parser that properly parses markdown AST
/// for better sentence extraction, structure analysis, and content understanding.
/// </summary>
public class MarkdigDocumentParser
{
    private readonly MarkdownPipeline _pipeline;
    
    // Sentence boundary regex - handles abbreviations, numbers, quotes
    private static readonly Regex SentenceBoundary = new(
        @"(?<=[.!?])\s+(?=[A-Z""'])|(?<=[.!?][""'])\s+(?=[A-Z])",
        RegexOptions.Compiled);
    
    // Common abbreviations that shouldn't end sentences
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "vs", "etc", "ie", "eg",
        "inc", "ltd", "co", "corp", "st", "ave", "blvd", "apt", "no", "vol",
        "fig", "pp", "cf", "al", "et"
    };

    public MarkdigDocumentParser()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();
    }

    /// <summary>
    /// Parse markdown into a structured document with sections, paragraphs, and sentences
    /// </summary>
    public ParsedDocument Parse(string markdown)
    {
        var document = Markdown.Parse(markdown, _pipeline);
        var sections = new List<ParsedSection>();
        var currentSection = new ParsedSection("Introduction", 0);
        var allSentences = new List<SentenceInfo>();
        var sentenceIndex = 0;

        foreach (var block in document)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    // Flush current section if it has content
                    if (currentSection.Paragraphs.Count > 0 || currentSection.Sentences.Count > 0)
                    {
                        sections.Add(currentSection);
                    }
                    
                    var headingText = GetInlineText(heading.Inline);
                    currentSection = new ParsedSection(headingText, heading.Level);
                    break;
                    
                case ParagraphBlock paragraph:
                    var paragraphText = GetInlineText(paragraph.Inline);
                    if (!string.IsNullOrWhiteSpace(paragraphText))
                    {
                        currentSection.Paragraphs.Add(paragraphText);
                        
                        // Extract sentences from paragraph
                        var sentences = ExtractSentences(paragraphText);
                        foreach (var sentence in sentences)
                        {
                            var info = new SentenceInfo(
                                sentenceIndex++,
                                sentence,
                                currentSection.Heading,
                                currentSection.Level,
                                sections.Count);
                            currentSection.Sentences.Add(info);
                            allSentences.Add(info);
                        }
                    }
                    break;
                    
                case FencedCodeBlock codeBlock:
                    var code = codeBlock.Lines.ToString();
                    var language = codeBlock.Info ?? "code";
                    currentSection.CodeBlocks.Add(new CodeBlockInfo(language, code));
                    break;
                    
                case ListBlock listBlock:
                    var items = ExtractListItems(listBlock);
                    currentSection.ListItems.AddRange(items);
                    
                    // Also extract sentences from list items
                    foreach (var item in items)
                    {
                        var sentences = ExtractSentences(item);
                        foreach (var sentence in sentences)
                        {
                            var info = new SentenceInfo(
                                sentenceIndex++,
                                sentence,
                                currentSection.Heading,
                                currentSection.Level,
                                sections.Count);
                            currentSection.Sentences.Add(info);
                            allSentences.Add(info);
                        }
                    }
                    break;
                    
                case QuoteBlock quote:
                    var quoteText = ExtractQuoteText(quote);
                    if (!string.IsNullOrWhiteSpace(quoteText))
                    {
                        currentSection.Quotes.Add(quoteText);
                        
                        // Extract sentences from quotes
                        var sentences = ExtractSentences(quoteText);
                        foreach (var sentence in sentences)
                        {
                            var info = new SentenceInfo(
                                sentenceIndex++,
                                sentence,
                                currentSection.Heading,
                                currentSection.Level,
                                sections.Count);
                            currentSection.Sentences.Add(info);
                            allSentences.Add(info);
                        }
                    }
                    break;
            }
        }
        
        // Add final section
        if (currentSection.Paragraphs.Count > 0 || currentSection.Sentences.Count > 0)
        {
            sections.Add(currentSection);
        }

        return new ParsedDocument(sections, allSentences);
    }

    /// <summary>
    /// Extract plain text from inline elements (handles emphasis, links, code, etc.)
    /// </summary>
    private static string GetInlineText(ContainerInline? inline)
    {
        if (inline == null) return "";
        
        var sb = new StringBuilder();
        foreach (var child in inline)
        {
            switch (child)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;
                case EmphasisInline emphasis:
                    sb.Append(GetInlineText(emphasis));
                    break;
                case LinkInline link:
                    // Get link text, not URL
                    sb.Append(GetInlineText(link));
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case LineBreakInline:
                    sb.Append(' ');
                    break;
                case ContainerInline container:
                    sb.Append(GetInlineText(container));
                    break;
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Extract text from list items recursively
    /// </summary>
    private static List<string> ExtractListItems(ListBlock list)
    {
        var items = new List<string>();
        foreach (var item in list)
        {
            if (item is ListItemBlock listItem)
            {
                foreach (var block in listItem)
                {
                    if (block is ParagraphBlock para)
                    {
                        var text = GetInlineText(para.Inline);
                        if (!string.IsNullOrWhiteSpace(text))
                            items.Add(text);
                    }
                    else if (block is ListBlock nested)
                    {
                        items.AddRange(ExtractListItems(nested));
                    }
                }
            }
        }
        return items;
    }

    /// <summary>
    /// Extract text from quote blocks
    /// </summary>
    private static string ExtractQuoteText(QuoteBlock quote)
    {
        var sb = new StringBuilder();
        foreach (var block in quote)
        {
            if (block is ParagraphBlock para)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(GetInlineText(para.Inline));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Extract sentences from a paragraph of text.
    /// Handles abbreviations, quotes, and other edge cases.
    /// </summary>
    public static List<string> ExtractSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        var sentences = new List<string>();
        
        // Normalize whitespace
        text = Regex.Replace(text, @"\s+", " ").Trim();
        
        // Simple but effective sentence splitting
        var parts = SentenceBoundary.Split(text);
        var currentSentence = new StringBuilder();
        
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;
                
            // Check if this might be a continuation after an abbreviation
            if (currentSentence.Length > 0)
            {
                var lastWord = GetLastWord(currentSentence.ToString());
                if (Abbreviations.Contains(lastWord.TrimEnd('.')))
                {
                    // It's an abbreviation, continue the sentence
                    currentSentence.Append(' ');
                    currentSentence.Append(trimmed);
                    continue;
                }
                
                // Previous sentence is complete
                var sentence = currentSentence.ToString().Trim();
                if (sentence.Length >= 10) // Minimum sentence length
                    sentences.Add(sentence);
                currentSentence.Clear();
            }
            
            currentSentence.Append(trimmed);
        }
        
        // Add final sentence
        var finalSentence = currentSentence.ToString().Trim();
        if (finalSentence.Length >= 10)
            sentences.Add(finalSentence);
        
        // If no sentences were extracted, treat the whole text as one sentence
        if (sentences.Count == 0 && text.Length >= 10)
            sentences.Add(text);

        return sentences;
    }

    private static string GetLastWord(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 ? words[^1] : "";
    }
}

/// <summary>
/// A fully parsed document with sections and sentences
/// </summary>
public record ParsedDocument(
    List<ParsedSection> Sections,
    List<SentenceInfo> AllSentences)
{
    /// <summary>
    /// Total sentence count
    /// </summary>
    public int SentenceCount => AllSentences.Count;
    
    /// <summary>
    /// Get sentences with position weights applied
    /// </summary>
    public List<SentenceInfo> GetWeightedSentences(ContentType contentType)
    {
        var totalSentences = AllSentences.Count;
        if (totalSentences == 0) return AllSentences;
        
        var introThreshold = PositionWeights.GetIntroThreshold(contentType);
        var conclusionThreshold = PositionWeights.GetConclusionThreshold(contentType);
        
        foreach (var sentence in AllSentences)
        {
            var position = (double)sentence.Index / totalSentences;
            
            if (position < introThreshold)
                sentence.PositionWeight = PositionWeights.GetWeight(ChunkPosition.Introduction, contentType);
            else if (position >= conclusionThreshold)
                sentence.PositionWeight = PositionWeights.GetWeight(ChunkPosition.Conclusion, contentType);
            else
                sentence.PositionWeight = PositionWeights.GetWeight(ChunkPosition.Body, contentType);
        }
        
        return AllSentences;
    }
    
    /// <summary>
    /// Get document purpose from introduction (first few sentences)
    /// </summary>
    public string GetIntroductionContext(int maxSentences = 3)
    {
        return string.Join(" ", AllSentences.Take(maxSentences).Select(s => s.Text));
    }
}

/// <summary>
/// A parsed section of a document
/// </summary>
public record ParsedSection(string Heading, int Level)
{
    public List<string> Paragraphs { get; } = new();
    public List<SentenceInfo> Sentences { get; } = new();
    public List<CodeBlockInfo> CodeBlocks { get; } = new();
    public List<string> ListItems { get; } = new();
    public List<string> Quotes { get; } = new();
    
    /// <summary>
    /// Get all text content from this section
    /// </summary>
    public string GetFullText() => string.Join("\n\n", Paragraphs);
}

/// <summary>
/// Information about a single sentence for BERT extraction
/// </summary>
public class SentenceInfo
{
    public int Index { get; }
    public string Text { get; }
    public string SectionHeading { get; }
    public int SectionLevel { get; }
    public int SectionIndex { get; }
    
    /// <summary>
    /// Position weight (set by GetWeightedSentences)
    /// </summary>
    public double PositionWeight { get; set; } = 1.0;
    
    /// <summary>
    /// Embedding for this sentence (set by BertSummarizer)
    /// </summary>
    public float[]? Embedding { get; set; }
    
    /// <summary>
    /// Final score for extraction ranking
    /// </summary>
    public double Score { get; set; }

    public SentenceInfo(int index, string text, string sectionHeading, int sectionLevel, int sectionIndex)
    {
        Index = index;
        Text = text;
        SectionHeading = sectionHeading;
        SectionLevel = sectionLevel;
        SectionIndex = sectionIndex;
    }
    
    /// <summary>
    /// Get citation reference for this sentence
    /// </summary>
    public string Citation => $"[s{Index + 1}]";
}

/// <summary>
/// Code block information
/// </summary>
public record CodeBlockInfo(string Language, string Code);
