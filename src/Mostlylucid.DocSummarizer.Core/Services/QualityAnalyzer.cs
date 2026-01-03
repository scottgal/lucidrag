using System.Text.RegularExpressions;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Analyzes summary quality using statistical and heuristic metrics
/// </summary>
public class QualityAnalyzer
{
    private readonly TextAnalysisService _textAnalysis = new();
    
    /// <summary>
    /// Comprehensive quality report for a summary
    /// </summary>
    public record QualityReport(
        string DocumentId,
        string ModelUsed,
        double OverallScore,
        CitationMetrics Citations,
        CoherenceMetrics Coherence,
        EntityMetrics Entities,
        FactualityMetrics Factuality,
        List<QualityIssue> Issues,
        TimeSpan ProcessingTime,
        EvidenceDensityMetrics? EvidenceDensity = null,
        StabilityMetrics? Stability = null)
    {
        public string Grade => OverallScore switch
        {
            >= 0.9 => "A",
            >= 0.8 => "B",
            >= 0.7 => "C",
            >= 0.6 => "D",
            _ => "F"
        };
    }
    
    /// <summary>
    /// Evidence density: supported_claims / total_claims
    /// More meaningful than raw citation count
    /// </summary>
    public record EvidenceDensityMetrics(
        int TotalClaims,
        int SupportedClaims,
        int UnsupportedClaims,
        double Density,
        double AverageEvidencePerClaim,
        List<ClaimEvidenceDetail> ClaimDetails);
    
    /// <summary>
    /// Detail for each claim's evidence status
    /// </summary>
    public record ClaimEvidenceDetail(
        string ClaimText,
        bool IsSupported,
        int EvidenceCount,
        List<string> CitedChunks,
        double SourceOverlap);
    
    /// <summary>
    /// Stability metrics for confidence intervals across multiple runs
    /// </summary>
    public record StabilityMetrics(
        double MeanScore,
        double StandardDeviation,
        double ConfidenceIntervalLow,
        double ConfidenceIntervalHigh,
        int RunCount,
        List<double> IndividualScores)
    {
        public bool IsStable => StandardDeviation < 0.05;
        public string StabilityGrade => StandardDeviation switch
        {
            < 0.02 => "Excellent",
            < 0.05 => "Good",
            < 0.10 => "Fair",
            _ => "Unstable"
        };
    }

    public record CitationMetrics(
        int TotalCitations,
        int UniqueCitations,
        int ChunksCovered,
        int TotalChunks,
        double CoverageRatio,
        double CitationsPerClaim,
        List<string> UncitedClaims);

    public record CoherenceMetrics(
        double SentenceFlowScore,
        double TopicConsistencyScore,
        double RepetitionPenalty,
        int DuplicateSentences,
        double AverageClaimLength);

    public record EntityMetrics(
        int TotalEntities,
        int HighConfidenceEntities,
        int LowConfidenceEntities,
        int DuplicateEntities,
        double EntityDensity,
        List<string> SuspiciousEntities,
        List<EntityHallucinationDetail>? HallucinationDetails = null);
    
    /// <summary>
    /// Detailed hallucination analysis for a suspicious entity
    /// </summary>
    public record EntityHallucinationDetail(
        string EntityName,
        string EntityType,
        HallucinationReason Reason,
        double Confidence,
        string Explanation);
    
    public enum HallucinationReason
    {
        NotInSource,           // Entity doesn't appear in source corpus at all
        OnlyInSummary,         // Appears in summary but never in extracted features
        SingleOccurrenceHigh,  // Appears once in source but heavily weighted in summary
        PartialMatch,          // Partial match suggests confusion with similar entity
        InventedCombination    // Name appears to combine parts of multiple real entities
    }

    public record FactualityMetrics(
        double EvidenceRatio,
        int FactClaims,
        int InferenceClaims,
        int ColourClaims,
        double HallucinationRisk,
        List<string> UnsupportedClaims);

    public record QualityIssue(
        string Category,
        string Severity, // "critical", "warning", "info"
        string Description,
        string? Suggestion);

