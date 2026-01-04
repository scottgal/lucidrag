namespace Mostlylucid.DocSummarizer.Images.Services.Ocr.PostProcessing;

/// <summary>
/// Common OCR error patterns and substitution rules.
/// These patterns capture frequent misrecognitions by Tesseract and similar OCR engines.
///
/// Categories:
/// - Letter/Number confusion (O→0, l→1, S→5, etc.)
/// - Letter combinations (rn→m, vv→w, cl→d, etc.)
/// - Punctuation errors (' → ', . → ,, etc.)
/// - Case errors (uppercase I → lowercase l, etc.)
/// </summary>
public static class OcrErrorPatterns
{
    /// <summary>
    /// Character-level substitution rules: incorrect → correct
    /// Applied when the incorrect character is surrounded by letters (word context).
    /// </summary>
    public static readonly Dictionary<char, char> LetterToNumber = new()
    {
        ['O'] = '0', // Letter O → Number 0
        ['l'] = '1', // Lowercase L → Number 1
        ['I'] = '1', // Uppercase i → Number 1
        ['S'] = '5', // Letter S → Number 5
        ['B'] = '8', // Letter B → Number 8
        ['Z'] = '2', // Letter Z → Number 2
    };

    /// <summary>
    /// Character-level substitution rules: incorrect → correct
    /// Applied when the incorrect character is surrounded by digits (numeric context).
    /// </summary>
    public static readonly Dictionary<char, char> NumberToLetter = new()
    {
        ['0'] = 'O', // Number 0 → Letter O
        ['1'] = 'l', // Number 1 → Lowercase L
        ['5'] = 'S', // Number 5 → Letter S
        ['8'] = 'B', // Number 8 → Letter B
        ['2'] = 'Z', // Number 2 → Letter Z
    };

    /// <summary>
    /// Multi-character substitution patterns: incorrect → correct
    /// Common OCR misrecognitions of character combinations.
    /// </summary>
    public static readonly Dictionary<string, string> CharacterCombinations = new()
    {
        // Letter combinations
        ["rn"] = "m",     // rn → m (common in serif fonts)
        ["vv"] = "w",     // vv → w
        ["VV"] = "W",     // VV → W
        ["cl"] = "d",     // cl → d
        ["ii"] = "ü",     // ii → ü (German umlaut)
        ["fi"] = "fi",    // Ligature preservation
        ["fl"] = "fl",    // Ligature preservation

        // Number/letter combinations
        ["l0"] = "10",    // l0 → 10
        ["O0"] = "00",    // O0 → 00
        ["0O"] = "00",    // 0O → 00

        // Punctuation
        [" ,"] = ",",     // Space before comma
        [" ."] = ".",     // Space before period
        [",,"] = "\"",    // Double comma → quote
        ["` "] = "'",     // Backtick → apostrophe
        [" '"] = "'",     // Space before apostrophe (likely part of word)
    };

    /// <summary>
    /// Case-sensitive substitutions for specific contexts.
    /// </summary>
    public static readonly Dictionary<string, string> CaseSensitive = new()
    {
        // Uppercase I confused with lowercase l at start of words
        ["I "] = "I ",     // Keep uppercase I as sentence start
        [" I "] = " I ",   // Keep uppercase I as pronoun
        [" l "] = " I ",   // Standalone lowercase l → uppercase I (pronoun)

        // Lowercase L at start of words
        ["l'"] = "I'",     // l'm → I'm
        ["lm"] = "Im",     // lm → Im
        ["lf"] = "If",     // lf → If
        ["ln"] = "In",     // ln → In
    };

    /// <summary>
    /// Word-level corrections: common OCR mistakes in whole words.
    /// </summary>
    public static readonly Dictionary<string, string> CommonWords = new()
    {
        // Pronouns
        ["l"] = "I",
        ["i"] = "I",

        // Articles
        ["tne"] = "the",
        ["tn"] = "in",
        ["oI"] = "of",
        ["ancl"] = "and",

        // Common verbs
        ["wlll"] = "will",
        ["can't"] = "can't",  // Fix apostrophe
        ["dont"] = "don't",
        ["doesnt"] = "doesn't",
        ["wont"] = "won't",
        ["cant"] = "can't",

        // Numbers as words
        ["flve"] = "five",
        ["nlne"] = "nine",
    };

    /// <summary>
    /// Check if a character is a letter (a-z, A-Z).
    /// </summary>
    public static bool IsLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    /// <summary>
    /// Check if a character is a digit (0-9).
    /// </summary>
    public static bool IsDigit(char c) => c >= '0' && c <= '9';

    /// <summary>
    /// Get context around a character position (previous and next character).
    /// </summary>
    public static (char? Prev, char? Next) GetContext(string text, int index)
    {
        char? prev = index > 0 ? text[index - 1] : null;
        char? next = index < text.Length - 1 ? text[index + 1] : null;
        return (prev, next);
    }

    /// <summary>
    /// Determine if context is alphabetic (surrounded by letters).
    /// </summary>
    public static bool IsAlphaContext(char? prev, char? next)
    {
        return (prev == null || IsLetter(prev.Value)) && (next == null || IsLetter(next.Value));
    }

    /// <summary>
    /// Determine if context is numeric (surrounded by digits).
    /// </summary>
    public static bool IsNumericContext(char? prev, char? next)
    {
        return (prev == null || IsDigit(prev.Value)) && (next == null || IsDigit(next.Value));
    }
}
