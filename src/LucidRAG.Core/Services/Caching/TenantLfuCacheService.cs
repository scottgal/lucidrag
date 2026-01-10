using System.Collections.Concurrent;
using System.Text;
using LucidRAG.Entities;
using Microsoft.Extensions.Options;

namespace LucidRAG.Core.Services.Caching;

/// <summary>
/// Per-tenant LFU cache service for evidence artifacts and entities.
/// Provides memory-efficient caching with tenant isolation.
/// </summary>
public interface ITenantLfuCacheService
{
    // Evidence caching (segment hash → text)
    Task<string?> GetEvidenceTextAsync(string tenantId, string segmentHash);
    Task<Dictionary<string, string>> GetEvidenceTextsAsync(string tenantId, IEnumerable<string> segmentHashes);
    void CacheEvidenceText(string tenantId, string segmentHash, string text);
    void InvalidateEvidence(string tenantId, string segmentHash);

    // Entity caching (entity ID → entity)
    Task<ExtractedEntity?> GetEntityAsync(string tenantId, Guid entityId);
    void CacheEntity(string tenantId, ExtractedEntity entity);
    void InvalidateEntity(string tenantId, Guid entityId);

    // Tenant management
    void InvalidateTenant(string tenantId);
    TenantCacheStatistics GetTenantStatistics(string tenantId);
    IReadOnlyDictionary<string, TenantCacheStatistics> GetAllStatistics();
}

public class TenantLfuCacheService : ITenantLfuCacheService
{
    private readonly ConcurrentDictionary<string, LfuCache<string, string>> _evidenceCaches = new();
    private readonly ConcurrentDictionary<string, LfuCache<Guid, ExtractedEntity>> _entityCaches = new();
    private readonly LfuCacheConfig _config;
    private readonly ILogger<TenantLfuCacheService> _logger;

