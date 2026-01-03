using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mostlylucid.Shared.Entities;

public class AnnouncementEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// Unique key to identify this announcement (for dismiss tracking)
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Raw markdown content
    /// </summary>
    [Required]
    public string Markdown { get; set; } = string.Empty;

    /// <summary>
    /// Rendered HTML content
    /// </summary>
    public string HtmlContent { get; set; } = string.Empty;

    /// <summary>
    /// Language code (e.g., "en", "es", "fr")
    /// </summary>
    [Required]
    [MaxLength(10)]
    public string Language { get; set; } = "en";

    /// <summary>
    /// Whether this announcement is currently active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Priority for ordering (higher = more important)
    /// </summary>
    public int Priority { get; set; } = 0;

    /// <summary>
    /// Optional start date for the announcement
    /// </summary>
    public DateTimeOffset? StartDate { get; set; }

    /// <summary>
    /// Optional end date for the announcement
    /// </summary>
    public DateTimeOffset? EndDate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
