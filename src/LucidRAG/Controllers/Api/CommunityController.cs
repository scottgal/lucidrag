using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using LucidRAG.Data;
using LucidRAG.Models;
using LucidRAG.Services;

namespace LucidRAG.Controllers.Api;

/// <summary>
/// API controller for GraphRAG community detection and management.
/// Communities are automatically detected clusters of related entities.
/// </summary>
[ApiController]
[Route("api/communities")]
public class CommunityController(
    ICommunityDetectionService communityService,
    RagDocumentsDbContext db,
    ILogger<CommunityController> logger) : ControllerBase
{
    /// <summary>
    /// Run community detection on the entity graph using Louvain algorithm.
    /// Creates/updates community structure from the entity graph.
    /// </summary>
    [HttpPost]
    public async Task<Results<Ok<CommunityDetectionResponse>, StatusCodeHttpResult>> DetectCommunities(CancellationToken ct)
    {
        logger.LogInformation("Starting community detection");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var result = await communityService.DetectCommunitiesAsync(ct);
            sw.Stop();

            return TypedResults.Ok(new CommunityDetectionResponse(
                CommunitiesDetected: result.CommunitiesDetected,
                EntitiesAssigned: result.EntitiesAssigned,
                Modularity: result.Modularity,
                DurationSeconds: result.ProcessingTime.TotalSeconds,
                Meta: new ApiMeta(DateTimeOffset.UtcNow, sw.ElapsedMilliseconds)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Community detection failed");
            return TypedResults.StatusCode(500);
        }
    }

    /// <summary>
    /// Generate or regenerate LLM summaries for all communities.
    /// </summary>
    [HttpPost("summaries")]
    public async Task<Results<Ok<CommunitySummaryResponse>, StatusCodeHttpResult>> GenerateSummaries(CancellationToken ct)
    {
        logger.LogInformation("Generating community summaries");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await communityService.GenerateCommunitySummariesAsync(ct);
            sw.Stop();

            var count = await db.Communities.CountAsync(c => c.Summary != null, ct);

            return TypedResults.Ok(new CommunitySummaryResponse(
                Summarized: count,
                Meta: new ApiMeta(DateTimeOffset.UtcNow, sw.ElapsedMilliseconds)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Summary generation failed");
            return TypedResults.StatusCode(500);
        }
    }

    /// <summary>
    /// List all detected communities with pagination.
    /// </summary>
    [HttpGet]
    public async Task<Ok<PagedResponse<CommunityListItem>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] int? level = null,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Communities.AsQueryable();

        if (level.HasValue)
            query = query.Where(c => c.Level == level.Value);

        var total = await query.CountAsync(ct);

        var communities = await query
            .OrderByDescending(c => c.EntityCount)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new CommunityListItem(
                c.Id,
                c.Name,
                c.Summary,
                c.Algorithm,
                c.Level,
                c.EntityCount,
                c.Cohesion,
                c.ParentCommunityId,
                c.CreatedAt))
            .ToListAsync(ct);

        return TypedResults.Ok(ApiResponseHelpers.Paged(communities, page, pageSize, total, "/api/communities"));
    }

    /// <summary>
    /// Get a specific community with its members.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var community = await communityService.GetCommunityAsync(id, ct);

        if (community == null)
            return NotFound(new { error = "Community not found" });

        // Get members
        var members = await db.CommunityMemberships
            .Where(m => m.CommunityId == id)
            .Join(db.Entities,
                m => m.EntityId,
                e => e.Id,
                (m, e) => new
                {
                    e.Id,
                    e.CanonicalName,
                    e.EntityType,
                    m.Centrality,
                    m.IsRepresentative
                })
            .OrderByDescending(m => m.Centrality)
            .Take(100)
            .ToListAsync(ct);

        // Parse features JSON if present
        object? features = null;
        if (!string.IsNullOrEmpty(community.Features))
        {
            try
            {
                features = System.Text.Json.JsonSerializer.Deserialize<object>(community.Features);
            }
            catch { /* ignore parse errors */ }
        }

        return Ok(new
        {
            community.Id,
            community.Name,
            community.Summary,
            community.Algorithm,
            community.Level,
            community.EntityCount,
            community.Cohesion,
            community.ParentCommunityId,
            community.CreatedAt,
            features,
            members
        });
    }

    /// <summary>
    /// Search communities by query (matches name, summary, or features).
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string q,
        [FromQuery] int limit = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "Query parameter 'q' is required" });

        var results = await communityService.SearchCommunitiesAsync(q, limit, ct);

        return Ok(new
        {
            query = q,
            count = results.Count,
            items = results.Select(c => new
            {
                c.Id,
                c.Name,
                c.Summary,
                c.EntityCount,
                c.Cohesion
            })
        });
    }

    /// <summary>
    /// Get community hierarchy (top-level communities with children).
    /// </summary>
    [HttpGet("hierarchy")]
    public async Task<IActionResult> GetHierarchy(CancellationToken ct)
    {
        var topLevel = await db.Communities
            .Where(c => c.ParentCommunityId == null)
            .OrderByDescending(c => c.EntityCount)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Summary,
                c.EntityCount,
                c.Cohesion,
                ChildCount = db.Communities.Count(ch => ch.ParentCommunityId == c.Id)
            })
            .ToListAsync(ct);

        return Ok(new
        {
            total = topLevel.Count,
            items = topLevel
        });
    }

    /// <summary>
    /// Get children of a specific community.
    /// </summary>
    [HttpGet("{id:guid}/children")]
    public async Task<IActionResult> GetChildren(Guid id, CancellationToken ct)
    {
        var children = await db.Communities
            .Where(c => c.ParentCommunityId == id)
            .OrderByDescending(c => c.EntityCount)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Summary,
                c.EntityCount,
                c.Cohesion,
                c.Level
            })
            .ToListAsync(ct);

        return Ok(new
        {
            parentId = id,
            count = children.Count,
            items = children
        });
    }

    /// <summary>
    /// Delete all communities (useful before re-running detection).
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> DeleteAll(CancellationToken ct)
    {
        var count = await db.Communities.CountAsync(ct);

        // Delete memberships first (cascade should handle this, but be explicit)
        await db.CommunityMemberships.ExecuteDeleteAsync(ct);
        await db.Communities.ExecuteDeleteAsync(ct);

        logger.LogInformation("Deleted {Count} communities", count);

        return Ok(new
        {
            success = true,
            deleted = count
        });
    }

    /// <summary>
    /// Get statistics about current community detection.
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        var totalCommunities = await db.Communities.CountAsync(ct);
        var totalMemberships = await db.CommunityMemberships.CountAsync(ct);
        var totalEntities = await db.Entities.CountAsync(ct);
        var withSummaries = await db.Communities.CountAsync(c => c.Summary != null, ct);

        var avgEntityCount = totalCommunities > 0
            ? await db.Communities.AverageAsync(c => c.EntityCount, ct)
            : 0;

        var avgCohesion = totalCommunities > 0
            ? await db.Communities.AverageAsync(c => c.Cohesion, ct)
            : 0;

        var levels = await db.Communities
            .GroupBy(c => c.Level)
            .Select(g => new { Level = g.Key, Count = g.Count() })
            .OrderBy(x => x.Level)
            .ToListAsync(ct);

        return Ok(new
        {
            totalCommunities,
            totalMemberships,
            totalEntities,
            entitiesInCommunities = totalMemberships,
            communitiesWithSummaries = withSummaries,
            averageEntityCount = Math.Round(avgEntityCount, 1),
            averageCohesion = Math.Round(avgCohesion, 3),
            levelDistribution = levels
        });
    }
}
