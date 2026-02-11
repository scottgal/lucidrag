namespace LucidRAG.Entities;

public class ApiKeyCollectionLink
{
    public Guid Id { get; set; }
    public Guid ApiKeyId { get; set; }
    public Guid CollectionId { get; set; }

    /// <summary>
    ///     Display label for this collection in the widget (e.g., "Blog", "Docs").
    /// </summary>
    public string? Label { get; set; }

    public int SortOrder { get; set; }
    public DateTimeOffset LinkedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public ApiKeyEntity ApiKey { get; set; } = null!;
    public CollectionEntity Collection { get; set; } = null!;
}
