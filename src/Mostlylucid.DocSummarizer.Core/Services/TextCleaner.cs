using System.Text;
using System.Text.RegularExpressions;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Text cleaning utilities inspired by Unstructured.io and Trafilatura.
/// Provides functions to clean and normalize text extracted from documents.
/// </summary>
public static class TextCleaner
{
    #region Ligatures
    
    /// <summary>
    /// Common ligatures found in PDFs and their replacements.
    /// Borrowed from Unstructured.io's clean_ligatures function.
    /// </summary>
    private static readonly Dictionary<char, string> LigaturesMap = new()
    {
        { 'æ', "ae" },
        { 'Æ', "AE" },
        { '\ufb00', "ff" },  // ﬀ
        { '\ufb01', "fi" },  // ﬁ
        { '\ufb02', "fl" },  // ﬂ
        { '\ufb03', "ffi" }, // ﬃ
        { '\ufb04', "ffl" }, // ﬄ
        { '\ufb05', "ft" },  // ﬅ (long s + t)
        { '\u02AA', "ls" },  // ʪ
        { 'œ', "oe" },
        { 'Œ', "OE" },
        { '\u0239', "qp" },  // ȹ
        { '\ufb06', "st" },  // ﬆ
        { '\u02A6', "ts" },  // ʦ
    };
    
    /// <summary>
    /// Replaces ligatures with their equivalent characters.
    /// Example: "The beneﬁts" -> "The benefits"
    /// </summary>
    public static string CleanLigatures(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (LigaturesMap.TryGetValue(c, out var replacement))
                sb.Append(replacement);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }
    
    #endregion
    
    #region Unicode Quotes
    
    /// <summary>
    /// Replaces unicode quote characters with standard equivalents.
    /// Borrowed from Unstructured.io's replace_unicode_quotes function.
    /// Example: smart quotes and malformed UTF-8 get normalized.
    /// </summary>
    public static string ReplaceUnicodeQuotes(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        
        // Single quotes
        text = text.Replace("\x91", "'");
        text = text.Replace("\x92", "'");
        text = text.Replace("\u2018", "'"); // Left single quote
        text = text.Replace("\u2019", "'"); // Right single quote
        text = text.Replace("&apos;", "'");
        
        // Double quotes
        text = text.Replace("\x93", "\"");
        text = text.Replace("\x94", "\"");
        text = text.Replace("\u201C", "\""); // Left double quote
        text = text.Replace("\u201D", "\""); // Right double quote
        
        // Dashes
        text = text.Replace("\u2013", "-"); // En dash
        text = text.Replace("\u2014", "-"); // Em dash
        
        // Ellipsis
        text = text.Replace("\u2026", "...");
        
        // Common malformed UTF-8 sequences (from PDFs)
        // These occur when UTF-8 is misinterpreted as Latin-1
        text = text.Replace("\u00e2\u0080\u0099", "'");  // Right single quote
        text = text.Replace("\u00e2\u0080\u0098", "'");  // Left single quote
        text = text.Replace("\u00e2\u0080\u009c", "\""); // Left double quote
        text = text.Replace("\u00e2\u0080\u009d", "\""); // Right double quote
        text = text.Replace("\u00e2\u0080\u0094", "-");  // Em dash
        text = text.Replace("\u00e2\u0080\u0093", "-");  // En dash
        
        return text;
    }
    
    #endregion
    
    #region Bullets
    
    /// <summary>
    /// Regex pattern for common bullet characters at line start.
    /// Using explicit pattern instead of character class to avoid escaping issues.
    /// </summary>
    private static readonly Regex UnicodeBulletsRegex = new(
        @"^[\u0095\u2022\u2023\u2043\u204C\u204D\u2219\u25CB\u25CF\u25D8\u25E6\u2619\u2765\u2767\u29BE\u29BF\u00B7\-\*]+\s*",
        RegexOptions.Compiled);
    
    /// <summary>
    /// Removes bullets from the beginning of text.
    /// Example: "● An excellent point!" -> "An excellent point!"
    /// </summary>
    public static string CleanBullets(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return UnicodeBulletsRegex.Replace(text, "").Trim();
    }
    
