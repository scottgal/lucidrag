using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LucidRAG.Data;

namespace LucidRAG.Controllers.Api;

[ApiController]
[Route("api/graph")]
public class GraphController(
    RagDocumentsDbContext db,
    ILogger<GraphController> logger) : ControllerBase
{
    /// <summary>
    /// Get graph health statistics for all collections
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken ct = default)
    {
        var entities = await db.Entities.CountAsync(ct);
        var relationships = await db.EntityRelationships.CountAsync(ct);
        var documents = await db.Documents.CountAsync(ct);
        var documentsWithEntities = await db.DocumentEntityLinks
            .Select(l => l.DocumentId)
            .Distinct()
            .CountAsync(ct);

        // Find orphan entities (no relationships)
        var entitiesWithRelationships = await db.EntityRelationships
            .Select(r => r.SourceEntityId)
            .Union(db.EntityRelationships.Select(r => r.TargetEntityId))
            .Distinct()
            .CountAsync(ct);

        var orphans = entities - entitiesWithRelationships;
        var coverage = documents > 0 ? (documentsWithEntities * 100.0 / documents) : 0;

        return Ok(new
        {
            nodes = entities,
            edges = relationships,
            entityCoverage = $"{coverage:F0}%",
            orphans = orphans,
            documents = documents,
            documentsWithEntities = documentsWithEntities
        });
    }

    /// <summary>
    /// Get full graph data for D3.js visualization
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetGraph(
        [FromQuery] Guid? collectionId = null,
        [FromQuery] int maxNodes = 100,
        CancellationToken ct = default)
    {
        var entitiesQuery = db.Entities.AsQueryable();
        var relationshipsQuery = db.EntityRelationships.AsQueryable();

        if (collectionId.HasValue)
        {
            var entityIds = await db.DocumentEntityLinks
                .Where(l => l.Document!.CollectionId == collectionId)
                .Select(l => l.EntityId)
                .Distinct()
                .ToListAsync(ct);

            entitiesQuery = entitiesQuery.Where(e => entityIds.Contains(e.Id));
            relationshipsQuery = relationshipsQuery.Where(r =>
                entityIds.Contains(r.SourceEntityId) || entityIds.Contains(r.TargetEntityId));
        }

        var entities = await entitiesQuery
            .OrderByDescending(e => db.DocumentEntityLinks.Count(l => l.EntityId == e.Id))
            .Take(maxNodes)
            .Select(e => new
            {
                id = e.Id,
                name = e.CanonicalName,
                type = e.EntityType,
                description = e.Description,
                aliases = e.Aliases,
                mentionCount = db.DocumentEntityLinks.Count(l => l.EntityId == e.Id)
            })
            .ToListAsync(ct);

        var entityIds2 = entities.Select(e => e.id).ToHashSet();

        var relationships = await relationshipsQuery
            .Where(r => entityIds2.Contains(r.SourceEntityId) && entityIds2.Contains(r.TargetEntityId))
            .Select(r => new
            {
                source = r.SourceEntityId,
                target = r.TargetEntityId,
                type = r.RelationshipType,
                strength = r.Strength
            })
            .ToListAsync(ct);

        return Ok(new
        {
            nodes = entities,
            links = relationships
        });
    }

    /// <summary>
    /// Get subgraph centered on a specific entity
    /// </summary>
    [HttpGet("subgraph/{entityId:guid}")]
    public async Task<IActionResult> GetSubgraph(
        Guid entityId,
        [FromQuery] int maxHops = 2,
        [FromQuery] string? edgeFilter = null,
        CancellationToken ct = default)
    {
        var centerEntity = await db.Entities.FindAsync([entityId], ct);
        if (centerEntity is null)
            return NotFound(new { error = "Entity not found" });

        // BFS to find connected entities within maxHops
        var visited = new HashSet<Guid> { entityId };
        var frontier = new HashSet<Guid> { entityId };
        var allRelationships = new List<(Guid Source, Guid Target, string Type, float Strength)>();

        for (int hop = 0; hop < maxHops && frontier.Count > 0; hop++)
        {
            var relationshipsQuery = db.EntityRelationships
                .Where(r => frontier.Contains(r.SourceEntityId) || frontier.Contains(r.TargetEntityId));

            if (!string.IsNullOrEmpty(edgeFilter) && edgeFilter != "all")
            {
                relationshipsQuery = relationshipsQuery.Where(r => r.RelationshipType == edgeFilter);
            }

            var relationships = await relationshipsQuery.ToListAsync(ct);

            var newFrontier = new HashSet<Guid>();
            foreach (var rel in relationships)
            {
                allRelationships.Add((rel.SourceEntityId, rel.TargetEntityId, rel.RelationshipType, rel.Strength));

                if (!visited.Contains(rel.SourceEntityId))
                {
                    visited.Add(rel.SourceEntityId);
                    newFrontier.Add(rel.SourceEntityId);
                }
                if (!visited.Contains(rel.TargetEntityId))
                {
                    visited.Add(rel.TargetEntityId);
                    newFrontier.Add(rel.TargetEntityId);
                }
            }
            frontier = newFrontier;
        }

        var entities = await db.Entities
            .Where(e => visited.Contains(e.Id))
            .Select(e => new
            {
                id = e.Id,
                name = e.CanonicalName,
                type = e.EntityType,
                description = e.Description,
                isCenter = e.Id == entityId
            })
            .ToListAsync(ct);

        return Ok(new
        {
            center = centerEntity.CanonicalName,
            nodes = entities,
            links = allRelationships.Distinct().Select(r => new
            {
                source = r.Source,
                target = r.Target,
                type = r.Type,
                strength = r.Strength
            })
        });
    }

    /// <summary>
    /// Get entity details with supporting chunks
    /// </summary>
    [HttpGet("entities/{entityId:guid}")]
    public async Task<IActionResult> GetEntity(Guid entityId, CancellationToken ct = default)
    {
        var entity = await db.Entities
            .Include(e => e.DocumentLinks)
            .ThenInclude(l => l.Document)
            .FirstOrDefaultAsync(e => e.Id == entityId, ct);

        if (entity is null)
            return NotFound(new { error = "Entity not found" });

        // Get relationships
        var relationships = await db.EntityRelationships
            .Where(r => r.SourceEntityId == entityId || r.TargetEntityId == entityId)
            .Include(r => r.SourceEntity)
            .Include(r => r.TargetEntity)
            .Select(r => new
            {
                type = r.RelationshipType,
                direction = r.SourceEntityId == entityId ? "outgoing" : "incoming",
                otherEntity = r.SourceEntityId == entityId
                    ? new { id = r.TargetEntityId, name = r.TargetEntity!.CanonicalName, type = r.TargetEntity.EntityType }
                    : new { id = r.SourceEntityId, name = r.SourceEntity!.CanonicalName, type = r.SourceEntity.EntityType },
                strength = r.Strength
            })
            .ToListAsync(ct);

        return Ok(new
        {
            id = entity.Id,
            name = entity.CanonicalName,
            type = entity.EntityType,
            description = entity.Description,
            aliases = entity.Aliases,
            documents = entity.DocumentLinks.Select(l => new
            {
                id = l.DocumentId,
                name = l.Document?.Name,
                mentionCount = l.MentionCount
            }),
            relationships = relationships
        });
    }

    /// <summary>
    /// Search entities
    /// </summary>
    [HttpGet("entities")]
    public async Task<IActionResult> SearchEntities(
        [FromQuery] string? query = null,
        [FromQuery] string? type = null,
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        var entitiesQuery = db.Entities.AsQueryable();

        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.ToLowerInvariant();
            entitiesQuery = entitiesQuery.Where(e =>
                e.CanonicalName.ToLower().Contains(q) ||
                (e.Description != null && e.Description.ToLower().Contains(q)));
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            entitiesQuery = entitiesQuery.Where(e => e.EntityType == type);
        }

        var entities = await entitiesQuery
            .OrderByDescending(e => db.DocumentEntityLinks.Count(l => l.EntityId == e.Id))
            .Take(limit)
            .Select(e => new
            {
                id = e.Id,
                name = e.CanonicalName,
                type = e.EntityType,
                description = e.Description,
                mentionCount = db.DocumentEntityLinks.Count(l => l.EntityId == e.Id)
            })
            .ToListAsync(ct);

        return Ok(new { entities });
    }

    /// <summary>
    /// Get graph paths between two entities
    /// </summary>
    [HttpGet("paths")]
    public async Task<IActionResult> GetPaths(
        [FromQuery] Guid fromEntityId,
        [FromQuery] Guid toEntityId,
        [FromQuery] int maxHops = 3,
        CancellationToken ct = default)
    {
        // Simple BFS path finding
        var paths = new List<List<string>>();
        var visited = new Dictionary<Guid, (Guid Parent, string Relation)>();
        var queue = new Queue<(Guid EntityId, int Depth)>();

        queue.Enqueue((fromEntityId, 0));
        visited[fromEntityId] = (Guid.Empty, "start");

        while (queue.Count > 0 && paths.Count < 5)
        {
            var (current, depth) = queue.Dequeue();

            if (current == toEntityId)
            {
                // Reconstruct path
                var path = new List<string>();
                var node = toEntityId;
                while (node != fromEntityId)
                {
                    var (parent, relation) = visited[node];
                    var entityName = (await db.Entities.FindAsync([node], ct))?.CanonicalName ?? "?";
                    path.Insert(0, $"-({relation})-> {entityName}");
                    node = parent;
                }
                var startName = (await db.Entities.FindAsync([fromEntityId], ct))?.CanonicalName ?? "?";
                path.Insert(0, startName);
                paths.Add(path);
                continue;
            }

            if (depth >= maxHops) continue;

            var neighbors = await db.EntityRelationships
                .Where(r => r.SourceEntityId == current || r.TargetEntityId == current)
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

        return Ok(new
        {
            paths = paths.Select(p => string.Join(" ", p))
        });
    }
}
