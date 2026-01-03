using Mostlylucid.DocSummarizer.Config;
using Xunit;

namespace Mostlylucid.DocSummarizer.Tests.Config;

/// <summary>
/// Tests for WebFetchConfig and WebFetchMode
/// </summary>
public class WebFetchConfigTests
{
    [Fact]
    public void WebFetchConfig_DefaultMode_IsSimple()
    {
        // Arrange & Act
        var config = new WebFetchConfig();

        // Assert
        Assert.Equal(WebFetchMode.Simple, config.Mode);
    }

    [Fact]
    public void WebFetchConfig_DefaultEnabled_IsFalse()
    {
        // Arrange & Act
        var config = new WebFetchConfig();

        // Assert
        Assert.False(config.Enabled);
    }

    [Fact]
    public void WebFetchConfig_DefaultTimeout_Is30Seconds()
    {
        // Arrange & Act
        var config = new WebFetchConfig();

        // Assert
        Assert.Equal(30, config.TimeoutSeconds);
    }

    [Fact]
    public void WebFetchConfig_DefaultUserAgent_ContainsDocSummarizer()
    {
        // Arrange & Act
        var config = new WebFetchConfig();

        // Assert
        Assert.Contains("DocSummarizer", config.UserAgent);
    }

    [Fact]
    public void WebFetchConfig_BrowserExecutablePath_DefaultsToNull()
    {
        // Arrange & Act
        var config = new WebFetchConfig();

        // Assert
        Assert.Null(config.BrowserExecutablePath);
    }

    [Theory]
    [InlineData(WebFetchMode.Simple)]
    [InlineData(WebFetchMode.Playwright)]
    public void WebFetchConfig_Mode_CanBeSet(WebFetchMode expectedMode)
    {
        // Arrange
        var config = new WebFetchConfig();

        // Act
        config.Mode = expectedMode;

        // Assert
        Assert.Equal(expectedMode, config.Mode);
    }

    [Fact]
    public void WebFetchMode_Simple_HasValue0()
    {
        // Assert
        Assert.Equal(0, (int)WebFetchMode.Simple);
    }

    [Fact]
    public void WebFetchMode_Playwright_HasValue1()
    {
        // Assert
        Assert.Equal(1, (int)WebFetchMode.Playwright);
    }
}