    /// <summary>
    /// Removes ordered bullets like "1.1" or "a.b" from the beginning of text.
    /// Example: "1.1 This is important" -> "This is important"
    /// </summary>
    public static string CleanOrderedBullets(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return text;
        
        var firstPart = parts[0];
        // Check if first part looks like a bullet (e.g., "1.", "1.1", "a.", "a.b")
        if (firstPart.Contains('.') && !firstPart.Contains(".."))
        {
            var bulletParts = firstPart.Split('.');
            // If it's a short bullet prefix (1-2 chars per segment)
            if (bulletParts.All(p => p.Length <= 2))
            {
                return parts[1];
            }
        }
        
        return text;
    }
    
    #endregion
    
    #region Whitespace and Formatting
    
    private static readonly Regex NonBreakingSpaceRegex = new(
        @"\xa0+", RegexOptions.Compiled);
    
    private static readonly Regex MultipleSpacesRegex = new(
        @"[ ]{2,}", RegexOptions.Compiled);
    
    private static readonly Regex MultipleNewlinesRegex = new(
        @"\n{3,}", RegexOptions.Compiled);
    
    /// <summary>
    /// Cleans extra whitespace characters that appear between words.
    /// Preserves newlines for markdown structure, but normalizes non-breaking spaces.
    /// Example: "ITEM 1.     BUSINESS" -> "ITEM 1. BUSINESS"
    /// </summary>
    public static string CleanExtraWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        
        // Replace non-breaking spaces with regular spaces
        text = NonBreakingSpaceRegex.Replace(text, " ");
        // Collapse multiple spaces
        text = MultipleSpacesRegex.Replace(text, " ");
        // Collapse excessive newlines (but keep double newlines for paragraphs)
        text = MultipleNewlinesRegex.Replace(text, "\n\n");
        return text.Trim();
    }
    
    private static readonly Regex DashRegex = new(
        @"[-\u2013]", RegexOptions.Compiled);
    
    /// <summary>
    /// Cleans dash characters in text.
    /// Example: "RISK-FACTORS" -> "RISK FACTORS"
    /// </summary>
    public static string CleanDashes(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return DashRegex.Replace(text, " ").Trim();
    }
    
    /// <summary>
    /// Clean all trailing punctuation in text.
    /// Example: "ITEM 1. BUSINESS." -> "ITEM 1. BUSINESS"
    /// </summary>
    public static string CleanTrailingPunctuation(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.TrimEnd('.', ',', ':', ';');
    }
    
    #endregion
    
    #region Prefix/Postfix Patterns
    
    /// <summary>
    /// Removes prefixes from a string according to the specified pattern.
    /// Example: "SUMMARY: This is important" with pattern "SUMMARY:" -> "This is important"
    /// </summary>
    public static string CleanPrefix(string text, string pattern, bool ignoreCase = false)
    {
        if (string.IsNullOrEmpty(text)) return text;
        
        var options = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        return Regex.Replace(text, $"^{pattern}", "", options).TrimStart();
    }
    
    /// <summary>
    /// Removes postfixes from a string according to the specified pattern.
    /// Example: "The end! END" with pattern "(END|STOP)" -> "The end!"
    /// </summary>
    public static string CleanPostfix(string text, string pattern, bool ignoreCase = false)
    {
        if (string.IsNullOrEmpty(text)) return text;
        
        var options = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        return Regex.Replace(text, $"{pattern}$", "", options).TrimEnd();
    }
    
    #endregion
    
    #region Paragraph Grouping
    
    private static readonly Regex ParagraphSplitRegex = new(
        @"\s*\n\s*", RegexOptions.Compiled);
    
    private static readonly Regex DoubleParagraphRegex = new(
        @"(\s*\n\s*){2,}", RegexOptions.Compiled);
    
    /// <summary>
    /// Groups together paragraphs that are broken up with line breaks for visual purposes.
    /// Common in .txt files.
    /// 
    /// Example:
    /// "The big brown fox\nwas walking down the lane.\n\nAt the end of the lane..."
    /// becomes
    /// "The big brown fox was walking down the lane.\n\nAt the end of the lane..."
    /// </summary>
    public static string GroupBrokenParagraphs(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        
        var paragraphs = DoubleParagraphRegex.Split(text);
        var cleanParagraphs = new List<string>();
        
        foreach (var paragraph in paragraphs)
        {
            var stripped = paragraph.Trim();
            if (string.IsNullOrEmpty(stripped)) continue;
            
            // Check if paragraph starts with a bullet
            if (UnicodeBulletsRegex.IsMatch(stripped))
            {
                cleanParagraphs.Add(stripped);
                continue;
            }
            
            // Check if all lines are short (likely a list or formatted section)
            var lines = ParagraphSplitRegex.Split(paragraph);
            var allLinesShort = lines.All(line => 
                line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 5);
            
            if (allLinesShort)
            {
                // Keep lines separate
                cleanParagraphs.AddRange(lines.Where(l => !string.IsNullOrWhiteSpace(l)));
            }
            else
            {
                // Join lines into single paragraph
                var joined = ParagraphSplitRegex.Replace(paragraph, " ").Trim();
                cleanParagraphs.Add(joined);
            }
        }
        
        return string.Join("\n\n", cleanParagraphs);
    }
    
    #endregion
    
    #region Non-ASCII Cleaning
    
    /// <summary>
    /// Removes non-ASCII characters from text.
    /// Example: "\x88This text contains ®non-ascii!" -> "This text contains non-ascii!"
    /// </summary>
    public static string CleanNonAscii(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (c < 128) // ASCII range
                sb.Append(c);
        }
        return sb.ToString();
    }
    
    /// <summary>
    /// Removes control characters but keeps standard whitespace.
    /// </summary>
    public static string CleanControlCharacters(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')
                continue;
            sb.Append(c);
        }
        return sb.ToString();
    }
    
    #endregion
    
    #region Citation Removal
    
    private static readonly Regex CitationRegex = new(
        @"\[\d{1,3}\]", RegexOptions.Compiled);
    
    private static readonly Regex RomanCitationRegex = new(
        @"\[[ivxlcdm]+\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    /// <summary>
    /// Removes citations like [1], [2], [i], [ii] from text.
    /// Example: "This is important [1] for the study [2]." -> "This is important for the study."
    /// </summary>
    public static string RemoveCitations(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        
        text = CitationRegex.Replace(text, "");
        text = RomanCitationRegex.Replace(text, "");
        return CleanExtraWhitespace(text);
    }
    
    #endregion
    
    #region Combined Cleaning
    
    /// <summary>
    /// Options for the combined Clean function.
    /// </summary>
    public class CleanOptions
    {
        public bool Ligatures { get; set; } = true;
        public bool UnicodeQuotes { get; set; } = true;
        public bool Bullets { get; set; } = false;
        public bool ExtraWhitespace { get; set; } = true;
        public bool Dashes { get; set; } = false;
        public bool TrailingPunctuation { get; set; } = false;
        public bool ControlCharacters { get; set; } = true;
        public bool Lowercase { get; set; } = false;
    }
    
    /// <summary>
    /// Combined cleaning function with configurable options.
    /// Inspired by Unstructured.io's clean() function.
    /// </summary>
    public static string Clean(string text, CleanOptions? options = null)
    {
        if (string.IsNullOrEmpty(text)) return text;
        
        options ??= new CleanOptions();
        
        if (options.ControlCharacters)
            text = CleanControlCharacters(text);
        
        if (options.Ligatures)
            text = CleanLigatures(text);
        
        if (options.UnicodeQuotes)
            text = ReplaceUnicodeQuotes(text);
        
        if (options.Bullets)
            text = CleanBullets(text);
        
        if (options.Dashes)
            text = CleanDashes(text);
        
        if (options.ExtraWhitespace)
            text = CleanExtraWhitespace(text);
        
        if (options.TrailingPunctuation)
            text = CleanTrailingPunctuation(text);
        
        if (options.Lowercase)
            text = text.ToLowerInvariant();
        
        return text.Trim();
    }
    
    /// <summary>
    /// Apply default cleaning suitable for document summarization.
    /// - Cleans ligatures (ﬁ -> fi)
    /// - Cleans unicode quotes
    /// - Cleans control characters
    /// - Cleans extra whitespace
    /// </summary>
    public static string CleanForSummarization(string text)
    {
        return Clean(text, new CleanOptions
        {
            Ligatures = true,
            UnicodeQuotes = true,
            ControlCharacters = true,
            ExtraWhitespace = true,
            Bullets = false,        // Keep bullets for structure
            Dashes = false,         // Keep dashes in compound words
            TrailingPunctuation = false,
            Lowercase = false
        });
    }
    
    #endregion
}
