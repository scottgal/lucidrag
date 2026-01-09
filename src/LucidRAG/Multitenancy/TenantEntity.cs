using System.ComponentModel.DataAnnotations;

namespace LucidRAG.Multitenancy;

/// <summary>
/// Database entity for tenant registration.
/// Stored in the public schema, shared across all tenants.
/// </summary>
public class TenantEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Unique tenant identifier (e.g., "acme").
    /// Used in subdomain: acme.lucidrag.com
    /// </summary>
    [Required]
    [MaxLength(64)]
    public required string TenantId { get; set; }

    /// <summary>
    /// PostgreSQL schema name for this tenant.
    /// Format: "tenant_{tenantId}"
    /// </summary>
    [Required]
    [MaxLength(128)]
    public required string SchemaName { get; set; }

    /// <summary>
    /// Qdrant collection name for this tenant's vectors.
    /// Format: "tenant_{tenantId}_vectors"
    /// </summary>
    [Required]
    [MaxLength(128)]
    public required string QdrantCollection { get; set; }

    /// <summary>
    /// Display name for the tenant.
    /// </summary>
    [MaxLength(256)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Contact email for the tenant.
    /// </summary>
    [MaxLength(256)]
    public string? ContactEmail { get; set; }

    /// <summary>
    /// Whether this tenant is active.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether the tenant schema has been provisioned.
    /// </summary>
    public bool IsProvisioned { get; set; } = false;

    /// <summary>
    /// Tenant-specific settings (JSON).
    /// </summary>
    public string? Settings { get; set; }

    /// <summary>
    /// Subscription tier or plan.
    /// </summary>
    [MaxLength(32)]
    public string? Plan { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProvisionedAt { get; set; }
    public DateTimeOffset? LastAccessedAt { get; set; }
}

/// <summary>
/// Subscription plans for tenants.
/// </summary>
public static class TenantPlans
{
    public const string Free = "free";
    public const string Starter = "starter";
    public const string Pro = "pro";
    public const string Enterprise = "enterprise";
}
