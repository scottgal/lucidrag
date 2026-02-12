using System.Security.Claims;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Identity;
using LucidRAG.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LucidRAG.Controllers.Api;

[ApiController]
[Route("api/saas/admin")]
[Authorize]
public class SaasAdminController(
    IApiKeyService apiKeyService,
    UserManager<ApplicationUser> userManager,
    RagDocumentsDbContext db,
    ILogger<SaasAdminController> logger) : ControllerBase
{
    // --- Key Management ---

    [HttpPost("keys")]
    public async Task<IActionResult> CreateKey([FromBody] CreateKeyRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        try
        {
            var (fullKey, entity) = await apiKeyService.CreateKeyAsync(userId, user.Email, request.Description, ct);

            return Ok(new
            {
                id = entity.Id,
                key = fullKey, // Only shown ONCE
                prefix = entity.KeyPrefix,
                description = entity.Description,
                plan = entity.Plan,
                createdAt = entity.CreatedAt,
                message = "Store this key securely. It will not be shown again."
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("keys")]
    public async Task<IActionResult> ListKeys(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var keys = await apiKeyService.GetByUserIdAsync(userId, ct);

        return Ok(new
        {
            keys = keys.Select(k => new
            {
                id = k.Id,
                prefix = k.KeyPrefix,
                description = k.Description,
                plan = k.Plan,
                isActive = k.IsActive,
                allowChat = k.AllowChat,
                allowSearch = k.AllowSearch,
                totalRequests = k.TotalRequests,
                lastUsedAt = k.LastUsedAt,
                createdAt = k.CreatedAt,
                readDomainCount = k.ReadDomains.Count,
                hasIndexingSource = k.IndexingSource is not null,
                indexingSourceType = k.IndexingSource?.SourceType.ToString(),
                indexingSourceValue = k.IndexingSource?.SourceValue
            })
        });
    }

    [HttpGet("keys/{id:guid}")]
    public async Task<IActionResult> GetKey(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var key = await apiKeyService.GetByIdAsync(id, ct);
        if (key is null || key.UserId != userId) return NotFound(new { error = "API key not found" });

        return Ok(new
        {
            id = key.Id,
            prefix = key.KeyPrefix,
            description = key.Description,
            plan = key.Plan,
            isActive = key.IsActive,
            allowChat = key.AllowChat,
            allowSearch = key.AllowSearch,
            rateLimitPerMinute = key.RateLimitPerMinute,
            rateLimitPerDay = key.RateLimitPerDay,
            totalRequests = key.TotalRequests,
            lastUsedAt = key.LastUsedAt,
            createdAt = key.CreatedAt,
            collectionId = key.CollectionId,
            indexingSource = key.IndexingSource is not null
                ? new
                {
                    id = key.IndexingSource.Id,
                    type = key.IndexingSource.SourceType.ToString(),
                    value = key.IndexingSource.SourceValue,
                    documentCount = key.IndexingSource.DocumentCount,
                    maxDocuments = key.IndexingSource.MaxDocuments,
                    crawlStatus = key.IndexingSource.CrawlStatus,
                    lastCrawledAt = key.IndexingSource.LastCrawledAt
                }
                : null,
            readDomains = key.ReadDomains.Select(d => new
            {
                id = d.Id,
                domain = d.Domain,
                createdAt = d.CreatedAt
            }),
            embedSnippet = GenerateEmbedSnippet(key)
        });
    }

    [HttpDelete("keys/{id:guid}")]
    public async Task<IActionResult> RevokeKey(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var key = await apiKeyService.GetByIdAsync(id, ct);
        if (key is null || key.UserId != userId) return NotFound(new { error = "API key not found" });

        await apiKeyService.RevokeKeyAsync(id, ct);
        return Ok(new { success = true });
    }

    [HttpPost("keys/{id:guid}/rotate")]
    public async Task<IActionResult> RotateKey(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var existingKey = await apiKeyService.GetByIdAsync(id, ct);
        if (existingKey is null || existingKey.UserId != userId)
            return NotFound(new { error = "API key not found" });

        try
        {
            var (fullKey, entity) = await apiKeyService.RotateKeyAsync(id, ct);
            return Ok(new
            {
                id = entity.Id,
                key = fullKey,
                prefix = entity.KeyPrefix,
                message = "New key generated. Old key has been revoked. Store this key securely."
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // --- Indexing Source ---

    [HttpPut("keys/{id:guid}/source")]
    public async Task<IActionResult> SetIndexingSource(Guid id, [FromBody] SetSourceRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var key = await apiKeyService.GetByIdAsync(id, ct);
        if (key is null || key.UserId != userId) return NotFound(new { error = "API key not found" });

        if (!Enum.TryParse<SourceType>(request.Type, true, out var sourceType))
            return BadRequest(new { error = "Invalid source type. Use 'Domain' or 'GitHubRepo'." });

        if (string.IsNullOrWhiteSpace(request.Value))
            return BadRequest(new { error = "Source value is required." });

        var source = await apiKeyService.SetIndexingSourceAsync(id, sourceType, request.Value, ct);
        return Ok(new
        {
            id = source.Id,
            type = source.SourceType.ToString(),
            value = source.SourceValue,
            maxDocuments = source.MaxDocuments
        });
    }

    [HttpDelete("keys/{id:guid}/source")]
    public async Task<IActionResult> RemoveIndexingSource(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var key = await apiKeyService.GetByIdAsync(id, ct);
        if (key is null || key.UserId != userId) return NotFound(new { error = "API key not found" });

        await apiKeyService.RemoveIndexingSourceAsync(id, ct);
        return Ok(new { success = true });
    }

    [HttpPost("keys/{id:guid}/source/crawl")]
    public async Task<IActionResult> TriggerCrawl(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var key = await apiKeyService.GetByIdAsync(id, ct);
        if (key is null || key.UserId != userId) return NotFound(new { error = "API key not found" });
        if (key.IndexingSource is null) return BadRequest(new { error = "No indexing source configured." });

        // TODO: Wire WebCrawlerService / GitHubRepoIndexer here in Phase 4
        // For now, return accepted status
        logger.LogInformation("Crawl triggered for key {KeyPrefix}, source: {Source}",
            key.KeyPrefix, key.IndexingSource.SourceValue);

        return Accepted(new
        {
            message = "Crawl initiated",
            source = key.IndexingSource.SourceValue,
            type = key.IndexingSource.SourceType.ToString()
        });
    }

    // --- Read Domains ---

    [HttpGet("keys/{id:guid}/domains")]
    public async Task<IActionResult> ListReadDomains(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var key = await apiKeyService.GetByIdAsync(id, ct);
        if (key is null || key.UserId != userId) return NotFound(new { error = "API key not found" });

        return Ok(new
        {
            domains = key.ReadDomains.Select(d => new
            {
                id = d.Id,
                domain = d.Domain,
                createdAt = d.CreatedAt
            })
        });
    }

    [HttpPost("keys/{id:guid}/domains")]
    public async Task<IActionResult> AddReadDomain(Guid id, [FromBody] AddDomainRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var key = await apiKeyService.GetByIdAsync(id, ct);
        if (key is null || key.UserId != userId) return NotFound(new { error = "API key not found" });

        if (string.IsNullOrWhiteSpace(request.Domain))
            return BadRequest(new { error = "Domain is required." });

        try
        {
            var domain = await apiKeyService.AddReadDomainAsync(id, request.Domain, ct);
            return Ok(new
            {
                id = domain.Id,
                domain = domain.Domain,
                createdAt = domain.CreatedAt
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("keys/{id:guid}/domains/{domainId:guid}")]
    public async Task<IActionResult> RemoveReadDomain(Guid id, Guid domainId, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var key = await apiKeyService.GetByIdAsync(id, ct);
        if (key is null || key.UserId != userId) return NotFound(new { error = "API key not found" });

        await apiKeyService.RemoveReadDomainAsync(id, domainId, ct);
        return Ok(new { success = true });
    }

    // --- Documents ---

    [HttpGet("keys/{id:guid}/documents")]
    public async Task<IActionResult> ListDocuments(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var key = await apiKeyService.GetByIdAsync(id, ct);
        if (key is null || key.UserId != userId) return NotFound(new { error = "API key not found" });
        if (key.CollectionId is null) return Ok(new { documents = Array.Empty<object>() });

        var docs = await db.Documents
            .Where(d => d.CollectionId == key.CollectionId)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new
            {
                id = d.Id,
                name = d.Name,
                status = d.Status.ToString(),
                segmentCount = d.SegmentCount,
                sourceUrl = d.SourceUrl,
                createdAt = d.CreatedAt,
                processedAt = d.ProcessedAt
            })
            .ToListAsync(ct);

        return Ok(new { documents = docs });
    }

    [HttpDelete("keys/{id:guid}/documents/{docId:guid}")]
    public async Task<IActionResult> RemoveDocument(Guid id, Guid docId, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var key = await apiKeyService.GetByIdAsync(id, ct);
        if (key is null || key.UserId != userId) return NotFound(new { error = "API key not found" });

        var doc = await db.Documents.FirstOrDefaultAsync(
            d => d.Id == docId && d.CollectionId == key.CollectionId, ct);
        if (doc is null) return NotFound(new { error = "Document not found" });

        db.Documents.Remove(doc);
        await db.SaveChangesAsync(ct);

        // Decrement document count on indexing source
        if (key.IndexingSource is not null)
        {
            key.IndexingSource.DocumentCount = Math.Max(0, key.IndexingSource.DocumentCount - 1);
            await db.SaveChangesAsync(ct);
        }

        return Ok(new { success = true });
    }

    // --- Usage ---

    [HttpGet("keys/{id:guid}/usage")]
    public async Task<IActionResult> GetUsage(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var key = await apiKeyService.GetByIdAsync(id, ct);
        if (key is null || key.UserId != userId) return NotFound(new { error = "API key not found" });

        var documentCount = key.CollectionId is not null
            ? await db.Documents.CountAsync(d => d.CollectionId == key.CollectionId, ct)
            : 0;

        return Ok(new
        {
            totalRequests = key.TotalRequests,
            lastUsedAt = key.LastUsedAt,
            documentCount,
            maxDocuments = key.IndexingSource?.MaxDocuments ?? 25,
            readDomainCount = key.ReadDomains.Count,
            maxReadDomains = 5,
            plan = key.Plan
        });
    }

    private static string GenerateEmbedSnippet(ApiKeyEntity key)
    {
        return $"""
                <div id="lucidrag-search"></div>
                <script src="https://search.lucidrag.com/widget/lucidrag-search.js"
                        data-api-key="{key.KeyPrefix}..."
                        data-mode="{(key.AllowChat ? "both" : "search")}"
                        data-theme="auto">
                </script>
                """;
    }
}

public record CreateKeyRequest(string? Description = null);
public record SetSourceRequest(string Type, string Value);
public record AddDomainRequest(string Domain);
