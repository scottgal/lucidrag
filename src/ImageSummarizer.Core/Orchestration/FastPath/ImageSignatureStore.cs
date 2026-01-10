using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Mostlylucid.DocSummarizer.Images.Orchestration.FastPath;

/// <summary>
///     Persistent signature store following BotDetection's WeightStore pattern:
///     - Write-behind cache (memory is source of truth, PostgreSQL persisted async)
///     - Sliding expiration for LRU eviction
///     - Confidence tracking with EMA updates
///     - Support count for observation tracking
///     - Decay for old signatures
/// </summary>
public interface IImageSignatureStore
{
    /// <summary>Get signature by content hash (exact match).</summary>
    Task<StoredImageSignature?> GetByContentHashAsync(string contentHash, CancellationToken ct = default);

    /// <summary>Get signature by perceptual hash (similar match).</summary>
    Task<StoredImageSignature?> GetByPerceptualHashAsync(string perceptualHash, int maxHammingDistance = 5,
        CancellationToken ct = default);

    /// <summary>Get multiple signatures by content hashes (batch lookup).</summary>
    Task<IReadOnlyDictionary<string, StoredImageSignature>> GetByContentHashesAsync(
        IEnumerable<string> contentHashes, CancellationToken ct = default);

    /// <summary>Store or update a signature.</summary>
    Task StoreAsync(StoredImageSignature signature, CancellationToken ct = default);

    /// <summary>Record an observation (reinforces confidence, updates last seen).</summary>
    Task RecordObservationAsync(string contentHash, bool wasSuccessful, double confidence,
        CancellationToken ct = default);

    /// <summary>Decay old signatures that haven't been seen recently.</summary>
    Task DecayOldSignaturesAsync(TimeSpan maxAge, double decayFactor, CancellationToken ct = default);

    /// <summary>Get store statistics.</summary>
    Task<SignatureStoreStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>Preload signatures into cache (warmup).</summary>
    Task WarmupCacheAsync(int count = 1000, CancellationToken ct = default);
}

/// <summary>
///     Stored image signature with analysis results and confidence tracking.
/// </summary>
public sealed record StoredImageSignature
{
    /// <summary>Content hash (SHA256) - primary key for exact match.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Perceptual hash (aHash) - for similarity matching.</summary>
    public string? PerceptualHash { get; init; }

    /// <summary>File size in bytes.</summary>
    public long FileSize { get; init; }

    /// <summary>Image width.</summary>
    public int Width { get; init; }

    /// <summary>Image height.</summary>
    public int Height { get; init; }

    /// <summary>MIME type.</summary>
    public string? MimeType { get; init; }

    /// <summary>Whether the image is animated.</summary>
    public bool IsAnimated { get; init; }

    /// <summary>Frame count for animated images.</summary>
    public int FrameCount { get; init; }

    // ===== Analysis Results =====

    /// <summary>Best caption from analysis.</summary>
    public string? Caption { get; init; }

    /// <summary>Best OCR text from analysis.</summary>
    public string? OcrText { get; init; }

    /// <summary>Dominant color.</summary>
    public string? DominantColor { get; init; }

    /// <summary>Detected content type (photo, screenshot, meme, etc.).</summary>
    public string? ContentType { get; init; }

    /// <summary>Whether this is a scanned document.</summary>
    public bool IsScannedDocument { get; init; }

    /// <summary>All signals as JSON.</summary>
    public string? SignalsJson { get; init; }

    // ===== Confidence & Learning =====

    /// <summary>Confidence in this signature (0.0 to 1.0).</summary>
    public double Confidence { get; set; }

    /// <summary>Number of times this signature was observed (support count).</summary>
    public int ObservationCount { get; set; }

    /// <summary>Whether the analysis is complete.</summary>
    public bool IsComplete { get; init; }

    /// <summary>Original processing time in ms.</summary>
    public long OriginalProcessingTimeMs { get; init; }

    /// <summary>Which waves contributed to this signature.</summary>
    public string? ContributingWavesJson { get; init; }

    // ===== Timestamps =====

