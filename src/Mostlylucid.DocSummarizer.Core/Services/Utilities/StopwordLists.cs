using System.Reflection;
using StopWord;

namespace Mostlylucid.DocSummarizer.Services.Utilities;

/// <summary>
/// Shared stopword and word lists for entity extraction and text processing.
/// Uses dotnet-stop-words package for base stopwords, plus custom lists for:
/// - Honorifics (Mr., Dr., Captain, etc.)
/// - Place indicators (Street, Road, etc.)
/// - Code keywords (function, class, API, etc.)
/// - Project Gutenberg boilerplate
/// 
/// All lookups are O(1) using HashSets with case-insensitive comparison.
/// Lists are loaded once at startup from embedded resources.
/// </summary>
public static class StopwordLists
{
    private static readonly Lazy<HashSet<string>> _stopwords = new(LoadStopwords);
    private static readonly Lazy<HashSet<string>> _honorifics = new(() => LoadEmbeddedList("honorifics.txt"));
    private static readonly Lazy<HashSet<string>> _placeIndicators = new(() => LoadEmbeddedList("place-indicators.txt"));
    private static readonly Lazy<HashSet<string>> _codeKeywords = new(() => LoadEmbeddedList("code-keywords.txt"));
    private static readonly Lazy<HashSet<string>> _dayNames = new(() => LoadEmbeddedList("day-names.txt"));
    private static readonly Lazy<HashSet<string>> _monthNames = new(() => LoadEmbeddedList("month-names.txt"));

    /// <summary>
    /// Combined stopwords from dotnet-stop-words + custom additions
    /// </summary>
    public static HashSet<string> Stopwords => _stopwords.Value;

    /// <summary>
    /// Honorific titles that precede names (Mr., Mrs., Dr., Captain, etc.)
    /// </summary>
    public static HashSet<string> Honorifics => _honorifics.Value;

    /// <summary>
    /// Place indicator suffixes (Street, Road, Avenue, etc.)
    /// </summary>
    public static HashSet<string> PlaceIndicators => _placeIndicators.Value;

    /// <summary>
    /// Code/technical keywords that shouldn't be entities
    /// </summary>
    public static HashSet<string> CodeKeywords => _codeKeywords.Value;

    /// <summary>
    /// Day names (Monday-Sunday)
    /// </summary>
    public static HashSet<string> DayNames => _dayNames.Value;

    /// <summary>
    /// Month names (January-December)
    /// </summary>
    public static HashSet<string> MonthNames => _monthNames.Value;

    /// <summary>
    /// Check if word is a stopword (pronouns, determiners, conjunctions, etc.)
    /// </summary>
    public static bool IsStopword(string word) =>
        Stopwords.Contains(word.ToLowerInvariant());

    /// <summary>
    /// Check if word is an honorific title
    /// </summary>
    public static bool IsHonorific(string word) =>
        Honorifics.Contains(word.TrimEnd('.').ToLowerInvariant()) ||
        Honorifics.Contains(word.ToLowerInvariant());

    /// <summary>
    /// Check if word is a place indicator suffix
    /// </summary>
    public static bool IsPlaceIndicator(string word) =>
        PlaceIndicators.Contains(word.TrimEnd('.').ToLowerInvariant());

    /// <summary>
    /// Check if word is a code/technical keyword
    /// </summary>
    public static bool IsCodeKeyword(string word) =>
        CodeKeywords.Contains(word.ToUpperInvariant()) ||
        CodeKeywords.Contains(word.ToLowerInvariant());

    /// <summary>
    /// Check if word is a day name
    /// </summary>
    public static bool IsDayName(string word) =>
        DayNames.Contains(word.ToLowerInvariant());

    /// <summary>
    /// Check if word is a month name
    /// </summary>
    public static bool IsMonthName(string word) =>
        MonthNames.Contains(word.ToLowerInvariant());

    /// <summary>
    /// Check if word should be filtered from entity extraction
    /// (any category except honorific - honorifics precede names)
    /// </summary>
    public static bool ShouldFilter(string word)
    {
        var lower = word.ToLowerInvariant();
        return Stopwords.Contains(lower) ||
               CodeKeywords.Contains(lower) ||
               CodeKeywords.Contains(word.ToUpperInvariant()) ||
               DayNames.Contains(lower) ||
               MonthNames.Contains(lower);
    }

    /// <summary>
    /// Check if this is a valid potential name start (titlecase, not a stopword)
    /// </summary>
    public static bool IsValidNameStart(string word)
    {
        if (string.IsNullOrEmpty(word) || word.Length < 2) return false;
        if (!char.IsUpper(word[0])) return false;
        if (ShouldFilter(word)) return false;
        
        // Allow honorifics as name starters
        if (IsHonorific(word)) return true;
        
        // Must be titlecase (first upper, rest lower, allow apostrophes)
        return word.Skip(1).All(c => char.IsLower(c) || c == '\'' || c == '-');
    }

    private static HashSet<string> LoadStopwords()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Load from dotnet-stop-words package (27 languages available)
        try
        {
            var englishStopwords = StopWords.GetStopWords("en");
            foreach (var word in englishStopwords)
            {
                set.Add(word);
            }
        }
        catch
        {
            // Fallback if package fails - add essential stopwords
            AddEssentialStopwords(set);
        }

