using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LucidRAG.Data;
using LucidRAG.Entities;
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
}

public class EvidenceRepository(
    RagDocumentsDbContext db,
    IEvidenceStorage storage,
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

        // Generate storage path: {entityId}/{artifactType}/{hash}{extension}
        var extension = GetExtensionForMimeType(mimeType);
        var storagePath = $"{entityId}/{artifactType}/{contentHash}{extension}";

        // Get file size
        var fileSize = content.CanSeek ? content.Length : 0;

        // Store in blob storage
        await storage.StoreAsync(content, storagePath, mimeType, ct);

        // If we couldn't get size from stream, get it from storage
        if (fileSize == 0)
        {
            var info = await storage.GetInfoAsync(storagePath, ct);
            fileSize = info?.SizeBytes ?? 0;
        }

        // Create database record
        var artifact = new EvidenceArtifact
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            ArtifactType = artifactType,
            MimeType = mimeType,
            StorageBackend = storage.ProviderName,
            StoragePath = storagePath,
            FileSizeBytes = fileSize,
            ContentHash = contentHash,
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
