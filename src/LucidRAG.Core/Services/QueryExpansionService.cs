using System.Collections.Concurrent;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Utilities;

namespace LucidRAG.Services;

/// <summary>
/// ML-powered query expansion service.
/// Uses embedding similarity to find synonyms and related terms.
///
/// "golden" → ["golden", "yellow", "gold", "amber", "warm"]
/// "fast" → ["fast", "quick", "rapid", "speedy", "swift"]
///
/// This enables semantic signal matching without embedding every signal.
/// BM25 on expanded terms gives semantic-ish matching at BM25 speed.
/// </summary>
public interface IQueryExpansionService
{
    /// <summary>
    /// Expand a query term to include synonyms and related terms.
    /// </summary>
    Task<IReadOnlyList<string>> ExpandTermAsync(string term, int maxExpansions = 5, double minSimilarity = 0.6, CancellationToken ct = default);

    /// <summary>
    /// Expand all terms in a query.
    /// </summary>
    Task<ExpandedQuery> ExpandQueryAsync(string query, int maxExpansionsPerTerm = 3, CancellationToken ct = default);

    /// <summary>
    /// Pre-warm the expansion cache with common signal terms.
    /// </summary>
    Task WarmCacheAsync(IEnumerable<string> terms, CancellationToken ct = default);
}

/// <summary>
/// Expanded query with original and expanded terms.
/// </summary>
public record ExpandedQuery(
    string OriginalQuery,
    IReadOnlyList<string> OriginalTerms,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Expansions,
    string ExpandedQueryText);

/// <summary>
/// Embedding-based query expansion using semantic similarity.
/// </summary>
public class EmbeddingQueryExpansionService : IQueryExpansionService
{
    private readonly IEmbeddingService _embedder;
    private readonly ILogger<EmbeddingQueryExpansionService> _logger;

    // Pre-computed signal vocabulary with embeddings
    private readonly ConcurrentDictionary<string, float[]> _vocabularyEmbeddings = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _expansionCache = new();

    // Real signal terms from actual analysis services in the codebase
    // Source: ColorAnalyzer.cs, EntityTypeProfiles.cs, prompt-templates.json, SIGNALS.md
    // NOTE: Multi-word terms use hyphens for embedding, but we also add single-word versions
    private static readonly Dictionary<string, string[]> SignalVocabulary = new()
    {
        // Colors from ColorAnalyzer.NamedColors (deduplicated, single-word preferred)
        ["colors"] = [
            // Grayscale
            "black", "white", "gray", "grey", "silver", "charcoal", "slate",
            // Reds
            "red", "crimson", "scarlet", "ruby", "maroon", "burgundy",
            "pink", "rose", "salmon", "coral", "peach",
            // Oranges
            "orange", "tangerine", "amber",
            // Yellows
            "yellow", "gold", "golden", "lemon", "cream", "khaki", "beige", "ivory", "tan",
            // Greens
            "green", "lime", "emerald", "jade", "olive", "mint", "seafoam", "sage", "chartreuse",
            // Cyans/Teals
            "cyan", "aqua", "teal", "turquoise", "aquamarine",
            // Blues
            "blue", "navy", "cobalt", "sapphire", "indigo", "periwinkle", "azure",
            // Purples
            "purple", "violet", "magenta", "fuchsia", "plum", "orchid", "amethyst",
            "lavender", "lilac", "mauve",
            // Browns
            "brown", "chocolate", "sienna", "coffee", "mocha",
            // Tints
            "sepia", "warm", "cool", "tinted", "pastel", "vivid", "muted", "bright", "dark", "light"
        ],

        // Document/entity types from EntityTypeProfiles.cs
        ["entities"] = [
            // Profile types
            "technical", "legal", "business", "academic", "scientific",
            // Entity categories
            "technology", "framework", "library", "api", "database", "service",
            "contract", "clause", "obligation", "jurisdiction",
            "company", "organization", "product", "metric", "department", "project",
            "person", "location", "event", "category"
        ],

        // Image types from prompt-templates.json
        ["image_types"] = [
            "photo", "photograph", "screenshot", "diagram", "chart", "artwork",
            "meme", "scanned", "icon", "logo", "illustration", "graphic"
        ],

        // Motion/animation signals from SIGNALS.md
        ["motion"] = [
            "animated", "static", "moving", "still", "motion", "movement",
            "oscillating", "stationary", "rotating", "spinning", "panning"
        ],

        // Scene/setting types
        ["scenes"] = [
            "landscape", "portrait", "indoor", "outdoor", "nature", "urban", "rural",
            "beach", "mountain", "forest", "desert", "ocean", "sky", "sunset", "sunrise",
            "night", "day", "cloudy", "sunny", "city", "architecture"
        ],

        // Subject matter (deduplicated from entities)
        ["subjects"] = [
            "people", "crowd", "face", "animal", "pet", "dog", "cat", "bird", "wildlife",
            "food", "vehicle", "car", "abstract", "pattern", "texture", "object", "building"
        ],

        // Document tones/styles
        ["tones"] = [
            "formal", "informal", "casual", "professional", "conversational",
            "serious", "neutral", "objective", "instructional", "descriptive", "analytical"
        ],

        // Sizes/dimensions
        ["sizes"] = [
            "large", "small", "tiny", "huge", "massive", "medium", "big", "little",
            "short", "long", "tall", "wide", "narrow", "thick", "thin"
        ],

        // Temporal descriptors
        ["temporal"] = [
            "recent", "old", "new", "ancient", "modern", "contemporary", "vintage", "retro", "classic",
            "fresh", "current", "outdated", "latest"
        ],

        // Quality descriptors
        ["quality"] = [
            "high", "low", "good", "bad", "excellent", "poor", "sharp", "blurry",
            "clear", "grainy", "noisy", "clean", "crisp"
        ]
    };