        // Add custom stopwords that the package might miss
        AddCustomStopwords(set);

        return set;
    }

    private static void AddEssentialStopwords(HashSet<string> set)
    {
        // Pronouns
        var pronouns = new[] { "i", "me", "my", "mine", "myself", "you", "your", "yours", 
            "yourself", "he", "him", "his", "himself", "she", "her", "hers", "herself",
            "it", "its", "itself", "we", "us", "our", "ours", "ourselves", "they", "them",
            "their", "theirs", "themselves", "who", "whom", "whose", "which", "what",
            "that", "this", "these", "those" };
        
        // Articles and determiners
        var determiners = new[] { "a", "an", "the", "some", "any", "no", "every", "each",
            "either", "neither", "all", "both", "half", "several", "many", "much", "few" };
        
        // Conjunctions and prepositions
        var conjunctions = new[] { "and", "but", "or", "nor", "so", "for", "yet", "because",
            "although", "while", "when", "where", "if", "unless", "until", "after", "before" };

        foreach (var w in pronouns.Concat(determiners).Concat(conjunctions))
            set.Add(w);
    }

    private static void AddCustomStopwords(HashSet<string> set)
    {
        // Sentence adverbs (often start sentences but aren't entities)
        var adverbs = new[] { "however", "therefore", "moreover", "furthermore", 
            "nevertheless", "nonetheless", "meanwhile", "otherwise", "instead", 
            "indeed", "thus", "hence", "accordingly", "yet", "still", "also" };

        // Common non-name capitalized words
        var nonNames = new[] { "chapter", "part", "book", "section", "volume", "page",
            "note", "notes", "introduction", "conclusion", "summary", "appendix",
            "here", "there", "now", "then", "today", "tomorrow", "yesterday" };

        // Project Gutenberg boilerplate
        var gutenberg = new[] { "project", "gutenberg", "foundation", "archive", 
            "literary", "license", "ebook", "copyright", "trademark", "donations" };

        // Generic geographic/building words
        var geographic = new[] { "island", "river", "lake", "mountain", "valley", 
            "forest", "garden", "park", "castle", "palace", "house", "hall", "tower" };
        
        // Modal verbs and common verbs at sentence start
        var verbs = new[] { "let", "could", "would", "should", "must", "shall", 
            "will", "may", "might", "can" };

        foreach (var w in adverbs.Concat(nonNames).Concat(gutenberg).Concat(geographic).Concat(verbs))
            set.Add(w);
    }

    private static HashSet<string> LoadEmbeddedList(string fileName)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"Mostlylucid.DocSummarizer.Resources.{fileName.Replace("-", "_")}";

        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                // Resource not found - return defaults
                return GetDefaultList(fileName);
            }

            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith('#'))
                {
                    set.Add(trimmed);
                }
            }
        }
        catch
        {
            return GetDefaultList(fileName);
        }

        return set.Count > 0 ? set : GetDefaultList(fileName);
    }

    private static HashSet<string> GetDefaultList(string fileName) => fileName switch
    {
        "honorifics.txt" => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mr", "mr.", "mrs", "mrs.", "ms", "ms.", "miss", "dr", "dr.",
            "prof", "prof.", "professor", "rev", "rev.", "reverend",
            "sir", "dame", "lord", "lady", "baron", "baroness",
            "captain", "capt", "capt.", "colonel", "col", "col.",
            "general", "gen", "gen.", "major", "maj", "maj.",
            "lieutenant", "lt", "lt.", "sergeant", "sgt", "sgt.",
            "admiral", "adm", "adm.", "commander", "cmdr", "cmdr.",
            "inspector", "detective", "officer", "constable"
        },
        "place-indicators.txt" => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "street", "st", "st.", "road", "rd", "rd.", "avenue", "ave", "ave.",
            "boulevard", "blvd", "blvd.", "drive", "dr", "lane", "ln", "ln.",
            "court", "ct", "ct.", "place", "pl", "pl.", "way", "circle", "cir",
            "terrace", "ter", "square", "sq", "alley", "aly", "wharf", "yard"
        },
        "code-keywords.txt" => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // C-style
            "class", "interface", "public", "private", "protected", "static",
            "void", "int", "string", "bool", "var", "const", "let", "function",
            "return", "if", "else", "for", "while", "switch", "case", "try",
            "catch", "throw", "new", "null", "true", "false",
            // Technical acronyms
            "api", "http", "https", "json", "xml", "html", "css", "sql",
            "rest", "get", "post", "put", "delete", "url", "uri",
            "cpu", "gpu", "ram", "rom", "sdk", "ide", "cli", "gui",
            // Assembly
            "mov", "call", "ret", "jmp", "push", "pop", "add", "sub"
        },
        "day-names.txt" => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
            "mon", "tue", "tues", "wed", "weds", "thu", "thur", "thurs", "fri", "sat", "sun"
        },
        "month-names.txt" => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "january", "february", "march", "april", "may", "june",
            "july", "august", "september", "october", "november", "december",
            "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec"
        },
        _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    };

}
