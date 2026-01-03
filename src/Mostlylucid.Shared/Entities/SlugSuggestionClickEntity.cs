using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mostlylucid.Shared.Entities;

/// <summary>
/// Entity for tracking individual clicks on slug suggestions from 404 pages
/// Used for analytics and debugging the learning system
/// </summary>
[Table("slug_suggestion_clicks", Schema = "mostlylucid")]
public class SlugSuggestionClickEntity
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    /// <summary>
    /// The incorrect slug that caused the 404
    /// </summary>
    [Required]
    [Column("requested_slug")]
    [MaxLength(500)]
    public string RequestedSlug { get; set; } = string.Empty;

    /// <summary>
    /// The suggested slug that was clicked
    /// </summary>
    [Required]
    [Column("clicked_slug")]
    [MaxLength(500)]
    public string ClickedSlug { get; set; } = string.Empty;

    /// <summary>
    /// Language of the blog post
    /// </summary>
    [Required]
    [Column("language")]
    [MaxLength(10)]
    public string Language { get; set; } = "en";

    /// <summary>
    /// Position of the clicked suggestion in the list (0-based)
    /// </summary>
    [Column("suggestion_position")]
    public int SuggestionPosition { get; set; }

    /// <summary>
    /// The original similarity score from the suggestion algorithm
    /// </summary>
    [Column("original_similarity_score")]
    public double OriginalSimilarityScore { get; set; }

    /// <summary>
    /// User's IP address (for analytics, anonymized)
    /// </summary>
    [Column("user_ip")]
    [MaxLength(50)]
    public string? UserIp { get; set; }

    /// <summary>
    /// User agent string
    /// </summary>
    [Column("user_agent")]
    [MaxLength(500)]
    public string? UserAgent { get; set; }

    /// <summary>
    /// When this click occurred
    /// </summary>
    [Column("clicked_at")]
    public DateTimeOffset ClickedAt { get; set; } = DateTimeOffset.UtcNow;
}
