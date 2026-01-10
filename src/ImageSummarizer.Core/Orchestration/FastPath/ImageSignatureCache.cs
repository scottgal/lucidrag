using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.DocSummarizer.Images.Orchestration.FastPath;

/// <summary>
///     Ultra-fast signature cache for instant image lookups.
///     Keyed by content hash - allows instant return for known images.
///     This is the "12 basic shapes" fast-path for images.
/// </summary>
public interface IImageSignatureCache
{
    /// <summary>
    ///     Try to get a cached result for an image.
    ///     Returns null if not found or expired.
    /// </summary>
    CachedImageSignature? Get(string signatureKey);

    /// <summary>
    ///     Store a signature (called from background learning).
    /// </summary>
    void Set(string signatureKey, CachedImageSignature signature);

    /// <summary>
    ///     Compute exact content hash (for identical images).
    /// </summary>
    string ComputeContentHash(byte[] imageBytes);

    /// <summary>
    ///     Compute perceptual hash (for visually similar images).
    ///     Uses average hash algorithm - fast and catches resized/recompressed variants.
    /// </summary>
    Task<string> ComputePerceptualHashAsync(string imagePath, CancellationToken ct = default);

    /// <summary>
    ///     Compute composite signature key (combines content + perceptual hash).
    /// </summary>
    Task<ImageSignatureKey> ComputeSignatureKeyAsync(string imagePath, CancellationToken ct = default);

    /// <summary>
    ///     Try to find a similar image by perceptual hash.
    ///     Returns the closest match within hamming distance threshold.
    /// </summary>
    CachedImageSignature? FindSimilar(string perceptualHash, int maxHammingDistance = 5);

    /// <summary>
    ///     Get cache statistics.
    /// </summary>
    ImageCacheStats GetStats();
}

/// <summary>
///     Cached image signature with analysis results.
/// </summary>
public sealed record CachedImageSignature
{
    /// <summary>Content hash of the image.</summary>
    public required string SignatureKey { get; init; }

    /// <summary>When this signature was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When this signature was last accessed.</summary>
    public DateTimeOffset LastAccessedAt { get; set; }

    /// <summary>How many times this signature was hit.</summary>
    public int HitCount { get; set; }

    /// <summary>Confidence in this signature (higher = more reliable).</summary>
    public double Confidence { get; init; } = 0.8;

    /// <summary>Support count (how many times we've seen this image).</summary>
    public int Support { get; set; } = 1;

    /// <summary>Caption for the image.</summary>
    public string? Caption { get; init; }

    /// <summary>OCR text extracted.</summary>
    public string? OcrText { get; init; }

    /// <summary>Dominant color.</summary>
    public string? DominantColor { get; init; }

    /// <summary>Color palette.</summary>
    public IReadOnlyList<string>? ColorPalette { get; init; }

    /// <summary>Whether image is animated.</summary>
    public bool IsAnimated { get; init; }

    /// <summary>Image dimensions.</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Content type detected.</summary>
    public string? ContentType { get; init; }

    /// <summary>All signals from original analysis.</summary>
    public IReadOnlyDictionary<string, object> Signals { get; init; } =
        new Dictionary<string, object>();

    /// <summary>Which waves contributed to this signature.</summary>
    public IReadOnlySet<string> ContributingWaves { get; init; } = new HashSet<string>();

    /// <summary>Whether this is a complete analysis or partial.</summary>
    public bool IsComplete { get; init; }

    /// <summary>Processing time of original analysis.</summary>
    public long OriginalProcessingTimeMs { get; init; }
}

/// <summary>
///     Composite signature key with content + perceptual hash.
/// </summary>
public sealed record ImageSignatureKey
{
    /// <summary>Exact content hash (SHA256 of first 64KB + size).</summary>
    public required string ContentHash { get; init; }

    /// <summary>Perceptual hash (64-bit average hash).</summary>
    public required string PerceptualHash { get; init; }

    /// <summary>Combined key for primary lookup.</summary>
    public string CombinedKey => $"{ContentHash}|{PerceptualHash}";

    /// <summary>File size in bytes.</summary>
    public long FileSize { get; init; }
}

