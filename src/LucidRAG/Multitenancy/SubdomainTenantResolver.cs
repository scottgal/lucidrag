using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using LucidRAG.Data;

namespace LucidRAG.Multitenancy;

/// <summary>
/// Resolves the current tenant from the HTTP request.
/// This interface is ASP.NET-specific (uses HttpContext).
/// </summary>
public interface ITenantResolver
{
    /// <summary>
    /// Resolve the tenant context from the current HTTP request.
    /// Returns null if no tenant can be resolved.
    /// </summary>
    Task<TenantContext?> ResolveAsync(HttpContext context, CancellationToken ct = default);
}

/// <summary>
/// Resolves tenant from subdomain, header, or query parameter.
/// Priority: Header > Query > Subdomain > Default
/// </summary>
public class SubdomainTenantResolver : ITenantResolver
{
    private readonly IServiceProvider _services;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SubdomainTenantResolver> _logger;
    private readonly MultitenancyOptions _options;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public SubdomainTenantResolver(
        IServiceProvider services,
        IMemoryCache cache,
        ILogger<SubdomainTenantResolver> logger,
        Microsoft.Extensions.Options.IOptions<MultitenancyOptions> options)
    {
        _services = services;
        _cache = cache;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<TenantContext?> ResolveAsync(HttpContext context, CancellationToken ct = default)
    {
        // 1. Check header override (for API clients)
        if (context.Request.Headers.TryGetValue(TenantConstants.TenantIdHeader, out var headerValue)
            && !string.IsNullOrWhiteSpace(headerValue))
        {
            var tenantId = headerValue.ToString();
            _logger.LogDebug("Tenant resolved from header: {TenantId}", tenantId);
            return await GetOrCreateTenantContextAsync(tenantId, ct);
        }

        // 2. Check query parameter (for testing/debugging)
        if (context.Request.Query.TryGetValue(TenantConstants.TenantIdQueryParam, out var queryValue)
            && !string.IsNullOrWhiteSpace(queryValue))
        {
            var tenantId = queryValue.ToString();
            _logger.LogDebug("Tenant resolved from query: {TenantId}", tenantId);
            return await GetOrCreateTenantContextAsync(tenantId, ct);
        }

        // 3. Check path-based tenant: /t/{tenantId}
        var tenantFromPath = ExtractTenantFromPath(context.Request.Path);
        if (!string.IsNullOrEmpty(tenantFromPath))
        {
            _logger.LogDebug("Tenant resolved from path: {TenantId}", tenantFromPath);
            return await GetOrCreateTenantContextAsync(tenantFromPath, ct);
        }

        // 4. Extract from subdomain
        var host = context.Request.Host.Host;
        var tenantFromSubdomain = ExtractTenantFromHost(host);

        if (!string.IsNullOrEmpty(tenantFromSubdomain))
        {
            _logger.LogDebug("Tenant resolved from subdomain: {TenantId} (host: {Host})",
                tenantFromSubdomain, host);
            return await GetOrCreateTenantContextAsync(tenantFromSubdomain, ct);
        }

        // 5. Fall back to default tenant if enabled
        if (_options.AllowDefaultTenant)
        {
            _logger.LogDebug("Using default tenant");
            return await GetOrCreateTenantContextAsync(TenantConstants.DefaultTenantId, ct);
        }

        _logger.LogWarning("Could not resolve tenant from host: {Host}", host);
        return null;
    }

    /// <summary>
    /// Extract tenant ID from path: /t/{tenantId}/...
    /// Examples:
    ///   /t/mostlylucid -> mostlylucid
    ///   /t/acme/api/docs -> acme
    ///   /api/docs -> null
    /// </summary>
    private static string? ExtractTenantFromPath(PathString path)
    {
        var pathValue = path.Value;
        if (string.IsNullOrEmpty(pathValue))
            return null;

        // Check for /t/{tenantId} pattern
        if (pathValue.StartsWith("/t/", StringComparison.OrdinalIgnoreCase))
        {
            var remaining = pathValue[3..]; // Skip "/t/"
            var nextSlash = remaining.IndexOf('/');
            var tenantId = nextSlash >= 0 ? remaining[..nextSlash] : remaining;

            if (!string.IsNullOrWhiteSpace(tenantId) && tenantId.Length >= 2)
            {
                return tenantId.ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>
    /// Extract tenant ID from hostname.
    /// Examples:
    ///   acme.lucidrag.com -> acme
    ///   contoso.api.lucidrag.com -> contoso
    ///   localhost -> null (use default)
    ///   lucidrag.com -> null (use default)
    /// </summary>
    private string? ExtractTenantFromHost(string host)
    {
        // Remove port if present
        var hostWithoutPort = host.Split(':')[0];

        // Check if it's localhost or IP
        if (hostWithoutPort == "localhost" ||
            hostWithoutPort.StartsWith("127.") ||
            hostWithoutPort.StartsWith("192.168.") ||
            hostWithoutPort.StartsWith("10."))
        {
            return null;
        }

        // Split by dots
        var parts = hostWithoutPort.Split('.');

        // Need at least 3 parts for subdomain (tenant.domain.tld)
        if (parts.Length < 3)
        {
            return null;
        }

        // First part is the tenant subdomain
        var subdomain = parts[0];

        // Ignore common non-tenant subdomains
        if (IsReservedSubdomain(subdomain))
        {
            return null;
        }

        return subdomain;
    }

    private static bool IsReservedSubdomain(string subdomain)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "www", "api", "app", "admin", "mail", "smtp", "ftp",
            "dev", "staging", "test", "demo", "docs", "help",
            "status", "cdn", "static", "assets", "media"
        };
        return reserved.Contains(subdomain);
    }

    private async Task<TenantContext?> GetOrCreateTenantContextAsync(string tenantId, CancellationToken ct)
    {
        var cacheKey = $"tenant:{tenantId}";

        if (_cache.TryGetValue(cacheKey, out TenantContext? cached))
        {
            return cached;
        }

        // Look up tenant in database
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        var tenant = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId, ct);

        if (tenant == null)
        {
            // For default tenant or if auto-provisioning is enabled
            if (tenantId == TenantConstants.DefaultTenantId || _options.AutoProvisionTenants)
            {
                var context = TenantContext.FromTenantId(tenantId);
                _cache.Set(cacheKey, context, CacheDuration);
                return context;
            }

            _logger.LogWarning("Tenant not found: {TenantId}", tenantId);
            return null;
        }

        if (!tenant.IsActive)
        {
            _logger.LogWarning("Tenant is inactive: {TenantId}", tenantId);
            return null;
        }

        var tenantContext = new TenantContext
        {
            TenantId = tenant.TenantId,
            SchemaName = tenant.SchemaName,
            QdrantCollection = tenant.QdrantCollection,
            DisplayName = tenant.DisplayName,
            IsActive = tenant.IsActive,
            Settings = tenant.Settings,
            CreatedAt = tenant.CreatedAt
        };

        _cache.Set(cacheKey, tenantContext, CacheDuration);
        return tenantContext;
    }
}

/// <summary>
/// Configuration options for multi-tenancy.
/// </summary>
public class MultitenancyOptions
{
    public const string SectionName = "Multitenancy";

    /// <summary>
    /// Whether to enable multi-tenancy.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Whether to allow the default tenant when no tenant is specified.
    /// </summary>
    public bool AllowDefaultTenant { get; set; } = true;

    /// <summary>
    /// Whether to auto-provision tenants that don't exist.
    /// </summary>
    public bool AutoProvisionTenants { get; set; } = false;

    /// <summary>
    /// Base domain for tenant subdomains (e.g., "lucidrag.com").
    /// </summary>
    public string? BaseDomain { get; set; }
}
