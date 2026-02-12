namespace LucidRAG.Entities;

public class WidgetConfigEntity
{
    public Guid Id { get; set; }
    public Guid ApiKeyId { get; set; }

    // Appearance
    public string? Theme { get; set; }
    public string? AccentColor { get; set; }
    public int BorderRadius { get; set; } = 8;
    public string? FontFamily { get; set; }
    public string? CustomCss { get; set; }
    public string? LogoUrl { get; set; }
    public bool ShowBranding { get; set; } = true;

    // Behaviour
    public string Position { get; set; } = "inline";
    public string Mode { get; set; } = "both";
    public string? Placeholder { get; set; }
    public int MaxResults { get; set; } = 5;
    public string CorpusStyle { get; set; } = "hidden";

    // Hosted page
    public string? PageTitle { get; set; }
    public string? PageDescription { get; set; }
    public string? FaviconUrl { get; set; }
    public string? WelcomeMessage { get; set; }

    // Timestamps
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public ApiKeyEntity ApiKey { get; set; } = null!;
}
