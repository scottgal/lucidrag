using Microsoft.AspNetCore.Mvc;
using LucidRAG.Filters;
using LucidRAG.Models;
using LucidRAG.Services;

namespace LucidRAG.Controllers.Api;

[ApiController]
[Route("api/crawl")]
[DemoModeWriteBlock(Operation = "Web crawling")]
public class CrawlController(
    IWebCrawlerService crawlerService,
    ILogger<CrawlController> logger) : ControllerBase
{
    /// <summary>
    /// Start a new web crawl job
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> StartCrawl([FromBody] CrawlRequest request, CancellationToken ct = default)
    {
        if (request.SeedUrls.Length == 0)
        {
            return BadRequest(new { error = "At least one seed URL is required" });
        }

        // Validate URLs
        foreach (var url in request.SeedUrls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                return BadRequest(new { error = $"Invalid URL: {url}" });
            }
        }

        try
        {
            var crawlId = await crawlerService.StartCrawlAsync(request, ct);

            return Ok(new CrawlStartResponse(crawlId, "Crawl started successfully"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start crawl");
            return StatusCode(500, new { error = "Failed to start crawl" });
        }
    }

    /// <summary>
    /// Get crawl job status
    /// </summary>
    [HttpGet("{id:guid}")]
    public IActionResult GetCrawlJob(Guid id)
    {
        var job = crawlerService.GetCrawlJob(id);
        if (job is null)
        {
            return NotFound(new { error = "Crawl job not found" });
        }

        return Ok(new
        {
            id = job.Id,
            seedUrls = job.SeedUrls,
            contentSelector = job.ContentSelector,
            maxPages = job.MaxPages,
            maxDepth = job.MaxDepth,
            collectionId = job.CollectionId,
            status = job.Status.ToString().ToLowerInvariant(),
            pagesDiscovered = job.PagesDiscovered,
            pagesProcessed = job.PagesProcessed,
            pagesFailed = job.PagesFailed,
            currentUrl = job.CurrentUrl,
            errorMessage = job.ErrorMessage,
            startedAt = job.StartedAt,
            completedAt = job.CompletedAt
        });
    }

    /// <summary>
    /// Stream crawl progress via SSE
    /// </summary>
    [HttpGet("{id:guid}/status")]
    public async Task StreamProgress(Guid id, CancellationToken ct = default)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        await foreach (var progress in crawlerService.StreamProgressAsync(id, ct))
        {
            var data = System.Text.Json.JsonSerializer.Serialize(new
            {
                crawlId = progress.CrawlId,
                pagesDiscovered = progress.PagesDiscovered,
                pagesProcessed = progress.PagesProcessed,
                pagesFailed = progress.PagesFailed,
                currentUrl = progress.CurrentUrl,
                status = progress.Status.ToString().ToLowerInvariant(),
                errorMessage = progress.ErrorMessage
            });

            await Response.WriteAsync($"data: {data}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }

    /// <summary>
    /// List all crawl jobs
    /// </summary>
    [HttpGet]
    public IActionResult ListCrawlJobs()
    {
        var jobs = crawlerService.GetCrawlJobs();

        return Ok(new
        {
            crawls = jobs.Select(job => new
            {
                id = job.Id,
                seedUrls = job.SeedUrls,
                status = job.Status.ToString().ToLowerInvariant(),
                pagesProcessed = job.PagesProcessed,
                pagesFailed = job.PagesFailed,
                startedAt = job.StartedAt,
                completedAt = job.CompletedAt
            })
        });
    }
}
