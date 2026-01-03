using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace LucidRAG.Tests.Integration;

/// <summary>
/// Integration tests for the Crawl API
/// </summary>
[Collection("Integration")]
public class CrawlApiTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public CrawlApiTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        await _factory.EnsureDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.CleanupAsync();
    }

    [Fact]
    public async Task StartCrawl_ValidUrl_ReturnsSuccess()
    {
        // Arrange
        var request = new
        {
            seedUrls = new[] { "https://example.com" },
            maxPages = 5,
            maxDepth = 2
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/crawl", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("crawlId").GetString().Should().NotBeNullOrEmpty();
        result.GetProperty("message").GetString().Should().Contain("started");
    }

    [Fact]
    public async Task StartCrawl_EmptySeedUrls_ReturnsBadRequest()
    {
        // Arrange
        var request = new
        {
            seedUrls = Array.Empty<string>(),
            maxPages = 5,
            maxDepth = 2
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/crawl", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("error").GetString().Should().Contain("seed URL");
    }

    [Fact]
    public async Task StartCrawl_InvalidUrl_ReturnsBadRequest()
    {
        // Arrange
        var request = new
        {
            seedUrls = new[] { "not-a-valid-url" },
            maxPages = 5,
            maxDepth = 2
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/crawl", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("error").GetString().Should().Contain("Invalid URL");
    }

    [Fact]
    public async Task StartCrawl_NonHttpUrl_ReturnsBadRequest()
    {
        // Arrange
        var request = new
        {
            seedUrls = new[] { "ftp://ftp.example.com/files" },
            maxPages = 5,
            maxDepth = 2
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/crawl", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("error").GetString().Should().Contain("Invalid URL");
    }

    [Fact]
    public async Task GetCrawlJob_AfterStart_ReturnsJobDetails()
    {
        // Arrange - Start a crawl first
        var request = new
        {
            seedUrls = new[] { "https://example.com" },
            maxPages = 5,
            maxDepth = 2
        };

        var startResponse = await _client.PostAsJsonAsync("/api/crawl", request);
        var startResult = await startResponse.Content.ReadFromJsonAsync<JsonElement>();
        var crawlId = startResult.GetProperty("crawlId").GetString();

        // Act
        var response = await _client.GetAsync($"/api/crawl/{crawlId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("id").GetString().Should().Be(crawlId);
        result.GetProperty("seedUrls").GetArrayLength().Should().Be(1);
        result.GetProperty("maxPages").GetInt32().Should().Be(5);
        result.GetProperty("maxDepth").GetInt32().Should().Be(2);
        result.GetProperty("status").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetCrawlJob_NonExistent_ReturnsNotFound()
    {
        // Act
        var response = await _client.GetAsync($"/api/crawl/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("error").GetString().Should().Contain("not found");
    }

    [Fact]
    public async Task ListCrawlJobs_ReturnsAllJobs()
    {
        // Arrange - Start multiple crawl jobs
        for (int i = 0; i < 2; i++)
        {
            var request = new
            {
                seedUrls = new[] { $"https://example{i}.com" },
                maxPages = 5
            };
            await _client.PostAsJsonAsync("/api/crawl", request);
        }

        // Act
        var response = await _client.GetAsync("/api/crawl");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("crawls").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task StartCrawl_WithContentSelector_IncludesInJob()
    {
        // Arrange
        var request = new
        {
            seedUrls = new[] { "https://example.com" },
            contentSelector = "article.post-content",
            maxPages = 10,
            maxDepth = 3
        };

        var startResponse = await _client.PostAsJsonAsync("/api/crawl", request);
        var startResult = await startResponse.Content.ReadFromJsonAsync<JsonElement>();
        var crawlId = startResult.GetProperty("crawlId").GetString();

        // Act
        var response = await _client.GetAsync($"/api/crawl/{crawlId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("contentSelector").GetString().Should().Be("article.post-content");
    }

    [Fact]
    public async Task StartCrawl_WithCollection_IncludesCollectionId()
    {
        // Arrange - Create a collection first
        var collectionResponse = await _client.PostAsJsonAsync("/api/collections", new { name = "Crawl Test Collection" });
        collectionResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var collectionResult = await collectionResponse.Content.ReadFromJsonAsync<JsonElement>();
        var collectionId = collectionResult.GetProperty("id").GetString();

        var request = new
        {
            seedUrls = new[] { "https://example.com" },
            maxPages = 5,
            collectionId = collectionId
        };

        var startResponse = await _client.PostAsJsonAsync("/api/crawl", request);
        var startResult = await startResponse.Content.ReadFromJsonAsync<JsonElement>();
        var crawlId = startResult.GetProperty("crawlId").GetString();

        // Act
        var response = await _client.GetAsync($"/api/crawl/{crawlId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("collectionId").GetString().Should().Be(collectionId);
    }

    [Fact]
    public async Task StartCrawl_MultipleSeedUrls_AllIncluded()
    {
        // Arrange
        var request = new
        {
            seedUrls = new[]
            {
                "https://example.com/page1",
                "https://example.com/page2",
                "https://example.com/page3"
            },
            maxPages = 10
        };

        var startResponse = await _client.PostAsJsonAsync("/api/crawl", request);
        var startResult = await startResponse.Content.ReadFromJsonAsync<JsonElement>();
        var crawlId = startResult.GetProperty("crawlId").GetString();

        // Act
        var response = await _client.GetAsync($"/api/crawl/{crawlId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("seedUrls").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task StreamProgress_ValidCrawl_ReturnsSSEHeaders()
    {
        // Arrange - Start a crawl first
        var request = new
        {
            seedUrls = new[] { "https://example.com" },
            maxPages = 1
        };

        var startResponse = await _client.PostAsJsonAsync("/api/crawl", request);
        var startResult = await startResponse.Content.ReadFromJsonAsync<JsonElement>();
        var crawlId = startResult.GetProperty("crawlId").GetString();

        // Act - Request the SSE stream (we'll just check headers, not wait for completion)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var response = await _client.GetAsync(
                $"/api/crawl/{crawlId}/status",
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);

            // Assert - Check SSE headers
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        }
        catch (OperationCanceledException)
        {
            // Expected - we cancelled after checking headers
        }
    }

    [Fact]
    public async Task StartCrawl_DefaultValues_Applied()
    {
        // Arrange - Only provide seed URLs
        var request = new
        {
            seedUrls = new[] { "https://example.com" }
        };

        var startResponse = await _client.PostAsJsonAsync("/api/crawl", request);
        var startResult = await startResponse.Content.ReadFromJsonAsync<JsonElement>();
        var crawlId = startResult.GetProperty("crawlId").GetString();

        // Act
        var response = await _client.GetAsync($"/api/crawl/{crawlId}");

        // Assert - Check defaults are applied
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("maxPages").GetInt32().Should().Be(50); // Default
        result.GetProperty("maxDepth").GetInt32().Should().Be(3);  // Default
    }

    [Fact]
    public async Task ListCrawlJobs_IncludesStatusAndTimestamps()
    {
        // Arrange - Start a crawl
        var request = new
        {
            seedUrls = new[] { "https://example.com" }
        };
        await _client.PostAsJsonAsync("/api/crawl", request);

        // Act
        var response = await _client.GetAsync("/api/crawl");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var crawls = result.GetProperty("crawls");
        crawls.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        var firstCrawl = crawls[0];
        firstCrawl.TryGetProperty("id", out _).Should().BeTrue();
        firstCrawl.TryGetProperty("status", out _).Should().BeTrue();
        firstCrawl.TryGetProperty("startedAt", out _).Should().BeTrue();
    }
}
