using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Mostlylucid.DocSummarizer.Images.Services.Vision.Clients;

namespace Mostlylucid.DocSummarizer.Images.Services.Vision;

/// <summary>
/// Fast caption service for direct LLM calls without full heuristics pipeline.
/// Provides consistent prompts across all clients (CLI, Desktop, etc.).
/// </summary>
public class FastCaptionService
{
    private readonly UnifiedVisionService _visionService;
    private readonly ILogger<FastCaptionService>? _logger;

    // Standard prompts - used by ALL clients for consistency
    public static class Prompts
    {
        public const string SimpleCaption = "Describe this image in one concise sentence.";

        public const string DetailedCaption = "Describe this image concisely. Focus on the main subject and any visible text.";

        public const string GifStrip = "This is a {0}-frame animated GIF shown as a horizontal strip (left to right = time). " +
            "Describe the animation and read any text/subtitles.";

        public const string GifStripDetailed = "This is a {0}-frame animated GIF shown left-to-right. " +
            "Describe what happens in the animation, the motion, and transcribe any visible text or subtitles.";

        public const string TextExtraction = "Read and transcribe all visible text in this image. Return only the text, nothing else.";
    }

    public FastCaptionService(
        UnifiedVisionService visionService,
        ILogger<FastCaptionService>? logger = null)
    {
        _visionService = visionService;
        _logger = logger;
    }

    /// <summary>
    /// Get a quick caption for an image without full analysis.
    /// Handles GIFs by creating frame strips automatically.
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <param name="detailed">If true, asks for more detail including text</param>
    /// <param name="model">Optional model override</param>
    /// <param name="ct">Cancellation token</param>
    public async Task<FastCaptionResult> GetCaptionAsync(
        string imagePath,
        bool detailed = true,
        string? model = null,
        CancellationToken ct = default)
    {
        var ext = Path.GetExtension(imagePath).ToLowerInvariant();

        // Handle GIFs with frame strip
        if (ext == ".gif")
        {
            return await GetGifCaptionAsync(imagePath, detailed, model, ct);
        }

        // Static image - simple caption
        var prompt = detailed ? Prompts.DetailedCaption : Prompts.SimpleCaption;
        var result = await _visionService.AnalyzeImageAsync(imagePath, prompt, model: model, ct: ct);

        return new FastCaptionResult(
            Success: result.Success,
            Caption: CleanCaption(result.Caption),
            Error: result.Error,
            FrameCount: 1,
            Model: result.Model);
    }

