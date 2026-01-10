using System.Text.RegularExpressions;
using LucidRAG.Data;
using LucidRAG.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LucidRAG.Services;

/// <summary>
/// Extracts salient terms from collection documents using TF-IDF and entity analysis.
/// Combines multiple ranking signals using Reciprocal Rank Fusion (RRF).
/// </summary>
public class SalientTermsService(
    RagDocumentsDbContext db,
    ILogger<SalientTermsService> logger) : ISalientTermsService
{
    private const int DefaultTopTerms = 100; // Keep top N terms per collection
    private const int MinTermLength = 3;
    private const int MaxTermLength = 50;
    private const double RrfConstant = 60.0; // Standard RRF constant

    public async Task UpdateCollectionTermsAsync(Guid collectionId, CancellationToken ct = default)
    {
        logger.LogInformation("Updating salient terms for collection {CollectionId}", collectionId);

        var collection = await db.Collections
            .Include(c => c.Documents)
            .FirstOrDefaultAsync(c => c.Id == collectionId, ct);

        if (collection == null)
        {
            logger.LogWarning("Collection {CollectionId} not found", collectionId);
            return;
        }

        var completedDocs = collection.Documents
            .Where(d => d.Status == DocumentStatus.Completed)
            .ToList();

        if (completedDocs.Count == 0)
        {
            logger.LogInformation("No completed documents in collection {CollectionId}", collectionId);
            return;
        }

        // Extract terms from different sources
        var tfidfTerms = await ExtractTfIdfTermsAsync(collectionId, completedDocs, ct);
        var entityTerms = await ExtractEntityTermsAsync(collectionId, ct);
        var queryTerms = await ExtractQueryTermsAsync(collectionId, ct);

        // Combine using RRF
        var combinedTerms = CombineWithRrf(tfidfTerms, entityTerms, queryTerms);

        // Store in database
        await StoreSalientTermsAsync(collectionId, combinedTerms, ct);

        logger.LogInformation(
            "Updated {Count} salient terms for collection {CollectionId}",
            combinedTerms.Count, collectionId);
    }

    public async Task<List<SalientTermSuggestion>> GetAutocompleteSuggestionsAsync(
        Guid collectionId,
        string queryPrefix,
        int maxResults = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(queryPrefix) || queryPrefix.Length < 2)
            return [];

        var normalized = NormalizeTerm(queryPrefix);

        var suggestions = await db.SalientTerms
            .Where(t => t.CollectionId == collectionId &&
                       t.NormalizedTerm.StartsWith(normalized))
            .OrderByDescending(t => t.Score)
            .Take(maxResults)
            .Select(t => new SalientTermSuggestion(
                t.Term,
                t.Score,
                t.Source,
                t.DocumentFrequency))
            .ToListAsync(ct);

        return suggestions;
    }

    public async Task UpdateAllCollectionsAsync(CancellationToken ct = default)
    {
        var collectionIds = await db.Collections
            .Select(c => c.Id)
            .ToListAsync(ct);

        logger.LogInformation("Updating salient terms for {Count} collections", collectionIds.Count);

        foreach (var collectionId in collectionIds)
        {
            try
            {
                await UpdateCollectionTermsAsync(collectionId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to update salient terms for collection {CollectionId}", collectionId);
            }
        }

        logger.LogInformation("Completed updating salient terms for all collections");
    }

    public async Task<SalientTermStats> GetStatsAsync(Guid collectionId, CancellationToken ct = default)
    {
        var terms = await db.SalientTerms
            .Where(t => t.CollectionId == collectionId)
            .ToListAsync(ct);

        return new SalientTermStats(
            CollectionId: collectionId,
            TotalTerms: terms.Count,
            TfIdfTerms: terms.Count(t => t.Source == "tfidf"),
            EntityTerms: terms.Count(t => t.Source == "entity"),
            QueryTerms: terms.Count(t => t.Source == "query"),
            LastUpdated: terms.Any() ? terms.Max(t => t.UpdatedAt) : DateTimeOffset.MinValue
        );
    }

    /// <summary>
    /// Extract terms using TF-IDF from document content.
    /// Uses evidence artifacts (segment_text) to get document text.
    /// </summary>
    private async Task<Dictionary<string, TermScore>> ExtractTfIdfTermsAsync(
        Guid collectionId,
        List<DocumentEntity> documents,
        CancellationToken ct)
    {
        var termDocFreq = new Dictionary<string, HashSet<Guid>>(); // term -> set of doc IDs
        var docTermFreq = new Dictionary<Guid, Dictionary<string, int>>(); // docId -> term -> count

        // Get evidence text for documents via entity links
        var documentIds = documents.Select(d => d.Id).ToList();
        var evidenceTexts = await db.DocumentEntityLinks
            .Where(del => documentIds.Contains(del.DocumentId))
            .Select(del => del.EntityId)
            .Distinct()
            .SelectMany(entityId => db.EvidenceArtifacts
                .Where(e => e.EntityId == entityId && e.ArtifactType == EvidenceTypes.SegmentText)
                .Select(e => new {
                    EntityId = e.EntityId,
                    e.Content,
                    DocumentIds = db.DocumentEntityLinks
                        .Where(del2 => del2.EntityId == entityId && documentIds.Contains(del2.DocumentId))
                        .Select(del2 => del2.DocumentId)
                        .ToList()
                }))
            .ToListAsync(ct);

        // Count term frequencies from evidence text
        foreach (var evidence in evidenceTexts)
        {
            if (string.IsNullOrWhiteSpace(evidence.Content))
                continue;

            var terms = ExtractTerms(evidence.Content);

            foreach (var docId in evidence.DocumentIds)
            {
                if (!docTermFreq.ContainsKey(docId))
                    docTermFreq[docId] = new Dictionary<string, int>();

                var docTerms = docTermFreq[docId];

                foreach (var term in terms)
                {
                    // Document frequency (for IDF)
                    if (!termDocFreq.ContainsKey(term))
                        termDocFreq[term] = new HashSet<Guid>();
                    termDocFreq[term].Add(docId);

                    // Term frequency (for TF)
                    if (!docTerms.ContainsKey(term))
                        docTerms[term] = 0;
                    docTerms[term]++;
                }
            }
        }

        // Calculate TF-IDF scores
        var totalDocs = documents.Count;
        var tfidfScores = new Dictionary<string, TermScore>();

        foreach (var (term, termDocIds) in termDocFreq)
        {
            var df = termDocIds.Count;
            var idf = Math.Log((double)totalDocs / df);

            // Average TF across documents containing the term
            var avgTf = termDocIds.Average(docId =>
            {
                if (docTermFreq.TryGetValue(docId, out var terms) &&
                    terms.TryGetValue(term, out var count))
                {
                    var maxTf = terms.Values.Max();
                    return (double)count / maxTf; // Normalized TF
                }
                return 0;
            });

            var tfidf = avgTf * idf;

            tfidfScores[term] = new TermScore(
                Term: term,
                Score: tfidf,
                Source: "tfidf",
                DocumentFrequency: df
            );
        }

        // Return top terms
        return tfidfScores
            .OrderByDescending(kvp => kvp.Value.Score)
            .Take(DefaultTopTerms)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    /// <summary>
    /// Extract salient terms from entities in the collection.
    /// </summary>
    private async Task<Dictionary<string, TermScore>> ExtractEntityTermsAsync(
        Guid collectionId,
        CancellationToken ct)
    {
        var entities = await db.DocumentEntityLinks
            .Where(del => del.Document.CollectionId == collectionId)
            .Select(del => new { del.Entity!.CanonicalName, del.EntityId })
            .Distinct()
            .GroupBy(e => e.CanonicalName.ToLower())
            .Select(g => new
            {
                Term = g.First().CanonicalName,
                Count = g.Count(),
                EntityIds = g.Select(e => e.EntityId).Distinct().ToList()
            })
            .OrderByDescending(e => e.Count)
            .Take(DefaultTopTerms)
            .ToListAsync(ct);

        var maxCount = entities.Any() ? entities.Max(e => e.Count) : 1.0;

        return entities.ToDictionary(
            e => e.Term.ToLower(),
            e => new TermScore(
                Term: e.Term,
                Score: (double)e.Count / maxCount,
                Source: "entity",
                DocumentFrequency: e.Count // Using count as document frequency approximation
            ));
    }

    /// <summary>
    /// Extract terms from historical queries to the collection.
    /// (Future: track query patterns in ConversationEntity)
    /// </summary>
    private async Task<Dictionary<string, TermScore>> ExtractQueryTermsAsync(
        Guid collectionId,
        CancellationToken ct)
    {
        // For now, return empty - can be enhanced to track popular queries
        // from ConversationEntity messages
        return new Dictionary<string, TermScore>();
    }

    /// <summary>
    /// Combine multiple term rankings using Reciprocal Rank Fusion (RRF).
    /// RRF formula: score = sum(1 / (k + rank_i)) for each ranking
    /// </summary>
    private List<CombinedTerm> CombineWithRrf(
        params Dictionary<string, TermScore>[] rankings)
    {
        var allTerms = rankings
            .SelectMany(r => r.Keys)
            .Distinct()
            .ToList();

        var combined = new List<CombinedTerm>();

        foreach (var term in allTerms)
        {
            var rrfScore = 0.0;
            var sources = new List<string>();
            var docFreq = 0;

            foreach (var ranking in rankings)
            {
                if (ranking.TryGetValue(term, out var termScore))
                {
                    // Convert score to rank (assuming scores are already sorted)
                    var rank = ranking.Values
                        .OrderByDescending(t => t.Score)
                        .ToList()
                        .FindIndex(t => t.Term.Equals(termScore.Term, StringComparison.OrdinalIgnoreCase));

                    rrfScore += 1.0 / (RrfConstant + rank + 1);
                    sources.Add(termScore.Source);
                    docFreq = Math.Max(docFreq, termScore.DocumentFrequency);
                }
            }

            combined.Add(new CombinedTerm(
                Term: term,
                Score: rrfScore,
                Source: sources.Count > 1 ? "combined" : sources.FirstOrDefault() ?? "unknown",
                DocumentFrequency: docFreq
            ));
        }

        return combined
            .OrderByDescending(t => t.Score)
            .Take(DefaultTopTerms)
            .ToList();
    }

    /// <summary>
    /// Store salient terms in database, replacing existing terms for the collection.
    /// </summary>
    private async Task StoreSalientTermsAsync(
        Guid collectionId,
        List<CombinedTerm> terms,
        CancellationToken ct)
    {
        // Remove existing terms
        var existing = await db.SalientTerms
            .Where(t => t.CollectionId == collectionId)
            .ToListAsync(ct);

        db.SalientTerms.RemoveRange(existing);

        // Add new terms
        var entities = terms.Select(t => new CollectionSalientTerm
        {
            Id = Guid.NewGuid(),
            CollectionId = collectionId,
            Term = t.Term,
            NormalizedTerm = NormalizeTerm(t.Term),
            Score = t.Score,
            Source = t.Source,
            DocumentFrequency = t.DocumentFrequency,
            UpdatedAt = DateTimeOffset.UtcNow
        }).ToList();

        db.SalientTerms.AddRange(entities);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Extract terms from text using regex and filtering.
    /// </summary>
    private List<string> ExtractTerms(string text)
    {
        // Lowercase and extract word sequences
        var normalized = text.ToLowerInvariant();

        // Extract 1-3 word phrases
        var terms = new List<string>();

        // Single words
        var words = Regex.Matches(normalized, @"\b[a-z]{3,}\b")
            .Select(m => m.Value)
            .Where(w => !IsStopWord(w) && w.Length >= MinTermLength && w.Length <= MaxTermLength)
            .ToList();

        terms.AddRange(words);

        // Bigrams
        for (int i = 0; i < words.Count - 1; i++)
        {
            var bigram = $"{words[i]} {words[i + 1]}";
            if (bigram.Length <= MaxTermLength)
                terms.Add(bigram);
        }

        // Trigrams
        for (int i = 0; i < words.Count - 2; i++)
        {
            var trigram = $"{words[i]} {words[i + 1]} {words[i + 2]}";
            if (trigram.Length <= MaxTermLength)
                terms.Add(trigram);
        }

        return terms;
    }

    private static string NormalizeTerm(string term)
    {
        return term.ToLowerInvariant().Trim();
    }

    private static bool IsStopWord(string word)
    {
        // Common English stop words
        var stopWords = new HashSet<string>
        {
            "the", "be", "to", "of", "and", "a", "in", "that", "have", "i",
            "it", "for", "not", "on", "with", "he", "as", "you", "do", "at",
            "this", "but", "his", "by", "from", "they", "we", "say", "her", "she",
            "or", "an", "will", "my", "one", "all", "would", "there", "their",
            "what", "so", "up", "out", "if", "about", "who", "get", "which", "go",
            "me", "when", "make", "can", "like", "time", "no", "just", "him", "know",
            "take", "people", "into", "year", "your", "good", "some", "could", "them",
            "see", "other", "than", "then", "now", "look", "only", "come", "its", "over",
            "think", "also", "back", "after", "use", "two", "how", "our", "work",
            "first", "well", "way", "even", "new", "want", "because", "any", "these",
            "give", "day", "most", "us"
        };

        return stopWords.Contains(word);
    }

    private record TermScore(string Term, double Score, string Source, int DocumentFrequency);
    private record CombinedTerm(string Term, double Score, string Source, int DocumentFrequency);
}