    /// <summary>
    /// Analyze a document summary for quality
    /// </summary>
    public QualityReport Analyze(
        DocumentSummary summary,
        List<DocumentChunk> sourceChunks,
        string modelUsed,
        TimeSpan processingTime)
    {
        var issues = new List<QualityIssue>();
        
        // Build TF-IDF index from source chunks for factuality checking
        _textAnalysis.BuildTfIdfIndex(sourceChunks.Select(c => c.Content));
        
        // Analyze each dimension
        var citationMetrics = AnalyzeCitations(summary, sourceChunks, issues);
        var coherenceMetrics = AnalyzeCoherence(summary, issues);
        var entityMetrics = AnalyzeEntitiesEnhanced(summary, sourceChunks, issues);
        var factualityMetrics = AnalyzeFactuality(summary, sourceChunks, issues);
        var evidenceDensity = AnalyzeEvidenceDensity(summary, sourceChunks, issues);
        
        // Calculate overall score (weighted average)
        var overallScore = CalculateOverallScore(
            citationMetrics, coherenceMetrics, entityMetrics, factualityMetrics, evidenceDensity);
        
        return new QualityReport(
            summary.Trace.DocumentId,
            modelUsed,
            overallScore,
            citationMetrics,
            coherenceMetrics,
            entityMetrics,
            factualityMetrics,
            issues,
            processingTime,
            evidenceDensity);
    }
    
    /// <summary>
    /// Analyze multiple runs of the same document to compute stability metrics
    /// </summary>
    public StabilityMetrics AnalyzeStability(List<QualityReport> reports)
    {
        if (reports.Count == 0)
            return new StabilityMetrics(0, 0, 0, 0, 0, []);
        
        if (reports.Count == 1)
            return new StabilityMetrics(
                reports[0].OverallScore, 
                0, 
                reports[0].OverallScore, 
                reports[0].OverallScore, 
                1, 
                [reports[0].OverallScore]);
        
        var scores = reports.Select(r => r.OverallScore).ToList();
        var mean = scores.Average();
        var variance = scores.Sum(s => Math.Pow(s - mean, 2)) / scores.Count;
        var stdDev = Math.Sqrt(variance);
        
        // 95% confidence interval (using t-distribution approximation for small samples)
        var tValue = reports.Count < 30 ? 2.0 : 1.96; // Simplified
        var marginOfError = tValue * (stdDev / Math.Sqrt(reports.Count));
        
        return new StabilityMetrics(
            mean,
            stdDev,
            Math.Max(0, mean - marginOfError),
            Math.Min(1, mean + marginOfError),
            reports.Count,
            scores);
    }

    private CitationMetrics AnalyzeCitations(
        DocumentSummary summary, 
        List<DocumentChunk> chunks,
        List<QualityIssue> issues)
    {
        var allText = summary.ExecutiveSummary + " " + 
                      string.Join(" ", summary.TopicSummaries.Select(t => t.Summary));
        
        // Extract citations from text
        var citationMatches = Regex.Matches(allText, @"\[chunk-(\d+)\]");
        var citations = citationMatches
            .Select(m => $"chunk-{m.Groups[1].Value}")
            .ToList();
        
        var uniqueCitations = citations.Distinct().ToList();
        var validChunkIds = chunks.Select(c => c.Id).ToHashSet();
        var coveredChunks = uniqueCitations.Where(c => validChunkIds.Contains(c)).ToList();
        
        // Find claims without citations
        var sentences = SplitIntoSentences(allText);
        var uncitedClaims = sentences
            .Where(s => s.Length > 50 && !Regex.IsMatch(s, @"\[chunk-\d+\]"))
            .Take(5)
            .ToList();
        
        var coverageRatio = chunks.Count > 0 
            ? (double)coveredChunks.Count / chunks.Count 
            : 0;
        
        var citationsPerClaim = sentences.Count > 0 
            ? (double)citations.Count / sentences.Count 
            : 0;
        
        // Issue checks
        if (coverageRatio < 0.3)
        {
            issues.Add(new QualityIssue(
                "Citations",
                "warning",
                $"Low chunk coverage: only {coverageRatio:P0} of chunks cited",
                "Increase retrieval count or topic diversity"));
        }
        
        if (uncitedClaims.Count > 3)
        {
            issues.Add(new QualityIssue(
                "Citations",
                "warning",
                $"{uncitedClaims.Count} claims lack citations",
                "Add evidence references for key claims"));
        }
        
        return new CitationMetrics(
            citations.Count,
            uniqueCitations.Count,
            coveredChunks.Count,
            chunks.Count,
            coverageRatio,
            citationsPerClaim,
            uncitedClaims);
    }

