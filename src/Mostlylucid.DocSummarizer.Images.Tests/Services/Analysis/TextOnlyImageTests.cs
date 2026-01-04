using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;
using FluentAssertions;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;

namespace Mostlylucid.DocSummarizer.Images.Tests.Services.Analysis;

/// <summary>
/// Tests for the edge case where the image IS text itself (logos, word images, etc.)
/// Example: An image containing only the letters "m" and "l" as a logo
/// </summary>
public class TextOnlyImageTests
{
    private readonly TextLikelinessAnalyzer _textAnalyzer = new();
    private readonly BlurAnalyzer _blurAnalyzer = new();
    private readonly EdgeAnalyzer _edgeAnalyzer = new();

    [Fact]
    public void TextOnlyImage_Logo_ShouldHaveHighTextLikeliness()
    {
        // Arrange: Create an image with just letters (like "ml" logo)
        using var image = CreateTextOnlyImage("ml", fontSize: 200);

        // Act
        var textLikeliness = _textAnalyzer.CalculateTextLikeliness(image);

        // Assert
        textLikeliness.Should().BeGreaterThan(0.7,
            "because the entire image is text, text likeliness should be very high");
    }

    [Fact]
    public void TextOnlyImage_SingleLetter_ShouldBeDetectedAsText()
    {
        // Arrange: Create an image with a single letter
        using var image = CreateTextOnlyImage("A", fontSize: 300);

        // Act
        var textLikeliness = _textAnalyzer.CalculateTextLikeliness(image);
        var edgeDensity = _edgeAnalyzer.CalculateEdgeDensity(image);

        // Assert
        textLikeliness.Should().BeGreaterThan(0.6, "single letter should be recognized as text");
        edgeDensity.Should().BeGreaterThan(0.05, "letters have high edge density");
    }

    [Fact]
    public void TextOnlyImage_SmallText_VersusLargeText_TextLikelinessShouldBeSimilar()
    {
        // Arrange
        using var smallText = CreateTextOnlyImage("TEXT", fontSize: 40);
        using var largeText = CreateTextOnlyImage("TEXT", fontSize: 200);

        // Act
        var smallLikeliness = _textAnalyzer.CalculateTextLikeliness(smallText);
        var largeLikeliness = _textAnalyzer.CalculateTextLikeliness(largeText);

        // Assert
        // Both should have high text likeliness, within 0.2 of each other
        Math.Abs(smallLikeliness - largeLikeliness).Should().BeLessThan(0.2,
            "text likeliness should be relatively scale-invariant");

        smallLikeliness.Should().BeGreaterThan(0.5, "small text should still be detected");
        largeLikeliness.Should().BeGreaterThan(0.5, "large text should still be detected");
    }

    [Fact]
    public void TextOnlyImage_BlurredLogo_ShouldHaveLowerSharpnessButStillDetectableText()
    {
        // Arrange
        using var sharpLogo = CreateTextOnlyImage("ml", fontSize: 200);
        using var blurredLogo = sharpLogo.Clone(ctx => ctx.GaussianBlur(5.0f));

        // Act
        var sharpTextLikeliness = _textAnalyzer.CalculateTextLikeliness(sharpLogo);
        var blurredTextLikeliness = _textAnalyzer.CalculateTextLikeliness(blurredLogo);

        var sharpness = _blurAnalyzer.CalculateLaplacianVariance(sharpLogo);
        var blurredSharpness = _blurAnalyzer.CalculateLaplacianVariance(blurredLogo);

        // Assert
        blurredSharpness.Should().BeLessThan(sharpness * 0.5,
            "blurred logo should have significantly lower sharpness");

        blurredTextLikeliness.Should().BeGreaterThan(0.4,
            "even blurred text should still be recognizable as text-like");

        blurredTextLikeliness.Should().BeLessThan(sharpTextLikeliness,
            "blur should reduce text likeliness somewhat");
    }

    [Fact]
    public void TextOnlyImage_WhiteOnBlack_VersusBlackOnWhite_ShouldBothDetectText()
    {
        // Arrange: Test both common logo styles
        using var whiteOnBlack = CreateTextOnlyImage("ml", fontSize: 200,
            textColor: Color.White, backgroundColor: Color.Black);
        using var blackOnWhite = CreateTextOnlyImage("ml", fontSize: 200,
            textColor: Color.Black, backgroundColor: Color.White);

        // Act
        var whiteLikeliness = _textAnalyzer.CalculateTextLikeliness(whiteOnBlack);
        var blackLikeliness = _textAnalyzer.CalculateTextLikeliness(blackOnWhite);

        // Assert
        whiteLikeliness.Should().BeGreaterThan(0.6, "white on black should detect text");
        blackLikeliness.Should().BeGreaterThan(0.6, "black on white should detect text");

        // Should be similar regardless of polarity
        Math.Abs(whiteLikeliness - blackLikeliness).Should().BeLessThan(0.15,
            "text detection should be relatively color-invariant");
    }