    public TenantLfuCacheService(
        IOptions<LfuCacheConfig> config,
        ILogger<TenantLfuCacheService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    // ===== Evidence Caching =====

    public Task<string?> GetEvidenceTextAsync(string tenantId, string segmentHash)
    {
        var cache = GetOrCreateEvidenceCache(tenantId);
        var found = cache.TryGet(segmentHash, out var text);
        return Task.FromResult(found ? text : null);
    }

    public async Task<Dictionary<string, string>> GetEvidenceTextsAsync(
        string tenantId,
        IEnumerable<string> segmentHashes)
    {
        var cache = GetOrCreateEvidenceCache(tenantId);
        var result = new Dictionary<string, string>();

        foreach (var hash in segmentHashes)
        {
            if (cache.TryGet(hash, out var text))
            {
                result[hash] = text;
            }
        }

        return await Task.FromResult(result);
    }

    public void CacheEvidenceText(string tenantId, string segmentHash, string text)
    {
        var cache = GetOrCreateEvidenceCache(tenantId);
        var sizeBytes = Encoding.UTF8.GetByteCount(text);
        cache.Set(segmentHash, text, sizeBytes);
    }

    public void InvalidateEvidence(string tenantId, string segmentHash)
    {
        if (_evidenceCaches.TryGetValue(tenantId, out var cache))
        {
            cache.Remove(segmentHash);
        }
    }

    // ===== Entity Caching =====

    public Task<ExtractedEntity?> GetEntityAsync(string tenantId, Guid entityId)
    {
        var cache = GetOrCreateEntityCache(tenantId);
        var found = cache.TryGet(entityId, out var entity);
        return Task.FromResult(found ? entity : null);
    }

    public void CacheEntity(string tenantId, ExtractedEntity entity)
    {
        var cache = GetOrCreateEntityCache(tenantId);

        // Estimate entity size (rough approximation)
        var sizeBytes = EstimateEntitySize(entity);
        cache.Set(entity.Id, entity, sizeBytes);
    }

    public void InvalidateEntity(string tenantId, Guid entityId)
    {
        if (_entityCaches.TryGetValue(tenantId, out var cache))
        {
            cache.Remove(entityId);
        }
    }

    // ===== Tenant Management =====

    public void InvalidateTenant(string tenantId)
    {
        if (_evidenceCaches.TryRemove(tenantId, out var evidenceCache))
        {
            evidenceCache.Clear();
        }

        if (_entityCaches.TryRemove(tenantId, out var entityCache))
        {
            entityCache.Clear();
        }

        _logger.LogInformation("Invalidated all caches for tenant {TenantId}", tenantId);
    }

    public TenantCacheStatistics GetTenantStatistics(string tenantId)
    {
        var evidenceStats = _evidenceCaches.TryGetValue(tenantId, out var evidenceCache)
            ? evidenceCache.GetStatistics()
            : new CacheStatistics();

        var entityStats = _entityCaches.TryGetValue(tenantId, out var entityCache)
            ? entityCache.GetStatistics()
            : new CacheStatistics();

        return new TenantCacheStatistics
        {
            TenantId = tenantId,
            EvidenceCache = evidenceStats,
            EntityCache = entityStats
        };
    }

    public IReadOnlyDictionary<string, TenantCacheStatistics> GetAllStatistics()
    {
        var allTenants = _evidenceCaches.Keys.Union(_entityCaches.Keys).Distinct();
        var result = new Dictionary<string, TenantCacheStatistics>();

        foreach (var tenantId in allTenants)
        {
            result[tenantId] = GetTenantStatistics(tenantId);
        }

        return result;
    }

    // ===== Private Helpers =====

    private LfuCache<string, string> GetOrCreateEvidenceCache(string tenantId)
    {
        return _evidenceCaches.GetOrAdd(tenantId, _ =>
        {
            var maxMemoryBytes = _config.MaxMemoryPerTenantMB * 1024L * 1024L;
            _logger.LogInformation(
                "Creating evidence cache for tenant {TenantId}: capacity={Capacity}, maxMemoryMB={MaxMemoryMB}",
                tenantId, _config.EvidenceCacheCapacity, _config.MaxMemoryPerTenantMB);

            return new LfuCache<string, string>(_config.EvidenceCacheCapacity, maxMemoryBytes);
        });
    }

    private LfuCache<Guid, ExtractedEntity> GetOrCreateEntityCache(string tenantId)
    {
        return _entityCaches.GetOrAdd(tenantId, _ =>
        {
            var maxMemoryBytes = _config.MaxMemoryPerTenantMB * 1024L * 1024L;
            _logger.LogInformation(
                "Creating entity cache for tenant {TenantId}: capacity={Capacity}, maxMemoryMB={MaxMemoryMB}",
                tenantId, _config.EntityCacheCapacity, _config.MaxMemoryPerTenantMB);

            return new LfuCache<Guid, ExtractedEntity>(_config.EntityCacheCapacity, maxMemoryBytes);
        });
    }

    private static int EstimateEntitySize(ExtractedEntity entity)
    {
        // Rough size estimation for entity
        var size = 128; // Base overhead
        size += entity.CanonicalName?.Length * 2 ?? 0;
        size += entity.Description?.Length * 2 ?? 0;
        size += entity.EntityType?.Length * 2 ?? 0;
        size += entity.OutgoingRelationships?.Count * 64 ?? 0; // Rough estimate per relationship
        return size;
    }
}

/// <summary>
/// Configuration for LFU cache service.
/// </summary>
public class LfuCacheConfig
{
    /// <summary>
    /// Maximum entries per tenant evidence cache (default: 1000)
    /// </summary>
    public int EvidenceCacheCapacity { get; set; } = 1000;

    /// <summary>
    /// Maximum entries per tenant entity cache (default: 500)
    /// </summary>
    public int EntityCacheCapacity { get; set; } = 500;

    /// <summary>
    /// Maximum memory per tenant cache in MB (default: 50MB)
    /// </summary>
    public int MaxMemoryPerTenantMB { get; set; } = 50;

    /// <summary>
    /// Enable cache statistics tracking (default: true)
    /// </summary>
    public bool EnableStatistics { get; set; } = true;

    /// <summary>
    /// Cache entry TTL in minutes (default: 60 minutes)
    /// Not currently enforced - future enhancement
    /// </summary>
    public int EntryTtlMinutes { get; set; } = 60;
}

/// <summary>
/// Statistics for a single tenant's caches.
/// </summary>
public class TenantCacheStatistics
{
    public required string TenantId { get; init; }
    public required CacheStatistics EvidenceCache { get; init; }
    public required CacheStatistics EntityCache { get; init; }

    public long TotalMemoryBytes => EvidenceCache.MemoryUsageBytes + EntityCache.MemoryUsageBytes;
    public double TotalMemoryMB => TotalMemoryBytes / (1024.0 * 1024.0);
    public double OverallHitRate => CalculateOverallHitRate();

    private double CalculateOverallHitRate()
    {
        var totalHits = EvidenceCache.Hits + EntityCache.Hits;
        var totalRequests = EvidenceCache.TotalRequests + EntityCache.TotalRequests;
        return totalRequests > 0 ? (double)totalHits / totalRequests : 0;
    }
}
