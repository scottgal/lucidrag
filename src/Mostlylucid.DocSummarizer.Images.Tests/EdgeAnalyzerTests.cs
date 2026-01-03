using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Mostlylucid.DocSummarizer.Images.Tests;

public class EdgeAnalyzerTests
{
    private readonly EdgeAnalyzer _analyzer = new();

    #region CalculateEdgeDensity Tests

    [Fact]
    public void CalculateEdgeDensity_SolidColor_ReturnsLowEdgeDensity()
    {
        using var image = TestImageGenerator.CreateSolidColor(200, 200, Color.Blue);

        var edgeDensity = _analyzer.CalculateEdgeDensity(image);

        Assert.True(edgeDensity < 0.1, $"Expected low edge density for solid color, got {edgeDensity}");
    }

    [Fact]
    public void CalculateEdgeDensity_Checkerboard_ReturnsHighEdgeDensity()
    {
        using var image = TestImageGenerator.CreateCheckerboard(200, 200, 10);

        var edgeDensity = _analyzer.CalculateEdgeDensity(image);

        Assert.True(edgeDensity > 0.3, $"Expected high edge density for checkerboard, got {edgeDensity}");
    }

    [Fact]
    public void CalculateEdgeDensity_Gradient_ReturnsLowEdgeDensity()
    {
        using var image = TestImageGenerator.CreateGradient(200, 200, Color.Black, Color.White);

        var edgeDensity = _analyzer.CalculateEdgeDensity(image);

        Assert.True(edgeDensity < 0.2, $"Expected low edge density for gradient, got {edgeDensity}");
    }

    [Fact]
    public void CalculateEdgeDensity_Stripes_ReturnsMediumEdgeDensity()
    {
        using var image = TestImageGenerator.CreateHorizontalStripes(200, 200, 20);

        var edgeDensity = _analyzer.CalculateEdgeDensity(image);

        Assert.InRange(edgeDensity, 0.1, 0.5);
    }

    [Fact]
    public void CalculateEdgeDensity_ReturnsValueInRange()
    {
        using var image = TestImageGenerator.CreateSharpImage(300, 300);

        var edgeDensity = _analyzer.CalculateEdgeDensity(image);

        Assert.InRange(edgeDensity, 0.0, 1.0);
    }

    #endregion

    #region CalculateLuminanceEntropy Tests

    [Fact]
    public void CalculateLuminanceEntropy_SolidColor_ReturnsLowEntropy()
    {
        using var image = TestImageGenerator.CreateSolidColor(200, 200, Color.Gray);

        var entropy = _analyzer.CalculateLuminanceEntropy(image);

        Assert.True(entropy < 1.0, $"Expected low entropy for solid color, got {entropy}");
    }

    [Fact]
    public void CalculateLuminanceEntropy_Noise_ReturnsHighEntropy()
    {
        using var image = TestImageGenerator.CreateNoiseImage(200, 200);

        var entropy = _analyzer.CalculateLuminanceEntropy(image);

        Assert.True(entropy > 5.0, $"Expected high entropy for noise, got {entropy}");
    }

    [Fact]
    public void CalculateLuminanceEntropy_ReturnsValueInRange()
    {
        using var image = TestImageGenerator.CreateColorBlocks(200, 200);

        var entropy = _analyzer.CalculateLuminanceEntropy(image);

        // Entropy should be between 0 and 8 (log2(256))
        Assert.InRange(entropy, 0.0, 8.0);
    }

    [Fact]
    public void CalculateLuminanceEntropy_Gradient_ReturnsMediumEntropy()
    {
        using var image = TestImageGenerator.CreateGradient(200, 200, Color.Black, Color.White);

        var entropy = _analyzer.CalculateLuminanceEntropy(image);

        // Gradient has many unique values
        Assert.InRange(entropy, 2.0, 7.5);
    }

    #endregion

    #region CalculateStraightEdgeRatio Tests

    [Fact]
    public void CalculateStraightEdgeRatio_HorizontalStripes_ReturnsHighRatio()
    {
        using var image = TestImageGenerator.CreateHorizontalStripes(200, 200, 20);

        var ratio = _analyzer.CalculateStraightEdgeRatio(image);

        Assert.True(ratio > 0.3, $"Expected high straight edge ratio for horizontal stripes, got {ratio}");
    }

    [Fact]
    public void CalculateStraightEdgeRatio_VerticalStripes_ReturnsHighRatio()
    {
        using var image = TestImageGenerator.CreateVerticalStripes(200, 200, 20);

        var ratio = _analyzer.CalculateStraightEdgeRatio(image);

        Assert.True(ratio > 0.3, $"Expected high straight edge ratio for vertical stripes, got {ratio}");
    }

    [Fact]
    public void CalculateStraightEdgeRatio_SolidColor_ReturnsLowRatio()
    {
        using var image = TestImageGenerator.CreateSolidColor(200, 200, Color.Blue);

        var ratio = _analyzer.CalculateStraightEdgeRatio(image);

        Assert.True(ratio < 0.2, $"Expected low straight edge ratio for solid color, got {ratio}");
    }

    [Fact]
    public void CalculateStraightEdgeRatio_ScreenshotLike_ReturnsHighRatio()
    {
        using var image = TestImageGenerator.CreateScreenshotLike(400, 300);

        var ratio = _analyzer.CalculateStraightEdgeRatio(image);

        Assert.True(ratio > 0.4, $"Expected high straight edge ratio for screenshot, got {ratio}");
    }

    [Fact]
    public void CalculateStraightEdgeRatio_ReturnsValueInRange()
    {
        using var image = TestImageGenerator.CreateDiagramLike(300, 300);

        var ratio = _analyzer.CalculateStraightEdgeRatio(image);

        Assert.InRange(ratio, 0.0, 1.0);
    }

    #endregion

    #region Real Image Tests

    [Fact]
    public void CalculateEdgeDensity_RealScreenshot_DetectsEdges()
    {
        var testImagePath = GetTestImagePath("01-home.png");
        if (!File.Exists(testImagePath))
        {
            return;
        }

        using var image = Image.Load<Rgba32>(testImagePath);
        var edgeDensity = _analyzer.CalculateEdgeDensity(image);

        // Screenshots have varying edge density from UI elements
        Assert.InRange(edgeDensity, 0.01, 0.8);
    }

    [Fact]
    public void CalculateStraightEdgeRatio_RealScreenshot_DetectsStraightEdges()
    {
        var testImagePath = GetTestImagePath("03-chat-response.png");
        if (!File.Exists(testImagePath))
        {
            return;
        }

        using var image = Image.Load<Rgba32>(testImagePath);
        var ratio = _analyzer.CalculateStraightEdgeRatio(image);

        // Screenshots should have high straight edge ratio
        Assert.True(ratio > 0.2, $"Expected screenshot to have straight edges, got ratio {ratio}");
    }

    #endregion

    private static string GetTestImagePath(string filename)
    {
        var dir = Path.GetDirectoryName(typeof(EdgeAnalyzerTests).Assembly.Location)!;
        return Path.Combine(dir, "TestImages", filename);
    }
}