/// <summary>
///     Cache statistics.
/// </summary>
public sealed record ImageCacheStats
{
    public long TotalEntries { get; init; }
    public long TotalHits { get; init; }
    public long TotalMisses { get; init; }
    public long PerceptualHits { get; init; }
    public double HitRate => TotalHits + TotalMisses > 0
        ? TotalHits / (double)(TotalHits + TotalMisses)
        : 0;
    public double PerceptualHitRate => TotalHits > 0
        ? PerceptualHits / (double)TotalHits
        : 0;
    public long TotalMemoryBytes { get; init; }
}

/// <summary>
///     In-memory LRU cache for image signatures with perceptual hash support.
///     Thread-safe with configurable size limit.
/// </summary>
public sealed class ImageSignatureCache : IImageSignatureCache
{
    private readonly ConcurrentDictionary<string, CachedImageSignature> _cache = new();
    private readonly ConcurrentDictionary<string, string> _perceptualIndex = new(); // phash -> content hash
    private readonly ILogger<ImageSignatureCache> _logger;
    private readonly int _maxSize;
    private readonly TimeSpan _ttl;
    private long _totalHits;
    private long _totalMisses;
    private long _perceptualHits;

    public ImageSignatureCache(
        ILogger<ImageSignatureCache> logger,
        int maxSize = 10000,
        TimeSpan? ttl = null)
    {
        _logger = logger;
        _maxSize = maxSize;
        _ttl = ttl ?? TimeSpan.FromHours(24);
    }

    public CachedImageSignature? Get(string signatureKey)
    {
        if (_cache.TryGetValue(signatureKey, out var signature))
        {
            // Check TTL
            if (DateTimeOffset.UtcNow - signature.CreatedAt > _ttl)
            {
                _cache.TryRemove(signatureKey, out _);
                Interlocked.Increment(ref _totalMisses);
                return null;
            }

            // Update access time and hit count
            signature.LastAccessedAt = DateTimeOffset.UtcNow;
            signature.HitCount++;

            Interlocked.Increment(ref _totalHits);

            _logger.LogDebug(
                "Signature cache HIT (exact): {Key} (hits={Hits}, confidence={Confidence:F2})",
                signatureKey[..Math.Min(16, signatureKey.Length)], signature.HitCount, signature.Confidence);

            return signature;
        }

        Interlocked.Increment(ref _totalMisses);
        return null;
    }

    public CachedImageSignature? FindSimilar(string perceptualHash, int maxHammingDistance = 5)
    {
        // Try exact perceptual hash match first
        if (_perceptualIndex.TryGetValue(perceptualHash, out var contentHash))
        {
            if (_cache.TryGetValue(contentHash, out var exact))
            {
                exact.LastAccessedAt = DateTimeOffset.UtcNow;
                exact.HitCount++;
                Interlocked.Increment(ref _perceptualHits);

                _logger.LogDebug(
                    "Signature cache HIT (perceptual exact): {Hash}",
                    perceptualHash[..Math.Min(16, perceptualHash.Length)]);

                return exact;
            }
        }

        // Search for similar hashes within hamming distance
        var phashBits = HexToBits(perceptualHash);
        if (phashBits == null) return null;

        foreach (var (storedHash, storedContentHash) in _perceptualIndex)
        {
            var storedBits = HexToBits(storedHash);
            if (storedBits == null) continue;

            var distance = HammingDistance(phashBits, storedBits);
            if (distance <= maxHammingDistance)
            {
                if (_cache.TryGetValue(storedContentHash, out var similar))
                {
                    similar.LastAccessedAt = DateTimeOffset.UtcNow;
                    similar.HitCount++;
                    Interlocked.Increment(ref _perceptualHits);

                    _logger.LogDebug(
                        "Signature cache HIT (perceptual similar): distance={Distance}",
                        distance);

                    return similar;
                }
            }
        }

        return null;
    }

    public void Set(string signatureKey, CachedImageSignature signature)
    {
        // Evict if at capacity
        if (_cache.Count >= _maxSize)
        {
            EvictOldest();
        }

        _cache[signatureKey] = signature;

        // Also index by perceptual hash if available
        if (signature.Signals.TryGetValue("_perceptual_hash", out var phash) && phash is string phashStr)
        {
            _perceptualIndex[phashStr] = signatureKey;
        }

        _logger.LogDebug(
            "Signature cached: {Key} (confidence={Confidence:F2}, waves={Waves})",
            signatureKey[..Math.Min(16, signatureKey.Length)], signature.Confidence, signature.ContributingWaves.Count);
    }

