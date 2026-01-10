namespace LucidRAG.Entities;

public class ConversationEntity
{
    public Guid Id { get; set; }
    public Guid? CollectionId { get; set; }
    public string? Title { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public CollectionEntity? Collection { get; set; }
    public ICollection<ConversationMessage> Messages { get; set; } = [];
}

public class ConversationMessage
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public required string Role { get; set; } // user, assistant, system
    public required string Content { get; set; }
    public string? Metadata { get; set; } // JSON - sources, entities referenced
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public ConversationEntity? Conversation { get; set; }
}
