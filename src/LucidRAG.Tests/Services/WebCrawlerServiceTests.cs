using AngleSharp;
using AngleSharp.Dom;
using FluentAssertions;

namespace LucidRAG.Tests.Services;

/// <summary>
/// Unit tests for WebCrawlerService helper methods.
/// Since the service has many private methods, we test the public behavior
/// through carefully crafted scenarios, and extract testable logic where possible.
/// </summary>
public class WebCrawlerServiceTests
{
    private readonly IBrowsingContext _browsingContext;

    public WebCrawlerServiceTests()
    {
        var config = Configuration.Default;
        _browsingContext = BrowsingContext.New(config);
    }

    #region Content Extraction Tests

    [Fact]
    public async Task ExtractContent_WithArticleElement_ExtractsArticleContent()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <nav>Navigation menu</nav>
                <article>
                    <h1>Article Title</h1>
                    <p>This is the article content that should be extracted.</p>
                </article>
                <footer>Footer content</footer>
            </body>
            </html>
            """;
        var document = await _browsingContext.OpenAsync(req => req.Content(html));

        // Act
        var content = ExtractContent(document, null);

        // Assert
        content.Should().Contain("Article Title");
        content.Should().Contain("article content that should be extracted");
        content.Should().NotContain("Navigation menu");
        content.Should().NotContain("Footer content");
    }

    [Fact]
    public async Task ExtractContent_WithCustomSelector_UsesSelector()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <div class="sidebar">Sidebar content</div>
                <div class="post-content">
                    <h2>Blog Post</h2>
                    <p>Main blog content here.</p>
                </div>
                <div class="comments">Comment section</div>
            </body>
            </html>
            """;
        var document = await _browsingContext.OpenAsync(req => req.Content(html));

        // Act
        var content = ExtractContent(document, ".post-content");

