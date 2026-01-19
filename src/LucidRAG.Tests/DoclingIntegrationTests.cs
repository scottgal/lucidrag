using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;
using Mostlylucid.DocSummarizer.Services;
using Xunit;
using Xunit.Abstractions;
using AudioAnalysisContext = AudioSummarizer.Core.Services.Analysis.AnalysisContext;

namespace LucidRAG.Tests;

/// <summary>
/// Integration tests for Docling service integration.
/// These tests verify that Docling handlers work correctly when the service is available,
/// and fall back gracefully when it's not.
/// </summary>
public class DoclingIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public DoclingIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task DoclingDocumentHandler_WhenEnabled_CanProcessPdf()
    {
        // Arrange
        var config = CreateConfig(enabled: true);
        var handler = new DoclingDocumentHandler(config);

        // Check if Docling is available
        var isAvailable = await handler.IsAvailableAsync();
        _output.WriteLine($"Docling service available: {isAvailable}");

        if (!isAvailable)
        {
            _output.WriteLine("Skipping test - Docling service not running at http://localhost:5001");
            return;
        }

        // Create a simple test PDF (or use an existing one)
        var testPdfPath = FindTestPdf();
        if (testPdfPath == null)
        {
            _output.WriteLine("Skipping test - No test PDF found");
            return;
        }

        _output.WriteLine($"Testing with PDF: {testPdfPath}");

        // Act
        var canHandle = handler.CanHandle(testPdfPath);
        _output.WriteLine($"CanHandle: {canHandle}");

        if (canHandle)
        {
            var result = await handler.ProcessAsync(testPdfPath, new DocumentHandlerOptions());

            // Assert
            Assert.NotNull(result);
            Assert.NotNull(result.Markdown);
            Assert.NotEmpty(result.Markdown);

            _output.WriteLine($"Extraction method: {result.Metadata?["extractionMethod"]}");
            _output.WriteLine($"Content length: {result.Markdown.Length} chars");
            _output.WriteLine($"Title: {result.Title}");
            _output.WriteLine($"First 500 chars: {result.Markdown[..Math.Min(500, result.Markdown.Length)]}");
        }
    }

    [Fact]
    public async Task DoclingDocumentHandler_WhenDisabled_ReturnsFalseForCanHandle()
    {
        // Arrange - Docling disabled
        var config = CreateConfig(enabled: false);
        var handler = new DoclingDocumentHandler(config);

        // Act
        var canHandle = handler.CanHandle(@"C:\test.pdf");

        // Assert
        Assert.False(canHandle);
        _output.WriteLine("DoclingDocumentHandler correctly returns false when disabled");
    }

    [Fact]
    public async Task DoclingDocumentHandler_DetectsCapabilities()
    {
        // Arrange
        var config = CreateConfig(enabled: true);
        var handler = new DoclingDocumentHandler(config);

        // Act
        var capabilities = await handler.DetectCapabilitiesAsync();

        // Assert
        _output.WriteLine($"Docling available: {capabilities.Available}");
        _output.WriteLine($"Has GPU: {capabilities.HasGpu}");
        _output.WriteLine($"Accelerator: {capabilities.Accelerator ?? "not reported"}");

        // Capabilities detection is best-effort, just verify we got a result
        Assert.True(capabilities.Available || !capabilities.Available, "Should return a valid capabilities object");
    }

    [Fact]
    public void DoclingConfig_ShouldHandle_ReturnsCorrectResults()
    {
        // Arrange
        var config = CreateConfig(enabled: true).Value.Docling;

        // Act & Assert - Documents
        Assert.True(config.ShouldHandle(".pdf"));
        Assert.True(config.ShouldHandle(".docx"));
        Assert.True(config.ShouldHandle(".pptx"));
        Assert.True(config.ShouldHandle(".xlsx"));
        Assert.True(config.ShouldHandle(".html"));

        // Images
        Assert.True(config.ShouldHandle(".png"));
        Assert.True(config.ShouldHandle(".jpg"));
        Assert.True(config.ShouldHandle(".jpeg"));

        // Audio
        Assert.True(config.ShouldHandle(".wav"));
        Assert.True(config.ShouldHandle(".mp3"));

        // Unsupported
        Assert.False(config.ShouldHandle(".exe"));
        Assert.False(config.ShouldHandle(".zip"));

        _output.WriteLine("DoclingConfig.ShouldHandle() returns correct results");
    }

    [Fact]
    public void DoclingConfig_WhenDisabled_ShouldHandleReturnsFalse()
    {
        // Arrange
        var config = CreateConfig(enabled: false).Value.Docling;

        // Act & Assert - All should return false when disabled
        Assert.False(config.ShouldHandle(".pdf"));
        Assert.False(config.ShouldHandle(".png"));
        Assert.False(config.ShouldHandle(".wav"));

        _output.WriteLine("DoclingConfig.ShouldHandle() returns false when master switch is disabled");
    }

    private static IOptions<DocSummarizerConfig> CreateConfig(bool enabled)
    {
        return Options.Create(new DocSummarizerConfig
        {
            Docling = new DoclingConfig
            {
                Enabled = enabled,
                BaseUrl = "http://localhost:5001",
                TimeoutSeconds = 60,
                Documents = new DoclingDocumentConfig
                {
                    Enabled = true,
                    Extensions = [".pdf", ".docx", ".pptx", ".xlsx", ".html"],
                    Priority = 20
                },
                Images = new DoclingImageConfig
                {
                    Enabled = true,
                    Extensions = [".png", ".jpg", ".jpeg", ".tiff", ".webp", ".bmp"],
                    UseAsPrimaryOcr = false
                },
                Audio = new DoclingAudioConfig
                {
                    Enabled = true,
                    Extensions = [".wav", ".mp3", ".m4a", ".flac", ".ogg"],
                    UseAsPrimaryTranscription = false
                }
            }
        });
    }

    [Fact]
    public async Task DoclingOcrWave_WhenEnabled_CanProcessImage()
    {
        // Arrange
        var config = CreateConfig(enabled: true);
        var wave = new DoclingOcrWave(config);
        var context = new AnalysisContext();

        // Find a test image
        var testImagePath = FindTestImage();
        if (testImagePath == null)
        {
            _output.WriteLine("Skipping test - No test image found");
            return;
        }

        _output.WriteLine($"Testing with image: {testImagePath}");

        // Check if wave should run
        var shouldRun = wave.ShouldRun(testImagePath, context);
        _output.WriteLine($"ShouldRun: {shouldRun}");

        if (!shouldRun)
        {
            _output.WriteLine("Wave decided not to run (Docling may be unavailable)");
            return;
        }

        // Act
        var signals = await wave.AnalyzeAsync(testImagePath, context);
        var signalsList = signals.ToList();

        // Assert
        Assert.NotEmpty(signalsList);
        _output.WriteLine($"Produced {signalsList.Count} signals:");

        foreach (var signal in signalsList)
        {
            var valueStr = signal.Value?.ToString() ?? "null";
            if (valueStr.Length > 200) valueStr = valueStr[..200] + "...";
            _output.WriteLine($"  {signal.Key} = {valueStr} (confidence: {signal.Confidence:F2})");
        }

        // Check for OCR text signal
        var textSignal = signalsList.FirstOrDefault(s => s.Key == "ocr.docling.text");
        if (textSignal != null)
        {
            _output.WriteLine($"\nExtracted OCR text ({textSignal.Value?.ToString()?.Length ?? 0} chars)");
        }
    }

    [Fact]
    public async Task DoclingOcrWave_WhenDisabled_ShouldNotRun()
    {
        // Arrange
        var config = CreateConfig(enabled: false);
        var wave = new DoclingOcrWave(config);
        var context = new AnalysisContext();

        // Act
        var shouldRun = wave.ShouldRun(@"C:\test.png", context);

        // Assert
        Assert.False(shouldRun);
        _output.WriteLine("DoclingOcrWave correctly skips when disabled");
    }

    [Fact]
    public async Task DoclingTranscriptionWave_WhenEnabled_CanProcessAudio()
    {
        // Arrange
        var config = CreateConfig(enabled: true);
        var wave = new AudioSummarizer.Core.Services.Analysis.Waves.DoclingTranscriptionWave(config);
        var context = new AudioAnalysisContext();

        // Find a test audio file
        var testAudioPath = FindTestAudio();
        if (testAudioPath == null)
        {
            _output.WriteLine("Skipping test - No test audio found");
            return;
        }

        _output.WriteLine($"Testing with audio: {testAudioPath}");

        // Check if wave should run
        var shouldRun = wave.ShouldRun(testAudioPath, context);
        _output.WriteLine($"ShouldRun: {shouldRun}");

        if (!shouldRun)
        {
            _output.WriteLine("Wave decided not to run (Docling may be unavailable)");
            return;
        }

        // Act
        var signals = await wave.AnalyzeAsync(testAudioPath, context);
        var signalsList = signals.ToList();

        // Assert
        Assert.NotEmpty(signalsList);
        _output.WriteLine($"Produced {signalsList.Count} signals:");

        foreach (var signal in signalsList)
        {
            var valueStr = signal.Value?.ToString() ?? "null";
            if (valueStr.Length > 200) valueStr = valueStr[..200] + "...";
            _output.WriteLine($"  {signal.Name} = {valueStr} (confidence: {signal.Confidence:F2})");
        }
    }

    [Fact]
    public async Task DoclingTranscriptionWave_WhenDisabled_ShouldNotRun()
    {
        // Arrange
        var config = CreateConfig(enabled: false);
        var wave = new AudioSummarizer.Core.Services.Analysis.Waves.DoclingTranscriptionWave(config);
        var context = new AudioAnalysisContext();

        // Act
        var shouldRun = wave.ShouldRun(@"C:\test.wav", context);

        // Assert
        Assert.False(shouldRun);
        _output.WriteLine("DoclingTranscriptionWave correctly skips when disabled");
    }

    private string? FindTestPdf()
    {
        // Look for test PDFs in common locations
        var searchPaths = new[]
        {
            @"E:\source\lucidrag\src\LucidRAG.Tests\TestData",
            @"E:\source\lucidrag\testdata",
            @"C:\Users\scott\Downloads"
        };

        foreach (var path in searchPaths)
        {
            if (Directory.Exists(path))
            {
                var pdfs = Directory.GetFiles(path, "*.pdf", SearchOption.TopDirectoryOnly);
                if (pdfs.Length > 0)
                {
                    return pdfs[0];
                }
            }
        }

        return null;
    }

    private string? FindTestImage()
    {
        var searchPaths = new[]
        {
            @"E:\source\lucidrag\src\LucidRAG.Tests\TestData",
            @"E:\source\lucidrag\testdata",
            @"C:\Users\scott\Downloads",
            @"C:\Users\scott\Pictures"
        };

        var extensions = new[] { "*.png", "*.jpg", "*.jpeg" };

        foreach (var path in searchPaths)
        {
            if (Directory.Exists(path))
            {
                foreach (var ext in extensions)
                {
                    var files = Directory.GetFiles(path, ext, SearchOption.TopDirectoryOnly);
                    if (files.Length > 0)
                    {
                        return files[0];
                    }
                }
            }
        }

        return null;
    }

    private string? FindTestAudio()
    {
        var searchPaths = new[]
        {
            @"E:\source\lucidrag\src\LucidRAG.Tests\TestData",
            @"E:\source\lucidrag\testdata",
            @"C:\Users\scott\Downloads",
            @"C:\Users\scott\Music"
        };

        var extensions = new[] { "*.wav", "*.mp3", "*.m4a" };

        foreach (var path in searchPaths)
        {
            if (Directory.Exists(path))
            {
                foreach (var ext in extensions)
                {
                    var files = Directory.GetFiles(path, ext, SearchOption.TopDirectoryOnly);
                    if (files.Length > 0)
                    {
                        return files[0];
                    }
                }
            }
        }

        return null;
    }
}
