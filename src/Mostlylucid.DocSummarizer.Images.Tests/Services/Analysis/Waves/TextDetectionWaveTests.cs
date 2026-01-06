using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.Detection;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;
using BoundingBox = Mostlylucid.DocSummarizer.Images.Services.Ocr.BoundingBox;

namespace Mostlylucid.DocSummarizer.Images.Tests.Services.Analysis.Waves;

/// <summary>
/// Tests for TextDetectionWave - Signal emission from EAST/CRAFT detection.
/// </summary>
public class TextDetectionWaveTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDir;
    private readonly Mock<ILogger<TextDetectionWave>> _loggerMock;

    public TextDetectionWaveTests(ITestOutputHelper output)
    {
        _output = output;
        _testDir = Path.Combine(Path.GetTempPath(), $"TextDetectionWaveTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _loggerMock = new Mock<ILogger<TextDetectionWave>>();
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
    public void TextDetectionWave_HasCorrectPriority()
    {
        // Arrange
        var wave = new TextDetectionWave();

        // Assert
        wave.Priority.Should().Be(82, "should run after routing (98) but before OCR (80)");
        wave.Name.Should().Be("TextDetectionWave");
        wave.Tags.Should().Contain("text").And.Contain("detection");
    }

    [Fact]
    public void ShouldRun_ReturnsFalse_WhenDetectionServiceIsNull()
    {
        // Arrange
        var wave = new TextDetectionWave(detectionService: null);
        var context = new AnalysisContext();

        // Act
        var shouldRun = wave.ShouldRun("test.png", context);

        // Assert
        shouldRun.Should().BeFalse("no detection service available");
    }

    [Fact]
    public void ShouldRun_ReturnsFalse_OnFastRouteWithLowTextLikeliness()
    {
        // Arrange
        var mockService = CreateMockDetectionService();
        var config = Options.Create(new ImageConfig { Ocr = { EnableTextDetection = true } });
        var wave = new TextDetectionWave(mockService, config, _loggerMock.Object);

        var context = new AnalysisContext();
        context.AddSignal(new Signal { Key = "route.selected", Value = "fast", Confidence = 1.0, Source = "test" });
        context.AddSignal(new Signal { Key = "content.text_likeliness", Value = 0.1, Confidence = 1.0, Source = "test" });

        // Act
        var shouldRun = wave.ShouldRun("test.png", context);

        // Assert
        shouldRun.Should().BeFalse("fast route with low text likeliness should skip");

        _output.WriteLine("Fast route + low text likeliness = skipped");
    }

    [Fact]
    public void ShouldRun_ReturnsTrue_OnFastRouteWithHighTextLikeliness()
    {
        // Arrange
        var mockService = CreateMockDetectionService();
        var config = Options.Create(new ImageConfig { Ocr = { EnableTextDetection = true } });
        var wave = new TextDetectionWave(mockService, config, _loggerMock.Object);

        var context = new AnalysisContext();
        context.AddSignal(new Signal { Key = "route.selected", Value = "fast", Confidence = 1.0, Source = "test" });
        context.AddSignal(new Signal { Key = "content.text_likeliness", Value = 0.5, Confidence = 1.0, Source = "test" });

        // Act
        var shouldRun = wave.ShouldRun("test.png", context);

        // Assert
        shouldRun.Should().BeTrue("high text likeliness should override fast route skip");
    }

    [Fact]
    public void ShouldRun_ReturnsTrue_OnBalancedOrQualityRoute()
    {
        // Arrange
        var mockService = CreateMockDetectionService();
        var config = Options.Create(new ImageConfig { Ocr = { EnableTextDetection = true } });
        var wave = new TextDetectionWave(mockService, config, _loggerMock.Object);

        var context = new AnalysisContext();
        context.AddSignal(new Signal { Key = "route.selected", Value = "balanced", Confidence = 1.0, Source = "test" });

        // Act
        var shouldRun = wave.ShouldRun("test.png", context);

        // Assert
        shouldRun.Should().BeTrue("balanced/quality routes should run detection");
    }

    [Fact]
    public async Task AnalyzeAsync_EmitsMethodSignal()
    {
        // Arrange
        var mockService = CreateMockDetectionService(detectionMethod: "TesseractPSM", boxCount: 0);
        var config = Options.Create(new ImageConfig());
        var wave = new TextDetectionWave(mockService, config, _loggerMock.Object);
        var imagePath = CreateTestImage("method.png", 200, 200);
        var context = CreateContextWithIdentity(200, 200);

        // Act
        var signals = (await wave.AnalyzeAsync(imagePath, context)).ToList();

        // Assert
        signals.Should().Contain(s => s.Key == "text_detection.method");
        var methodSignal = signals.First(s => s.Key == "text_detection.method");
        methodSignal.Value.Should().Be("TesseractPSM");
        methodSignal.Confidence.Should().Be(1.0);

        _output.WriteLine($"Method signal: {methodSignal.Value}");
    }

    [Fact]
    public async Task AnalyzeAsync_EmitsRegionCountSignal()
    {
        // Arrange - Using fallback TesseractPSM which returns 0 regions (full image OCR mode)
        var mockService = CreateMockDetectionService(detectionMethod: "TesseractPSM", boxCount: 0);
        var config = Options.Create(new ImageConfig());
        var wave = new TextDetectionWave(mockService, config, _loggerMock.Object);
        var imagePath = CreateTestImage("regions.png", 400, 300);
        var context = CreateContextWithIdentity(400, 300);

        // Act
        var signals = (await wave.AnalyzeAsync(imagePath, context)).ToList();

        // Assert
        signals.Should().Contain(s => s.Key == "text_detection.region_count");
        var countSignal = signals.First(s => s.Key == "text_detection.region_count");
        // TesseractPSM returns empty list (signals full-image OCR mode)
        countSignal.Value.Should().Be(0);

        _output.WriteLine($"Region count: {countSignal.Value} (TesseractPSM mode)");
    }

    [Fact]
    public async Task AnalyzeAsync_EmitsHasTextSignal_WhenRegionsDetected()
    {
        // Arrange - with regions via mock
        var mockInterface = new Mock<ITextDetectionService>();
        mockInterface
            .Setup(s => s.DetectTextRegionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TextDetectionResult
            {
                Success = true,
                DetectionMethod = "CRAFT",
                BoundingBoxes = new List<BoundingBox>
                {
                    new() { X1 = 10, Y1 = 10, X2 = 100, Y2 = 30, Width = 90, Height = 20, Confidence = 0.9f },
                    new() { X1 = 10, Y1 = 50, X2 = 100, Y2 = 70, Width = 90, Height = 20, Confidence = 0.85f }
                }
            });

        var config = Options.Create(new ImageConfig());
        var wave = new TextDetectionWave(mockInterface.Object, config, _loggerMock.Object);
        var imagePath = CreateTestImage("hastext.png", 300, 200);
        var context = CreateContextWithIdentity(300, 200);

        // Act
        var signals = (await wave.AnalyzeAsync(imagePath, context)).ToList();

        // Assert
        signals.Should().Contain(s => s.Key == "text_detection.has_text");
        var hasTextSignal = signals.First(s => s.Key == "text_detection.has_text");
        hasTextSignal.Value.Should().Be(true);

        _output.WriteLine($"Has text: {hasTextSignal.Value}");
    }

    [Fact]
    public async Task AnalyzeAsync_EmitsFalseHasText_WhenNoRegions()
    {
        // Arrange - no regions
        var mockService = CreateMockDetectionService(detectionMethod: "TesseractPSM", boxCount: 0);
        var config = Options.Create(new ImageConfig());
        var wave = new TextDetectionWave(mockService, config, _loggerMock.Object);
        var imagePath = CreateTestImage("notext.png", 200, 200);
        var context = CreateContextWithIdentity(200, 200);

        // Act
        var signals = (await wave.AnalyzeAsync(imagePath, context)).ToList();

        // Assert
        var hasTextSignal = signals.First(s => s.Key == "text_detection.has_text");
        hasTextSignal.Value.Should().Be(false);

        _output.WriteLine($"Has text (empty): {hasTextSignal.Value}");
    }

    [Fact]
    public async Task AnalyzeAsync_EmitsCoverageSignal_WhenRegionsDetected()
    {
        // Arrange - boxes covering some of image via mock
        var mockInterface = new Mock<ITextDetectionService>();
        mockInterface
            .Setup(s => s.DetectTextRegionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TextDetectionResult
            {
                Success = true,
                DetectionMethod = "EAST",
                BoundingBoxes = new List<BoundingBox>
                {
                    new() { X1 = 10, Y1 = 10, X2 = 60, Y2 = 30, Width = 50, Height = 20, Confidence = 0.9f },
                    new() { X1 = 110, Y1 = 10, X2 = 160, Y2 = 30, Width = 50, Height = 20, Confidence = 0.9f }
                }
            });

        var config = Options.Create(new ImageConfig());
        var wave = new TextDetectionWave(mockInterface.Object, config, _loggerMock.Object);
        var imagePath = CreateTestImage("coverage.png", 400, 200);
        var context = CreateContextWithIdentity(400, 200);

        // Act
        var signals = (await wave.AnalyzeAsync(imagePath, context)).ToList();

        // Assert
        signals.Should().Contain(s => s.Key == "text_detection.coverage");
        var coverageSignal = signals.First(s => s.Key == "text_detection.coverage");
        var coverage = Convert.ToDouble(coverageSignal.Value);
        coverage.Should().BeGreaterThan(0).And.BeLessThan(1);

        _output.WriteLine($"Coverage: {coverage:P2}");
    }

    [Fact]
    public async Task AnalyzeAsync_EmitsRegionSignals_WhenRegionsDetected()
    {
        // Arrange - Use mock interface to inject regions
        var mockInterface = new Mock<ITextDetectionService>();
        mockInterface
            .Setup(s => s.DetectTextRegionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TextDetectionResult
            {
                Success = true,
                DetectionMethod = "CRAFT",
                BoundingBoxes = new List<BoundingBox>
                {
                    new() { X1 = 10, Y1 = 10, X2 = 100, Y2 = 30, Width = 90, Height = 20, Confidence = 0.95f },
                    new() { X1 = 10, Y1 = 50, X2 = 100, Y2 = 70, Width = 90, Height = 20, Confidence = 0.90f },
                    new() { X1 = 10, Y1 = 90, X2 = 100, Y2 = 110, Width = 90, Height = 20, Confidence = 0.85f }
                }
            });

        var config = Options.Create(new ImageConfig());
        var wave = new TextDetectionWave(mockInterface.Object, config, _loggerMock.Object);
        var imagePath = CreateTestImage("individual.png", 500, 300);
        var context = CreateContextWithIdentity(500, 300);

        // Act
        var signals = (await wave.AnalyzeAsync(imagePath, context)).ToList();

        // Assert
        signals.Should().Contain(s => s.Key == "text_detection.region.0");
        signals.Should().Contain(s => s.Key == "text_detection.region.1");
        signals.Should().Contain(s => s.Key == "text_detection.region.2");
        signals.Count(s => s.Key.StartsWith("text_detection.region.")).Should().Be(3);

        foreach (var regionSignal in signals.Where(s => s.Key.StartsWith("text_detection.region.")))
        {
            regionSignal.Tags.Should().Contain("region");
            regionSignal.Metadata.Should().ContainKey("index");
            _output.WriteLine($"Signal: {regionSignal.Key}, Index: {regionSignal.Metadata?["index"]}");
        }
    }

    [Fact]
    public async Task AnalyzeAsync_EmitsRegionsCollectionSignal_WhenRegionsDetected()
    {
        // Arrange - Use mock interface
        var mockInterface = new Mock<ITextDetectionService>();
        mockInterface
            .Setup(s => s.DetectTextRegionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TextDetectionResult
            {
                Success = true,
                DetectionMethod = "EAST",
                BoundingBoxes = new List<BoundingBox>
                {
                    new() { X1 = 10, Y1 = 10, X2 = 110, Y2 = 30, Width = 100, Height = 20, Confidence = 0.92f },
                    new() { X1 = 10, Y1 = 50, X2 = 110, Y2 = 70, Width = 100, Height = 20, Confidence = 0.88f }
                }
            });

        var config = Options.Create(new ImageConfig());
        var wave = new TextDetectionWave(mockInterface.Object, config, _loggerMock.Object);
        var imagePath = CreateTestImage("collection.png", 400, 200);
        var context = CreateContextWithIdentity(400, 200);

        // Act
        var signals = (await wave.AnalyzeAsync(imagePath, context)).ToList();

        // Assert
        signals.Should().Contain(s => s.Key == "text_detection.regions");
        var regionsSignal = signals.First(s => s.Key == "text_detection.regions");
        regionsSignal.Tags.Should().Contain("collection");
        regionsSignal.Metadata.Should().ContainKey("count");
        regionsSignal.Metadata?["count"].Should().Be(2);

        _output.WriteLine($"Regions collection: count={regionsSignal.Metadata?["count"]}");
    }

    [Fact]
    public async Task AnalyzeAsync_EmitsUnavailableStatus_WhenNoService()
    {
        // Arrange
        var wave = new TextDetectionWave(detectionService: null);
        var imagePath = CreateTestImage("noservice.png", 100, 100);
        var context = new AnalysisContext();

        // Act
        var signals = (await wave.AnalyzeAsync(imagePath, context)).ToList();

        // Assert
        signals.Should().ContainSingle(s => s.Key == "text_detection.status");
        var statusSignal = signals.First(s => s.Key == "text_detection.status");
        statusSignal.Value.Should().Be("unavailable");

        _output.WriteLine($"Status: {statusSignal.Value}");
    }

    #region Helper Methods

    private TextDetectionService CreateMockDetectionService(
        string detectionMethod = "TesseractPSM",
        int boxCount = 0,
        Func<int, BoundingBox>? boxGenerator = null)
    {
        // Create a real service with mock model downloader that returns fallback
        var modelsDir = Path.Combine(_testDir, "models");
        Directory.CreateDirectory(modelsDir);
        var modelDownloader = new ModelDownloader(modelsDir, autoDownload: false);

        var config = new OcrConfig { EnableTextDetection = true };

        // For this mock, we'll create a wrapper or use the fallback path
        // Since we can't easily mock the service, we'll test the fallback behavior
        return new TextDetectionService(modelDownloader, config);
    }

    private AnalysisContext CreateContextWithIdentity(int width, int height)
    {
        var context = new AnalysisContext();
        context.AddSignal(new Signal { Key = "identity.width", Value = width, Confidence = 1.0, Source = "test" });
        context.AddSignal(new Signal { Key = "identity.height", Value = height, Confidence = 1.0, Source = "test" });
        return context;
    }

    private string CreateTestImage(string filename, int width, int height)
    {
        var path = Path.Combine(_testDir, filename);
        using var image = TestImageGenerator.CreateTextLikeImage(width, height);
        image.SaveAsPng(path);
        return path;
    }

    #endregion
}
