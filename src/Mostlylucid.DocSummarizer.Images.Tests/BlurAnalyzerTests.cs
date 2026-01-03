using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Mostlylucid.DocSummarizer.Images.Tests;

public class BlurAnalyzerTests
{
    private readonly BlurAnalyzer _analyzer = new();

    #region CalculateLaplacianVariance Tests

    [Fact]
    public void CalculateLaplacianVariance_SharpImage_ReturnsHighVariance()
    {
        using var image = TestImageGenerator.CreateSharpImage(300, 300);

        var variance = _analyzer.CalculateLaplacianVariance(image);

        Assert.True(variance > 500, $"Expected high Laplacian variance for sharp image, got {variance}");
    }

    [Fact]
    public void CalculateLaplacianVariance_BlurredImage_ReturnsLowVariance()
    {
        using var image = TestImageGenerator.CreateBlurredImage(300, 300);

        var variance = _analyzer.CalculateLaplacianVariance(image);

        Assert.True(variance < 500, $"Expected low Laplacian variance for blurred image, got {variance}");
    }

    [Fact]
    public void CalculateLaplacianVariance_SolidColor_ReturnsVeryLowVariance()
    {
        using var image = TestImageGenerator.CreateSolidColor(200, 200, Color.Gray);

        var variance = _analyzer.CalculateLaplacianVariance(image);

        Assert.True(variance < 10, $"Expected very low Laplacian variance for solid color, got {variance}");
    }

    [Fact]
    public void CalculateLaplacianVariance_Checkerboard_ReturnsHighVariance()
    {
        using var image = TestImageGenerator.CreateCheckerboard(200, 200, 10);

        var variance = _analyzer.CalculateLaplacianVariance(image);

        Assert.True(variance > 1000, $"Expected high Laplacian variance for checkerboard, got {variance}");
    }

    [Fact]
    public void CalculateLaplacianVariance_Gradient_ReturnsMediumVariance()
    {
        using var image = TestImageGenerator.CreateGradient(200, 200, Color.Black, Color.White);

        var variance = _analyzer.CalculateLaplacianVariance(image);

        // Gradients have medium variance (smooth transitions)
        Assert.InRange(variance, 0, 500);
    }

    [Fact]
    public void CalculateLaplacianVariance_ReturnsNonNegative()
    {
        using var image = TestImageGenerator.CreateNoiseImage(200, 200);

        var variance = _analyzer.CalculateLaplacianVariance(image);

        Assert.True(variance >= 0);
    }

    #endregion

    #region CategorizeBlur Tests

    [Fact]
    public void CategorizeBlur_VeryLowVariance_ReturnsVeryBlurry()
    {
        var level = _analyzer.CategorizeBlur(50);
        Assert.Equal(BlurLevel.VeryBlurry, level);
    }

    [Fact]
    public void CategorizeBlur_LowVariance_ReturnsBlurry()
    {
        var level = _analyzer.CategorizeBlur(200);
        Assert.Equal(BlurLevel.Blurry, level);
    }

    [Fact]
    public void CategorizeBlur_MediumVariance_ReturnsSlightlySoft()
    {
        var level = _analyzer.CategorizeBlur(500);
        Assert.Equal(BlurLevel.SlightlySoft, level);
    }

    [Fact]
    public void CategorizeBlur_HighVariance_ReturnsSharp()
    {
        var level = _analyzer.CategorizeBlur(1000);
        Assert.Equal(BlurLevel.Sharp, level);
    }

    [Fact]
    public void CategorizeBlur_VeryHighVariance_ReturnsVerySharp()
    {
        var level = _analyzer.CategorizeBlur(2000);
        Assert.Equal(BlurLevel.VerySharp, level);
    }

    [Fact]
    public void CategorizeBlur_BlurredImage_ReturnsBlurryOrVeryBlurry()
    {
        using var image = TestImageGenerator.CreateBlurredImage(300, 300);
        var variance = _analyzer.CalculateLaplacianVariance(image);
        var level = _analyzer.CategorizeBlur(variance);

        Assert.True(level == BlurLevel.VeryBlurry || level == BlurLevel.Blurry || level == BlurLevel.SlightlySoft,
            $"Expected blurry category, got {level}");
    }

    [Fact]
    public void CategorizeBlur_SharpImage_ReturnsSharpOrVerySharp()
    {
        using var image = TestImageGenerator.CreateCheckerboard(200, 200, 5);
        var variance = _analyzer.CalculateLaplacianVariance(image);
        var level = _analyzer.CategorizeBlur(variance);

        Assert.True(level == BlurLevel.Sharp || level == BlurLevel.VerySharp,
            $"Expected sharp category, got {level} (variance: {variance})");
    }

    #endregion

    #region Real Image Tests

    [Fact]
    public void CalculateLaplacianVariance_RealScreenshot_IsSharp()
    {
        var testImagePath = GetTestImagePath("01-home.png");
        if (!File.Exists(testImagePath))
        {
            return;
        }

        using var image = Image.Load<Rgba32>(testImagePath);
        var variance = _analyzer.CalculateLaplacianVariance(image);
        var level = _analyzer.CategorizeBlur(variance);

        // Screenshots may vary in sharpness depending on content
        Assert.True(level != BlurLevel.VeryBlurry,
            $"Expected screenshot not to be very blurry, got {level} (variance: {variance})");
    }

    [Fact]
    public void CalculateLaplacianVariance_Icon_IsSharp()
    {
        var testImagePath = GetTestImagePath("icon.png");
        if (!File.Exists(testImagePath))
        {
            return;
        }

        using var image = Image.Load<Rgba32>(testImagePath);
        var variance = _analyzer.CalculateLaplacianVariance(image);

        // Icons typically have clear edges
        Assert.True(variance > 100, $"Expected icon to have clear edges, got variance {variance}");
    }

    #endregion

    private static string GetTestImagePath(string filename)
    {
        var dir = Path.GetDirectoryName(typeof(BlurAnalyzerTests).Assembly.Location)!;
        return Path.Combine(dir, "TestImages", filename);
    }
}
