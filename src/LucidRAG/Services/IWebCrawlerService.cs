using LucidRAG.Models;

namespace LucidRAG.Services;

public interface IWebCrawlerService
{
    /// <summary>
    /// Start a web crawl job
    /// </summary>
    Task<Guid> StartCrawlAsync(CrawlRequest request, CancellationToken ct = default);

    /// <summary>
    /// Get status of a crawl job
    /// </summary>
    CrawlJob? GetCrawlJob(Guid crawlId);

    /// <summary>
    /// Stream progress updates for a crawl job
    /// </summary>
    IAsyncEnumerable<CrawlProgress> StreamProgressAsync(Guid crawlId, CancellationToken ct = default);

    /// <summary>
    /// Get all active and recent crawl jobs
    /// </summary>
    IEnumerable<CrawlJob> GetCrawlJobs();
}
