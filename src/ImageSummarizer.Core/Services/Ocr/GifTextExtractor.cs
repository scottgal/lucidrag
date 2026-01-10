using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.DocSummarizer.Images.Services.Ocr;

/// <summary>
/// Extracts text from animated images (GIF/WebP) by intelligently sampling frames.
/// Handles progressive text reveals (letters appearing one by one) and subtitle-style text changes.
/// </summary>
public class GifTextExtractor
{
    private readonly IOcrEngine _ocrEngine;
    private readonly TextLikelinessAnalyzer _textAnalyzer;
    private readonly ILogger<GifTextExtractor>? _logger;
    private readonly AdvancedGifOcrService? _advancedOcrService;
    private readonly ImageConfig _config;

    // Configuration
    private readonly int _maxFramesToSample = 10; // Sample up to 10 frames
    private readonly int _maxFramesToAnalyze = 30; // Don't analyze more than 30 frames total
    private readonly double _textLikelinessThreshold = 0.3; // Only OCR frames with text score > 0.3
    private readonly bool _enableSubtitleOptimization = true; // Enable fast path for subtitle regions
    private readonly double _subtitleRegionHeight = 0.25; // Bottom 25% of frame for subtitles

    public GifTextExtractor(
        IOcrEngine ocrEngine,
        ImageConfig config,
        AdvancedGifOcrService? advancedOcrService = null,
        ILogger<GifTextExtractor>? logger = null)
    {
        _ocrEngine = ocrEngine;
        _config = config;
        _advancedOcrService = advancedOcrService;
        _textAnalyzer = new TextLikelinessAnalyzer();
        _logger = logger;
    }

