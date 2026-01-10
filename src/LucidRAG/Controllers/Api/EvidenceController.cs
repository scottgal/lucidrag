using Microsoft.AspNetCore.Mvc;
using LucidRAG.Entities;
using LucidRAG.Services;

namespace LucidRAG.Controllers.Api;

/// <summary>
/// API for accessing evidence artifacts associated with retrieval entities.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class EvidenceController(
    IEvidenceRepository evidenceRepository,
    ILogger<EvidenceController> logger) : ControllerBase
{
    /// <summary>
    /// List all evidence artifacts for an entity.
    /// </summary>
    [HttpGet("{entityId:guid}")]
    public async Task<ActionResult<IEnumerable<EvidenceArtifactDto>>> GetByEntity(
        Guid entityId,
        [FromQuery] string[]? types = null,
        CancellationToken ct = default)
    {
        var artifacts = await evidenceRepository.GetByEntityAsync(entityId, types, ct);

        return Ok(artifacts.Select(a => new EvidenceArtifactDto
        {
            Id = a.Id,
            EntityId = a.EntityId,
            ArtifactType = a.ArtifactType,
            MimeType = a.MimeType,
            FileSizeBytes = a.FileSizeBytes,
            ProducerSource = a.ProducerSource,
            ProducerVersion = a.ProducerVersion,
            Confidence = a.Confidence,
            CreatedAt = a.CreatedAt
        }));
    }

    /// <summary>
    /// List available evidence types for an entity.
    /// </summary>
    [HttpGet("{entityId:guid}/types")]
    public async Task<ActionResult<IEnumerable<string>>> GetAvailableTypes(
        Guid entityId,
        CancellationToken ct = default)
    {
        var types = await evidenceRepository.GetAvailableTypesAsync(entityId, ct);
        return Ok(types);
    }

    /// <summary>
    /// Get artifact metadata.
    /// </summary>
    [HttpGet("artifact/{artifactId:guid}")]
    public async Task<ActionResult<EvidenceArtifactDto>> GetArtifact(
        Guid artifactId,
        CancellationToken ct = default)
    {
        var artifact = await evidenceRepository.GetByIdAsync(artifactId, ct);
        if (artifact == null)
        {
            return NotFound();
        }

        return Ok(new EvidenceArtifactDto
        {
            Id = artifact.Id,
            EntityId = artifact.EntityId,
            ArtifactType = artifact.ArtifactType,
            MimeType = artifact.MimeType,
            FileSizeBytes = artifact.FileSizeBytes,
            ContentHash = artifact.ContentHash,
            ProducerSource = artifact.ProducerSource,
            ProducerVersion = artifact.ProducerVersion,
            Confidence = artifact.Confidence,
            Metadata = artifact.Metadata,
            CreatedAt = artifact.CreatedAt
        });
    }

    /// <summary>
    /// Download artifact content.
    /// </summary>
    [HttpGet("artifact/{artifactId:guid}/download")]
    public async Task<IActionResult> DownloadArtifact(
        Guid artifactId,
        CancellationToken ct = default)
    {
        var artifact = await evidenceRepository.GetByIdAsync(artifactId, ct);
        if (artifact == null)
        {
            return NotFound();
        }

        var stream = await evidenceRepository.GetStreamAsync(artifactId, ct);
        if (stream == null)
        {
            logger.LogWarning("Artifact {ArtifactId} exists in DB but content not found in storage", artifactId);
            return NotFound("Artifact content not found");
        }

        // Generate a filename
        var extension = GetExtensionForMimeType(artifact.MimeType);
        var filename = $"{artifact.ArtifactType}_{artifact.Id:N}{extension}";

        return File(stream, artifact.MimeType, filename);
    }

    /// <summary>
    /// Delete an artifact.
    /// </summary>
    [HttpDelete("artifact/{artifactId:guid}")]
    public async Task<IActionResult> DeleteArtifact(
        Guid artifactId,
        CancellationToken ct = default)
    {
        var deleted = await evidenceRepository.DeleteAsync(artifactId, ct);
        if (!deleted)
        {
            return NotFound();
        }

        return NoContent();
    }

    /// <summary>
    /// Upload new evidence for an entity.
    /// </summary>
    [HttpPost("{entityId:guid}")]
    public async Task<ActionResult<EvidenceArtifactDto>> UploadEvidence(
        Guid entityId,
        [FromForm] string artifactType,
        [FromForm] IFormFile file,
        [FromForm] string? producerSource = null,
        [FromForm] string? producerVersion = null,
        [FromForm] double? confidence = null,
        CancellationToken ct = default)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("No file provided");
        }

        await using var stream = file.OpenReadStream();

        var artifactId = await evidenceRepository.StoreAsync(
            entityId,
            artifactType,
            stream,
            file.ContentType ?? "application/octet-stream",
            producerSource,
            producerVersion,
            confidence,
            null,     // metadata
            null,     // segmentHash
            ct);

        var artifact = await evidenceRepository.GetByIdAsync(artifactId, ct);
        if (artifact == null)
        {
            return StatusCode(500, "Failed to retrieve stored artifact");
        }

        return CreatedAtAction(
            nameof(GetArtifact),
            new { artifactId },
            new EvidenceArtifactDto
            {
                Id = artifact.Id,
                EntityId = artifact.EntityId,
                ArtifactType = artifact.ArtifactType,
                MimeType = artifact.MimeType,
                FileSizeBytes = artifact.FileSizeBytes,
                ContentHash = artifact.ContentHash,
                ProducerSource = artifact.ProducerSource,
                ProducerVersion = artifact.ProducerVersion,
                Confidence = artifact.Confidence,
                CreatedAt = artifact.CreatedAt
            });
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

/// <summary>
/// DTO for evidence artifact responses.
/// </summary>
public record EvidenceArtifactDto
{
    public Guid Id { get; init; }
    public Guid EntityId { get; init; }
    public required string ArtifactType { get; init; }
    public required string MimeType { get; init; }
    public long FileSizeBytes { get; init; }
    public string? ContentHash { get; init; }
    public string? ProducerSource { get; init; }
    public string? ProducerVersion { get; init; }
    public double? Confidence { get; init; }
    public string? Metadata { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
