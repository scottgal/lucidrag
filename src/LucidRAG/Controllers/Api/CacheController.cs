using LucidRAG.Core.Services.Caching;
using Microsoft.AspNetCore.Mvc;

namespace LucidRAG.Controllers.Api;

/// <summary>
/// API endpoints for LFU cache management and statistics.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class CacheController : ControllerBase
{
    private readonly ITenantLfuCacheService _cacheService;
    private readonly ILogger<CacheController> _logger;

    public CacheController(
        ITenantLfuCacheService cacheService,
        ILogger<CacheController> logger)
    {
        _cacheService = cacheService;
        _logger = logger;
    }

    /// <summary>
    /// Get cache statistics for all tenants.
    /// </summary>
    [HttpGet("statistics")]
    public ActionResult<CacheStatisticsResponse> GetStatistics()
    {
        var stats = _cacheService.GetAllStatistics();

        var response = new CacheStatisticsResponse
        {
            TenantCount = stats.Count,
            Tenants = stats.Values.Select(t => new TenantStatistics
            {
                TenantId = t.TenantId,
                EvidenceCache = new CacheInfo
                {
                    Capacity = t.EvidenceCache.Capacity,
                    CurrentSize = t.EvidenceCache.CurrentSize,
                    HitRate = t.EvidenceCache.HitRate,
                    TotalHits = t.EvidenceCache.Hits,
                    TotalMisses = t.EvidenceCache.Misses,
                    Evictions = t.EvidenceCache.Evictions,
                    MemoryUsageMB = t.EvidenceCache.MemoryUsageMB
                },
                EntityCache = new CacheInfo
                {
                    Capacity = t.EntityCache.Capacity,
                    CurrentSize = t.EntityCache.CurrentSize,
                    HitRate = t.EntityCache.HitRate,
                    TotalHits = t.EntityCache.Hits,
                    TotalMisses = t.EntityCache.Misses,
                    Evictions = t.EntityCache.Evictions,
                    MemoryUsageMB = t.EntityCache.MemoryUsageMB
                },
                TotalMemoryMB = t.TotalMemoryMB,
                OverallHitRate = t.OverallHitRate
            }).ToList()
        };

        return Ok(response);
    }

    /// <summary>
    /// Get cache statistics for a specific tenant.
    /// </summary>
    [HttpGet("statistics/{tenantId}")]
    public ActionResult<TenantStatistics> GetTenantStatistics(string tenantId)
    {
        var stats = _cacheService.GetTenantStatistics(tenantId);

        var response = new TenantStatistics
        {
            TenantId = stats.TenantId,
            EvidenceCache = new CacheInfo
            {
                Capacity = stats.EvidenceCache.Capacity,
                CurrentSize = stats.EvidenceCache.CurrentSize,
                HitRate = stats.EvidenceCache.HitRate,
                TotalHits = stats.EvidenceCache.Hits,
                TotalMisses = stats.EvidenceCache.Misses,
                Evictions = stats.EvidenceCache.Evictions,
                MemoryUsageMB = stats.EvidenceCache.MemoryUsageMB
            },
            EntityCache = new CacheInfo
            {
                Capacity = stats.EntityCache.Capacity,
                CurrentSize = stats.EntityCache.CurrentSize,
                HitRate = stats.EntityCache.HitRate,
                TotalHits = stats.EntityCache.Hits,
                TotalMisses = stats.EntityCache.Misses,
                Evictions = stats.EntityCache.Evictions,
                MemoryUsageMB = stats.EntityCache.MemoryUsageMB
            },
            TotalMemoryMB = stats.TotalMemoryMB,
            OverallHitRate = stats.OverallHitRate
        };

        return Ok(response);
    }

    /// <summary>
    /// Invalidate (clear) cache for a specific tenant.
    /// </summary>
    [HttpPost("invalidate/{tenantId}")]
    public IActionResult InvalidateTenant(string tenantId)
    {
        _cacheService.InvalidateTenant(tenantId);
        _logger.LogInformation("Invalidated cache for tenant {TenantId} via API", tenantId);
        return Ok(new { message = $"Cache cleared for tenant {tenantId}" });
    }
}

// Response DTOs

public class CacheStatisticsResponse
{
    public int TenantCount { get; set; }
    public List<TenantStatistics> Tenants { get; set; } = new();
}

public class TenantStatistics
{
    public required string TenantId { get; set; }
    public required CacheInfo EvidenceCache { get; set; }
    public required CacheInfo EntityCache { get; set; }
    public double TotalMemoryMB { get; set; }
    public double OverallHitRate { get; set; }
}

public class CacheInfo
{
    public int Capacity { get; set; }
    public int CurrentSize { get; set; }
    public double HitRate { get; set; }
    public long TotalHits { get; set; }
    public long TotalMisses { get; set; }
    public long Evictions { get; set; }
    public double MemoryUsageMB { get; set; }
}