    public string ComputeContentHash(byte[] imageBytes)
    {
        var hash = SHA256.HashData(imageBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<string> ComputePerceptualHashAsync(string imagePath, CancellationToken ct = default)
    {
        // Average hash (aHash) - fast and effective for similar image detection
        // 1. Resize to 8x8
        // 2. Convert to grayscale
        // 3. Compute mean
        // 4. Each pixel above mean = 1, below = 0
        // Result: 64-bit hash

        using var image = await SixLabors.ImageSharp.Image.LoadAsync<SixLabors.ImageSharp.PixelFormats.Rgba32>(imagePath, ct);

        // Resize to 8x8 for hash computation
        image.Mutate(x => x.Resize(8, 8));

        // Convert to grayscale values
        var pixels = new byte[64];
        var idx = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var pixel = image[x, y];
                // Grayscale using luminance formula
                pixels[idx++] = (byte)(0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B);
            }
        }

        // Compute mean
        var mean = pixels.Average(p => (double)p);

        // Generate hash: 1 if pixel >= mean, 0 otherwise
        var hash = 0UL;
        for (var i = 0; i < 64; i++)
        {
            if (pixels[i] >= mean)
            {
                hash |= 1UL << i;
            }
        }

        return hash.ToString("x16"); // 16 hex chars = 64 bits
    }

    public async Task<ImageSignatureKey> ComputeSignatureKeyAsync(string imagePath, CancellationToken ct = default)
    {
        // Read file for content hash
        var fileInfo = new FileInfo(imagePath);
        await using var stream = File.OpenRead(imagePath);

        // Content hash: first 64KB + size
        var buffer = new byte[Math.Min(65536, stream.Length)];
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);

        var sizeBytes = BitConverter.GetBytes(stream.Length);
        var combined = new byte[bytesRead + sizeBytes.Length];
        Buffer.BlockCopy(buffer, 0, combined, 0, bytesRead);
        Buffer.BlockCopy(sizeBytes, 0, combined, bytesRead, sizeBytes.Length);

        var contentHash = Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant();

        // Perceptual hash
        var perceptualHash = await ComputePerceptualHashAsync(imagePath, ct);

        return new ImageSignatureKey
        {
            ContentHash = $"img:{contentHash}",
            PerceptualHash = $"phash:{perceptualHash}",
            FileSize = fileInfo.Length
        };
    }

    public ImageCacheStats GetStats()
    {
        return new ImageCacheStats
        {
            TotalEntries = _cache.Count,
            TotalHits = _totalHits,
            TotalMisses = _totalMisses,
            TotalMemoryBytes = EstimateMemoryUsage(),
            PerceptualHits = _perceptualHits
        };
    }

    private void EvictOldest()
    {
        var toEvict = _cache
            .OrderBy(kvp => kvp.Value.LastAccessedAt)
            .Take(_maxSize / 10)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toEvict)
        {
            if (_cache.TryRemove(key, out var sig))
            {
                // Also remove from perceptual index
                if (sig.Signals.TryGetValue("_perceptual_hash", out var phash) && phash is string phashStr)
                {
                    _perceptualIndex.TryRemove(phashStr, out _);
                }
            }
        }

        _logger.LogDebug("Evicted {Count} entries from signature cache", toEvict.Count);
    }

    private long EstimateMemoryUsage()
    {
        return _cache.Count * 1024;
    }

    private static byte[]? HexToBits(string hex)
    {
        if (hex.StartsWith("phash:"))
            hex = hex[6..];

        if (hex.Length != 16) return null;

        try
        {
            var value = Convert.ToUInt64(hex, 16);
            var bits = new byte[64];
            for (var i = 0; i < 64; i++)
            {
                bits[i] = (byte)((value >> i) & 1);
            }
            return bits;
        }
        catch
        {
            return null;
        }
    }

    private static int HammingDistance(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return int.MaxValue;

        var distance = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) distance++;
        }
        return distance;
    }
}
