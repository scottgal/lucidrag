using System.Text.RegularExpressions;
using LucidRAG.Data;
using LucidRAG.Entities;

namespace LucidRAG.Services.Sentinel;

/// <summary>
///     Routes queries to collections using salient term matching.
///     Scoring algorithm:
///     1. Extract query keywords (significant terms, no stopwords)
///     2. Match against each collection's salient terms (pre-computed TF-IDF + entity terms)
///     3. Score = sum of (term_score * match_quality) / sqrt(collection_term_count)
///     4. Normalize across collections and apply confidence threshold
///
///     Designed to be reusable across LucidRAG, lucidSUPPORT, and other consumers.
/// </summary>
public partial class CollectionRouter(
    RagDocumentsDbContext db,
    ILogger<CollectionRouter> logger) : ICollectionRouter
{
    /// <summary>
    ///     Minimum confidence to consider a collection a match.
    ///     Below this, we search all collections.
    /// </summary>
    private const double DefaultConfidenceThreshold = 0.3;

    /// <summary>
    ///     Maximum number of salient terms to load per collection for matching.
    /// </summary>
    private const int MaxTermsPerCollection = 200;

    /// <summary>
    ///     Minimum gap between top-1 and top-2 scores to auto-route.
    ///     If two collections score similarly, search both rather than picking one.
    /// </summary>
    private const double MinScoreGap = 0.1;

    public async Task<CollectionRoutingResult> RouteAsync(
        string query,
        QueryPlan? queryPlan = null,
        CancellationToken ct = default)
    {
        // Get all collections with document counts
        var collections = await db.Collections
            .Select(c => new { c.Id, c.Name, c.Description, DocCount = c.Documents.Count })
            .ToListAsync(ct);

        if (collections.Count == 0)
            return new CollectionRoutingResult { Explanation = "No collections available" };

        // Single collection? No routing needed — use it directly
        if (collections.Count == 1)
        {
            var only = collections[0];
            return new CollectionRoutingResult
            {
                Candidates =
                [
                    new CollectionCandidate(only.Id, only.Name, 1.0, 0, [])
                ],
                ConfidenceThreshold = 0.0,
                Explanation = $"Single collection '{only.Name}' — auto-selected"
            };
        }

        // Extract query keywords for matching
        var queryKeywords = ExtractQueryKeywords(query);

        // Also incorporate sub-query terms from the QueryPlan if available
        if (queryPlan?.SubQueries is { Count: > 0 })
        {
            foreach (var sq in queryPlan.SubQueries)
            {
                var subKeywords = ExtractQueryKeywords(sq.Query);
                foreach (var kw in subKeywords) queryKeywords.Add(kw);
            }
        }

        if (queryKeywords.Count == 0)
        {
            logger.LogDebug("No significant keywords extracted from query '{Query}', skipping routing", query);
            return new CollectionRoutingResult { Explanation = "No significant keywords in query" };
        }

        // Load salient terms for all collections (batch query)
        var collectionIds = collections.Select(c => c.Id).ToList();
        var allTerms = await db.SalientTerms
            .Where(t => collectionIds.Contains(t.CollectionId))
            .OrderByDescending(t => t.Score)
            .ToListAsync(ct);

        // Group by collection
        var termsByCollection = allTerms
            .GroupBy(t => t.CollectionId)
            .ToDictionary(
                g => g.Key,
                g => g.Take(MaxTermsPerCollection).ToList());

        // Score each collection
        var candidates = new List<CollectionCandidate>();

        foreach (var col in collections)
        {
            if (!termsByCollection.TryGetValue(col.Id, out var terms) || terms.Count == 0)
            {
                // Collection has no salient terms — try description matching as fallback
                var descScore = ScoreDescriptionMatch(query, queryKeywords, col.Description);
                if (descScore > 0)
                    candidates.Add(new CollectionCandidate(
                        col.Id, col.Name, descScore * 0.5, 0, []));
                continue;
            }

            var (score, matchedCount, matchedTerms) = ScoreCollection(queryKeywords, terms);

            if (score > 0)
                candidates.Add(new CollectionCandidate(
                    col.Id, col.Name, score, matchedCount, matchedTerms));
        }

        if (candidates.Count == 0)
        {
            logger.LogDebug("No collection matched query '{Query}' keywords: [{Keywords}]",
                query, string.Join(", ", queryKeywords.Take(10)));
            return new CollectionRoutingResult
            {
                Explanation = $"No collection matched keywords: {string.Join(", ", queryKeywords.Take(5))}"
            };
        }

        // Sort by confidence descending
        candidates = candidates.OrderByDescending(c => c.Confidence).ToList();

        // Normalize scores so top candidate is relative to others
        var maxScore = candidates[0].Confidence;
        if (maxScore > 0)
            candidates = candidates
                .Select(c => c with { Confidence = c.Confidence / maxScore })
                .ToList();

        // Check score gap: if top-2 are too close, don't auto-route
        var effectiveThreshold = DefaultConfidenceThreshold;
        string explanation;

        if (candidates.Count >= 2)
        {
            var gap = candidates[0].Confidence - candidates[1].Confidence;
            if (gap < MinScoreGap)
            {
                explanation =
                    $"Ambiguous: '{candidates[0].Name}' ({candidates[0].Confidence:F2}) vs '{candidates[1].Name}' ({candidates[1].Confidence:F2}), gap={gap:F2}";
                logger.LogInformation("Collection routing ambiguous for '{Query}': {Explanation}", query, explanation);
                // Return candidates but with a higher threshold so BestCollectionId is null
                return new CollectionRoutingResult
                {
                    Candidates = candidates,
                    ConfidenceThreshold = 1.1, // Force no auto-select
                    Explanation = explanation
                };
            }

            explanation =
                $"Routed to '{candidates[0].Name}' ({candidates[0].Confidence:F2}, {candidates[0].MatchedTermCount} terms matched)";
        }
        else
        {
            explanation =
                $"Routed to '{candidates[0].Name}' ({candidates[0].Confidence:F2}, {candidates[0].MatchedTermCount} terms matched)";
        }

        logger.LogInformation("Collection routing for '{Query}': {Explanation}", query, explanation);

        return new CollectionRoutingResult
        {
            Candidates = candidates,
            ConfidenceThreshold = effectiveThreshold,
            Explanation = explanation
        };
    }

    /// <summary>
    ///     Score a collection against query keywords using its salient terms.
    ///     Returns (rawScore, matchedCount, matchedTerms).
    /// </summary>
    private static (double score, int matchedCount, List<string> matchedTerms) ScoreCollection(
        HashSet<string> queryKeywords,
        List<CollectionSalientTerm> terms)
    {
        var score = 0.0;
        var matchedCount = 0;
        var matchedTerms = new List<string>();

        foreach (var term in terms)
        {
            // Exact match: query keyword matches normalized salient term
            if (queryKeywords.Contains(term.NormalizedTerm))
            {
                score += term.Score * 1.0;
                matchedCount++;
                matchedTerms.Add(term.Term);
                continue;
            }

            // Partial match: multi-word salient term where any word matches a query keyword
            var termWords = term.NormalizedTerm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (termWords.Length > 1)
            {
                var wordMatches = termWords.Count(tw => queryKeywords.Contains(tw));
                if (wordMatches > 0)
                {
                    // Partial credit: proportion of term words that matched
                    var partialScore = term.Score * (wordMatches / (double)termWords.Length) * 0.7;
                    score += partialScore;
                    matchedCount++;
                    matchedTerms.Add(term.Term);
                    continue;
                }
            }

            // Containment match: query keyword contains or is contained in salient term
            foreach (var kw in queryKeywords)
            {
                if (kw.Length >= 4 && term.NormalizedTerm.Contains(kw))
                {
                    score += term.Score * 0.5;
                    matchedCount++;
                    matchedTerms.Add(term.Term);
                    break;
                }

                if (term.NormalizedTerm.Length >= 4 && kw.Contains(term.NormalizedTerm))
                {
                    score += term.Score * 0.4;
                    matchedCount++;
                    matchedTerms.Add(term.Term);
                    break;
                }
            }
        }

        // Normalize by sqrt of total terms to avoid bias toward large collections
        if (terms.Count > 0)
            score /= Math.Sqrt(terms.Count);

        return (score, matchedCount, matchedTerms);
    }

    /// <summary>
    ///     Fallback: score a collection by matching query keywords against its description.
    /// </summary>
    private static double ScoreDescriptionMatch(
        string query,
        HashSet<string> queryKeywords,
        string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return 0.0;

        var descLower = description.ToLowerInvariant();
        var descWords = descLower
            .Split([' ', ',', '.', ':', ';', '!', '?', '(', ')', '[', ']', '-', '/'],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .ToHashSet();

        var matches = queryKeywords.Count(kw => descWords.Contains(kw));
        return queryKeywords.Count > 0 ? (double)matches / queryKeywords.Count : 0.0;
    }

    /// <summary>
    ///     Extract significant keywords from query text.
    ///     Filters stopwords, strips punctuation, normalizes to lowercase.
    /// </summary>
    private static HashSet<string> ExtractQueryKeywords(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var words = WordBoundaryRegex().Matches(query)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => w.Length >= 2 && !IsStopword(w))
            .ToHashSet();

        return words;
    }

    [GeneratedRegex(@"\b[a-zA-Z0-9]{2,}\b")]
    private static partial Regex WordBoundaryRegex();

    /// <summary>
    ///     Minimal stopword list for routing purposes.
    ///     Kept small to avoid filtering domain-specific terms that might be collection identifiers.
    /// </summary>
    private static bool IsStopword(string word)
    {
        return word.Length <= 2 || Stopwords.Contains(word);
    }

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Common English stopwords
        "the", "and", "for", "are", "but", "not", "you", "all", "can", "had", "her", "was",
        "one", "our", "out", "has", "have", "been", "some", "them", "than", "its", "over",
        "such", "that", "this", "with", "will", "each", "make", "like", "does", "into",
        "what", "when", "where", "which", "while", "who", "how", "about", "would", "there",
        "their", "from", "more", "other", "were", "they", "been", "said", "could",
        // Query-specific
        "tell", "show", "find", "search", "give", "help", "want", "need", "please",
        "know", "about", "much", "many", "also", "just", "very", "really", "should"
    };
}
