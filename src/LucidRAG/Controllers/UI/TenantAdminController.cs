using LucidRAG.Authorization;
using LucidRAG.Data;
using LucidRAG.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LucidRAG.Controllers.UI;

/// <summary>
/// Tenant administration controller for managing collections and tenant settings.
/// Requires TenantAdmin or SystemAdmin role.
/// </summary>
[Route("admin/tenant")]
[Authorize(Roles = $"{Roles.TenantAdmin},{Roles.SystemAdmin}")]
public class TenantAdminController(
    RagDocumentsDbContext db,
    ILogger<TenantAdminController> logger) : Controller
{
    /// <summary>
    /// Tenant admin dashboard.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var stats = new TenantDashboardViewModel
        {
            TotalCollections = await db.Collections.CountAsync(ct),
            TotalDocuments = await db.Documents.CountAsync(ct),
            CompletedDocuments = await db.Documents.CountAsync(d => d.Status == DocumentStatus.Completed, ct),
            ProcessingDocuments = await db.Documents.CountAsync(d => d.Status == DocumentStatus.Processing, ct),
            FailedDocuments = await db.Documents.CountAsync(d => d.Status == DocumentStatus.Failed, ct),
            TotalEntities = await db.Entities.CountAsync(ct),
            TotalRelationships = await db.EntityRelationships.CountAsync(ct)
        };

        return View(stats);
    }

    /// <summary>
    /// List all collections.
    /// </summary>
    [HttpGet("collections")]
    public async Task<IActionResult> Collections(CancellationToken ct = default)
    {
        var collections = await db.Collections
            .Include(c => c.Documents)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new CollectionListItemViewModel
            {
                Id = c.Id,
                Name = c.Name,
                Description = c.Description,
                DocumentCount = c.Documents.Count,
                CompletedCount = c.Documents.Count(d => d.Status == DocumentStatus.Completed),
                ProcessingCount = c.Documents.Count(d => d.Status == DocumentStatus.Processing),
                FailedCount = c.Documents.Count(d => d.Status == DocumentStatus.Failed),
                EntityCount = c.Documents.Sum(d => d.EntityCount),
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt
            })
            .ToListAsync(ct);

        return View(collections);
    }

    /// <summary>
    /// Create collection form.
    /// </summary>
    [HttpGet("collections/create")]
    public IActionResult CreateCollection()
    {
        return View(new CollectionFormViewModel());
    }

    /// <summary>
    /// Create collection handler.
    /// </summary>
    [HttpPost("collections/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateCollection(CollectionFormViewModel model, CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        // Check for duplicate name
        var exists = await db.Collections.AnyAsync(c => c.Name == model.Name, ct);
        if (exists)
        {
            ModelState.AddModelError(nameof(model.Name), "A collection with this name already exists");
            return View(model);
        }

        var collection = new CollectionEntity
        {
            Id = Guid.NewGuid(),
            Name = model.Name!.Trim(),
            Description = model.Description?.Trim()
        };

        db.Collections.Add(collection);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Created collection {CollectionId} with name {Name}", collection.Id, collection.Name);

        TempData["Success"] = $"Collection '{collection.Name}' created successfully";
        return RedirectToAction(nameof(Collections));
    }

    /// <summary>
    /// Edit collection form.
    /// </summary>
    [HttpGet("collections/{id:guid}/edit")]
    public async Task<IActionResult> EditCollection(Guid id, CancellationToken ct = default)
    {
        var collection = await db.Collections.FindAsync([id], ct);
        if (collection is null)
        {
            return NotFound();
        }

        var model = new CollectionFormViewModel
        {
            Id = collection.Id,
            Name = collection.Name,
            Description = collection.Description,
            IsDefault = collection.IsDefault
        };

        return View(model);
    }

    /// <summary>
    /// Edit collection handler.
    /// </summary>
    [HttpPost("collections/{id:guid}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditCollection(Guid id, CollectionFormViewModel model, CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var collection = await db.Collections.FindAsync([id], ct);
        if (collection is null)
        {
            return NotFound();
        }

        // Check for duplicate name
        var exists = await db.Collections.AnyAsync(c => c.Name == model.Name && c.Id != id, ct);
        if (exists)
        {
            ModelState.AddModelError(nameof(model.Name), "A collection with this name already exists");
            return View(model);
        }

        collection.Name = model.Name!.Trim();
        collection.Description = model.Description?.Trim();
        collection.IsDefault = model.IsDefault;
        collection.UpdatedAt = DateTimeOffset.UtcNow;

        // If setting as default, clear other defaults
        if (model.IsDefault)
        {
            var otherDefaults = await db.Collections
                .Where(c => c.IsDefault && c.Id != id)
                .ToListAsync(ct);
            foreach (var c in otherDefaults)
            {
                c.IsDefault = false;
            }
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Updated collection {CollectionId}", id);

        TempData["Success"] = $"Collection '{collection.Name}' updated successfully";
        return RedirectToAction(nameof(Collections));
    }

    /// <summary>
    /// Delete collection confirmation.
    /// </summary>
    [HttpGet("collections/{id:guid}/delete")]
    public async Task<IActionResult> DeleteCollection(Guid id, CancellationToken ct = default)
    {
        var collection = await db.Collections
            .Include(c => c.Documents)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (collection is null)
        {
            return NotFound();
        }

        var model = new CollectionDeleteViewModel
        {
            Id = collection.Id,
            Name = collection.Name,
            DocumentCount = collection.Documents.Count
        };

        return View(model);
    }

    /// <summary>
    /// Delete collection handler.
    /// </summary>
    [HttpPost("collections/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCollectionConfirmed(Guid id, CancellationToken ct = default)
    {
        var collection = await db.Collections
            .Include(c => c.Documents)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (collection is null)
        {
            return NotFound();
        }

        var name = collection.Name;
        var documentCount = collection.Documents.Count;

        db.Collections.Remove(collection);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Deleted collection {CollectionId} with {DocumentCount} documents", id, documentCount);

        TempData["Success"] = $"Collection '{name}' and {documentCount} documents deleted";
        return RedirectToAction(nameof(Collections));
    }

    /// <summary>
    /// Collection details with documents.
    /// </summary>
    [HttpGet("collections/{id:guid}")]
    public async Task<IActionResult> CollectionDetails(Guid id, CancellationToken ct = default)
    {
        var collection = await db.Collections
            .Include(c => c.Documents)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (collection is null)
        {
            return NotFound();
        }

        var model = new CollectionDetailsViewModel
        {
            Id = collection.Id,
            Name = collection.Name,
            Description = collection.Description,
            IsDefault = collection.IsDefault,
            CreatedAt = collection.CreatedAt,
            UpdatedAt = collection.UpdatedAt,
            Documents = collection.Documents
                .OrderByDescending(d => d.CreatedAt)
                .Select(d => new DocumentListItemViewModel
                {
                    Id = d.Id,
                    Name = d.Name,
                    Status = d.Status,
                    SegmentCount = d.SegmentCount,
                    EntityCount = d.EntityCount,
                    CreatedAt = d.CreatedAt,
                    ProcessedAt = d.ProcessedAt
                })
                .ToList()
        };

        return View(model);
    }
}

// View Models
public class TenantDashboardViewModel
{
    public int TotalCollections { get; set; }
    public int TotalDocuments { get; set; }
    public int CompletedDocuments { get; set; }
    public int ProcessingDocuments { get; set; }
    public int FailedDocuments { get; set; }
    public int TotalEntities { get; set; }
    public int TotalRelationships { get; set; }
}

public class CollectionListItemViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int DocumentCount { get; set; }
    public int CompletedCount { get; set; }
    public int ProcessingCount { get; set; }
    public int FailedCount { get; set; }
    public int EntityCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class CollectionFormViewModel
{
    public Guid? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool IsDefault { get; set; }
}

public class CollectionDeleteViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int DocumentCount { get; set; }
}

public class CollectionDetailsViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<DocumentListItemViewModel> Documents { get; set; } = [];
}

public class DocumentListItemViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DocumentStatus Status { get; set; }
    public int SegmentCount { get; set; }
    public int EntityCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}