    /// <summary>When this signature was first created.</summary>
    public DateTimeOffset FirstSeen { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When this signature was last accessed.</summary>
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
///     Signature store statistics.
/// </summary>
public sealed record SignatureStoreStats
{
    public int TotalSignatures { get; init; }
    public int CompleteSignatures { get; init; }
    public int AnimatedSignatures { get; init; }
    public int ScannedDocuments { get; init; }
    public double AverageConfidence { get; init; }
    public int HighConfidenceCount { get; init; }
    public long TotalCacheHits { get; init; }
    public long TotalCacheMisses { get; init; }
    public double CacheHitRate { get; init; }
    public DateTimeOffset? OldestSignature { get; init; }
    public DateTimeOffset? NewestSignature { get; init; }
}

/// <summary>
///     PostgreSQL implementation of image signature store with write-behind cache.
///     Follows BotDetection's WeightStore pattern.
/// </summary>
public sealed class PostgresImageSignatureStore : IImageSignatureStore, IAsyncDisposable, IDisposable
{
    private const string TableName = "image_signatures";
    private const string SchemaName = "docsummarizer";

    private readonly MemoryCache _cache;
    private readonly int _cacheSize;
    private readonly string _connectionString;
    private readonly TimeSpan _flushInterval = TimeSpan.FromMilliseconds(500);
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly Timer _flushTimer;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ILogger<PostgresImageSignatureStore> _logger;
    private readonly ConcurrentDictionary<string, StoredImageSignature> _pendingWrites = new();
    private readonly TimeSpan _slidingExpiration;

    private bool _disposed;
    private bool _initialized;
    private long _totalHits;
    private long _totalMisses;

    public PostgresImageSignatureStore(
        ILogger<PostgresImageSignatureStore> logger,
        string connectionString,
        int cacheSize = 5000,
        TimeSpan? slidingExpiration = null)
    {
        _logger = logger;
        _connectionString = connectionString;
        _cacheSize = cacheSize;
        _slidingExpiration = slidingExpiration ?? TimeSpan.FromHours(2);

        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = _cacheSize,
            CompactionPercentage = 0.25
        });

        // Start background flush timer
        _flushTimer = new Timer(FlushCallback, null, _flushInterval, _flushInterval);

        _logger.LogDebug(
            "PostgresImageSignatureStore initialized (cache={CacheSize}, expiration={Expiration}h)",
            _cacheSize, _slidingExpiration.TotalHours);
    }

    public async Task<StoredImageSignature?> GetByContentHashAsync(string contentHash, CancellationToken ct = default)
    {
        // Fast path: check cache
        if (_cache.TryGetValue(contentHash, out StoredImageSignature? cached) && cached != null)
        {
            Interlocked.Increment(ref _totalHits);
            cached.LastSeen = DateTimeOffset.UtcNow;
            return cached;
        }

        Interlocked.Increment(ref _totalMisses);

        // Slow path: load from DB
        await EnsureInitializedAsync(ct);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"SELECT * FROM {SchemaName}.{TableName} WHERE content_hash = @hash";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hash", contentHash);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var sig = ReadSignature(reader);
            CacheSignature(sig);
            return sig;
        }

