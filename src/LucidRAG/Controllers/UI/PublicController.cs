using LucidRAG.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LucidRAG.Controllers.UI;

/// <summary>
/// Public-facing UI controller for unauthenticated access.
/// Provides read-only chat interface scoped to collections.
/// </summary>
[AllowAnonymous]
public class PublicController : Controller
{
    /// <summary>
    /// Public chat home page.
    /// </summary>
    [HttpGet("/")]
    [HttpGet("/public")]
    public IActionResult Index()
    {
        // If user is authenticated with admin role, redirect to admin dashboard
        if (User.Identity?.IsAuthenticated == true &&
            (User.IsInRole(Roles.SystemAdmin) || User.IsInRole(Roles.TenantAdmin) || User.IsInRole(Roles.User)))
        {
            return Redirect("/admin");
        }

        return View();
    }

    /// <summary>
    /// Collection-scoped chat page.
    /// </summary>
    [HttpGet("/collection/{slug}")]
    public IActionResult Collection(string slug)
    {
        ViewData["CollectionSlug"] = slug;
        return View("Index");
    }
}
