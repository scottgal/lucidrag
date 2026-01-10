using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Filters;
using LucidRAG.Models;

namespace LucidRAG.Controllers.Api;

[ApiController]
[Route("api/collections")]
[DemoModeWriteBlock] // Blocks POST/PUT/DELETE when demo mode is enabled
public class CollectionsController(
    RagDocumentsDbContext db,
    ILogger<CollectionsController> logger) : ControllerBase
{

    /// <summary>
    /// List all collections with document counts and processing status
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct = default)
    {
        var collections = await db.Collections
            .Include(c => c.Documents)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new
            {
                id = c.Id,
                name = c.Name,
                description = c.Description,
                documentCount = c.Documents.Count,
                completedCount = c.Documents.Count(d => d.Status == DocumentStatus.Completed),
                processingCount = c.Documents.Count(d => d.Status == DocumentStatus.Processing),
                pendingCount = c.Documents.Count(d => d.Status == DocumentStatus.Pending),
                failedCount = c.Documents.Count(d => d.Status == DocumentStatus.Failed),
                entityCount = c.Documents.Sum(d => d.EntityCount),
                segmentCount = c.Documents.Sum(d => d.SegmentCount),
                createdAt = c.CreatedAt,
                updatedAt = c.UpdatedAt
            })
            .ToListAsync(ct);

        return Ok(new { collections });
    }

    /// <summary>
    /// Get a single collection with details
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct = default)
    {
        var collection = await db.Collections
            .Include(c => c.Documents)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (collection is null)
        {
            return NotFound(new { error = "Collection not found" });
        }

        return Ok(new
        {
            id = collection.Id,
            name = collection.Name,
            description = collection.Description,
            settings = collection.Settings,
            documentCount = collection.Documents.Count,
            entityCount = collection.Documents.Sum(d => d.EntityCount),
            segmentCount = collection.Documents.Sum(d => d.SegmentCount),
            documents = collection.Documents.Select(d => new
            {
                id = d.Id,
                name = d.Name,
                status = d.Status.ToString().ToLowerInvariant(),
                segmentCount = d.SegmentCount,
                entityCount = d.EntityCount,
                createdAt = d.CreatedAt
            }),
            createdAt = collection.CreatedAt,
            updatedAt = collection.UpdatedAt
        });
    }

    /// <summary>
    /// Create a new collection
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCollectionRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Name is required" });
        }

        // Check for duplicate name
        var exists = await db.Collections.AnyAsync(c => c.Name == request.Name, ct);
        if (exists)
        {
            return Conflict(new { error = "A collection with this name already exists" });
        }

        var collection = new CollectionEntity
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            Settings = request.Settings
        };

        db.Collections.Add(collection);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Created collection {CollectionId} with name {Name}", collection.Id, collection.Name);

        return CreatedAtAction(nameof(Get), new { id = collection.Id }, new
        {
            id = collection.Id,
            name = collection.Name,
            description = collection.Description,
            createdAt = collection.CreatedAt
        });
    }

    /// <summary>
    /// Update a collection
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCollectionRequest request, CancellationToken ct = default)
    {
        var collection = await db.Collections.FindAsync([id], ct);
        if (collection is null)
        {
            return NotFound(new { error = "Collection not found" });
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            // Check for duplicate name (excluding current)
            var exists = await db.Collections.AnyAsync(c => c.Name == request.Name && c.Id != id, ct);
            if (exists)
            {
                return Conflict(new { error = "A collection with this name already exists" });
            }
            collection.Name = request.Name.Trim();
        }

        if (request.Description is not null)
        {
            collection.Description = request.Description.Trim();
        }

        if (request.Settings is not null)
        {
            collection.Settings = request.Settings;
        }

        collection.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Updated collection {CollectionId}", id);

        return Ok(new
        {
            id = collection.Id,
            name = collection.Name,
            description = collection.Description,
            settings = collection.Settings,
            updatedAt = collection.UpdatedAt
        });
    }

    /// <summary>
    /// Delete a collection (cascades to documents)
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var collection = await db.Collections
            .Include(c => c.Documents)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (collection is null)
        {
            return NotFound(new { error = "Collection not found" });
        }

        var documentCount = collection.Documents.Count;

        db.Collections.Remove(collection);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Deleted collection {CollectionId} with {DocumentCount} documents", id, documentCount);

        return Ok(new
        {
            success = true,
            deletedDocuments = documentCount
        });
    }

    /// <summary>
    /// Add documents to a collection
    /// </summary>
    [HttpPost("{id:guid}/documents")]
    public async Task<IActionResult> AddDocuments(Guid id, [FromBody] AddDocumentsRequest request, CancellationToken ct = default)
    {
        var collection = await db.Collections.FindAsync([id], ct);
        if (collection is null)
        {
            return NotFound(new { error = "Collection not found" });
        }

        var documents = await db.Documents
            .Where(d => request.DocumentIds.Contains(d.Id))
            .ToListAsync(ct);

        if (documents.Count == 0)
        {
            return BadRequest(new { error = "No valid documents found" });
        }

        foreach (var doc in documents)
        {
            doc.CollectionId = id;
        }

        collection.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Added {Count} documents to collection {CollectionId}", documents.Count, id);

        return Ok(new
        {
            success = true,
            added = documents.Count,
            documentIds = documents.Select(d => d.Id)
        });
    }

    /// <summary>
    /// Remove documents from a collection (moves to uncategorized)
    /// </summary>
    [HttpDelete("{id:guid}/documents")]
    public async Task<IActionResult> RemoveDocuments(Guid id, [FromBody] RemoveDocumentsRequest request, CancellationToken ct = default)
    {
        var documents = await db.Documents
            .Where(d => d.CollectionId == id && request.DocumentIds.Contains(d.Id))
            .ToListAsync(ct);

        if (documents.Count == 0)
        {
            return BadRequest(new { error = "No matching documents found in this collection" });
        }

        foreach (var doc in documents)
        {
            doc.CollectionId = null;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Removed {Count} documents from collection {CollectionId}", documents.Count, id);

        return Ok(new
        {
            success = true,
            removed = documents.Count,
            documentIds = documents.Select(d => d.Id)
        });
    }
}