        return null;
    }

    public async Task<StoredImageSignature?> GetByPerceptualHashAsync(
        string perceptualHash, int maxHammingDistance = 5, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Exact match first
        var sql = $"SELECT * FROM {SchemaName}.{TableName} WHERE perceptual_hash = @hash LIMIT 1";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hash", perceptualHash);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var sig = ReadSignature(reader);
            CacheSignature(sig);
            return sig;
        }

        // TODO: For hamming distance search, could use:
        // - pgvector extension with bit vectors
        // - Qdrant for dedicated vector similarity
        // - Custom hamming distance function in SQL
        return null;
    }

    public async Task<IReadOnlyDictionary<string, StoredImageSignature>> GetByContentHashesAsync(
        IEnumerable<string> contentHashes, CancellationToken ct = default)
    {
        var result = new Dictionary<string, StoredImageSignature>();
        var hashList = contentHashes.ToList();

        // Check cache first
        var missing = new List<string>();
        foreach (var hash in hashList)
        {
            if (_cache.TryGetValue(hash, out StoredImageSignature? cached) && cached != null)
            {
                result[hash] = cached;
                Interlocked.Increment(ref _totalHits);
            }
            else
            {
                missing.Add(hash);
                Interlocked.Increment(ref _totalMisses);
            }
        }

        if (missing.Count == 0) return result;

        // Load missing from DB
        await EnsureInitializedAsync(ct);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"SELECT * FROM {SchemaName}.{TableName} WHERE content_hash = ANY(@hashes)";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hashes", missing.ToArray());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sig = ReadSignature(reader);
            CacheSignature(sig);
            result[sig.ContentHash] = sig;
        }

        return result;
    }

    public Task StoreAsync(StoredImageSignature signature, CancellationToken ct = default)
    {
        // Update cache immediately (source of truth)
        CacheSignature(signature);

        // Queue for async persistence
        _pendingWrites[signature.ContentHash] = signature;

        return Task.CompletedTask;
    }

    public Task RecordObservationAsync(
        string contentHash, bool wasSuccessful, double confidence, CancellationToken ct = default)
    {
        const double alpha = 0.1; // EMA learning rate

        if (_cache.TryGetValue(contentHash, out StoredImageSignature? existing) && existing != null)
        {
            // Update with EMA
            var newConfidence = existing.Confidence * (1 - alpha) + confidence * alpha;
            var updated = existing with
            {
                Confidence = wasSuccessful ? Math.Min(1.0, newConfidence + 0.01) : newConfidence,
                ObservationCount = existing.ObservationCount + 1,
                LastSeen = DateTimeOffset.UtcNow
            };

            CacheSignature(updated);
            _pendingWrites[contentHash] = updated;
        }

        return Task.CompletedTask;
    }

    public async Task DecayOldSignaturesAsync(TimeSpan maxAge, double decayFactor, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var cutoff = DateTimeOffset.UtcNow.Subtract(maxAge);

        // Decay old signatures
        var sql = $@"
            UPDATE {SchemaName}.{TableName}
            SET confidence = confidence * @decay
            WHERE last_seen < @cutoff";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@decay", decayFactor);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        var updated = await cmd.ExecuteNonQueryAsync(ct);

        // Delete very low confidence signatures
        var deleteSql = $@"
            DELETE FROM {SchemaName}.{TableName}
            WHERE confidence < 0.1 AND observation_count < 3";

        await using var deleteCmd = new NpgsqlCommand(deleteSql, conn);
        var deleted = await deleteCmd.ExecuteNonQueryAsync(ct);

        if (updated > 0 || deleted > 0)
        {
            _logger.LogInformation("Signature decay: {Updated} decayed, {Deleted} deleted", updated, deleted);
            _cache.Compact(0.25);
        }
    }

    public async Task<SignatureStoreStats> GetStatsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $@"
            SELECT
                COUNT(*) as total,
                COUNT(*) FILTER (WHERE is_complete) as complete,
                COUNT(*) FILTER (WHERE is_animated) as animated,
                COUNT(*) FILTER (WHERE is_scanned_document) as scanned,
                AVG(confidence) as avg_conf,
                COUNT(*) FILTER (WHERE confidence > 0.8) as high_conf,
                MIN(first_seen) as oldest,
                MAX(last_seen) as newest
            FROM {SchemaName}.{TableName}";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            var total = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            return new SignatureStoreStats
            {
                TotalSignatures = total,
                CompleteSignatures = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                AnimatedSignatures = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                ScannedDocuments = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                AverageConfidence = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                HighConfidenceCount = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                TotalCacheHits = _totalHits,
                TotalCacheMisses = _totalMisses,
                CacheHitRate = _totalHits + _totalMisses > 0
                    ? _totalHits / (double)(_totalHits + _totalMisses)
                    : 0,
                OldestSignature = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                NewestSignature = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)
            };
        }

        return new SignatureStoreStats();
    }

    public async Task WarmupCacheAsync(int count = 1000, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Load most recently seen, high confidence signatures
        var sql = $@"
            SELECT * FROM {SchemaName}.{TableName}
            WHERE confidence > 0.5
            ORDER BY last_seen DESC, observation_count DESC
            LIMIT @count";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@count", count);

        var loaded = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sig = ReadSignature(reader);
            CacheSignature(sig);
            loaded++;
        }

        _logger.LogInformation("Cache warmup: loaded {Count} signatures", loaded);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _flushTimer.DisposeAsync();
        await FlushPendingWritesAsync(CancellationToken.None);

        _cache.Dispose();
        _flushLock.Dispose();
        _initLock.Dispose();

        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _flushTimer.Dispose();
        FlushPendingWritesAsync(CancellationToken.None).GetAwaiter().GetResult();

        _cache.Dispose();
        _flushLock.Dispose();
        _initLock.Dispose();

        GC.SuppressFinalize(this);
    }

    private void CacheSignature(StoredImageSignature sig)
    {
        var options = new MemoryCacheEntryOptions()
            .SetSlidingExpiration(_slidingExpiration)
            .SetSize(1);

        _cache.Set(sig.ContentHash, sig, options);
    }

    private static StoredImageSignature ReadSignature(NpgsqlDataReader reader)
    {
        return new StoredImageSignature
        {
            ContentHash = reader.GetString(reader.GetOrdinal("content_hash")),
            PerceptualHash = reader.IsDBNull(reader.GetOrdinal("perceptual_hash"))
                ? null
                : reader.GetString(reader.GetOrdinal("perceptual_hash")),
            FileSize = reader.GetInt64(reader.GetOrdinal("file_size")),
            Width = reader.GetInt32(reader.GetOrdinal("width")),
            Height = reader.GetInt32(reader.GetOrdinal("height")),
            MimeType = reader.IsDBNull(reader.GetOrdinal("mime_type"))
                ? null
                : reader.GetString(reader.GetOrdinal("mime_type")),
            IsAnimated = reader.GetBoolean(reader.GetOrdinal("is_animated")),
            FrameCount = reader.GetInt32(reader.GetOrdinal("frame_count")),
            Caption = reader.IsDBNull(reader.GetOrdinal("caption"))
                ? null
                : reader.GetString(reader.GetOrdinal("caption")),
            OcrText = reader.IsDBNull(reader.GetOrdinal("ocr_text"))
                ? null
                : reader.GetString(reader.GetOrdinal("ocr_text")),
            DominantColor = reader.IsDBNull(reader.GetOrdinal("dominant_color"))
                ? null
                : reader.GetString(reader.GetOrdinal("dominant_color")),
            ContentType = reader.IsDBNull(reader.GetOrdinal("content_type"))
                ? null
                : reader.GetString(reader.GetOrdinal("content_type")),
            IsScannedDocument = reader.GetBoolean(reader.GetOrdinal("is_scanned_document")),
            SignalsJson = reader.IsDBNull(reader.GetOrdinal("signals_json"))
                ? null
                : reader.GetString(reader.GetOrdinal("signals_json")),
            Confidence = reader.GetDouble(reader.GetOrdinal("confidence")),
            ObservationCount = reader.GetInt32(reader.GetOrdinal("observation_count")),
            IsComplete = reader.GetBoolean(reader.GetOrdinal("is_complete")),
            OriginalProcessingTimeMs = reader.GetInt64(reader.GetOrdinal("original_processing_time_ms")),
            ContributingWavesJson = reader.IsDBNull(reader.GetOrdinal("contributing_waves_json"))
                ? null
                : reader.GetString(reader.GetOrdinal("contributing_waves_json")),
            FirstSeen = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("first_seen")),
            LastSeen = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_seen"))
        };
    }

    private void FlushCallback(object? state)
    {
        _ = FlushPendingWritesAsync(CancellationToken.None);
    }

    private async Task FlushPendingWritesAsync(CancellationToken ct)
    {
        if (_pendingWrites.IsEmpty) return;
        if (!await _flushLock.WaitAsync(0, ct)) return;

        try
        {
            await EnsureInitializedAsync(ct);

            var writes = new List<StoredImageSignature>();
            foreach (var key in _pendingWrites.Keys.ToList())
                if (_pendingWrites.TryRemove(key, out var sig))
                    writes.Add(sig);

            if (writes.Count == 0) return;

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var transaction = await conn.BeginTransactionAsync(ct);

            try
            {
                foreach (var sig in writes)
                {
                    var sql = $@"
                        INSERT INTO {SchemaName}.{TableName} (
                            content_hash, perceptual_hash, file_size, width, height,
                            mime_type, is_animated, frame_count, caption, ocr_text,
                            dominant_color, content_type, is_scanned_document, signals_json,
                            confidence, observation_count, is_complete, original_processing_time_ms,
                            contributing_waves_json, first_seen, last_seen
                        ) VALUES (
                            @content_hash, @perceptual_hash, @file_size, @width, @height,
                            @mime_type, @is_animated, @frame_count, @caption, @ocr_text,
                            @dominant_color, @content_type, @is_scanned_document, @signals_json,
                            @confidence, @observation_count, @is_complete, @original_processing_time_ms,
                            @contributing_waves_json, @first_seen, @last_seen
                        )
                        ON CONFLICT (content_hash) DO UPDATE SET
                            perceptual_hash = EXCLUDED.perceptual_hash,
                            caption = COALESCE(EXCLUDED.caption, {SchemaName}.{TableName}.caption),
                            ocr_text = COALESCE(EXCLUDED.ocr_text, {SchemaName}.{TableName}.ocr_text),
                            dominant_color = COALESCE(EXCLUDED.dominant_color, {SchemaName}.{TableName}.dominant_color),
                            content_type = COALESCE(EXCLUDED.content_type, {SchemaName}.{TableName}.content_type),
                            is_scanned_document = EXCLUDED.is_scanned_document,
                            signals_json = EXCLUDED.signals_json,
                            confidence = EXCLUDED.confidence,
                            observation_count = EXCLUDED.observation_count,
                            is_complete = GREATEST({SchemaName}.{TableName}.is_complete::int, EXCLUDED.is_complete::int)::bool,
                            contributing_waves_json = EXCLUDED.contributing_waves_json,
                            last_seen = EXCLUDED.last_seen";

                    await using var cmd = new NpgsqlCommand(sql, conn, transaction);
                    cmd.Parameters.AddWithValue("@content_hash", sig.ContentHash);
                    cmd.Parameters.AddWithValue("@perceptual_hash", (object?)sig.PerceptualHash ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@file_size", sig.FileSize);
                    cmd.Parameters.AddWithValue("@width", sig.Width);
                    cmd.Parameters.AddWithValue("@height", sig.Height);
                    cmd.Parameters.AddWithValue("@mime_type", (object?)sig.MimeType ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@is_animated", sig.IsAnimated);
                    cmd.Parameters.AddWithValue("@frame_count", sig.FrameCount);
                    cmd.Parameters.AddWithValue("@caption", (object?)sig.Caption ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ocr_text", (object?)sig.OcrText ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@dominant_color", (object?)sig.DominantColor ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@content_type", (object?)sig.ContentType ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@is_scanned_document", sig.IsScannedDocument);
                    cmd.Parameters.AddWithValue("@signals_json", (object?)sig.SignalsJson ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@confidence", sig.Confidence);
                    cmd.Parameters.AddWithValue("@observation_count", sig.ObservationCount);
                    cmd.Parameters.AddWithValue("@is_complete", sig.IsComplete);
                    cmd.Parameters.AddWithValue("@original_processing_time_ms", sig.OriginalProcessingTimeMs);
                    cmd.Parameters.AddWithValue("@contributing_waves_json",
                        (object?)sig.ContributingWavesJson ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@first_seen", sig.FirstSeen);
                    cmd.Parameters.AddWithValue("@last_seen", sig.LastSeen);

                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await transaction.CommitAsync(ct);
                _logger.LogDebug("Flushed {Count} signatures to PostgreSQL", writes.Count);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush pending signature writes");
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            // Create schema if not exists
            var schemaSql = $"CREATE SCHEMA IF NOT EXISTS {SchemaName}";
            await using (var schemaCmd = new NpgsqlCommand(schemaSql, conn))
            {
                await schemaCmd.ExecuteNonQueryAsync(ct);
            }

            var sql = $@"
                CREATE TABLE IF NOT EXISTS {SchemaName}.{TableName} (
                    content_hash TEXT PRIMARY KEY,
                    perceptual_hash TEXT,
                    file_size BIGINT NOT NULL,
                    width INTEGER NOT NULL,
                    height INTEGER NOT NULL,
                    mime_type TEXT,
                    is_animated BOOLEAN NOT NULL DEFAULT FALSE,
                    frame_count INTEGER NOT NULL DEFAULT 1,
                    caption TEXT,
                    ocr_text TEXT,
                    dominant_color TEXT,
                    content_type TEXT,
                    is_scanned_document BOOLEAN NOT NULL DEFAULT FALSE,
                    signals_json JSONB,
                    confidence DOUBLE PRECISION NOT NULL DEFAULT 0.5,
                    observation_count INTEGER NOT NULL DEFAULT 1,
                    is_complete BOOLEAN NOT NULL DEFAULT FALSE,
                    original_processing_time_ms BIGINT NOT NULL DEFAULT 0,
                    contributing_waves_json JSONB,
                    first_seen TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    last_seen TIMESTAMPTZ NOT NULL DEFAULT NOW()
                );

                CREATE INDEX IF NOT EXISTS idx_{TableName}_perceptual_hash
                    ON {SchemaName}.{TableName}(perceptual_hash);
                CREATE INDEX IF NOT EXISTS idx_{TableName}_confidence
                    ON {SchemaName}.{TableName}(confidence);
                CREATE INDEX IF NOT EXISTS idx_{TableName}_last_seen
                    ON {SchemaName}.{TableName}(last_seen);
                CREATE INDEX IF NOT EXISTS idx_{TableName}_content_type
                    ON {SchemaName}.{TableName}(content_type);
                CREATE INDEX IF NOT EXISTS idx_{TableName}_is_scanned
                    ON {SchemaName}.{TableName}(is_scanned_document) WHERE is_scanned_document = TRUE;
                CREATE INDEX IF NOT EXISTS idx_{TableName}_signals
                    ON {SchemaName}.{TableName} USING GIN (signals_json);
            ";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);

            _initialized = true;
            _logger.LogDebug("PostgresImageSignatureStore database initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }
}
