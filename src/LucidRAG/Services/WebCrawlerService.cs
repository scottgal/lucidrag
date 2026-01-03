using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using LucidRAG.Config;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Models;
using LucidRAG.Services.Background;

namespace LucidRAG.Services;

public partial class WebCrawlerService : IWebCrawlerService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DocumentProcessingQueue _queue;
    private readonly ILogger<WebCrawlerService> _logger;
    private readonly CrawlerConfig _crawlerConfig;
    private readonly string _uploadPath;

    private readonly ConcurrentDictionary<Guid, CrawlJob> _crawlJobs = new();
    private readonly ConcurrentDictionary<Guid, Channel<CrawlProgress>> _progressChannels = new();
    private readonly ConcurrentDictionary<string, RobotsTxt> _robotsCache = new();

    public WebCrawlerService(
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        DocumentProcessingQueue queue,
        IOptions<RagDocumentsConfig> config,
        ILogger<WebCrawlerService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        _queue = queue;
        _crawlerConfig = config.Value.Crawler;
        _uploadPath = config.Value.UploadPath;
        _logger = logger;
    }

    public async Task<Guid> StartCrawlAsync(CrawlRequest request, CancellationToken ct = default)
    {
        var crawlId = Guid.NewGuid();
        var job = new CrawlJob(
            crawlId,
            request.SeedUrls,
            request.ContentSelector,
            request.MaxPages,
            request.MaxDepth,
            request.CollectionId,
            DateTimeOffset.UtcNow);

        _crawlJobs[crawlId] = job;
        _progressChannels[crawlId] = Channel.CreateUnbounded<CrawlProgress>();

        // Start crawl in background - don't pass the HTTP request's cancellation token
        // as it will cancel when the request ends
        _ = Task.Run(() => ExecuteCrawlAsync(crawlId, request, CancellationToken.None));

        return crawlId;
    }

    public CrawlJob? GetCrawlJob(Guid crawlId) =>
        _crawlJobs.TryGetValue(crawlId, out var job) ? job : null;

    public IEnumerable<CrawlJob> GetCrawlJobs() => _crawlJobs.Values;

    public async IAsyncEnumerable<CrawlProgress> StreamProgressAsync(
        Guid crawlId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_progressChannels.TryGetValue(crawlId, out var channel))
        {
            yield break;
        }

        await foreach (var progress in channel.Reader.ReadAllAsync(ct))
        {
            yield return progress;
        }
    }

    private async Task ExecuteCrawlAsync(Guid crawlId, CrawlRequest request, CancellationToken ct)
    {
        var job = _crawlJobs[crawlId];
        var channel = _progressChannels[crawlId];

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(_crawlerConfig.UserAgent);
            client.Timeout = TimeSpan.FromSeconds(_crawlerConfig.TimeoutSeconds);

            // Parse seed URLs and determine base hosts
            var baseHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seedUris = new List<string>();
            foreach (var seedUrl in request.SeedUrls)
            {
                if (Uri.TryCreate(seedUrl, UriKind.Absolute, out var uri))
                {
                    baseHosts.Add(uri.Host);
                    seedUris.Add(uri.AbsoluteUri);
                }
            }

            // PHASE 1: Quick link discovery (no content extraction)
            job.Status = CrawlStatus.Crawling;
            await ReportProgressAsync(crawlId, job);

            var discoveredUrls = await DiscoverLinksAsync(client, seedUris, baseHosts, request.MaxPages, request.MaxDepth, job, crawlId, ct);

            _logger.LogInformation("Crawl {CrawlId}: Discovered {Count} URLs", crawlId, discoveredUrls.Count);

            // PHASE 2: Download and queue for processing
            job.Status = CrawlStatus.Processing;
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);

            foreach (var url in discoveredUrls)
            {
                if (ct.IsCancellationRequested) break;

                job.CurrentUrl = url;
                await ReportProgressAsync(crawlId, job);

                try
                {
                    // Fetch page
                    var response = await client.GetAsync(url, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogDebug("HTTP {StatusCode} for {Url}", response.StatusCode, url);
                        job.PagesFailed++;
                        continue;
                    }

                    var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                    if (!contentType.Contains("html"))
                    {
                        _logger.LogDebug("Non-HTML content type {Type} for {Url}", contentType, url);
                        continue;
                    }

                    var html = await response.Content.ReadAsStringAsync(ct);
                    var document = await context.OpenAsync(req => req.Content(html), ct);

                    // Extract title
                    var title = document.Title ?? url;

                    // Extract content using selector
                    var content = ExtractContent(document, request.ContentSelector);
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        _logger.LogDebug("No content extracted from {Url}", url);
                        job.PagesFailed++;
                        continue;
                    }

                    // Convert to Markdown and queue
                    var markdown = ConvertToMarkdown(content, title, url);
                    await QueueCrawledPageAsync(url, title, markdown, request.CollectionId, ct);
                    job.PagesProcessed++;

                    await ReportProgressAsync(crawlId, job);

                    // Rate limiting between downloads
                    await Task.Delay(_crawlerConfig.RequestDelayMs, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to download {Url}", url);
                    job.PagesFailed++;
                }
            }

            job.Status = CrawlStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.CurrentUrl = null;
            await ReportProgressAsync(crawlId, job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Crawl {CrawlId} failed", crawlId);
            job.Status = CrawlStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await ReportProgressAsync(crawlId, job);
        }
        finally
        {
            channel.Writer.Complete();
        }
    }

    /// <summary>
    /// Phase 1: Quick BFS link discovery without content extraction
    /// </summary>
    private async Task<List<string>> DiscoverLinksAsync(
        HttpClient client,
        List<string> seedUrls,
        HashSet<string> baseHosts,
        int maxPages,
        int maxDepth,
        CrawlJob job,
        Guid crawlId,
        CancellationToken ct)
    {
        var discovered = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Url, int Depth)>();

        foreach (var seed in seedUrls)
        {
            queue.Enqueue((seed, 0));
        }

        var config = Configuration.Default;
        var context = BrowsingContext.New(config);

        while (queue.Count > 0 && discovered.Count < maxPages && !ct.IsCancellationRequested)
        {
            var (url, depth) = queue.Dequeue();

            if (visited.Contains(url)) continue;
            visited.Add(url);

            // Check robots.txt
            if (_crawlerConfig.RespectRobotsTxt && !await IsAllowedByRobotsTxtAsync(client, url, ct))
            {
                _logger.LogDebug("Blocked by robots.txt: {Url}", url);
                continue;
            }

            job.CurrentUrl = url;
            job.PagesDiscovered = discovered.Count + queue.Count + 1;
            await ReportProgressAsync(crawlId, job);

            try
            {
                // Quick fetch just to find links
                var response = await client.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) continue;

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (!contentType.Contains("html")) continue;

                // Add to discovered list
                discovered.Add(url);

                // Extract links for further discovery
                if (depth < maxDepth && discovered.Count < maxPages)
                {
                    var html = await response.Content.ReadAsStringAsync(ct);
                    var document = await context.OpenAsync(req => req.Content(html), ct);

                    var links = ExtractLinks(document, url, baseHosts);
                    foreach (var link in links)
                    {
                        if (!visited.Contains(link) && !queue.Any(q => q.Url == link))
                        {
                            queue.Enqueue((link, depth + 1));
                        }
                    }
                }

                // Minimal delay for discovery (faster than full download)
                await Task.Delay(100, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to discover links from {Url}", url);
            }
        }

        return discovered;
    }

    private async Task ReportProgressAsync(Guid crawlId, CrawlJob job)
    {
        if (_progressChannels.TryGetValue(crawlId, out var channel))
        {
            await channel.Writer.WriteAsync(new CrawlProgress(
                crawlId,
                job.PagesDiscovered,
                job.PagesProcessed,
                job.PagesFailed,
                job.CurrentUrl,
                job.Status,
                job.ErrorMessage));
        }
    }

    private string ExtractContent(IDocument document, string? selector)
    {
        // Remove unwanted elements first
        foreach (var el in document.QuerySelectorAll("script, style, nav, header, footer, aside, .sidebar, .navigation, .menu, .ad, .advertisement"))
        {
            el.Remove();
        }

        IElement? contentElement = null;

        if (!string.IsNullOrEmpty(selector))
        {
            contentElement = document.QuerySelector(selector);
        }

        // Fallback selectors
        contentElement ??= document.QuerySelector("article");
        contentElement ??= document.QuerySelector("main");
        contentElement ??= document.QuerySelector("[role='main']");
        contentElement ??= document.QuerySelector(".content");
        contentElement ??= document.QuerySelector("#content");
        contentElement ??= document.QuerySelector(".post-content");
        contentElement ??= document.QuerySelector(".entry-content");
        contentElement ??= document.Body;

        return contentElement?.TextContent?.Trim() ?? "";
    }

    private static IEnumerable<string> ExtractLinks(IDocument document, string baseUrl, HashSet<string> allowedHosts)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            yield break;

        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrEmpty(href)) continue;

            // Skip fragment-only links
            if (href.StartsWith('#')) continue;

            // Resolve relative URLs
            if (Uri.TryCreate(baseUri, href, out var absoluteUri))
            {
                // Only same-site links
                if (!allowedHosts.Contains(absoluteUri.Host)) continue;

                // Only http/https
                if (absoluteUri.Scheme != "http" && absoluteUri.Scheme != "https") continue;

                // Remove fragment
                var cleanUrl = absoluteUri.GetLeftPart(UriPartial.Query);

                yield return cleanUrl;
            }
        }
    }

    private string ConvertToMarkdown(string textContent, string title, string url)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"> Source: {url}");
        sb.AppendLine();

        // Clean up whitespace
        var cleaned = WhitespaceRegex().Replace(textContent, "\n\n");
        cleaned = cleaned.Trim();

        sb.AppendLine(cleaned);

        return sb.ToString();
    }

    private async Task QueueCrawledPageAsync(string url, string title, string markdown, Guid? collectionId, CancellationToken ct)
    {
        // Compute content hash from URL (for deduplication)
        using var sha = SHA256.Create();
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
        var contentHash = Convert.ToHexString(hashBytes[..16]).ToLowerInvariant();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

        // Check for existing document with same URL
        var existing = await db.Documents
            .FirstOrDefaultAsync(d => d.SourceUrl == url && d.CollectionId == collectionId, ct);

        if (existing is not null)
        {
            _logger.LogDebug("Page already crawled: {Url}", url);
            return;
        }

        // Create document entity
        var documentId = Guid.NewGuid();
        var filename = SanitizeFilename(title) + ".md";

        // Save markdown to disk
        var uploadDir = Path.Combine(_uploadPath, documentId.ToString());
        Directory.CreateDirectory(uploadDir);
        var filePath = Path.Combine(uploadDir, filename);
        await File.WriteAllTextAsync(filePath, markdown, ct);

        var document = new DocumentEntity
        {
            Id = documentId,
            CollectionId = collectionId,
            Name = title,
            OriginalFilename = filename,
            ContentHash = contentHash,
            FilePath = filePath,
            FileSizeBytes = new FileInfo(filePath).Length,
            MimeType = "text/markdown",
            Status = DocumentStatus.Pending,
            SourceUrl = url
        };

        db.Documents.Add(document);
        await db.SaveChangesAsync(ct);

        // Queue for processing
        await _queue.EnqueueAsync(new DocumentProcessingJob(documentId, filePath, collectionId), ct);

        _logger.LogInformation("Queued crawled page: {Title} ({Url})", title, url);
    }

    private static string SanitizeFilename(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(title.Where(c => !invalid.Contains(c)).ToArray());
        if (sanitized.Length > 100) sanitized = sanitized[..100];
        return string.IsNullOrWhiteSpace(sanitized) ? "page" : sanitized;
    }

    private async Task<bool> IsAllowedByRobotsTxtAsync(HttpClient client, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var robotsUrl = $"{uri.Scheme}://{uri.Host}/robots.txt";

        if (!_robotsCache.TryGetValue(uri.Host, out var robotsTxt))
        {
            try
            {
                var response = await client.GetAsync(robotsUrl, ct);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(ct);
                    robotsTxt = RobotsTxt.Parse(content);
                }
                else
                {
                    robotsTxt = RobotsTxt.AllowAll;
                }
            }
            catch
            {
                robotsTxt = RobotsTxt.AllowAll;
            }

            _robotsCache[uri.Host] = robotsTxt;
        }

        return robotsTxt.IsAllowed(uri.AbsolutePath, "LucidRAG");
    }

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex WhitespaceRegex();
}

