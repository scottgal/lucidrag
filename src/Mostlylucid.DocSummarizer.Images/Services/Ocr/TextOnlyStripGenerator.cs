using Microsoft.Extensions.Logging;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.DocSummarizer.Images.Services.Ocr;

/// <summary>
/// Generates compact "text-only" images by extracting and combining text bounding boxes.
/// This improves OCR accuracy across ALL engines (Tesseract, ML, Florence-2) by:
/// - Removing visual noise from non-text regions
/// - Reducing image size for faster processing
/// - Focusing the model attention on just the text
/// </summary>
public class TextOnlyStripGenerator
{
    private readonly ILogger<TextOnlyStripGenerator>? _logger;

    public TextOnlyStripGenerator(ILogger<TextOnlyStripGenerator>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Result of text-only strip generation.
    /// </summary>
    public record TextOnlyStripResult
    {
        public required Image<Rgba32> StripImage { get; init; }
        public required int TotalFrames { get; init; }
        public required int FramesWithText { get; init; }
        public required int TextRegionsExtracted { get; init; }
        public required int ClearFramesDetected { get; init; }
        public required int TextSegments { get; init; } // Number of distinct text appearances (separated by clear frames)
        public required List<TextRegionInfo> Regions { get; init; }
    }

    public record TextRegionInfo
    {
        public int FrameIndex { get; init; }
        public int SegmentIndex { get; init; } // Which text segment (0, 1, 2... separated by clear frames)
        public int FrameCount { get; init; } // How many frames this text appeared for
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int StripY { get; init; } // Y position in the combined strip
        public bool IsClearFrame { get; init; } // Was this after a clear/blank frame
    }

    /// <summary>
    /// Detected frame with text info for tracking repetitions.
    /// </summary>
    private record FrameTextInfo
    {
        public int FrameIndex { get; init; }
        public bool HasText { get; init; }
        public List<Rect> TextBoxes { get; init; } = new();
        public Image<Rgba32>? Frame { get; init; }
    }

    /// <summary>
    /// Generates a compact text-only strip from an animated GIF.
    /// Extracts ONLY the text bounding boxes and stacks them vertically.
    /// Detects clear frames between text repetitions to properly segment captions.
    /// </summary>
    public async Task<TextOnlyStripResult> GenerateTextOnlyStripAsync(
        string gifPath,
        int maxFrames = 10,
        int padding = 4,
        CancellationToken ct = default)
    {
        using var image = await Image.LoadAsync<Rgba32>(gifPath, ct);

        if (image.Frames.Count < 2)
        {
            return await ExtractTextRegionsFromSingleFrameAsync(image, padding, ct);
        }

        var totalFrames = image.Frames.Count;

        // Phase 1: Analyze ALL frames to detect text/clear frame pattern
        var frameInfos = AnalyzeFramesForTextAndClearFrames(image);

        // Phase 2: Identify segments (text separated by clear frames)
        var segments = IdentifyTextSegments(frameInfos);

        _logger?.LogInformation(
            "Frame analysis: {Total} frames, {WithText} with text, {ClearFrames} clear, {Segments} segments",
            totalFrames, frameInfos.Count(f => f.HasText), frameInfos.Count(f => !f.HasText && f.FrameIndex > 0),
            segments.Count);

        if (segments.Count == 0)
        {
            _logger?.LogWarning("No text segments detected in {Path}", gifPath);
            return new TextOnlyStripResult
            {
                StripImage = new Image<Rgba32>(1, 1),
                TotalFrames = totalFrames,
                FramesWithText = 0,
                TextRegionsExtracted = 0,
                ClearFramesDetected = 0,
                TextSegments = 0,
                Regions = new List<TextRegionInfo>()
            };
        }

        // Phase 3: Extract best representative frame from each unique segment
        // Each segment already represents unique text content (detected via visual comparison)
        var regions = new List<TextRegionInfo>();
        var croppedImages = new List<(Image<Rgba32> Image, TextRegionInfo Info)>();
        var clearFrameCount = 0;

        for (int segIdx = 0; segIdx < segments.Count; segIdx++)
        {
            var segment = segments[segIdx];

            // Pick frame with most detected text boxes (likely clearest text)
            var bestFrame = segment.Frames.OrderByDescending(f => f.TextBoxes.Count).First();

            if (bestFrame.Frame == null) continue;

            // Track if this segment started after a clear frame
            var isAfterClear = segment.StartedAfterClearFrame;
            if (isAfterClear) clearFrameCount++;

            var mergedBoxes = MergeTextBoxesIntoLines(bestFrame.TextBoxes, bestFrame.Frame.Width);

            foreach (var box in mergedBoxes)
            {
                // Calculate safe crop bounds
                var x = Math.Max(0, box.X - padding);
                var y = Math.Max(0, box.Y - padding);
                var width = Math.Min(box.Width + padding * 2, bestFrame.Frame.Width - x);
                var height = Math.Min(box.Height + padding * 2, bestFrame.Frame.Height - y);

                // Skip invalid crops
                if (width <= 0 || height <= 0) continue;

                var cropRect = new SixLabors.ImageSharp.Rectangle(x, y, width, height);
                var cropped = bestFrame.Frame.Clone(ctx => ctx.Crop(cropRect));

                var info = new TextRegionInfo
                {
                    FrameIndex = bestFrame.FrameIndex,
                    SegmentIndex = segIdx,
                    FrameCount = segment.Frames.Count,
                    X = box.X,
                    Y = box.Y,
                    Width = cropRect.Width,
                    Height = cropRect.Height,
                    StripY = 0,
                    IsClearFrame = isAfterClear
                };

                croppedImages.Add((cropped, info));
            }
        }

        // Cleanup frame images
        foreach (var frameInfo in frameInfos)
        {
            frameInfo.Frame?.Dispose();
        }

        if (croppedImages.Count == 0)
        {
            _logger?.LogWarning("No text regions extracted from segments");
            return new TextOnlyStripResult
            {
                StripImage = new Image<Rgba32>(1, 1),
                TotalFrames = totalFrames,
                FramesWithText = frameInfos.Count(f => f.HasText),
                TextRegionsExtracted = 0,
                ClearFramesDetected = clearFrameCount,
                TextSegments = segments.Count,
                Regions = new List<TextRegionInfo>()
            };
        }

        // Skip deduplication - we already identified unique segments
        // Each segment represents different text content
        // croppedImages = DeduplicateTextRegions(croppedImages);

        // Combine into a single vertical strip
        var maxWidth = croppedImages.Max(x => x.Image.Width);
        var totalHeight = croppedImages.Sum(x => x.Image.Height) + (croppedImages.Count - 1) * padding;

        var stripImage = new Image<Rgba32>(maxWidth, totalHeight);
        stripImage.Mutate(ctx => ctx.BackgroundColor(SixLabors.ImageSharp.Color.White));

        var currentY = 0;
        foreach (var (croppedImage, info) in croppedImages)
        {
            var x = (maxWidth - croppedImage.Width) / 2;
            stripImage.Mutate(ctx => ctx.DrawImage(croppedImage, new SixLabors.ImageSharp.Point(x, currentY), 1f));
            regions.Add(info with { StripY = currentY });
            currentY += croppedImage.Height + padding;
            croppedImage.Dispose();
        }

        var framesWithText = frameInfos.Count(f => f.HasText);

        _logger?.LogInformation(
            "Text-only strip: {TotalFrames} frames → {Segments} segments → {Regions} regions, {ClearFrames} clear frames, Strip: {Width}x{Height}",
            totalFrames, segments.Count, regions.Count, clearFrameCount, stripImage.Width, stripImage.Height);

        return new TextOnlyStripResult
        {
            StripImage = stripImage,
            TotalFrames = totalFrames,
            FramesWithText = framesWithText,
            TextRegionsExtracted = regions.Count,
            ClearFramesDetected = clearFrameCount,
            TextSegments = segments.Count,
            Regions = regions
        };
    }

    /// <summary>
    /// Analyze all frames to detect which have text and which are clear/blank.
    /// Samples every frame for GIFs under 100 frames, otherwise samples ~100 frames.
    /// </summary>
    private List<FrameTextInfo> AnalyzeFramesForTextAndClearFrames(Image<Rgba32> image)
    {
        var frameInfos = new List<FrameTextInfo>();

        // For smaller GIFs, sample every frame to catch all text changes
        // For larger GIFs, sample ~100 frames
        var sampleInterval = image.Frames.Count <= 100 ? 1 : Math.Max(1, image.Frames.Count / 100);

        for (int i = 0; i < image.Frames.Count; i += sampleInterval)
        {
            var frame = image.Frames.CloneFrame(i);
            var textBoxes = DetectTextBoundingBoxes(frame);

            frameInfos.Add(new FrameTextInfo
            {
                FrameIndex = i,
                HasText = textBoxes.Count >= 1,
                TextBoxes = textBoxes,
                Frame = frame
            });
        }

        _logger?.LogDebug("Frame analysis: sampled {Count} frames (interval {Interval}) from {Total} total",
            frameInfos.Count, sampleInterval, image.Frames.Count);

        return frameInfos;
    }

    /// <summary>
    /// Identify distinct text segments by detecting:
    /// 1. Clear frames (no text) between segments
    /// 2. Significant text position/content changes (bounding box moved/resized)
    /// </summary>
    private List<TextSegment> IdentifyTextSegments(List<FrameTextInfo> frameInfos)
    {
        var segments = new List<TextSegment>();
        var currentSegment = new TextSegment();
        var wasCleared = false;
        FrameTextInfo? lastTextFrame = null;

        foreach (var frame in frameInfos)
        {
            if (frame.HasText)
            {
                // Check if text changed using detailed comparison
                var textChanged = lastTextFrame != null && AreTextRegionsDifferent(lastTextFrame, frame);

                if (currentSegment.Frames.Count == 0 || wasCleared || textChanged)
                {
                    // Start new segment if:
                    // - First segment
                    // - After a clear frame
                    // - Text position/size changed significantly
                    if (currentSegment.Frames.Count > 0)
                    {
                        segments.Add(currentSegment);
                    }
                    currentSegment = new TextSegment { StartedAfterClearFrame = wasCleared || textChanged };
                    wasCleared = false;
                }

                currentSegment.Frames.Add(frame);
                lastTextFrame = frame;
            }
            else
            {
                // Clear frame detected
                wasCleared = true;
                lastTextFrame = null;
            }
        }

        // Add final segment
        if (currentSegment.Frames.Count > 0)
        {
            segments.Add(currentSegment);
        }

        _logger?.LogDebug("Segment detection: {Count} segments from {Frames} frames",
            segments.Count, frameInfos.Count);

        return segments;
    }

    private class TextSegment
    {
        public List<FrameTextInfo> Frames { get; } = new();
        public bool StartedAfterClearFrame { get; set; }
    }

    private long ComputeSegmentHash(List<Rect> boxes)
    {
        // Hash based on bounding box positions - use coarse quantization
        // to handle minor variations while detecting actual text changes
        long hash = 0;
        foreach (var box in boxes.OrderBy(b => b.Y).ThenBy(b => b.X))
        {
            // Coarse quantization - changes < 20px are considered same
            hash = hash * 31 + box.X / 20;
            hash = hash * 31 + box.Y / 20;
            hash = hash * 31 + box.Width / 20;
            hash = hash * 31 + box.Height / 20;
        }
        return hash;
    }

    /// <summary>
    /// Check if two text regions are significantly different (different captions).
    /// Compares actual pixel content in the text region.
    /// </summary>
    private bool AreTextRegionsDifferent(FrameTextInfo frame1, FrameTextInfo frame2)
    {
        if (frame1.TextBoxes.Count == 0 || frame2.TextBoxes.Count == 0)
            return true;

        if (frame1.Frame == null || frame2.Frame == null)
            return true;

        var box1 = frame1.TextBoxes[0];
        var box2 = frame2.TextBoxes[0];

        // Compare the actual pixel content of text regions
        // Crop text regions and compare visually
        try
        {
            using var crop1 = frame1.Frame.Clone(ctx => ctx.Crop(
                new SixLabors.ImageSharp.Rectangle(
                    Math.Max(0, box1.X),
                    Math.Max(0, box1.Y),
                    Math.Min(box1.Width, frame1.Frame.Width - box1.X),
                    Math.Min(box1.Height, frame1.Frame.Height - box1.Y))));

            using var crop2 = frame2.Frame.Clone(ctx => ctx.Crop(
                new SixLabors.ImageSharp.Rectangle(
                    Math.Max(0, box2.X),
                    Math.Max(0, box2.Y),
                    Math.Min(box2.Width, frame2.Frame.Width - box2.X),
                    Math.Min(box2.Height, frame2.Frame.Height - box2.Y))));

            // Calculate visual similarity
            var similarity = CalculateImageSimilarity(crop1, crop2);

            // If less than 85% similar, text has changed
            return similarity < 0.85;
        }
        catch
        {
            // On error, assume different
            return true;
        }
    }

    /// <summary>
    /// Calculate visual similarity between two images, focusing ONLY on bright pixels (text).
    /// Ignores dark background to detect text changes even when background is similar.
    /// </summary>
    private double CalculateImageSimilarity(Image<Rgba32> img1, Image<Rgba32> img2)
    {
        // Quick size check - very different sizes = different content
        if (Math.Abs(img1.Width - img2.Width) > img1.Width * 0.3 ||
            Math.Abs(img1.Height - img2.Height) > img1.Height * 0.3)
            return 0.0;

        var width = Math.Min(img1.Width, img2.Width);
        var height = Math.Min(img1.Height, img2.Height);

        if (width == 0 || height == 0) return 0.0;

        int brightPixels1 = 0;
        int brightPixels2 = 0;
        int matchingBright = 0;

        img1.ProcessPixelRows(img2, (accessor1, accessor2) =>
        {
            for (int y = 0; y < height; y++)
            {
                var row1 = accessor1.GetRowSpan(y);
                var row2 = accessor2.GetRowSpan(y);

                for (int x = 0; x < width; x++)
                {
                    var p1 = row1[x];
                    var p2 = row2[x];

                    // Check if pixel is bright (text)
                    var l1 = (int)(0.299 * p1.R + 0.587 * p1.G + 0.114 * p1.B);
                    var l2 = (int)(0.299 * p2.R + 0.587 * p2.G + 0.114 * p2.B);

                    var isBright1 = l1 > 150;
                    var isBright2 = l2 > 150;

                    if (isBright1) brightPixels1++;
                    if (isBright2) brightPixels2++;

                    // Count matching bright pixels (both are bright at same position)
                    if (isBright1 && isBright2) matchingBright++;
                }
            }
        });

        // Calculate Jaccard similarity of bright pixels (text)
        var unionBright = brightPixels1 + brightPixels2 - matchingBright;
        if (unionBright == 0) return 1.0; // Both have no text = same

        return (double)matchingBright / unionBright;
    }

    private async Task<TextOnlyStripResult> ExtractTextRegionsFromSingleFrameAsync(
        Image<Rgba32> image, int padding, CancellationToken ct)
    {
        var frame = image.Frames.Count > 1
            ? image.Frames.CloneFrame(0)
            : image.Clone();

        var textBoxes = DetectTextBoundingBoxes(frame);

        if (textBoxes.Count == 0)
        {
            frame.Dispose();
            return new TextOnlyStripResult
            {
                StripImage = new Image<Rgba32>(1, 1),
                TotalFrames = 1,
                FramesWithText = 0,
                TextRegionsExtracted = 0,
                ClearFramesDetected = 0,
                TextSegments = 0,
                Regions = new List<TextRegionInfo>()
            };
        }

        var mergedBoxes = MergeTextBoxesIntoLines(textBoxes, frame.Width);
        var regions = new List<TextRegionInfo>();
        var croppedImages = new List<Image<Rgba32>>();

        foreach (var box in mergedBoxes)
        {
            // Calculate safe crop bounds
            var x = Math.Max(0, box.X - padding);
            var y = Math.Max(0, box.Y - padding);
            var width = Math.Min(box.Width + padding * 2, frame.Width - x);
            var height = Math.Min(box.Height + padding * 2, frame.Height - y);

            // Skip invalid crops
            if (width <= 0 || height <= 0) continue;

            var cropRect = new SixLabors.ImageSharp.Rectangle(x, y, width, height);
            var cropped = frame.Clone(ctx => ctx.Crop(cropRect));
            croppedImages.Add(cropped);

            regions.Add(new TextRegionInfo
            {
                FrameIndex = 0,
                SegmentIndex = 0,
                FrameCount = 1,
                X = box.X,
                Y = box.Y,
                Width = cropRect.Width,
                Height = cropRect.Height,
                StripY = 0,
                IsClearFrame = false
            });
        }

        frame.Dispose();

        // Combine
        var maxWidth = croppedImages.Max(x => x.Width);
        var totalHeight = croppedImages.Sum(x => x.Height) + (croppedImages.Count - 1) * padding;

        var stripImage = new Image<Rgba32>(maxWidth, totalHeight);
        stripImage.Mutate(ctx => ctx.BackgroundColor(SixLabors.ImageSharp.Color.White));

        var currentY = 0;
        for (int i = 0; i < croppedImages.Count; i++)
        {
            var x = (maxWidth - croppedImages[i].Width) / 2;
            stripImage.Mutate(ctx => ctx.DrawImage(croppedImages[i], new SixLabors.ImageSharp.Point(x, currentY), 1f));
            regions[i] = regions[i] with { StripY = currentY };
            currentY += croppedImages[i].Height + padding;
            croppedImages[i].Dispose();
        }

        return new TextOnlyStripResult
        {
            StripImage = stripImage,
            TotalFrames = 1,
            FramesWithText = 1,
            TextRegionsExtracted = regions.Count,
            ClearFramesDetected = 0,
            TextSegments = 1,
            Regions = regions
        };
    }

    /// <summary>
    /// Detect text bounding boxes - returns a SINGLE tight box around subtitle text.
    /// Crops just the bottom portion containing text, not individual characters.
    /// </summary>
    private List<Rect> DetectTextBoundingBoxes(Image<Rgba32> frame)
    {
        using var mat = ImageSharpToMat(frame);

        // Focus on subtitle region (bottom 30% of frame where subtitles appear)
        var subtitleY = (int)(mat.Height * 0.70);
        var subtitleHeight = mat.Height - subtitleY;

        using var subtitleRegion = new Mat(mat, new Rect(0, subtitleY, mat.Width, subtitleHeight));

        // Convert to grayscale
        using var gray = new Mat();
        if (subtitleRegion.Channels() > 1)
            Cv2.CvtColor(subtitleRegion, gray, ColorConversionCodes.BGR2GRAY);
        else
            subtitleRegion.CopyTo(gray);

        // Threshold to find bright text (subtitles are typically white/yellow)
        using var thresh = new Mat();
        Cv2.Threshold(gray, thresh, 150, 255, ThresholdTypes.Binary);

        // Count bright pixels - if enough, there's text
        var brightPixels = Cv2.CountNonZero(thresh);
        var totalPixels = thresh.Width * thresh.Height;
        var brightRatio = (double)brightPixels / totalPixels;

        // If 1-30% bright pixels, likely has subtitle text
        if (brightRatio > 0.01 && brightRatio < 0.30)
        {
            // Find the actual text bounds by scanning rows
            int textTop = 0, textBottom = thresh.Height - 1;
            int textLeft = 0, textRight = thresh.Width - 1;

            // Scan from top to find first row with significant bright pixels
            for (int y = 0; y < thresh.Height; y++)
            {
                var row = new Mat(thresh, new Rect(0, y, thresh.Width, 1));
                if (Cv2.CountNonZero(row) > thresh.Width * 0.02)
                {
                    textTop = y;
                    break;
                }
            }

            // Scan from bottom to find last row with bright pixels
            for (int y = thresh.Height - 1; y >= 0; y--)
            {
                var row = new Mat(thresh, new Rect(0, y, thresh.Width, 1));
                if (Cv2.CountNonZero(row) > thresh.Width * 0.02)
                {
                    textBottom = y;
                    break;
                }
            }

            // Scan columns for text bounds
            for (int x = 0; x < thresh.Width; x++)
            {
                var col = new Mat(thresh, new Rect(x, 0, 1, thresh.Height));
                if (Cv2.CountNonZero(col) > 0)
                {
                    textLeft = x;
                    break;
                }
            }

            for (int x = thresh.Width - 1; x >= 0; x--)
            {
                var col = new Mat(thresh, new Rect(x, 0, 1, thresh.Height));
                if (Cv2.CountNonZero(col) > 0)
                {
                    textRight = x;
                    break;
                }
            }

            // Create tight bounding box around text only
            var textWidth = textRight - textLeft + 1;
            var textHeight = textBottom - textTop + 1;

            if (textWidth > 30 && textHeight > 8 && textHeight < 80)
            {
                // Return single tight box (coordinates relative to full frame)
                return new List<Rect>
                {
                    new Rect(textLeft, textTop + subtitleY, textWidth, textHeight)
                };
            }
        }

        return new List<Rect>();
    }

    /// <summary>
    /// Fallback MSER-based text detection for non-subtitle text.
    /// </summary>
    private List<Rect> DetectTextBoxesMSER(Mat mat)
    {
        using var gray = new Mat();
        if (mat.Channels() > 1)
            Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);
        else
            mat.CopyTo(gray);

        using var mser = MSER.Create(
            delta: 5,
            minArea: 100,
            maxArea: 5000, // Much smaller max area - text characters are small
            maxVariation: 0.25,
            minDiversity: 0.2);

        mser.DetectRegions(gray, out var msers, out var bboxes);

        var textBoxes = new List<Rect>();
        var totalArea = mat.Width * mat.Height;

        foreach (var bbox in bboxes)
        {
            var aspectRatio = (double)bbox.Width / Math.Max(1, bbox.Height);
            var areaRatio = (double)(bbox.Width * bbox.Height) / totalArea;

            // Very strict text-like heuristics
            if (aspectRatio >= 0.3 && aspectRatio <= 8 &&
                areaRatio >= 0.0001 && areaRatio <= 0.02 && // Much smaller max area
                bbox.Width >= 10 && bbox.Height >= 8 && bbox.Height <= 60)
            {
                textBoxes.Add(bbox);
            }
        }

        return textBoxes;
    }

