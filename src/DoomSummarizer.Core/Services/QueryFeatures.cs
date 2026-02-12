using System.Text.RegularExpressions;

namespace DoomSummarizer.Services;

/// <summary>
///     Extracts structural features from query text for short-query classification enrichment.
///     Features run in parallel with embedding computation (under 0.02ms) and provide
///     discriminative signals that embeddings miss on short queries (4 words or fewer).
///     All patterns are pre-compiled via source generators for zero allocation at runtime.
/// </summary>
internal static partial class QueryFeatures
{
    // ── Synonym expansion for abbreviations in short queries ──
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AI"] = "artificial intelligence",
        ["ML"] = "machine learning",
        ["LLM"] = "large language model",
        ["LLMs"] = "large language models",
        ["NLP"] = "natural language processing",
        ["CV"] = "computer vision",
        ["infosec"] = "information security cybersecurity",
        ["DevOps"] = "development operations",
        ["k8s"] = "Kubernetes",
        ["JS"] = "JavaScript",
        ["TS"] = "TypeScript",
        ["CS"] = "computer science",
        ["HPC"] = "high performance computing",
        ["IoT"] = "internet of things",
        ["VR"] = "virtual reality",
        ["AR"] = "augmented reality",
        ["API"] = "application programming interface",
        ["DB"] = "database",
        ["UI"] = "user interface",
        ["UX"] = "user experience",
        ["SaaS"] = "software as a service",
        ["OSS"] = "open source software",
        ["EV"] = "electric vehicle",
        ["EVs"] = "electric vehicles",
        ["GPU"] = "graphics processing unit",
        ["CPU"] = "central processing unit",
    };

    /// <summary>
    ///     Extract structural features from a query string. O(1) — pre-compiled regex matches.
    /// </summary>
    public static QueryFeatureSet Extract(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new QueryFeatureSet
        {
            WordCount = words.Length,
            HasQuestionWord = QuestionWordPattern().IsMatch(query),
            HasQuestionMark = query.Contains('?'),
            HasComparisonMarker = ComparisonPattern().IsMatch(query),
            HasHowtoMarker = HowtoPattern().IsMatch(query),
            HasSearchOnlyMarker = SearchOnlyPattern().IsMatch(query),
            HasQaMarker = QaPattern().IsMatch(query),
            HasCompositeConjunction = CompositeConjunctionPattern().IsMatch(query),
            HasImperativeVerb = ImperativeVerbPattern().IsMatch(query),
        };
    }

    /// <summary>
    ///     Expand common abbreviations in short queries to produce richer embeddings.
    ///     Only applies to queries with ≤ maxWords words (longer queries have enough context).
    ///     Returns the original query unchanged if no expansions apply.
    /// </summary>
    public static string ExpandSynonyms(string query, int maxWords = 5)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > maxWords) return query;

        var expanded = false;
        for (var i = 0; i < words.Length; i++)
        {
            // Strip trailing punctuation for lookup, preserve it in output
            var clean = words[i].TrimEnd('?', '.', ',', '!', ';', ':');
            if (Synonyms.TryGetValue(clean, out var synonym))
            {
                var suffix = words[i][clean.Length..];
                words[i] = synonym + suffix;
                expanded = true;
            }
        }

        return expanded ? string.Join(' ', words) : query;
    }

    // ── Pre-compiled regex patterns (source-generated) ──

    /// <summary>Detects question words at the start of the query.</summary>
    [GeneratedRegex(@"^(how|what|why|when|who|where|which|can\s+i|should\s+i|is\s+there|are\s+there|do\s+i)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex QuestionWordPattern();

    /// <summary>Detects comparison intent markers.</summary>
    [GeneratedRegex(@"\b(compare|vs\.?|versus|difference\s+between|better\s+than|or\s+\w+\s+better|pros\s+and\s+cons)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ComparisonPattern();

    /// <summary>Detects how-to/tutorial intent markers.</summary>
    [GeneratedRegex(
        @"\b(how\s+(do|can|to|should)|set\s+up|configure|install|guide|tutorial|step\s+by\s+step|walkthrough|getting\s+started|troubleshoot)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex HowtoPattern();

    /// <summary>Detects direct-answer/search-only patterns (factual lookups, conversions, definitions).</summary>
    [GeneratedRegex(
        @"\b(convert|define|meaning\s+of|definition\s+of|population\s+of|capital\s+of|what\s+time|weather\s+in|how\s+(many|much|long|far|tall|old|big)|translate|score\s+of|price\s+of|cost\s+of|height\s+of|distance\s+(between|from|to))\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex SearchOnlyPattern();

    /// <summary>Detects QA intent markers (direct questions seeking factual answers).</summary>
    [GeneratedRegex(@"^(what\s+is|what\s+are|who\s+is|who\s+are|where\s+is|when\s+(did|was|is|will))\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex QaPattern();

    /// <summary>Detects composite query conjunctions and list-like patterns (multi-part queries).</summary>
    [GeneratedRegex(
        @"\b(and\s+also|and\s+(compare|summarize|check|find|show|tell)|also\s+(check|show|get|find|look)|plus\s+\w+|as\s+well\s+as|in\s+addition\s+to|along\s+with)\b|;\s*\w|(?<=\w)\s*[/+]\s*(?=\w{3,})",
        RegexOptions.IgnoreCase)]
    private static partial Regex CompositeConjunctionPattern();

    /// <summary>Detects imperative verbs suggesting action/fetch intent.</summary>
    [GeneratedRegex(@"^(show|get|find|tell|give|list|summarize|fetch|grab|pull|check)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ImperativeVerbPattern();
}

/// <summary>
///     Extracted query features — structural signals for classification enrichment.
///     All fields are deterministic and computed from query text alone (no embedding).
/// </summary>
internal record QueryFeatureSet
{
    /// <summary>Number of whitespace-delimited words in the query.</summary>
    public int WordCount { get; init; }

    /// <summary>Query starts with a question word (how, what, why, when, who, where, which).</summary>
    public bool HasQuestionWord { get; init; }

    /// <summary>Query contains a question mark.</summary>
    public bool HasQuestionMark { get; init; }

    /// <summary>Query contains comparison intent markers (compare, vs, difference between).</summary>
    public bool HasComparisonMarker { get; init; }

    /// <summary>Query contains how-to intent markers (how do I, set up, configure, tutorial).</summary>
    public bool HasHowtoMarker { get; init; }

    /// <summary>Query matches direct-answer/search-only patterns (convert, define, population of).</summary>
    public bool HasSearchOnlyMarker { get; init; }

    /// <summary>Query contains QA intent markers (what is, who is).</summary>
    public bool HasQaMarker { get; init; }

    /// <summary>Query contains composite conjunctions (and also, as well as, in addition to).</summary>
    public bool HasCompositeConjunction { get; init; }

    /// <summary>Query starts with an imperative verb (show, get, find, tell).</summary>
    public bool HasImperativeVerb { get; init; }

    /// <summary>Whether the query is considered "short" based on the configured threshold.</summary>
    public bool IsShortQuery(int maxWords = 4) => WordCount <= maxWords;
}
