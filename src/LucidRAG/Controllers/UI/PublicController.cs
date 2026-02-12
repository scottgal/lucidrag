using LucidRAG.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LucidRAG.Controllers.UI;

/// <summary>
///     Public-facing UI controller for unauthenticated access.
///     Provides read-only chat interface scoped to collections.
/// </summary>
[AllowAnonymous]
public class PublicController : Controller
{
    /// <summary>
    ///     Public chat/search page.
    /// </summary>
    [HttpGet("/public")]
    [HttpGet("/search")]
    public IActionResult Index()
    {
        return View();
    }

    /// <summary>
    ///     Collection-scoped chat page.
    /// </summary>
    [HttpGet("/collection/{slug}")]
    public IActionResult Collection(string slug)
    {
        ViewData["CollectionSlug"] = slug;
        return View("Index");
    }
}