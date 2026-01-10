using Mostlylucid.DocSummarizer.Images.Config;

namespace Mostlylucid.DocSummarizer.Images.Models;

/// <summary>
/// Final bounded image summary combining deterministic profile with validated model claims.
/// This is the main output of the image analysis pipeline.
/// </summary>
public record BoundedImageSummary
{
    /// <summary>
    /// Deterministic image profile (always available)
    /// </summary>
    public required ImageProfile Profile { get; init; }

    /// <summary>
    /// Bounded caption after validation (may differ from model's raw caption)
    /// </summary>
    public string? BoundedCaption { get; init; }

    /// <summary>
    /// Tags that passed validation
    /// </summary>
    public IReadOnlyList<string> ValidatedTags { get; init; } = [];

    /// <summary>
    /// Claims that were supported by the profile
    /// </summary>
    public IReadOnlyList<ValidatedClaim> SupportedClaims { get; init; } = [];

    /// <summary>
    /// Claims that were downgraded/reframed due to lack of support
    /// </summary>
    public IReadOnlyList<ValidatedClaim> DowngradedClaims { get; init; } = [];

    /// <summary>
    /// Claims that couldn't be verified either way
    /// </summary>
    public IReadOnlyList<ValidatedClaim> UncertainClaims { get; init; } = [];

    /// <summary>
    /// Extracted text from OCR (if text-likeliness was high)
    /// </summary>
    public string? ExtractedText { get; init; }

    /// <summary>
    /// CLIP embedding for similarity search (512 or 768 dimensions)
    /// </summary>
    public float[]? ClipEmbedding { get; init; }

    /// <summary>
    /// Processing mode used for this image
    /// </summary>
    public ImageSummaryMode Mode { get; init; }

    /// <summary>
    /// Processing time in milliseconds
    /// </summary>
    public long ProcessingTimeMs { get; init; }

    /// <summary>
    /// Generate a human-readable markdown summary
    /// </summary>
    public string ToMarkdown()
    {
        var sb = new System.Text.StringBuilder();

        // Type and dimensions
        sb.AppendLine($"**{Profile.DetectedType}** ({Profile.Width}x{Profile.Height}, {Profile.Format})");
        sb.AppendLine();

        // Bounded caption
        if (!string.IsNullOrEmpty(BoundedCaption))
        {
            sb.AppendLine($"**Caption:** {BoundedCaption}");
            sb.AppendLine();
        }

        // Key measured properties
        sb.AppendLine("**Measured Properties:**");
        sb.AppendLine($"- Brightness: {DescribeBrightness()}");
        sb.AppendLine($"- Sharpness: {DescribeSharpness()}");
        sb.AppendLine($"- Dominant colors: {string.Join(", ", Profile.DominantColors.Take(3).Select(c => c.Name))}");
        if (Profile.IsMostlyGrayscale)
            sb.AppendLine("- Mostly grayscale");
        sb.AppendLine();

        // Validated tags
        if (ValidatedTags.Count > 0)
        {
            sb.AppendLine($"**Tags:** {string.Join(", ", ValidatedTags)}");
            sb.AppendLine();
        }

        // Extracted text
        if (!string.IsNullOrEmpty(ExtractedText))
        {
            sb.AppendLine("**Extracted Text:**");
            sb.AppendLine($"```\n{ExtractedText.Trim()}\n```");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private string DescribeBrightness()
    {
        return Profile.MeanLuminance switch
        {
            < 50 => "very dark",
            < 100 => "dark",
            < 150 => "medium",
            < 200 => "bright",
            _ => "very bright"
        };
    }

    private string DescribeSharpness()
    {
        return Profile.LaplacianVariance switch
        {
            < 100 => "blurry",
            < 500 => "slightly soft",
            < 1500 => "sharp",
            _ => "very sharp"
        };
    }
}
