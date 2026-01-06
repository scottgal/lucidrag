using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Vision;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// ML-based OCR Wave - Uses OpenCV + Florence-2 for fast text detection before Tesseract.
/// Priority: 28 (runs BEFORE OcrWave at 30)
///
/// Pipeline:
/// 1. OpenCV MSER detection (~5-20ms) - quick check for text-like regions
/// 2. If no text regions found, skip OCR entirely
/// 3. If text regions found and Florence-2 available, use for fast OCR (~1-2s)
/// 4. Based on results, signal whether to run Tesseract for accuracy
/// 5. For GIFs, detect which frames have text changes using OpenCV
/// </summary>
public class MlOcrWave : IAnalysisWave
{
    private readonly Florence2CaptionService? _florence2;
    private readonly OpenCvTextDetector _opencvDetector;
    private readonly TextRegionChangeDetector _textChangeDetector;
    private readonly ImageConfig _config;
    private readonly ILogger<MlOcrWave>? _logger;

    public string Name => "MlOcrWave";
    public int Priority => 28; // Before OcrWave (30), runs early for fast detection
    public IReadOnlyList<string> Tags => new[] { SignalTags.Content, "ocr", "ml", "text" };

    // Minimum text length to consider "meaningful"
    private const int MinMeaningfulTextLength = 3;

    /// <summary>
    /// Check if this wave should run.
    /// MlOcrWave runs on ALL routes (provides fast Florence-2 OCR for captions).
    /// Only skip if OpenCV detection was already done in AutoRoutingWave.
    /// </summary>
    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        // MlOcrWave should always run - it's the FAST path for OCR
        // It provides Florence-2 results even when Tesseract is skipped

        // However, if AutoRoutingWave already did OpenCV detection, we can reuse those results
        var existingRegions = context.GetCached<List<Dictionary<string, int>>>("ocr.opencv.text_regions");
        if (existingRegions != null)
        {
            _logger?.LogDebug("MlOcrWave: Reusing OpenCV detection from AutoRoutingWave ({Count} regions)", existingRegions.Count);
        }

