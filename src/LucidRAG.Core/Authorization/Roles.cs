namespace LucidRAG.Authorization;

/// <summary>
/// Role constants for the application.
/// </summary>
public static class Roles
{
    /// <summary>
    /// System administrator with full access to all tenants and system configuration.
    /// </summary>
    public const string SystemAdmin = "SystemAdmin";

    /// <summary>
    /// Tenant administrator with full access to their tenant's collections and users.
    /// </summary>
    public const string TenantAdmin = "TenantAdmin";

    /// <summary>
    /// Regular authenticated user with read access to tenant documents and chat.
    /// </summary>
    public const string User = "User";

    /// <summary>
    /// All admin roles.
    /// </summary>
    public const string AllAdmins = $"{SystemAdmin},{TenantAdmin}";

    /// <summary>
    /// All authenticated roles.
    /// </summary>
    public const string AllAuthenticated = $"{SystemAdmin},{TenantAdmin},{User}";
}

/// <summary>
/// Policy names for authorization.
/// </summary>
public static class Policies
{
    /// <summary>
    /// Allows read-only access to public tenant info (document counts, chat).
    /// Available to anonymous users.
    /// </summary>
    public const string PublicRead = "PublicRead";

    /// <summary>
    /// Allows full read access to tenant documents and content.
    /// Requires authentication.
    /// </summary>
    public const string TenantRead = "TenantRead";

    /// <summary>
    /// Allows write access to tenant documents (upload, delete).
    /// Requires User role or higher.
    /// </summary>
    public const string TenantWrite = "TenantWrite";

    /// <summary>
    /// Allows tenant administration (manage collections, invite users).
    /// Requires TenantAdmin role.
    /// </summary>
    public const string TenantManage = "TenantManage";

    /// <summary>
    /// Allows system administration (manage tenants, all users).
    /// Requires SystemAdmin role.
    /// </summary>
    public const string SystemManage = "SystemManage";
}

/// <summary>
/// Claims used for authorization decisions.
/// </summary>
public static class ClaimTypes
{
    /// <summary>
    /// The tenant ID the user belongs to.
    /// </summary>
    public const string TenantId = "tenant_id";

    /// <summary>
    /// The user's display name.
    /// </summary>
    public const string DisplayName = "display_name";
}
