using System.ServiceModel.Syndication;
using System.Xml;
using Microsoft.AspNetCore.Mvc;

namespace LucidRAG.Controllers.Api;

[ApiController]
[Route("api/[controller]")]
public class ArticlesController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArticlesController> _logger;

    // Cache the articles for 1 hour
    private static List<ArticleDto>? _cachedArticles;
    private static DateTime _cacheExpiry = DateTime.MinValue;

    public ArticlesController(IHttpClientFactory httpClientFactory, ILogger<ArticlesController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets RAG-related articles from the mostlylucid blog
    /// </summary>
    [HttpGet("rag")]
    public async Task<IActionResult> GetRagArticles()
    {
        // Return cached articles if still valid
        if (_cachedArticles != null && DateTime.UtcNow < _cacheExpiry)
        {
            return Ok(_cachedArticles);
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            // Fetch the RSS feed for the RAG category
            var rssUrl = "https://www.mostlylucid.net/rss/category/RAG";
            using var response = await client.GetStreamAsync(rssUrl);
            using var reader = XmlReader.Create(response);
            var feed = SyndicationFeed.Load(reader);

            var articles = feed.Items
                .Take(20)
                .Select(item => new ArticleDto
                {
                    Title = item.Title?.Text ?? "Untitled",
                    Link = item.Links.FirstOrDefault()?.Uri?.ToString() ?? "",
                    Date = item.PublishDate.ToString("MMM yyyy")
                })
                .ToList();

            // Cache for 1 hour
            _cachedArticles = articles;
            _cacheExpiry = DateTime.UtcNow.AddHours(1);

            return Ok(articles);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch RSS feed from mostlylucid");

            // Return fallback articles
            return Ok(new List<ArticleDto>
            {
                new() { Title = "Building an Agentic RAG Pipeline", Link = "https://www.mostlylucid.net/blog/agentic-rag-pipeline", Date = "2024" },
                new() { Title = "GraphRAG Entity Extraction", Link = "https://www.mostlylucid.net/blog/graphrag-entity-extraction", Date = "2024" },
                new() { Title = "Multi-Document RAG with ONNX", Link = "https://www.mostlylucid.net/blog/multi-document-rag", Date = "2024" }
            });
        }
    }

    public class ArticleDto
    {
        public string Title { get; set; } = "";
        public string Link { get; set; } = "";
        public string Date { get; set; } = "";
    }
}