    /// <summary>
    /// Extract text from an animated image by sampling multiple frames.
    /// Returns combined text from all frames, handling progressive reveals and subtitle changes.
    /// </summary>
    public async Task<GifTextExtractionResult> ExtractTextAsync(
        string imagePath,
        CancellationToken ct = default)
    {
        var format = Image.DetectFormat(imagePath);
        var formatName = format?.Name?.ToUpperInvariant();

        if (formatName != "GIF" && formatName != "WEBP")
        {
            throw new ArgumentException($"Unsupported animated format: {formatName}. Only GIF and WebP are supported.");
        }

        // Use advanced pipeline if enabled and available
        if (_config.Ocr.UseAdvancedPipeline && _advancedOcrService != null)
        {
            _logger?.LogInformation(
                "Using advanced OCR pipeline (mode={Mode}) for {Format}: {Path}",
                _config.Ocr.QualityMode, formatName, imagePath);

            var advancedResult = await _advancedOcrService.ExtractTextAsync(imagePath, captureProcessedFrames: false, ct: ct);

            // Convert AdvancedOcrResult to GifTextExtractionResult
            return new GifTextExtractionResult
            {
                TotalFrames = advancedResult.Metrics.TotalFrames,
                FramesAnalyzed = advancedResult.Metrics.FramesVoted > 0
                    ? advancedResult.Metrics.FramesVoted
                    : advancedResult.Metrics.TotalFrames,
                FramesWithText = advancedResult.TextRegions.Count > 0 ? 1 : 0,
                TextRegions = advancedResult.TextRegions,
                CombinedText = advancedResult.ConsensusText,
                FrameTextData = new List<FrameTextData>
                {
                    new()
                    {
                        FrameIndex = 0,
                        TextRegions = advancedResult.TextRegions,
                        TextLikelinessScore = advancedResult.Confidence
                    }
                }
            };
        }

        _logger?.LogInformation("Extracting text from {Format} with multi-frame sampling: {Path}",
            formatName, imagePath);

        using var image = await Image.LoadAsync<Rgba32>(imagePath, ct);

        // Get frame count
        var frameCount = image.Frames.Count;
        _logger?.LogInformation("Image has {FrameCount} frames", frameCount);

        if (frameCount == 1)
        {
            // Single frame - just use standard OCR
            _logger?.LogInformation("Single frame detected, using standard OCR");
            var regions = _ocrEngine.ExtractTextWithCoordinates(imagePath);
            return new GifTextExtractionResult
            {
                TotalFrames = 1,
                FramesAnalyzed = 1,
                FramesWithText = regions.Count > 0 ? 1 : 0,
                TextRegions = regions,
                CombinedText = string.Join(" ", regions.Select(r => r.Text)),
                FrameTextData = new List<FrameTextData>
                {
                    new() { FrameIndex = 0, TextRegions = regions, TextLikelinessScore = 1.0 }
                }
            };
        }

        // Multi-frame analysis
        var framesToAnalyze = Math.Min(frameCount, _maxFramesToAnalyze);
        var samplingInterval = Math.Max(1, frameCount / framesToAnalyze);

        _logger?.LogInformation("Sampling every {Interval} frames (up to {Max} frames)",
            samplingInterval, framesToAnalyze);

        // Step 1: Sample frames and calculate text likeliness scores with SSIM-based deduplication
        var frameScores = new List<(int FrameIndex, double TextScore, Image<Rgba32> Frame)>();
        Image<Rgba32>? previousFrame = null;
        int skippedDuplicates = 0;

        for (int i = 0; i < frameCount; i += samplingInterval)
        {
            if (ct.IsCancellationRequested) break;
            if (frameScores.Count >= framesToAnalyze) break;

            var frame = image.Frames.CloneFrame(i);

            // SSIM-based deduplication: Skip frames that are very similar to previous frame
            if (previousFrame != null)
            {
                var similarity = ComputeFrameSimilarity(previousFrame, frame);
                if (similarity > 0.95) // 95% similar = skip as duplicate
                {
                    _logger?.LogDebug("Frame {Index}: skipped (similarity={Similarity:F3})", i, similarity);
                    frame.Dispose();
                    skippedDuplicates++;
                    continue;
                }
            }

            var textScore = _textAnalyzer.CalculateTextLikeliness(frame);
            frameScores.Add((i, textScore, frame));

            _logger?.LogDebug("Frame {Index}: text likeliness = {Score:F3}",
                i, textScore);

            // Dispose previous frame and update
            previousFrame?.Dispose();
            previousFrame = frame.Clone();
        }

        // Clean up last previous frame
        previousFrame?.Dispose();

        _logger?.LogInformation("SSIM deduplication: skipped {Skipped} duplicate frames out of {Total}",
            skippedDuplicates, frameCount);

        // Step 2: Select top N frames with highest text scores
        var selectedFrames = frameScores
            .Where(f => f.TextScore >= _textLikelinessThreshold)
            .OrderByDescending(f => f.TextScore)
            .Take(_maxFramesToSample)
            .OrderBy(f => f.FrameIndex) // Process in chronological order
            .ToList();

        _logger?.LogInformation("Selected {Count} frames for OCR (threshold: {Threshold:F2})",
            selectedFrames.Count, _textLikelinessThreshold);

        if (selectedFrames.Count == 0)
        {
            _logger?.LogWarning("No frames met text likeliness threshold");

            // Clean up
            foreach (var (_, _, frame) in frameScores)
            {
                frame.Dispose();
            }

            return new GifTextExtractionResult
            {
                TotalFrames = frameCount,
                FramesAnalyzed = frameScores.Count,
                FramesWithText = 0,
                TextRegions = new List<OcrTextRegion>(),
                CombinedText = string.Empty,
                FrameTextData = new List<FrameTextData>()
            };
        }

        // Step 3: Extract text from selected frames
        var allTextRegions = new List<OcrTextRegion>();
        var frameTextData = new List<FrameTextData>();
        var tempImagePath = Path.Combine(Path.GetTempPath(), $"frame_{Guid.NewGuid()}.png");
        int subtitleOptimizationCount = 0;

        try
        {
            foreach (var (frameIndex, textScore, frame) in selectedFrames)
            {
                if (ct.IsCancellationRequested) break;

                List<OcrTextRegion> regions;

                // Fast path: Check if text is likely in subtitle region (bottom portion)
                if (_enableSubtitleOptimization && HasSubtitleLikelyDistribution(frame))
                {
                    // Crop to subtitle region only (much faster OCR)
                    var subtitleHeight = (int)(frame.Height * _subtitleRegionHeight);
                    var subtitleY = frame.Height - subtitleHeight;

                    using var croppedFrame = frame.Clone(ctx =>
                        ctx.Crop(new SixLabors.ImageSharp.Rectangle(0, subtitleY, frame.Width, subtitleHeight)));

                    await croppedFrame.SaveAsPngAsync(tempImagePath, ct);

                    // Extract text from cropped region
                    regions = _ocrEngine.ExtractTextWithCoordinates(tempImagePath);

                    // Adjust bounding boxes to account for crop offset
                    foreach (var region in regions)
                    {
                        var adjustedBox = new BoundingBox
                        {
                            X1 = region.BoundingBox.X1,
                            Y1 = region.BoundingBox.Y1 + subtitleY, // Add back the crop offset
                            X2 = region.BoundingBox.X2,
                            Y2 = region.BoundingBox.Y2 + subtitleY,
                            Width = region.BoundingBox.Width,
                            Height = region.BoundingBox.Height
                        };

                        allTextRegions.Add(region with { BoundingBox = adjustedBox });
                    }

                    subtitleOptimizationCount++;
                    _logger?.LogDebug("Frame {Index}: used subtitle region optimization (cropped to bottom {Percent}%)",
                        frameIndex, _subtitleRegionHeight * 100);
                }
                else
                {
                    // Standard path: OCR entire frame
                    await frame.SaveAsPngAsync(tempImagePath, ct);

                    // Extract text from full frame
                    regions = _ocrEngine.ExtractTextWithCoordinates(tempImagePath);
                }

                if (regions.Count > 0)
                {
                    _logger?.LogInformation("Frame {Index}: extracted {Count} text regions",
                        frameIndex, regions.Count);

                    // Tag regions with frame index for tracking
                    allTextRegions.AddRange(regions);

                    frameTextData.Add(new FrameTextData
                    {
                        FrameIndex = frameIndex,
                        TextRegions = regions,
                        TextLikelinessScore = textScore
                    });
                }
            }
        }
        finally
        {
            // Clean up temp file
            if (File.Exists(tempImagePath))
            {
                File.Delete(tempImagePath);
            }

            // Clean up frames
            foreach (var (_, _, frame) in frameScores)
            {
                frame.Dispose();
            }
        }

        // Step 4: Deduplicate and combine text
        var combinedText = DeduplicateAndCombineText(frameTextData);

        if (subtitleOptimizationCount > 0)
        {
            _logger?.LogInformation("Subtitle optimization: {Count} frames used fast path (bottom {Percent}% only)",
                subtitleOptimizationCount, _subtitleRegionHeight * 100);
        }

        _logger?.LogInformation("Extracted {Total} text regions from {Frames} frames, combined to: {Preview}",
            allTextRegions.Count, selectedFrames.Count,
            combinedText.Length > 100 ? combinedText.Substring(0, 100) + "..." : combinedText);

        return new GifTextExtractionResult
        {
            TotalFrames = frameCount,
            FramesAnalyzed = frameScores.Count,
            FramesWithText = frameTextData.Count,
            TextRegions = allTextRegions,
            CombinedText = combinedText,
            FrameTextData = frameTextData
        };
    }

