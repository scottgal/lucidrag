using Microsoft.AspNetCore.Authorization;

namespace LucidRAG.Authorization;

/// <summary>
/// Extension methods for setting up authorization policies.
/// </summary>
public static class AuthorizationSetup
{
    /// <summary>
    /// Adds the application's authorization policies.
    /// </summary>
    public static IServiceCollection AddLucidRagAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            // Public read - allows anonymous access to limited tenant info
            .AddPolicy(Policies.PublicRead, policy =>
            {
                // No requirements - allows anonymous
                policy.RequireAssertion(_ => true);
            })
            // Tenant read - requires authenticated user
            .AddPolicy(Policies.TenantRead, policy =>
            {
                policy.RequireAuthenticatedUser();
            })
            // Tenant write - requires User role or higher
            .AddPolicy(Policies.TenantWrite, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireRole(Roles.User, Roles.TenantAdmin, Roles.SystemAdmin);
            })
            // Tenant manage - requires TenantAdmin or SystemAdmin
            .AddPolicy(Policies.TenantManage, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireRole(Roles.TenantAdmin, Roles.SystemAdmin);
            })
            // System manage - requires SystemAdmin only
            .AddPolicy(Policies.SystemManage, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireRole(Roles.SystemAdmin);
            });

        return services;
    }
}
