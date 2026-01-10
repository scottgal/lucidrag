using Mostlylucid.DocSummarizer.Images.Models.Dynamic;

namespace Mostlylucid.DocSummarizer.Images.Models;

/// <summary>
/// Multi-vector discriminator score for vision analysis quality
/// </summary>
public record DiscriminatorScore
{
    /// <summary>
    /// Unique identifier for this scoring event
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Image SHA256 being scored
    /// </summary>
    public required string ImageHash { get; init; }

    /// <summary>
    /// Timestamp of evaluation
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Detected image type
    /// </summary>
    public required ImageType ImageType { get; init; }

    /// <summary>
    /// Analysis goal (ocr, caption, object_detection, etc.)
    /// </summary>
    public required string Goal { get; init; }

    /// <summary>
    /// Vector scores (orthogonal quality dimensions)
    /// </summary>
    public required VectorScores Vectors { get; init; }

    /// <summary>
    /// Overall quality score (0.0 = poor, 1.0 = excellent)
    /// </summary>
    public double OverallScore => Vectors.ComputeOverallScore();

    /// <summary>
    /// Contributing signals and their effectiveness
    /// </summary>
    public Dictionary<string, SignalContribution> SignalContributions { get; init; } = new();

    /// <summary>
    /// Vision model used (if any)
    /// </summary>
    public string? VisionModel { get; init; }

    /// <summary>
    /// Strategy applied (if any)
    /// </summary>
    public string? Strategy { get; init; }

    /// <summary>
    /// Whether this result was accepted (ground truth for learning)
    /// </summary>
    public bool? Accepted { get; init; }

    /// <summary>
    /// User feedback (if provided)
    /// </summary>
    public string? Feedback { get; init; }
}

/// <summary>
/// Orthogonal quality vectors for multi-dimensional optimization
/// </summary>
public record VectorScores
{
    /// <summary>
    /// OCR fidelity: How well extracted text matches visual content (0.0-1.0)
    /// Measured by: Text detection confidence, character recognition certainty, spell-check pass rate
    /// </summary>
    public double OcrFidelity { get; init; } = 0.0;

    /// <summary>
    /// Motion agreement: Consistency between motion analysis and visual observation (0.0-1.0)
    /// Measured by: Frame-to-frame consistency, optical flow confidence, temporal voting consensus
    /// </summary>
    public double MotionAgreement { get; init; } = 0.0;

    /// <summary>
    /// Palette consistency: Color analysis reliability (0.0-1.0)
    /// Measured by: Dominant color confidence, saturation variance, grayscale detection certainty
    /// </summary>
    public double PaletteConsistency { get; init; } = 0.0;

    /// <summary>
    /// Structural alignment: Geometric and edge detection confidence (0.0-1.0)
    /// Measured by: Edge density reliability, sharpness consistency, aspect ratio stability
    /// </summary>
    public double StructuralAlignment { get; init; } = 0.0;

    /// <summary>
    /// Grounding completeness: How well caption claims are backed by evidence (0.0-1.0)
    /// Measured by: Evidence source coverage, claim-to-signal ratio, non-synthesis grounding percentage
    /// </summary>
    public double GroundingCompleteness { get; init; } = 0.0;

    /// <summary>
    /// Novelty vs prior: Difference from previous results for same image (0.0-1.0)
    /// Measured by: Caption divergence, new signals discovered, confidence delta
    /// </summary>
    public double NoveltyVsPrior { get; init; } = 0.0;

    /// <summary>
    /// Compute overall score as weighted average of vectors
    /// Weights are initially equal but will be learned over time
    /// </summary>
    public double ComputeOverallScore()
    {
        var vectors = new[]
        {
            OcrFidelity,
            MotionAgreement,
            PaletteConsistency,
            StructuralAlignment,
            GroundingCompleteness,
            NoveltyVsPrior
        };

        // Filter out zero values (not applicable for this image type)
        var applicable = vectors.Where(v => v > 0.0).ToArray();

        if (applicable.Length == 0)
            return 0.0;

        return applicable.Average();
    }
}

/// <summary>
/// Signal contribution to discriminator score
/// </summary>
public record SignalContribution
{
    /// <summary>
    /// Signal name (e.g., "LaplacianVariance", "TextLikeliness", "MotionMagnitude")
    /// </summary>
    public required string SignalName { get; init; }

    /// <summary>
    /// Signal value
    /// </summary>
    public required object Value { get; init; }

    /// <summary>
    /// Which vectors this signal contributed to
    /// </summary>
    public List<string> ContributedVectors { get; init; } = new();

    /// <summary>
    /// Contribution strength (0.0-1.0)
    /// </summary>
    public double Strength { get; init; } = 0.0;

    /// <summary>
    /// Agreement with other signals in same vectors
    /// </summary>
    public double Agreement { get; init; } = 0.0;
}

/// <summary>
/// Discriminator effectiveness over time (with decay)
/// </summary>
public record DiscriminatorEffectiveness
{
    /// <summary>
    /// Signal name
    /// </summary>
    public required string SignalName { get; init; }

    /// <summary>
    /// Image type this effectiveness applies to
    /// </summary>
    public required ImageType ImageType { get; init; }

    /// <summary>
    /// Analysis goal
    /// </summary>
    public required string Goal { get; init; }

    /// <summary>
    /// Current effectiveness weight (0.0-1.0, starts at 1.0)
    /// </summary>
    public double Weight { get; init; } = 1.0;

    /// <summary>
    /// Number of evaluations this discriminator contributed to
    /// </summary>
    public int EvaluationCount { get; init; } = 0;

    /// <summary>
    /// Number of times this discriminator agreed with accepted results
    /// </summary>
    public int AgreementCount { get; init; } = 0;

    /// <summary>
    /// Number of times this discriminator disagreed with accepted results
    /// </summary>
    public int DisagreementCount { get; init; } = 0;

    /// <summary>
    /// Agreement rate (0.0-1.0)
    /// </summary>
    public double AgreementRate => EvaluationCount > 0
        ? (double)AgreementCount / EvaluationCount
        : 0.5; // Neutral prior

    /// <summary>
    /// Last evaluation timestamp (for decay calculation)
    /// </summary>
    public DateTimeOffset LastEvaluated { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Decay rate per day (default: 0.95 = 5% decay per day)
    /// </summary>
    public double DecayRate { get; init; } = 0.95;

    /// <summary>
    /// Calculate current weight with time-based decay
    /// </summary>
    public double GetDecayedWeight(DateTimeOffset now)
    {
        var daysSinceLastEval = (now - LastEvaluated).TotalDays;
        return Weight * Math.Pow(DecayRate, daysSinceLastEval);
    }

    /// <summary>
    /// Whether this discriminator should be retired (weight too low)
    /// </summary>
    public bool ShouldRetire(DateTimeOffset now, double threshold = 0.1)
    {
        return GetDecayedWeight(now) < threshold;
    }
}