    /// <summary>
    /// Fast heuristic to detect if text is likely in subtitle region (bottom portion of frame).
    /// Uses edge density distribution to avoid expensive text-likeliness calculation.
    /// </summary>
    private bool HasSubtitleLikelyDistribution(Image<Rgba32> frame)
    {
        var height = frame.Height;
        var width = frame.Width;

        // Divide frame into top 75% and bottom 25%
        var splitY = (int)(height * (1.0 - _subtitleRegionHeight));

        long topEdges = 0;
        long bottomEdges = 0;

        // Quick edge detection: count high-contrast pixels
        frame.ProcessPixelRows(accessor =>
        {
            for (int y = 1; y < height - 1; y += 4) // Sample every 4th row for speed
            {
                var prevRow = accessor.GetRowSpan(y - 1);
                var currRow = accessor.GetRowSpan(y);
                var nextRow = accessor.GetRowSpan(y + 1);

                for (int x = 1; x < width - 1; x += 4) // Sample every 4th pixel
                {
                    var center = currRow[x];
                    var centerLum = (int)(0.299 * center.R + 0.587 * center.G + 0.114 * center.B);

                    // Check for edges (high contrast)
                    var isEdge = false;
                    foreach (var neighbor in new[] { prevRow[x], currRow[x - 1], currRow[x + 1], nextRow[x] })
                    {
                        var neighLum = (int)(0.299 * neighbor.R + 0.587 * neighbor.G + 0.114 * neighbor.B);
                        if (Math.Abs(centerLum - neighLum) > 40) // Edge threshold
                        {
                            isEdge = true;
                            break;
                        }
                    }

                    if (isEdge)
                    {
                        if (y < splitY)
                            topEdges++;
                        else
                            bottomEdges++;
                    }
                }
            }
        });

        // If bottom 25% has >= 40% of edges, likely subtitle region
        var totalEdges = topEdges + bottomEdges;
        if (totalEdges == 0) return false;

        var bottomRatio = bottomEdges / (double)totalEdges;
        return bottomRatio >= 0.40; // At least 40% of edges in bottom region
    }

