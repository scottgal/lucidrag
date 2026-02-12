using LucidRAG.Entities;
using LucidRAG.Services;

namespace LucidRAG.Middleware;

public class ApiKeyAuthMiddleware(RequestDelegate next, ILogger<ApiKeyAuthMiddleware> logger)
{
    private const string ApiKeyHeaderName = "X-Api-Key";
    private const string ApiKeyQueryParam = "api_key";
    private const string SaasPrefix = "/api/saas/";
    private const string PublicPrefix = "/api/saas/public/";
    private const string AdminPrefix = "/api/saas/admin/";
    private const string WidgetPrefix = "/api/saas/widget/";

    public async Task InvokeAsync(HttpContext context, IApiKeyService apiKeyService)
    {
        var path = context.Request.Path.Value ?? "";

        // Only handle /api/saas/ routes
        if (!path.StartsWith(SaasPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        // Admin endpoints use ASP.NET Identity auth, not API keys
        if (path.StartsWith(AdminPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        // Public endpoints don't require API key
        if (path.StartsWith(PublicPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        // Extract API key from header or query param
        string? apiKey = context.Request.Headers[ApiKeyHeaderName].FirstOrDefault();
        if (string.IsNullOrEmpty(apiKey))
            apiKey = context.Request.Query[ApiKeyQueryParam].FirstOrDefault();

        // Widget config endpoint allows key via query param (GET from script tag)
        if (string.IsNullOrEmpty(apiKey) && path.StartsWith(WidgetPrefix, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "API key required" });
            return;
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "API key required. Pass via X-Api-Key header or api_key query parameter." });
            return;
        }

        // Validate key
        var keyEntity = await apiKeyService.ValidateKeyAsync(apiKey, context.RequestAborted);
        if (keyEntity is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or expired API key." });
            return;
        }

        // Validate read domain (Referer/Origin)
        var referer = context.Request.Headers.Referer.FirstOrDefault();
        var origin = context.Request.Headers.Origin.FirstOrDefault();
        if (!apiKeyService.ValidateReadDomain(keyEntity, referer, origin))
        {
            logger.LogWarning("API key {KeyPrefix} used from unauthorized domain. Referer: {Referer}, Origin: {Origin}",
                keyEntity.KeyPrefix, referer, origin);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "This API key is not authorized for this domain." });
            return;
        }

        // Check rate limit
        if (!apiKeyService.CheckRateLimit(keyEntity.Id, keyEntity.RateLimitPerMinute, keyEntity.RateLimitPerDay))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "60";
            await context.Response.WriteAsJsonAsync(new { error = "Rate limit exceeded. Please try again later." });
            return;
        }

        // Record request
        apiKeyService.RecordRequest(keyEntity.Id);

        // Store in HttpContext for downstream use
        context.Items["ApiKey"] = keyEntity;

        // Set CORS headers dynamically based on the key's read domains
        if (origin is not null)
        {
            context.Response.Headers.Append("Access-Control-Allow-Origin", origin);
            context.Response.Headers.Append("Access-Control-Allow-Headers", "Content-Type, X-Api-Key");
            context.Response.Headers.Append("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            context.Response.Headers.Append("Access-Control-Max-Age", "3600");
        }

        // Handle CORS preflight
        if (context.Request.Method == HttpMethods.Options)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        // Fire-and-forget request count increment
        _ = Task.Run(async () =>
        {
            try
            {
                await apiKeyService.IncrementRequestCountAsync(keyEntity.Id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to increment request count for key {KeyPrefix}", keyEntity.KeyPrefix);
            }
        });

        await next(context);
    }
}

public static class ApiKeyAuthMiddlewareExtensions
{
    public static IApplicationBuilder UseApiKeyAuth(this IApplicationBuilder app)
    {
        return app.UseMiddleware<ApiKeyAuthMiddleware>();
    }
}

/// <summary>
///     Extension method to extract the validated API key from HttpContext.
/// </summary>
public static class HttpContextApiKeyExtensions
{
    public static ApiKeyEntity? GetApiKey(this HttpContext context)
    {
        return context.Items.TryGetValue("ApiKey", out var key) ? key as ApiKeyEntity : null;
    }
}
