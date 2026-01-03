using Xunit;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Config;

namespace Mostlylucid.DocSummarizer.Tests.Models;

/// <summary>
/// Unit tests for adaptive retrieval configuration and calculations
/// </summary>
public class AdaptiveRetrievalTests
{
    [Fact]
    public void RetrievalConfig_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var config = new RetrievalConfig();

        // Assert
        Assert.True(config.AdaptiveTopK);
        Assert.Equal(5.0, config.MinCoveragePercent);
        Assert.Equal(15, config.MinTopK);
        Assert.Equal(100, config.MaxTopK);
        Assert.Equal(1.5, config.NarrativeBoost);
    }

    [Fact]
    public void AdaptiveRetrievalConfig_ApplyTo_SetsCorrectValues()
    {
        // Arrange
        var adaptiveConfig = new AdaptiveRetrievalConfig
        {
            Enabled = true,
            MinCoveragePercent = 7.0,
            MinTopK = 20,
            MaxTopK = 150,
            NarrativeBoost = 2.0
        };
        var retrievalConfig = new RetrievalConfig();

        // Act
        adaptiveConfig.ApplyTo(retrievalConfig);

        // Assert
        Assert.True(retrievalConfig.AdaptiveTopK);
        Assert.Equal(7.0, retrievalConfig.MinCoveragePercent);
        Assert.Equal(20, retrievalConfig.MinTopK);
        Assert.Equal(150, retrievalConfig.MaxTopK);
        Assert.Equal(2.0, retrievalConfig.NarrativeBoost);
    }

    [Fact]
    public void AdaptiveRetrievalConfig_Disabled_SetsAdaptiveTopKFalse()
    {
        // Arrange
        var adaptiveConfig = new AdaptiveRetrievalConfig
        {
            Enabled = false
        };
        var retrievalConfig = new RetrievalConfig();

        // Act
        adaptiveConfig.ApplyTo(retrievalConfig);

        // Assert
        Assert.False(retrievalConfig.AdaptiveTopK);
    }

    [Theory]
    [InlineData(100, 5.0, 1.0, 15, 100, 15)]  // Small doc, use MinTopK
    [InlineData(500, 5.0, 1.0, 15, 100, 25)]  // 500 * 5% = 25
    [InlineData(1000, 5.0, 1.0, 15, 100, 50)] // 1000 * 5% = 50
    [InlineData(2000, 5.0, 1.0, 15, 100, 100)] // 2000 * 5% = 100, capped at MaxTopK
    [InlineData(3000, 5.0, 1.0, 15, 100, 100)] // Capped at MaxTopK
    public void CalculateAdaptiveTopK_NonNarrative_ReturnsExpected(
        int segmentCount, 
        double minCoverage, 
        double narrativeBoost, 
        int minTopK, 
        int maxTopK,
        int expectedTopK)
    {
        // This simulates the calculation logic
        var coverageTopK = (int)Math.Ceiling(segmentCount * minCoverage / 100.0);
        var adaptiveTopK = Math.Max(minTopK, Math.Min(maxTopK, coverageTopK));

        Assert.Equal(expectedTopK, adaptiveTopK);
    }

    [Theory]
    [InlineData(1000, 5.0, 1.5, 15, 100, 75)]  // 1000 * 5% * 1.5 = 75
    [InlineData(2000, 5.0, 1.5, 15, 100, 100)] // 2000 * 5% * 1.5 = 150, capped at 100
    [InlineData(500, 5.0, 1.5, 15, 100, 38)]   // 500 * 5% * 1.5 = 37.5 -> 38
    public void CalculateAdaptiveTopK_Narrative_AppliesBoost(
        int segmentCount,
        double minCoverage,
        double narrativeBoost,
        int minTopK,
        int maxTopK,
        int expectedTopK)
    {
        // This simulates the calculation logic with narrative boost
        var coverageTopK = (int)Math.Ceiling(segmentCount * minCoverage / 100.0);
        var boostedTopK = (int)Math.Ceiling(coverageTopK * narrativeBoost);
        var adaptiveTopK = Math.Max(minTopK, Math.Min(maxTopK, boostedTopK));

        Assert.Equal(expectedTopK, adaptiveTopK);
    }

    [Fact]
    public void ExtractionConfigSection_ToExtractionConfig_MapsCorrectly()
    {
        // Arrange
        var section = new ExtractionConfigSection
        {
            ExtractionRatio = 0.2,
            MinSegments = 15,
            MaxSegments = 200,
            MaxSegmentsToEmbed = 300,
            MmrLambda = 0.8
        };

        // Act
        var config = section.ToExtractionConfig();

        // Assert
        Assert.Equal(0.2, config.ExtractionRatio);
        Assert.Equal(15, config.MinSegments);
        Assert.Equal(200, config.MaxSegments);
        Assert.Equal(300, config.MaxSegmentsToEmbed);
        Assert.Equal(0.8, config.MmrLambda);
    }

    [Fact]
    public void RetrievalConfigSection_ToRetrievalConfig_MapsCorrectly()
    {
        // Arrange
        var section = new RetrievalConfigSection
        {
            TopK = 30,
            FallbackCount = 10,
            UseRRF = false,
            RrfK = 50,
            UseHybridSearch = false,
            Alpha = 0.7,
            MinSimilarity = 0.4
        };

        // Act
        var config = section.ToRetrievalConfig();

        // Assert
        Assert.Equal(30, config.TopK);
        Assert.Equal(10, config.FallbackCount);
        Assert.False(config.UseRRF);
        Assert.Equal(50, config.RrfK);
        Assert.False(config.UseHybridSearch);
        Assert.Equal(0.7, config.Alpha);
        Assert.Equal(0.4, config.MinSimilarity);
    }
}
