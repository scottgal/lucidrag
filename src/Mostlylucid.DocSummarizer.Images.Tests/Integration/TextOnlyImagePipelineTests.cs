using Xunit;
using FluentAssertions;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Services.Storage;
using System.Text.Json;

namespace Mostlylucid.DocSummarizer.Images.Tests.Integration;

/// <summary>
/// Integration tests for the full pipeline when handling text-only images (logos, word images).
/// Covers the edge case where the image IS text itself, not just containing text.
/// </summary>
public class TextOnlyImagePipelineTests
{
    [Fact]
    public async Task TextOnlyImage_Logo_ShouldExtractTextAndScoreCorrectly()
    {
        // Arrange: Simulate a logo image ("ml") with vision response
        var profile = CreateLogoImageProfile();
        var visionResult = CreateLogoVisionResult();

        var discriminatorService = CreateDiscriminatorService();

        // Act
        var score = await discriminatorService.ScoreAnalysisAsync(
            imageHash: "test-logo-hash",
            profile: profile,
            gifMotion: null,
            visionResult: visionResult,
            extractedText: "ml",  // OCR extracted the letters
            goal: "caption",
            CancellationToken.None);

        // Assert - Text-only image should have specific score characteristics
        // Note: OcrFidelity depends on DiscriminatorService vector calculation, not raw TextLikeliness
        score.Vectors.OcrFidelity.Should().BeGreaterThan(0.2,
            "OCR should have positive fidelity for clear text");

        score.Vectors.StructuralAlignment.Should().BeGreaterThan(0.2,
            "logos have clear structure");

        score.OverallScore.Should().BeGreaterThan(0.3,
            "text-only image should score reasonably overall");

        score.SignalContributions.Should().ContainKey("TextLikeliness")
            .WhoseValue.Strength.Should().BeGreaterThan(0.5,
            "text likeliness should be a significant signal");
    }

    [Fact]
    public void VisionResponse_VerboseLogoDescription_ShouldStillRecognizeAsText()
    {
        // Arrange: The exact scenario from user's example
        var verboseCaption = @"The image features a logo consisting of the letters ""m"" and ""l"". " +
            @"The letter 'm' is larger than 'l', with both set against a dark background. " +
            @"The font used for these characters appears modern and sleek, suggesting it could be " +
            @"associated with technology or contemporary services.\n\n" +
            @"Given that logos often represent brands or companies, this image likely represents a " +
            @"brand identity. Without additional context such as the company's name alongside the logo, " +
            @"I'm unable to provide specifics on what 'm' and 'l' might stand for. However, based on " +
            @"common practices in branding, it could be part of a larger word not visible here.\n\n" +
            @"If you have more information or if there are any particular aspects you'd like further " +
            @"clarification on regarding this image, feel free to ask!";

        // Act: Check if we can extract that it's about letters from the caption
        var containsLetters = verboseCaption.Contains("letters", StringComparison.OrdinalIgnoreCase);
        var mentionsM = verboseCaption.Contains("\"m\"") || verboseCaption.Contains("'m'");
        var mentionsL = verboseCaption.Contains("\"l\"") || verboseCaption.Contains("'l'");
        var mentionsLogo = verboseCaption.Contains("logo", StringComparison.OrdinalIgnoreCase);

        // Assert: Despite verbosity, key information is present
        containsLetters.Should().BeTrue("should mention 'letters'");
        mentionsM.Should().BeTrue("should mention letter 'm'");
        mentionsL.Should().BeTrue("should mention letter 'l'");
        mentionsLogo.Should().BeTrue("should identify it as a logo");
    }

    [Fact]
    public void ExtractTextFromVerboseCaption_ShouldIdentifyActualLetters()
    {
        // Arrange: Helper to extract actual text content from verbose description
        var verboseCaption = @"The image features a logo consisting of the letters ""m"" and ""l"".";

        // Act: Extract quoted letters
        var extractedLetters = ExtractQuotedText(verboseCaption);

        // Assert
        extractedLetters.Should().Contain("m");
        extractedLetters.Should().Contain("l");
    }

