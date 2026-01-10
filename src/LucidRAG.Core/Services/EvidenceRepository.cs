using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LucidRAG.Core.Services.Caching;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Multitenancy;
using LucidRAG.Services.Storage;

namespace LucidRAG.Services;

/// <summary>
/// Repository for evidence artifacts.
/// Coordinates between database metadata and blob storage.
/// </summary>
public interface IEvidenceRepository
{
    /// <summary>
    /// Store an evidence artifact for an entity.
    /// </summary>
    Task<Guid> StoreAsync(
        Guid entityId,
        string artifactType,
        Stream content,
        string mimeType,
        string? producerSource = null,
        string? producerVersion = null,
        double? confidence = null,
        object? metadata = null,
        string? segmentHash = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get the content stream for an artifact.
    /// </summary>
    Task<Stream?> GetStreamAsync(
        Guid artifactId,
        CancellationToken ct = default);

    /// <summary>
    /// Get artifact metadata by ID.
    /// </summary>
    Task<EvidenceArtifact?> GetByIdAsync(
        Guid artifactId,
        CancellationToken ct = default);

    /// <summary>
    /// Get all artifacts for an entity.
    /// </summary>
    Task<IReadOnlyList<EvidenceArtifact>> GetByEntityAsync(
        Guid entityId,
        string[]? artifactTypes = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get available artifact types for an entity.
    /// </summary>
    Task<IReadOnlyList<string>> GetAvailableTypesAsync(
        Guid entityId,
        CancellationToken ct = default);

    /// <summary>
    /// Delete an artifact and its stored content.
    /// </summary>
    Task<bool> DeleteAsync(
        Guid artifactId,
        CancellationToken ct = default);

    /// <summary>
    /// Delete all artifacts for an entity.
    /// </summary>
    Task<int> DeleteAllForEntityAsync(
        Guid entityId,
        CancellationToken ct = default);

    /// <summary>
    /// Check if an artifact exists by content hash (for deduplication).
    /// </summary>
    Task<EvidenceArtifact?> FindByHashAsync(
        Guid entityId,
        string artifactType,
        string contentHash,
        CancellationToken ct = default);

    /// <summary>
    /// Get segment text by content hash. Used for hydrating RAG results from vector store.
    /// Queries segment_text evidence artifacts where metadata contains the content hash.
    /// </summary>
    Task<string?> GetSegmentTextByHashAsync(string contentHash, CancellationToken ct = default);

    /// <summary>
    /// Batch get segment texts by content hashes for efficient hydration.
    /// Returns dictionary mapping contentHash to text.
    /// </summary>
    Task<Dictionary<string, string>> GetSegmentTextsByHashesAsync(
        IEnumerable<string> contentHashes,
        CancellationToken ct = default);
}

public class EvidenceRepository(
    RagDocumentsDbContext db,
    IEvidenceStorage storage,
    ITenantLfuCacheService? cache,
    ITenantAccessor? tenantAccessor,
    ILogger<EvidenceRepository> logger) : IEvidenceRepository
{
    public async Task<Guid> StoreAsync(
        Guid entityId,
        string artifactType,
        Stream content,
        string mimeType,
        string? producerSource = null,
        string? producerVersion = null,
        double? confidence = null,
        object? metadata = null,
        string? segmentHash = null,
        CancellationToken ct = default)
    {
        // Compute content hash for deduplication
        var hashBytes = await SHA256.HashDataAsync(content, ct);
        var contentHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        // Reset stream position for storage
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        // Check for existing artifact with same hash
        var existing = await FindByHashAsync(entityId, artifactType, contentHash, ct);
        if (existing != null)
        {
            logger.LogDebug("Found existing artifact with same hash: {ArtifactId}", existing.Id);
            return existing.Id;
        }

        // Determine storage strategy: inline for text artifacts, blob storage for others
        bool storeInline = ShouldStoreInline(artifactType, mimeType);
        string storagePath;
        long fileSize;
        string? inlineContent = null;

        if (storeInline)
        {
            // Store content inline in database for full-text search
            using var reader = new StreamReader(content);
            inlineContent = await reader.ReadToEndAsync(ct);
            fileSize = System.Text.Encoding.UTF8.GetByteCount(inlineContent);
            storagePath = $"inline:{artifactType}"; // Marker for inline storage
        }
        else
        {
            // Store in blob storage for binary/large content
            var extension = GetExtensionForMimeType(mimeType);
            storagePath = $"{entityId}/{artifactType}/{contentHash}{extension}";
            fileSize = content.CanSeek ? content.Length : 0;

            await storage.StoreAsync(content, storagePath, mimeType, ct);

            if (fileSize == 0)
            {
                var info = await storage.GetInfoAsync(storagePath, ct);
                fileSize = info?.SizeBytes ?? 0;
            }
        }

        // Create database record
        var artifact = new EvidenceArtifact
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            ArtifactType = artifactType,
            MimeType = mimeType,
            StorageBackend = storeInline ? "inline" : storage.ProviderName,
            StoragePath = storagePath,
            FileSizeBytes = fileSize,
            ContentHash = contentHash,
            SegmentHash = segmentHash,  // For fast RAG text hydration lookups
            Content = inlineContent,  // Store inline for text artifacts
            ProducerSource = producerSource,
            ProducerVersion = producerVersion,
            Confidence = confidence,
            Metadata = metadata != null ? JsonSerializer.Serialize(metadata) : null
        };

        db.EvidenceArtifacts.Add(artifact);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Stored evidence artifact {ArtifactId} type={Type} for entity {EntityId} ({Size} bytes)",
            artifact.Id, artifactType, entityId, fileSize);

        return artifact.Id;
    }

