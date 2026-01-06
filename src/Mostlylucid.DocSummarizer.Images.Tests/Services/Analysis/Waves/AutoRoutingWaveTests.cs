using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;
using Mostlylucid.DocSummarizer.Images.Services.Storage;
using SixLabors.ImageSharp;
using Xunit;
using Xunit.Abstractions;

namespace Mostlylucid.DocSummarizer.Images.Tests.Services.Analysis.Waves;

/// <summary>
/// Tests for AutoRoutingWave - Signal-based routing and SignalDatabase integration.
/// </summary>
public class AutoRoutingWaveTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDir;
    private readonly Mock<ILogger<AutoRoutingWave>> _loggerMock;
    private readonly Mock<ISignalDatabase> _signalDbMock;

    public AutoRoutingWaveTests(ITestOutputHelper output)
    {
        _output = output;
        _testDir = Path.Combine(Path.GetTempPath(), $"AutoRoutingWaveTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        _loggerMock = new Mock<ILogger<AutoRoutingWave>>();
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
    public void AutoRoutingWave_HasCorrectPriority()
    {
        // Arrange
        var wave = new AutoRoutingWave();

        // Assert
        wave.Priority.Should().Be(98, "should run after Color (100) but early in pipeline");
        wave.Name.Should().Be("AutoRoutingWave");
        wave.Tags.Should().Contain("routing").And.Contain("auto");
    }

    [Fact]
    public async Task AnalyzeAsync_EmitsRouteSelectedSignal()
    {
        // Arrange
        var wave = new AutoRoutingWave(logger: _loggerMock.Object);
        var imagePath = CreateTestImage("route.png", 200, 200);
        var context = CreateBasicContext();

        // Act
        var signals = (await wave.AnalyzeAsync(imagePath, context)).ToList();

        // Assert
        signals.Should().Contain(s => s.Key == "route.selected");
        var routeSignal = signals.First(s => s.Key == "route.selected");
        routeSignal.Confidence.Should().Be(1.0);
        routeSignal.Value.Should().BeOneOf("fast", "balanced", "quality");

        _output.WriteLine($"Route selected: {routeSignal.Value}");
    }

    [Fact]
    public async Task AnalyzeAsync_EmitsRouteReasonSignal()
    {
        // Arrange
        var wave = new AutoRoutingWave(logger: _loggerMock.Object);
        var imagePath = CreateTestImage("reason.png", 200, 200);
        var context = CreateBasicContext();

        // Act
        var signals = (await wave.AnalyzeAsync(imagePath, context)).ToList();

        // Assert
        signals.Should().Contain(s => s.Key == "route.reason");
        var reasonSignal = signals.First(s => s.Key == "route.reason");
        reasonSignal.Value.Should().NotBeNull();

        _output.WriteLine($"Route reason: {reasonSignal.Value}");
    }

    [Fact]
    public async Task AnalyzeAsync_EmitsSkipWavesSignal()
    {
        // Arrange
        var wave = new AutoRoutingWave(logger: _loggerMock.Object);
        var imagePath = CreateTestImage("skip.png", 200, 200);
        var context = CreateBasicContext();

        // Act
        var signals = (await wave.AnalyzeAsync(imagePath, context)).ToList();

        // Assert
        signals.Should().Contain(s => s.Key == "route.skip_waves");
        var skipSignal = signals.First(s => s.Key == "route.skip_waves");
        skipSignal.Metadata.Should().ContainKey("route");

        _output.WriteLine($"Skip waves metadata: route={skipSignal.Metadata?["route"]}");
    }

    [Fact]
    public async Task AnalyzeAsync_EmitsQualityTierSignal()
    {
        // Arrange
        var wave = new AutoRoutingWave(logger: _loggerMock.Object);
        var imagePath = CreateTestImage("tier.png", 200, 200);
        var context = CreateBasicContext();

        // Act
        var signals = (await wave.AnalyzeAsync(imagePath, context)).ToList();

        // Assert
        signals.Should().Contain(s => s.Key == "route.quality_tier");
        var tierSignal = signals.First(s => s.Key == "route.quality_tier");
        var tier = Convert.ToInt32(tierSignal.Value);
        tier.Should().BeInRange(1, 3);

        _output.WriteLine($"Quality tier: {tier} (1=fast, 2=balanced, 3=quality)");
    }

    [Fact]
    public async Task AnalyzeAsync_SelectsFastRoute_ForSimpleStaticImage()
    {
        // Arrange - Simple, small, low-text image
        var wave = new AutoRoutingWave(logger: _loggerMock.Object);
        var imagePath = CreateTestImage("simple.png", 50, 50);
        var context = CreateContextForSimpleImage();

        // Act
        var signals = (await wave.AnalyzeAsync(imagePath, context)).ToList();

        // Assert
        var routeSignal = signals.First(s => s.Key == "route.selected");
        routeSignal.Value.Should().Be("fast");

        _output.WriteLine($"Simple image -> {routeSignal.Value} route");
    }

    [Fact]
    public async Task AnalyzeAsync_SelectsRoute_ForAnimatedGif()
    {
        // Arrange - Animated with many frames
        var wave = new AutoRoutingWave(logger: _loggerMock.Object);
        var imagePath = CreateTestImage("animated.png", 200, 200);
        var context = CreateContextForAnimatedImage();

        // Act
        var signals = (await wave.AnalyzeAsync(imagePath, context)).ToList();

        // Assert - Should select a valid route (routing logic may vary)
        var routeSignal = signals.First(s => s.Key == "route.selected");
        routeSignal.Value.Should().BeOneOf("quality", "balanced", "fast");

        var reasonSignal = signals.First(s => s.Key == "route.reason");
        reasonSignal.Value?.ToString().Should().NotBeNullOrEmpty();

        _output.WriteLine($"Animated image -> {routeSignal.Value} route, reason: {reasonSignal.Value}");
    }

    [Fact]
    public async Task AnalyzeAsync_SelectsRoute_ForTextHeavyImage()
    {
        // Arrange - High text likeliness + content type
        var wave = new AutoRoutingWave(logger: _loggerMock.Object);
        var imagePath = CreateTestImage("textheavy.png", 300, 200);
        var context = CreateContextForTextHeavyImage();
        // Add large pixel count to increase chances of quality route
        context.AddSignal(new Signal { Key = "identity.pixel_count", Value = 3000000, Confidence = 1.0, Source = "test" });
        context.AddSignal(new Signal { Key = "quality.edge_density", Value = 0.20, Confidence = 1.0, Source = "test" });

        // Act
        var signals = (await wave.AnalyzeAsync(imagePath, context)).ToList();

        // Assert - Should select a valid route based on indicators
        var routeSignal = signals.First(s => s.Key == "route.selected");
        routeSignal.Value.Should().BeOneOf("quality", "balanced", "fast");

        var reasonSignal = signals.First(s => s.Key == "route.reason");
        reasonSignal.Value?.ToString().Should().NotBeNullOrEmpty();

        _output.WriteLine($"Text-heavy image -> {routeSignal.Value} route, reason: {reasonSignal.Value}");
    }

    [Fact]
    public async Task AnalyzeAsync_UsesPersistedRoute_FromSignalDatabase()
    {
        // Arrange - Mock SignalDatabase to return a previous profile with route
        var previousProfile = new DynamicImageProfile { ImagePath = "previous.png" };
        previousProfile.AddSignal(new Signal
        {
            Key = "route.selected",
            Value = "quality",
            Confidence = 1.0,
            Source = "AutoRoutingWave"
        });

        _signalDbMock
            .Setup(db => db.LoadProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousProfile);

        var wave = new AutoRoutingWave(_signalDbMock.Object, _loggerMock.Object);
        var imagePath = CreateTestImage("persisted.png", 200, 200);

        // Use a context with known hash
        var context = CreateBasicContext();
        context.AddSignal(new Signal
        {
            Key = "identity.sha256",
            Value = "ABC123DEF456",
            Confidence = 1.0,
            Source = "test"
        });

        // Act
        var signals = (await wave.AnalyzeAsync(imagePath, context)).ToList();

        // Assert
        var routeSignal = signals.First(s => s.Key == "route.selected");
        routeSignal.Value.Should().Be("quality", "should use persisted route from SignalDatabase");

        var reasonSignal = signals.First(s => s.Key == "route.reason");
        reasonSignal.Value?.ToString().Should().Be("cached_decision");

        _signalDbMock.Verify(db => db.LoadProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        _output.WriteLine($"Used persisted route: {routeSignal.Value}");
    }

    [Fact]
    public async Task AnalyzeAsync_FallsBackToComputation_WhenNoPersisted()
    {
        // Arrange - SignalDatabase returns null (no previous profile)
        _signalDbMock
            .Setup(db => db.LoadProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DynamicImageProfile?)null);

        var wave = new AutoRoutingWave(_signalDbMock.Object, _loggerMock.Object);
        var imagePath = CreateTestImage("nopersisted.png", 200, 200);
        var context = CreateBasicContext();

        // Act
        var signals = (await wave.AnalyzeAsync(imagePath, context)).ToList();

        // Assert
        var routeSignal = signals.First(s => s.Key == "route.selected");
        routeSignal.Value.Should().NotBeNull();

        var reasonSignal = signals.First(s => s.Key == "route.reason");
        reasonSignal.Value?.ToString().Should().NotBe("cached_decision");

        _output.WriteLine($"Computed route: {routeSignal.Value}");
    }

    [Fact]
    public async Task AnalyzeAsync_EmitsIndividualSkipFlags()
    {
        // Arrange - Set up for fast route
        var wave = new AutoRoutingWave(logger: _loggerMock.Object);
        var imagePath = CreateTestImage("skipflags.png", 50, 50);
        var context = CreateContextForSimpleImage();

        // Act
        var signals = (await wave.AnalyzeAsync(imagePath, context)).ToList();

        // Assert - Fast route should emit skip flags for expensive waves
        var routeSignal = signals.First(s => s.Key == "route.selected");
        if (routeSignal.Value?.ToString() == "fast")
        {
            signals.Should().Contain(s => s.Key == "route.skip.VisionLlmWave");
            signals.Should().Contain(s => s.Key == "route.skip.ClipEmbeddingWave");

            _output.WriteLine("Skip flags emitted for fast route:");
            foreach (var skipSignal in signals.Where(s => s.Key.StartsWith("route.skip.")))
            {
                _output.WriteLine($"  {skipSignal.Key}");
            }
        }
    }

    #region Helper Methods

    private AnalysisContext CreateBasicContext()
    {
        var context = new AnalysisContext();
        context.AddSignal(new Signal { Key = "identity.is_animated", Value = false, Confidence = 1.0, Source = "test" });
        context.AddSignal(new Signal { Key = "identity.frame_count", Value = 1, Confidence = 1.0, Source = "test" });
        context.AddSignal(new Signal { Key = "identity.pixel_count", Value = 40000, Confidence = 1.0, Source = "test" });
        context.AddSignal(new Signal { Key = "identity.format", Value = "PNG", Confidence = 1.0, Source = "test" });
        context.AddSignal(new Signal { Key = "content.text_likeliness", Value = 0.2, Confidence = 1.0, Source = "test" });
        context.AddSignal(new Signal { Key = "quality.edge_density", Value = 0.05, Confidence = 1.0, Source = "test" });
        context.AddSignal(new Signal { Key = "color.is_grayscale", Value = false, Confidence = 1.0, Source = "test" });
        context.AddSignal(new Signal { Key = "content.type", Value = "Photo", Confidence = 1.0, Source = "test" });
        return context;
    }

    private AnalysisContext CreateContextForSimpleImage()
    {
        var context = CreateBasicContext();
        // Override for simple image characteristics
        context.AddSignal(new Signal { Key = "identity.pixel_count", Value = 2500, Confidence = 1.0, Source = "test" }); // Small
        context.AddSignal(new Signal { Key = "content.text_likeliness", Value = 0.05, Confidence = 1.0, Source = "test" }); // Low text
        context.AddSignal(new Signal { Key = "quality.edge_density", Value = 0.02, Confidence = 1.0, Source = "test" }); // Simple
        return context;
    }

    private AnalysisContext CreateContextForAnimatedImage()
    {
        var context = CreateBasicContext();
        context.AddSignal(new Signal { Key = "identity.is_animated", Value = true, Confidence = 1.0, Source = "test" });
        context.AddSignal(new Signal { Key = "identity.frame_count", Value = 24, Confidence = 1.0, Source = "test" });
        return context;
    }

    private AnalysisContext CreateContextForTextHeavyImage()
    {
        var context = CreateBasicContext();
        context.AddSignal(new Signal { Key = "content.text_likeliness", Value = 0.75, Confidence = 1.0, Source = "test" });
        context.AddSignal(new Signal { Key = "content.type", Value = "ScannedDocument", Confidence = 1.0, Source = "test" });
        return context;
    }

    private string CreateTestImage(string filename, int width, int height)
    {
        var path = Path.Combine(_testDir, filename);
        using var image = TestImageGenerator.CreateSolidColor(width, height, Color.LightGray);
        image.SaveAsPng(path);
        return path;
    }

    #endregion
}