    /// <summary>
    /// Merge nearby boxes into text lines - creates TIGHT bounding boxes around text only.
    /// Does NOT expand horizontally - keeps the actual text bounds.
    /// </summary>
    private List<Rect> MergeTextBoxesIntoLines(List<Rect> boxes, int imageWidth)
    {
        if (boxes.Count == 0) return boxes;
        if (boxes.Count == 1) return boxes;

        // Sort by Y position
        var sorted = boxes.OrderBy(b => b.Y).ThenBy(b => b.X).ToList();
        var merged = new List<Rect>();
        var used = new bool[sorted.Count];

        for (int i = 0; i < sorted.Count; i++)
        {
            if (used[i]) continue;

            var current = sorted[i];
            var lineTop = current.Top;
            var lineBottom = current.Bottom;
            var lineLeft = current.Left;
            var lineRight = current.Right;

            // Find boxes on the same line (similar Y)
            for (int j = i + 1; j < sorted.Count; j++)
            {
                if (used[j]) continue;

                var other = sorted[j];
                var yOverlap = Math.Max(0, Math.Min(lineBottom, other.Bottom) - Math.Max(lineTop, other.Top));
                var lineHeight = lineBottom - lineTop;

                // If boxes overlap vertically by >50%, they're on the same line
                if (yOverlap > lineHeight * 0.5 && yOverlap > other.Height * 0.5)
                {
                    lineTop = Math.Min(lineTop, other.Top);
                    lineBottom = Math.Max(lineBottom, other.Bottom);
                    lineLeft = Math.Min(lineLeft, other.Left);
                    lineRight = Math.Max(lineRight, other.Right);
                    used[j] = true;
                }
            }

            merged.Add(new Rect(lineLeft, lineTop, lineRight - lineLeft, lineBottom - lineTop));
        }

        // Merge vertically adjacent lines that are very close
        merged = MergeCloseVerticalBoxes(merged);

        return merged;
    }

