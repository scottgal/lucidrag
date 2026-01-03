using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mostlylucid.Shared.Entities;

/// <summary>
/// Entity for tracking learned slug redirects from 404 errors
/// </summary>
[Table("slug_redirects", Schema = "mostlylucid")]
public class SlugRedirectEntity
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    /// <summary>
    /// The incorrect slug that was requested (caused 404)
    /// </summary>
    [Required]
    [Column("from_slug")]
    [MaxLength(500)]
    public string FromSlug { get; set; } = string.Empty;

    /// <summary>
    /// The correct slug that users clicked on
    /// </summary>
    [Required]
    [Column("to_slug")]
    [MaxLength(500)]
    public string ToSlug { get; set; } = string.Empty;

    /// <summary>
    /// Language of the blog post
    /// </summary>
    [Required]
    [Column("language")]
    [MaxLength(10)]
    public string Language { get; set; } = "en";

    /// <summary>
    /// Weight/score indicating how many times this redirect was clicked
    /// Higher weight = more confidence in this redirect
    /// </summary>
    [Column("weight")]
    public int Weight { get; set; } = 0;

    /// <summary>
    /// Number of times this suggestion was shown but not clicked
    /// </summary>
    [Column("shown_count")]
    public int ShownCount { get; set; } = 0;

    /// <summary>
    /// Confidence score (Weight / (Weight + ShownCount))
    /// </summary>
    [Column("confidence_score")]
    public double ConfidenceScore { get; set; } = 0.0;

    /// <summary>
    /// Whether this redirect should automatically redirect (301) without showing 404
    /// </summary>
    [Column("auto_redirect")]
    public bool AutoRedirect { get; set; } = false;

    /// <summary>
    /// Threshold for auto-redirect (default 5 clicks with >70% confidence)
    /// </summary>
    public const int AutoRedirectWeightThreshold = 5;
    public const double AutoRedirectConfidenceThreshold = 0.7;

    /// <summary>
    /// When this redirect was first created
    /// </summary>
    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When this redirect was last updated
    /// </summary>
    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the last click occurred
    /// </summary>
    [Column("last_clicked_at")]
    public DateTimeOffset? LastClickedAt { get; set; }

    /// <summary>
    /// Calculate and update the confidence score
    /// </summary>
    public void UpdateConfidenceScore()
    {
        var total = Weight + ShownCount;
        ConfidenceScore = total > 0 ? (double)Weight / total : 0.0;

        // Automatically enable redirect if thresholds are met
        if (Weight >= AutoRedirectWeightThreshold && ConfidenceScore >= AutoRedirectConfidenceThreshold)
        {
            AutoRedirect = true;
        }
    }
}
