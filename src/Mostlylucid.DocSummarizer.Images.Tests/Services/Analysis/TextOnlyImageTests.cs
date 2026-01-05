using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.Fonts;
using Xunit;
using FluentAssertions;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;

namespace Mostlylucid.DocSummarizer.Images.Tests.Services.Analysis;

/// <summary>
/// Tests for the edge case where the image IS text itself (logos, word images, etc.)
/// Example: An image containing only the letters "m" and "l" as a logo
///
/// Note: These tests use synthetic rectangular patterns to simulate text.
/// The TextLikelinessAnalyzer uses edge density, histogram patterns, and complexity
/// metrics - simple rectangular patterns score lower than real rendered text.
/// Thresholds are calibrated for synthetic patterns, not real fonts.
/// </summary>
public class TextOnlyImageTests
{
    private readonly TextLikelinessAnalyzer _textAnalyzer = new();
    private readonly BlurAnalyzer _blurAnalyzer = new();
    private readonly EdgeAnalyzer _edgeAnalyzer = new();

    [Fact]
    public void TextOnlyImage_Logo_ShouldHavePositiveTextLikeliness()
    {
        // Arrange: Create an image with synthetic letter patterns (like "ml" logo)
        using var image = CreateTextOnlyImage("ml", fontSize: 200);

        // Act
        var textLikeliness = _textAnalyzer.CalculateTextLikeliness(image);

        // Assert: Synthetic patterns have lower scores than real fonts
        textLikeliness.Should().BeGreaterThan(0.15,
            "because the image has high-contrast patterns that suggest text presence");
    }

    [Fact]
    public void TextOnlyImage_SingleLetter_ShouldBeDetectedAsText()
    {
        // Arrange: Create an image with a single letter pattern
        using var image = CreateTextOnlyImage("A", fontSize: 300);

        // Act
        var textLikeliness = _textAnalyzer.CalculateTextLikeliness(image);
        var edgeDensity = _edgeAnalyzer.CalculateEdgeDensity(image);

        // Assert: Synthetic patterns have characteristic edge density
        textLikeliness.Should().BeGreaterThan(0.15, "synthetic pattern should show text-like characteristics");
        edgeDensity.Should().BeGreaterThan(0.02, "rectangular patterns have edge density");
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

        // Assert: Both should have similar text likeliness within tolerance
        Math.Abs(smallLikeliness - largeLikeliness).Should().BeLessThan(0.3,
            "text likeliness should be relatively scale-invariant");

        // Both should be positive (indicates some text-like characteristics)
        smallLikeliness.Should().BeGreaterThan(0.1, "small patterns should still show some text characteristics");
        largeLikeliness.Should().BeGreaterThan(0.1, "large patterns should still show some text characteristics");
    }

    [Fact]
    public void TextOnlyImage_BlurredLogo_ShouldHaveLowerSharpnessButStillDetectable()
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

        blurredTextLikeliness.Should().BeGreaterThan(0.1,
            "even blurred patterns should still be detectable");

