using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mostlylucid.Shared.Entities;

public class MarkdownFetchEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// The URL to fetch markdown content from
    /// </summary>
    [Required]
    [MaxLength(2048)]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Poll frequency in hours (e.g., 12 for 12 hours)
    /// </summary>
    public int PollFrequencyHours { get; set; }

    /// <summary>
    /// Last time the content was successfully fetched
    /// </summary>
    public DateTimeOffset? LastFetchedAt { get; set; }

    /// <summary>
    /// Last time a fetch was attempted (successful or not)
    /// </summary>
    public DateTimeOffset? LastAttemptedAt { get; set; }

    /// <summary>
    /// Number of consecutive failures
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// The fetched markdown content (cached)
    /// </summary>
    public string CachedContent { get; set; } = string.Empty;

    /// <summary>
    /// Hash of the cached content for change detection
    /// </summary>
    [MaxLength(64)]
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Error message from last failed attempt
    /// </summary>
    [MaxLength(2000)]
    public string? LastError { get; set; }

    /// <summary>
    /// The blog post that contains this fetch directive (nullable for initial fetches)
    /// </summary>
    public int? BlogPostId { get; set; }

    /// <summary>
    /// Navigation property to the blog post
    /// </summary>
    public BlogPostEntity? BlogPost { get; set; }

    /// <summary>
    /// Whether this fetch is currently enabled
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Created timestamp
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Updated timestamp
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}