using System.Text.RegularExpressions;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Statistical text analysis service for entity normalization, 
/// semantic deduplication, and TF-IDF weighting.
/// Implements string similarity algorithms inline for AOT compatibility.
/// </summary>
public class TextAnalysisService
{
    
    // TF-IDF state
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.OrdinalIgnoreCase);
    private int _totalDocuments;
    
    // Known entity patterns for validation
    private static readonly HashSet<string> CommonTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mr.", "Mrs.", "Ms.", "Dr.", "Sir", "Lady", "Lord", "Miss", 
        "Captain", "Major", "Colonel", "Inspector", "Professor", "Rev.", "Reverend"
    };
    
    // Stop words for TF-IDF (common words to ignore)
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with",
        "by", "from", "as", "is", "was", "are", "were", "been", "be", "have", "has", "had",
        "do", "does", "did", "will", "would", "could", "should", "may", "might", "must",
        "shall", "can", "this", "that", "these", "those", "it", "its", "he", "she", "they",
        "him", "her", "them", "his", "their", "my", "your", "our", "who", "which", "what",
        "when", "where", "why", "how", "all", "each", "every", "both", "few", "more", "most",
        "other", "some", "such", "no", "not", "only", "same", "so", "than", "too", "very",
        "just", "also", "now", "here", "there", "then", "once", "i", "you", "we", "me", "us"
    };

    #region Entity Normalization

    /// <summary>
    /// Normalize and deduplicate a list of entity names using fuzzy matching
    /// </summary>
    public List<NormalizedEntity> NormalizeEntities(
        IEnumerable<string> rawNames, 
        string entityType,
        double similarityThreshold = 0.85)
    {
        var entities = new List<NormalizedEntity>();
        
        foreach (var rawName in rawNames)
        {
            var cleanName = CleanEntityName(rawName);
            if (string.IsNullOrWhiteSpace(cleanName)) continue;
            
            // Find best matching existing entity
            var (bestMatch, similarity) = FindBestMatch(cleanName, entities);
            
            if (bestMatch != null && similarity >= similarityThreshold)
            {
                // Merge into existing entity
                if (!bestMatch.Aliases.Contains(cleanName, StringComparer.OrdinalIgnoreCase) &&
                    !bestMatch.CanonicalName.Equals(cleanName, StringComparison.OrdinalIgnoreCase))
                {
                    bestMatch.Aliases.Add(cleanName);
                }
                
                // Update canonical name if this one is more complete
                if (IsMoreCanonical(cleanName, bestMatch.CanonicalName))
                {
                    bestMatch.Aliases.Add(bestMatch.CanonicalName);
                    // Create new entity with updated canonical name
                    var idx = entities.IndexOf(bestMatch);
                    entities[idx] = bestMatch with { CanonicalName = cleanName };
                }
            }
            else
            {
                // New entity
                var confidence = AssessEntityConfidence(cleanName, entityType);
                entities.Add(new NormalizedEntity(
                    cleanName,
                    new List<string>(),
                    entityType,
                    confidence,
                    new List<string>()));
            }
        }
        
        return entities
            .Where(e => e.Confidence > ConfidenceLevel.Uncertain)
            .OrderByDescending(e => e.Confidence)
            .ThenBy(e => e.CanonicalName)
            .ToList();
    }

    /// <summary>
    /// Find the best matching entity using combined similarity metrics
    /// </summary>
    private (NormalizedEntity? entity, double similarity) FindBestMatch(
        string name, 
        List<NormalizedEntity> entities)
    {
        NormalizedEntity? bestMatch = null;
        double bestSimilarity = 0;
        
        foreach (var entity in entities)
        {
            // Check against canonical name and all aliases
            var namesToCheck = new List<string> { entity.CanonicalName };
            namesToCheck.AddRange(entity.Aliases);
            
            foreach (var existingName in namesToCheck)
            {
                var similarity = ComputeCombinedSimilarity(name, existingName);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestMatch = entity;
                }
            }
        }
        
        return (bestMatch, bestSimilarity);
    }

    /// <summary>
    /// Compute combined similarity using multiple algorithms for robustness
    /// </summary>
    public double ComputeCombinedSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        
        // Normalize for comparison
        var normA = NormalizeForComparison(a);
        var normB = NormalizeForComparison(b);
        
        if (normA == normB) return 1.0;
        
        // Containment check (one is substring of other)
        if (normA.Contains(normB) || normB.Contains(normA))
            return 0.9;
        
        // Combined metric: weighted average of Jaro-Winkler, Levenshtein, and Cosine
        var jw = JaroWinklerSimilarity(normA, normB);
        var lev = NormalizedLevenshteinSimilarity(normA, normB);
        var cos = CosineSimilarity(normA, normB);
        
        // Weight Jaro-Winkler higher for names (good at prefix matching)
        return (jw * 0.5) + (lev * 0.3) + (cos * 0.2);
    }
    
    #region String Similarity Algorithms (AOT-compatible implementations)
    
    /// <summary>
    /// Jaro-Winkler similarity - good for names, emphasizes prefix matching
    /// </summary>
    private static double JaroWinklerSimilarity(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
            return s1 == s2 ? 1.0 : 0.0;
        
        var jaro = JaroSimilarity(s1, s2);
        
        // Calculate common prefix (up to 4 chars)
        var prefixLength = 0;
        var maxPrefix = Math.Min(4, Math.Min(s1.Length, s2.Length));
        for (int i = 0; i < maxPrefix; i++)
        {
            if (char.ToLowerInvariant(s1[i]) == char.ToLowerInvariant(s2[i]))
                prefixLength++;
            else
                break;
        }
        
        // Winkler modification: boost for common prefix
        const double scalingFactor = 0.1;
        return jaro + (prefixLength * scalingFactor * (1 - jaro));
    }
    
    /// <summary>
    /// Jaro similarity base calculation
    /// </summary>
    private static double JaroSimilarity(string s1, string s2)
    {
        if (s1 == s2) return 1.0;
        if (s1.Length == 0 || s2.Length == 0) return 0.0;
        
        var matchDistance = Math.Max(s1.Length, s2.Length) / 2 - 1;
        if (matchDistance < 0) matchDistance = 0;
        
        var s1Matches = new bool[s1.Length];
        var s2Matches = new bool[s2.Length];
        
        var matches = 0;
        var transpositions = 0;
        
        // Find matches
        for (int i = 0; i < s1.Length; i++)
        {
            var start = Math.Max(0, i - matchDistance);
            var end = Math.Min(i + matchDistance + 1, s2.Length);
            
            for (int j = start; j < end; j++)
            {
                if (s2Matches[j] || char.ToLowerInvariant(s1[i]) != char.ToLowerInvariant(s2[j]))
                    continue;
                
                s1Matches[i] = true;
                s2Matches[j] = true;
                matches++;
                break;
            }
        }
        
        if (matches == 0) return 0.0;
        
        // Count transpositions
        var k = 0;
        for (int i = 0; i < s1.Length; i++)
        {
            if (!s1Matches[i]) continue;
            while (!s2Matches[k]) k++;
            if (char.ToLowerInvariant(s1[i]) != char.ToLowerInvariant(s2[k]))
                transpositions++;
            k++;
        }
        
        return ((double)matches / s1.Length +
                (double)matches / s2.Length +
                (double)(matches - transpositions / 2) / matches) / 3.0;
    }
    
    /// <summary>
    /// Normalized Levenshtein similarity (0-1 range)
    /// </summary>
    private static double NormalizedLevenshteinSimilarity(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2)) return 1.0;
        if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0.0;
        
        var distance = LevenshteinDistance(s1, s2);
        var maxLength = Math.Max(s1.Length, s2.Length);
        
        return 1.0 - ((double)distance / maxLength);
    }
    
    /// <summary>
    /// Levenshtein edit distance
    /// </summary>
    private static int LevenshteinDistance(string s1, string s2)
    {
        var m = s1.Length;
        var n = s2.Length;
        
        // Use two-row optimization for memory efficiency
        var prev = new int[n + 1];
        var curr = new int[n + 1];
        
        for (int j = 0; j <= n; j++)
            prev[j] = j;
        
        for (int i = 1; i <= m; i++)
        {
            curr[0] = i;
            
            for (int j = 1; j <= n; j++)
            {
                var cost = char.ToLowerInvariant(s1[i - 1]) == char.ToLowerInvariant(s2[j - 1]) ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(prev[j] + 1, curr[j - 1] + 1),
                    prev[j - 1] + cost);
            }
            
            (prev, curr) = (curr, prev);
        }
        
        return prev[n];
    }
    
    /// <summary>
    /// Cosine similarity using character n-grams
    /// </summary>
    private static double CosineSimilarity(string s1, string s2, int ngramSize = 2)
    {
        if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0.0;
        if (s1.Length < ngramSize || s2.Length < ngramSize)
            return s1.Equals(s2, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
        
        var ngrams1 = GetNGramFrequency(s1, ngramSize);
        var ngrams2 = GetNGramFrequency(s2, ngramSize);
        
        // Compute dot product and magnitudes
        double dotProduct = 0;
        double magnitude1 = 0;
        double magnitude2 = 0;
        
        foreach (var kv in ngrams1)
        {
            magnitude1 += kv.Value * kv.Value;
            if (ngrams2.TryGetValue(kv.Key, out var count2))
                dotProduct += kv.Value * count2;
        }
        
        foreach (var kv in ngrams2)
            magnitude2 += kv.Value * kv.Value;
        
        var denominator = Math.Sqrt(magnitude1) * Math.Sqrt(magnitude2);
        return denominator == 0 ? 0.0 : dotProduct / denominator;
    }
    
    /// <summary>
    /// Get n-gram frequency map for a string
    /// </summary>
    private static Dictionary<string, int> GetNGramFrequency(string s, int n)
    {
        var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        
        for (int i = 0; i <= s.Length - n; i++)
        {
            var ngram = s.Substring(i, n);
            if (!freq.TryAdd(ngram, 1))
                freq[ngram]++;
        }
        
        return freq;
    }
    
    #endregion

    /// <summary>
    /// Assess confidence level for an entity based on heuristics
    /// </summary>
    private ConfidenceLevel AssessEntityConfidence(string name, string entityType)
    {
        // Very short names are suspicious
        if (name.Length < 3) return ConfidenceLevel.Uncertain;
        
        // Names with parenthetical qualifiers are often uncertain
        if (name.Contains("(") || name.Contains("unnamed") || name.Contains("unknown"))
            return ConfidenceLevel.Low;
        
        // Check for patterns that suggest this is a valid entity
        switch (entityType.ToLowerInvariant())
        {
            case "character":
                // Names with titles are more confident
                if (CommonTitles.Any(t => name.StartsWith(t, StringComparison.OrdinalIgnoreCase)))
                    return ConfidenceLevel.High;
                // Capitalized multi-word names
                if (Regex.IsMatch(name, @"^[A-Z][a-z]+(\s+[A-Z][a-z]+)+$"))
                    return ConfidenceLevel.High;
                // Single capitalized word (could be name or common noun)
                if (Regex.IsMatch(name, @"^[A-Z][a-z]+$"))
                    return ConfidenceLevel.Medium;
                break;
                
            case "location":
                // Places often have specific patterns
                if (name.Contains("Street") || name.Contains("Lodge") || name.Contains("House") ||
                    name.Contains("Park") || name.Contains("London") || name.Contains("Road"))
                    return ConfidenceLevel.High;
                break;
                
            case "date":
                // Dates with numbers are more confident
                if (Regex.IsMatch(name, @"\d"))
                    return ConfidenceLevel.High;
                // Named time periods
                if (name.Contains("century") || name.Contains("era") || name.Contains("morning") ||
                    name.Contains("evening") || name.Contains("night"))
                    return ConfidenceLevel.Medium;
                break;
        }
        
        return ConfidenceLevel.Medium;
    }

    /// <summary>
    /// Check if name A is more canonical (complete) than name B
    /// </summary>
    private bool IsMoreCanonical(string a, string b)
    {
        // Longer is generally more complete
        if (a.Length > b.Length + 3) return true;
        
        // Has title prefix
        var aHasTitle = CommonTitles.Any(t => a.StartsWith(t, StringComparison.OrdinalIgnoreCase));
        var bHasTitle = CommonTitles.Any(t => b.StartsWith(t, StringComparison.OrdinalIgnoreCase));
        if (aHasTitle && !bHasTitle) return true;
        
        // More words (fuller name)
        var aWords = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var bWords = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (aWords > bWords) return true;
        
        return false;
    }

    #endregion

    #region Semantic Deduplication

    /// <summary>
    /// Deduplicate claims/sentences based on semantic similarity
    /// </summary>
    public List<Claim> DeduplicateClaims(List<Claim> claims, double similarityThreshold = 0.75)
    {
        if (claims.Count <= 1) return claims;
        
        var deduplicated = new List<Claim>();
        var used = new HashSet<int>();
        
        for (int i = 0; i < claims.Count; i++)
        {
            if (used.Contains(i)) continue;
            
            var cluster = new List<Claim> { claims[i] };
            
            // Find similar claims
            for (int j = i + 1; j < claims.Count; j++)
            {
                if (used.Contains(j)) continue;
                
                var similarity = ComputeClaimSimilarity(claims[i], claims[j]);
                if (similarity >= similarityThreshold)
                {
                    cluster.Add(claims[j]);
                    used.Add(j);
                }
            }
            
            // Keep the best claim from the cluster (highest weight, most evidence)
            var best = cluster.OrderByDescending(c => c.Weight)
                              .ThenByDescending(c => c.Evidence.Count)
                              .First();
            
            // Merge evidence from all claims in cluster
            var mergedEvidence = cluster
                .SelectMany(c => c.Evidence)
                .DistinctBy(e => e.ChunkId)
                .ToList();
            
            deduplicated.Add(best with { Evidence = mergedEvidence });
            used.Add(i);
        }
        
        return deduplicated;
    }

    /// <summary>
    /// Compute similarity between two claims
    /// </summary>
    private double ComputeClaimSimilarity(Claim a, Claim b)
    {
        // Use cosine similarity on the claim text
        var textSimilarity = CosineSimilarity(
            NormalizeForComparison(a.Text),
            NormalizeForComparison(b.Text));
        
        // Boost if they share the same topic
        if (!string.IsNullOrEmpty(a.Topic) && a.Topic == b.Topic)
            textSimilarity = Math.Min(1.0, textSimilarity + 0.1);
        
        // Boost if they share evidence chunks
        var sharedChunks = a.Evidence.Select(e => e.ChunkId)
            .Intersect(b.Evidence.Select(e => e.ChunkId))
            .Count();
        if (sharedChunks > 0)
            textSimilarity = Math.Min(1.0, textSimilarity + 0.05 * sharedChunks);
        
        return textSimilarity;
    }

    #endregion

    #region TF-IDF Analysis

    /// <summary>
    /// Build TF-IDF index from document chunks
    /// </summary>
    public void BuildTfIdfIndex(IEnumerable<string> documents)
    {
        _documentFrequency.Clear();
        _totalDocuments = 0;
        
        foreach (var doc in documents)
        {
            _totalDocuments++;
            var terms = Tokenize(doc).Distinct();
            
            foreach (var term in terms)
            {
                if (!_documentFrequency.TryAdd(term, 1))
                    _documentFrequency[term]++;
            }
        }
    }

    /// <summary>
    /// Compute TF-IDF score for a term in a document
    /// </summary>
    public double ComputeTfIdf(string term, string document)
    {
        var tokens = Tokenize(document);
        var tf = (double)tokens.Count(t => t.Equals(term, StringComparison.OrdinalIgnoreCase)) / tokens.Count;
        
        var df = _documentFrequency.GetValueOrDefault(term.ToLowerInvariant(), 1);
        var idf = Math.Log((double)_totalDocuments / df);
        
        return tf * idf;
    }

    /// <summary>
    /// Get the most distinctive terms in a document (high TF-IDF)
    /// </summary>
    public List<(string term, double score)> GetDistinctiveTerms(string document, int topN = 10)
    {
        var tokens = Tokenize(document).Distinct().ToList();
        
        return tokens
            .Select(t => (term: t, score: ComputeTfIdf(t, document)))
            .OrderByDescending(x => x.score)
            .Take(topN)
            .ToList();
    }

    /// <summary>
    /// Classify if a term is "colour" (incidental) or potentially plot-critical
    /// High TF-IDF in few documents = likely colour (distinctive but rare)
    /// Moderate TF-IDF across many documents = likely plot-critical
    /// </summary>
    public ClaimType ClassifyTermImportance(string term)
    {
        var df = _documentFrequency.GetValueOrDefault(term.ToLowerInvariant(), 0);
        
        if (_totalDocuments == 0 || df == 0)
            return ClaimType.Colour;
        
        var documentRatio = (double)df / _totalDocuments;
        
        // Appears in most documents = likely plot-critical
        if (documentRatio > 0.5)
            return ClaimType.Fact;
        
        // Appears in some documents = moderate importance
        if (documentRatio > 0.2)
            return ClaimType.Inference;
        
        // Rare = likely incidental detail
        return ClaimType.Colour;
    }

    #endregion

    #region Output Post-Processing
    
    /// <summary>
    /// Post-process summary to remove hedging language, repetition, and parser failures
    /// Returns cleaned text with penalty score for logging
    /// </summary>
    public (string cleanedText, double penaltyScore, List<string> issues) PostProcessSummary(string summary)
    {
        var issues = new List<string>();
        var penalty = 0.0;
        var lines = summary.Split('\n').ToList();
        var cleanedLines = new List<string>();
        var seenInsights = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var line in lines)
        {
            var cleanLine = line.Trim();
            if (string.IsNullOrWhiteSpace(cleanLine)) continue;
            
            // Check for hedging language
            var hedgingScore = DetectHedging(cleanLine);
            if (hedgingScore > 0)
            {
                penalty += hedgingScore * 0.1;
                issues.Add($"Hedging: \"{cleanLine[..Math.Min(50, cleanLine.Length)]}...\"");
                // Remove hedging phrases but keep the content
                cleanLine = RemoveHedgingPhrases(cleanLine);
            }
            
            // Check for repetition (semantic dedup)
            var normalized = NormalizeForComparison(cleanLine);
            if (seenInsights.Any(s => ComputeCombinedSimilarity(s, normalized) > 0.75))
            {
                penalty += 0.15;
                issues.Add($"Repetition: \"{cleanLine[..Math.Min(50, cleanLine.Length)]}...\"");
                continue; // Skip duplicate
            }
            seenInsights.Add(normalized);
            
            // Check for meta-commentary (model explaining what it's doing)
            if (IsMetaCommentary(cleanLine))
            {
                penalty += 0.2;
                issues.Add($"Meta-commentary: \"{cleanLine[..Math.Min(50, cleanLine.Length)]}...\"");
                continue; // Remove entirely
            }
            
            cleanedLines.Add(cleanLine);
        }
        
        return (string.Join("\n", cleanedLines), penalty, issues);
    }
    
    /// <summary>
    /// Detect hedging language and return a score (0 = none, 1 = heavy)
    /// </summary>
    private double DetectHedging(string text)
    {
        var hedgingPhrases = new[]
        {
            "appears to", "seems to", "possibly", "likely", "probably",
            "may be", "might be", "could be", "it is possible",
            "assuming", "if this is", "this suggests", "apparently",
            "presumably", "potentially", "it seems", "one could argue"
        };
        
        var count = hedgingPhrases.Count(p => 
            text.Contains(p, StringComparison.OrdinalIgnoreCase));
        
        return Math.Min(1.0, count * 0.3);
    }
    
    /// <summary>
    /// Remove hedging phrases from text while preserving the core claim
    /// </summary>
    private string RemoveHedgingPhrases(string text)
    {
        var result = text;
        var hedgingPhrases = new[]
        {
            "appears to ", "seems to ", "possibly ", "likely ", "probably ",
            "it is possible that ", "assuming this is ", "apparently ",
            "presumably ", "potentially ", "it seems that ", "it seems "
        };
        
        foreach (var phrase in hedgingPhrases)
        {
            result = Regex.Replace(result, phrase, "", RegexOptions.IgnoreCase);
        }
        
        return result.Trim();
    }
    
    /// <summary>
    /// Detect meta-commentary (model explaining its process or uncertainty)
    /// </summary>
    private bool IsMetaCommentary(string text)
    {
        var metaPatterns = new[]
        {
            @"^(Note:|Note that|I should|I cannot|I don't have)",
            @"^(Based on|From the|In the|According to the) (text|document|content|passage)",
            @"(not enough context|unclear from|cannot determine|not mentioned)",
            @"(assuming these are|if this is|appears to be a)"
        };
        
        return metaPatterns.Any(p => 
            Regex.IsMatch(text, p, RegexOptions.IgnoreCase));
    }
    
    /// <summary>
    /// Filter entities to remove parser failures (pronouns, relational phrases, etc)
    /// </summary>
    public List<string> FilterEntities(List<string> rawEntities, int maxCount = 10)
    {
        return rawEntities
            .Select(CleanEntityName)
            .Where(IsValidEntity)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxCount)
            .ToList();
    }
    
    /// <summary>
    /// Check if an entity name is valid (not a pronoun, parser failure, etc)
    /// </summary>
    private bool IsValidEntity(string entity)
    {
        if (string.IsNullOrWhiteSpace(entity) || entity.Length < 2)
            return false;
        
        // Reject pronouns and relational phrases
        var invalidPatterns = new[]
        {
            @"^(his|her|their|its|my|your|our)\s",  // Possessive pronouns
            @"^(he|she|they|it|we|you)\b",           // Subject pronouns
            @"^(a|an|the|some|any|this|that)\s",     // Articles/determiners
            @"\b(brother|sister|mother|father|family|wife|husband)\b",  // Relationships without names
            @"^(young|old|younger|older|eldest)\s",  // Adjective-only
            @"^\d+\s*(st|nd|rd|th)?\s*$",            // Just numbers
            @"^\[.*\]$",                              // Bracketed content (parser artifacts)
            @"^(none|n/a|unknown|unnamed|not specified)$",
            @"(mentioned|described|referred)",        // Meta-references
        };
        
        return !invalidPatterns.Any(p => 
            Regex.IsMatch(entity, p, RegexOptions.IgnoreCase));
    }
    
    #endregion

    #region Text Utilities

    /// <summary>
    /// Tokenize text into terms for analysis
    /// </summary>
    public List<string> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();
        
        return Regex.Split(text.ToLowerInvariant(), @"\W+")
            .Where(t => t.Length > 2 && !StopWords.Contains(t))
            .ToList();
    }

    /// <summary>
    /// Normalize text for comparison (lowercase, remove punctuation, collapse whitespace)
    /// </summary>
    public string NormalizeForComparison(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        
        // Remove titles for comparison
        var normalized = text;
        foreach (var title in CommonTitles)
        {
            normalized = Regex.Replace(normalized, $@"\b{Regex.Escape(title)}\b\s*", "", RegexOptions.IgnoreCase);
        }
        
        return Regex.Replace(normalized.ToLowerInvariant(), @"[^\w\s]", "")
            .Trim();
    }

    /// <summary>
    /// Clean an entity name for storage
    /// </summary>
    public string CleanEntityName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        
        var clean = name.Trim()
            .Trim('*', '[', ']', '"', '\'', '-', '(', ')')
            .Trim();
        
        // Remove markdown artifacts
        clean = Regex.Replace(clean, @"\*\*|\*|`", "");
        
        // Remove citation references
        clean = Regex.Replace(clean, @"\[chunk-\d+\]", "");
        
        // Skip obviously bad entries
        if (clean.Length < 2 || 
            clean.StartsWith("no ", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("unnamed", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("not mentioned", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("none", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("likely", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("possibly", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("implied", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
        
        return clean;
    }

    /// <summary>
    /// Extract n-grams from text for similarity comparison
    /// </summary>
    public HashSet<string> ExtractNGrams(string text, int n = 2)
    {
        var ngrams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = NormalizeForComparison(text);
        
        if (normalized.Length < n) return ngrams;
        
        for (int i = 0; i <= normalized.Length - n; i++)
        {
            ngrams.Add(normalized.Substring(i, n));
        }
        
        return ngrams;
    }

    /// <summary>
    /// Compute Jaccard similarity between two sets
    /// </summary>
    public double JaccardSimilarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;
        if (a.Count == 0 || b.Count == 0) return 0.0;
        
        var intersection = a.Intersect(b).Count();
        var union = a.Union(b).Count();
        
        return (double)intersection / union;
    }

    #endregion
}