    private CoherenceMetrics AnalyzeCoherence(
        DocumentSummary summary,
        List<QualityIssue> issues)
    {
        var allText = summary.ExecutiveSummary + " " + 
                      string.Join(" ", summary.TopicSummaries.Select(t => t.Summary));
        
        var sentences = SplitIntoSentences(allText);
        
        // Check for duplicate/near-duplicate sentences
        var duplicates = FindDuplicateSentences(sentences);
        
        // Calculate flow score (adjacent sentence similarity should be moderate)
        var flowScore = CalculateFlowScore(sentences);
        
        // Topic consistency (do topics stay coherent within sections?)
        var topicConsistency = CalculateTopicConsistency(summary);
        
        // Repetition penalty
        var repetitionPenalty = duplicates.Count > 0 
            ? Math.Min(1.0, duplicates.Count * 0.1) 
            : 0;
        
        var avgClaimLength = sentences.Count > 0 
            ? sentences.Average(s => s.Split(' ').Length) 
            : 0;
        
        // Issue checks
        if (duplicates.Count > 2)
        {
            issues.Add(new QualityIssue(
                "Coherence",
                "warning",
                $"Found {duplicates.Count} duplicate/near-duplicate sentences",
                "Apply semantic deduplication"));
        }
        
        if (avgClaimLength > 50)
        {
            issues.Add(new QualityIssue(
                "Coherence",
                "info",
                $"Average sentence length ({avgClaimLength:F0} words) is high",
                "Consider breaking long sentences"));
        }
        
        return new CoherenceMetrics(
            flowScore,
            topicConsistency,
            repetitionPenalty,
            duplicates.Count,
            avgClaimLength);
    }

    private EntityMetrics AnalyzeEntities(
        DocumentSummary summary,
        List<DocumentChunk> chunks,
        List<QualityIssue> issues)
    {
        if (summary.Entities == null || !summary.Entities.HasAny)
        {
            return new EntityMetrics(0, 0, 0, 0, 0, new List<string>());
        }
        
        var allEntities = new List<string>();
        allEntities.AddRange(summary.Entities.Characters);
        allEntities.AddRange(summary.Entities.Locations);
        allEntities.AddRange(summary.Entities.Organizations);
        
        // Check for duplicates using fuzzy matching
        var duplicates = FindDuplicateEntities(allEntities);
        
        // Check for suspicious entities (likely hallucinations)
        var sourceText = string.Join(" ", chunks.Select(c => c.Content));
        var suspicious = FindSuspiciousEntities(allEntities, sourceText);
        
        // Confidence distribution
        var normalized = _textAnalysis.NormalizeEntities(
            summary.Entities.Characters, "character");
        var highConfidence = normalized.Count(e => e.Confidence >= ConfidenceLevel.High);
        var lowConfidence = normalized.Count(e => e.Confidence <= ConfidenceLevel.Low);
        
        var totalWords = summary.ExecutiveSummary.Split(' ').Length;
        var entityDensity = totalWords > 0 
            ? (double)allEntities.Count / totalWords * 100 
            : 0;
        
        // Issue checks
        if (duplicates.Count > 3)
        {
            issues.Add(new QualityIssue(
                "Entities",
                "warning",
                $"Found {duplicates.Count} duplicate entities: {string.Join(", ", duplicates.Take(3))}",
                "Apply entity normalization"));
        }
        
        if (suspicious.Count > 0)
        {
            issues.Add(new QualityIssue(
                "Entities",
                "critical",
                $"Suspicious entities not found in source: {string.Join(", ", suspicious.Take(3))}",
                "These may be hallucinations"));
        }
        
        return new EntityMetrics(
            allEntities.Count,
            highConfidence,
            lowConfidence,
            duplicates.Count,
            entityDensity,
            suspicious);
    }
    