        return true;
    }

    public MlOcrWave(
        Florence2CaptionService? florence2,
        IOptions<ImageConfig> config,
        ILogger<MlOcrWave>? logger = null)
    {
        _florence2 = florence2;
        _config = config.Value;
        _opencvDetector = new OpenCvTextDetector(logger as ILogger<OpenCvTextDetector>);
        _textChangeDetector = new TextRegionChangeDetector(logger as ILogger<TextRegionChangeDetector>);
        _logger = logger;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        // Check if Florence-2 is available
        if (_florence2 == null)
        {
            _logger?.LogDebug("Florence-2 not available, skipping MlOcrWave");
            signals.Add(new Signal
            {
                Key = "ocr.ml.skipped",
                Value = true,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr", "ml" },
                Metadata = new Dictionary<string, object> { ["reason"] = "florence2_not_available" }
            });
            return signals;
        }

        // STEP 1: Fast OpenCV MSER detection
        // OPTIMIZATION: Reuse cached results from AutoRoutingWave if available
        OpenCvTextDetector.TextDetectionResult opencvResult;
        var cachedRegions = context.GetCached<List<Dictionary<string, int>>>("ocr.opencv.text_regions");

        if (cachedRegions != null)
        {
            // Reuse AutoRoutingWave detection - saves ~5-20ms
            var textCoverage = context.GetValue<double>("route.text_detection.coverage");
            opencvResult = new OpenCvTextDetector.TextDetectionResult
            {
                HasTextLikeRegions = cachedRegions.Count > 0,
                TextRegionCount = cachedRegions.Count,
                TextAreaRatio = textCoverage,
                DetectionTimeMs = 0, // Already done
                Confidence = 0.8,
                TextBoundingBoxes = cachedRegions.Select(r => new OpenCvSharp.Rect(
                    r.GetValueOrDefault("x", 0),
                    r.GetValueOrDefault("y", 0),
                    r.GetValueOrDefault("width", 0),
                    r.GetValueOrDefault("height", 0)
                )).ToList()
            };
            _logger?.LogDebug("MlOcrWave: Reused OpenCV detection from AutoRoutingWave ({Regions} regions)", cachedRegions.Count);
        }
        else
        {
            // Run fresh OpenCV detection (~5-20ms)
            opencvResult = _opencvDetector.DetectTextRegions(imagePath);
        }

        signals.Add(new Signal
        {
            Key = "ocr.opencv.has_text",
            Value = opencvResult.HasTextLikeRegions,
            Confidence = opencvResult.Confidence,
            Source = Name,
            Tags = new List<string> { "ocr", "opencv" },
            Metadata = new Dictionary<string, object>
            {
                ["region_count"] = opencvResult.TextRegionCount,
                ["text_area_ratio"] = opencvResult.TextAreaRatio,
                ["detection_time_ms"] = opencvResult.DetectionTimeMs,
                ["reused_from_routing"] = cachedRegions != null
            }
        });

        // Cache bounding boxes for downstream OCR waves (Tesseract, etc.)
        if (opencvResult.TextBoundingBoxes.Count > 0)
        {
            var boxesData = cachedRegions ?? opencvResult.TextBoundingBoxes.Select(b => new Dictionary<string, int>
            {
                ["x"] = b.X,
                ["y"] = b.Y,
                ["width"] = b.Width,
                ["height"] = b.Height
            }).ToList();

            if (cachedRegions == null)
            {
                context.SetCached("ocr.opencv.text_regions", boxesData);
            }

            signals.Add(new Signal
            {
                Key = "ocr.opencv.text_regions",
                Value = opencvResult.TextRegionCount,
                Confidence = opencvResult.Confidence,
                Source = Name,
                Tags = new List<string> { "ocr", "opencv", "regions" },
                Metadata = new Dictionary<string, object>
                {
                    ["boxes"] = boxesData
                }
            });
        }

        // Check for subtitle region specifically (even faster ~2-5ms)
        var hasSubtitles = _opencvDetector.HasSubtitleRegion(imagePath);
        if (hasSubtitles)
        {
            signals.Add(new Signal
            {
                Key = "ocr.opencv.has_subtitles",
                Value = true,
                Confidence = 0.8,
                Source = Name,
                Tags = new List<string> { "ocr", "opencv", "subtitles" }
            });
        }

        // STEP 2: Check text likeliness threshold combined with OpenCV result
        var textLikeliness = context.GetValue<double>("content.text_likeliness");

        // Skip if BOTH OpenCV and text likeliness indicate no text
        if (!opencvResult.HasTextLikeRegions && textLikeliness < 0.1)
        {
            _logger?.LogDebug("OpenCV ({Regions} regions) and text likeliness {Score:F3} both indicate no text, skipping ML OCR",
                opencvResult.TextRegionCount, textLikeliness);
            signals.Add(new Signal
            {
                Key = "ocr.ml.skipped",
                Value = true,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr", "ml" },
                Metadata = new Dictionary<string, object>
                {
                    ["reason"] = "no_text_detected",
                    ["text_likeliness"] = textLikeliness,
                    ["opencv_regions"] = opencvResult.TextRegionCount
                }
            });

            // Signal to skip heavy Tesseract OCR
            signals.Add(new Signal
            {
                Key = "ocr.escalation.skip_tesseract",
                Value = true,
                Confidence = 0.95, // High confidence when both methods agree
                Source = Name,
                Tags = new List<string> { "ocr", "escalation" }
            });

            return signals;
        }

        try
        {
            var isAnimated = context.GetValue<bool>("identity.is_animated");
            var frameCount = context.GetValue<int>("identity.frame_count");

            string? mlText = null;
            int framesWithText = 0;
            List<int>? textChangedFrameIndices = null;

            if (isAnimated && frameCount > 1)
            {
                // For GIFs, analyze text changes across frames with OpenCV + Florence-2
                var result = await AnalyzeAnimatedTextWithOpenCvAsync(imagePath, frameCount, ct);
                mlText = result.Text;
                framesWithText = result.FramesWithText;
                textChangedFrameIndices = result.TextChangedFrameIndices;

                signals.Add(new Signal
                {
                    Key = "ocr.ml.frames_with_text",
                    Value = framesWithText,
                    Confidence = 0.85,
                    Source = Name,
                    Tags = new List<string> { "ocr", "ml", "animation" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["total_frames"] = frameCount,
                        ["text_changed_frames"] = textChangedFrameIndices?.Count ?? 0,
                        ["frames_with_text_regions"] = result.FramesWithTextRegions
                    }
                });

                // Cache data for downstream OCR waves
                if (textChangedFrameIndices != null && textChangedFrameIndices.Count > 0)
                {
                    context.SetCached("ocr.ml.text_changed_indices", textChangedFrameIndices);
                }

                // Cache per-frame text regions for targeted Tesseract OCR
                if (result.PerFrameTextRegions != null && result.PerFrameTextRegions.Count > 0)
                {
                    context.SetCached("ocr.opencv.per_frame_regions", result.PerFrameTextRegions);
                }
            }
            else
            {
                // Static image - use OpenCV regions if available for targeted OCR
                if (opencvResult.TextBoundingBoxes.Count > 0 && _florence2 != null)
                {
                    // Extract text from detected regions
                    mlText = await ExtractTextFromRegionsAsync(imagePath, opencvResult.TextBoundingBoxes, ct);
                }
                else
                {
                    // Fallback to full image
                    var result = await _florence2.ExtractTextAsync(imagePath, ct);
                    mlText = result.Text;
                }
            }

            // Emit ML OCR result
            var hasText = !string.IsNullOrWhiteSpace(mlText) && mlText.Length >= MinMeaningfulTextLength;

            signals.Add(new Signal
            {
                Key = "ocr.ml.text",
                Value = mlText ?? "",
                Confidence = hasText ? 0.75 : 0.5, // Florence-2 OCR is less accurate than Tesseract
                Source = Name,
                Tags = new List<string> { "ocr", "ml", "text" },
                Metadata = new Dictionary<string, object>
                {
                    ["has_meaningful_text"] = hasText,
                    ["text_length"] = mlText?.Length ?? 0
                }
            });

            signals.Add(new Signal
            {
                Key = "ocr.ml.has_text",
                Value = hasText,
                Confidence = 0.85,
                Source = Name,
                Tags = new List<string> { "ocr", "ml" }
            });

            // Decide whether to escalate to Tesseract
            var shouldEscalate = hasText && (
                textLikeliness > 0.3 ||           // High text probability
                (mlText?.Length ?? 0) > 10 ||     // Substantial text found
                framesWithText > 1                 // Multiple frames have text
            );

            signals.Add(new Signal
            {
                Key = "ocr.escalation.run_tesseract",
                Value = shouldEscalate,
                Confidence = 0.85,
                Source = Name,
                Tags = new List<string> { "ocr", "escalation" },
                Metadata = new Dictionary<string, object>
                {
                    ["reason"] = shouldEscalate
                        ? "meaningful_text_detected"
                        : hasText ? "minimal_text" : "no_text_found",
                    ["ml_text_preview"] = mlText?.Substring(0, Math.Min(50, mlText?.Length ?? 0)) ?? ""
                }
            });

            _logger?.LogInformation(
                "ML OCR: hasText={HasText}, length={Length}, escalate={Escalate}",
                hasText, mlText?.Length ?? 0, shouldEscalate);

            return signals;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ML OCR failed, will fall back to Tesseract");

            signals.Add(new Signal
            {
                Key = "ocr.ml.error",
                Value = ex.Message,
                Confidence = 0,
                Source = Name,
                Tags = new List<string> { "ocr", "ml", "error" }
            });

            // On error, default to running Tesseract
            signals.Add(new Signal
            {
                Key = "ocr.escalation.run_tesseract",
                Value = true,
                Confidence = 0.5,
                Source = Name,
                Tags = new List<string> { "ocr", "escalation" },
                Metadata = new Dictionary<string, object> { ["reason"] = "ml_ocr_error" }
            });

            return signals;
        }
    }

    /// <summary>
    /// Result of animated text analysis with OpenCV.
    /// </summary>
    private record AnimatedTextResult(
        string? Text,
        int FramesWithText,
        List<int>? TextChangedFrameIndices,
        int FramesWithTextRegions,
        Dictionary<int, List<Dictionary<string, int>>>? PerFrameTextRegions);

    /// <summary>
    /// Analyzes text in animated images using OpenCV per-frame + text change detection.
    /// </summary>
    private async Task<AnimatedTextResult> AnalyzeAnimatedTextWithOpenCvAsync(
        string imagePath, int frameCount, CancellationToken ct)
    {
        try
        {
            // Extract key frames for analysis
            var maxFramesToAnalyze = Math.Min(8, frameCount);
            var frames = await ExtractKeyFramesAsync(imagePath, maxFramesToAnalyze, ct);

            if (frames.Count == 0)
            {
                return new AnimatedTextResult(null, 0, null, 0, null);
            }

            // STEP 1: Run OpenCV text detection on each frame (~5-20ms per frame)
            var perFrameRegions = new Dictionary<int, List<Dictionary<string, int>>>();
            var framesWithTextRegions = 0;

            for (int i = 0; i < frames.Count; i++)
            {
                var result = _opencvDetector.DetectTextRegions(frames[i]);
                if (result.HasTextLikeRegions)
                {
                    framesWithTextRegions++;
                    perFrameRegions[i] = result.TextBoundingBoxes.Select(b => new Dictionary<string, int>
                    {
                        ["x"] = b.X,
                        ["y"] = b.Y,
                        ["width"] = b.Width,
                        ["height"] = b.Height
                    }).ToList();
                }
            }

            _logger?.LogDebug("OpenCV detected text in {Count}/{Total} frames",
                framesWithTextRegions, frames.Count);

            // If no frames have text regions, skip OCR entirely
            if (framesWithTextRegions == 0)
            {
                foreach (var frame in frames)
                    frame.Dispose();
                return new AnimatedTextResult(null, 0, null, 0, null);
            }

            // STEP 2: Use TextRegionChangeDetector to find frames where text CHANGED
            var textChangedIndices = _textChangeDetector.GetTextChangedFrameIndices(frames);

            // STEP 3: Only OCR frames that have text AND where text changed
            var framesToOcr = textChangedIndices
                .Where(idx => perFrameRegions.ContainsKey(idx))
                .Take(4) // Limit to 4 frames for speed
                .ToList();

            var textsFound = new List<string>();

            foreach (var idx in framesToOcr)
            {
                if (idx < frames.Count)
                {
                    var frame = frames[idx];
                    var regions = perFrameRegions.GetValueOrDefault(idx);

                    // Extract text from specific regions if available
                    var text = regions != null && regions.Count > 0
                        ? await ExtractTextFromFrameRegionsAsync(frame, regions, ct)
                        : await ExtractTextFromFrameAsync(frame, ct);

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        textsFound.Add(text);
                    }
                }
            }

            // Cleanup
            foreach (var frame in frames)
            {
                frame.Dispose();
            }

            // Combine unique texts
            var uniqueTexts = textsFound.Distinct().ToList();
            var combinedText = string.Join("\n", uniqueTexts);

            return new AnimatedTextResult(
                combinedText,
                uniqueTexts.Count,
                textChangedIndices,
                framesWithTextRegions,
                perFrameRegions);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to analyze animated text with OpenCV");
            return new AnimatedTextResult(null, 0, null, 0, null);
        }
    }

    private async Task<List<Image<Rgba32>>> ExtractKeyFramesAsync(string imagePath, int maxFrames, CancellationToken ct)
    {
        var frames = new List<Image<Rgba32>>();

        try
        {
            using var gif = await Image.LoadAsync(imagePath, ct);
            var totalFrames = gif.Frames.Count;

            if (totalFrames <= maxFrames)
            {
                // Extract all frames
                for (int i = 0; i < totalFrames; i++)
                {
                    using var genericFrame = gif.Frames.CloneFrame(i);
                    frames.Add(genericFrame.CloneAs<Rgba32>());
                }
            }
            else
            {
                // Extract evenly spaced frames
                var step = (double)(totalFrames - 1) / (maxFrames - 1);
                for (int i = 0; i < maxFrames; i++)
                {
                    var frameIdx = (int)(i * step);
                    using var genericFrame = gif.Frames.CloneFrame(frameIdx);
                    frames.Add(genericFrame.CloneAs<Rgba32>());
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to extract frames from {Path}", imagePath);
        }

        return frames;
    }

    private async Task<string?> ExtractTextFromFrameAsync(Image<Rgba32> frame, CancellationToken ct)
    {
        // Save frame to temp file for Florence-2
        var tempPath = Path.Combine(Path.GetTempPath(), $"mlocr_{Guid.NewGuid()}.png");

        try
        {
            await frame.SaveAsPngAsync(tempPath, ct);
            var result = await _florence2!.ExtractTextAsync(tempPath, ct);
            return result.Text;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    /// <summary>
    /// Extracts text from specific OpenCV-detected regions in a static image.
    /// More accurate than full-image OCR as it focuses on text areas only.
    /// </summary>
    private async Task<string?> ExtractTextFromRegionsAsync(
        string imagePath,
        IReadOnlyList<OpenCvSharp.Rect> regions,
        CancellationToken ct)
    {
        try
        {
            using var image = await Image.LoadAsync<Rgba32>(imagePath, ct);
            return await ExtractTextFromImageRegionsAsync(image, regions, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to extract text from regions, falling back to full image");
            var result = await _florence2!.ExtractTextAsync(imagePath, ct);
            return result.Text;
        }
    }

    /// <summary>
    /// Extracts text from specific regions in a frame.
    /// </summary>
    private async Task<string?> ExtractTextFromFrameRegionsAsync(
        Image<Rgba32> frame,
        List<Dictionary<string, int>> regions,
        CancellationToken ct)
    {
        try
        {
            // Convert dictionary regions back to Rect
            var rects = regions.Select(r => new OpenCvSharp.Rect(
                r["x"], r["y"], r["width"], r["height"]
            )).ToList();

            return await ExtractTextFromImageRegionsAsync(frame, rects, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to extract text from frame regions, falling back to full frame");
            return await ExtractTextFromFrameAsync(frame, ct);
        }
    }

    /// <summary>
    /// Core method that extracts text from cropped regions of an image.
    /// </summary>
    private async Task<string?> ExtractTextFromImageRegionsAsync(
        Image<Rgba32> image,
        IReadOnlyList<OpenCvSharp.Rect> regions,
        CancellationToken ct)
    {
        var textsFound = new List<string>();
        var tempPaths = new List<string>();

        try
        {
            // Merge nearby regions for efficiency
            var mergedRegions = MergeNearbyRegions(regions, image.Width, image.Height);

            foreach (var rect in mergedRegions.Take(5)) // Limit to 5 regions
            {
                // Clamp to image bounds with some padding
                var padding = 5;
                var x = Math.Max(0, rect.X - padding);
                var y = Math.Max(0, rect.Y - padding);
                var width = Math.Min(image.Width - x, rect.Width + padding * 2);
                var height = Math.Min(image.Height - y, rect.Height + padding * 2);

                if (width < 10 || height < 10) continue; // Skip tiny regions

                // Crop the region
                var cropRect = new Rectangle(x, y, width, height);
                using var cropped = image.Clone(ctx => ctx.Crop(cropRect));

                // Save to temp file
                var tempPath = Path.Combine(Path.GetTempPath(), $"mlocr_region_{Guid.NewGuid()}.png");
                tempPaths.Add(tempPath);
                await cropped.SaveAsPngAsync(tempPath, ct);

                // OCR the cropped region
                var ocrResult = await _florence2!.ExtractTextAsync(tempPath, ct);
                if (ocrResult.Success && !string.IsNullOrWhiteSpace(ocrResult.Text))
                {
                    textsFound.Add(ocrResult.Text.Trim());
                }
            }

            return textsFound.Count > 0 ? string.Join(" ", textsFound.Distinct()) : null;
        }
        finally
        {
            // Cleanup temp files
            foreach (var path in tempPaths)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }
    }

    /// <summary>
    /// Merges nearby text regions to reduce OCR calls.
    /// </summary>
    private List<OpenCvSharp.Rect> MergeNearbyRegions(
        IReadOnlyList<OpenCvSharp.Rect> regions,
        int imageWidth,
        int imageHeight)
    {
        if (regions.Count <= 1) return regions.ToList();

        var merged = new List<OpenCvSharp.Rect>();
        var used = new bool[regions.Count];
        var proximity = Math.Min(imageWidth, imageHeight) / 10; // 10% of smaller dimension

        for (int i = 0; i < regions.Count; i++)
        {
            if (used[i]) continue;

            var current = regions[i];

            for (int j = i + 1; j < regions.Count; j++)
            {
                if (used[j]) continue;

                // Check if regions are close enough to merge
                var other = regions[j];
                var horizontalDist = Math.Max(0, Math.Max(current.Left, other.Left) - Math.Min(current.Right, other.Right));
                var verticalDist = Math.Max(0, Math.Max(current.Top, other.Top) - Math.Min(current.Bottom, other.Bottom));

                if (horizontalDist < proximity && verticalDist < proximity)
                {
                    // Merge
                    var left = Math.Min(current.Left, other.Left);
                    var top = Math.Min(current.Top, other.Top);
                    var right = Math.Max(current.Right, other.Right);
                    var bottom = Math.Max(current.Bottom, other.Bottom);
                    current = new OpenCvSharp.Rect(left, top, right - left, bottom - top);
                    used[j] = true;
                }
            }

            merged.Add(current);
        }

        return merged;
    }
}
