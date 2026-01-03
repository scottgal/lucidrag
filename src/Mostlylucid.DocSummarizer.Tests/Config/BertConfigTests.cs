using Xunit;
using Mostlylucid.DocSummarizer.Services;

namespace Mostlylucid.DocSummarizer.Tests.Config;

/// <summary>
/// Tests for BertConfig defaults and validation
/// </summary>
public class BertConfigTests
{
    [Fact]
    public void BertConfig_DefaultLambda_Is0Point7()
    {
        // Arrange & Act
        var config = new BertConfig();

        // Assert - 0.7 favors relevance slightly over diversity
        Assert.Equal(0.7, config.Lambda);
    }

    [Fact]
    public void BertConfig_DefaultExtractionRatio_Is0Point15()
    {
        // Arrange & Act
        var config = new BertConfig();

        // Assert - select ~15% of sentences
        Assert.Equal(0.15, config.ExtractionRatio);
    }

    [Fact]
    public void BertConfig_DefaultMinSentences_Is3()
    {
        // Arrange & Act
        var config = new BertConfig();

        // Assert - minimum 3 sentences for any summary
        Assert.Equal(3, config.MinSentences);
    }

    [Fact]
    public void BertConfig_DefaultMaxSentences_Is30()
    {
        // Arrange & Act
        var config = new BertConfig();

        // Assert - cap at 30 sentences
        Assert.Equal(30, config.MaxSentences);
    }

    [Fact]
    public void BertConfig_DefaultUsePositionWeighting_IsTrue()
    {
        // Arrange & Act
        var config = new BertConfig();

        // Assert - position weighting enabled by default
        Assert.True(config.UsePositionWeighting);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void BertConfig_Lambda_CanBeSetInValidRange(double lambda)
    {
        // Arrange
        var config = new BertConfig();

        // Act
        config.Lambda = lambda;

        // Assert
        Assert.Equal(lambda, config.Lambda);
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.10)]
    [InlineData(0.25)]
    public void BertConfig_ExtractionRatio_CanBeSet(double ratio)
    {
        // Arrange
        var config = new BertConfig();

        // Act
        config.ExtractionRatio = ratio;

        // Assert
        Assert.Equal(ratio, config.ExtractionRatio);
    }

    [Fact]
    public void BertConfig_AllPropertiesHaveSafeDefaults()
    {
        // Arrange & Act
        var config = new BertConfig();

        // Assert - all defaults are safe and reasonable
        Assert.True(config.Lambda >= 0 && config.Lambda <= 1, "Lambda should be between 0 and 1");
        Assert.True(config.ExtractionRatio > 0 && config.ExtractionRatio <= 1, "ExtractionRatio should be between 0 and 1");
        Assert.True(config.MinSentences > 0, "MinSentences should be positive");
        Assert.True(config.MaxSentences >= config.MinSentences, "MaxSentences should be >= MinSentences");
    }
}
