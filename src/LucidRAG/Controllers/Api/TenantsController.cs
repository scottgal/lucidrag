using Microsoft.AspNetCore.Mvc;
using LucidRAG.Multitenancy;

namespace LucidRAG.Controllers.Api;

/// <summary>
/// API for managing tenants in multi-tenant mode.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class TenantsController(
    ITenantProvisioningService provisioningService,
    ITenantAccessor tenantAccessor,
    ILogger<TenantsController> logger) : ControllerBase
{
    /// <summary>
    /// List all tenants.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<TenantDto>>> List(
        [FromQuery] bool? isActive = null,
        CancellationToken ct = default)
    {
        var tenants = await provisioningService.ListTenantsAsync(isActive, ct);
        return Ok(tenants.Select(MapToDto));
    }

    /// <summary>
    /// Get a tenant by ID.
    /// </summary>
    [HttpGet("{tenantId}")]
    public async Task<ActionResult<TenantDto>> Get(string tenantId, CancellationToken ct = default)
    {
        var tenant = await provisioningService.GetTenantAsync(tenantId, ct);
        if (tenant == null)
        {
            return NotFound();
        }

        return Ok(MapToDto(tenant));
    }

    /// <summary>
    /// Get the current tenant context.
    /// </summary>
    [HttpGet("current")]
    public ActionResult<TenantContextDto> GetCurrent()
    {
        var current = tenantAccessor.Current;
        if (current == null)
        {
            return NotFound(new { message = "No tenant context available" });
        }

        return Ok(new TenantContextDto
        {
            TenantId = current.TenantId,
            SchemaName = current.SchemaName,
            QdrantCollection = current.QdrantCollection,
            DisplayName = current.DisplayName,
            IsActive = current.IsActive
        });
    }

    /// <summary>
    /// Provision a new tenant.
    /// Creates database schema and vector collection.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<TenantDto>> Create(
        [FromBody] CreateTenantRequest request,
        CancellationToken ct = default)
    {
        // Check if tenant already exists
        if (await provisioningService.ExistsAsync(request.TenantId, ct))
        {
            return Conflict(new { message = $"Tenant '{request.TenantId}' already exists" });
        }

        logger.LogInformation("Creating tenant: {TenantId}", request.TenantId);

        var context = await provisioningService.ProvisionAsync(
            request.TenantId,
            request.DisplayName,
            request.ContactEmail,
            request.Plan,
            ct);

        var tenant = await provisioningService.GetTenantAsync(request.TenantId, ct);

        return CreatedAtAction(
            nameof(Get),
            new { tenantId = request.TenantId },
            MapToDto(tenant!));
    }

    /// <summary>
    /// Update tenant status (activate/deactivate).
    /// </summary>
    [HttpPatch("{tenantId}/status")]
    public async Task<IActionResult> UpdateStatus(
        string tenantId,
        [FromBody] UpdateTenantStatusRequest request,
        CancellationToken ct = default)
    {
        if (!await provisioningService.ExistsAsync(tenantId, ct))
        {
            return NotFound();
        }

        await provisioningService.UpdateStatusAsync(tenantId, request.IsActive, ct);
        return NoContent();
    }

    /// <summary>
    /// Run migrations for a tenant.
    /// </summary>
    [HttpPost("{tenantId}/migrate")]
    public async Task<IActionResult> Migrate(string tenantId, CancellationToken ct = default)
    {
        if (!await provisioningService.ExistsAsync(tenantId, ct))
        {
            return NotFound();
        }

        await provisioningService.MigrateTenantAsync(tenantId, ct);
        return NoContent();
    }

    /// <summary>
    /// Deprovision a tenant.
    /// WARNING: This deletes all tenant data permanently.
    /// </summary>
    [HttpDelete("{tenantId}")]
    public async Task<IActionResult> Delete(
        string tenantId,
        [FromQuery] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!confirm)
        {
            return BadRequest(new
            {
                message = "Tenant deletion requires confirmation",
                hint = "Add ?confirm=true to permanently delete the tenant and all its data"
            });
        }

        if (!await provisioningService.ExistsAsync(tenantId, ct))
        {
            return NotFound();
        }

        logger.LogWarning("Deprovisioning tenant: {TenantId}", tenantId);
        await provisioningService.DeprovisionAsync(tenantId, ct);

        return NoContent();
    }

    private static TenantDto MapToDto(TenantEntity entity) => new()
    {
        Id = entity.Id,
        TenantId = entity.TenantId,
        SchemaName = entity.SchemaName,
        QdrantCollection = entity.QdrantCollection,
        DisplayName = entity.DisplayName,
        ContactEmail = entity.ContactEmail,
        Plan = entity.Plan,
        IsActive = entity.IsActive,
        IsProvisioned = entity.IsProvisioned,
        CreatedAt = entity.CreatedAt,
        ProvisionedAt = entity.ProvisionedAt
    };
}

/// <summary>
/// Request to create a new tenant.
/// </summary>
public record CreateTenantRequest
{
    /// <summary>
    /// Unique tenant identifier (alphanumeric, lowercase).
    /// Will be used in subdomain: {tenantId}.lucidrag.com
    /// </summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// Display name for the tenant.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Contact email for the tenant.
    /// </summary>
    public string? ContactEmail { get; init; }

    /// <summary>
    /// Subscription plan: free, starter, pro, enterprise.
    /// </summary>
    public string? Plan { get; init; }
}

/// <summary>
/// Request to update tenant status.
/// </summary>
public record UpdateTenantStatusRequest
{
    public bool IsActive { get; init; }
}

/// <summary>
/// DTO for tenant responses.
/// </summary>
public record TenantDto
{
    public Guid Id { get; init; }
    public required string TenantId { get; init; }
    public required string SchemaName { get; init; }
    public required string QdrantCollection { get; init; }
    public string? DisplayName { get; init; }
    public string? ContactEmail { get; init; }
    public string? Plan { get; init; }
    public bool IsActive { get; init; }
    public bool IsProvisioned { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ProvisionedAt { get; init; }
}

/// <summary>
/// DTO for current tenant context.
/// </summary>
public record TenantContextDto
{
    public required string TenantId { get; init; }
    public required string SchemaName { get; init; }
    public required string QdrantCollection { get; init; }
    public string? DisplayName { get; init; }
    public bool IsActive { get; init; }
}
