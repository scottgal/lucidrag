using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Florence2;

namespace Mostlylucid.DocSummarizer.Images.Services.Vision;

/// <summary>
/// Fast local captioning and OCR service using Florence-2 ONNX models.
/// Provides sub-second inference without requiring external services.
/// Compensates for Florence-2's weak color detection by enhancing captions with ColorWave signals.
/// Can be used as a "first pass" before escalating to more powerful vision LLMs.
/// </summary>
public class Florence2CaptionService
{
    private readonly ILogger<Florence2CaptionService>? _logger;
    private readonly ImageConfig _config;
    private readonly ColorAnalyzer _colorAnalyzer;
    private Florence2Model? _model;
    private readonly SemaphoreSlim _modelLock = new(1, 1);
    private bool _modelInitialized;
    private string? _modelsDirectory;

    public Florence2CaptionService(
        IOptions<ImageConfig> config,
        ColorAnalyzer colorAnalyzer,
        ILogger<Florence2CaptionService>? logger = null)
    {
        _config = config.Value;
        _colorAnalyzer = colorAnalyzer;
        _logger = logger;
        _modelsDirectory = Path.Combine(_config.ModelsDirectory, "florence2");
    }

    /// <summary>
    /// Check if Florence-2 models are available.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureModelLoadedAsync(ct);
            return _model != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get a fast caption for an image using Florence-2.
    /// Handles GIFs by creating frame strips automatically.
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <param name="detailed">If true, uses DETAILED_CAPTION task</param>
    /// <param name="enhanceWithColors">If true, enhances caption with accurate color signals</param>
    /// <param name="ct">Cancellation token</param>
    public async Task<Florence2CaptionResult> GetCaptionAsync(
        string imagePath,
        bool detailed = true,
        bool enhanceWithColors = true,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await EnsureModelLoadedAsync(ct);

            if (_model == null)
            {
                return new Florence2CaptionResult(
                    Success: false,
                    Caption: null,
                    OcrText: null,
                    Error: "Florence-2 model not loaded",
                    DurationMs: sw.ElapsedMilliseconds,
                    EnhancedWithColors: false);
            }

            var ext = Path.GetExtension(imagePath).ToLowerInvariant();

            // Handle GIFs with frame strip
            if (ext == ".gif")
            {
                return await GetGifCaptionAsync(imagePath, detailed, enhanceWithColors, sw, ct);
            }

            // Static image
            return await GetStaticCaptionAsync(imagePath, detailed, enhanceWithColors, sw, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Florence-2 captioning failed for {Path}", imagePath);
            return new Florence2CaptionResult(
                Success: false,
                Caption: null,
                OcrText: null,
                Error: ex.Message,
                DurationMs: sw.ElapsedMilliseconds,
                EnhancedWithColors: false);
        }
    }

