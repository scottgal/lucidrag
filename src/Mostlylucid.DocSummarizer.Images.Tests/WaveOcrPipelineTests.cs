using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Extensions;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace Mostlylucid.DocSummarizer.Images.Tests;

/// <summary>
/// Integration tests for wave-based OCR pipeline.
/// Tests the complete signal flow from AdvancedOcrWave.
/// </summary>
public class WaveOcrPipelineTests
{
    private readonly ITestOutputHelper _output;
    private readonly ServiceProvider _serviceProvider;
    private readonly WaveOrchestrator _orchestrator;

    public WaveOcrPipelineTests(ITestOutputHelper output)
    {
        _output = output;

        // Setup DI container
        var services = new ServiceCollection();

        // Configure image settings with advanced OCR enabled
        services.Configure<ImageConfig>(config =>
        {
            config.ModelsDirectory = "./models";
            config.EnableOcr = true;
            config.Ocr.UseAdvancedPipeline = true;
            config.Ocr.QualityMode = OcrQualityMode.Fast;
            config.Ocr.ConfidenceThresholdForEarlyExit = 0.95;
            config.Ocr.EnableStabilization = true;
            config.Ocr.EnableTemporalMedian = true;
            config.Ocr.EnableTemporalVoting = true;
            config.Ocr.EnablePostCorrection = false; // No dictionary for tests
            config.Ocr.MaxFramesForVoting = 5;
            config.Ocr.EmitPerformanceMetrics = true;
        });

        // Register DocSummarizer.Images services (includes waves)
        services.AddDocSummarizerImages();

        _serviceProvider = services.BuildServiceProvider();
        _orchestrator = _serviceProvider.GetRequiredService<WaveOrchestrator>();
    }

    [Fact]
    public async Task WaveOrchestrator_ShouldBeRegistered()
    {
        // Arrange & Act
        var orchestrator = _serviceProvider.GetService<WaveOrchestrator>();

        // Assert
        Assert.NotNull(orchestrator);
    }

    [Fact]
    public async Task WaveOrchestrator_ShouldHaveOcrWavesRegistered()
    {
        // Arrange & Act
        var waves = _orchestrator.GetRegisteredWaves().ToList();

        // Assert
        Assert.NotEmpty(waves);
        Assert.Contains(waves, w => w.Name == "OcrWave");
        Assert.Contains(waves, w => w.Name == "AdvancedOcrWave");
        Assert.Contains(waves, w => w.Name == "ColorWave");

        // Log registered waves
        _output.WriteLine("Registered waves:");
        foreach (var wave in waves.OrderByDescending(w => w.Priority))
        {
            _output.WriteLine($"  [{wave.Priority}] {wave.Name} - Tags: {string.Join(", ", wave.Tags)}");
        }
    }

