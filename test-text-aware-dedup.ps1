# Test script for text-aware frame deduplication
# Demonstrates the new text quality scoring in action

param(
    [string]$TestImage = "E:\source\lucidrag\test-images\chat-ui.png"
)

$ErrorActionPreference = "Stop"

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Text-Aware Deduplication Evaluation" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

if (-not (Test-Path $TestImage)) {
    Write-Host "Test image not found: $TestImage" -ForegroundColor Red
    exit 1
}

Write-Host "Building project..." -ForegroundColor Yellow
dotnet build "E:\source\lucidrag\LucidRAG.sln" -c Release /p:WarningLevel=0 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}
Write-Host "Build successful!`n" -ForegroundColor Green

$cliPath = "E:\source\lucidrag\src\LucidRAG.ImageCli"

# Create a simple C# test program to evaluate text quality scoring
$testCode = @'
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;

var imagePath = args[0];
using var image = Image.Load<Rgba32>(imagePath);

Console.WriteLine($"Image: {System.IO.Path.GetFileName(imagePath)}");
Console.WriteLine($"Dimensions: {image.Width}x{image.Height}");
Console.WriteLine();

// Compute text quality score
var textQuality = ComputeTextQualityScore(image);
Console.WriteLine($"Text Quality Score: {textQuality:F3}");
Console.WriteLine($"  - Text Likeliness: {ComputeFastTextLikeliness(image):F3}");
Console.WriteLine($"  - Sharpness: {ComputeFastSharpness(image):F3}");
Console.WriteLine();

// Create degraded versions to test replacement logic
Console.WriteLine("Testing degradation scenarios:");
Console.WriteLine();

// Scenario 1: Slightly blurred
using var blurred = image.Clone(ctx => ctx.GaussianBlur(2.0f));
var blurredQuality = ComputeTextQualityScore(blurred);
Console.WriteLine($"1. Gaussian Blur (2.0): {blurredQuality:F3}");
Console.WriteLine($"   Quality loss: {((textQuality - blurredQuality) * 100):F1}%");
Console.WriteLine($"   Would replace? {(textQuality - blurredQuality > 0.2 ? "YES" : "NO")}");
Console.WriteLine();

// Scenario 2: Heavily blurred
using var heavyBlur = image.Clone(ctx => ctx.GaussianBlur(5.0f));
var heavyBlurQuality = ComputeTextQualityScore(heavyBlur);
Console.WriteLine($"2. Heavy Blur (5.0): {heavyBlurQuality:F3}");
Console.WriteLine($"   Quality loss: {((textQuality - heavyBlurQuality) * 100):F1}%");
Console.WriteLine($"   Would replace? {(textQuality - heavyBlurQuality > 0.2 ? "YES" : "NO")}");
Console.WriteLine();

// Scenario 3: Brightness reduced (simulating poor lighting)
using var darkened = image.Clone(ctx => ctx.Brightness(0.7f));
var darkenedQuality = ComputeTextQualityScore(darkened);
Console.WriteLine($"3. Darkened (0.7): {darkenedQuality:F3}");
Console.WriteLine($"   Quality loss: {((textQuality - darkenedQuality) * 100):F1}%");
Console.WriteLine($"   Would replace? {(textQuality - darkenedQuality > 0.2 ? "YES" : "NO")}");
Console.WriteLine();

Console.WriteLine("========================================");
Console.WriteLine("Evaluation Summary");
Console.WriteLine("========================================");
Console.WriteLine();
Console.WriteLine($"Original image has text quality score of {textQuality:F3}");
Console.WriteLine();
Console.WriteLine("The text-aware deduplication will:");
Console.WriteLine("  1. Score each frame for text clarity");
Console.WriteLine("  2. When frames are visually similar (SSIM > threshold):");
Console.WriteLine("     - Keep frame with >20% better text quality");
Console.WriteLine("     - Discard frame with similar/worse text quality");
Console.WriteLine("  3. Prioritize capturing text at its clearest moment");
Console.WriteLine();

if (textQuality > 0.5) {
    Console.WriteLine($"[HIGH TEXT] This image has strong text signals ({textQuality:F3})");
    Console.WriteLine("Perfect candidate for text-aware deduplication!");
} else if (textQuality > 0.3) {
    Console.WriteLine($"[MODERATE TEXT] This image has moderate text signals ({textQuality:F3})");
    Console.WriteLine("Would benefit from text-aware deduplication.");
} else {
    Console.WriteLine($"[LOW TEXT] This image has weak text signals ({textQuality:F3})");
    Console.WriteLine("Text-aware deduplication would fall back to SSIM-only.");
}

// Helper methods (simplified versions from AdvancedGifOcrService)
double ComputeTextQualityScore(Image<Rgba32> frame) {
    var textLikeliness = ComputeFastTextLikeliness(frame);
    var sharpness = ComputeFastSharpness(frame);
    return 0.7 * textLikeliness + 0.3 * sharpness;
}

double ComputeFastTextLikeliness(Image<Rgba32> frame) {
    using var workImage = frame.Clone();
    if (workImage.Width > 256) {
        workImage.Mutate(x => x.Resize(256, 0));
    }

    var edgeDensity = 0.0;
    var highContrastPixels = 0;
    var totalPixels = workImage.Width * workImage.Height;

    for (int y = 1; y < workImage.Height - 1; y++) {
        for (int x = 1; x < workImage.Width - 1; x++) {
            var gx = Math.Abs(
                -1 * Luma(workImage[x - 1, y - 1]) + 1 * Luma(workImage[x + 1, y - 1]) +
                -2 * Luma(workImage[x - 1, y]) + 2 * Luma(workImage[x + 1, y]) +
                -1 * Luma(workImage[x - 1, y + 1]) + 1 * Luma(workImage[x + 1, y + 1]));

            if (gx > 30) edgeDensity += 1;

            var luminance = Luma(workImage[x, y]);
            if (luminance < 64 || luminance > 192) highContrastPixels++;
        }
    }

    edgeDensity /= totalPixels;
    var contrastRatio = highContrastPixels / (double)totalPixels;
    return Math.Min(1.0, edgeDensity * 10 + contrastRatio * 0.5);
}

double ComputeFastSharpness(Image<Rgba32> frame) {
    using var workImage = frame.Clone();
    if (workImage.Width > 256) {
        workImage.Mutate(x => x.Resize(256, 0));
    }

    var variances = new List<double>();

    for (int y = 1; y < workImage.Height - 1; y += 3) {
        for (int x = 1; x < workImage.Width - 1; x += 3) {
            var values = new List<double>();
            for (int dy = -1; dy <= 1; dy++) {
                for (int dx = -1; dx <= 1; dx++) {
                    values.Add(Luma(workImage[x + dx, y + dy]));
                }
            }

            var mean = values.Average();
            var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
            variances.Add(variance);
        }
    }

    if (variances.Count == 0) return 0;

    var avgVariance = variances.Average();
    return Math.Min(1.0, avgVariance / 100.0);
}

double Luma(Rgba32 pixel) {
    return 0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B;
}
'@

# Write test program
$testProgPath = "E:\source\lucidrag\test-text-quality.csx"
$testCode | Out-File -FilePath $testProgPath -Encoding UTF8

Write-Host "Running text quality evaluation..." -ForegroundColor Yellow
Write-Host ""

# Run with dotnet-script
dotnet script $testProgPath $TestImage

Write-Host "`n[SUCCESS] Evaluation complete!`n" -ForegroundColor Green

# Cleanup
Remove-Item $testProgPath -ErrorAction SilentlyContinue
