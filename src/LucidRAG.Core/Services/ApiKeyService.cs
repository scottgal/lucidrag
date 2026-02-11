using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LucidRAG.Data;
using LucidRAG.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace LucidRAG.Services;

public interface IApiKeyService
{
    // Key lifecycle
    Task<(string fullKey, ApiKeyEntity entity)> CreateKeyAsync(string userId, string? userEmail, string? description,
        CancellationToken ct);

    Task<ApiKeyEntity?> ValidateKeyAsync(string apiKey, CancellationToken ct);
    Task<ApiKeyEntity?> GetByIdAsync(Guid keyId, CancellationToken ct);
    Task<List<ApiKeyEntity>> GetByUserIdAsync(string userId, CancellationToken ct);
    Task RevokeKeyAsync(Guid keyId, CancellationToken ct);
    Task<(string fullKey, ApiKeyEntity entity)> RotateKeyAsync(Guid keyId, CancellationToken ct);

    // Source management
    Task<ApiKeyIndexingSource> SetIndexingSourceAsync(Guid keyId, SourceType type, string value, CancellationToken ct);
    Task RemoveIndexingSourceAsync(Guid keyId, CancellationToken ct);
    Task<ApiKeyReadDomain> AddReadDomainAsync(Guid keyId, string domain, CancellationToken ct);
    Task RemoveReadDomainAsync(Guid keyId, Guid domainId, CancellationToken ct);

    // Collection links (multi-collection per key)
    Task<ApiKeyCollectionLink> LinkCollectionAsync(Guid keyId, Guid collectionId, string? label, CancellationToken ct);
    Task UnlinkCollectionAsync(Guid keyId, Guid collectionId, CancellationToken ct);
    Task<List<ApiKeyCollectionLink>> GetLinkedCollectionsAsync(Guid keyId, CancellationToken ct);

    // Slug / hosted page
    Task<ApiKeyEntity?> GetBySlugAsync(string slug, CancellationToken ct);

    // HMAC authentication
    Task<ApiKeyEntity?> ValidateHmacAsync(Guid keyId, string hmac, string salt, CancellationToken ct);

    // Validation
    bool ValidateReadDomain(ApiKeyEntity key, string? referer, string? origin);
    Task<bool> CheckDocumentQuotaAsync(Guid keyId, CancellationToken ct);

    // Rate limiting
    bool CheckRateLimit(Guid keyId, int perMinute, int perDay);
    void RecordRequest(Guid keyId);
    Task IncrementRequestCountAsync(Guid keyId, CancellationToken ct);

    // Cache management
    void InvalidateCacheForKey(Guid keyId);
}