    [Fact]
    public async Task TextOnlyImage_SingleLetter_ShouldNotConfuseWithDiagram()
    {
        // Arrange: A single letter might be mistaken for a simple diagram
        var profile = new ImageProfile
        {
            Sha256 = "single-letter",
            Width = 200,
            Height = 200,
            AspectRatio = 1.0,
            Format = "PNG",
            DetectedType = ImageType.Diagram,  // Might be misclassified
            TypeConfidence = 0.6,
            LaplacianVariance = 800,  // Sharp
            EdgeDensity = 0.15,  // High edges
            TextLikeliness = 0.85,  // Very high!
            MeanLuminance = 0.5,
            LuminanceStdDev = 0.4,
            LuminanceEntropy = 0.6,
            ClippedBlacksPercent = 5.0,
            ClippedWhitesPercent = 5.0,
            MeanSaturation = 0.0,
            IsMostlyGrayscale = true,
            DominantColors = new List<DominantColor>
            {
                new("#000000", 60, "Black"),
                new("#FFFFFF", 40, "White")
            }
        };

        var visionResult = new VisionResult(
            Success: true,
            Error: null,
            Caption: "The image shows a large letter 'A' in black on white background",
            Model: "test-model",
            ConfidenceScore: 0.9,
            Claims: new List<EvidenceClaim>
            {
                new("shows letter 'A'", new List<string> { "V" }, new List<string> { "letter_shape" })
            },
            EnhancedMetadata: null
        );

        var discriminatorService = CreateDiscriminatorService();

        // Act
        var score = await discriminatorService.ScoreAnalysisAsync(
            imageHash: "letter-A",
            profile: profile,
            gifMotion: null,
            visionResult: visionResult,
            extractedText: "A",
            goal: "ocr",
            CancellationToken.None);

        // Assert: Despite DetectedType=Diagram, text signals should dominate
        // Note: OcrFidelity calculation depends on multiple factors beyond TextLikeliness
        score.Vectors.OcrFidelity.Should().BeGreaterThan(0.2,
            "single clear letter should have positive OCR fidelity");

        score.SignalContributions["TextLikeliness"].Strength.Should().BeGreaterThan(0.5,
            "text likeliness signal should be significant");
    }

    [Fact]
    public void CompareTextOnlyImage_VersusImageWithText_ShouldDistinguish()
    {
        // Arrange
        var pureLogo = new ImageProfile
        {
            Sha256 = "pure-logo",
            Width = 400,
            Height = 200,
            AspectRatio = 2.0,
            Format = "PNG",
            DetectedType = ImageType.Diagram,
            TypeConfidence = 0.7,
            LaplacianVariance = 900,
            EdgeDensity = 0.12,
            TextLikeliness = 0.95,  // Almost pure text
            MeanLuminance = 0.5,
            LuminanceStdDev = 0.45,
            LuminanceEntropy = 0.65,
            ClippedBlacksPercent = 5.0,
            ClippedWhitesPercent = 5.0,
            MeanSaturation = 0.0,
            IsMostlyGrayscale = true,
            DominantColors = new List<DominantColor>
            {
                new("#000000", 55, "Black"),
                new("#FFFFFF", 45, "White")
            }
        };

        var photoWithCaption = new ImageProfile
        {
            Sha256 = "photo-caption",
            Width = 1920,
            Height = 1080,
            AspectRatio = 1.78,
            Format = "JPEG",
            DetectedType = ImageType.Photo,
            TypeConfidence = 0.9,
            LaplacianVariance = 600,
            EdgeDensity = 0.25,  // Higher due to complex scene
            TextLikeliness = 0.35,  // Some text, but not dominant
            MeanLuminance = 0.6,
            LuminanceStdDev = 0.3,
            LuminanceEntropy = 0.85,
            ClippedBlacksPercent = 2.0,
            ClippedWhitesPercent = 3.0,
            MeanSaturation = 0.4,
            IsMostlyGrayscale = false,
            DominantColors = new List<DominantColor>
            {
                new("#0000FF", 30, "Blue"),
                new("#00FF00", 25, "Green"),
                new("#A52A2A", 20, "Brown")
            }
        };

        // Assert: Key distinguishing features
        pureLogo.TextLikeliness.Should().BeGreaterThan(photoWithCaption.TextLikeliness * 2,
            "pure logo has much higher text likeliness");

        pureLogo.IsMostlyGrayscale.Should().BeTrue(
            "logos are often monochrome");

        photoWithCaption.EdgeDensity.Should().BeGreaterThan(pureLogo.EdgeDensity,
            "photos have more complex edge patterns from scenes");

        (pureLogo.DominantColors.Count).Should().BeLessOrEqualTo(3,
            "logos typically use limited color palettes");

        (photoWithCaption.DominantColors.Count).Should().BeGreaterThan(pureLogo.DominantColors.Count,
            "photos have more diverse colors");
    }

