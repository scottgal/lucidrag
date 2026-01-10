using LucidRAG.Authorization;
using LucidRAG.Identity;
using LucidRAG.Multitenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LucidRAG.Controllers.UI;

/// <summary>
/// System administration controller for managing tenants, users, and system settings.
/// Requires SystemAdmin role.
/// </summary>
[Route("admin/system")]
[Authorize(Roles = Roles.SystemAdmin)]
public class SystemAdminController(
    ITenantProvisioningService tenantService,
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    ILogger<SystemAdminController> logger) : Controller
{
    /// <summary>
    /// System admin dashboard.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var tenants = await tenantService.ListTenantsAsync(null, ct);
        var users = await userManager.Users.ToListAsync(ct);

        var model = new SystemDashboardViewModel
        {
            TotalTenants = tenants.Count,
            ActiveTenants = tenants.Count(t => t.IsActive),
            ProvisionedTenants = tenants.Count(t => t.IsProvisioned),
            TotalUsers = users.Count,
            RecentTenants = tenants
                .OrderByDescending(t => t.CreatedAt)
                .Take(5)
                .Select(t => new TenantSummaryViewModel
                {
                    Id = t.Id,
                    TenantId = t.TenantId,
                    DisplayName = t.DisplayName,
                    Plan = t.Plan,
                    IsActive = t.IsActive,
                    IsProvisioned = t.IsProvisioned,
                    CreatedAt = t.CreatedAt
                })
                .ToList()
        };

        return View(model);
    }

    #region Tenant Management

    /// <summary>
    /// List all tenants.
    /// </summary>
    [HttpGet("tenants")]
    public async Task<IActionResult> Tenants(CancellationToken ct = default)
    {
        var tenants = await tenantService.ListTenantsAsync(null, ct);

        var model = tenants
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new TenantListItemViewModel
            {
                Id = t.Id,
                TenantId = t.TenantId,
                DisplayName = t.DisplayName,
                ContactEmail = t.ContactEmail,
                Plan = t.Plan,
                IsActive = t.IsActive,
                IsProvisioned = t.IsProvisioned,
                CreatedAt = t.CreatedAt,
                ProvisionedAt = t.ProvisionedAt
            })
            .ToList();

        return View(model);
    }

    /// <summary>
    /// Create tenant form.
    /// </summary>
    [HttpGet("tenants/create")]
    public IActionResult CreateTenant()
    {
        return View(new TenantFormViewModel());
    }

    /// <summary>
    /// Create tenant handler.
    /// </summary>
    [HttpPost("tenants/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTenant(TenantFormViewModel model, CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        // Validate tenant ID format
        if (!System.Text.RegularExpressions.Regex.IsMatch(model.TenantId!, @"^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$"))
        {
            ModelState.AddModelError(nameof(model.TenantId), "Tenant ID must be lowercase alphanumeric with optional hyphens (3-64 chars)");
            return View(model);
        }

        // Check if exists
        if (await tenantService.ExistsAsync(model.TenantId!, ct))
        {
            ModelState.AddModelError(nameof(model.TenantId), "A tenant with this ID already exists");
            return View(model);
        }

        try
        {
            await tenantService.ProvisionAsync(
                model.TenantId!,
                model.DisplayName,
                model.ContactEmail,
                model.Plan,
                ct);

            logger.LogInformation("Created tenant {TenantId}", model.TenantId);
            TempData["Success"] = $"Tenant '{model.DisplayName ?? model.TenantId}' created and provisioned successfully";
            return RedirectToAction(nameof(Tenants));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create tenant {TenantId}", model.TenantId);
            ModelState.AddModelError("", $"Failed to create tenant: {ex.Message}");
            return View(model);
        }
    }

    /// <summary>
    /// Tenant details.
    /// </summary>
    [HttpGet("tenants/{tenantId}")]
    public async Task<IActionResult> TenantDetails(string tenantId, CancellationToken ct = default)
    {
        var tenant = await tenantService.GetTenantAsync(tenantId, ct);
        if (tenant is null)
        {
            return NotFound();
        }

        var model = new TenantDetailsViewModel
        {
            Id = tenant.Id,
            TenantId = tenant.TenantId,
            DisplayName = tenant.DisplayName,
            ContactEmail = tenant.ContactEmail,
            SchemaName = tenant.SchemaName,
            QdrantCollection = tenant.QdrantCollection,
            Plan = tenant.Plan,
            IsActive = tenant.IsActive,
            IsProvisioned = tenant.IsProvisioned,
            CreatedAt = tenant.CreatedAt,
            ProvisionedAt = tenant.ProvisionedAt,
            LastAccessedAt = tenant.LastAccessedAt
        };

        return View(model);
    }

    /// <summary>
    /// Toggle tenant active status.
    /// </summary>
    [HttpPost("tenants/{tenantId}/toggle-status")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleTenantStatus(string tenantId, CancellationToken ct = default)
    {
        var tenant = await tenantService.GetTenantAsync(tenantId, ct);
        if (tenant is null)
        {
            return NotFound();
        }

        await tenantService.UpdateStatusAsync(tenantId, !tenant.IsActive, ct);

        TempData["Success"] = tenant.IsActive
            ? $"Tenant '{tenant.DisplayName ?? tenant.TenantId}' has been deactivated"
            : $"Tenant '{tenant.DisplayName ?? tenant.TenantId}' has been activated";

        return RedirectToAction(nameof(TenantDetails), new { tenantId });
    }

    /// <summary>
    /// Delete tenant confirmation.
    /// </summary>
    [HttpGet("tenants/{tenantId}/delete")]
    public async Task<IActionResult> DeleteTenant(string tenantId, CancellationToken ct = default)
    {
        var tenant = await tenantService.GetTenantAsync(tenantId, ct);
        if (tenant is null)
        {
            return NotFound();
        }

        var model = new TenantDeleteViewModel
        {
            TenantId = tenant.TenantId,
            DisplayName = tenant.DisplayName,
            SchemaName = tenant.SchemaName
        };

        return View(model);
    }

    /// <summary>
    /// Delete tenant handler.
    /// </summary>
    [HttpPost("tenants/{tenantId}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTenantConfirmed(string tenantId, CancellationToken ct = default)
    {
        var tenant = await tenantService.GetTenantAsync(tenantId, ct);
        if (tenant is null)
        {
            return NotFound();
        }

        try
        {
            await tenantService.DeprovisionAsync(tenantId, ct);
            logger.LogWarning("Deleted tenant {TenantId}", tenantId);
            TempData["Success"] = $"Tenant '{tenant.DisplayName ?? tenant.TenantId}' has been deleted";
            return RedirectToAction(nameof(Tenants));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete tenant {TenantId}", tenantId);
            TempData["Error"] = $"Failed to delete tenant: {ex.Message}";
            return RedirectToAction(nameof(TenantDetails), new { tenantId });
        }
    }

    #endregion

    #region User Management

    /// <summary>
    /// List all users.
    /// </summary>
    [HttpGet("users")]
    public async Task<IActionResult> Users(CancellationToken ct = default)
    {
        var users = await userManager.Users
            .OrderByDescending(u => u.CreatedAt)
            .ToListAsync(ct);

        var model = new List<UserListItemViewModel>();
        foreach (var user in users)
        {
            var roles = await userManager.GetRolesAsync(user);
            model.Add(new UserListItemViewModel
            {
                Id = user.Id,
                Email = user.Email ?? "",
                DisplayName = user.DisplayName,
                TenantId = user.TenantId,
                Roles = roles.ToList(),
                EmailConfirmed = user.EmailConfirmed,
                LockoutEnd = user.LockoutEnd,
                CreatedAt = user.CreatedAt,
                LastLoginAt = user.LastLoginAt
            });
        }

        return View(model);
    }

    /// <summary>
    /// User details.
    /// </summary>
    [HttpGet("users/{id}")]
    public async Task<IActionResult> UserDetails(string id, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        var roles = await userManager.GetRolesAsync(user);
        var allRoles = await roleManager.Roles.ToListAsync(ct);

        var model = new UserDetailsViewModel
        {
            Id = user.Id,
            Email = user.Email ?? "",
            DisplayName = user.DisplayName,
            TenantId = user.TenantId,
            Roles = roles.ToList(),
            AllRoles = allRoles.Select(r => r.Name!).ToList(),
            EmailConfirmed = user.EmailConfirmed,
            LockoutEnd = user.LockoutEnd,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt
        };

        return View(model);
    }

    /// <summary>
    /// Update user roles.
    /// </summary>
    [HttpPost("users/{id}/roles")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateUserRoles(string id, [FromForm] List<string> roles, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        var currentRoles = await userManager.GetRolesAsync(user);

        // Remove old roles
        var rolesToRemove = currentRoles.Except(roles).ToList();
        if (rolesToRemove.Any())
        {
            await userManager.RemoveFromRolesAsync(user, rolesToRemove);
        }

        // Add new roles
        var rolesToAdd = roles.Except(currentRoles).ToList();
        if (rolesToAdd.Any())
        {
            await userManager.AddToRolesAsync(user, rolesToAdd);
        }

        logger.LogInformation("Updated roles for user {UserId}: {Roles}", id, string.Join(", ", roles));
        TempData["Success"] = "User roles updated successfully";
        return RedirectToAction(nameof(UserDetails), new { id });
    }

    /// <summary>
    /// Toggle user lockout.
    /// </summary>
    [HttpPost("users/{id}/toggle-lockout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleUserLockout(string id)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        if (user.LockoutEnd.HasValue && user.LockoutEnd > DateTimeOffset.UtcNow)
        {
            // Unlock
            await userManager.SetLockoutEndDateAsync(user, null);
            TempData["Success"] = $"User '{user.DisplayName ?? user.Email}' has been unlocked";
        }
        else
        {
            // Lock for 100 years (effectively permanent)
            await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddYears(100));
            TempData["Success"] = $"User '{user.DisplayName ?? user.Email}' has been locked";
        }

        return RedirectToAction(nameof(UserDetails), new { id });
    }

    #endregion
}

// View Models
public class SystemDashboardViewModel
{
    public int TotalTenants { get; set; }
    public int ActiveTenants { get; set; }
    public int ProvisionedTenants { get; set; }
    public int TotalUsers { get; set; }
    public List<TenantSummaryViewModel> RecentTenants { get; set; } = [];
}

public class TenantSummaryViewModel
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Plan { get; set; }
    public bool IsActive { get; set; }
    public bool IsProvisioned { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class TenantListItemViewModel
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? ContactEmail { get; set; }
    public string? Plan { get; set; }
    public bool IsActive { get; set; }
    public bool IsProvisioned { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProvisionedAt { get; set; }
}

public class TenantFormViewModel
{
    public string? TenantId { get; set; }
    public string? DisplayName { get; set; }
    public string? ContactEmail { get; set; }
    public string? Plan { get; set; } = TenantPlans.Free;
}

public class TenantDetailsViewModel
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? ContactEmail { get; set; }
    public string SchemaName { get; set; } = "";
    public string QdrantCollection { get; set; } = "";
    public string? Plan { get; set; }
    public bool IsActive { get; set; }
    public bool IsProvisioned { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProvisionedAt { get; set; }
    public DateTimeOffset? LastAccessedAt { get; set; }
}

public class TenantDeleteViewModel
{
    public string TenantId { get; set; } = "";
    public string? DisplayName { get; set; }
    public string SchemaName { get; set; } = "";
}

public class UserListItemViewModel
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? TenantId { get; set; }
    public List<string> Roles { get; set; } = [];
    public bool EmailConfirmed { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}

public class UserDetailsViewModel
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? TenantId { get; set; }
    public List<string> Roles { get; set; } = [];
    public List<string> AllRoles { get; set; } = [];
    public bool EmailConfirmed { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}
