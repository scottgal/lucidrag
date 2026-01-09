using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;

namespace LucidRAG.Services;

/// <summary>
/// Configuration for the synthesis cache
/// </summary>
public class SynthesisCacheConfig
{
    /// <summary>
    /// Maximum number of entries in the cache
    /// </summary>
    public int MaxEntries { get; set; } = 1000;

    /// <summary>
    /// Absolute maximum age for cache entries (hard expiration)
    /// </summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Sliding expiration - entry expires if not accessed within this duration
    /// </summary>
    public TimeSpan SlidingExpiration { get; set; } = TimeSpan.FromHours(4);

    /// <summary>
    /// Whether the cache is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// If true, cache entries are invalidated when the LLM model changes
    /// </summary>
    public bool InvalidateOnModelChange { get; set; } = true;

    /// <summary>
    /// If true, store evidence separately so we can re-synthesize without re-fetching
    /// </summary>
    public bool StoreEvidenceSeparately { get; set; } = true;
}

/// <summary>
/// Cached synthesis entry with metadata for smart invalidation
/// </summary>
public class SynthesisCacheEntry
{
    /// <summary>
    /// The synthesized response
    /// </summary>
    public required string Response { get; init; }

    /// <summary>
    /// Hash of the evidence/context used for this synthesis
    /// </summary>
    public required string EvidenceHash { get; init; }

    /// <summary>
    /// Document IDs that contributed evidence to this synthesis
    /// </summary>
    public required Guid[] SourceDocumentIds { get; init; }

    /// <summary>
    /// LLM model used for synthesis
    /// </summary>
    public required string LlmModel { get; init; }

    /// <summary>
    /// When this entry was created
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Last time this entry was accessed
    /// </summary>
    public DateTimeOffset LastAccessedAt { get; set; }

    /// <summary>
    /// Number of times this entry was accessed (for LFU eviction)
    /// </summary>
    public int AccessCount { get; set; }
}

/// <summary>
/// Cached evidence entry for re-synthesis without re-fetching
/// </summary>
public class EvidenceCacheEntry
{
    /// <summary>
    /// The query this evidence is for
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// The evidence/context string
    /// </summary>
    public required string Evidence { get; init; }

    /// <summary>
    /// Hash of the evidence for quick comparison
    /// </summary>
    public required string EvidenceHash { get; init; }

    /// <summary>
    /// Document IDs that contributed to this evidence
    /// </summary>
    public required Guid[] SourceDocumentIds { get; init; }

    /// <summary>
    /// When this evidence was fetched
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// LFU-cached service for synthesis results with smart invalidation.
/// Supports re-synthesis when model changes without re-fetching evidence.
/// </summary>
public class SynthesisCacheService
{
    private readonly ILogger<SynthesisCacheService> _logger;
    private readonly SynthesisCacheConfig _config;
    private readonly string _currentModel;

    private readonly ConcurrentDictionary<string, SynthesisCacheEntry> _synthesisCache = new();
    private readonly ConcurrentDictionary<string, EvidenceCacheEntry> _evidenceCache = new();
    private readonly object _evictionLock = new();

    private int _cacheHits;
    private int _cacheMisses;
    private int _evidenceHits;
    private int _reSynthesisCount;

    public SynthesisCacheService(
        IOptions<DocSummarizerConfig> config,
        ILogger<SynthesisCacheService> logger)
    {
        _logger = logger;
        _config = new SynthesisCacheConfig(); // Could bind from config
        _currentModel = config.Value.Ollama?.Model ?? "unknown";
    }

