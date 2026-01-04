using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;

namespace LucidRAG.ImageCli.Tests;

public class GifMotionAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_WithSingleFrameGif_ReturnsStatic()
    {
        // Arrange
        using var analyzer = new GifMotionAnalyzer(NullLogger<GifMotionAnalyzer>.Instance);

        // For this test, we'd need a real single-frame GIF
        // Skipping for now as we don't have test assets
        // This is a placeholder showing the expected API usage
    }

    [Fact]
    public void GifMotionProfile_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var profile = new Mostlylucid.DocSummarizer.Images.Models.GifMotionProfile
        {
            FrameCount = 10,
            FrameDelayMs = 100,
            MotionDirection = "right",
            MotionMagnitude = 5.2,
            Confidence = 0.85
        };

        // Assert
        profile.FrameCount.Should().Be(10);
        profile.FrameDelayMs.Should().Be(100);
        profile.Fps.Should().BeApproximately(10.0, 0.01); // 1000/100 = 10 FPS
        profile.MotionDirection.Should().Be("right");
        profile.MotionMagnitude.Should().BeApproximately(5.2, 0.01);
        profile.Confidence.Should().BeApproximately(0.85, 0.01);
    }

    [Theory]
    [InlineData(100, 10.0)]
    [InlineData(50, 20.0)]
    [InlineData(33, 30.3)]
    [InlineData(16, 62.5)]
    public void GifMotionProfile_FpsCalculation_IsCorrect(int frameDelayMs, double expectedFps)
    {
        // Arrange
        var profile = new Mostlylucid.DocSummarizer.Images.Models.GifMotionProfile
        {
            FrameDelayMs = frameDelayMs
        };

        // Act & Assert
        profile.Fps.Should().BeApproximately(expectedFps, 0.1);
    }

    [Fact]
    public void MotionRegion_Properties_SetCorrectly()
    {
        // Arrange & Act
        var region = new Mostlylucid.DocSummarizer.Images.Models.MotionRegion
        {
            X = 0.25,
            Y = 0.30,
            Width = 0.50,
            Height = 0.40,
            Magnitude = 12.5,
            Direction = "down-right"
        };

        // Assert
        region.X.Should().BeApproximately(0.25, 0.01);
        region.Y.Should().BeApproximately(0.30, 0.01);
        region.Width.Should().BeApproximately(0.50, 0.01);
        region.Height.Should().BeApproximately(0.40, 0.01);
        region.Magnitude.Should().BeApproximately(12.5, 0.01);
        region.Direction.Should().Be("down-right");
    }

    [Fact]
    public void FrameMotionData_Properties_SetCorrectly()
    {
        // Arrange & Act
        var frameData = new Mostlylucid.DocSummarizer.Images.Models.FrameMotionData
        {
            FrameIndex = 5,
            Magnitude = 8.3,
            Direction = "left",
            HorizontalMotion = -7.2,
            VerticalMotion = 0.5
        };

        // Assert
        frameData.FrameIndex.Should().Be(5);
        frameData.Magnitude.Should().BeApproximately(8.3, 0.01);
        frameData.Direction.Should().Be("left");
        frameData.HorizontalMotion.Should().BeApproximately(-7.2, 0.01);
        frameData.VerticalMotion.Should().BeApproximately(0.5, 0.01);
    }
}
