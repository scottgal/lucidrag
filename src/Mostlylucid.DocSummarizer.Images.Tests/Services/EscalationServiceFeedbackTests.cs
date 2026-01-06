using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Services;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.DocSummarizer.Images.Services.Storage;
using Mostlylucid.DocSummarizer.Images.Services.Vision;
using SixLabors.ImageSharp;
using Xunit;
using Xunit.Abstractions;

namespace Mostlylucid.DocSummarizer.Images.Tests.Services;

/// <summary>
/// Tests for EscalationService feedback storage functionality.
/// </summary>
public class EscalationServiceFeedbackTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDir;
    private readonly Mock<IImageAnalyzer> _imageAnalyzerMock;
    private readonly Mock<ISignalDatabase> _signalDbMock;
    private readonly Mock<ILogger<EscalationService>> _loggerMock;

    public EscalationServiceFeedbackTests(ITestOutputHelper output)
    {
        _output = output;
        _testDir = Path.Combine(Path.GetTempPath(), $"EscalationServiceTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        _imageAnalyzerMock = new Mock<IImageAnalyzer>();
        _loggerMock = new Mock<ILogger<EscalationService>>();
        _signalDbMock = new Mock<ISignalDatabase>();
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
    public async Task StoreFeedbackAsync_WithSignalDatabase_StoresFeedback()
    {
        // Arrange
        var visionServiceMock = CreateMockVisionLlmService();
        var service = new EscalationService(
            _imageAnalyzerMock.Object,
            visionServiceMock,
            _loggerMock.Object,
            _signalDbMock.Object);

        var imagePath = CreateTestImage("feedback.png", 200, 200);
        var profile = CreateTestProfile();

        _signalDbMock
            .Setup(db => db.StoreFeedbackAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<double?>(),
                It.IsAny<string?>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await service.StoreFeedbackAsync(
            imagePath,
            profile,
            llmCaption: "Original caption",
            wasCorrect: true);

        // Assert
        _signalDbMock.Verify(db => db.StoreFeedbackAsync(
            It.IsAny<string>(),
            "caption_correct",
            "Original caption",
            null,
            0.1,
            It.IsAny<string?>(),
            It.IsAny<long?>(),
            It.IsAny<CancellationToken>()),
            Times.Once);

        _output.WriteLine("Correct feedback stored to SignalDatabase");
    }

    [Fact]
    public async Task StoreFeedbackAsync_WithCorrection_StoresNegativeAdjustment()
    {
        // Arrange
        var visionServiceMock = CreateMockVisionLlmService();
        var service = new EscalationService(
            _imageAnalyzerMock.Object,
            visionServiceMock,
            _loggerMock.Object,
            _signalDbMock.Object);

        var imagePath = CreateTestImage("correction.png", 200, 200);
        var profile = CreateTestProfile();

        _signalDbMock
            .Setup(db => db.StoreFeedbackAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<double?>(),
                It.IsAny<string?>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await service.StoreFeedbackAsync(
            imagePath,
            profile,
            llmCaption: "Wrong caption",
            wasCorrect: false,
            userCorrection: "Corrected caption");

        // Assert
        _signalDbMock.Verify(db => db.StoreFeedbackAsync(
            It.IsAny<string>(),
            "caption_incorrect",
            "Wrong caption",
            "Corrected caption",
            -0.1,
            It.IsAny<string?>(),
            It.IsAny<long?>(),
            It.IsAny<CancellationToken>()),
            Times.Once);

        _output.WriteLine("Incorrect feedback with correction stored");
    }

    [Fact]
    public async Task StoreFeedbackAsync_WithoutSignalDatabase_LogsOnly()
    {
        // Arrange - No SignalDatabase
        var visionServiceMock = CreateMockVisionLlmService();
        var service = new EscalationService(
            _imageAnalyzerMock.Object,
            visionServiceMock,
            _loggerMock.Object,
            signalDatabase: null);

        var imagePath = CreateTestImage("nodb.png", 200, 200);
        var profile = CreateTestProfile();

        // Act
        await service.StoreFeedbackAsync(
            imagePath,
            profile,
            llmCaption: "Test caption",
            wasCorrect: true);

        // Assert - Should complete without error
        _signalDbMock.Verify(db => db.StoreFeedbackAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<double?>(),
            It.IsAny<string?>(),
            It.IsAny<long?>(),
            It.IsAny<CancellationToken>()),
            Times.Never);

        _output.WriteLine("Feedback logged without database");
    }

    [Fact]
    public async Task StoreFeedbackAsync_HandlesDbError_Gracefully()
    {
        // Arrange
        var visionServiceMock = CreateMockVisionLlmService();
        var service = new EscalationService(
            _imageAnalyzerMock.Object,
            visionServiceMock,
            _loggerMock.Object,
            _signalDbMock.Object);

        var imagePath = CreateTestImage("dberror.png", 200, 200);
        var profile = CreateTestProfile();

        _signalDbMock
            .Setup(db => db.StoreFeedbackAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<double?>(),
                It.IsAny<string?>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act - Should not throw
        var act = async () => await service.StoreFeedbackAsync(
            imagePath,
            profile,
            llmCaption: "Test",
            wasCorrect: true);

        // Assert
        await act.Should().NotThrowAsync("should handle database errors gracefully");

        _output.WriteLine("Database error handled gracefully");
    }

    [Fact]
    public async Task StoreFeedbackAsync_ComputesSha256_ForImage()
    {
        // Arrange
        string? capturedSha256 = null;

        var visionServiceMock = CreateMockVisionLlmService();
        var service = new EscalationService(
            _imageAnalyzerMock.Object,
            visionServiceMock,
            _loggerMock.Object,
            _signalDbMock.Object);

        var imagePath = CreateTestImage("sha256.png", 100, 100);
        var profile = CreateTestProfile();

        _signalDbMock
            .Setup(db => db.StoreFeedbackAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<double?>(),
                It.IsAny<string?>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, string?, double?, string?, long?, CancellationToken>(
                (sha256, _, _, _, _, _, _, _) => capturedSha256 = sha256)
            .Returns(Task.CompletedTask);

        // Act
        await service.StoreFeedbackAsync(imagePath, profile, "test", true);

        // Assert
        capturedSha256.Should().NotBeNullOrEmpty();
        capturedSha256.Should().HaveLength(64, "SHA256 hash should be 64 hex characters");
        capturedSha256.Should().MatchRegex("^[A-F0-9]+$", "should be valid hex");

        _output.WriteLine($"Computed SHA256: {capturedSha256}");
    }

    #region Helper Methods

    private VisionLlmService CreateMockVisionLlmService()
    {
        var configData = new Dictionary<string, string?>
        {
            ["Ollama:BaseUrl"] = "http://localhost:11434",
            ["Ollama:VisionModel"] = "test-model"
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        return new VisionLlmService(config, null);
    }

    private ImageProfile CreateTestProfile()
    {
        return new ImageProfile
        {
            Sha256 = "ABCD1234",
            Format = "PNG",
            Width = 200,
            Height = 200,
            AspectRatio = 1.0,
            EdgeDensity = 0.1,
            LuminanceEntropy = 5.0,
            MeanLuminance = 128,
            LuminanceStdDev = 30,
            ClippedBlacksPercent = 0.01,
            ClippedWhitesPercent = 0.01,
            DominantColors = new List<DominantColor>(),
            MeanSaturation = 0.5,
            IsMostlyGrayscale = false,
            LaplacianVariance = 100,
            TextLikeliness = 0.3,
            DetectedType = ImageType.Photo
        };
    }

    private string CreateTestImage(string filename, int width, int height)
    {
        var path = Path.Combine(_testDir, filename);
        using var image = TestImageGenerator.CreateSolidColor(width, height, Color.White);
        image.SaveAsPng(path);
        return path;
    }

    #endregion
}
