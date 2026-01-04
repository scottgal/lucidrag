using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Extensions;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

Console.WriteLine("=== Testing Text Extraction from GIFs ===\n");

// Setup DI
var services = new ServiceCollection();

// Configure with LOWER text detection threshold to ensure OCR runs
services.Configure<ImageConfig>(config =>
{
    config.ModelsDirectory = "./models";
    config.EnableOcr = true;
    config.Ocr.UseAdvancedPipeline = true;
    config.Ocr.QualityMode = OcrQualityMode.Fast;
    config.Ocr.TextDetectionConfidenceThreshold = 0.1; // VERY LOW - force OCR to run
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

// Test files
var testGifs = new[]
{
    "F:/Gifs/anchorman-not-even-mad.gif",
    "F:/Gifs/animatedbullshit.gif",
    "F:/Gifs/aed.gif"
};

foreach (var gifPath in testGifs)
{
    if (!File.Exists(gifPath))
    {
        Console.WriteLine($"Skipping {Path.GetFileName(gifPath)} - file not found\n");
        continue;
    }

    Console.WriteLine($"=== Processing: {Path.GetFileName(gifPath)} ===");

    try
    {
        var profile = await orchestrator.AnalyzeAsync(gifPath);

        Console.WriteLine($"Analysis completed in {profile.AnalysisDurationMs}ms");
        Console.WriteLine($"Total signals: {profile.GetAllSignals().Count()}");
        Console.WriteLine($"Contributing waves: {string.Join(", ", profile.ContributingWaves)}\n");

        // Check text-likeliness
        if (profile.HasSignal("content.text_likeliness"))
        {
            var textLikeliness = profile.GetValue<double>("content.text_likeliness");
            Console.WriteLine($"Text Likeliness: {textLikeliness:F3}");
        }

        // Check for OCR signals
        var ocrSignals = profile.GetSignalsByTag("ocr").ToList();
        Console.WriteLine($"\nOCR Signals Emitted: {ocrSignals.Count}");

        foreach (var signal in ocrSignals)
        {
            Console.WriteLine($"\n[{signal.Source}] {signal.Key}");

            if (signal.Key.Contains("text") || signal.Key.Contains("Text"))
            {
                Console.WriteLine($"  TEXT CONTENT: \"{signal.Value}\"");
                Console.WriteLine($"  Confidence: {signal.Confidence:F3}");
            }
            else
            {
                Console.WriteLine($"  Value: {signal.Value}");
                Console.WriteLine($"  Confidence: {signal.Confidence:F3}");
            }

            if (signal.Metadata != null && signal.Metadata.Any())
            {
                Console.WriteLine($"  Metadata: {string.Join(", ", signal.Metadata.Select(kv => $"{kv.Key}={kv.Value}"))}");
            }
        }

        // Show extracted text from all sources
        Console.WriteLine("\n--- Extracted Text Summary ---");

        if (profile.HasSignal("ocr.corrected.text"))
        {
            Console.WriteLine($"✅ CORRECTED TEXT: \"{profile.GetValue<string>("ocr.corrected.text")}\"");
        }
        else if (profile.HasSignal("ocr.voting.consensus_text"))
        {
            Console.WriteLine($"✅ CONSENSUS TEXT: \"{profile.GetValue<string>("ocr.voting.consensus_text")}\"");
        }
        else if (profile.HasSignal("ocr.temporal_median.full_text"))
        {
            Console.WriteLine($"✅ TEMPORAL MEDIAN TEXT: \"{profile.GetValue<string>("ocr.temporal_median.full_text")}\"");
        }
        else if (profile.HasSignal("ocr.full_text"))
        {
            Console.WriteLine($"✅ SIMPLE OCR TEXT: \"{profile.GetValue<string>("ocr.full_text")}\"");
        }
        else
        {
            Console.WriteLine("❌ NO TEXT EXTRACTED");

            // Check why
            if (profile.HasSignal("ocr.skipped"))
            {
                Console.WriteLine($"   Reason: OCR was skipped");
            }
            if (profile.HasSignal("ocr.advanced.skipped"))
            {
                var signal = profile.GetBestSignal("ocr.advanced.skipped");
                Console.WriteLine($"   Reason: {signal?.Metadata?["reason"]}");
            }
        }

        Console.WriteLine("\n" + new string('=', 60) + "\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: {ex.Message}");
        Console.WriteLine($"Stack: {ex.StackTrace}\n");
    }
}

Console.WriteLine("=== Test Complete ===");