public class ApiKeyService(
    RagDocumentsDbContext db,
    IMemoryCache cache,
    IDistributedCache distributedCache,
    ILogger<ApiKeyService> logger) : IApiKeyService
{
    private static readonly ConcurrentDictionary<Guid, RateLimitEntry> RateLimits = new();
    private const string CachePrefix = "apikey:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    // Base62 alphabet for key generation
    private const string Base62Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public async Task<(string fullKey, ApiKeyEntity entity)> CreateKeyAsync(
        string userId, string? userEmail, string? description, CancellationToken ct)
    {
        // Check free-tier limit: 1 key per normalized email
        if (userEmail is not null)
        {
            var normalizedEmail = EmailNormalizer.Normalize(userEmail);

            // Check if this normalized email already has a free key
            var existingFreeKey = await db.ApiKeys
                .AnyAsync(k => k.NormalizedOwnerEmail == normalizedEmail
                               && k.Plan == "free"
                               && k.RevokedAt == null, ct);

            if (existingFreeKey)
                throw new InvalidOperationException("Free tier allows only one API key per email address.");
        }

        var fullKey = GenerateKey();
        var keyHash = HashKey(fullKey);
        var keyPrefix = fullKey[..12]; // "lrag_" + 7 chars

        // Auto-create a collection for this key
        var collection = new CollectionEntity
        {
            Id = Guid.NewGuid(),
            Name = $"Key: {keyPrefix}",
            Description = description ?? $"Auto-created collection for API key {keyPrefix}",
            Settings = $"{{\"apiKeyId\":\"{Guid.NewGuid()}\"}}"
        };
        db.Collections.Add(collection);

        var entity = new ApiKeyEntity
        {
            Id = Guid.NewGuid(),
            KeyPrefix = keyPrefix,
            KeyHash = keyHash,
            SigningSecret = GenerateSigningSecret(),
            Slug = GenerateSlug(keyPrefix),
            Description = description,
            UserId = userId,
            NormalizedOwnerEmail = userEmail is not null ? EmailNormalizer.Normalize(userEmail) : null,
            Plan = "free",
            CollectionId = collection.Id
        };

        // Fix the settings to use the actual key ID
        collection.Settings = $"{{\"apiKeyId\":\"{entity.Id}\"}}";

        db.ApiKeys.Add(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Created API key {KeyPrefix} for user {UserId}", keyPrefix, userId);
        return (fullKey, entity);
    }

    public async Task<ApiKeyEntity?> ValidateKeyAsync(string apiKey, CancellationToken ct)
    {
        var keyHash = HashKey(apiKey);
        var cacheKey = CachePrefix + keyHash;

        if (cache.TryGetValue(cacheKey, out ApiKeyEntity? cached))
        {
            if (cached is not null)
            {
                cached.LastUsedAt = DateTimeOffset.UtcNow;
                return cached;
            }

            return null;
        }

        var entity = await db.ApiKeys
            .Include(k => k.ReadDomains)
            .Include(k => k.IndexingSource)
            .Include(k => k.Collection)
            .Include(k => k.CollectionLinks)
                .ThenInclude(l => l.Collection)
            .FirstOrDefaultAsync(k => k.KeyHash == keyHash, ct);

        if (entity is null)
        {
            cache.Set(cacheKey, (ApiKeyEntity?)null, CacheTtl);
            return null;
        }

        // Check active, not revoked, not expired
        if (!entity.IsActive || entity.RevokedAt is not null ||
            (entity.ExpiresAt is not null && entity.ExpiresAt <= DateTimeOffset.UtcNow))
        {
            cache.Set(cacheKey, (ApiKeyEntity?)null, CacheTtl);
            return null;
        }

        cache.Set(cacheKey, entity, CacheTtl);
        return entity;
    }

    public async Task<ApiKeyEntity?> GetByIdAsync(Guid keyId, CancellationToken ct)
    {
        return await db.ApiKeys
            .Include(k => k.ReadDomains)
            .Include(k => k.IndexingSource)
            .Include(k => k.Collection)
            .Include(k => k.CollectionLinks)
                .ThenInclude(l => l.Collection)
            .Include(k => k.WidgetConfig)
            .Include(k => k.CustomDomain)
            .FirstOrDefaultAsync(k => k.Id == keyId, ct);
    }

    public async Task<ApiKeyEntity?> GetBySlugAsync(string slug, CancellationToken ct)
    {
        return await db.ApiKeys
            .Include(k => k.Collection)
            .Include(k => k.CollectionLinks)
                .ThenInclude(l => l.Collection)
            .Include(k => k.WidgetConfig)
            .Include(k => k.CustomDomain)
            .FirstOrDefaultAsync(k => k.Slug == slug && k.IsActive && k.RevokedAt == null, ct);
    }

    public async Task<ApiKeyEntity?> ValidateHmacAsync(Guid keyId, string hmac, string salt, CancellationToken ct)
    {
        var cacheKey = CachePrefix + "hmac:" + keyId;

        // Try cache first
        if (cache.TryGetValue(cacheKey, out ApiKeyEntity? cached) && cached is not null)
        {
            if (VerifyHmac(cached.SigningSecret!, salt, hmac))
            {
                cached.LastUsedAt = DateTimeOffset.UtcNow;
                return cached;
            }
            return null;
        }

        var entity = await db.ApiKeys
            .Include(k => k.ReadDomains)
            .Include(k => k.IndexingSource)
            .Include(k => k.Collection)
            .Include(k => k.CollectionLinks)
                .ThenInclude(l => l.Collection)
            .FirstOrDefaultAsync(k => k.Id == keyId, ct);

        if (entity is null || !entity.IsActive || entity.RevokedAt is not null)
            return null;

        if (entity.ExpiresAt is not null && entity.ExpiresAt < DateTimeOffset.UtcNow)
            return null;

        if (string.IsNullOrEmpty(entity.SigningSecret))
            return null;

        if (!VerifyHmac(entity.SigningSecret, salt, hmac))
            return null;

        cache.Set(cacheKey, entity, CacheTtl);
        entity.LastUsedAt = DateTimeOffset.UtcNow;
        return entity;
    }

    private static bool VerifyHmac(string signingSecret, string salt, string providedHmac)
    {
        var secretBytes = Encoding.UTF8.GetBytes(signingSecret);
        var saltBytes = Encoding.UTF8.GetBytes(salt);
        var expectedBytes = HMACSHA256.HashData(secretBytes, saltBytes);
        var expectedHex = Convert.ToHexStringLower(expectedBytes);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expectedHex),
            Encoding.UTF8.GetBytes(providedHmac.ToLowerInvariant()));
    }

    public async Task<List<ApiKeyEntity>> GetByUserIdAsync(string userId, CancellationToken ct)
    {
        return await db.ApiKeys
            .Include(k => k.ReadDomains)
            .Include(k => k.IndexingSource)
            .Where(k => k.UserId == userId && k.RevokedAt == null)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task RevokeKeyAsync(Guid keyId, CancellationToken ct)
    {
        var key = await db.ApiKeys.FindAsync([keyId], ct);
        if (key is null) return;

        key.RevokedAt = DateTimeOffset.UtcNow;
        key.IsActive = false;
        await db.SaveChangesAsync(ct);

        // Evict from cache
        cache.Remove(CachePrefix + key.KeyHash);
        logger.LogInformation("Revoked API key {KeyPrefix}", key.KeyPrefix);
    }

    public async Task<(string fullKey, ApiKeyEntity entity)> RotateKeyAsync(Guid keyId, CancellationToken ct)
    {
        var oldKey = await db.ApiKeys.FindAsync([keyId], ct);
        if (oldKey is null) throw new InvalidOperationException("Key not found");

        // Revoke old
        oldKey.RevokedAt = DateTimeOffset.UtcNow;
        oldKey.IsActive = false;
        cache.Remove(CachePrefix + oldKey.KeyHash);

        // Generate new key with same settings
        var fullKey = GenerateKey();
        var newEntity = new ApiKeyEntity
        {
            Id = Guid.NewGuid(),
            KeyPrefix = fullKey[..12],
            KeyHash = HashKey(fullKey),
            SigningSecret = GenerateSigningSecret(),
            Description = oldKey.Description,
            UserId = oldKey.UserId,
            NormalizedOwnerEmail = oldKey.NormalizedOwnerEmail,
            Plan = oldKey.Plan,
            CollectionId = oldKey.CollectionId,
            AllowChat = oldKey.AllowChat,
            AllowSearch = oldKey.AllowSearch,
            RateLimitPerMinute = oldKey.RateLimitPerMinute,
            RateLimitPerDay = oldKey.RateLimitPerDay
        };

        db.ApiKeys.Add(newEntity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Rotated API key {OldPrefix} → {NewPrefix}", oldKey.KeyPrefix, newEntity.KeyPrefix);
        return (fullKey, newEntity);
    }

    public async Task<ApiKeyIndexingSource> SetIndexingSourceAsync(Guid keyId, SourceType type, string value,
        CancellationToken ct)
    {
        // Remove existing source if any
        var existing = await db.ApiKeyIndexingSources.FirstOrDefaultAsync(s => s.ApiKeyId == keyId, ct);
        if (existing is not null)
            db.ApiKeyIndexingSources.Remove(existing);

        var source = new ApiKeyIndexingSource
        {
            Id = Guid.NewGuid(),
            ApiKeyId = keyId,
            SourceType = type,
            SourceValue = value.Trim()
        };

        db.ApiKeyIndexingSources.Add(source);
        await db.SaveChangesAsync(ct);
        return source;
    }

    public async Task RemoveIndexingSourceAsync(Guid keyId, CancellationToken ct)
    {
        var source = await db.ApiKeyIndexingSources.FirstOrDefaultAsync(s => s.ApiKeyId == keyId, ct);
        if (source is not null)
        {
            db.ApiKeyIndexingSources.Remove(source);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<ApiKeyReadDomain> AddReadDomainAsync(Guid keyId, string domain, CancellationToken ct)
    {
        domain = NormalizeDomain(domain);

        // Check limit based on plan
        var key = await db.ApiKeys.FindAsync([keyId], ct);
        var maxDomains = key is not null ? GetPlanMaxDomains(key.Plan) : 5;
        var count = await db.ApiKeyReadDomains.CountAsync(d => d.ApiKeyId == keyId, ct);
        if (count >= maxDomains)
            throw new InvalidOperationException($"Maximum {maxDomains} read domains per API key ({key?.Plan ?? "free"} plan).");

        // Check uniqueness
        var exists = await db.ApiKeyReadDomains
            .AnyAsync(d => d.ApiKeyId == keyId && d.Domain == domain, ct);
        if (exists)
            throw new InvalidOperationException($"Domain '{domain}' is already registered for this key.");

        var entry = new ApiKeyReadDomain
        {
            Id = Guid.NewGuid(),
            ApiKeyId = keyId,
            Domain = domain
        };

        db.ApiKeyReadDomains.Add(entry);
        await db.SaveChangesAsync(ct);

        // Evict key from cache so domain list refreshes
        if (key is not null)
            cache.Remove(CachePrefix + key.KeyHash);

        return entry;
    }

    public async Task RemoveReadDomainAsync(Guid keyId, Guid domainId, CancellationToken ct)
    {
        var domain = await db.ApiKeyReadDomains
            .FirstOrDefaultAsync(d => d.Id == domainId && d.ApiKeyId == keyId, ct);
        if (domain is not null)
        {
            db.ApiKeyReadDomains.Remove(domain);
            await db.SaveChangesAsync(ct);

            // Evict key from cache
            var key = await db.ApiKeys.FindAsync([keyId], ct);
            if (key is not null)
                cache.Remove(CachePrefix + key.KeyHash);
        }
    }

    public async Task<ApiKeyCollectionLink> LinkCollectionAsync(Guid keyId, Guid collectionId, string? label,
        CancellationToken ct)
    {
        // Check uniqueness
        var exists = await db.ApiKeyCollectionLinks
            .AnyAsync(l => l.ApiKeyId == keyId && l.CollectionId == collectionId, ct);
        if (exists)
            throw new InvalidOperationException("Collection is already linked to this API key.");

        var maxSort = await db.ApiKeyCollectionLinks
            .Where(l => l.ApiKeyId == keyId)
            .Select(l => (int?)l.SortOrder)
            .MaxAsync(ct) ?? 0;

        var link = new ApiKeyCollectionLink
        {
            Id = Guid.NewGuid(),
            ApiKeyId = keyId,
            CollectionId = collectionId,
            Label = label,
            SortOrder = maxSort + 1
        };

        db.ApiKeyCollectionLinks.Add(link);
        await db.SaveChangesAsync(ct);

        // Evict key from cache
        var apiKey = await db.ApiKeys.FindAsync([keyId], ct);
        if (apiKey is not null)
            cache.Remove(CachePrefix + apiKey.KeyHash);

        return link;
    }

    public async Task UnlinkCollectionAsync(Guid keyId, Guid collectionId, CancellationToken ct)
    {
        var link = await db.ApiKeyCollectionLinks
            .FirstOrDefaultAsync(l => l.ApiKeyId == keyId && l.CollectionId == collectionId, ct);
        if (link is not null)
        {
            db.ApiKeyCollectionLinks.Remove(link);
            await db.SaveChangesAsync(ct);

            // Evict key from cache
            var apiKey = await db.ApiKeys.FindAsync([keyId], ct);
            if (apiKey is not null)
                cache.Remove(CachePrefix + apiKey.KeyHash);
        }
    }

    public async Task<List<ApiKeyCollectionLink>> GetLinkedCollectionsAsync(Guid keyId, CancellationToken ct)
    {
        return await db.ApiKeyCollectionLinks
            .Include(l => l.Collection)
            .Where(l => l.ApiKeyId == keyId)
            .OrderBy(l => l.SortOrder)
            .ToListAsync(ct);
    }

    public bool ValidateReadDomain(ApiKeyEntity key, string? referer, string? origin)
    {
        // No read domains configured = allow all (dev-friendly default)
        if (key.ReadDomains.Count == 0)
            return true;

        var requestDomain = ExtractDomain(referer) ?? ExtractDomain(origin);

        // No referer/origin = likely server-side call, allow
        if (requestDomain is null)
            return true;

        // Always allow localhost for dev
        if (requestDomain is "localhost" or "127.0.0.1" or "[::1]")
            return true;

        return key.ReadDomains.Any(d =>
            string.Equals(d.Domain, requestDomain, StringComparison.OrdinalIgnoreCase) ||
            (d.Domain.StartsWith("*.") &&
             requestDomain.EndsWith(d.Domain[1..], StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<bool> CheckDocumentQuotaAsync(Guid keyId, CancellationToken ct)
    {
        var source = await db.ApiKeyIndexingSources.FirstOrDefaultAsync(s => s.ApiKeyId == keyId, ct);
        if (source is null) return true; // No source = no quota to check

        return source.DocumentCount < source.MaxDocuments;
    }

    public bool CheckRateLimit(Guid keyId, int perMinute, int perDay)
    {
        var entry = RateLimits.GetOrAdd(keyId, _ => new RateLimitEntry());
        return entry.Check(perMinute, perDay);
    }

    public void RecordRequest(Guid keyId)
    {
        var entry = RateLimits.GetOrAdd(keyId, _ => new RateLimitEntry());
        entry.Record();
    }

    public async Task IncrementRequestCountAsync(Guid keyId, CancellationToken ct)
    {
        await db.ApiKeys
            .Where(k => k.Id == keyId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(k => k.TotalRequests, k => k.TotalRequests + 1)
                .SetProperty(k => k.LastUsedAt, DateTimeOffset.UtcNow), ct);
    }

    public void InvalidateCacheForKey(Guid keyId)
    {
        // Evict HMAC cache entry
        cache.Remove(CachePrefix + "hmac:" + keyId);

        // For hash-based cache entries, we need to find the key's hash
        // Look up the key directly (bypassing cache) to get the hash
        var key = db.ApiKeys.AsNoTracking().FirstOrDefault(k => k.Id == keyId);
        if (key is not null)
            cache.Remove(CachePrefix + key.KeyHash);

        // Signal distributed cache so other instances invalidate their local cache
        var invalidationKey = CachePrefix + "invalidated:" + keyId;
        distributedCache.SetString(invalidationKey,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl });
    }

    private static string GenerateKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var sb = new StringBuilder("lrag_", 37);
        foreach (var b in bytes)
            sb.Append(Base62Chars[b % 62]);
        return sb.ToString()[..37]; // lrag_ + 32 chars = 37 total
    }

    private static string GenerateSigningSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    ///     Generates a URL-friendly slug from the key prefix.
    ///     E.g., "lrag_Ab3Xk9z" → "ab3xk9z" (lowercase, no prefix).
    /// </summary>
    private static string GenerateSlug(string keyPrefix)
    {
        // Strip "lrag_" prefix and lowercase
        var slug = keyPrefix.StartsWith("lrag_", StringComparison.OrdinalIgnoreCase)
            ? keyPrefix[5..].ToLowerInvariant()
            : keyPrefix.ToLowerInvariant();
        // Ensure minimum length by appending random chars if needed
        if (slug.Length < 6)
        {
            var extra = RandomNumberGenerator.GetBytes(4);
            foreach (var b in extra) slug += Base62Chars[b % 62];
        }
        return slug;
    }

    private static string HashKey(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToBase64String(hash);
    }

    private static string NormalizeDomain(string domain)
    {
        domain = domain.Trim().ToLowerInvariant();
        // Strip protocol
        if (domain.StartsWith("https://")) domain = domain[8..];
        if (domain.StartsWith("http://")) domain = domain[7..];
        // Strip path and port
        var slashIndex = domain.IndexOf('/');
        if (slashIndex >= 0) domain = domain[..slashIndex];
        var colonIndex = domain.IndexOf(':');
        if (colonIndex >= 0) domain = domain[..colonIndex];
        return domain;
    }

    private static string? ExtractDomain(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var uri = new Uri(url);
            return uri.Host.ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Returns the maximum read domains allowed for a plan tier.
    /// </summary>
    private static int GetPlanMaxDomains(string plan) => plan.ToLowerInvariant() switch
    {
        "starter" => 10,
        "pro" => 25,
        "enterprise" => 100,
        _ => 5 // free
    };

    /// <summary>
    ///     O(1) sliding window rate limiter using bucket arrays.
    ///     Minute window: 60 second-buckets. Day window: 1440 minute-buckets.
    /// </summary>
    private class RateLimitEntry
    {
        private readonly int[] _secondBuckets = new int[60];
        private readonly int[] _minuteBuckets = new int[1440];
        private long _lastSecondTick;
        private long _lastMinuteTick;
        private int _minuteCount;
        private int _dayCount;
        private readonly object _lock = new();

        public bool Check(int perMinute, int perDay)
        {
            lock (_lock)
            {
                RotateBuckets();
                return _minuteCount < perMinute && _dayCount < perDay;
            }
        }

        public void Record()
        {
            lock (_lock)
            {
                RotateBuckets();
                var now = DateTimeOffset.UtcNow;
                var sec = (int)(now.ToUnixTimeSeconds() % 60);
                var min = (int)(now.ToUnixTimeSeconds() / 60 % 1440);
                _secondBuckets[sec]++;
                _minuteBuckets[min]++;
                _minuteCount++;
                _dayCount++;
            }
        }

        private void RotateBuckets()
        {
            var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var nowMinutes = nowSeconds / 60;

            // Rotate second buckets (minute window)
            var elapsedSeconds = nowSeconds - _lastSecondTick;
            if (elapsedSeconds > 0)
            {
                if (elapsedSeconds >= 60)
                {
                    Array.Clear(_secondBuckets);
                    _minuteCount = 0;
                }
                else
                {
                    for (var i = 1; i <= elapsedSeconds; i++)
                    {
                        var idx = (int)((_lastSecondTick + i) % 60);
                        _minuteCount -= _secondBuckets[idx];
                        _secondBuckets[idx] = 0;
                    }
                }
                _lastSecondTick = nowSeconds;
            }

            // Rotate minute buckets (day window)
            var elapsedMinutes = nowMinutes - _lastMinuteTick;
            if (elapsedMinutes > 0)
            {
                if (elapsedMinutes >= 1440)
                {
                    Array.Clear(_minuteBuckets);
                    _dayCount = 0;
                }
                else
                {
                    for (var i = 1; i <= elapsedMinutes; i++)
                    {
                        var idx = (int)((_lastMinuteTick + i) % 1440);
                        _dayCount -= _minuteBuckets[idx];
                        _minuteBuckets[idx] = 0;
                    }
                }
                _lastMinuteTick = nowMinutes;
            }
        }
    }
}