/// <summary>
/// Simple robots.txt parser
/// </summary>
public class RobotsTxt
{
    private readonly List<string> _disallowPatterns = [];
    private readonly List<string> _allowPatterns = [];

    public static RobotsTxt AllowAll { get; } = new();

    public static RobotsTxt Parse(string content)
    {
        var robots = new RobotsTxt();
        var currentUserAgent = "";
        var appliesToUs = false;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var colonIndex = trimmed.IndexOf(':');
            if (colonIndex < 0) continue;

            var directive = trimmed[..colonIndex].Trim().ToLowerInvariant();
            var value = trimmed[(colonIndex + 1)..].Trim();

            switch (directive)
            {
                case "user-agent":
                    currentUserAgent = value;
                    appliesToUs = value == "*" ||
                                  value.Contains("LucidRAG", StringComparison.OrdinalIgnoreCase);
                    break;
                case "disallow" when appliesToUs && !string.IsNullOrEmpty(value):
                    robots._disallowPatterns.Add(value);
                    break;
                case "allow" when appliesToUs && !string.IsNullOrEmpty(value):
                    robots._allowPatterns.Add(value);
                    break;
            }
        }

        return robots;
    }

    public bool IsAllowed(string path, string userAgent)
    {
        // Check allow patterns first (they take precedence)
        foreach (var pattern in _allowPatterns)
        {
            if (PathMatches(path, pattern))
                return true;
        }

        // Check disallow patterns
        foreach (var pattern in _disallowPatterns)
        {
            if (PathMatches(path, pattern))
                return false;
        }

        return true;
    }

    private static bool PathMatches(string path, string pattern)
    {
        if (pattern.EndsWith('*'))
        {
            return path.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        }

        return path.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