    public EmbeddingQueryExpansionService(
        IEmbeddingService embedder,
        ILogger<EmbeddingQueryExpansionService> logger)
    {
        _embedder = embedder;
        _logger = logger;
    }

    /// <summary>
    /// Initialize vocabulary embeddings on first use.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_vocabularyEmbeddings.Count > 0) return;

        _logger.LogInformation("Initializing query expansion vocabulary...");

        var allTerms = SignalVocabulary.Values.SelectMany(v => v).Distinct().ToList();

        foreach (var term in allTerms)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var embedding = await _embedder.EmbedAsync(term, ct);
                if (embedding != null)
                {
                    _vocabularyEmbeddings[term.ToLowerInvariant()] = embedding;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to embed term: {Term}", term);
            }
        }

        _logger.LogInformation("Initialized {Count} vocabulary embeddings", _vocabularyEmbeddings.Count);
    }

    public async Task<IReadOnlyList<string>> ExpandTermAsync(
        string term,
        int maxExpansions = 5,
        double minSimilarity = 0.6,
        CancellationToken ct = default)
    {
        var normalizedTerm = term.ToLowerInvariant().Trim();

        // Check cache
        if (_expansionCache.TryGetValue(normalizedTerm, out var cached))
            return cached;

        // Ensure vocabulary is initialized
        if (_vocabularyEmbeddings.Count == 0)
            await InitializeAsync(ct);

        // Get embedding for query term
        var termEmbedding = await _embedder.EmbedAsync(normalizedTerm, ct);
        if (termEmbedding == null)
            return [normalizedTerm];

        // Find similar terms from vocabulary
        var similarities = new List<(string Term, double Similarity)>();

        foreach (var (vocabTerm, vocabEmbedding) in _vocabularyEmbeddings)
        {
            var similarity = CosineSimilarity(termEmbedding, vocabEmbedding);
            if (similarity >= minSimilarity && vocabTerm != normalizedTerm)
            {
                similarities.Add((vocabTerm, similarity));
            }
        }

        // Take top expansions
        var expansions = similarities
            .OrderByDescending(x => x.Similarity)
            .Take(maxExpansions)
            .Select(x => x.Term)
            .Prepend(normalizedTerm) // Always include original
            .ToList();

        // Cache result
        _expansionCache[normalizedTerm] = expansions;

        _logger.LogDebug("Expanded '{Term}' to: [{Expansions}]", term, string.Join(", ", expansions));

        return expansions;
    }

    public async Task<ExpandedQuery> ExpandQueryAsync(
        string query,
        int maxExpansionsPerTerm = 3,
        CancellationToken ct = default)
    {
        // Tokenize query
        var terms = TokenizeQuery(query);
        var expansions = new Dictionary<string, IReadOnlyList<string>>();

        foreach (var term in terms)
        {
            if (ct.IsCancellationRequested) break;

            // Skip very short terms and stopwords (use shared stopword list)
            if (term.Length < 3 || StopwordLists.IsStopword(term))
            {
                expansions[term] = [term];
                continue;
            }

            var expanded = await ExpandTermAsync(term, maxExpansionsPerTerm, 0.65, ct);
            expansions[term] = expanded;
        }

        // Build expanded query text
        // Original terms + all expansions for BM25
        var allTerms = expansions.Values
            .SelectMany(e => e)
            .Distinct()
            .ToList();

        var expandedText = string.Join(" ", allTerms);

        return new ExpandedQuery(
            OriginalQuery: query,
            OriginalTerms: terms,
            Expansions: expansions,
            ExpandedQueryText: expandedText);
    }

    public async Task WarmCacheAsync(IEnumerable<string> terms, CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        foreach (var term in terms)
        {
            if (ct.IsCancellationRequested) break;
            await ExpandTermAsync(term, ct: ct);
        }
    }

    private static string[] TokenizeQuery(string query)
    {
        return query
            .ToLowerInvariant()
            .Split([' ', ',', '.', '!', '?', ';', ':', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 0)
            .ToArray();
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        double dotProduct = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator == 0 ? 0 : dotProduct / denominator;
    }
}