    private List<Rect> MergeCloseVerticalBoxes(List<Rect> boxes)
    {
        if (boxes.Count <= 1) return boxes;

        var sorted = boxes.OrderBy(b => b.Y).ToList();
        var merged = new List<Rect>();
        var current = sorted[0];

        for (int i = 1; i < sorted.Count; i++)
        {
            var next = sorted[i];
            var gap = next.Top - current.Bottom;

            // If gap is less than average line height, merge
            if (gap < current.Height * 0.5)
            {
                current = new Rect(
                    Math.Min(current.Left, next.Left),
                    current.Top,
                    Math.Max(current.Right, next.Right) - Math.Min(current.Left, next.Left),
                    next.Bottom - current.Top);
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);
        return merged;
    }

    /// <summary>
    /// Deduplicate text regions that look similar (same text appearing across frames).
    /// </summary>
    private List<(Image<Rgba32> Image, TextRegionInfo Info)> DeduplicateTextRegions(
        List<(Image<Rgba32> Image, TextRegionInfo Info)> regions)
    {
        if (regions.Count <= 1) return regions;

        var unique = new List<(Image<Rgba32> Image, TextRegionInfo Info)>();

        foreach (var region in regions)
        {
            var isDuplicate = unique.Any(existing =>
                ImagesAreSimilar(existing.Image, region.Image, 0.90));

            if (!isDuplicate)
            {
                unique.Add(region);
            }
            else
            {
                region.Image.Dispose();
            }
        }

        _logger?.LogDebug("Deduplication: {Original} → {Unique} unique text regions",
            regions.Count, unique.Count);

        return unique;
    }

    private bool FramesAreSimilar(Image<Rgba32> frame1, Image<Rgba32> frame2, double threshold = 0.95)
    {
        return ImagesAreSimilar(frame1, frame2, threshold);
    }

    private bool ImagesAreSimilar(Image<Rgba32> img1, Image<Rgba32> img2, double threshold)
    {
        // Use bright pixel (text) comparison - same as segment detection
        var similarity = CalculateImageSimilarity(img1, img2);
        return similarity >= threshold;
    }

    private bool ImagesAreSimilarOld(Image<Rgba32> img1, Image<Rgba32> img2, double threshold)
    {
        // Quick size check
        if (Math.Abs(img1.Width - img2.Width) > 20 || Math.Abs(img1.Height - img2.Height) > 20)
            return false;

        var width = Math.Min(img1.Width, img2.Width);
        var height = Math.Min(img1.Height, img2.Height);

        long totalDiff = 0;
        long totalPixels = 0;

        // Sample every 4th pixel
        img1.ProcessPixelRows(img2, (accessor1, accessor2) =>
        {
            for (int y = 0; y < height; y += 4)
            {
                var row1 = accessor1.GetRowSpan(y);
                var row2 = accessor2.GetRowSpan(y);

                for (int x = 0; x < width; x += 4)
                {
                    var p1 = row1[x];
                    var p2 = row2[x];

                    var diff = Math.Abs(p1.R - p2.R) + Math.Abs(p1.G - p2.G) + Math.Abs(p1.B - p2.B);
                    totalDiff += diff;
                    totalPixels++;
                }
            }
        });

        if (totalPixels == 0) return true;

        var avgDiff = totalDiff / (double)totalPixels;
        var similarity = 1.0 - (avgDiff / 765.0);

        return similarity >= threshold;
    }

    private Mat ImageSharpToMat(Image<Rgba32> image)
    {
        var mat = new Mat(image.Height, image.Width, MatType.CV_8UC4);

        image.ProcessPixelRows(accessor =>
        {
            unsafe
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    var matPtr = (byte*)mat.Ptr(y).ToPointer();

                    for (int x = 0; x < row.Length; x++)
                    {
                        var pixel = row[x];
                        matPtr[x * 4 + 0] = pixel.B;
                        matPtr[x * 4 + 1] = pixel.G;
                        matPtr[x * 4 + 2] = pixel.R;
                        matPtr[x * 4 + 3] = pixel.A;
                    }
                }
            }
        });

        return mat;
    }
}
