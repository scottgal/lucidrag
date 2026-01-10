using HotChocolate;
using HotChocolate.Data;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using LucidRAG.Data;
using LucidRAG.Entities;

namespace LucidRAG.GraphQL;

/// <summary>
/// GraphQL Query type for the knowledge graph.
/// Provides access to entities, relationships, communities, and documents.
/// </summary>
public class KnowledgeGraphQuery
{
    /// <summary>
    /// Get all entities with optional filtering and pagination.
    /// </summary>
    [UsePaging(IncludeTotalCount = true)]
    [UseFiltering]
    [UseSorting]
    public IQueryable<ExtractedEntity> GetEntities([Service] RagDocumentsDbContext db)
        => db.Entities.AsNoTracking();

    /// <summary>
    /// Get a specific entity by ID.
    /// </summary>
    public async Task<ExtractedEntity?> GetEntity(
        Guid id,
        [Service] RagDocumentsDbContext db,
        CancellationToken ct)
        => await db.Entities
            .Include(e => e.DocumentLinks)
            .ThenInclude(l => l.Document)
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, ct);

    /// <summary>
    /// Get entities by type (e.g., "person", "organization", "concept").
    /// </summary>
    [UsePaging(IncludeTotalCount = true)]
    [UseFiltering]
    public IQueryable<ExtractedEntity> GetEntitiesByType(
        string entityType,
        [Service] RagDocumentsDbContext db)
        => db.Entities.Where(e => e.EntityType == entityType).AsNoTracking();

    /// <summary>
    /// Search entities by name or description.
    /// </summary>
    [UsePaging(IncludeTotalCount = true)]
    public IQueryable<ExtractedEntity> SearchEntities(
        string query,
        [Service] RagDocumentsDbContext db)
    {
        var lowerQuery = query.ToLowerInvariant();
        return db.Entities
            .Where(e => e.CanonicalName.ToLower().Contains(lowerQuery) ||
                        (e.Description != null && e.Description.ToLower().Contains(lowerQuery)))
            .AsNoTracking();
    }

    /// <summary>
    /// Get all relationships with optional filtering.
    /// </summary>
    [UsePaging(IncludeTotalCount = true)]
    [UseFiltering]
    [UseSorting]
    public IQueryable<EntityRelationship> GetRelationships([Service] RagDocumentsDbContext db)
        => db.EntityRelationships
            .Include(r => r.SourceEntity)
            .Include(r => r.TargetEntity)
            .AsNoTracking();

    /// <summary>
    /// Get relationships for a specific entity (both incoming and outgoing).
    /// </summary>
    public async Task<EntityConnections> GetEntityConnections(
        Guid entityId,
        [Service] RagDocumentsDbContext db,
        CancellationToken ct)
    {
        var outgoing = await db.EntityRelationships
            .Where(r => r.SourceEntityId == entityId)
            .Include(r => r.TargetEntity)
            .AsNoTracking()
            .ToListAsync(ct);

        var incoming = await db.EntityRelationships
            .Where(r => r.TargetEntityId == entityId)
            .Include(r => r.SourceEntity)
            .AsNoTracking()
            .ToListAsync(ct);

        return new EntityConnections
        {
            EntityId = entityId,
            OutgoingRelationships = outgoing,
            IncomingRelationships = incoming,
            TotalConnections = outgoing.Count + incoming.Count
        };
    }

    /// <summary>
    /// Get all communities with optional filtering.
    /// </summary>
    [UsePaging(IncludeTotalCount = true)]
    [UseFiltering]
    [UseSorting]
    public IQueryable<CommunityEntity> GetCommunities([Service] RagDocumentsDbContext db)
        => db.Communities.AsNoTracking();

    /// <summary>
    /// Get a specific community by ID with members.
    /// </summary>
    public async Task<CommunityEntity?> GetCommunity(
        Guid id,
        [Service] RagDocumentsDbContext db,
        CancellationToken ct)
        => await db.Communities
            .Include(c => c.Members)
            .ThenInclude(m => m.Entity)
            .Include(c => c.ChildCommunities)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);

    /// <summary>
    /// Get top-level communities (hierarchy root).
    /// </summary>
    [UsePaging(IncludeTotalCount = true)]
    public IQueryable<CommunityEntity> GetTopLevelCommunities([Service] RagDocumentsDbContext db)
        => db.Communities
            .Where(c => c.ParentCommunityId == null)
            .OrderByDescending(c => c.EntityCount)
            .AsNoTracking();

    /// <summary>
    /// Get graph statistics.
    /// </summary>
    public async Task<GraphStats> GetGraphStats(
        [Service] RagDocumentsDbContext db,
        CancellationToken ct)
    {
        var entityCount = await db.Entities.CountAsync(ct);
        var relationshipCount = await db.EntityRelationships.CountAsync(ct);
        var communityCount = await db.Communities.CountAsync(ct);
        var documentCount = await db.Documents.CountAsync(ct);

        var entityTypes = await db.Entities
            .GroupBy(e => e.EntityType)
            .Select(g => new TypeCount { Type = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var relationshipTypes = await db.EntityRelationships
            .GroupBy(r => r.RelationshipType)
            .Select(g => new TypeCount { Type = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return new GraphStats
        {
            TotalEntities = entityCount,
            TotalRelationships = relationshipCount,
            TotalCommunities = communityCount,
            TotalDocuments = documentCount,
            EntityTypeBreakdown = entityTypes,
            RelationshipTypeBreakdown = relationshipTypes
        };
    }

    /// <summary>
    /// Get documents in the system.
    /// </summary>
    [UsePaging(IncludeTotalCount = true)]
    [UseFiltering]
    [UseSorting]
    public IQueryable<DocumentEntity> GetDocuments([Service] RagDocumentsDbContext db)
        => db.Documents.Include(d => d.Collection).AsNoTracking();

    /// <summary>
    /// Get a specific document by ID.
    /// </summary>
    public async Task<DocumentEntity?> GetDocument(
        Guid id,
        [Service] RagDocumentsDbContext db,
        CancellationToken ct)
        => await db.Documents
            .Include(d => d.Collection)
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, ct);

    /// <summary>
    /// Find paths between two entities (up to maxHops).
    /// </summary>
    public async Task<List<EntityPath>> FindPaths(
        Guid fromEntityId,
        Guid toEntityId,
        int maxHops,
        [Service] RagDocumentsDbContext db,
        CancellationToken ct)
    {
        maxHops = Math.Clamp(maxHops, 1, 5);
        var paths = new List<EntityPath>();
        var visited = new Dictionary<Guid, (Guid Parent, string Relation)>();
        var queue = new Queue<(Guid EntityId, int Depth)>();

        queue.Enqueue((fromEntityId, 0));
        visited[fromEntityId] = (Guid.Empty, "start");

        while (queue.Count > 0 && paths.Count < 5)
        {
            var (current, depth) = queue.Dequeue();

            if (current == toEntityId)
            {
                var path = new List<PathStep>();
                var node = toEntityId;
                while (node != fromEntityId)
                {
                    var (parent, relation) = visited[node];
                    var entity = await db.Entities.AsNoTracking()
                        .FirstOrDefaultAsync(e => e.Id == node, ct);
                    path.Insert(0, new PathStep
                    {
                        EntityId = node,
                        EntityName = entity?.CanonicalName ?? "Unknown",
                        Relationship = relation
                    });
                    node = parent;
                }
                var fromEntity = await db.Entities.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Id == fromEntityId, ct);
                path.Insert(0, new PathStep
                {
                    EntityId = fromEntityId,
                    EntityName = fromEntity?.CanonicalName ?? "Unknown",
                    Relationship = "start"
                });
                paths.Add(new EntityPath { Steps = path, Length = path.Count - 1 });
                continue;
            }

            if (depth >= maxHops) continue;

            var neighbors = await db.EntityRelationships
                .Where(r => r.SourceEntityId == current || r.TargetEntityId == current)
                .AsNoTracking()
                .ToListAsync(ct);

            foreach (var rel in neighbors)
            {
                var neighbor = rel.SourceEntityId == current ? rel.TargetEntityId : rel.SourceEntityId;
                if (!visited.ContainsKey(neighbor))
                {
                    visited[neighbor] = (current, rel.RelationshipType);
                    queue.Enqueue((neighbor, depth + 1));
                }
            }
        }

        return paths;
    }
}

/// <summary>
/// Entity connections (incoming and outgoing relationships).
/// </summary>
public class EntityConnections
{
    public Guid EntityId { get; set; }
    public List<EntityRelationship> OutgoingRelationships { get; set; } = [];
    public List<EntityRelationship> IncomingRelationships { get; set; } = [];
    public int TotalConnections { get; set; }
}

/// <summary>
/// Graph statistics summary.
/// </summary>
public class GraphStats
{
    public int TotalEntities { get; set; }
    public int TotalRelationships { get; set; }
    public int TotalCommunities { get; set; }
    public int TotalDocuments { get; set; }
    public List<TypeCount> EntityTypeBreakdown { get; set; } = [];
    public List<TypeCount> RelationshipTypeBreakdown { get; set; } = [];
}

/// <summary>
/// Type count for breakdowns.
/// </summary>
public class TypeCount
{
    public string Type { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>
/// A path between two entities.
/// </summary>
public class EntityPath
{
    public List<PathStep> Steps { get; set; } = [];
    public int Length { get; set; }
}

/// <summary>
/// A step in an entity path.
/// </summary>
public class PathStep
{
    public Guid EntityId { get; set; }
    public string EntityName { get; set; } = "";
    public string Relationship { get; set; } = "";
}