    /// <summary>
    /// Try to get a cached synthesis response
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="evidenceHash">Hash of the evidence/context</param>
    /// <param name="response">The cached response if found</param>
    /// <returns>True if cache hit, false otherwise</returns>
    public bool TryGetSynthesis(string query, string evidenceHash, out string? response)
    {
        if (!_config.Enabled)
        {
            response = null;
            return false;
        }

        var cacheKey = ComputeSynthesisKey(query, evidenceHash);

        if (_synthesisCache.TryGetValue(cacheKey, out var entry))
        {
            var now = DateTimeOffset.UtcNow;

            // Check absolute expiration (hard limit)
            if (now - entry.CreatedAt > _config.MaxAge)
            {
                _synthesisCache.TryRemove(cacheKey, out _);
                response = null;
                Interlocked.Increment(ref _cacheMisses);
                _logger.LogDebug("Cache entry expired (absolute): {Key}", cacheKey[..12]);
                return false;
            }

            // Check sliding expiration (idle timeout)
            if (now - entry.LastAccessedAt > _config.SlidingExpiration)
            {
                _synthesisCache.TryRemove(cacheKey, out _);
                response = null;
                Interlocked.Increment(ref _cacheMisses);
                _logger.LogDebug("Cache entry expired (sliding): {Key}", cacheKey[..12]);
                return false;
            }

            // Check if model changed
            if (_config.InvalidateOnModelChange && entry.LlmModel != _currentModel)
            {
                _logger.LogDebug("Cache entry model mismatch: cached={Cached}, current={Current}",
                    entry.LlmModel, _currentModel);
                // Don't remove - evidence is still valid for re-synthesis
                response = null;
                return false;
            }

            entry.AccessCount++;
            entry.LastAccessedAt = now;
            Interlocked.Increment(ref _cacheHits);
            _logger.LogDebug("Synthesis cache HIT (key: {Key}, model: {Model})",
                cacheKey[..12], entry.LlmModel);
            response = entry.Response;
            return true;
        }

        Interlocked.Increment(ref _cacheMisses);
        response = null;
        return false;
    }

    /// <summary>
    /// Try to get cached evidence for a query (for re-synthesis without re-fetching)
    /// </summary>
    public bool TryGetEvidence(string query, out EvidenceCacheEntry? evidence)
    {
        if (!_config.Enabled || !_config.StoreEvidenceSeparately)
        {
            evidence = null;
            return false;
        }

        var evidenceKey = ComputeEvidenceKey(query);

        if (_evidenceCache.TryGetValue(evidenceKey, out evidence))
        {
            // Check if expired
            if (DateTimeOffset.UtcNow - evidence.CreatedAt > _config.MaxAge)
            {
                _evidenceCache.TryRemove(evidenceKey, out _);
                evidence = null;
                return false;
            }

            Interlocked.Increment(ref _evidenceHits);
            _logger.LogDebug("Evidence cache HIT for query: {Query}", query[..Math.Min(50, query.Length)]);
            return true;
        }

        evidence = null;
        return false;
    }

    /// <summary>
    /// Store a synthesis result in the cache
    /// </summary>
    public void SetSynthesis(
        string query,
        string evidence,
        string response,
        Guid[] sourceDocumentIds)
    {
        if (!_config.Enabled) return;

        var evidenceHash = ComputeHash(evidence);
        var cacheKey = ComputeSynthesisKey(query, evidenceHash);

        var entry = new SynthesisCacheEntry
        {
            Response = response,
            EvidenceHash = evidenceHash,
            SourceDocumentIds = sourceDocumentIds,
            LlmModel = _currentModel,
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow,
            AccessCount = 1
        };

        _synthesisCache[cacheKey] = entry;

        // Also store evidence separately for re-synthesis
        if (_config.StoreEvidenceSeparately)
        {
            var evidenceKey = ComputeEvidenceKey(query);
            _evidenceCache[evidenceKey] = new EvidenceCacheEntry
            {
                Query = query,
                Evidence = evidence,
                EvidenceHash = evidenceHash,
                SourceDocumentIds = sourceDocumentIds,
                CreatedAt = DateTimeOffset.UtcNow
            };
        }

        // Evict if over capacity
        if (_synthesisCache.Count > _config.MaxEntries)
        {
            EvictLeastFrequentlyUsed();
        }

        _logger.LogDebug("Synthesis cached (key: {Key}, model: {Model}, docs: {DocCount})",
            cacheKey[..12], _currentModel, sourceDocumentIds.Length);
    }

