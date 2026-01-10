using LucidRAG.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LucidRAG.Services;

namespace LucidRAG.Controllers.UI;

/// <summary>
/// Admin home controller - requires authentication.
/// Provides full document management, upload, and chat functionality.
/// </summary>
[Route("admin")]
[Authorize(Roles = Roles.AllAuthenticated)]
public class HomeController(
    IDocumentProcessingService documentService) : Controller
{
    [HttpGet]
    [HttpGet("~/home")]  // Also accessible at /home for backwards compat
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var documents = await documentService.GetDocumentsAsync(ct: ct);

        ViewBag.Documents = documents;
        ViewBag.TotalSegments = documents.Sum(d => d.SegmentCount);

        return View();
    }

    [HttpGet("documents")]
    public async Task<IActionResult> DocumentList(CancellationToken ct = default)
    {
        var documents = await documentService.GetDocumentsAsync(ct: ct);
        return PartialView("_DocumentList", documents);
    }

    [HttpGet("documents/{id:guid}/status-badge")]
    public async Task<IActionResult> DocumentStatusBadge(Guid id, CancellationToken ct = default)
    {
        var doc = await documentService.GetDocumentAsync(id, ct);
        if (doc is null) return NotFound();

        return PartialView("_DocumentStatusBadge", doc);
    }
}