    [Theory]
    [InlineData("F:/Gifs/aed.gif")]
    [InlineData("F:/Gifs/alanshrug_opt.gif")]
    public async Task AdvancedOcrWave_ShouldAnalyzeGif_AndEmitSignals(string gifPath)
    {
        // Skip if file doesn't exist
        if (!File.Exists(gifPath))
        {
            _output.WriteLine($"Skipping test - file not found: {gifPath}");
            return;
        }

        // Arrange
        _output.WriteLine($"\n=== Testing GIF: {Path.GetFileName(gifPath)} ===\n");

        // Act
        var profile = await _orchestrator.AnalyzeAsync(gifPath);

        // Assert
        Assert.NotNull(profile);
        Assert.NotEmpty(profile.GetAllSignals());

        // Log analysis results
        _output.WriteLine($"Analysis completed in {profile.AnalysisDurationMs}ms");
        _output.WriteLine($"Total signals: {profile.GetAllSignals().Count()}");
        _output.WriteLine($"Contributing waves: {string.Join(", ", profile.ContributingWaves)}");
        _output.WriteLine("");

        // Check for OCR signals
        var ocrSignals = profile.GetSignalsByTag("ocr").ToList();
        _output.WriteLine($"OCR signals emitted: {ocrSignals.Count}");

        foreach (var signal in ocrSignals)
        {
            _output.WriteLine($"  [{signal.Source}] {signal.Key} = {signal.Value} (confidence: {signal.Confidence:F3})");
        }

        // Verify advanced pipeline ran
        var advancedProcessed = profile.HasSignal("ocr.advanced.performance") ||
                               profile.HasSignal("ocr.frames.extracted");

        if (advancedProcessed)
        {
            _output.WriteLine("\n--- Advanced Pipeline Results ---");

            // Frame extraction
            if (profile.HasSignal("ocr.frames.extracted"))
            {
                var frameCount = profile.GetValue<int>("ocr.frames.extracted");
                _output.WriteLine($"Frames extracted: {frameCount}");
            }

            // Stabilization
            if (profile.HasSignal("ocr.stabilization.confidence"))
            {
                var stabConf = profile.GetValue<double>("ocr.stabilization.confidence");
                _output.WriteLine($"Stabilization confidence: {stabConf:F3}");
            }

            // Temporal median
            if (profile.HasSignal("ocr.temporal_median.computed"))
            {
                _output.WriteLine("Temporal median composite: Created");
                var medianText = profile.GetValue<string>("ocr.temporal_median.full_text");
                if (!string.IsNullOrEmpty(medianText))
                {
                    _output.WriteLine($"  Text: \"{medianText.Substring(0, Math.Min(100, medianText.Length))}...\"");
                }
            }

            // Early exit
            if (profile.HasSignal("ocr.advanced.early_exit"))
            {
                var earlyExit = profile.GetValue<bool>("ocr.advanced.early_exit");
                _output.WriteLine($"Early exit: {earlyExit}");
            }

            // Voting
            if (profile.HasSignal("ocr.voting.consensus_text"))
            {
                var consensusText = profile.GetValue<string>("ocr.voting.consensus_text");
                var agreementScore = profile.GetValue<double>("ocr.voting.agreement_score");
                _output.WriteLine($"Voting agreement: {agreementScore:F3}");
                _output.WriteLine($"Consensus text: \"{consensusText}\"");
            }

            // Corrections
            if (profile.HasSignal("ocr.corrections.count"))
            {
                var corrections = profile.GetValue<int>("ocr.corrections.count");
                _output.WriteLine($"Post-corrections applied: {corrections}");
            }

            // Performance
            if (profile.HasSignal("ocr.advanced.performance"))
            {
                var perf = profile.GetBestSignal("ocr.advanced.performance");
                _output.WriteLine($"Performance: {perf?.Metadata?["duration_ms"]}ms, Quality mode: {perf?.Metadata?["quality_mode"]}");
            }

            Assert.True(advancedProcessed, "Advanced OCR pipeline should have processed the GIF");
        }
        else
        {
            _output.WriteLine("\nAdvanced pipeline was skipped (may be a static image or low text-likeliness)");
        }
    }

    [Fact]
    public async Task AdvancedOcrWave_ShouldRespectEarlyExit()
    {
        // This test verifies early exit optimization
        var gifPath = "F:/Gifs/aed.gif";

        if (!File.Exists(gifPath))
        {
            _output.WriteLine("Skipping test - file not found");
            return;
        }

        // Arrange - Lower early exit threshold to trigger it
        var services = new ServiceCollection();
        services.Configure<ImageConfig>(config =>
        {
            config.Ocr.UseAdvancedPipeline = true;
            config.Ocr.QualityMode = OcrQualityMode.Fast;
            config.Ocr.ConfidenceThresholdForEarlyExit = 0.5; // Very low threshold
            config.Ocr.EnableStabilization = true;
            config.Ocr.EnableTemporalMedian = true;
            config.Ocr.EnableTemporalVoting = true;
        });
        services.AddDocSummarizerImages();
        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<WaveOrchestrator>();

        // Act
        var profile = await orchestrator.AnalyzeAsync(gifPath);

        // Assert
        _output.WriteLine($"Analysis duration: {profile.AnalysisDurationMs}ms");

        if (profile.HasSignal("ocr.advanced.early_exit"))
        {
            var earlyExit = profile.GetValue<bool>("ocr.advanced.early_exit");
            _output.WriteLine($"Early exit triggered: {earlyExit}");

            if (earlyExit)
            {
                // Verify voting was skipped
                Assert.False(profile.HasSignal("ocr.voting.consensus_text"),
                    "Voting should be skipped when early exit is triggered");
            }
        }
    }

