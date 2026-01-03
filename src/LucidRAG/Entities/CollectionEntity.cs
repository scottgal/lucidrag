namespace LucidRAG.Entities;

public class CollectionEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Settings { get; set; } // JSON
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public ICollection<DocumentEntity> Documents { get; set; } = [];
    public ICollection<ConversationEntity> Conversations { get; set; } = [];
}
