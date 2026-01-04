#!/usr/bin/env dotnet-script
#r "nuget: Microsoft.Extensions.DependencyInjection, 9.0.0"
#r "nuget: Microsoft.Extensions.Logging.Console, 9.0.0"
#r "nuget: Microsoft.Extensions.Options, 9.0.0"

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

// Add project references
#r "src/Mostlylucid.DocSummarizer.Images/bin/Debug/net10.0/Mostlylucid.DocSummarizer.Images.dll"

using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Extensions;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;

Console.WriteLine("=== Wave-Based OCR Pipeline Test ===\n");

// Setup DI
var services = new ServiceCollection();

// Add logging
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

// Configure image settings with advanced OCR
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
    config.Ocr.EnablePostCorrection = false;
    config.Ocr.MaxFramesForVoting = 5;
    config.Ocr.EmitPerformanceMetrics = true;
});

// Register services
services.AddDocSummarizerImages();

var provider = services.BuildServiceProvider();
var orchestrator = provider.GetRequiredService<WaveOrchestrator>();

// List registered waves
Console.WriteLine("Registered Analysis Waves:");
foreach (var wave in orchestrator.GetRegisteredWaves().OrderByDescending(w => w.Priority))
{
    Console.WriteLine($"  [{wave.Priority,3}] {wave.Name,-30} Tags: {string.Join(", ", wave.Tags)}");
}
Console.WriteLine();

// Test GIF path
var gifPath = Args.FirstOrDefault() ?? "F:/Gifs/anchorman-not-even-mad.gif";

if (!File.Exists(gifPath))
{
    Console.WriteLine($"Error: File not found: {gifPath}");
    return;
}

Console.WriteLine($"Analyzing: {Path.GetFileName(gifPath)}\n");

// Analyze
var profile = await orchestrator.AnalyzeAsync(gifPath);

Console.WriteLine($"Analysis completed in {profile.AnalysisDurationMs}ms");
Console.WriteLine($"Total signals: {profile.GetAllSignals().Count()}");
Console.WriteLine($"Contributing waves: {string.Join(", ", profile.ContributingWaves)}\n");

// Show all OCR signals
var ocrSignals = profile.GetSignalsByTag("ocr").ToList();
if (ocrSignals.Any())
{
    Console.WriteLine($"=== OCR Signals ({ocrSignals.Count}) ===");
    foreach (var signal in ocrSignals)
    {
        Console.WriteLine($"[{signal.Source}] {signal.Key}");
        Console.WriteLine($"  Value: {signal.Value}");
        Console.WriteLine($"  Confidence: {signal.Confidence:F3}");
        if (signal.Metadata != null && signal.Metadata.Any())
        {
            Console.WriteLine($"  Metadata: {string.Join(", ", signal.Metadata.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }
        Console.WriteLine();
    }
}
else
{
    Console.WriteLine("No OCR signals emitted.");
}

// Show key results
Console.WriteLine("=== Key Results ===");
Console.WriteLine($"Text Likeliness: {profile.GetValue<double>("content.text_likeliness"):F3}");

if (profile.HasSignal("ocr.frames.extracted"))
{
    Console.WriteLine($"Frames Extracted: {profile.GetValue<int>("ocr.frames.extracted")}");
}

if (profile.HasSignal("ocr.stabilization.confidence"))
{
    Console.WriteLine($"Stabilization Confidence: {profile.GetValue<double>("ocr.stabilization.confidence"):F3}");
}

if (profile.HasSignal("ocr.voting.consensus_text"))
{
    var text = profile.GetValue<string>("ocr.voting.consensus_text");
    Console.WriteLine($"Consensus Text: \"{text}\"");
    Console.WriteLine($"Agreement Score: {profile.GetValue<double>("ocr.voting.agreement_score"):F3}");
}
else if (profile.HasSignal("ocr.temporal_median.full_text"))
{
    var text = profile.GetValue<string>("ocr.temporal_median.full_text");
    Console.WriteLine($"Temporal Median OCR: \"{text}\"");
}
else if (profile.HasSignal("ocr.full_text"))
{
    var text = profile.GetValue<string>("ocr.full_text");
    Console.WriteLine($"Simple OCR Text: \"{text}\"");
}

if (profile.HasSignal("ocr.advanced.early_exit"))
{
    Console.WriteLine($"Early Exit: {profile.GetValue<bool>("ocr.advanced.early_exit")}");
}

Console.WriteLine("\n=== Test Complete ===");
