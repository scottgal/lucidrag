namespace LucidRAG.Multitenancy;

/// <summary>
/// Represents the current tenant context for a request.
/// Contains all tenant-specific configuration needed for data isolation.
/// </summary>
public class TenantContext
{
    /// <summary>
    /// Unique tenant identifier (e.g., "acme", "contoso").
    /// </summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// PostgreSQL schema name for this tenant's data.
    /// Format: "tenant_{tenantId}"
    /// </summary>
    public required string SchemaName { get; init; }

    /// <summary>
    /// Qdrant collection name for this tenant's vectors.
    /// Format: "tenant_{tenantId}_vectors"
    /// </summary>
    public required string QdrantCollection { get; init; }

    /// <summary>
    /// Display name for the tenant.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Whether this tenant is active.
    /// </summary>
    public bool IsActive { get; init; } = true;

    /// <summary>
    /// Tenant-specific settings (JSON).
    /// </summary>
    public string? Settings { get; init; }

    /// <summary>
    /// When this tenant was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Create a tenant context from a tenant ID.
    /// </summary>
    public static TenantContext FromTenantId(string tenantId, string? displayName = null)
    {
        var sanitizedId = SanitizeTenantId(tenantId);
        return new TenantContext
        {
            TenantId = sanitizedId,
            SchemaName = $"tenant_{sanitizedId}",
            QdrantCollection = $"tenant_{sanitizedId}_vectors",
            DisplayName = displayName ?? tenantId,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Sanitize tenant ID for use in schema/collection names.
    /// </summary>
    private static string SanitizeTenantId(string tenantId)
    {
        // Only allow alphanumeric and underscores, lowercase
        return new string(tenantId
            .ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == '_')
            .ToArray());
    }
}

// ITenantResolver interface is in the web project (uses HttpContext)

/// <summary>
/// Provides access to the current tenant context.
/// </summary>
public interface ITenantAccessor
{
    /// <summary>
    /// The current tenant context, or null if not in a tenant context.
    /// </summary>
    TenantContext? Current { get; set; }
}

/// <summary>
/// Scoped service that holds the current tenant context.
/// </summary>
public class TenantAccessor : ITenantAccessor
{
    public TenantContext? Current { get; set; }
}

/// <summary>
/// Constants for multi-tenancy.
/// </summary>
public static class TenantConstants
{
    /// <summary>
    /// The default/system tenant for non-tenant-specific operations.
    /// </summary>
    public const string DefaultTenantId = "default";

    /// <summary>
    /// HTTP header for tenant ID override (for API clients).
    /// </summary>
    public const string TenantIdHeader = "X-Tenant-Id";

    /// <summary>
    /// Query parameter for tenant ID override.
    /// </summary>
    public const string TenantIdQueryParam = "tenantId";

    /// <summary>
    /// The public schema name (shared across tenants).
    /// </summary>
    public const string PublicSchema = "public";
}
