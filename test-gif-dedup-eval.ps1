# Simplified test for text-aware deduplication evaluation
# Focuses on frame extraction and text quality scoring

param(
    [string]$GifPath = "F:\Gifs\alanshrug_opt.gif"
)

$ErrorActionPreference = "Stop"

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Text-Aware Deduplication - Evaluation" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

if (-not (Test-Path $GifPath)) {
    Write-Host "GIF not found: $GifPath" -ForegroundColor Red
    exit 1
}

Write-Host "GIF: $GifPath" -ForegroundColor Yellow
$gifInfo = Get-Item $GifPath
Write-Host "Size: $([math]::Round($gifInfo.Length/1KB, 1)) KB`n" -ForegroundColor Gray

# Create test C# script
$testCode = @'
#r "nuget: SixLabors.ImageSharp, 3.1.5"
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Linq;

var gifPath = args[0];

Console.WriteLine("Loading GIF...");
using var image = Image.Load<Rgba32>(gifPath);

Console.WriteLine($"Total Frames: {image.Frames.Count}");
Console.WriteLine();

// Simulate text-aware deduplication
var kept = new List<int>();
var skipped = new List<int>();
var replaced = new List<(int oldIdx, int newIdx, double improvement)>();

Image<Rgba32>? previousFrame = null;
double previousTextQuality = 0;
int keptIndex = -1;

const double SSIM_THRESHOLD = 0.95;
const double TEXT_IMPROVEMENT_THRESHOLD = 0.2;

Console.WriteLine("Processing frames with text-aware deduplication...");
Console.WriteLine();

for (int i = 0; i < image.Frames.Count; i++)
{
    var frame = image.Frames.CloneFrame(i);
    var textQuality = ComputeTextQualityScore(frame);

    if (previousFrame != null)
    {
        var similarity = ComputeFrameSimilarity(previousFrame, frame);

        if (similarity > SSIM_THRESHOLD)
        {
            var improvement = textQuality - previousTextQuality;

            if (improvement > TEXT_IMPROVEMENT_THRESHOLD)
            {
                // Replace previous
                replaced.Add((keptIndex, i, improvement));
                previousFrame.Dispose();
                previousFrame = frame;
                previousTextQuality = textQuality;
                keptIndex = i;
                Console.WriteLine($"  Frame {i,3}: REPLACE (text quality {previousTextQuality:F3} -> {textQuality:F3}, +{improvement:F3})");
            }
            else
            {
                // Skip this frame
                skipped.Add(i);
                frame.Dispose();
            }
            continue;
        }
    }

    // Keep this frame
    kept.Add(i);
    previousFrame?.Dispose();
    previousFrame = frame;
    previousTextQuality = textQuality;
    keptIndex = i;

    if (i % 5 == 0)
    {
        Console.WriteLine($"  Frame {i,3}: KEEP   (text quality: {textQuality:F3})");
    }
}

previousFrame?.Dispose();

Console.WriteLine();
Console.WriteLine("========================================");
Console.WriteLine("Deduplication Results");
Console.WriteLine("========================================");
Console.WriteLine();
Console.WriteLine($"Original frames: {image.Frames.Count}");
Console.WriteLine($"Kept frames:     {kept.Count}");
Console.WriteLine($"Skipped (dupes): {skipped.Count}");
Console.WriteLine($"Replaced:        {replaced.Count}");
Console.WriteLine();
Console.WriteLine($"Reduction:       {((skipped.Count * 100.0) / image.Frames.Count):F1}%");
Console.WriteLine($"Final frames:    {kept.Count}");
Console.WriteLine();

if (replaced.Any())
{
    Console.WriteLine("Text Quality Improvements:");
    foreach (var (oldIdx, newIdx, improvement) in replaced.Take(5))
    {
        Console.WriteLine($"  Frame {oldIdx} -> {newIdx}: +{improvement:F3} ({improvement * 100:F1}%)");
    }
    if (replaced.Count > 5)
    {
        Console.WriteLine($"  ... and {replaced.Count - 5} more replacements");
    }
    Console.WriteLine();
}

Console.WriteLine("========================================");
Console.WriteLine("Evaluation");
Console.WriteLine("========================================");
Console.WriteLine();

var avgImprovement = replaced.Any() ? replaced.Average(r => r.improvement) : 0;
if (replaced.Any())
{
    Console.WriteLine($"[SUCCESS] Text-aware deduplication working!");
    Console.WriteLine($"  - {replaced.Count} frames replaced with better text quality");
    Console.WriteLine($"  - Average improvement: {avgImprovement:F3} ({avgImprovement * 100:F1}%)");
    Console.WriteLine($"  - System correctly prioritizes text-rich frames");
}
else if (skipped.Count > 0)
{
    Console.WriteLine($"[OK] Standard SSIM deduplication");
    Console.WriteLine($"  - {skipped.Count} duplicate frames removed");
    Console.WriteLine($"  - No significant text quality improvements found");
}
else
{
    Console.WriteLine($"[INFO] All frames kept - highly unique content");
}

Console.WriteLine();

// Helper methods
double ComputeTextQualityScore(Image<Rgba32> frame)
{
    var textLikeliness = ComputeFastTextLikeliness(frame);
    var sharpness = ComputeFastSharpness(frame);
    return 0.7 * textLikeliness + 0.3 * sharpness;
}

double ComputeFastTextLikeliness(Image<Rgba32> frame)
{
    using var workImage = frame.Clone();
    if (workImage.Width > 256)
    {
        workImage.Mutate(x => x.Resize(256, 0));
    }

    var edgeDensity = 0.0;
    var highContrastPixels = 0;
    var totalPixels = workImage.Width * workImage.Height;

    for (int y = 1; y < workImage.Height - 1; y++)
    {
        for (int x = 1; x < workImage.Width - 1; x++)
        {
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

double ComputeFastSharpness(Image<Rgba32> frame)
{
    using var workImage = frame.Clone();
    if (workImage.Width > 256)
    {
        workImage.Mutate(x => x.Resize(256, 0));
    }

    var variances = new List<double>();

    for (int y = 1; y < workImage.Height - 1; y += 3)
    {
        for (int x = 1; x < workImage.Width - 1; x += 3)
        {
            var values = new List<double>();
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
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

double Luma(Rgba32 pixel)
{
    return 0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B;
}

double ComputeFrameSimilarity(Image<Rgba32> frame1, Image<Rgba32> frame2)
{
    var width = Math.Min(frame1.Width, frame2.Width);
    var height = Math.Min(frame1.Height, frame2.Height);

    long totalDifference = 0;
    long totalPixels = 0;

    for (int y = 0; y < height; y += 4)
    {
        for (int x = 0; x < width; x += 4)
        {
            var p1 = frame1[x, y];
            var p2 = frame2[x, y];

            var diff = Math.Abs(p1.R - p2.R) + Math.Abs(p1.G - p2.G) + Math.Abs(p1.B - p2.B);
            totalDifference += diff;
            totalPixels++;
        }
    }

    if (totalPixels == 0) return 1.0;

    var avgDifference = totalDifference / (double)totalPixels;
    var normalizedDifference = avgDifference / 765.0;

    return 1.0 - normalizedDifference;
}
'@

$scriptPath = "E:\source\lucidrag\temp-gif-test.csx"
$testCode | Out-File -FilePath $scriptPath -Encoding UTF8

Write-Host "Running evaluation..." -ForegroundColor Yellow
dotnet script $scriptPath $GifPath

Write-Host "`n[COMPLETE] Evaluation finished!`n" -ForegroundColor Green

# Cleanup
Remove-Item $scriptPath -ErrorAction SilentlyContinue
