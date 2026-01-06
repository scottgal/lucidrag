using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;
using Mostlylucid.DocSummarizer.Images.Services.Ocr;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.Detection;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace Mostlylucid.DocSummarizer.Images.Tests.Services.Ocr.Detection;

/// <summary>
/// Tests for TextDetectionService - EAST/CRAFT ONNX text region detection.
/// </summary>
public class TextDetectionServiceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDir;
    private readonly Mock<ILogger<TextDetectionService>> _loggerMock;
    private readonly OcrConfig _config;

    public TextDetectionServiceTests(ITestOutputHelper output)
    {
        _output = output;
        _testDir = Path.Combine(Path.GetTempPath(), $"TextDetectionTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        _loggerMock = new Mock<ILogger<TextDetectionService>>();
        _config = new OcrConfig
        {
            EnableTextDetection = true,
            EmitPerformanceMetrics = true
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); }
            catch { /* ignore cleanup errors */ }
        }
    }

    [Fact]
    public async Task DetectTextRegionsAsync_WithTextDetectionDisabled_ReturnsTesseractPSM()
    {
        // Arrange
        var config = new OcrConfig { EnableTextDetection = false };
        var modelDownloader = CreateMockModelDownloader(eastPath: null, craftPath: null);
        var service = new TextDetectionService(modelDownloader, config, _loggerMock.Object);
        var imagePath = CreateTestImage("solid.png", 100, 100, Color.White);

        // Act
        var result = await service.DetectTextRegionsAsync(imagePath);

        // Assert
        result.Success.Should().BeTrue();
        result.DetectionMethod.Should().Be("TesseractPSM");
        result.BoundingBoxes.Should().BeEmpty("TesseractPSM returns empty list to signal full-image OCR");

        _output.WriteLine($"Detection method: {result.DetectionMethod}");
    }

    [Fact]
    public async Task DetectTextRegionsAsync_WithNoModelsAvailable_FallsBackToTesseractPSM()
    {
        // Arrange
        var modelDownloader = CreateMockModelDownloader(eastPath: null, craftPath: null);
        var service = new TextDetectionService(modelDownloader, _config, _loggerMock.Object);
        var imagePath = CreateTestImage("nomodels.png", 200, 200, Color.White);

        // Act
        var result = await service.DetectTextRegionsAsync(imagePath);

        // Assert
        result.Success.Should().BeTrue();
        result.DetectionMethod.Should().Be("TesseractPSM");
        result.ErrorMessage.Should().BeNull();

        _output.WriteLine($"Fallback detection method: {result.DetectionMethod}");
    }

    [Fact]
    public async Task DetectTextRegionsAsync_ReturnsValidResult_ForAnyImage()
    {
        // Arrange - Use mock that returns no models (fallback path)
        var modelDownloader = CreateMockModelDownloader(eastPath: null, craftPath: null);
        var service = new TextDetectionService(modelDownloader, _config, _loggerMock.Object);
        var imagePath = CreateTestImage("test.png", 300, 200, Color.LightGray);

        // Act
        var result = await service.DetectTextRegionsAsync(imagePath);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.DetectionMethod.Should().NotBeNullOrEmpty();
        result.BoundingBoxes.Should().NotBeNull();

        _output.WriteLine($"Result: Method={result.DetectionMethod}, Boxes={result.BoundingBoxes.Count}");
    }

    [Fact]
    public void ApplyNonMaximumSuppression_RemovesOverlappingBoxes()
    {
        // Arrange
        var modelDownloader = CreateMockModelDownloader(null, null);
        var service = new TextDetectionService(modelDownloader, _config, _loggerMock.Object);

        var boxes = new List<Mostlylucid.DocSummarizer.Images.Services.Ocr.BoundingBox>
        {
            new() { X1 = 0, Y1 = 0, X2 = 100, Y2 = 50, Width = 100, Height = 50, Confidence = 0.9f },
            new() { X1 = 10, Y1 = 5, X2 = 90, Y2 = 45, Width = 80, Height = 40, Confidence = 0.8f }, // Overlapping
            new() { X1 = 200, Y1 = 0, X2 = 300, Y2 = 50, Width = 100, Height = 50, Confidence = 0.85f } // Non-overlapping
        };

        // Act
        var result = service.ApplyNonMaximumSuppression(boxes, iouThreshold: 0.5);

        // Assert
        result.Should().HaveCount(2, "overlapping box should be removed");
        result.Should().Contain(b => b.X1 == 0, "first box should be kept (larger)");
        result.Should().Contain(b => b.X1 == 200, "non-overlapping box should be kept");

        _output.WriteLine($"NMS: {boxes.Count} boxes -> {result.Count} boxes");
    }

    [Fact]
    public void ApplyNonMaximumSuppression_KeepsAllNonOverlappingBoxes()
    {
        // Arrange
        var modelDownloader = CreateMockModelDownloader(null, null);
        var service = new TextDetectionService(modelDownloader, _config, _loggerMock.Object);

        var boxes = new List<Mostlylucid.DocSummarizer.Images.Services.Ocr.BoundingBox>
        {
            new() { X1 = 0, Y1 = 0, X2 = 50, Y2 = 50, Width = 50, Height = 50, Confidence = 0.9f },
            new() { X1 = 100, Y1 = 0, X2 = 150, Y2 = 50, Width = 50, Height = 50, Confidence = 0.85f },
            new() { X1 = 200, Y1 = 0, X2 = 250, Y2 = 50, Width = 50, Height = 50, Confidence = 0.8f },
            new() { X1 = 0, Y1 = 100, X2 = 50, Y2 = 150, Width = 50, Height = 50, Confidence = 0.75f }
        };

        // Act
        var result = service.ApplyNonMaximumSuppression(boxes, iouThreshold: 0.5);

        // Assert
        result.Should().HaveCount(4, "all non-overlapping boxes should be kept");

        _output.WriteLine($"All {result.Count} non-overlapping boxes kept");
    }

    [Fact]
    public void ApplyNonMaximumSuppression_WithEmptyList_ReturnsEmpty()
    {
        // Arrange
        var modelDownloader = CreateMockModelDownloader(null, null);
        var service = new TextDetectionService(modelDownloader, _config, _loggerMock.Object);

        // Act
        var result = service.ApplyNonMaximumSuppression(
            new List<Mostlylucid.DocSummarizer.Images.Services.Ocr.BoundingBox>(),
            iouThreshold: 0.5);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectTextRegionsAsync_HandlesInvalidPath_Gracefully()
    {
        // Arrange
        var modelDownloader = CreateMockModelDownloader(null, null);
        var service = new TextDetectionService(modelDownloader, _config, _loggerMock.Object);

        // Act
        var result = await service.DetectTextRegionsAsync("/nonexistent/path/image.png");

        // Assert - Should handle gracefully, not throw
        result.Should().NotBeNull();
        // Either fails gracefully or falls back
        if (!result.Success)
        {
            result.ErrorMessage.Should().NotBeNullOrEmpty();
            _output.WriteLine($"Handled invalid path: {result.ErrorMessage}");
        }
    }

    #region Helper Methods

    private ModelDownloader CreateMockModelDownloader(string? eastPath, string? craftPath)
    {
        // Create a real ModelDownloader that won't find models (no auto-download)
        var modelsDir = Path.Combine(_testDir, "models");
        Directory.CreateDirectory(modelsDir);

        // If paths provided, create dummy files
        if (eastPath != null)
        {
            var eastDir = Path.Combine(modelsDir, "east");
            Directory.CreateDirectory(eastDir);
            File.WriteAllText(Path.Combine(eastDir, "frozen_east_text_detection.pb"), "dummy");
        }

        if (craftPath != null)
        {
            var craftDir = Path.Combine(modelsDir, "craft");
            Directory.CreateDirectory(craftDir);
            File.WriteAllText(Path.Combine(craftDir, "craft_mlt_25k.onnx"), "dummy");
        }

        return new ModelDownloader(modelsDir, autoDownload: false);
    }

    private string CreateTestImage(string filename, int width, int height, Color color)
    {
        var path = Path.Combine(_testDir, filename);
        using var image = TestImageGenerator.CreateSolidColor(width, height, color);
        image.SaveAsPng(path);
        return path;
    }

    #endregion
}