    public async Task<Stream?> GetStreamAsync(Guid artifactId, CancellationToken ct = default)
    {
        var artifact = await db.EvidenceArtifacts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == artifactId, ct);

        if (artifact == null)
        {
            logger.LogDebug("Artifact {ArtifactId} not found", artifactId);
            return null;
        }

        // If stored inline, return content from database
        if (artifact.StorageBackend == "inline" && artifact.Content != null)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(artifact.Content);
            return new MemoryStream(bytes);
        }

        // Otherwise retrieve from blob storage
        return await storage.RetrieveAsync(artifact.StoragePath, ct);
    }

    public async Task<EvidenceArtifact?> GetByIdAsync(Guid artifactId, CancellationToken ct = default)
    {
        return await db.EvidenceArtifacts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == artifactId, ct);
    }

    public async Task<IReadOnlyList<EvidenceArtifact>> GetByEntityAsync(
        Guid entityId,
        string[]? artifactTypes = null,
        CancellationToken ct = default)
    {
        var query = db.EvidenceArtifacts
            .AsNoTracking()
            .Where(a => a.EntityId == entityId);

        if (artifactTypes is { Length: > 0 })
        {
            query = query.Where(a => artifactTypes.Contains(a.ArtifactType));
        }

        return await query
            .OrderBy(a => a.ArtifactType)
            .ThenByDescending(a => a.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetAvailableTypesAsync(
        Guid entityId,
        CancellationToken ct = default)
    {
        return await db.EvidenceArtifacts
            .AsNoTracking()
            .Where(a => a.EntityId == entityId)
            .Select(a => a.ArtifactType)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid artifactId, CancellationToken ct = default)
    {
        var artifact = await db.EvidenceArtifacts.FindAsync([artifactId], ct);
        if (artifact == null)
        {
            return false;
        }

        // Delete from blob storage
        try
        {
            await storage.DeleteAsync(artifact.StoragePath, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete blob for artifact {ArtifactId}", artifactId);
        }

        // Delete database record
        db.EvidenceArtifacts.Remove(artifact);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Deleted evidence artifact {ArtifactId}", artifactId);
        return true;
    }

    public async Task<int> DeleteAllForEntityAsync(Guid entityId, CancellationToken ct = default)
    {
        var artifacts = await db.EvidenceArtifacts
            .Where(a => a.EntityId == entityId)
            .ToListAsync(ct);

        if (artifacts.Count == 0)
        {
            return 0;
        }

        // Delete blobs
        foreach (var artifact in artifacts)
        {
            try
            {
                await storage.DeleteAsync(artifact.StoragePath, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete blob for artifact {ArtifactId}", artifact.Id);
            }
        }

        // Delete database records
        db.EvidenceArtifacts.RemoveRange(artifacts);
        await db.SaveChangesAsync(ct);

        // Invalidate cache entries
        if (cache != null && tenantAccessor?.Current?.TenantId != null)
        {
            var tenantId = tenantAccessor.Current.TenantId;
            var segmentHashes = artifacts
                .Where(a => !string.IsNullOrEmpty(a.SegmentHash))
                .Select(a => a.SegmentHash!)
                .ToList();

            foreach (var hash in segmentHashes)
            {
                cache.InvalidateEvidence(tenantId, hash);
            }

            logger.LogDebug("Invalidated {Count} cache entries for tenant {TenantId}",
                segmentHashes.Count, tenantId);
        }

        logger.LogInformation("Deleted {Count} evidence artifacts for entity {EntityId}",
            artifacts.Count, entityId);

        return artifacts.Count;
    }

    public async Task<EvidenceArtifact?> FindByHashAsync(
        Guid entityId,
        string artifactType,
        string contentHash,
        CancellationToken ct = default)
    {
        return await db.EvidenceArtifacts
            .AsNoTracking()
            .FirstOrDefaultAsync(a =>
                a.EntityId == entityId &&
                a.ArtifactType == artifactType &&
                a.ContentHash == contentHash, ct);
    }

    public async Task<string?> GetSegmentTextByHashAsync(string contentHash, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(contentHash))
            return null;

        // Find segment_text artifact by SegmentHash
        var artifact = await db.EvidenceArtifacts
            .AsNoTracking()
            .FirstOrDefaultAsync(a =>
                a.ArtifactType == EvidenceTypes.SegmentText &&
                a.SegmentHash == contentHash, ct);

        if (artifact == null)
        {
            logger.LogDebug("No segment text found for hash: {Hash}", contentHash);
            return null;
        }

        // If stored inline, return directly from database (fast!)
        if (artifact.StorageBackend == "inline" && artifact.Content != null)
        {
            return artifact.Content;
        }

        // Otherwise read from blob storage
        try
        {
            using var stream = await storage.RetrieveAsync(artifact.StoragePath, ct);
            if (stream == null)
                return null;

            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to retrieve segment text for hash: {Hash}", contentHash);
            return null;
        }
    }

    public async Task<Dictionary<string, string>> GetSegmentTextsByHashesAsync(
        IEnumerable<string> contentHashes,
        CancellationToken ct = default)
    {
        var hashList = contentHashes.Where(h => !string.IsNullOrEmpty(h)).Distinct().ToList();
        if (hashList.Count == 0)
            return new Dictionary<string, string>();

        var result = new Dictionary<string, string>();
        var cacheMisses = new List<string>();

        // Try cache first (if available and in multi-tenant mode)
        if (cache != null && tenantAccessor?.Current?.TenantId != null)
        {
            var tenantId = tenantAccessor.Current.TenantId;
            var cachedResults = await cache.GetEvidenceTextsAsync(tenantId, hashList);

            foreach (var hash in hashList)
            {
                if (cachedResults.TryGetValue(hash, out var text))
                {
                    result[hash] = text;
                }
                else
                {
                    cacheMisses.Add(hash);
                }
            }

            logger.LogDebug("Evidence cache: {Hits} hits, {Misses} misses for tenant {TenantId}",
                result.Count, cacheMisses.Count, tenantId);
        }
        else
        {
            // No cache available, all are cache misses
            cacheMisses = hashList;
        }

        // Fetch cache misses from database
        if (cacheMisses.Count > 0)
        {
            var artifacts = await db.EvidenceArtifacts
                .AsNoTracking()
                .Where(a =>
                    a.ArtifactType == EvidenceTypes.SegmentText &&
                    a.SegmentHash != null &&
                    cacheMisses.Contains(a.SegmentHash))
                .ToListAsync(ct);

            // Read text for each artifact
            foreach (var artifact in artifacts)
            {
                if (string.IsNullOrEmpty(artifact.SegmentHash))
                    continue;

                try
                {
                    string text;

                    // If stored inline, use database content (much faster!)
                    if (artifact.StorageBackend == "inline" && artifact.Content != null)
                    {
                        text = artifact.Content;
                    }
                    else
                    {
                        // Otherwise read from blob storage
                        using var stream = await storage.RetrieveAsync(artifact.StoragePath, ct);
                        if (stream == null)
                            continue;

                        using var reader = new StreamReader(stream);
                        text = await reader.ReadToEndAsync(ct);
                    }

                    result[artifact.SegmentHash] = text;

                    // Cache for future queries (if cache available)
                    if (cache != null && tenantAccessor?.Current?.TenantId != null)
                    {
                        cache.CacheEvidenceText(tenantAccessor.Current.TenantId, artifact.SegmentHash, text);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to retrieve segment text for hash: {Hash}", artifact.SegmentHash);
                }
            }
        }

        logger.LogDebug("Retrieved {Count}/{Requested} segment texts from evidence",
            result.Count, hashList.Count);

        return result;
    }

    /// <summary>
    /// Determine if content should be stored inline (database) or in blob storage.
    /// Text artifacts (segment_text, transcript, etc.) are stored inline for PostgreSQL FTS.
    /// Binary/large content (images, PDFs, etc.) uses blob storage.
    /// </summary>
    private static bool ShouldStoreInline(string artifactType, string mimeType)
    {
        // Store text artifacts inline for PostgreSQL full-text search
        return artifactType switch
        {
            EvidenceTypes.SegmentText => true,
            EvidenceTypes.DocumentText => true,
            EvidenceTypes.OcrText => true,
            EvidenceTypes.Transcript => true,
            EvidenceTypes.TranscriptSegments => false,  // JSON, can be large
            EvidenceTypes.LlmSummary => true,
            EvidenceTypes.LlmClaims => true,
            EvidenceTypes.ImageCaption => true,
            EvidenceTypes.ImageAltText => true,
            EvidenceTypes.ImageOcrText => true,
            _ => mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
                 !mimeType.Contains("json", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static string GetExtensionForMimeType(string mimeType)
    {
        return mimeType.ToLowerInvariant() switch
        {
            "text/plain" => ".txt",
            "application/json" => ".json",
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "audio/wav" => ".wav",
            "audio/mp3" or "audio/mpeg" => ".mp3",
            "video/mp4" => ".mp4",
            "application/pdf" => ".pdf",
            _ => ".bin"
        };
    }
}
