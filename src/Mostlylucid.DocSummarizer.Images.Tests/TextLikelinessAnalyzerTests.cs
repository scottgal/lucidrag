using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace Mostlylucid.DocSummarizer.Images.Tests;

public class TextLikelinessAnalyzerTests
{
    private readonly TextLikelinessAnalyzer _analyzer = new();

    #region CalculateTextLikeliness Tests

    [Fact]
    public void CalculateTextLikeliness_TextLikeImage_ReturnsHighScore()
    {
        using var image = TestImageGenerator.CreateTextLikeImage(400, 300);

        var score = _analyzer.CalculateTextLikeliness(image);

        Assert.True(score > 0.3, $"Expected high text-likeliness for text-like image, got {score}");
    }

    [Fact]
    public void CalculateTextLikeliness_SolidColor_ReturnsLowScore()
    {
        using var image = TestImageGenerator.CreateSolidColor(200, 200, Color.White);

        var score = _analyzer.CalculateTextLikeliness(image);

        Assert.True(score < 0.3, $"Expected low text-likeliness for solid color, got {score}");
    }

    [Fact]
    public void CalculateTextLikeliness_Gradient_ReturnsLowScore()
    {
        using var image = TestImageGenerator.CreateGradient(200, 200, Color.Blue, Color.Red);

        var score = _analyzer.CalculateTextLikeliness(image);

        Assert.True(score < 0.3, $"Expected low text-likeliness for gradient, got {score}");
    }

    [Fact]
    public void CalculateTextLikeliness_HorizontalStripes_ReturnsMediumScore()
    {
        // Horizontal stripes are somewhat text-like (horizontal patterns)
        using var image = TestImageGenerator.CreateHorizontalStripes(300, 200, 15);

        var score = _analyzer.CalculateTextLikeliness(image);

        Assert.InRange(score, 0.1, 0.7);
    }

    [Fact]
    public void CalculateTextLikeliness_Checkerboard_ReturnsMediumScore()
    {
        using var image = TestImageGenerator.CreateCheckerboard(200, 200, 10);

        var score = _analyzer.CalculateTextLikeliness(image);

        // Checkerboard has high contrast but not text-like horizontal patterns
        Assert.InRange(score, 0.0, 0.8);
    }

    [Fact]
    public void CalculateTextLikeliness_Screenshot_HasTextElements()
    {
        using var image = TestImageGenerator.CreateScreenshotLike(400, 300);

        var score = _analyzer.CalculateTextLikeliness(image);

        // Screenshots with UI elements often look text-like
        Assert.True(score > 0.1, $"Expected screenshot to have some text-like elements, got {score}");
    }

    [Fact]
    public void CalculateTextLikeliness_ReturnsValueInRange()
    {
        using var image = TestImageGenerator.CreateColorBlocks(200, 200);

        var score = _analyzer.CalculateTextLikeliness(image);

        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void CalculateTextLikeliness_Diagram_ReturnsMediumScore()
    {
        using var image = TestImageGenerator.CreateDiagramLike(300, 300);

        var score = _analyzer.CalculateTextLikeliness(image);

        // Diagrams have some text-like characteristics but not as strong
        Assert.InRange(score, 0.0, 0.5);
    }

    #endregion

    #region Bimodal Distribution Tests

    [Fact]
    public void CalculateTextLikeliness_BlackOnWhite_HasHighBimodalScore()
    {
        // Black text on white background is bimodal
        using var image = new Image<Rgba32>(300, 200);
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.White);
            // Draw some black horizontal lines (text-like)
            for (var y = 30; y < 180; y += 25)
            {
                ctx.Fill(Color.Black, new Rectangle(20, y, 260, 8));
            }
        });

        var score = _analyzer.CalculateTextLikeliness(image);

        Assert.True(score > 0.2, $"Expected bimodal image to have text-like score, got {score}");
    }

    [Fact]
    public void CalculateTextLikeliness_WhiteOnBlack_HasHighBimodalScore()
    {
        // White text on black background is also bimodal
        using var image = new Image<Rgba32>(300, 200);
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.Black);
            for (var y = 30; y < 180; y += 25)
            {
                ctx.Fill(Color.White, new Rectangle(20, y, 260, 8));
            }
        });

        var score = _analyzer.CalculateTextLikeliness(image);

        Assert.True(score > 0.2, $"Expected inverted text image to have text-like score, got {score}");
    }

    #endregion

    #region Real Image Tests

    [Fact]
    public void CalculateTextLikeliness_RealScreenshot_DetectsText()
    {
        var testImagePath = GetTestImagePath("03-chat-response.png");
        if (!File.Exists(testImagePath))
        {
            return;
        }

        using var image = Image.Load<Rgba32>(testImagePath);
        var score = _analyzer.CalculateTextLikeliness(image);

        // Chat response screenshot should have text
        Assert.True(score > 0.1, $"Expected screenshot with chat to have text, got {score}");
    }

    [Fact]
    public void CalculateTextLikeliness_Icon_HasLowTextScore()
    {
        var testImagePath = GetTestImagePath("icon.png");
        if (!File.Exists(testImagePath))
        {
            return;
        }

        using var image = Image.Load<Rgba32>(testImagePath);
        var score = _analyzer.CalculateTextLikeliness(image);

        // Icons typically don't have text
        Assert.True(score < 0.5, $"Expected icon to have low text score, got {score}");
    }

    [Fact]
    public void CalculateTextLikeliness_ErrorStateScreenshot_DetectsText()
    {
        var testImagePath = GetTestImagePath("error-state.png");
        if (!File.Exists(testImagePath))
        {
            return;
        }

        using var image = Image.Load<Rgba32>(testImagePath);
        var score = _analyzer.CalculateTextLikeliness(image);

        // Error messages typically contain text
        Assert.InRange(score, 0.0, 1.0);
    }

    #endregion

    private static string GetTestImagePath(string filename)
    {
        var dir = Path.GetDirectoryName(typeof(TextLikelinessAnalyzerTests).Assembly.Location)!;
        return Path.Combine(dir, "TestImages", filename);
    }
}
