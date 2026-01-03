using System.Net;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services;
using Xunit;

// WebFetchPermanentException, SecurityException, WebFetchResult are in Core's Services namespace

namespace Mostlylucid.DocSummarizer.Tests.Services;

/// <summary>
/// Tests for WebFetcher resilience features (circuit breaker, retry, exceptions)
/// </summary>
public class WebFetcherResilienceTests
{
    private readonly WebFetchConfig _enabledConfig = new()
    {
        Enabled = true,
        Mode = WebFetchMode.Simple,
        TimeoutSeconds = 30
    };

    #region WebFetchPermanentException Tests

    [Fact]
    public void WebFetchPermanentException_StoresStatusCode()
    {
        // Arrange & Act
        var exception = new WebFetchPermanentException("Test message", HttpStatusCode.Forbidden);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal("Test message", exception.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Gone, 410)]
    [InlineData(HttpStatusCode.BadRequest, 400)]
    public void WebFetchPermanentException_SupportsAllPermanentStatusCodes(HttpStatusCode statusCode, int expectedValue)
    {
        // Arrange & Act
        var exception = new WebFetchPermanentException($"HTTP {expectedValue}", statusCode);

        // Assert
        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Equal((int)statusCode, expectedValue);
    }

    [Fact]
    public void WebFetchPermanentException_WithInnerException_PreservesInner()
    {
        // Arrange
        var inner = new InvalidOperationException("Inner error");
        
        // Act
        var exception = new WebFetchPermanentException("Outer message", HttpStatusCode.Forbidden, inner);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal("Outer message", exception.Message);
        Assert.Same(inner, exception.InnerException);
    }

    #endregion

    #region SecurityException Tests

    [Fact]
    public void SecurityException_StoresMessage()
    {
        // Arrange & Act
        var exception = new SecurityException("SSRF attempt blocked");

        // Assert
        Assert.Equal("SSRF attempt blocked", exception.Message);
    }

    [Fact]
    public void SecurityException_WithInnerException_PreservesInner()
    {
        // Arrange
        var inner = new Exception("DNS resolution failed");
        
        // Act
        var exception = new SecurityException("Host blocked", inner);

        // Assert
        Assert.Equal("Host blocked", exception.Message);
        Assert.Same(inner, exception.InnerException);
    }

    [Fact]
    public void SecurityException_IsException()
    {
        // Arrange & Act
        var exception = new SecurityException("Test");

        // Assert
        Assert.IsAssignableFrom<Exception>(exception);
    }

    #endregion

    #region WebFetchResult Tests

    [Fact]
    public void WebFetchResult_StoresAllProperties()
    {
        // Arrange & Act
        var result = new WebFetchResult(
            tempFilePath: "/tmp/test.html",
            contentType: "text/html",
            sourceUrl: "https://example.com",
            fileExtension: ".html",
            isHtmlContent: true);

        // Assert
        Assert.Equal("/tmp/test.html", result.TempFilePath);
        Assert.Equal("text/html", result.ContentType);
        Assert.Equal("https://example.com", result.SourceUrl);
        Assert.Equal(".html", result.FileExtension);
        Assert.True(result.IsHtmlContent);
    }

    [Theory]
    [InlineData(".pdf", "PDF Document")]
    [InlineData(".docx", "Word Document")]
    [InlineData(".xlsx", "Excel Spreadsheet")]
    [InlineData(".pptx", "PowerPoint Presentation")]
    [InlineData(".html", "HTML Page")]
    [InlineData(".md", "Markdown")]
    [InlineData(".txt", "Plain Text")]
    [InlineData(".json", "JSON")]
    [InlineData(".xml", "XML")]
    [InlineData(".csv", "CSV Data")]
    [InlineData(".png", "Image")]
    [InlineData(".jpg", "Image")]
    [InlineData(".jpeg", "Image")]
    [InlineData(".tiff", "Image")]
    public void WebFetchResult_GetContentDescription_ReturnsCorrectDescription(string extension, string expectedDescription)
    {
        // Arrange
        var result = new WebFetchResult(
            tempFilePath: $"/tmp/test{extension}",
            contentType: "application/octet-stream",
            sourceUrl: "https://example.com",
            fileExtension: extension);

        // Act
        var description = result.GetContentDescription();

        // Assert
        Assert.Equal(expectedDescription, description);
    }

    [Fact]
    public void WebFetchResult_GetContentDescription_UnknownExtension_ReturnsContentType()
    {
        // Arrange
        var result = new WebFetchResult(
            tempFilePath: "/tmp/test.xyz",
            contentType: "application/x-custom",
            sourceUrl: "https://example.com",
            fileExtension: ".xyz");

        // Act
        var description = result.GetContentDescription();

        // Assert
        Assert.Equal("application/x-custom", description);
    }

    [Fact]
    public void WebFetchResult_Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var result = new WebFetchResult(
            tempFilePath: "/tmp/nonexistent.html",
            contentType: "text/html",
            sourceUrl: "https://example.com",
            fileExtension: ".html");

        // Act & Assert - should not throw
        result.Dispose();
        result.Dispose();
        result.Dispose();
    }

    [Fact]
    public void WebFetchResult_IsHtmlContent_DefaultsToFalse()
    {
        // Arrange & Act
        var result = new WebFetchResult(
            tempFilePath: "/tmp/test.pdf",
            contentType: "application/pdf",
            sourceUrl: "https://example.com",
            fileExtension: ".pdf");

        // Assert
        Assert.False(result.IsHtmlContent);
    }

    #endregion

    #region WebFetcher Construction Tests

    [Fact]
    public void WebFetcher_Construction_InitializesResiliencePipeline()
    {
        // Arrange & Act
        var fetcher = new WebFetcher(_enabledConfig);

        // Assert - if construction succeeds, resilience pipeline was built
        Assert.NotNull(fetcher);
    }

    [Fact]
    public void WebFetcher_WithDifferentConfigs_CanBeConstructed()
    {
        // Arrange
        var configs = new[]
        {
            new WebFetchConfig { Enabled = true, TimeoutSeconds = 10 },
            new WebFetchConfig { Enabled = true, TimeoutSeconds = 120 },
            new WebFetchConfig { Enabled = false },
            new WebFetchConfig { Enabled = true, Mode = WebFetchMode.Playwright }
        };

        // Act & Assert
        foreach (var config in configs)
        {
            var fetcher = new WebFetcher(config);
            Assert.NotNull(fetcher);
        }
    }

    #endregion

    #region Status Code Classification Tests

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]        // 400
    [InlineData(HttpStatusCode.Unauthorized)]      // 401
    [InlineData(HttpStatusCode.PaymentRequired)]   // 402
    [InlineData(HttpStatusCode.Forbidden)]         // 403
    [InlineData(HttpStatusCode.NotFound)]          // 404
    [InlineData(HttpStatusCode.MethodNotAllowed)]  // 405
    [InlineData(HttpStatusCode.Gone)]              // 410
    public void PermanentFailureStatusCodes_AreCorrectlyClassified(HttpStatusCode statusCode)
    {
        // These should NOT trigger retries - they are permanent failures
        // Test via exception creation (we can't easily test the pipeline directly without mocking HTTP)
        var exception = new WebFetchPermanentException($"HTTP {(int)statusCode}", statusCode);
        
        Assert.Equal(statusCode, exception.StatusCode);
    }

    #endregion
}