    /// <summary>
    /// Enhanced entity analysis with detailed hallucination detection
    /// Flags when: entity not in source, appears only in summary, single occurrence but heavily weighted
    /// </summary>
    private EntityMetrics AnalyzeEntitiesEnhanced(
        DocumentSummary summary,
        List<DocumentChunk> chunks,
        List<QualityIssue> issues)
    {
        if (summary.Entities == null || !summary.Entities.HasAny)
        {
            return new EntityMetrics(0, 0, 0, 0, 0, [], []);
        }
        
        var allEntities = new List<string>();
        allEntities.AddRange(summary.Entities.Characters);
        allEntities.AddRange(summary.Entities.Locations);
        allEntities.AddRange(summary.Entities.Organizations);
        
        // Check for duplicates using fuzzy matching
        var duplicates = FindDuplicateEntities(allEntities);
        
        // Build source corpus index for entity verification
        var sourceText = string.Join(" ", chunks.Select(c => c.Content));
        var sourceTextLower = sourceText.ToLowerInvariant();
        
        // Detailed hallucination analysis
        var hallucinationDetails = new List<EntityHallucinationDetail>();
        var suspicious = new List<string>();
        
        // Analyze characters specifically (most prone to hallucination)
        foreach (var character in summary.Entities.Characters)
        {
            var detail = AnalyzeEntityForHallucination(character, "character", sourceTextLower, chunks);
            if (detail != null)
            {
                hallucinationDetails.Add(detail);
                suspicious.Add(character);
            }
        }
        
        // Analyze locations
        foreach (var location in summary.Entities.Locations)
        {
            var detail = AnalyzeEntityForHallucination(location, "location", sourceTextLower, chunks);
            if (detail != null)
            {
                hallucinationDetails.Add(detail);
                suspicious.Add(location);
            }
        }
        
        // Confidence distribution
        var normalized = _textAnalysis.NormalizeEntities(
            summary.Entities.Characters, "character");
        var highConfidence = normalized.Count(e => e.Confidence >= ConfidenceLevel.High);
        var lowConfidence = normalized.Count(e => e.Confidence <= ConfidenceLevel.Low);
        
        var totalWords = summary.ExecutiveSummary.Split(' ').Length;
        var entityDensity = totalWords > 0 
            ? (double)allEntities.Count / totalWords * 100 
            : 0;
        
        // Issue checks
        if (duplicates.Count > 3)
        {
            issues.Add(new QualityIssue(
                "Entities",
                "warning",
                $"Found {duplicates.Count} duplicate entities: {string.Join(", ", duplicates.Take(3))}",
                "Apply entity normalization"));
        }
        
        if (hallucinationDetails.Count > 0)
        {
            var criticalHallucinations = hallucinationDetails
                .Where(h => h.Reason == HallucinationReason.NotInSource || 
                           h.Reason == HallucinationReason.InventedCombination)
                .ToList();
            
            if (criticalHallucinations.Count > 0)
            {
                issues.Add(new QualityIssue(
                    "Entities",
                    "critical",
                    $"Likely hallucinated entities: {string.Join(", ", criticalHallucinations.Take(3).Select(h => $"{h.EntityName} ({h.Reason})"))}",
                    "These entities do not appear in source material"));
            }
            
            var suspiciousOnes = hallucinationDetails
                .Where(h => h.Reason == HallucinationReason.SingleOccurrenceHigh)
                .ToList();
            
            if (suspiciousOnes.Count > 0)
            {
                issues.Add(new QualityIssue(
                    "Entities",
                    "warning",
                    $"Over-emphasized entities (appear once but heavily featured): {string.Join(", ", suspiciousOnes.Take(3).Select(h => h.EntityName))}",
                    "These may be incidental details being over-weighted"));
            }
        }
        
        return new EntityMetrics(
            allEntities.Count,
            highConfidence,
            lowConfidence,
            duplicates.Count,
            entityDensity,
            suspicious,
            hallucinationDetails);
    }
    