        // Assert
        content.Should().Contain("Blog Post");
        content.Should().Contain("Main blog content");
        content.Should().NotContain("Sidebar");
        content.Should().NotContain("Comment");
    }

    [Fact]
    public async Task ExtractContent_FallsBackToMain_WhenNoArticle()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <header>Header</header>
                <main>
                    <h1>Main Content</h1>
                    <p>This is in the main element.</p>
                </main>
                <aside>Sidebar</aside>
            </body>
            </html>
            """;
        var document = await _browsingContext.OpenAsync(req => req.Content(html));

        // Act
        var content = ExtractContent(document, null);

        // Assert
        content.Should().Contain("Main Content");
        content.Should().NotContain("Header");
        content.Should().NotContain("Sidebar");
    }

    [Fact]
    public async Task ExtractContent_RemovesScriptsAndStyles()
    {
        // Arrange
        var html = """
            <html>
            <head>
                <style>.hidden { display: none; }</style>
            </head>
            <body>
                <article>
                    <script>alert('XSS');</script>
                    <p>Visible content</p>
                    <style>.inline { color: red; }</style>
                </article>
            </body>
            </html>
            """;
        var document = await _browsingContext.OpenAsync(req => req.Content(html));

        // Act
        var content = ExtractContent(document, null);

        // Assert
        content.Should().Contain("Visible content");
        content.Should().NotContain("alert");
        content.Should().NotContain("XSS");
        content.Should().NotContain("display: none");
    }

    [Fact]
    public async Task ExtractContent_RemovesNavigation()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <nav class="navigation">
                    <a href="/">Home</a>
                    <a href="/about">About</a>
                </nav>
                <article>
                    <p>Article content</p>
                </article>
            </body>
            </html>
            """;
        var document = await _browsingContext.OpenAsync(req => req.Content(html));

        // Act
        var content = ExtractContent(document, null);

        // Assert
        content.Should().Contain("Article content");
        content.Should().NotContain("Home");
        content.Should().NotContain("About");
    }

    #endregion

    #region Link Extraction Tests

    [Fact]
    public async Task ExtractLinks_ExtractsSameSiteLinks()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <a href="/page1">Page 1</a>
                <a href="/page2">Page 2</a>
                <a href="https://example.com/page3">Page 3</a>
                <a href="https://external.com/page">External</a>
            </body>
            </html>
            """;
        var document = await _browsingContext.OpenAsync(req => req.Content(html));
        var baseUrl = "https://example.com/";
        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" };

        // Act
        var links = ExtractLinks(document, baseUrl, allowedHosts).ToList();

        // Assert
        links.Should().Contain("https://example.com/page1");
        links.Should().Contain("https://example.com/page2");
        links.Should().Contain("https://example.com/page3");
        links.Should().NotContain(l => l.Contains("external.com"));
    }

    [Fact]
    public async Task ExtractLinks_SkipsFragmentOnlyLinks()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <a href="#section1">Section 1</a>
                <a href="#section2">Section 2</a>
                <a href="/page#section">Page with anchor</a>
            </body>
            </html>
            """;
        var document = await _browsingContext.OpenAsync(req => req.Content(html));
        var baseUrl = "https://example.com/";
        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" };

        // Act
        var links = ExtractLinks(document, baseUrl, allowedHosts).ToList();

        // Assert
        links.Should().NotContain("#section1");
        links.Should().NotContain("#section2");
        // The /page#section should be included but fragment stripped
        links.Should().Contain("https://example.com/page");
    }

    [Fact]
    public async Task ExtractLinks_ResolvesRelativeUrls()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <a href="page1">Relative</a>
                <a href="./page2">Dot relative</a>
                <a href="../page3">Parent relative</a>
                <a href="/absolute">Absolute</a>
            </body>
            </html>
            """;
        var document = await _browsingContext.OpenAsync(req => req.Content(html));
        var baseUrl = "https://example.com/blog/post/";
        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" };

        // Act
        var links = ExtractLinks(document, baseUrl, allowedHosts).ToList();

        // Assert
        links.Should().Contain("https://example.com/blog/post/page1");
        links.Should().Contain("https://example.com/blog/post/page2");
        links.Should().Contain("https://example.com/blog/page3");
        links.Should().Contain("https://example.com/absolute");
    }

    [Fact]
    public async Task ExtractLinks_SkipsNonHttpSchemes()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <a href="mailto:test@example.com">Email</a>
                <a href="javascript:void(0)">JS</a>
                <a href="tel:+1234567890">Phone</a>
                <a href="ftp://files.example.com/file">FTP</a>
                <a href="https://example.com/valid">Valid</a>
            </body>
            </html>
            """;
        var document = await _browsingContext.OpenAsync(req => req.Content(html));
        var baseUrl = "https://example.com/";
        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" };

        // Act
        var links = ExtractLinks(document, baseUrl, allowedHosts).ToList();

        // Assert
        links.Should().HaveCount(1);
        links.Should().Contain("https://example.com/valid");
    }

    [Fact]
    public async Task ExtractLinks_HandlesEmptyHref()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <a href="">Empty</a>
                <a>No href</a>
                <a href="   ">Whitespace</a>
                <a href="/valid">Valid</a>
            </body>
            </html>
            """;
        var document = await _browsingContext.OpenAsync(req => req.Content(html));
        var baseUrl = "https://example.com/";
        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" };

        // Act
        var links = ExtractLinks(document, baseUrl, allowedHosts).ToList();

        // Assert
        // Empty href="" resolves to current URL (valid URI behavior)
        // "No href" attribute is skipped (a[href] selector)
        // Whitespace href resolves to current URL
        links.Should().Contain("https://example.com/valid");
        links.Should().NotContain(l => l.Contains("No href"));
    }

    [Fact]
    public async Task ExtractLinks_RemovesQueryStringFragment()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <a href="/page?query=1#section">With query and fragment</a>
            </body>
            </html>
            """;
        var document = await _browsingContext.OpenAsync(req => req.Content(html));
        var baseUrl = "https://example.com/";
        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" };

        // Act
        var links = ExtractLinks(document, baseUrl, allowedHosts).ToList();

        // Assert
        // GetLeftPart(UriPartial.Query) includes the query but not the fragment
        links.Should().Contain("https://example.com/page?query=1");
    }

    #endregion

    #region Filename Sanitization Tests

    [Fact]
    public void SanitizeFilename_RemovesSlashes()
    {
        // Slashes are invalid on all platforms
        var result = SanitizeFilename("Title/With/Slashes");
        result.Should().Be("TitleWithSlashes");
    }

    [Fact]
    public void SanitizeFilename_EmptyReturnsPage()
    {
        SanitizeFilename("").Should().Be("page");
        SanitizeFilename("   ").Should().Be("page");
    }

    [Fact]
    public void SanitizeFilename_PreservesSimpleTitle()
    {
        SanitizeFilename("Simple Title").Should().Be("Simple Title");
    }

    [Fact]
    public void SanitizeFilename_RemovesInvalidChars()
    {
        // Test that invalid chars for current platform are removed
        var result = SanitizeFilename("Test/File");
        result.Should().NotContain("/");
    }

    [Fact]
    public void SanitizeFilename_TruncatesLongTitles()
    {
        // Arrange
        var longTitle = new string('A', 200);

        // Act
        var result = SanitizeFilename(longTitle);

        // Assert
        result.Should().HaveLength(100);
    }

    #endregion

    #region Helper Methods (extracted from WebCrawlerService for testing)

    /// <summary>
    /// Extracts content from document - mirrors WebCrawlerService.ExtractContent
    /// </summary>
    private static string ExtractContent(IDocument document, string? selector)
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

    /// <summary>
    /// Extracts links from document - mirrors WebCrawlerService.ExtractLinks
    /// </summary>
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

    /// <summary>
    /// Sanitizes filename - mirrors WebCrawlerService.SanitizeFilename
    /// </summary>
    private static string SanitizeFilename(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(title.Where(c => !invalid.Contains(c)).ToArray());
        if (sanitized.Length > 100) sanitized = sanitized[..100];
        return string.IsNullOrWhiteSpace(sanitized) ? "page" : sanitized;
    }

    #endregion
}
