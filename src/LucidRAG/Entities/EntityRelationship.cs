namespace LucidRAG.Entities;

public class EntityRelationship
{
    public Guid Id { get; set; }
    public Guid SourceEntityId { get; set; }
    public Guid TargetEntityId { get; set; }
    public required string RelationshipType { get; set; } // calls, uses, extends, related_to
    public float Strength { get; set; } = 1.0f;
    public Guid[] SourceDocuments { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public ExtractedEntity? SourceEntity { get; set; }
    public ExtractedEntity? TargetEntity { get; set; }
}