    /// <summary>
    /// Analyze a single entity for hallucination indicators
    /// </summary>
    private EntityHallucinationDetail? AnalyzeEntityForHallucination(
        string entityName,
        string entityType,
        string sourceTextLower,
        List<DocumentChunk> chunks)
    {
        var cleanEntity = _textAnalysis.CleanEntityName(entityName);
        if (string.IsNullOrEmpty(cleanEntity) || cleanEntity.Length < 2) 
            return null;
        
        var entityLower = cleanEntity.ToLowerInvariant();
        
        // Check 1: Not in source at all
        if (!sourceTextLower.Contains(entityLower))
        {
            // Check individual words (might be partial match)
            var words = cleanEntity.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var foundWords = words.Count(w => w.Length > 2 && sourceTextLower.Contains(w.ToLowerInvariant()));
            
            if (foundWords == 0)
            {
                return new EntityHallucinationDetail(
                    cleanEntity,
                    entityType,
                    HallucinationReason.NotInSource,
                    0.95,
                    "Entity name does not appear anywhere in source text");
            }
            else if (foundWords < words.Length)
            {
                // Partial match - might be invented combination
                return new EntityHallucinationDetail(
                    cleanEntity,
                    entityType,
                    HallucinationReason.InventedCombination,
                    0.7,
                    $"Only {foundWords}/{words.Length} words found in source - possible confabulation");
            }
        }
        
        // Check 2: Count occurrences - single occurrence but heavily featured is suspicious
        var occurrences = CountOccurrences(sourceTextLower, entityLower);
        if (occurrences == 1)
        {
            // Check if entity appears prominently in summary
            // If it appears once in source but multiple times in summary, suspicious
            return new EntityHallucinationDetail(
                cleanEntity,
                entityType,
                HallucinationReason.SingleOccurrenceHigh,
                0.5,
                "Entity mentioned only once in source but extracted as key entity");
        }
        
        return null;
    }
    