    /// <summary>
    /// Extract text from an image using Florence-2 OCR.
    /// Faster than Tesseract but less accurate for complex layouts.
    /// Good as a "first pass" before escalating to full OCR.
    /// </summary>
    public async Task<Florence2OcrResult> ExtractTextAsync(
        string imagePath,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await EnsureModelLoadedAsync(ct);

            if (_model == null)
            {
                return new Florence2OcrResult(
                    Success: false,
                    Text: null,
                    Error: "Florence-2 model not loaded",
                    DurationMs: sw.ElapsedMilliseconds);
            }

            using var imgStream = File.OpenRead(imagePath);
            var streams = new Stream[] { imgStream };

            // Use OCR task
            var results = _model.Run(TaskTypes.OCR, streams, textInput: null, cancellationToken: ct);
            var text = ExtractText(results);

            return new Florence2OcrResult(
                Success: !string.IsNullOrWhiteSpace(text),
                Text: text?.Trim(),
                Error: string.IsNullOrWhiteSpace(text) ? "No text detected" : null,
                DurationMs: sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Florence-2 OCR failed for {Path}", imagePath);
            return new Florence2OcrResult(
                Success: false,
                Text: null,
                Error: ex.Message,
                DurationMs: sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Run combined caption + OCR analysis in a single pass.
    /// Useful as a quick "first pass" before full wave pipeline.
    /// </summary>
    public async Task<Florence2AnalysisResult> AnalyzeAsync(
        string imagePath,
        bool enhanceWithColors = true,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await EnsureModelLoadedAsync(ct);

            if (_model == null)
            {
                return new Florence2AnalysisResult(
                    Success: false,
                    Caption: null,
                    OcrText: null,
                    Error: "Florence-2 model not loaded",
                    DurationMs: sw.ElapsedMilliseconds,
                    EnhancedWithColors: false);
            }

            // Get caption
            using var imgStream1 = File.OpenRead(imagePath);
            var captionResult = _model.Run(TaskTypes.DETAILED_CAPTION, new[] { imgStream1 }, textInput: null, cancellationToken: ct);
            var caption = ExtractText(captionResult);

            // Get OCR text
            using var imgStream2 = File.OpenRead(imagePath);
            var ocrResult = _model.Run(TaskTypes.OCR, new[] { imgStream2 }, textInput: null, cancellationToken: ct);
            var ocrText = ExtractText(ocrResult);

            // Enhance caption with colors
            string? enhancedCaption = caption;
            bool wasEnhanced = false;

            if (enhanceWithColors && !string.IsNullOrWhiteSpace(caption))
            {
                var colorResult = await EnhanceCaptionWithColorsAsync(imagePath, caption, ct);
                enhancedCaption = colorResult.caption;
                wasEnhanced = colorResult.enhanced;
            }

            return new Florence2AnalysisResult(
                Success: !string.IsNullOrWhiteSpace(enhancedCaption) || !string.IsNullOrWhiteSpace(ocrText),
                Caption: CleanCaption(enhancedCaption),
                OcrText: ocrText?.Trim(),
                Error: null,
                DurationMs: sw.ElapsedMilliseconds,
                EnhancedWithColors: wasEnhanced);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Florence-2 analysis failed for {Path}", imagePath);
            return new Florence2AnalysisResult(
                Success: false,
                Caption: null,
                OcrText: null,
                Error: ex.Message,
                DurationMs: sw.ElapsedMilliseconds,
                EnhancedWithColors: false);
        }
    }

    private async Task<Florence2CaptionResult> GetStaticCaptionAsync(
        string imagePath,
        bool detailed,
        bool enhanceWithColors,
        System.Diagnostics.Stopwatch sw,
        CancellationToken ct)
    {
        using var imgStream = File.OpenRead(imagePath);
        var streams = new Stream[] { imgStream };

        // Get caption from Florence-2
        var task = detailed ? TaskTypes.DETAILED_CAPTION : TaskTypes.CAPTION;
        var results = _model!.Run(task, streams, textInput: null, cancellationToken: ct);
        var caption = ExtractText(results);

        // Also get OCR text quickly
        string? ocrText = null;
        try
        {
            using var imgStream2 = File.OpenRead(imagePath);
            var ocrResults = _model.Run(TaskTypes.OCR, new[] { imgStream2 }, textInput: null, cancellationToken: ct);
            ocrText = ExtractText(ocrResults)?.Trim();
        }
        catch { /* OCR is optional */ }

        if (string.IsNullOrWhiteSpace(caption))
        {
            return new Florence2CaptionResult(
                Success: false,
                Caption: null,
                OcrText: ocrText,
                Error: "No caption generated",
                DurationMs: sw.ElapsedMilliseconds,
                EnhancedWithColors: false);
        }

        // Enhance with color signals if requested
        string? enhancedCaption = caption;
        bool wasEnhanced = false;

        if (enhanceWithColors)
        {
            var colorResult = await EnhanceCaptionWithColorsAsync(imagePath, caption, ct);
            enhancedCaption = colorResult.caption;
            wasEnhanced = colorResult.enhanced;
        }

        return new Florence2CaptionResult(
            Success: true,
            Caption: CleanCaption(enhancedCaption),
            OcrText: ocrText,
            Error: null,
            DurationMs: sw.ElapsedMilliseconds,
            EnhancedWithColors: wasEnhanced);
    }

    private async Task<Florence2CaptionResult> GetGifCaptionAsync(
        string gifPath,
        bool detailed,
        bool enhanceWithColors,
        System.Diagnostics.Stopwatch sw,
        CancellationToken ct)
    {
        try
        {
            // Create frame strip for GIF
            var (stripPath, frameCount) = await CreateGifFrameStripAsync(gifPath, ct);

            if (stripPath == null || frameCount < 2)
            {
                // Single frame GIF - treat as static
                return await GetStaticCaptionAsync(gifPath, detailed, enhanceWithColors, sw, ct);
            }

            try
            {
                using var imgStream = File.OpenRead(stripPath);
                var streams = new Stream[] { imgStream };

                // Get caption
                var task = detailed ? TaskTypes.DETAILED_CAPTION : TaskTypes.CAPTION;
                var results = _model!.Run(task, streams, textInput: null, cancellationToken: ct);
                var caption = ExtractText(results);

                // Also get OCR from strip (may capture subtitles)
                string? ocrText = null;
                try
                {
                    using var imgStream2 = File.OpenRead(stripPath);
                    var ocrResults = _model.Run(TaskTypes.OCR, new[] { imgStream2 }, textInput: null, cancellationToken: ct);
                    ocrText = ExtractText(ocrResults)?.Trim();
                }
                catch { /* OCR is optional */ }

                // Add GIF context
                if (!string.IsNullOrWhiteSpace(caption))
                {
                    caption = $"Animated GIF ({frameCount} frames): {caption}";
                }

                // Enhance with colors
                string? enhancedCaption = caption;
                bool wasEnhanced = false;

                if (enhanceWithColors && !string.IsNullOrWhiteSpace(caption))
                {
                    var colorResult = await EnhanceCaptionWithColorsAsync(gifPath, caption, ct);
                    enhancedCaption = colorResult.caption;
                    wasEnhanced = colorResult.enhanced;
                }

                return new Florence2CaptionResult(
                    Success: !string.IsNullOrWhiteSpace(enhancedCaption),
                    Caption: CleanCaption(enhancedCaption),
                    OcrText: ocrText,
                    Error: string.IsNullOrWhiteSpace(enhancedCaption) ? "No caption generated" : null,
                    DurationMs: sw.ElapsedMilliseconds,
                    EnhancedWithColors: wasEnhanced,
                    FrameCount: frameCount);
            }
            finally
            {
                // Clean up temp strip
                if (File.Exists(stripPath))
                {
                    try { File.Delete(stripPath); } catch { /* ignore */ }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GIF frame strip creation failed, falling back to first frame");
            return await GetStaticCaptionAsync(gifPath, detailed, enhanceWithColors, sw, ct);
        }
    }

    /// <summary>
    /// Enhance caption with accurate color information from ColorWave.
    /// Florence-2 is weak at color detection, so we compensate with our deterministic color analyzer.
    /// </summary>
    private async Task<(string caption, bool enhanced)> EnhanceCaptionWithColorsAsync(
        string imagePath,
        string baseCaption,
        CancellationToken ct)
    {
        try
        {
            // Load image for color analysis
            using var image = await Image.LoadAsync<Rgba32>(imagePath, ct);

            // Get dominant colors using ColorAnalyzer
            var dominantColors = _colorAnalyzer.ExtractDominantColors(image, maxColors: 5);

            if (dominantColors == null || dominantColors.Count == 0)
            {
                return (baseCaption, false);
            }

            // Get top 2-3 significant colors
            var topColors = dominantColors
                .Take(3)
                .Where(c => c.Percentage > 10) // Only significant colors
                .Select(c => c.Name.ToLowerInvariant())
                .ToList();

            if (topColors.Count == 0)
            {
                return (baseCaption, false);
            }

            // Check if caption already mentions these colors
            var captionLower = baseCaption.ToLowerInvariant();
            var missingColors = topColors
                .Where(c => !captionLower.Contains(c) && !IsColorSynonymPresent(captionLower, c))
                .ToList();

            if (missingColors.Count == 0)
            {
                // Colors already mentioned
                return (baseCaption, false);
            }

            // Build color prefix
            var colorPhrase = missingColors.Count switch
            {
                1 => $"[{missingColors[0]} tones] ",
                2 => $"[{missingColors[0]} and {missingColors[1]} tones] ",
                _ => $"[{string.Join(", ", missingColors.Take(2))} tones] "
            };

            return (colorPhrase + baseCaption, true);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Color enhancement failed, using base caption");
            return (baseCaption, false);
        }
    }

    /// <summary>
    /// Check if a color synonym is present (e.g., "crimson" for "red")
    /// </summary>
    private static bool IsColorSynonymPresent(string text, string color)
    {
        var synonyms = color.ToLowerInvariant() switch
        {
            "red" => new[] { "crimson", "scarlet", "maroon", "ruby" },
            "blue" => new[] { "azure", "navy", "cobalt", "sapphire", "cyan" },
            "green" => new[] { "emerald", "jade", "olive", "lime", "forest" },
            "yellow" => new[] { "gold", "golden", "amber", "lemon" },
            "orange" => new[] { "tangerine", "peach", "coral" },
            "purple" => new[] { "violet", "lavender", "plum", "magenta" },
            "pink" => new[] { "rose", "fuchsia", "salmon" },
            "brown" => new[] { "tan", "beige", "chocolate", "coffee", "chestnut" },
            "gray" or "grey" => new[] { "silver", "charcoal", "slate" },
            "white" => new[] { "cream", "ivory", "snow" },
            "black" => new[] { "ebony", "onyx", "jet" },
            _ => Array.Empty<string>()
        };

        return synonyms.Any(s => text.Contains(s));
    }

    private string? ExtractText(object results)
    {
        // Florence2 returns different types based on task
        if (results is string str)
        {
            return str;
        }

        // Try to get text property via reflection or dynamic
        try
        {
            var type = results.GetType();

            // Try common text property names (Florence2 uses PureText for caption/OCR results)
            var propertyNames = new[] { "PureText", "Text", "text", "Caption", "caption", "Description", "description", "Content", "content" };
            foreach (var propName in propertyNames)
            {
                var prop = type.GetProperty(propName);
                if (prop != null)
                {
                    var value = prop.GetValue(results)?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            // If it's a collection, extract text from each item and combine
            if (results is System.Collections.IEnumerable enumerable)
            {
                var texts = new List<string>();
                var itemCount = 0;
                foreach (var item in enumerable)
                {
                    itemCount++;
                    if (item is string s && !string.IsNullOrWhiteSpace(s))
                    {
                        texts.Add(s);
                        continue;
                    }

                    var itemType = item.GetType();

                    // Log element type on first item for debugging
                    if (itemCount == 1)
                    {
                        _logger?.LogDebug("Florence2 collection element type: {Type}, properties: {Props}",
                            itemType.FullName,
                            string.Join(", ", itemType.GetProperties().Select(p => $"{p.Name}:{p.PropertyType.Name}")));
                    }

                    foreach (var propName in propertyNames)
                    {
                        var itemProp = itemType.GetProperty(propName);
                        if (itemProp != null)
                        {
                            var value = itemProp.GetValue(item)?.ToString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                texts.Add(value);
                                break;
                            }
                        }
                    }
                }

                _logger?.LogDebug("Florence2 collection had {Count} items, extracted {TextCount} text values",
                    itemCount, texts.Count);

                if (texts.Count > 0)
                {
                    return string.Join(" ", texts);
                }
            }

            // Last resort - ToString
            var toString = results.ToString();
            var typeName = type.Name;
            var fullTypeName = type.FullName;
            // Avoid returning unhelpful type strings like "FlorenceResults[]" or "Florence2.FlorenceResults[]"
            if (!string.IsNullOrWhiteSpace(toString)
                && toString != typeName
                && !toString.EndsWith(typeName)
                && fullTypeName != null && !toString.Contains(fullTypeName)
                && !toString.Contains("[]")  // Avoid array type names
                && !toString.StartsWith("System."))  // Avoid system type names
            {
                return toString;
            }

            // Debug: Log property names to help diagnose
            _logger?.LogDebug("Florence2 result type: {Type}, properties: {Props}",
                type.FullName,
                string.Join(", ", type.GetProperties().Select(p => $"{p.Name}:{p.PropertyType.Name}")));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Error extracting text from Florence2 result");
        }

        return null;
    }

    private async Task EnsureModelLoadedAsync(CancellationToken ct)
    {
        if (_modelInitialized) return;

        await _modelLock.WaitAsync(ct);
        try
        {
            if (_modelInitialized) return;

            _logger?.LogInformation("Loading Florence-2 models from {Path}", _modelsDirectory);

            // Ensure directory exists
            Directory.CreateDirectory(_modelsDirectory!);

            // Download models if needed (with status callback)
            var modelSource = new FlorenceModelDownloader(_modelsDirectory!);
            await modelSource.DownloadModelsAsync(
                onStatusUpdate: status => _logger?.LogDebug("Florence-2 download: {Status}", status),
                logger: null,
                ct: ct);

            // Load model
            _model = new Florence2Model(modelSource);
            _modelInitialized = true;

            _logger?.LogInformation("Florence-2 models loaded successfully");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load Florence-2 models");
            throw;
        }
        finally
        {
            _modelLock.Release();
        }
    }

    /// <summary>
    /// Create frame strip from GIF for Florence-2 to analyze.
    /// </summary>
    private async Task<(string? StripPath, int FrameCount)> CreateGifFrameStripAsync(
        string gifPath,
        CancellationToken ct)
    {
        using var image = await Image.LoadAsync<Rgba32>(gifPath, ct);

        if (image.Frames.Count < 2)
            return (null, 1);

        // Extract frames (sample up to 8 for Florence-2)
        var maxFrames = Math.Min(8, image.Frames.Count);
        var step = Math.Max(1, image.Frames.Count / maxFrames);
        var frames = new List<Image<Rgba32>>();

        for (int i = 0; i < image.Frames.Count && frames.Count < maxFrames; i += step)
        {
            frames.Add(image.Frames.CloneFrame(i));
        }

        // Simple deduplication
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

        // Save to temp
        var tempPath = Path.Combine(Path.GetTempPath(), $"florence2_strip_{Guid.NewGuid()}.png");
        await strip.SaveAsPngAsync(tempPath, ct);

        return (tempPath, uniqueFrames.Count);
    }

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

    private static string? CleanCaption(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
            return null;

        var result = caption.Trim();

        // Remove common Florence-2 artifacts
        var artifacts = new[]
        {
            "The image shows",
            "This image shows",
            "In this image,",
            "The picture shows",
        };

        foreach (var artifact in artifacts)
        {
            if (result.StartsWith(artifact, StringComparison.OrdinalIgnoreCase))
            {
                result = result[artifact.Length..].TrimStart(' ', ',');
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
/// Result from Florence-2 caption service
/// </summary>
public record Florence2CaptionResult(
    bool Success,
    string? Caption,
    string? OcrText,
    string? Error,
    long DurationMs,
    bool EnhancedWithColors,
    int FrameCount = 1);

/// <summary>
/// Result from Florence-2 OCR
/// </summary>
public record Florence2OcrResult(
    bool Success,
    string? Text,
    string? Error,
    long DurationMs);

/// <summary>
/// Combined analysis result from Florence-2
/// </summary>
public record Florence2AnalysisResult(
    bool Success,
    string? Caption,
    string? OcrText,
    string? Error,
    long DurationMs,
    bool EnhancedWithColors);