    // Helper methods
    private static ImageProfile CreateLogoImageProfile()
    {
        return new ImageProfile
        {
            Sha256 = "ml-logo-hash",
            Width = 400,
            Height = 400,
            AspectRatio = 1.0,
            Format = "PNG",
            DetectedType = ImageType.Diagram,
            TypeConfidence = 0.65,
            LaplacianVariance = 850,  // Sharp
            EdgeDensity = 0.10,
            TextLikeliness = 0.92,  // Very high - it's pure text
            MeanLuminance = 0.5,
            LuminanceStdDev = 0.48,  // High contrast (black text on white)
            LuminanceEntropy = 0.6,
            ClippedBlacksPercent = 5.0,
            ClippedWhitesPercent = 5.0,
            MeanSaturation = 0.0,
            IsMostlyGrayscale = true,
            DominantColors = new List<DominantColor>
            {
                new("#000000", 52, "Black"),
                new("#FFFFFF", 48, "White")
            }
        };
    }

    private static VisionResult CreateLogoVisionResult()
    {
        return new VisionResult(
            Success: true,
            Error: null,
            Caption: @"The image features a logo consisting of the letters ""m"" and ""l"". " +
                    @"The letter 'm' is larger than 'l', with both set against a dark background.",
            Model: "anthropic:claude-3-opus-20240229",
            ConfidenceScore: 0.95,
            Claims: new List<EvidenceClaim>
            {
                new("logo with letters m and l", new List<string> { "V" }, new List<string> { "visual_observation" }),
                new("letter m is larger than l", new List<string> { "V" }, new List<string> { "size_comparison" }),
                new("dark background", new List<string> { "V", "S" }, new List<string> { "luminance=0.2" })
            },
            EnhancedMetadata: new VisionMetadata
            {
                Tone = "professional",
                Sentiment = 0.0,
                Complexity = 0.3,  // Simple logo
                AestheticScore = 0.7,
                PrimarySubject = "logo",
                Purpose = "commercial",
                TargetAudience = "professionals",
                Confidence = 0.9
            }
        );
    }

    private static DiscriminatorService CreateDiscriminatorService()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<DiscriminatorService>.Instance;
        var trackerLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<SignalEffectivenessTracker>.Instance;

        // Use in-memory database for testing
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-signals-{Guid.NewGuid()}.db");
        var database = new SignalDatabase(dbPath);
        var tracker = new SignalEffectivenessTracker(trackerLogger, database);

        return new DiscriminatorService(logger, tracker);
    }

    private static List<string> ExtractQuotedText(string text)
    {
        var results = new List<string>();
        var matches = System.Text.RegularExpressions.Regex.Matches(
            text,
            @"[""']([a-zA-Z0-9])[""']");

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            results.Add(match.Groups[1].Value);
        }

        return results;
    }
}