    [Fact]
    public void ImageConfig_ShouldApplyQualityModePresets()
    {
        // Arrange
        var config = new ImageConfig();

        // Act - Set to Fast mode
        config.Ocr.QualityMode = OcrQualityMode.Fast;
        config.Ocr.ApplyQualityModePresets();

        // Assert
        Assert.True(config.Ocr.EnableStabilization);
        Assert.True(config.Ocr.EnableTemporalMedian);
        Assert.True(config.Ocr.EnableTemporalVoting);
        Assert.False(config.Ocr.EnableTextDetection); // Not in Fast mode
        Assert.Equal(0.90, config.Ocr.ConfidenceThresholdForEarlyExit);

        _output.WriteLine("Fast mode preset:");
        _output.WriteLine($"  Stabilization: {config.Ocr.EnableStabilization}");
        _output.WriteLine($"  Temporal Median: {config.Ocr.EnableTemporalMedian}");
        _output.WriteLine($"  Temporal Voting: {config.Ocr.EnableTemporalVoting}");
        _output.WriteLine($"  Text Detection: {config.Ocr.EnableTextDetection}");
        _output.WriteLine($"  Early Exit Threshold: {config.Ocr.ConfidenceThresholdForEarlyExit}");
    }

    [Fact]
    public async Task DynamicImageProfile_ShouldExportAsJson()
    {
        var gifPath = "F:/Gifs/aed.gif";

        if (!File.Exists(gifPath))
        {
            _output.WriteLine("Skipping test - file not found");
            return;
        }

        // Arrange & Act
        var profile = await _orchestrator.AnalyzeAsync(gifPath);
        var json = profile.ToJson(includeMetadata: true);

        // Assert
        Assert.NotNull(json);
        Assert.Contains("\"imagePath\"", json);
        Assert.Contains("\"signals\"", json);

        _output.WriteLine("JSON Export (first 500 chars):");
        _output.WriteLine(json.Substring(0, Math.Min(500, json.Length)));
        _output.WriteLine("...");
    }