        // Note: Blur may actually increase or decrease likeliness depending on how it affects edges
        // The key assertion is that both are still detectable (> 0.1)
    }

    [Fact]
    public void TextOnlyImage_WhiteOnBlack_VersusBlackOnWhite_ShouldBothBeDetectable()
    {
        // Arrange: Test both common logo styles
        using var whiteOnBlack = CreateTextOnlyImage("ml", fontSize: 200,
            textColor: Color.White, backgroundColor: Color.Black);
        using var blackOnWhite = CreateTextOnlyImage("ml", fontSize: 200,
            textColor: Color.Black, backgroundColor: Color.White);

        // Act
        var whiteLikeliness = _textAnalyzer.CalculateTextLikeliness(whiteOnBlack);
        var blackLikeliness = _textAnalyzer.CalculateTextLikeliness(blackOnWhite);

        // Assert: Both should be detectable
        whiteLikeliness.Should().BeGreaterThan(0.15, "white on black should show text-like patterns");
        blackLikeliness.Should().BeGreaterThan(0.15, "black on white should show text-like patterns");

        // Should be similar regardless of polarity
        Math.Abs(whiteLikeliness - blackLikeliness).Should().BeLessThan(0.2,
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

        // Assert: Pure text pattern should score higher than gradient with small overlay
        logoLikeliness.Should().BeGreaterThan(photoLikeliness,
            "pure text pattern should have higher text likeliness than photo with text overlay");

        logoLikeliness.Should().BeGreaterThan(0.15, "pattern has text-like characteristics");
    }

    [Fact]
    public void TextOnlyImage_MultiWordLogo_ShouldStillDetectAsText()
    {
        // Arrange: Logo with multiple word patterns
        using var multiWord = CreateTextOnlyImage("ML", fontSize: 150);

        // Act
        var textLikeliness = _textAnalyzer.CalculateTextLikeliness(multiWord);
        var edgeDensity = _edgeAnalyzer.CalculateEdgeDensity(multiWord);

        // Assert
        textLikeliness.Should().BeGreaterThan(0.15, "multi-pattern should be recognized as text-like");
        edgeDensity.Should().BeGreaterThan(0.02, "patterns have characteristic edge density");
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

        // Assert: All synthetic patterns should show some text-like characteristics
        textLikeliness.Should().BeGreaterThan(0.1,
            $"'{text}' pattern should show some text-like characteristics");
    }

    [Fact]
    public void TextOnlyImage_WithLowContrast_ShouldHaveLowerTextLikeliness()
    {
        // Arrange: Low contrast makes patterns harder to detect
        using var highContrast = CreateTextOnlyImage("ml", fontSize: 200,
            textColor: Color.Black, backgroundColor: Color.White);
        using var lowContrast = CreateTextOnlyImage("ml", fontSize: 200,
            textColor: Color.FromRgb(100, 100, 100), backgroundColor: Color.FromRgb(120, 120, 120));

        // Act
        var highContrastLikeliness = _textAnalyzer.CalculateTextLikeliness(highContrast);
        var lowContrastLikeliness = _textAnalyzer.CalculateTextLikeliness(lowContrast);

        // Assert: High contrast should be more easily detected
        highContrastLikeliness.Should().BeGreaterThan(lowContrastLikeliness,
            "high contrast patterns should be more easily detected");

        highContrastLikeliness.Should().BeGreaterThan(0.15, "high contrast pattern is detectable");
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

        // Use actual font rendering for realistic text images
        var fontFamily = SystemFonts.Families.FirstOrDefault(f =>
            f.Name.Contains("Arial", StringComparison.OrdinalIgnoreCase) ||
            f.Name.Contains("Helvetica", StringComparison.OrdinalIgnoreCase) ||
            f.Name.Contains("Sans", StringComparison.OrdinalIgnoreCase));

        // Fall back to first available font if preferred fonts not found
        if (fontFamily.Name == null)
        {
            fontFamily = SystemFonts.Families.FirstOrDefault();
        }

        // If no system fonts available, fall back to synthetic pattern
        if (fontFamily.Name == null)
        {
            image.Mutate(ctx =>
            {
                // Synthetic text-like pattern as fallback
                for (int i = 0; i < text.Length; i++)
                {
                    var x = 100 + i * fontSize;
                    var y = 200;
                    ctx.Fill(fgColor, new RectangleF(x, y, fontSize * 0.6f, fontSize));
                    ctx.Fill(fgColor, new RectangleF(x, y, fontSize * 0.6f, fontSize * 0.2f));
                    ctx.Fill(fgColor, new RectangleF(x, y + fontSize * 0.4f, fontSize * 0.6f, fontSize * 0.2f));
                }
            });
            return image;
        }

        var font = fontFamily.CreateFont(fontSize, FontStyle.Bold);
        var textOptions = new RichTextOptions(font)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Origin = new PointF(200, 200)
        };

        image.Mutate(ctx =>
        {
            ctx.DrawText(textOptions, text, fgColor);
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

            // Add small text overlay in corner using actual font
            var fontFamily = SystemFonts.Families.FirstOrDefault(f =>
                f.Name.Contains("Arial", StringComparison.OrdinalIgnoreCase) ||
                f.Name.Contains("Sans", StringComparison.OrdinalIgnoreCase));

            if (fontFamily.Name != null)
            {
                var font = fontFamily.CreateFont(20, FontStyle.Regular);
                ctx.DrawText("Text", font, Color.White, new PointF(320, 360));
            }
            else
            {
                // Fallback to rectangle
                ctx.Fill(Color.White, new RectangleF(320, 350, 60, 40));
            }
        });

        return image;
    }
}
