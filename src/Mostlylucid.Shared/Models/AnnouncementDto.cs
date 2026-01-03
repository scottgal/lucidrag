using System.ComponentModel.DataAnnotations;

namespace Mostlylucid.Shared.Models;

public class AnnouncementDto
{
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Key { get; set; } = string.Empty;

    [Required]
    public string Markdown { get; set; } = string.Empty;

    public string HtmlContent { get; set; } = string.Empty;

    [MaxLength(10)]
    public string Language { get; set; } = "en";

    public bool IsActive { get; set; } = true;

    public int Priority { get; set; } = 0;

    public DateTimeOffset? StartDate { get; set; }

    public DateTimeOffset? EndDate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public class CreateAnnouncementRequest
{
    [Required]
    [MaxLength(100)]
    public string Key { get; set; } = string.Empty;

    [Required]
    public string Markdown { get; set; } = string.Empty;

    [MaxLength(10)]
    public string Language { get; set; } = "en";

    public bool IsActive { get; set; } = true;

    public int Priority { get; set; } = 0;

    public DateTimeOffset? StartDate { get; set; }

    public DateTimeOffset? EndDate { get; set; }
}