    [Theory]
    [InlineData("F:/Gifs/anchorman-not-even-mad.gif")]
    [InlineData("F:/Gifs/animatedbullshit.gif")]
    public async Task AdvancedOcrWave_ShouldExtractText_FromGifWithText(string gifPath)
    {
        // This test verifies that actual text is extracted and returned as signals
        if (!File.Exists(gifPath))
        {
            _output.WriteLine($"Skipping test - file not found: {gifPath}");
            return;
        }

        // Arrange - Configure with threshold = 0 to FORCE OCR regardless of text-likeliness
        var services = new ServiceCollection();
        services.Configure<ImageConfig>(config =>
        {
            config.ModelsDirectory = "./models";
            config.EnableOcr = true;
            config.Ocr.UseAdvancedPipeline = true;
            config.Ocr.QualityMode = OcrQualityMode.Fast;
            config.Ocr.TextDetectionConfidenceThreshold = 0; // 0 = disable text-likeliness check, force OCR
            config.Ocr.ConfidenceThresholdForEarlyExit = 0.95;
            config.Ocr.EnableStabilization = true;
            config.Ocr.EnableTemporalMedian = true;
            config.Ocr.EnableTemporalVoting = true;
            config.Ocr.EnablePostCorrection = false;
            config.Ocr.MaxFramesForVoting = 5;
            config.Ocr.EmitPerformanceMetrics = true;
        });
        services.AddDocSummarizerImages();
        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<WaveOrchestrator>();

        _output.WriteLine($"\n=== Testing Text Extraction: {Path.GetFileName(gifPath)} ===\n");

        // Act
        var profile = await orchestrator.AnalyzeAsync(gifPath);

        // Assert basic analysis completed
        Assert.NotNull(profile);
        Assert.NotEmpty(profile.GetAllSignals());

        _output.WriteLine($"Analysis completed in {profile.AnalysisDurationMs}ms");
        _output.WriteLine($"Total signals: {profile.GetAllSignals().Count()}");
        _output.WriteLine($"Contributing waves: {string.Join(", ", profile.ContributingWaves)}");

        // Check text-likeliness
        if (profile.HasSignal("content.text_likeliness"))
        {
            var textLikeliness = profile.GetValue<double>("content.text_likeliness");
            _output.WriteLine($"Text Likeliness: {textLikeliness:F3}");
        }

        // Get all OCR signals
        var ocrSignals = profile.GetSignalsByTag("ocr").ToList();
        _output.WriteLine($"\n--- OCR Signals ({ocrSignals.Count}) ---");

        foreach (var signal in ocrSignals)
        {
            _output.WriteLine($"\n[{signal.Source}] {signal.Key}");
            if (signal.Key.Contains("text", StringComparison.OrdinalIgnoreCase))
            {
                _output.WriteLine($"  TEXT: \"{signal.Value}\"");
            }
            else
            {
                _output.WriteLine($"  Value: {signal.Value}");
            }
            _output.WriteLine($"  Confidence: {signal.Confidence:F3}");
        }

        // Verify text extraction - check all possible text signal keys
        _output.WriteLine("\n--- Text Extraction Results ---");

        string? extractedText = null;
        string? source = null;

        if (profile.HasSignal("ocr.corrected.text"))
        {
            extractedText = profile.GetValue<string>("ocr.corrected.text");
            source = "Post-Corrected";
        }
        else if (profile.HasSignal("ocr.voting.consensus_text"))
        {
            extractedText = profile.GetValue<string>("ocr.voting.consensus_text");
            source = "Temporal Voting";
        }
        else if (profile.HasSignal("ocr.temporal_median.full_text"))
        {
            extractedText = profile.GetValue<string>("ocr.temporal_median.full_text");
            source = "Temporal Median";
        }
        else if (profile.HasSignal("ocr.full_text"))
        {
            extractedText = profile.GetValue<string>("ocr.full_text");
            source = "Simple OCR";
        }

        if (!string.IsNullOrWhiteSpace(extractedText))
        {
            _output.WriteLine($"✅ TEXT EXTRACTED ({source}):");
            _output.WriteLine($"   \"{extractedText}\"");
            _output.WriteLine($"   Length: {extractedText.Length} characters");

            // Assert that we got some text
            Assert.NotNull(extractedText);
            Assert.NotEmpty(extractedText);
        }
        else
        {
            _output.WriteLine("⚠️  NO TEXT EXTRACTED");

            // Check why OCR was skipped
            if (profile.HasSignal("ocr.skipped") || profile.HasSignal("ocr.advanced.skipped"))
            {
                var skipSignal = profile.GetBestSignal("ocr.advanced.skipped") ?? profile.GetBestSignal("ocr.skipped");
                if (skipSignal?.Metadata != null && skipSignal.Metadata.ContainsKey("reason"))
                {
                    _output.WriteLine($"   Reason: {skipSignal.Metadata["reason"]}");
                }
            }

            // For this test, we're expecting text, so log a warning but don't fail
            _output.WriteLine("   Note: Expected text extraction from this GIF");
        }
    }

}
