using Microsoft.Extensions.Options;

namespace LucidRAG.Multitenancy;

/// <summary>
/// Middleware that resolves the tenant for each request and sets the tenant context.
/// </summary>
public class TenantMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantMiddleware> _logger;
    private readonly MultitenancyOptions _options;

    public TenantMiddleware(
        RequestDelegate next,
        ILogger<TenantMiddleware> logger,
        IOptions<MultitenancyOptions> options)
    {
        _next = next;
        _logger = logger;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context, ITenantResolver resolver, ITenantAccessor accessor)
    {
        // Skip tenant resolution for certain paths
        if (ShouldSkipTenantResolution(context.Request.Path))
        {
            await _next(context);
            return;
        }

        // Resolve tenant
        var tenantContext = await resolver.ResolveAsync(context, context.RequestAborted);

        if (tenantContext == null && !_options.AllowDefaultTenant)
        {
            _logger.LogWarning("Tenant resolution failed for host: {Host}", context.Request.Host);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Tenant not found",
                message = "Could not determine tenant from request. Please use a valid tenant subdomain or provide the X-Tenant-Id header."
            });
            return;
        }

        // Set tenant context
        accessor.Current = tenantContext;

        // Add tenant info to response headers for debugging
        if (tenantContext != null)
        {
            context.Response.Headers["X-Tenant-Id"] = tenantContext.TenantId;
        }

        // Store in HttpContext.Items for access by other components
        context.Items["TenantContext"] = tenantContext;

        _logger.LogDebug("Request for tenant: {TenantId}", tenantContext?.TenantId ?? "none");

        await _next(context);
    }

    /// <summary>
    /// Paths that should skip tenant resolution.
    /// </summary>
    private static bool ShouldSkipTenantResolution(PathString path)
    {
        var skipPaths = new[]
        {
            "/healthz",
            "/health",
            "/openapi",
            "/scalar",
            "/.well-known",
            "/favicon.ico"
        };

        foreach (var skipPath in skipPaths)
        {
            if (path.StartsWithSegments(skipPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Extension methods for tenant middleware.
/// </summary>
public static class TenantMiddlewareExtensions
{
    /// <summary>
    /// Add multi-tenancy middleware to the pipeline.
    /// </summary>
    public static IApplicationBuilder UseMultitenancy(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices
            .GetRequiredService<IOptions<MultitenancyOptions>>()
            .Value;

        if (!options.Enabled)
        {
            return app;
        }

        return app.UseMiddleware<TenantMiddleware>();
    }
}
