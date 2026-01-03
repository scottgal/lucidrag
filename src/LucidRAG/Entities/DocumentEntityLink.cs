namespace LucidRAG.Entities;

public class DocumentEntityLink
{
    public Guid DocumentId { get; set; }
    public Guid EntityId { get; set; }
    public int MentionCount { get; set; } = 1;
    public string[] SegmentIds { get; set; } = [];
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public DocumentEntity? Document { get; set; }
    public ExtractedEntity? Entity { get; set; }
}
