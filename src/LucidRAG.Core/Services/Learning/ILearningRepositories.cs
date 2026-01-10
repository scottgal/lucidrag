namespace LucidRAG.Core.Services.Learning;

/// <summary>
/// Document repository interface for learning queries.
/// </summary>
public interface IDocumentRepository
{
    Task<List<DocumentEntity>> FindByConfidenceAsync(
        double maxConfidence,
        TimeSpan minAge,
        CancellationToken ct = default);

    Task<List<DocumentEntity>> FindByEntityCountAsync(
        int maxEntityCount,
        TimeSpan minAge,
        CancellationToken ct = default);

    Task<List<DocumentEntity>> FindWithNegativeFeedbackAsync(
        TimeSpan minAge,
        CancellationToken ct = default);

    Task<List<DocumentEntity>> FindProcessedBeforeAsync(
        DateTime cutoffDate,
        CancellationToken ct = default);
}

/// <summary>
/// Entity repository interface.
/// </summary>
public interface IEntityRepository
{
    Task<List<ExtractedEntity>> GetEntitiesAsync(Guid documentId, CancellationToken ct = default);
    Task<List<EntityRelationship>> GetRelationshipsAsync(Guid documentId, CancellationToken ct = default);
    Task ReplaceEntitiesAsync(Guid documentId, List<ExtractedEntity> entities, CancellationToken ct = default);
}

/// <summary>
/// Evidence repository interface.
/// </summary>
public interface IEvidenceRepository
{
    Task<EvidenceArtifact?> GetAsync(Guid documentId, string evidenceType, CancellationToken ct = default);
    Task SaveAsync(EvidenceArtifact evidence, CancellationToken ct = default);
}

/// <summary>
/// Document entity model.
/// </summary>
public class DocumentEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string FilePath { get; set; }
    public string? ContentType { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public required string TenantId { get; set; }
    public string? ContentHash { get; set; }
}

/// <summary>
/// Evidence artifact model.
/// </summary>
public class EvidenceArtifact
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public required string Type { get; set; }
    public required string Content { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>
/// Extracted entity model.
/// </summary>
public class ExtractedEntity
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public double Confidence { get; init; }
}

/// <summary>
/// Entity relationship model.
/// </summary>
public class EntityRelationship
{
    public required string Source { get; init; }
    public required string Target { get; init; }
    public required string RelationType { get; init; }
}
