using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

// Simple VisionResult placeholder for discriminator scoring
// Full implementation lives in LucidRAG.ImageCli.Services.VisionClients
public record VisionResult(
    bool Success,
    string? Error = null,
    string? Caption = null,
    string? Model = null,
    double? ConfidenceScore = null,
    List<EvidenceClaim>? Claims = null,
    VisionMetadata? EnhancedMetadata = null);

public record EvidenceClaim(
    string Text,
    List<string> Sources,
    List<string>? Evidence = null);

public record VisionMetadata
{
    public string? Tone { get; init; }
    public double? Sentiment { get; init; }
    public double? Complexity { get; init; }
    public double? AestheticScore { get; init; }
    public string? PrimarySubject { get; init; }
    public string? Purpose { get; init; }
    public string? TargetAudience { get; init; }
    public double Confidence { get; init; } = 1.0;
};

/// <summary>
/// Multi-vector discriminator service for vision analysis quality scoring
/// Implements orthogonal vector evaluation with decay-based learning
/// </summary>
public class DiscriminatorService
{
    private readonly ILogger<DiscriminatorService> _logger;
    private readonly SignalEffectivenessTracker _tracker;

    public DiscriminatorService(
        ILogger<DiscriminatorService> logger,
        SignalEffectivenessTracker tracker)
    {
        _logger = logger;
        _tracker = tracker;
    }

    /// <summary>
    /// Score a vision analysis result across orthogonal quality vectors
    /// </summary>
    public async Task<DiscriminatorScore> ScoreAnalysisAsync(
        string imageHash,
        ImageProfile profile,
        GifMotionProfile? gifMotion,
        VisionResult? visionResult,
        string? extractedText,
        string goal = "caption",
        CancellationToken ct = default)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var vectors = new VectorScores
        {
            OcrFidelity = ComputeOcrFidelity(profile, extractedText, visionResult),
            MotionAgreement = ComputeMotionAgreement(gifMotion, visionResult),
            PaletteConsistency = ComputePaletteConsistency(profile, visionResult),
            StructuralAlignment = ComputeStructuralAlignment(profile, visionResult),
            GroundingCompleteness = ComputeGroundingCompleteness(visionResult),
            NoveltyVsPrior = await ComputeNoveltyVsPriorAsync(imageHash, visionResult, ct)
        };

        var signalContributions = ExtractSignalContributions(profile, gifMotion, visionResult);

        var score = new DiscriminatorScore
        {
            Id = Guid.NewGuid().ToString(),
            ImageHash = imageHash,
            Timestamp = timestamp,
            ImageType = profile.DetectedType,
            Goal = goal,
            Vectors = vectors,
            SignalContributions = signalContributions,
            VisionModel = visionResult?.Model,
            Strategy = null // Will be set if strategy was used
        };

        _logger.LogInformation(
            "Discriminator score: {OverallScore:F3} for {ImageType}/{Goal} " +
            "(OCR: {OcrFid:F2}, Motion: {MotionAgree:F2}, Palette: {PaletteCons:F2}, " +
            "Structural: {StructAlign:F2}, Grounding: {GroundComp:F2}, Novelty: {NoveltyVsPrior:F2})",
            score.OverallScore,
            score.ImageType,
            score.Goal,
            vectors.OcrFidelity,
            vectors.MotionAgreement,
            vectors.PaletteConsistency,
            vectors.StructuralAlignment,
            vectors.GroundingCompleteness,
            vectors.NoveltyVsPrior);

