using Microsoft.AspNetCore.Mvc;
using LucidRAG.Services;
using StyloFlow.Retrieval.Entities;

namespace LucidRAG.Controllers.Api;

/// <summary>
/// API for managing cross-modal retrieval entities.
/// </summary>
[ApiController]
[Route("api/retrieval-entities")]
public class RetrievalEntitiesController(
    IRetrievalEntityService entityService,
    ILogger<RetrievalEntitiesController> logger) : ControllerBase
{
    /// <summary>
    /// Create a new retrieval entity.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<RetrievalEntityDto>> Create(
        [FromBody] CreateRetrievalEntityRequest request,
        CancellationToken ct = default)
    {
        var entity = new RetrievalEntity
        {
            Id = Guid.NewGuid().ToString(),
            ContentType = Enum.Parse<ContentType>(request.ContentType, ignoreCase: true),
            Source = request.Source,
            Title = request.Title,
            Summary = request.Summary,
            TextContent = request.TextContent,
            Tags = request.Tags?.ToList() ?? [],
            Collection = request.CollectionId?.ToString()
        };

        var entityId = await entityService.StoreAsync(entity, ct);

        logger.LogInformation("Created retrieval entity {EntityId} type={Type}",
            entityId, entity.ContentType);

        var stored = await entityService.GetByIdAsync(entityId, ct);
        if (stored == null)
        {
            return StatusCode(500, "Failed to retrieve stored entity");
        }

        return CreatedAtAction(
            nameof(GetById),
            new { entityId },
            MapToDto(stored));
    }

    /// <summary>
    /// Get entity by ID.
    /// </summary>
    [HttpGet("{entityId}")]
    public async Task<ActionResult<RetrievalEntityDto>> GetById(
        string entityId,
        CancellationToken ct = default)
    {
        var entity = await entityService.GetByIdAsync(entityId, ct);
        if (entity == null)
        {
            return NotFound();
        }

        return Ok(MapToDto(entity));
    }

    /// <summary>
    /// List entities, optionally filtered by collection and content type.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<RetrievalEntityDto>>> List(
        [FromQuery] Guid? collectionId = null,
        [FromQuery] string[]? contentTypes = null,
        CancellationToken ct = default)
    {
        ContentType[]? types = null;
        if (contentTypes is { Length: > 0 })
        {
            types = contentTypes
                .Select(t => Enum.Parse<ContentType>(t, ignoreCase: true))
                .ToArray();
        }

        var entities = await entityService.GetByCollectionAsync(
            collectionId ?? Guid.Empty,
            types,
            ct);

        return Ok(entities.Select(MapToDto));
    }

    /// <summary>
    /// Delete an entity.
    /// </summary>
    [HttpDelete("{entityId}")]
    public async Task<IActionResult> Delete(
        string entityId,
        CancellationToken ct = default)
    {
        var deleted = await entityService.DeleteAsync(entityId, ct);
        if (!deleted)
        {
            return NotFound();
        }

        return NoContent();
    }

    /// <summary>
    /// Get entity counts by content type.
    /// </summary>
    [HttpGet("counts")]
    public async Task<ActionResult<Dictionary<string, int>>> GetCounts(
        [FromQuery] Guid? collectionId = null,
        CancellationToken ct = default)
    {
        var counts = await entityService.GetCountsByTypeAsync(collectionId, ct);
        return Ok(counts.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value));
    }

    private static RetrievalEntityDto MapToDto(RetrievalEntity entity) => new()
    {
        Id = entity.Id,
        ContentType = entity.ContentType.ToString(),
        Source = entity.Source,
        Title = entity.Title,
        Summary = entity.Summary,
        TextContent = entity.TextContent,
        Tags = entity.Tags?.ToArray(),
        CollectionId = entity.Collection != null && Guid.TryParse(entity.Collection, out var cid) ? cid : null,
        QualityScore = entity.QualityScore,
        ContentConfidence = entity.ContentConfidence,
        NeedsReview = entity.NeedsReview,
        CreatedAt = new DateTimeOffset(entity.CreatedAt, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(entity.UpdatedAt, TimeSpan.Zero)
    };
}

/// <summary>
/// Request to create a retrieval entity.
/// </summary>
public record CreateRetrievalEntityRequest
{
    public required string ContentType { get; init; }
    public required string Source { get; init; }
    public string? Title { get; init; }
    public string? Summary { get; init; }
    public string? TextContent { get; init; }
    public string[]? Tags { get; init; }
    public Guid? CollectionId { get; init; }
}

/// <summary>
/// DTO for retrieval entity responses.
/// </summary>
public record RetrievalEntityDto
{
    public string? Id { get; init; }
    public required string ContentType { get; init; }
    public required string Source { get; init; }
    public string? Title { get; init; }
    public string? Summary { get; init; }
    public string? TextContent { get; init; }
    public string[]? Tags { get; init; }
    public Guid? CollectionId { get; init; }
    public double QualityScore { get; init; }
    public double ContentConfidence { get; init; }
    public bool NeedsReview { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