    /// <summary>
    /// Get caption for a GIF by creating a deduped frame strip.
    /// </summary>
    private async Task<FastCaptionResult> GetGifCaptionAsync(
        string gifPath,
        bool detailed,
        string? model,
        CancellationToken ct)
    {
        try
        {
            // Create frame strip
            var (stripPath, frameCount) = await CreateGifFrameStripAsync(gifPath, ct);

            if (stripPath == null || frameCount < 2)
            {
                // Single frame GIF or failed - treat as static
                var prompt = detailed ? Prompts.DetailedCaption : Prompts.SimpleCaption;
                var result = await _visionService.AnalyzeImageAsync(gifPath, prompt, model: model, ct: ct);
                return new FastCaptionResult(result.Success, CleanCaption(result.Caption), result.Error, 1, result.Model);
            }

            try
            {
                // Use GIF-specific prompt
                var prompt = string.Format(detailed ? Prompts.GifStripDetailed : Prompts.GifStrip, frameCount);
                var result = await _visionService.AnalyzeImageAsync(stripPath, prompt, model: model, ct: ct);

                return new FastCaptionResult(
                    Success: result.Success,
                    Caption: CleanCaption(result.Caption),
                    Error: result.Error,
                    FrameCount: frameCount,
                    Model: result.Model);
            }
            finally
            {
                // Clean up temp file
                if (File.Exists(stripPath))
                {
                    try { File.Delete(stripPath); } catch { /* ignore */ }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to create GIF frame strip for {Path}", gifPath);

            // Fallback to original image
            var prompt = detailed ? Prompts.DetailedCaption : Prompts.SimpleCaption;
            var result = await _visionService.AnalyzeImageAsync(gifPath, prompt, model: model, ct: ct);
            return new FastCaptionResult(result.Success, CleanCaption(result.Caption), result.Error, 1, result.Model);
        }
    }

    /// <summary>
    /// Extract unique frames from GIF, dedupe, and create horizontal strip.
    /// </summary>
    private async Task<(string? StripPath, int FrameCount)> CreateGifFrameStripAsync(
        string gifPath,
        CancellationToken ct)
    {
        using var image = await Image.LoadAsync<Rgba32>(gifPath, ct);

        if (image.Frames.Count < 2)
            return (null, 1);

        // Extract frames (sample up to 16 for speed)
        var maxFrames = Math.Min(16, image.Frames.Count);
        var step = Math.Max(1, image.Frames.Count / maxFrames);
        var frames = new List<Image<Rgba32>>();

        for (int i = 0; i < image.Frames.Count && frames.Count < maxFrames; i += step)
        {
            frames.Add(image.Frames.CloneFrame(i));
        }

        // Simple deduplication using pixel comparison (fast)
        var uniqueFrames = new List<Image<Rgba32>> { frames[0] };
        for (int i = 1; i < frames.Count; i++)
        {
            if (!AreFramesSimilar(uniqueFrames[^1], frames[i], 0.95))
            {
                uniqueFrames.Add(frames[i]);
            }
            else
            {
                frames[i].Dispose();
            }
        }

        // Limit to 8 frames for strip (optimal for readability)
        while (uniqueFrames.Count > 8)
        {
            var removeIdx = uniqueFrames.Count / 2;
            uniqueFrames[removeIdx].Dispose();
            uniqueFrames.RemoveAt(removeIdx);
        }

        if (uniqueFrames.Count < 2)
        {
            foreach (var f in uniqueFrames) f.Dispose();
            return (null, 1);
        }

        // Create horizontal strip
        var frameWidth = Math.Min(256, uniqueFrames[0].Width);
        var frameHeight = (int)(uniqueFrames[0].Height * ((double)frameWidth / uniqueFrames[0].Width));
        var stripWidth = frameWidth * uniqueFrames.Count;

        using var strip = new Image<Rgba32>(stripWidth, frameHeight);

        int xOffset = 0;
        foreach (var frame in uniqueFrames)
        {
            using var resized = frame.Clone();
            resized.Mutate(x => x.Resize(frameWidth, frameHeight));
            strip.Mutate(x => x.DrawImage(resized, new Point(xOffset, 0), 1f));
            xOffset += frameWidth;
            frame.Dispose();
        }

        // Save to temp file
        var tempPath = Path.Combine(Path.GetTempPath(), $"fast_strip_{Guid.NewGuid()}.png");
        await strip.SaveAsPngAsync(tempPath, ct);

        _logger?.LogDebug("Created frame strip: {FrameCount} unique frames at {Path}", uniqueFrames.Count, tempPath);

        return (tempPath, uniqueFrames.Count);
    }

    /// <summary>
    /// Fast similarity check between two frames using sampled pixels.
    /// </summary>
    private static bool AreFramesSimilar(Image<Rgba32> frame1, Image<Rgba32> frame2, double threshold)
    {
        if (frame1.Width != frame2.Width || frame1.Height != frame2.Height)
            return false;

        int sampleStep = Math.Max(1, Math.Min(frame1.Width, frame1.Height) / 16);
        int matchingPixels = 0;
        int totalSampled = 0;

        for (int y = 0; y < frame1.Height; y += sampleStep)
        {
            for (int x = 0; x < frame1.Width; x += sampleStep)
            {
                var p1 = frame1[x, y];
                var p2 = frame2[x, y];

                var diff = Math.Abs(p1.R - p2.R) + Math.Abs(p1.G - p2.G) + Math.Abs(p1.B - p2.B);
                if (diff < 30)
                    matchingPixels++;
                totalSampled++;
            }
        }

        return totalSampled > 0 && (double)matchingPixels / totalSampled >= threshold;
    }

    /// <summary>
    /// Clean caption response - remove prompt leakage and instruction text.
    /// </summary>
    private static string? CleanCaption(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
            return null;

        var result = caption.Trim();

        // Prompt leakage patterns to remove
        var leakagePatterns = new[]
        {
            "Based on the provided",
            "According to the",
            "Here is",
            "Here's",
            "The image shows",
            "This image shows",
            "In this image",
            "I can see",
            "Looking at",
            "Sure,",
            "Certainly,",
            "Based on",
            "According to"
        };

        foreach (var pattern in leakagePatterns)
        {
            if (result.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
            {
                var idx = result.IndexOf(',');
                if (idx > 0 && idx < 60)
                {
                    result = result[(idx + 1)..].TrimStart();
                }
                else
                {
                    // Try to find the actual content after common phrases
                    idx = result.IndexOf(':');
                    if (idx > 0 && idx < 80)
                    {
                        result = result[(idx + 1)..].TrimStart();
                    }
                }
            }
        }

        // Capitalize first letter
        if (result.Length > 0 && char.IsLower(result[0]))
        {
            result = char.ToUpper(result[0]) + result[1..];
        }

        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}

/// <summary>
/// Result from fast caption service
/// </summary>
public record FastCaptionResult(
    bool Success,
    string? Caption,
    string? Error,
    int FrameCount,
    string? Model);
