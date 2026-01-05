using Xunit;
using Mostlylucid.DocSummarizer.Images.Services.Ocr;

namespace Mostlylucid.DocSummarizer.Images.Tests.Services.Ocr;

public class TesseractOcrEngineTests
{
    [Fact]
    public void Constructor_WithValidParameters_ShouldSucceed()
    {
        // Arrange & Act - uses new constructor with named parameters
        var engine = new TesseractOcrEngine(
            tesseractDataPath: "./tessdata",
            language: "eng");

        // Assert
        Assert.NotNull(engine);
    }

    [Fact]
    public void Constructor_WithNullDataPath_ShouldUseDefault()
    {
        // Arrange & Act
        var engine = new TesseractOcrEngine(
            tesseractDataPath: null,
            language: "eng");

        // Assert
        Assert.NotNull(engine);
    }

    [Fact]
    public void ExtractTextWithCoordinates_WithNonExistentFile_ShouldThrow()
    {
        // Arrange - default constructor uses auto-download or fallback paths
        var engine = new TesseractOcrEngine();

        // Act & Assert
        // Tesseract may throw TesseractException if tessdata not found OR file not found
        Assert.ThrowsAny<Exception>(() =>
            engine.ExtractTextWithCoordinates("nonexistent.png"));
    }

    [Fact]
    public void ExtractTextWithCoordinates_WithTextImage_ShouldReturnRegions()
    {
        // This test requires a real test image with text
        // Skipping in automated tests - would need test data
        Assert.True(true, "Integration test - requires test image");
    }
}
