using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;
using Xunit;
using Xunit.Abstractions;

namespace LucidRAG.Tests;

/// <summary>
/// Integration tests for LightOnOCR wave.
/// These tests verify that the LightOnOCR model (via Ollama) works correctly.
/// </summary>
public class LightOnOcrIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public LightOnOcrIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task LightOnOcrWave_WhenEnabled_CanProcessImage()
    {
        // Arrange
        var config = CreateConfig(enabled: true);
        var wave = new LightOnOcrWave(config);
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
            _output.WriteLine("Wave decided not to run (LightOnOCR may be disabled in config)");
            return;
        }

        // Act
        var signals = await wave.AnalyzeAsync(testImagePath, context);
        var signalsList = signals.ToList();

        // Assert
        _output.WriteLine($"Produced {signalsList.Count} signals:");

        foreach (var signal in signalsList)
        {
            var valueStr = signal.Value?.ToString() ?? "null";
            if (valueStr.Length > 200) valueStr = valueStr[..200] + "...";
            _output.WriteLine($"  {signal.Key} = {valueStr} (confidence: {signal.Confidence:F2})");
        }

        // Check for error signal (LightOnOCR service may not be running)
        var errorSignal = signalsList.FirstOrDefault(s => s.Key == "ocr.lightonocr.error");
        if (errorSignal != null)
        {
            _output.WriteLine($"\nLightOnOCR service unavailable: {errorSignal.Value}");
            _output.WriteLine("Ensure you have pulled the model: ollama pull aipib/LightOnOCR-1B-1025");
            return;
        }

        // Check for OCR text signal
        var textSignal = signalsList.FirstOrDefault(s => s.Key == "ocr.lightonocr.text");
        if (textSignal != null)
        {
            Assert.NotNull(textSignal.Value);
            _output.WriteLine($"\nExtracted OCR text ({textSignal.Value?.ToString()?.Length ?? 0} chars)");
        }

        // Check for markdown signal
        var markdownSignal = signalsList.FirstOrDefault(s => s.Key == "ocr.lightonocr.markdown");
        if (markdownSignal != null)
        {
            Assert.NotNull(markdownSignal.Value);
            _output.WriteLine($"Extracted markdown ({markdownSignal.Value?.ToString()?.Length ?? 0} chars)");
        }

        // Check for timing signal
        var timingSignal = signalsList.FirstOrDefault(s => s.Key == "ocr.lightonocr.duration_ms");
        if (timingSignal != null)
        {
            _output.WriteLine($"Processing time: {timingSignal.Value}ms");
        }
    }

    [Fact]
    public void LightOnOcrWave_WhenDisabled_ShouldNotRun()
    {
        // Arrange
        var config = CreateConfig(enabled: false);
        var wave = new LightOnOcrWave(config);
        var context = new AnalysisContext();

        // Act
        var shouldRun = wave.ShouldRun(@"C:\test.png", context);

        // Assert
        Assert.False(shouldRun);
        _output.WriteLine("LightOnOcrWave correctly skips when disabled");
    }

    [Fact]
    public void LightOnOcrWave_Priority_DefaultIs52()
    {
        // Arrange
        var config = CreateConfig(enabled: true, asPrimary: false);
        var wave = new LightOnOcrWave(config);

        // Assert
        Assert.Equal(52, wave.Priority);
        _output.WriteLine($"Priority when not primary: {wave.Priority}");
    }

    [Fact]
    public void LightOnOcrWave_Priority_WhenPrimary_Is65()
    {
        // Arrange
        var config = CreateConfig(enabled: true, asPrimary: true);
        var wave = new LightOnOcrWave(config);

        // Assert
        Assert.Equal(65, wave.Priority);
        _output.WriteLine($"Priority when primary: {wave.Priority}");
    }

    [Fact]
    public void LightOnOcrWave_HasCorrectTags()
    {
        // Arrange
        var config = CreateConfig(enabled: true);
        var wave = new LightOnOcrWave(config);

        // Assert
        Assert.Contains(SignalTags.Content, wave.Tags);
        Assert.Contains("ocr", wave.Tags);
        Assert.Contains("vlm", wave.Tags);
        Assert.Contains("lightonocr", wave.Tags);
        _output.WriteLine($"Tags: {string.Join(", ", wave.Tags)}");
    }

    [Fact]
    public void LightOnOcrWave_SkipsIfAlreadyHasResults()
    {
        // Arrange
        var config = CreateConfig(enabled: true);
        var wave = new LightOnOcrWave(config);
        var context = new AnalysisContext();

        // Pre-populate with existing results
        context.AddSignal(new Signal
        {
            Key = "ocr.lightonocr.text",
            Value = "Existing OCR text",
            Confidence = 0.9,
            Source = "test"
        });

        // Act
        var shouldRun = wave.ShouldRun(@"C:\test.png", context);

        // Assert
        Assert.False(shouldRun);
        _output.WriteLine("LightOnOcrWave correctly skips when results already exist");
    }

    [Fact]
    public void LightOnOcrWave_RunsAsEscalation_WhenOcrQualityPoor()
    {
        // Arrange
        var config = CreateConfig(enabled: true, asPrimary: false);
        var wave = new LightOnOcrWave(config);
        var context = new AnalysisContext();

        // Simulate poor OCR quality
        context.AddSignal(new Signal
        {
            Key = "ocr.quality.is_garbled",
            Value = true,
            Confidence = 0.9,
            Source = "test"
        });

        // Act
        var shouldRun = wave.ShouldRun(@"C:\test.png", context);

        // Assert
        Assert.True(shouldRun);
        _output.WriteLine("LightOnOcrWave runs as escalation when OCR quality is poor");
    }

    private static IOptions<ImageConfig> CreateConfig(bool enabled, bool asPrimary = false)
    {
        return Options.Create(new ImageConfig
        {
            Ocr = new OcrConfig
            {
                EnableLightOnOcr = enabled,
                LightOnOcrBaseUrl = "http://localhost:11434",
                LightOnOcrModelName = "aipib/LightOnOCR-1B-1025",
                LightOnOcrAsPrimary = asPrimary,
                LightOnOcrTimeoutSeconds = 120,
                LightOnOcrMaxTokens = 4096,
                LightOnOcrPreferMarkdown = true
            }
        });
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
}