    /// <summary>
    /// Compute similarity between two frames (SSIM-like metric).
    /// Returns value from 0.0 (completely different) to 1.0 (identical).
    /// </summary>
    private double ComputeFrameSimilarity(Image<Rgba32> frame1, Image<Rgba32> frame2)
    {
        var width = Math.Min(frame1.Width, frame2.Width);
        var height = Math.Min(frame1.Height, frame2.Height);

        long totalDifference = 0;
        long totalPixels = 0;

        // Sample every 4th pixel for performance
        frame1.ProcessPixelRows(frame2, (row1Accessor, row2Accessor) =>
        {
            for (int y = 0; y < height; y += 4)
            {
                var row1 = row1Accessor.GetRowSpan(y);
                var row2 = row2Accessor.GetRowSpan(y);

                for (int x = 0; x < width; x += 4)
                {
                    var p1 = row1[x];
                    var p2 = row2[x];

                    // Compute pixel difference (Manhattan distance in RGB space)
                    var diff = Math.Abs(p1.R - p2.R) + Math.Abs(p1.G - p2.G) + Math.Abs(p1.B - p2.B);
                    totalDifference += diff;
                    totalPixels++;
                }
            }
        });

        if (totalPixels == 0) return 1.0;

        // Normalize: max difference per pixel is 255*3 = 765
        var avgDifference = totalDifference / (double)totalPixels;
        var normalizedDifference = avgDifference / 765.0;

        // Convert to similarity: 1.0 = identical, 0.0 = completely different
        return 1.0 - normalizedDifference;
    }

