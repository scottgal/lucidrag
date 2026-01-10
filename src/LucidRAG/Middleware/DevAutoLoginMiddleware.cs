using LucidRAG.Identity;
using LucidRAG.Services;
using Microsoft.AspNetCore.Identity;

namespace LucidRAG.Middleware;

/// <summary>
/// Middleware that automatically logs in as the demo admin user in development mode.
/// This makes it easier to test the application without manual login.
/// </summary>
public class DevAutoLoginMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DevAutoLoginMiddleware> _logger;
    private bool _hasLoggedAutoLogin;

    public DevAutoLoginMiddleware(
        RequestDelegate next,
        IWebHostEnvironment environment,
        ILogger<DevAutoLoginMiddleware> logger)
    {
        _next = next;
        _environment = environment;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager)
    {
        // Only auto-login in development mode
        if (!_environment.IsDevelopment())
        {
            await _next(context);
            return;
        }

        // Skip if user is already authenticated
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await _next(context);
            return;
        }

        // Skip API requests, static files, public routes, and auth endpoints
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
        if (path.StartsWith("/api/") ||
            path.StartsWith("/hubs/") ||
            path.StartsWith("/graphql") ||
            path.StartsWith("/healthz") ||
            path.StartsWith("/auth/") ||
            path.StartsWith("/public") ||
            path.StartsWith("/collection/") ||
            path.StartsWith("/css/") ||
            path.StartsWith("/js/") ||
            path.StartsWith("/lib/") ||
            path.StartsWith("/dist/") ||
            path.StartsWith("/_") ||
            path.EndsWith(".js") ||
            path.EndsWith(".css") ||
            path.EndsWith(".map") ||
            path.EndsWith(".ico") ||
            path == "/")  // Skip root - public page
        {
            await _next(context);
            return;
        }

        // Try to auto-login as demo admin
        var user = await userManager.FindByEmailAsync(DemoAdminSeeder.DemoAdminEmail);
        if (user != null)
        {
            await signInManager.SignInAsync(user, isPersistent: true);

            // Update last login time
            user.LastLoginAt = DateTimeOffset.UtcNow;
            await userManager.UpdateAsync(user);

            if (!_hasLoggedAutoLogin)
            {
                _logger.LogInformation(
                    "Auto-logged in as demo admin: {Email} (development mode)",
                    DemoAdminSeeder.DemoAdminEmail);
                _hasLoggedAutoLogin = true;
            }

            // Redirect to force the auth cookie to take effect
            context.Response.Redirect(context.Request.Path + context.Request.QueryString);
            return;
        }

        await _next(context);
    }
}

/// <summary>
/// Extension methods for DevAutoLoginMiddleware.
/// </summary>
public static class DevAutoLoginMiddlewareExtensions
{
    /// <summary>
    /// Adds the development auto-login middleware to the pipeline.
    /// Only activates in development mode.
    /// </summary>
    public static IApplicationBuilder UseDevAutoLogin(this IApplicationBuilder app)
    {
        return app.UseMiddleware<DevAutoLoginMiddleware>();
    }
}