    /// <summary>
    /// Invalidate cache entries for a specific document
    /// </summary>
    public int InvalidateForDocument(Guid documentId)
    {
        var invalidated = 0;

        // Remove synthesis entries that used this document
        var keysToRemove = _synthesisCache
            .Where(kv => kv.Value.SourceDocumentIds.Contains(documentId))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            if (_synthesisCache.TryRemove(key, out _))
                invalidated++;
        }

        // Remove evidence entries that used this document
        var evidenceKeysToRemove = _evidenceCache
            .Where(kv => kv.Value.SourceDocumentIds.Contains(documentId))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in evidenceKeysToRemove)
        {
            _evidenceCache.TryRemove(key, out _);
        }

        if (invalidated > 0)
        {
            _logger.LogInformation("Invalidated {Count} cache entries for document {DocumentId}",
                invalidated, documentId);
        }

        return invalidated;
    }

    /// <summary>
    /// Mark that a re-synthesis occurred (for stats)
    /// </summary>
    public void RecordReSynthesis()
    {
        Interlocked.Increment(ref _reSynthesisCount);
    }

    /// <summary>
    /// Get cache statistics
    /// </summary>
    public SynthesisCacheStats GetStats()
    {
        var total = _cacheHits + _cacheMisses;
        var hitRate = total > 0 ? (double)_cacheHits / total * 100 : 0;

        return new SynthesisCacheStats(
            Hits: _cacheHits,
            Misses: _cacheMisses,
            EvidenceHits: _evidenceHits,
            ReSynthesisCount: _reSynthesisCount,
            SynthesisCacheSize: _synthesisCache.Count,
            EvidenceCacheSize: _evidenceCache.Count,
            HitRatePercent: hitRate,
            CurrentModel: _currentModel,
            Config: _config);
    }

    /// <summary>
    /// Clear the cache
    /// </summary>
    public void Clear()
    {
        _synthesisCache.Clear();
        _evidenceCache.Clear();
        _cacheHits = 0;
        _cacheMisses = 0;
        _evidenceHits = 0;
        _reSynthesisCount = 0;
        _logger.LogInformation("Synthesis cache cleared");
    }

    /// <summary>
    /// Compute hash for evidence/context
    /// </summary>
    public static string ComputeHash(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash);
    }

    private static string ComputeSynthesisKey(string query, string evidenceHash)
    {
        var keySource = $"synthesis:{query}:{evidenceHash}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(keySource));
        return Convert.ToHexString(hash);
    }

    private static string ComputeEvidenceKey(string query)
    {
        var keySource = $"evidence:{query}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(keySource));
        return Convert.ToHexString(hash);
    }

    private void EvictLeastFrequentlyUsed()
    {
        lock (_evictionLock)
        {
            if (_synthesisCache.Count <= _config.MaxEntries)
                return;

            var entriesToEvict = Math.Max(1, _synthesisCache.Count / 10);

            var toRemove = _synthesisCache
                .OrderBy(kv => kv.Value.AccessCount)
                .ThenBy(kv => kv.Value.LastAccessedAt)
                .Take(entriesToEvict)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                _synthesisCache.TryRemove(key, out _);
            }

            _logger.LogDebug("Evicted {Count} LFU synthesis cache entries", toRemove.Count);
        }
    }
}

public record SynthesisCacheStats(
    int Hits,
    int Misses,
    int EvidenceHits,
    int ReSynthesisCount,
    int SynthesisCacheSize,
    int EvidenceCacheSize,
    double HitRatePercent,
    string CurrentModel,
    SynthesisCacheConfig Config);
