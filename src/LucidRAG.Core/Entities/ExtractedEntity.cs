namespace LucidRAG.Entities;

public class ExtractedEntity
{
    public Guid Id { get; set; }
    public required string CanonicalName { get; set; }
    public required string EntityType { get; set; } // class, function, person, org, concept
    public string? Description { get; set; }
    public string[] Aliases { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public ICollection<DocumentEntityLink> DocumentLinks { get; set; } = [];
    public ICollection<EntityRelationship> OutgoingRelationships { get; set; } = [];
    public ICollection<EntityRelationship> IncomingRelationships { get; set; } = [];
}
