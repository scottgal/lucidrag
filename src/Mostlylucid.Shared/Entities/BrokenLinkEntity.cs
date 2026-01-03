using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mostlylucid.Shared.Entities;

/// <summary>
/// Tracks external links found in blog posts and their archive.org versions
/// </summary>
[Table("broken_links", Schema = "mostlylucid")]
public class BrokenLinkEntity
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    /// <summary>
    /// The original external URL found in blog content
    /// </summary>
    [Required]
    [Column("original_url")]
    [MaxLength(2048)]
    public string OriginalUrl { get; set; } = string.Empty;

    /// <summary>
    /// The archive.org URL for the original content (null if not yet fetched)
    /// </summary>
    [Column("archive_url")]
    [MaxLength(2048)]
    public string? ArchiveUrl { get; set; }

    /// <summary>
    /// Whether the original URL is currently broken (returns 404 or other error)
    /// </summary>
    [Column("is_broken")]
    public bool IsBroken { get; set; } = false;

    /// <summary>
    /// The HTTP status code from the last check (e.g., 200, 404, 500)
    /// </summary>
    [Column("last_status_code")]
    public int? LastStatusCode { get; set; }

    /// <summary>
    /// When the URL was first discovered
    /// </summary>
    [Column("discovered_at")]
    public DateTimeOffset DiscoveredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the URL was last checked for validity
    /// </summary>
    [Column("last_checked_at")]
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>
    /// Number of consecutive check failures
    /// </summary>
    [Column("consecutive_failures")]
    public int ConsecutiveFailures { get; set; } = 0;

    /// <summary>
    /// Whether archive.org has been checked for this URL
    /// </summary>
    [Column("archive_checked")]
    public bool ArchiveChecked { get; set; } = false;

    /// <summary>
    /// When archive.org was last checked for this URL
    /// </summary>
    [Column("archive_checked_at")]
    public DateTimeOffset? ArchiveCheckedAt { get; set; }

    /// <summary>
    /// Error message from the last check attempt (if any)
    /// </summary>
    [Column("last_error")]
    [MaxLength(1000)]
    public string? LastError { get; set; }

    /// <summary>
    /// The page URL where this link was first discovered (e.g., /blog/my-post)
    /// Used to look up publish date for archive.org timestamp
    /// </summary>
    [Column("source_page_url")]
    [MaxLength(500)]
    public string? SourcePageUrl { get; set; }
}
