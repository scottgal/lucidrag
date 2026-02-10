using LucidRAG.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LucidRAG.Controllers.UI;

/// <summary>
///     Marketing landing page controller.
///     Serves the public-facing marketing site at the root URL.
/// </summary>
[AllowAnonymous]
public class MarketingController : Controller
{
    /// <summary>
    ///     Marketing landing page - the root of the site.
    /// </summary>
    [HttpGet("/")]
    public IActionResult Index()
    {
        // Authenticated users get the dashboard
        if (User.Identity?.IsAuthenticated == true &&
            (User.IsInRole(Roles.SystemAdmin) || User.IsInRole(Roles.TenantAdmin) || User.IsInRole(Roles.User)))
            return Redirect("/admin");

        return View();
    }
}
