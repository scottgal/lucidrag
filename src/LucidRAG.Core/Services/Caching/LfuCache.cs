using System.Collections.Concurrent;
using System.Diagnostics;

namespace LucidRAG.Core.Services.Caching;

/// <summary>
/// Thread-safe Least Frequently Used (LFU) cache with memory tracking.
/// Evicts least frequently accessed items when capacity is reached.
/// Optimized for RAG workloads where certain segments are accessed repeatedly.
/// </summary>
public class LfuCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly long _maxMemoryBytes;
    private readonly ConcurrentDictionary<TKey, CacheEntry> _cache;
    private readonly object _evictionLock = new();
    private long _currentVersion;
    private long _currentMemoryBytes;

    public LfuCache(int capacity, long maxMemoryBytes = long.MaxValue)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive");

        _capacity = capacity;
        _maxMemoryBytes = maxMemoryBytes;
        _cache = new ConcurrentDictionary<TKey, CacheEntry>();
    }

    /// <summary>
    /// Try to get a value from the cache. Increments frequency on hit.
    /// </summary>
    public bool TryGet(TKey key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TValue? value)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            // Increment frequency and update access time (for LRU tie-breaking)
            entry.Frequency++;
            entry.LastAccessVersion = Interlocked.Increment(ref _currentVersion);
            entry.LastAccessed = DateTimeOffset.UtcNow;

            value = entry.Value;
            var stats = Statistics;
            stats.Hits++;
            Statistics = stats;
            return true;
        }

        value = default;
        var missStats = Statistics;
        missStats.Misses++;
        Statistics = missStats;
        return false;
    }

    /// <summary>
    /// Set a value in the cache. Evicts LFU items if capacity exceeded.
    /// </summary>
    public void Set(TKey key, TValue value, int sizeBytes = 0)
    {
        // Update existing entry
        if (_cache.TryGetValue(key, out var existing))
        {
            var sizeDelta = sizeBytes - existing.SizeBytes;
            existing.Value = value;
            existing.SizeBytes = sizeBytes;
            existing.LastAccessed = DateTimeOffset.UtcNow;
            Interlocked.Add(ref _currentMemoryBytes, sizeDelta);
            return;
        }

        // Check if eviction needed
        if (_cache.Count >= _capacity || _currentMemoryBytes + sizeBytes > _maxMemoryBytes)
        {
            EvictLeastFrequentlyUsed();
        }

        // Add new entry
        var entry = new CacheEntry
        {
            Key = key,
            Value = value,
            Frequency = 1,
            LastAccessVersion = Interlocked.Increment(ref _currentVersion),
            LastAccessed = DateTimeOffset.UtcNow,
            SizeBytes = sizeBytes
        };

        if (_cache.TryAdd(key, entry))
        {
            Interlocked.Add(ref _currentMemoryBytes, sizeBytes);
        }
    }

    /// <summary>
    /// Remove a specific key from the cache.
    /// </summary>
    public bool Remove(TKey key)
    {
        if (_cache.TryRemove(key, out var entry))
        {
            Interlocked.Add(ref _currentMemoryBytes, -entry.SizeBytes);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Clear all entries from the cache.
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
        Interlocked.Exchange(ref _currentMemoryBytes, 0);
        Interlocked.Exchange(ref _currentVersion, 0);
        Statistics = new CacheStatistics(); // Reset statistics
    }

    /// <summary>
    /// Get current cache statistics.
    /// </summary>
    public CacheStatistics GetStatistics()
    {
        Statistics.CurrentSize = _cache.Count;
        Statistics.MemoryUsageBytes = _currentMemoryBytes;
        Statistics.Capacity = _capacity;
        Statistics.HitRate = Statistics.TotalRequests > 0
            ? (double)Statistics.Hits / Statistics.TotalRequests
            : 0;

        return Statistics;
    }

    public CacheStatistics Statistics { get; private set; } = new();

    private void EvictLeastFrequentlyUsed()
    {
        lock (_evictionLock)
        {
            // Double-check after acquiring lock
            if (_cache.Count < _capacity && _currentMemoryBytes < _maxMemoryBytes)
                return;

            // Find entry with lowest frequency (LFU), use LastAccessVersion for tie-breaking (LRU)
            CacheEntry? victim = null;
            TKey? victimKey = default;

            foreach (var kvp in _cache)
            {
                var entry = kvp.Value;

                if (victim == null ||
                    entry.Frequency < victim.Frequency ||
                    (entry.Frequency == victim.Frequency && entry.LastAccessVersion < victim.LastAccessVersion))
                {
                    victim = entry;
                    victimKey = kvp.Key;
                }
            }

            if (victimKey != null && victim != null)
            {
                _cache.TryRemove(victimKey, out _);
                Interlocked.Add(ref _currentMemoryBytes, -victim.SizeBytes);
                var stats = Statistics;
                stats.Evictions++;
                Statistics = stats;
            }
        }
    }

    private class CacheEntry
    {
        public required TKey Key { get; init; }
        public required TValue Value { get; set; }
        public int Frequency { get; set; }
        public long LastAccessVersion { get; set; }
        public DateTimeOffset LastAccessed { get; set; }
        public int SizeBytes { get; set; }
    }
}

/// <summary>
/// Cache performance statistics.
/// </summary>
public class CacheStatistics
{
    public int Capacity { get; set; }
    public int CurrentSize { get; set; }
    public long Hits { get; set; }
    public long Misses { get; set; }
    public long Evictions { get; set; }
    public long MemoryUsageBytes { get; set; }
    public double HitRate { get; set; }
    public long TotalRequests => Hits + Misses;

    public double MemoryUsageMB => MemoryUsageBytes / (1024.0 * 1024.0);
}