        return score;
    }

    /// <summary>
    /// Record user feedback to update discriminator effectiveness
    /// </summary>
    public async Task RecordFeedbackAsync(
        DiscriminatorScore score,
        bool accepted,
        string? feedback = null,
        CancellationToken ct = default)
    {
        // Update score with feedback
        var updatedScore = score with
        {
            Accepted = accepted,
            Feedback = feedback
        };

        // Persist to ledger (immutable append-only)
        await _tracker.RecordScoreAsync(updatedScore, ct);

        // Update discriminator effectiveness weights
        await _tracker.UpdateEffectivenessAsync(updatedScore, ct);

        _logger.LogInformation(
            "Recorded feedback for {ImageHash}: {Accepted} (Overall: {Score:F3})",
            score.ImageHash,
            accepted ? "ACCEPTED" : "REJECTED",
            score.OverallScore);
    }

    /// <summary>
    /// Compute OCR fidelity vector score
    /// Measures: Text detection confidence, spell-check pass rate, OCR-vision agreement
    /// </summary>
    private double ComputeOcrFidelity(
        ImageProfile profile,
        string? extractedText,
        VisionResult? visionResult)
    {
        if (profile.TextLikeliness < 0.2)
            return 0.0; // Not applicable for non-text images

        var scores = new List<double>();

        // Text likeliness from profile (0.0-1.0)
        scores.Add(profile.TextLikeliness);

        // OCR extraction success
        if (!string.IsNullOrWhiteSpace(extractedText))
        {
            // Length as proxy for confidence (longer = more text detected)
            var lengthScore = Math.Min(1.0, extractedText.Length / 500.0);
            scores.Add(lengthScore);

            // Basic spell-check heuristic: ratio of dictionary words
            // (In production, would use actual spell-checker)
            var words = extractedText.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var validWordRatio = words.Length > 0
                ? words.Count(w => w.Length >= 2 && w.All(char.IsLetterOrDigit)) / (double)words.Length
                : 0.0;
            scores.Add(validWordRatio);
        }

        // Vision-OCR agreement: Does LLM caption mention text content?
        if (visionResult?.Caption != null && !string.IsNullOrWhiteSpace(extractedText))
        {
            var captionLower = visionResult.Caption.ToLowerInvariant();
            var textMentioned = captionLower.Contains("text") ||
                               captionLower.Contains("word") ||
                               captionLower.Contains("caption") ||
                               captionLower.Contains("label");

            scores.Add(textMentioned ? 0.8 : 0.3);
        }

        return scores.Count > 0 ? scores.Average() : 0.0;
    }

    /// <summary>
    /// Compute motion agreement vector score
    /// Measures: Frame consistency, optical flow confidence, temporal voting consensus
    /// </summary>
    private double ComputeMotionAgreement(
        GifMotionProfile? gifMotion,
        VisionResult? visionResult)
    {
        if (gifMotion == null)
            return 0.0; // Not applicable for static images

        var scores = new List<double>();

        // Motion detection confidence
        scores.Add(gifMotion.Confidence);

        // Motion magnitude consistency (not too chaotic, not too static)
        var magnitudeScore = gifMotion.MotionMagnitude > 1.0
            ? Math.Min(1.0, gifMotion.MotionMagnitude / 20.0)
            : 0.2; // Penalize near-static
        scores.Add(magnitudeScore);

        // Frame coverage (what % of frames have motion)
        var coverageScore = gifMotion.MotionPercentage / 100.0;
        scores.Add(coverageScore);

        // Vision-motion agreement: Does caption mention animation/movement?
        if (visionResult?.Caption != null)
        {
            var captionLower = visionResult.Caption.ToLowerInvariant();
            var motionMentioned = captionLower.Contains("animat") ||
                                 captionLower.Contains("moving") ||
                                 captionLower.Contains("motion") ||
                                 captionLower.Contains(gifMotion.MotionDirection);

            scores.Add(motionMentioned ? 0.9 : 0.4);
        }

        return scores.Average();
    }

    /// <summary>
    /// Compute palette consistency vector score
    /// Measures: Dominant color confidence, saturation variance, grayscale detection
    /// </summary>
    private double ComputePaletteConsistency(
        ImageProfile profile,
        VisionResult? visionResult)
    {
        var scores = new List<double>();

        // Dominant color confidence (top 3 colors account for what % of image)
        if (profile.DominantColors?.Any() == true)
        {
            var top3Coverage = profile.DominantColors.Take(3).Sum(c => c.Percentage) / 100.0;
            scores.Add(top3Coverage);
        }

        // Saturation consistency (low variance = consistent palette)
        // Mean saturation is already normalized 0.0-1.0
        var saturationScore = profile.IsMostlyGrayscale
            ? 1.0 // Perfect consistency for grayscale
            : Math.Max(0.0, 1.0 - Math.Abs(profile.MeanSaturation - 0.5) * 2); // Penalize extremes
        scores.Add(saturationScore);

        // Grayscale detection confidence
        if (profile.IsMostlyGrayscale)
        {
            scores.Add(0.95); // High confidence in grayscale
        }

        // Vision-color agreement: Does caption mention colors?
        if (visionResult?.Caption != null && profile.DominantColors?.Any() == true)
        {
            var captionLower = visionResult.Caption.ToLowerInvariant();
            var topColor = profile.DominantColors.First().Name.ToLowerInvariant();

            var colorMentioned = captionLower.Contains(topColor) ||
                                captionLower.Contains("color") ||
                                (profile.IsMostlyGrayscale && (captionLower.Contains("gray") || captionLower.Contains("black") || captionLower.Contains("white")));

            scores.Add(colorMentioned ? 0.85 : 0.4);
        }

        return scores.Count > 0 ? scores.Average() : 0.5;
    }

    /// <summary>
    /// Compute structural alignment vector score
    /// Measures: Edge density reliability, sharpness consistency, aspect ratio stability
    /// </summary>
    private double ComputeStructuralAlignment(
        ImageProfile profile,
        VisionResult? visionResult)
    {
        var scores = new List<double>();

        // Edge density normalization (typical range: 0.0-0.1)
        var edgeScore = Math.Min(1.0, profile.EdgeDensity * 10.0);
        scores.Add(edgeScore);

        // Sharpness scoring (LaplacianVariance)
        var sharpnessScore = profile.LaplacianVariance < 100 ? 0.2 : // Blurry
                            profile.LaplacianVariance < 500 ? 0.6 :  // Soft
                            Math.Min(1.0, profile.LaplacianVariance / 1000.0); // Sharp
        scores.Add(sharpnessScore);

        // Aspect ratio sanity check (extreme aspect ratios are suspicious)
        var aspectScore = profile.AspectRatio > 0.3 && profile.AspectRatio < 3.0 ? 1.0 : 0.5;
        scores.Add(aspectScore);

        // Luminance entropy (information content)
        var entropyScore = Math.Min(1.0, profile.LuminanceEntropy / 8.0); // Max entropy ~8
        scores.Add(entropyScore);

        return scores.Average();
    }

    /// <summary>
    /// Compute grounding completeness vector score
    /// Measures: Evidence source coverage, claim-to-signal ratio, non-synthesis grounding
    /// </summary>
    private double ComputeGroundingCompleteness(VisionResult? visionResult)
    {
        if (visionResult?.Claims == null || visionResult.Claims.Count == 0)
            return 0.0; // No structured claims

        var scores = new List<double>();

        // Evidence source diversity (how many different source types used)
        var allSources = visionResult.Claims
            .SelectMany(c => c.Sources)
            .Distinct()
            .ToList();

        var sourceTypes = allSources.Select(s => s[0]).Distinct().Count(); // V, M, O, S, G, L
        var diversityScore = Math.Min(1.0, sourceTypes / 4.0); // Expect 3-4 types for good grounding
        scores.Add(diversityScore);

        // Non-synthesis grounding percentage (claims with at least one non-L source)
        var groundedClaims = visionResult.Claims.Count(c =>
            c.Sources.Any(s => !s.StartsWith("L", StringComparison.OrdinalIgnoreCase)));
        var groundingRate = groundedClaims / (double)visionResult.Claims.Count;
        scores.Add(groundingRate);

        // Evidence detail (do claims have supporting evidence text?)
        var claimsWithEvidence = visionResult.Claims.Count(c =>
            c.Evidence != null && c.Evidence.Count > 0);
        var evidenceRate = claimsWithEvidence / (double)visionResult.Claims.Count;
        scores.Add(evidenceRate);

        // Penalize claims that are ONLY synthesis (L)
        var synthesisOnlyClaims = visionResult.Claims.Count(c =>
            c.Sources.All(s => s.StartsWith("L", StringComparison.OrdinalIgnoreCase)));
        var synthesisPenalty = 1.0 - (synthesisOnlyClaims / (double)visionResult.Claims.Count);
        scores.Add(synthesisPenalty);

        return scores.Average();
    }

    /// <summary>
    /// Compute novelty vs prior vector score
    /// Measures: Caption divergence, new signals discovered, confidence delta
    /// </summary>
    private async Task<double> ComputeNoveltyVsPriorAsync(
        string imageHash,
        VisionResult? visionResult,
        CancellationToken ct)
    {
        if (visionResult == null)
            return 0.0;

        // Retrieve prior scores for this image
        var priorScores = await _tracker.GetPriorScoresAsync(imageHash, limit: 5, ct);

        if (priorScores.Count == 0)
            return 1.0; // First analysis = maximum novelty

        var scores = new List<double>();

        // Caption divergence (simple Levenshtein-like measure)
        var priorCaptions = priorScores
            .Select(s => s.VisionModel)
            .Where(m => m != null)
            .ToList();

        if (priorCaptions.Count > 0)
        {
            // Simple heuristic: longer caption = more detail = more novelty
            var avgPriorLength = priorCaptions.Average(c => c?.Length ?? 0);
            var currentLength = visionResult.Caption?.Length ?? 0;

            var lengthDelta = Math.Abs(currentLength - avgPriorLength) / Math.Max(avgPriorLength, 1.0);
            var divergenceScore = Math.Min(1.0, lengthDelta);
            scores.Add(divergenceScore);
        }

        // Overall score delta
        var avgPriorScore = priorScores.Average(s => s.OverallScore);
        var scoreDelta = Math.Abs(visionResult.ConfidenceScore ?? 0.0 - avgPriorScore);
        scores.Add(scoreDelta);

        return scores.Count > 0 ? scores.Average() : 0.5;
    }

    /// <summary>
    /// Extract signal contributions from analysis results
    /// </summary>
    private Dictionary<string, SignalContribution> ExtractSignalContributions(
        ImageProfile profile,
        GifMotionProfile? gifMotion,
        VisionResult? visionResult)
    {
        var contributions = new Dictionary<string, SignalContribution>();

        // Profile signals
        AddContribution(contributions, "EdgeDensity", profile.EdgeDensity,
            new[] { "StructuralAlignment" }, profile.EdgeDensity);

        AddContribution(contributions, "LaplacianVariance", profile.LaplacianVariance,
            new[] { "StructuralAlignment" }, Math.Min(1.0, profile.LaplacianVariance / 1000.0));

        AddContribution(contributions, "TextLikeliness", profile.TextLikeliness,
            new[] { "OcrFidelity" }, profile.TextLikeliness);

        AddContribution(contributions, "MeanSaturation", profile.MeanSaturation,
            new[] { "PaletteConsistency" }, profile.MeanSaturation);

        AddContribution(contributions, "LuminanceEntropy", profile.LuminanceEntropy,
            new[] { "StructuralAlignment" }, Math.Min(1.0, profile.LuminanceEntropy / 8.0));

        // GIF motion signals
        if (gifMotion != null)
        {
            AddContribution(contributions, "MotionMagnitude", gifMotion.MotionMagnitude,
                new[] { "MotionAgreement" }, Math.Min(1.0, gifMotion.MotionMagnitude / 20.0));

            AddContribution(contributions, "MotionConfidence", gifMotion.Confidence,
                new[] { "MotionAgreement" }, gifMotion.Confidence);
        }

        // Vision result signals
        if (visionResult != null)
        {
            if (visionResult.Claims != null && visionResult.Claims.Count > 0)
            {
                var groundingRate = visionResult.Claims.Count(c =>
                    c.Sources.Any(s => !s.StartsWith("L"))) / (double)visionResult.Claims.Count;

                AddContribution(contributions, "ClaimGrounding", groundingRate,
                    new[] { "GroundingCompleteness" }, groundingRate);
            }
        }

        // Enhanced vision metadata signals (tone, sentiment, purpose, etc.)
        // These are LLM-derived features that the system will learn to weight appropriately
        if (visionResult?.EnhancedMetadata != null)
        {
            var metadata = visionResult.EnhancedMetadata;

            if (!string.IsNullOrEmpty(metadata.Tone))
            {
                // Tone: professional, casual, humorous, formal, technical
                // Contributes to grounding completeness (indicates model understanding)
                AddContribution(contributions, "LlmTone", metadata.Tone,
                    new[] { "GroundingCompleteness" }, metadata.Confidence);
            }

            if (metadata.Sentiment.HasValue)
            {
                // Sentiment: -1.0 (negative) to 1.0 (positive)
                // Normalize to 0.0-1.0 for strength (absolute value indicates confidence)
                var sentimentStrength = Math.Abs(metadata.Sentiment.Value);
                AddContribution(contributions, "LlmSentiment", metadata.Sentiment.Value,
                    new[] { "GroundingCompleteness" }, sentimentStrength * metadata.Confidence);
            }

            if (metadata.Complexity.HasValue)
            {
                // Visual complexity: 0.0 (simple) to 1.0 (complex)
                // Correlates with structural alignment
                AddContribution(contributions, "LlmComplexity", metadata.Complexity.Value,
                    new[] { "StructuralAlignment", "GroundingCompleteness" }, metadata.Complexity.Value * metadata.Confidence);
            }

            if (metadata.AestheticScore.HasValue)
            {
                // Aesthetic quality: 0.0 to 1.0
                // May correlate with palette consistency and structural alignment
                AddContribution(contributions, "LlmAestheticScore", metadata.AestheticScore.Value,
                    new[] { "PaletteConsistency", "StructuralAlignment" }, metadata.AestheticScore.Value * metadata.Confidence);
            }

            if (!string.IsNullOrEmpty(metadata.PrimarySubject))
            {
                // Primary subject (e.g., "portrait", "landscape", "diagram")
                // Indicates model understanding of content
                AddContribution(contributions, "LlmPrimarySubject", metadata.PrimarySubject,
                    new[] { "GroundingCompleteness" }, metadata.Confidence);
            }

            if (!string.IsNullOrEmpty(metadata.Purpose))
            {
                // Purpose: educational, entertainment, commercial, documentation
                // Correlates with image type detection
                AddContribution(contributions, "LlmPurpose", metadata.Purpose,
                    new[] { "GroundingCompleteness" }, metadata.Confidence);
            }

            if (!string.IsNullOrEmpty(metadata.TargetAudience))
            {
                // Target audience: general, technical, children, professionals
                // Indicates complexity and style understanding
                AddContribution(contributions, "LlmTargetAudience", metadata.TargetAudience,
                    new[] { "GroundingCompleteness" }, metadata.Confidence);
            }
        }

        // Calculate agreement between signals (signals in same vector)
        CalculateSignalAgreement(contributions);

        return contributions;
    }

    private void AddContribution(
        Dictionary<string, SignalContribution> contributions,
        string signalName,
        object value,
        string[] vectors,
        double strength)
    {
        contributions[signalName] = new SignalContribution
        {
            SignalName = signalName,
            Value = value,
            ContributedVectors = vectors.ToList(),
            Strength = strength
        };
    }

    /// <summary>
    /// Calculate agreement between signals based on vector overlap
    /// </summary>
    private void CalculateSignalAgreement(Dictionary<string, SignalContribution> contributions)
    {
        foreach (var (signalName, contribution) in contributions)
        {
            // Find other signals in same vectors
            var peersInVectors = contributions.Values
                .Where(c => c.SignalName != signalName &&
                           c.ContributedVectors.Intersect(contribution.ContributedVectors).Any())
                .ToList();

            if (peersInVectors.Count == 0)
            {
                contributions[signalName] = contribution with { Agreement = 1.0 }; // Solo signal
                continue;
            }

            // Agreement = how similar the strength values are
            var avgPeerStrength = peersInVectors.Average(p => p.Strength);
            var agreement = 1.0 - Math.Abs(contribution.Strength - avgPeerStrength);

            contributions[signalName] = contribution with { Agreement = agreement };
        }
    }
}