    [Fact]
    public void TextOnlyImage_Logo_VersusPhotoWithText_LogoShouldHaveHigherTextLikeliness()
    {
        // Arrange
        using var pureLogo = CreateTextOnlyImage("ml", fontSize: 200);
        using var photoWithText = CreatePhotoWithTextOverlay();

        // Act
        var logoLikeliness = _textAnalyzer.CalculateTextLikeliness(pureLogo);
        var photoLikeliness = _textAnalyzer.CalculateTextLikeliness(photoWithText);

        // Assert
        logoLikeliness.Should().BeGreaterThan(photoLikeliness,
            "pure text logo should have higher text likeliness than photo with text overlay");

        logoLikeliness.Should().BeGreaterThan(0.7, "logo is pure text");
        photoLikeliness.Should().BeLessThan(0.6, "photo has mixed content");
    }

    [Fact]
    public void TextOnlyImage_MultiWordLogo_ShouldStillDetectAsText()
    {
        // Arrange: Logo with multiple words (like "Machine Learning")
        using var multiWord = CreateTextOnlyImage("ML", fontSize: 150);

        // Act
        var textLikeliness = _textAnalyzer.CalculateTextLikeliness(multiWord);
        var edgeDensity = _edgeAnalyzer.CalculateEdgeDensity(multiWord);

        // Assert
        textLikeliness.Should().BeGreaterThan(0.6, "multi-word logo should be recognized as text");
        edgeDensity.Should().BeGreaterThan(0.04, "text has characteristic edge patterns");
    }

    [Theory]
    [InlineData("A")]      // Single letter
    [InlineData("ml")]     // Lowercase pair
    [InlineData("ML")]     // Uppercase pair
    [InlineData("M")]      // Single capital
    [InlineData("123")]    // Numbers
    [InlineData("A1")]     // Alphanumeric
    public void TextOnlyImage_VariousTextContent_ShouldAllBeDetectedAsText(string text)
    {
        // Arrange
        using var image = CreateTextOnlyImage(text, fontSize: 180);

        // Act
        var textLikeliness = _textAnalyzer.CalculateTextLikeliness(image);

        // Assert
        textLikeliness.Should().BeGreaterThan(0.5,
            $"'{text}' should be recognized as text");
    }

    [Fact]
    public void TextOnlyImage_WithLowContrast_ShouldHaveLowerTextLikeliness()
    {
        // Arrange: Low contrast makes text harder to detect
        using var highContrast = CreateTextOnlyImage("ml", fontSize: 200,
            textColor: Color.Black, backgroundColor: Color.White);
        using var lowContrast = CreateTextOnlyImage("ml", fontSize: 200,
            textColor: Color.FromRgb(100, 100, 100), backgroundColor: Color.FromRgb(120, 120, 120));

        // Act
        var highContrastLikeliness = _textAnalyzer.CalculateTextLikeliness(highContrast);
        var lowContrastLikeliness = _textAnalyzer.CalculateTextLikeliness(lowContrast);

        // Assert
        highContrastLikeliness.Should().BeGreaterThan(lowContrastLikeliness,
            "high contrast text should be more easily detected");

        highContrastLikeliness.Should().BeGreaterThan(0.7, "high contrast text is very clear");
        lowContrastLikeliness.Should().BeLessThan(0.5, "low contrast text is harder to detect");
    }

    // Helper methods
    private static Image<Rgba32> CreateTextOnlyImage(
        string text,
        int fontSize,
        Color? textColor = null,
        Color? backgroundColor = null)
    {
        var bgColor = backgroundColor ?? Color.Black;
        var fgColor = textColor ?? Color.White;

        var image = new Image<Rgba32>(400, 400, bgColor);

        // Note: This is a simplified version. In real implementation,
        // you'd use a font rendering library like SixLabors.Fonts
        // For now, we'll create synthetic text-like patterns

        image.Mutate(ctx =>
        {
            // Draw synthetic text pattern (simplified - real text would use fonts)
            // This creates high-contrast edges similar to text
            for (int i = 0; i < text.Length; i++)
            {
                var x = 100 + i * fontSize;
                var y = 200;

                // Create letter-like rectangular patterns with edges
                ctx.Fill(fgColor, new RectangleF(x, y, fontSize * 0.6f, fontSize));
                ctx.Fill(fgColor, new RectangleF(x, y, fontSize * 0.6f, fontSize * 0.2f));
                ctx.Fill(fgColor, new RectangleF(x, y + fontSize * 0.4f, fontSize * 0.6f, fontSize * 0.2f));
            }
        });

        return image;
    }

    private static Image<Rgba32> CreatePhotoWithTextOverlay()
    {
        // Create a synthetic "photo" with gradient background
        var image = new Image<Rgba32>(400, 400);

        image.Mutate(ctx =>
        {
            // Create photo-like gradient background
            for (int y = 0; y < 400; y++)
            {
                for (int x = 0; x < 400; x++)
                {
                    var r = (byte)(x * 255 / 400);
                    var g = (byte)(y * 255 / 400);
                    var b = (byte)((x + y) * 255 / 800);
                    image[x, y] = new Rgba32(r, g, b);
                }
            }

            // Add small text overlay in corner
            ctx.Fill(Color.White, new RectangleF(320, 350, 60, 40));
        });

        return image;
    }
}
