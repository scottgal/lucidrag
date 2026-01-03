using Microsoft.AspNetCore.Mvc;
using LucidRAG.Services;

namespace LucidRAG.Controllers.UI;

[Route("")]
public class HomeController(
    IDocumentProcessingService documentService,
    IConversationService conversationService) : Controller
{
    [HttpGet]
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
