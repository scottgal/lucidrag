namespace LucidRAG.Models;

/// <summary>
/// Request to start a web crawl
/// </summary>
public record CrawlRequest(
    /// <summary>
    /// Seed URLs to start crawling from
    /// </summary>
    string[] SeedUrls,

    /// <summary>
    /// CSS selector for content extraction (e.g., "article", ".post-content", "#main")
    /// If null, defaults to: article, main, [role="main"], .content, #content, body
    /// </summary>
    string? ContentSelector = null,

    /// <summary>
    /// Maximum number of pages to crawl
    /// </summary>
    int MaxPages = 50,

    /// <summary>
    /// Maximum crawl depth from seed URLs
    /// </summary>
    int MaxDepth = 3,

    /// <summary>
    /// Collection to add crawled documents to
    /// </summary>
    Guid? CollectionId = null
);

/// <summary>
/// Active crawl job tracking
/// </summary>
public record CrawlJob(
    Guid Id,
    string[] SeedUrls,
    string? ContentSelector,
    int MaxPages,
    int MaxDepth,
    Guid? CollectionId,
    DateTimeOffset StartedAt
)
{
    public CrawlStatus Status { get; set; } = CrawlStatus.Queued;
    public int PagesDiscovered { get; set; }
    public int PagesProcessed { get; set; }
    public int PagesFailed { get; set; }
    public string? CurrentUrl { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public enum CrawlStatus
{
    Queued,
    Crawling,
    Processing,
    Completed,
    Failed
}

/// <summary>
/// Progress update for SSE streaming
/// </summary>
public record CrawlProgress(
    Guid CrawlId,
    int PagesDiscovered,
    int PagesProcessed,
    int PagesFailed,
    string? CurrentUrl,
    CrawlStatus Status,
    string? ErrorMessage = null
);

/// <summary>
/// Response after starting a crawl
/// </summary>
public record CrawlStartResponse(
    Guid CrawlId,
    string Message
);

/// <summary>
/// Crawled page result before processing
/// </summary>
public record CrawledPage(
    string Url,
    string Title,
    string Content,
    string[] Links
);
