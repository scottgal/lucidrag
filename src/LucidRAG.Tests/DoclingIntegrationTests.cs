using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services;
using Xunit;
using Xunit.Abstractions;

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
        _output.WriteLine($"Accelerator: {capabilities.Accelerator}");

        if (capabilities.Available)
        {
            Assert.NotNull(capabilities.Accelerator);
        }
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
}
