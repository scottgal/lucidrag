using Microsoft.AspNetCore.Identity;

namespace LucidRAG.Identity;

/// <summary>
/// Application user with custom properties.
/// </summary>
public class ApplicationUser : IdentityUser
{
    /// <summary>
    /// Display name for the user.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Profile avatar URL.
    /// </summary>
    public string? AvatarUrl { get; set; }

    /// <summary>
    /// Associated tenant ID for multi-tenant access.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// When the user was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Last login timestamp.
    /// </summary>
    public DateTimeOffset? LastLoginAt { get; set; }
}
