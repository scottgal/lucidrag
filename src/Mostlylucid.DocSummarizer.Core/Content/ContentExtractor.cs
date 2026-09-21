using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using SmartReader;

namespace Mostlylucid.DocSummarizer.Content;

/// <summary>
///     Robust content extraction using SmartReader (Mozilla Readability port).
///     Extracts main article content, removing boilerplate (ads, nav, sidebars).
/// </summary>
public partial class ContentExtractor
{
    private readonly HttpClient _httpClient;

    public ContentExtractor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    ///     Extract main content from a URL using SmartReader.
    ///     Returns enriched content with author, date, featured image, etc.
    /// </summary>
    public async Task<ExtractedContent?> ExtractAsync(string url, CancellationToken ct = default)
    {
        try
        {
            // Fetch the HTML
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 MostlyLucid-DoomSummarizer/1.0");
            request.Headers.Add("Accept", "text/html,application/xhtml+xml");

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var html = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(html)) return null;

            // Use SmartReader for intelligent extraction
            var reader = new Reader(url, html);
            var article = await reader.GetArticleAsync();

            if (article == null || !article.IsReadable)
                // Fallback to basic extraction
                return FallbackExtract(html, url);

            var extracted = new ExtractedContent
            {
                Title = article.Title ?? "",
                Content = article.TextContent ?? "",
                HtmlContent = article.Content ?? "",
                Author = article.Author,
                PublishedDate = article.PublicationDate,
                Language = article.Language,
                FeaturedImage = article.FeaturedImage,
                SiteName = article.SiteName,
                ReadTimeMinutes = (int)Math.Ceiling(article.TimeToRead.TotalMinutes),
                Excerpt = article.Excerpt,
                IsReadable = true
            };

            // Convert HTML to structured Markdown (preserves headings, lists, tables, code blocks)
            EnrichWithMarkdown(extracted);

            return extracted;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Extract content from HTML string directly.
    /// </summary>
    public ExtractedContent? ExtractFromHtml(string html, string baseUrl)
    {
        try
        {
            var reader = new Reader(baseUrl, html);
            var article = reader.GetArticle();

            if (article == null || !article.IsReadable) return FallbackExtract(html, baseUrl);

            var extracted = new ExtractedContent
            {
                Title = article.Title ?? "",
                Content = article.TextContent ?? "",
                HtmlContent = article.Content ?? "",
                Author = article.Author,
                PublishedDate = article.PublicationDate,
                Language = article.Language,
                FeaturedImage = article.FeaturedImage,
                SiteName = article.SiteName,
                ReadTimeMinutes = (int)Math.Ceiling(article.TimeToRead.TotalMinutes),
                Excerpt = article.Excerpt,
                IsReadable = true
            };

            EnrichWithMarkdown(extracted);
            return extracted;
        }
        catch
        {
            return FallbackExtract(html, baseUrl);
        }
    }

    /// <summary>
    ///     Convert HTML content to Markdown and analyze structure.
    ///     Stores structured Markdown that preserves headings, lists, tables, code blocks.
    /// </summary>
    private static void EnrichWithMarkdown(ExtractedContent content)
    {
        if (string.IsNullOrEmpty(content.HtmlContent)) return;

        try
        {
            var (markdown, structure) = MarkdownContentAnalyzer.ExtractStructured(content.HtmlContent);
            if (!string.IsNullOrWhiteSpace(markdown))
            {
                content.MarkdownContent = markdown;
                content.Structure = structure;

                // If the Markdown version has more structure than plain text,
                // use it as the primary content (preserves headings, lists, etc.)
                if (structure.QualityScore > 0.2 && markdown.Length > content.Content.Length * 0.5)
                    content.Content = markdown;
            }
        }
        catch
        {
            // Markdown enrichment is non-fatal
        }
    }

    /// <summary>
    ///     Basic fallback extraction when SmartReader fails.
    ///     Uses heuristics to find main content.
    /// </summary>
    private static ExtractedContent? FallbackExtract(string html, string url)
    {
        try
        {
            // Use AngleSharp for basic extraction
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var document = context.OpenAsync(req => req.Content(html)).Result;

            // Try common content selectors in priority order
            var contentSelectors = new[]
            {
                "article",
                "main",
                "[role='main']",
                ".post-content",
                ".article-content",
                ".entry-content",
                ".content",
                "#content",
                ".post",
                ".article"
            };

            IElement? contentElement = null;
            foreach (var selector in contentSelectors)
            {
                contentElement = document.QuerySelector(selector);
                if (contentElement != null && contentElement.TextContent.Length > 200)
                    break;
            }

            // Fallback to body
            contentElement ??= document.Body;
            if (contentElement == null) return null;

            // Remove boilerplate elements
            var boilerplateSelectors = new[]
            {
                "script", "style", "nav", "header", "footer", "aside",
                ".sidebar", ".nav", ".menu", ".advertisement", ".ad",
                ".social", ".share", ".comments", ".related"
            };

            foreach (var selector in boilerplateSelectors)
                foreach (var el in contentElement.QuerySelectorAll(selector).ToList())
                    el.Remove();

            var title = document.QuerySelector("title")?.TextContent ??
                        document.QuerySelector("h1")?.TextContent ?? "";

            var extractedContent = contentElement.TextContent;
            extractedContent = WhitespaceRegex().Replace(extractedContent, " ").Trim();

            // Extract featured image
            var ogImage = document.QuerySelector("meta[property='og:image']")?.GetAttribute("content");
            var firstImage = contentElement.QuerySelector("img")?.GetAttribute("src");

            var extracted = new ExtractedContent
            {
                Title = title.Trim(),
                Content = extractedContent,
                HtmlContent = contentElement.InnerHtml,
                FeaturedImage = ogImage ?? firstImage,
                IsReadable = extractedContent.Length > 100
            };

            EnrichWithMarkdown(extracted);
            return extracted;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

/// <summary>
///     Extracted content from a web page.
/// </summary>
public class ExtractedContent
{
    public string Title { get; init; } = "";
    public string Content { get; set; } = "";
    public string? HtmlContent { get; init; }
    public string? MarkdownContent { get; set; }
    public ContentStructure? Structure { get; set; }
    public string? Author { get; init; }
    public DateTime? PublishedDate { get; init; }
    public string? Language { get; init; }
    public string? FeaturedImage { get; init; }
    public string? SiteName { get; init; }
    public int ReadTimeMinutes { get; init; }
    public string? Excerpt { get; init; }
    public bool IsReadable { get; init; }

    /// <summary>
    ///     Best available content: structured Markdown if available, otherwise plain text.
    /// </summary>
    public string BestContent => MarkdownContent ?? Content;
}