    /// <summary>
    /// Deduplicate and combine text from multiple frames.
    /// Handles progressive reveals (H -> HE -> HEL -> HELL -> HELLO) and subtitle changes.
    /// Uses Levenshtein distance to handle OCR errors gracefully.
    /// </summary>
    private string DeduplicateAndCombineText(List<FrameTextData> frameTextData)
    {
        if (frameTextData.Count == 0) return string.Empty;
        if (frameTextData.Count == 1)
        {
            return string.Join(" ", frameTextData[0].TextRegions.Select(r => r.Text));
        }

        // Strategy: Track unique text blocks across frames
        // - Progressive reveals: keep the longest version
        // - Subtitles: keep all unique subtitle lines in chronological order
        // - Use Levenshtein distance to merge OCR variations

        var subtitleLines = new List<string>();

        foreach (var frameData in frameTextData.OrderBy(f => f.FrameIndex))
        {
            var frameText = string.Join(" ", frameData.TextRegions.Select(r => r.Text)).Trim();

            if (string.IsNullOrWhiteSpace(frameText)) continue;

            bool merged = false;

            // Check for progressive reveals and OCR variations using Levenshtein distance
            for (int i = 0; i < subtitleLines.Count; i++)
            {
                var existing = subtitleLines[i];

                // Check for substring (progressive reveal)
                if (existing.Contains(frameText, StringComparison.OrdinalIgnoreCase))
                {
                    // Current text is shorter version - skip it
                    merged = true;
                    break;
                }
                else if (frameText.Contains(existing, StringComparison.OrdinalIgnoreCase))
                {
                    // Current text is longer version - replace
                    subtitleLines[i] = frameText;
                    merged = true;
                    break;
                }

                // Check for OCR variations using Levenshtein distance
                var distance = LevenshteinDistance(frameText, existing);
                var maxLength = Math.Max(frameText.Length, existing.Length);

                // If distance is small relative to text length, it's likely an OCR variation
                if (maxLength > 0 && distance <= Math.Max(3, maxLength * 0.15)) // Allow 15% error rate
                {
                    // Keep the longer version (likely more complete)
                    if (frameText.Length > existing.Length)
                    {
                        subtitleLines[i] = frameText;
                    }
                    merged = true;
                    break;
                }
            }

            if (!merged)
            {
                // This is genuinely new text
                subtitleLines.Add(frameText);
            }
        }

        // Return combined text
        return string.Join(" ", subtitleLines);
    }

    /// <summary>
    /// Calculate Levenshtein distance between two strings.
    /// Returns the minimum number of single-character edits required to transform one string into the other.
    /// </summary>
    private int LevenshteinDistance(string source, string target)
    {
        if (string.IsNullOrEmpty(source))
            return target?.Length ?? 0;

        if (string.IsNullOrEmpty(target))
            return source.Length;

        int sourceLength = source.Length;
        int targetLength = target.Length;

        var distance = new int[sourceLength + 1, targetLength + 1];

        // Initialize first column and row
        for (int i = 0; i <= sourceLength; i++)
            distance[i, 0] = i;

        for (int j = 0; j <= targetLength; j++)
            distance[0, j] = j;

        // Calculate distances
        for (int i = 1; i <= sourceLength; i++)
        {
            for (int j = 1; j <= targetLength; j++)
            {
                int cost = (target[j - 1] == source[i - 1]) ? 0 : 1;

                distance[i, j] = Math.Min(
                    Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                    distance[i - 1, j - 1] + cost);
            }
        }

        return distance[sourceLength, targetLength];
    }
}

/// <summary>
/// Result of GIF text extraction with multi-frame analysis.
/// </summary>
public record GifTextExtractionResult
{
    /// <summary>
    /// Total number of frames in the GIF/WebP.
    /// </summary>
    public required int TotalFrames { get; init; }

    /// <summary>
    /// Number of frames that were analyzed for text likeliness.
    /// </summary>
    public required int FramesAnalyzed { get; init; }

    /// <summary>
    /// Number of frames that had text extracted.
    /// </summary>
    public required int FramesWithText { get; init; }

    /// <summary>
    /// All text regions extracted from all frames.
    /// </summary>
    public required List<OcrTextRegion> TextRegions { get; init; }

    /// <summary>
    /// Combined deduplicated text from all frames.
    /// </summary>
    public required string CombinedText { get; init; }

    /// <summary>
    /// Per-frame text data (for debugging/analysis).
    /// </summary>
    public required List<FrameTextData> FrameTextData { get; init; }
}

/// <summary>
/// Text data extracted from a single frame.
/// </summary>
public record FrameTextData
{
    /// <summary>
    /// Frame index in the GIF/WebP.
    /// </summary>
    public required int FrameIndex { get; init; }

    /// <summary>
    /// Text regions extracted from this frame.
    /// </summary>
    public required List<OcrTextRegion> TextRegions { get; init; }

    /// <summary>
    /// Text likeliness score for this frame.
    /// </summary>
    public required double TextLikelinessScore { get; init; }
}
