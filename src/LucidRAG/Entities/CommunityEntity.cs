namespace LucidRAG.Entities;

/// <summary>
/// A detected community of related entities in the knowledge graph.
/// Communities are clusters of entities that frequently co-occur or are semantically related.
/// </summary>
public class CommunityEntity
{
    public Guid Id { get; set; }

    /// <summary>
    /// LLM-generated name for this community (e.g., "Image Processing Techniques")
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// LLM-generated summary describing what this community represents
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>
    /// Extracted features common to this community (JSON)
    /// Contains: dominant_types, key_terms, embedding_centroid, etc.
    /// </summary>
    public string? Features { get; set; }

    /// <summary>
    /// Community detection algorithm used (louvain, label_propagation, etc.)
    /// </summary>
    public string Algorithm { get; set; } = "louvain";

    /// <summary>
    /// Hierarchy level (0 = top-level, higher = more specific)
    /// </summary>
    public int Level { get; set; }

    /// <summary>
    /// Parent community ID for hierarchical communities
    /// </summary>
    public Guid? ParentCommunityId { get; set; }

    /// <summary>
    /// Number of entities in this community
    /// </summary>
    public int EntityCount { get; set; }

    /// <summary>
    /// Average internal edge weight (cohesion measure)
    /// </summary>
    public float Cohesion { get; set; }

    /// <summary>
    /// Representative embedding for the community (centroid of member embeddings)
    /// </summary>
    public float[]? Embedding { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    // Navigation
    public CommunityEntity? ParentCommunity { get; set; }
    public ICollection<CommunityEntity> ChildCommunities { get; set; } = [];
    public ICollection<CommunityMembership> Members { get; set; } = [];
}

/// <summary>
/// Links entities to their detected communities (many-to-many)
/// </summary>
public class CommunityMembership
{
    public Guid CommunityId { get; set; }
    public Guid EntityId { get; set; }

    /// <summary>
    /// How central this entity is to the community (0-1)
    /// </summary>
    public float Centrality { get; set; }

    /// <summary>
    /// Whether this entity is a "representative" for the community
    /// </summary>
    public bool IsRepresentative { get; set; }

    // Navigation
    public CommunityEntity? Community { get; set; }
    public ExtractedEntity? Entity { get; set; }
}

/// <summary>
/// Features extracted for a community
/// </summary>
public record CommunityFeatures
{
    /// <summary>
    /// Most common entity types in this community
    /// </summary>
    public Dictionary<string, int> DominantTypes { get; init; } = new();

    /// <summary>
    /// Key terms that appear frequently across community entities
    /// </summary>
    public List<string> KeyTerms { get; init; } = [];

    /// <summary>
    /// Representative entities (highest centrality)
    /// </summary>
    public List<string> Representatives { get; init; } = [];

    /// <summary>
    /// Documents that mention entities in this community
    /// </summary>
    public List<Guid> SourceDocuments { get; init; } = [];

    /// <summary>
    /// Average embedding similarity within the community
    /// </summary>
    public float InternalSimilarity { get; init; }

    /// <summary>
    /// Total relationship strength within the community
    /// </summary>
    public float TotalEdgeWeight { get; init; }
}