    /// <summary>
    /// Count occurrences of a term in text
    /// </summary>
    private static int CountOccurrences(string text, string term)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(term, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += term.Length;
        }
        return count;
    }
    
    /// <summary>
    /// Analyze evidence density: supported_claims / total_claims
    /// More meaningful than raw citation count
    /// </summary>
    private EvidenceDensityMetrics AnalyzeEvidenceDensity(
        DocumentSummary summary,
        List<DocumentChunk> chunks,
        List<QualityIssue> issues)
    {
        var allText = summary.ExecutiveSummary + " " + 
                      string.Join(" ", summary.TopicSummaries.Select(t => t.Summary));
        
        var claims = SplitIntoClaims(allText);
        var sourceText = string.Join(" ", chunks.Select(c => c.Content));
        var chunkTexts = chunks.ToDictionary(c => c.Id, c => c.Content);
        
        var claimDetails = new List<ClaimEvidenceDetail>();
        var supportedCount = 0;
        var totalEvidence = 0;
        
        foreach (var claim in claims)
        {
            // Extract citations from claim
            var citationMatches = Regex.Matches(claim, @"\[chunk-(\d+)\]");
            var citedChunks = citationMatches
                .Select(m => $"chunk-{m.Groups[1].Value}")
                .Distinct()
                .ToList();
            
            // Calculate source overlap
            var cleanClaim = Regex.Replace(claim, @"\[chunk-\d+\]", "").Trim();
            var sourceOverlap = CalculateSourceOverlap(cleanClaim, sourceText);
            
            var isSupported = citedChunks.Count > 0 || sourceOverlap > 0.5;
            if (isSupported) supportedCount++;
            totalEvidence += citedChunks.Count;
            
            claimDetails.Add(new ClaimEvidenceDetail(
                cleanClaim.Length > 100 ? cleanClaim[..100] + "..." : cleanClaim,
                isSupported,
                citedChunks.Count,
                citedChunks,
                sourceOverlap));
        }
        
        var density = claims.Count > 0 
            ? (double)supportedCount / claims.Count 
            : 0;
        
        var avgEvidence = claims.Count > 0 
            ? (double)totalEvidence / claims.Count 
            : 0;
        
        // Issue checks based on evidence density
        if (density < 0.5)
        {
            issues.Add(new QualityIssue(
                "Evidence",
                "critical",
                $"Low evidence density: only {density:P0} of claims are supported",
                "Increase citation requirements or constrain to verifiable facts"));
        }
        else if (density < 0.7)
        {
            issues.Add(new QualityIssue(
                "Evidence",
                "warning",
                $"Moderate evidence density: {density:P0} of claims supported",
                "Consider requiring citations for all factual claims"));
        }
        
        return new EvidenceDensityMetrics(
            claims.Count,
            supportedCount,
            claims.Count - supportedCount,
            density,
            avgEvidence,
            claimDetails);
    }
    
    /// <summary>
    /// Split text into individual claims (bullet points or sentences)
    /// </summary>
    private List<string> SplitIntoClaims(string text)
    {
        // First try bullet points
        var bullets = Regex.Split(text, @"\n\s*[-•*]\s*")
            .Where(s => s.Length > 20)
            .ToList();
        
        if (bullets.Count > 3)
            return bullets;
        
        // Fall back to sentences
        return SplitIntoSentences(text);
    }
    
    /// <summary>
    /// Calculate how much of a claim's key terms appear in source
    /// </summary>
    private double CalculateSourceOverlap(string claim, string sourceText)
    {
        var claimTerms = _textAnalysis.Tokenize(claim)
            .Where(t => t.Length > 3)
            .ToList();
        
        if (claimTerms.Count == 0) return 1.0; // No terms to check
        
        var sourceLower = sourceText.ToLowerInvariant();
        var foundTerms = claimTerms.Count(t => sourceLower.Contains(t));
        
        return (double)foundTerms / claimTerms.Count;
    }

    private FactualityMetrics AnalyzeFactuality(
        DocumentSummary summary,
        List<DocumentChunk> chunks,
        List<QualityIssue> issues)
    {
        var allText = summary.ExecutiveSummary;
        var sentences = SplitIntoSentences(allText);
        var sourceText = string.Join(" ", chunks.Select(c => c.Content));
        
        var factClaims = 0;
        var inferenceClaims = 0;
        var colourClaims = 0;
        var unsupportedClaims = new List<string>();
        
        foreach (var sentence in sentences)
        {
            var claimType = ClassifyClaimType(sentence, sourceText);
            switch (claimType)
            {
                case ClaimType.Fact:
                    factClaims++;
                    break;
                case ClaimType.Inference:
                    inferenceClaims++;
                    break;
                case ClaimType.Colour:
                    colourClaims++;
                    break;
            }
            
            // Check if claim has evidence in source
            if (!HasEvidenceInSource(sentence, sourceText))
            {
                unsupportedClaims.Add(sentence.Length > 100 
                    ? sentence[..100] + "..." 
                    : sentence);
            }
        }
        
        var totalClaims = factClaims + inferenceClaims + colourClaims;
        var evidenceRatio = totalClaims > 0 
            ? (double)(totalClaims - unsupportedClaims.Count) / totalClaims 
            : 0;
        
        // Hallucination risk: high if many unsupported claims or low evidence ratio
        var hallucinationRisk = 1.0 - evidenceRatio;
        if (colourClaims > factClaims)
            hallucinationRisk = Math.Min(1.0, hallucinationRisk + 0.2);
        
        // Issue checks
        if (hallucinationRisk > 0.4)
        {
            issues.Add(new QualityIssue(
                "Factuality",
                "critical",
                $"High hallucination risk: {hallucinationRisk:P0}",
                "Use larger model or constrain to cited facts only"));
        }
        
        if (colourClaims > factClaims)
        {
            issues.Add(new QualityIssue(
                "Factuality",
                "warning",
                "More 'colour' claims than facts - summary may over-emphasize incidental details",
                "Weight facts higher in synthesis"));
        }
        
        return new FactualityMetrics(
            evidenceRatio,
            factClaims,
            inferenceClaims,
            colourClaims,
            hallucinationRisk,
            unsupportedClaims.Take(5).ToList());
    }

    private double CalculateOverallScore(
        CitationMetrics citations,
        CoherenceMetrics coherence,
        EntityMetrics entities,
        FactualityMetrics factuality,
        EvidenceDensityMetrics? evidenceDensity = null)
    {
        // Weighted scoring
        var citationScore = (citations.CoverageRatio * 0.5) + 
                           (Math.Min(1.0, citations.CitationsPerClaim) * 0.5);
        
        var coherenceScore = (coherence.SentenceFlowScore * 0.4) +
                            (coherence.TopicConsistencyScore * 0.4) +
                            ((1 - coherence.RepetitionPenalty) * 0.2);
        
        var entityScore = entities.TotalEntities > 0
            ? (1 - (double)entities.SuspiciousEntities.Count / Math.Max(1, entities.TotalEntities)) *
              (1 - (double)entities.DuplicateEntities / Math.Max(1, entities.TotalEntities))
            : 0.5; // Neutral if no entities
        
        // Evidence density score (if available) - more meaningful than raw citation count
        var evidenceScore = evidenceDensity != null 
            ? evidenceDensity.Density 
            : factuality.EvidenceRatio;
        
        var factualityScore = (evidenceScore * 0.5) +  // Evidence density is key
                             (factuality.EvidenceRatio * 0.2) +
                             ((1 - factuality.HallucinationRisk) * 0.3);
        
        // Overall weighted average - factuality is most important for trustworthiness
        return (citationScore * 0.2) +
               (coherenceScore * 0.15) +
               (entityScore * 0.2) +     // Boosted - hallucination detection is critical
               (factualityScore * 0.45); // Factuality is primary concern
    }

    #region Helper Methods

    private List<string> SplitIntoSentences(string text)
    {
        return Regex.Split(text, @"(?<=[.!?])\s+")
            .Where(s => s.Length > 10)
            .Select(s => s.Trim())
            .ToList();
    }

    private List<string> FindDuplicateSentences(List<string> sentences)
    {
        var duplicates = new List<string>();
        
        for (int i = 0; i < sentences.Count; i++)
        {
            for (int j = i + 1; j < sentences.Count; j++)
            {
                var similarity = _textAnalysis.ComputeCombinedSimilarity(
                    sentences[i], sentences[j]);
                
                if (similarity > 0.8)
                {
                    duplicates.Add(sentences[j].Length > 50 
                        ? sentences[j][..50] + "..." 
                        : sentences[j]);
                }
            }
        }
        
        return duplicates.Distinct().ToList();
    }

    private double CalculateFlowScore(List<string> sentences)
    {
        if (sentences.Count < 2) return 1.0;
        
        var similarities = new List<double>();
        for (int i = 0; i < sentences.Count - 1; i++)
        {
            var sim = _textAnalysis.ComputeCombinedSimilarity(
                sentences[i], sentences[i + 1]);
            similarities.Add(sim);
        }
        
        // Ideal flow: moderate similarity (0.3-0.6)
        // Too low = disconnected, too high = repetitive
        var avgSim = similarities.Average();
        if (avgSim >= 0.3 && avgSim <= 0.6)
            return 1.0;
        else if (avgSim < 0.3)
            return 0.5 + avgSim; // Boost low similarity
        else
            return 1.0 - (avgSim - 0.6); // Penalize high similarity
    }

    private double CalculateTopicConsistency(DocumentSummary summary)
    {
        if (summary.TopicSummaries.Count < 2) return 1.0;
        
        // Check that each topic section stays focused
        var scores = new List<double>();
        
        foreach (var topic in summary.TopicSummaries)
        {
            var topicWords = _textAnalysis.Tokenize(topic.Topic);
            var summaryWords = _textAnalysis.Tokenize(topic.Summary);
            
            // How many topic words appear in summary?
            var overlap = topicWords.Count(tw => 
                summaryWords.Any(sw => sw.Contains(tw, StringComparison.OrdinalIgnoreCase)));
            
            var score = topicWords.Count > 0 
                ? (double)overlap / topicWords.Count 
                : 0.5;
            scores.Add(score);
        }
        
        return scores.Average();
    }

    private List<string> FindDuplicateEntities(List<string> entities)
    {
        var duplicates = new List<string>();
        var seen = new List<string>();
        
        foreach (var entity in entities)
        {
            var match = seen.FirstOrDefault(s => 
                _textAnalysis.ComputeCombinedSimilarity(s, entity) > 0.85);
            
            if (match != null)
                duplicates.Add($"{entity} ≈ {match}");
            else
                seen.Add(entity);
        }
        
        return duplicates;
    }

    private List<string> FindSuspiciousEntities(List<string> entities, string sourceText)
    {
        var suspicious = new List<string>();
        var normalizedSource = sourceText.ToLowerInvariant();
        
        foreach (var entity in entities)
        {
            var cleanEntity = _textAnalysis.CleanEntityName(entity);
            if (string.IsNullOrEmpty(cleanEntity)) continue;
            
            // Check if entity appears in source
            if (!normalizedSource.Contains(cleanEntity.ToLowerInvariant()))
            {
                // Check individual words for partial matches
                var words = cleanEntity.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var foundWords = words.Count(w => 
                    w.Length > 3 && normalizedSource.Contains(w.ToLowerInvariant()));
                
                if (foundWords < words.Length / 2.0)
                {
                    suspicious.Add(cleanEntity);
                }
            }
        }
        
        return suspicious;
    }

    private ClaimType ClassifyClaimType(string sentence, string sourceText)
    {
        var normalizedSentence = sentence.ToLowerInvariant();
        var normalizedSource = sourceText.ToLowerInvariant();
        
        // Inference indicators
        var inferenceWords = new[] { "suggests", "implies", "indicates", "likely", "probably", 
            "may", "might", "could", "appears", "seems", "perhaps", "possibly" };
        if (inferenceWords.Any(w => normalizedSentence.Contains(w)))
            return ClaimType.Inference;
        
        // Colour indicators (incidental details, description)
        var colourWords = new[] { "atmosphere", "setting", "mood", "tone", "style",
            "interestingly", "notably", "curiously", "vivid", "striking" };
        if (colourWords.Any(w => normalizedSentence.Contains(w)))
            return ClaimType.Colour;
        
        // Check for direct evidence in source
        var keyTerms = _textAnalysis.Tokenize(sentence)
            .Where(t => t.Length > 4)
            .Take(5)
            .ToList();
        
        var termsInSource = keyTerms.Count(t => normalizedSource.Contains(t));
        if (termsInSource >= keyTerms.Count * 0.6)
            return ClaimType.Fact;
        
        return ClaimType.Inference;
    }

    private bool HasEvidenceInSource(string claim, string sourceText)
    {
        var claimTerms = _textAnalysis.Tokenize(claim)
            .Where(t => t.Length > 4)
            .ToList();
        
        if (claimTerms.Count == 0) return true; // Can't verify, assume ok
        
        var normalizedSource = sourceText.ToLowerInvariant();
        var foundTerms = claimTerms.Count(t => normalizedSource.Contains(t));
        
        return foundTerms >= claimTerms.Count * 0.4;
    }

    #endregion
}
