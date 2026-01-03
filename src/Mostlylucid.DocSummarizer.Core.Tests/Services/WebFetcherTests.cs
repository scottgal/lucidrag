using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services;
using Xunit;

namespace Mostlylucid.DocSummarizer.Tests.Services;

/// <summary>
/// Unit tests for WebFetcher - tests security validation without network calls
/// </summary>
public class WebFetcherTests
{
    private readonly WebFetchConfig _enabledConfig = new()
    {
        Enabled = true,
        Mode = WebFetchMode.Simple,
        TimeoutSeconds = 30
    };

    private readonly WebFetchConfig _disabledConfig = new()
    {
        Enabled = false
    };

    [Fact]
    public async Task FetchAsync_WhenDisabled_ThrowsInvalidOperationException()
    {
        // Arrange
        var fetcher = new WebFetcher(_disabledConfig);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fetcher.FetchAsync("https://example.com", WebFetchMode.Simple));
        
        Assert.Contains("not enabled", ex.Message);
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    public async Task FetchAsync_WithBlockedScheme_ThrowsSecurityException(string url)
    {
        // Arrange
        var fetcher = new WebFetcher(_enabledConfig);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<SecurityException>(
            () => fetcher.FetchAsync(url, WebFetchMode.Simple));
        
        Assert.Contains("scheme", ex.Message.ToLower());
    }

    [Fact]
    public async Task FetchAsync_WithInvalidUrl_ThrowsArgumentException()
    {
        // Arrange
        var fetcher = new WebFetcher(_enabledConfig);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => fetcher.FetchAsync("not-a-valid-url", WebFetchMode.Simple));
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://10.0.0.1")]
    [InlineData("http://172.16.0.1")]
    [InlineData("http://192.168.1.1")]
    [InlineData("http://169.254.169.254")] // AWS metadata
    public async Task FetchAsync_WithPrivateIP_ThrowsSecurityException(string url)
    {
        // Arrange
        var fetcher = new WebFetcher(_enabledConfig);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<SecurityException>(
            () => fetcher.FetchAsync(url, WebFetchMode.Simple));
        
        Assert.True(
            ex.Message.Contains("private", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("metadata", StringComparison.OrdinalIgnoreCase),
            $"Expected security message about private/blocked IP, got: {ex.Message}");
    }

    [Fact]
    public void WebFetcher_CanBeConstructedWithConfig()
    {
        // Arrange & Act
        var fetcher = new WebFetcher(_enabledConfig);

        // Assert
        Assert.NotNull(fetcher);
    }

    [Theory]
    [InlineData(WebFetchMode.Simple)]
    [InlineData(WebFetchMode.Playwright)]
    public void WebFetchConfig_Mode_CanBeParsedFromString(WebFetchMode mode)
    {
        // Arrange
        var modeString = mode.ToString();

        // Act
        var parsed = Enum.Parse<WebFetchMode>(modeString);

        // Assert
        Assert.Equal(mode, parsed);
    }

    [Fact]
    public void WebFetchConfig_Defaults_AreReasonable()
    {
        // Arrange & Act
        var config = new WebFetchConfig();

        // Assert
        Assert.False(config.Enabled); // Should be opt-in
        Assert.Equal(WebFetchMode.Simple, config.Mode); // Default to fast/simple
        Assert.Equal(30, config.TimeoutSeconds); // Reasonable default timeout
        Assert.NotEmpty(config.UserAgent); // Should have a user agent
    }
}
