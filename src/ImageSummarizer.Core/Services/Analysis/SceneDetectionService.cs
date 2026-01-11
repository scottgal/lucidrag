using Microsoft.Extensions.Logging;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Scene detection service using histogram-based comparison.
/// Extracted to its own service for:
/// 1. Single Responsibility - scene detection is separate from captioning
/// 2. Early execution - run in pipeline to inform escalation decisions
/// 3. Reusability - used by Florence2, VisionLLM, MlOcr, etc.
///
/// Based on research: histogram-based methods achieve ~1.7ms per comparison
/// with F1=0.6+ accuracy, scaling well to video processing.
/// </summary>
public class SceneDetectionService
{
    private readonly ILogger<SceneDetectionService>? _logger;

    public SceneDetectionService(ILogger<SceneDetectionService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Detect scenes in an animated image using histogram-based motion detection.
    /// Returns frame indices at the END of each scene (where subtitles are complete).
    /// </summary>
    /// <param name="image">The loaded animated image</param>
    /// <param name="maxScenes">Maximum number of scenes to return</param>
    /// <returns>Scene detection result with frame indices and metrics</returns>
    public AnimatedSceneResult DetectScenes(Image<Rgba32> image, int maxScenes = 4)
    {
        var frameCount = image.Frames.Count;

        if (frameCount <= 1)
        {
            return new AnimatedSceneResult
            {
                TotalFrames = frameCount,
                SceneCount = 1,
                SceneEndFrameIndices = new List<int> { 0 },
                LastSceneFrameIndex = 0,
                SceneMotionScores = new List<double>(),
                AverageMotion = 0,
                UsedMotionDetection = false
            };
        }

        if (frameCount <= maxScenes)
        {
            // Few frames - just return all frame indices
            var allIndices = Enumerable.Range(0, frameCount).ToList();
            return new AnimatedSceneResult
            {
                TotalFrames = frameCount,
                SceneCount = frameCount,
                SceneEndFrameIndices = allIndices,
                LastSceneFrameIndex = frameCount - 1,
                SceneMotionScores = new List<double>(),
                AverageMotion = 0,
                UsedMotionDetection = false
            };
        }

        // OPTIMIZATION: For large frame counts, sample frames instead of analyzing all
        // This scales better for video processing (sample every Nth frame)
        var sampleInterval = frameCount > 100 ? frameCount / 50 : 1; // Sample ~50 frames max
        var sampledFrameIndices = new List<int>();
        for (int i = 0; i < frameCount; i += sampleInterval)
        {
            sampledFrameIndices.Add(i);
        }
        if (!sampledFrameIndices.Contains(frameCount - 1))
        {
            sampledFrameIndices.Add(frameCount - 1);
        }

        // Calculate histogram-based motion scores between sampled frames
        var motionScores = new List<(int frameIdx, double score)>();
        int[]? prevHist = null;
        int prevFrameIdx = -1;

        foreach (var frameIdx in sampledFrameIndices)
        {
            using var currentFrame = image.Frames.CloneFrame(frameIdx);
            var currentHist = ComputeColorHistogram(currentFrame);

            if (prevHist != null)
            {
                var motionScore = CompareHistograms(prevHist, currentHist);
                motionScores.Add((prevFrameIdx, motionScore)); // Score for frame BEFORE the change
            }

            prevHist = currentHist;
            prevFrameIdx = frameIdx;
        }

        if (motionScores.Count == 0)
        {
            return new AnimatedSceneResult
            {
                TotalFrames = frameCount,
                SceneCount = 2,
                SceneEndFrameIndices = new List<int> { 0, Math.Max(0, frameCount - 1) },
                LastSceneFrameIndex = Math.Max(0, frameCount - 1),
                SceneMotionScores = new List<double>(),
                AverageMotion = 0,
                UsedMotionDetection = false
            };
        }

        // Find scene boundaries (frames with high motion = scene change)
        var avgMotion = motionScores.Average(m => m.score);
        var stdDev = Math.Sqrt(motionScores.Average(m => Math.Pow(m.score - avgMotion, 2)));
        var threshold = avgMotion + stdDev; // Scene change = above average + 1 std dev

        // Find frames at the END of each scene (just before high motion)
        var sceneEndFrames = new List<int>();
        var sceneMotionScores = new List<double>();

        // Always include first frame as start of first scene
        sceneEndFrames.Add(0);

        for (int i = 0; i < motionScores.Count; i++)
        {
            var (frameIdx, score) = motionScores[i];

            // High motion detected = this is end of a scene
            if (score > threshold)
            {
                // Add the frame BEFORE the scene change (end of current scene)
                if (!sceneEndFrames.Contains(frameIdx))
                {
                    sceneEndFrames.Add(frameIdx);
                    sceneMotionScores.Add(score);
                }
            }
        }

        // Always include the last frame
        var lastFrame = frameCount - 1;
        if (!sceneEndFrames.Contains(lastFrame))
        {
            sceneEndFrames.Add(lastFrame);
        }

        // If we have too many scenes, select the most significant ones
        if (sceneEndFrames.Count > maxScenes)
        {
            // Keep first, last, and highest motion changes
            var sorted = sceneEndFrames
                .Select(idx => (idx, score: motionScores.FirstOrDefault(m => m.frameIdx == idx).score))
                .OrderByDescending(x => x.score)
                .Take(maxScenes - 2) // Reserve spots for first and last
                .Select(x => x.idx)
                .ToList();

            sorted.Add(0);
            sorted.Add(lastFrame);
            sceneEndFrames = sorted.Distinct().OrderBy(x => x).ToList();
        }

        // Ensure unique frames that are visually different
        sceneEndFrames = DeduplicateSceneFrames(image, sceneEndFrames);

        var result = new AnimatedSceneResult
        {
            TotalFrames = frameCount,
            SceneCount = sceneEndFrames.Count,
            SceneEndFrameIndices = sceneEndFrames,
            LastSceneFrameIndex = sceneEndFrames.Count > 0 ? sceneEndFrames[^1] : 0,
            SceneMotionScores = sceneMotionScores,
            AverageMotion = avgMotion,
            UsedMotionDetection = true
        };

        _logger?.LogDebug(
            "Scene detection: {SceneCount} scenes from {TotalFrames} frames at [{Indices}] (avgMotion={AvgMotion:F3})",
            result.SceneCount, result.TotalFrames,
            string.Join(", ", result.SceneEndFrameIndices), result.AverageMotion);

        return result;
    }

    /// <summary>
    /// Detect UNIQUE text frames using ML-detected bounding boxes and OpenCV histogram comparison.
    /// Only processes frames with actually different text content.
    /// Uses OpenCV's Cv2.CompareHist for industry-standard perceptual similarity.
    /// </summary>
    /// <param name="image">The animated image</param>
    /// <param name="textBoundingBoxes">ML-detected text bounding boxes from AutoRoutingWave</param>
    /// <param name="maxFrames">Maximum unique frames to return</param>
    /// <param name="similarityThreshold">Histogram correlation threshold (0.85 = 85% similar)</param>
    /// <returns>Frame indices with unique text content</returns>
    public List<int> DetectUniqueTextFrames(
        Image<Rgba32> image,
        List<OpenCvSharp.Rect> textBoundingBoxes,
        int maxFrames = 4,
        double similarityThreshold = 0.85)
    {
        var frameCount = image.Frames.Count;
        if (frameCount <= 1 || textBoundingBoxes.Count == 0)
            return new List<int> { 0 };

        var uniqueFrames = new List<int> { 0 }; // Always include first frame
        List<Mat>? prevHistograms = null;

        for (int i = 0; i < frameCount && uniqueFrames.Count < maxFrames; i++)
        {
            using var frame = image.Frames.CloneFrame(i);

            // Compute OpenCV histograms for text regions
            var currentHistograms = ComputeTextRegionHistograms(frame, textBoundingBoxes);

            if (prevHistograms != null)
            {
                // Compare histograms using OpenCV correlation
                var similarity = CompareOpenCvHistograms(prevHistograms, currentHistograms);

                if (similarity < similarityThreshold) // Different text content
                {
                    uniqueFrames.Add(i);
                    _logger?.LogDebug("Unique text frame {Frame} (correlation={Sim:F3})", i, similarity);

                    // Dispose previous histograms and store current
                    foreach (var hist in prevHistograms) hist.Dispose();
                    prevHistograms = currentHistograms;
                }
                else
                {
                    // Frames are similar, dispose current histograms
                    foreach (var hist in currentHistograms) hist.Dispose();
                }
            }
            else
            {
                prevHistograms = currentHistograms;
            }
        }

        // Clean up final histograms
        if (prevHistograms != null)
        {
            foreach (var hist in prevHistograms) hist.Dispose();
        }

        // Always include last frame if not already included
        if (!uniqueFrames.Contains(frameCount - 1) && uniqueFrames.Count < maxFrames)
            uniqueFrames.Add(frameCount - 1);

        return uniqueFrames;
    }

    /// <summary>
    /// Compute OpenCV grayscale histograms for text regions.
    /// One histogram per text bounding box for precise comparison.
    /// Uses 256 bins (0-255 luminance range) with normalization.
    /// </summary>
    private List<Mat> ComputeTextRegionHistograms(
        Image<Rgba32> frame,
        List<OpenCvSharp.Rect> textBoxes)
    {
        var histograms = new List<Mat>();

        foreach (var box in textBoxes)
        {
            // Convert ImageSharp text region to OpenCV Mat
            using var mat = ConvertTextRegionToGrayscaleMat(frame, box);

            // Compute grayscale histogram (256 bins, 0-255 range)
            var hist = new Mat();
            Cv2.CalcHist(
                images: new[] { mat },
                channels: new[] { 0 },
                mask: null,
                hist: hist,
                dims: 1,
                histSize: new[] { 256 },
                ranges: new[] { new Rangef(0, 256) });

            // Normalize histogram for consistent comparison (0-1 range)
            Cv2.Normalize(hist, hist, 0, 1, NormTypes.MinMax);

            histograms.Add(hist);
        }

        return histograms;
    }

    /// <summary>
    /// Convert ImageSharp text region to OpenCV Mat (grayscale).
    /// Clamps box to frame bounds to avoid out-of-range errors.
    /// </summary>
    private Mat ConvertTextRegionToGrayscaleMat(Image<Rgba32> frame, OpenCvSharp.Rect box)
    {
        // Clamp box to frame bounds
        var x = Math.Max(0, box.X);
        var y = Math.Max(0, box.Y);
        var width = Math.Min(box.Width, frame.Width - x);
        var height = Math.Min(box.Height, frame.Height - y);

        if (width <= 0 || height <= 0)
        {
            // Invalid box - return 1x1 black mat
            return new Mat(1, 1, MatType.CV_8UC1, Scalar.All(0));
        }

        // Extract region as grayscale
        var mat = new Mat(height, width, MatType.CV_8UC1);

        frame.ProcessPixelRows(accessor =>
        {
            for (int dy = 0; dy < height; dy++)
            {
                var rowSpan = accessor.GetRowSpan(y + dy);
                for (int dx = 0; dx < width; dx++)
                {
                    var pixel = rowSpan[x + dx];
                    // Convert to grayscale using ITU-R BT.601 formula
                    var luminance = (byte)(0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B);
                    mat.Set(dy, dx, luminance);
                }
            }
        });

        return mat;
    }

    /// <summary>
    /// Compare two sets of OpenCV histograms using correlation method.
    /// Returns average correlation: 1.0 = identical, 0.0 = completely different.
    /// Uses Cv2.CompareHist with CORREL method (industry standard).
    /// </summary>
    private double CompareOpenCvHistograms(List<Mat> histograms1, List<Mat> histograms2)
    {
        if (histograms1.Count != histograms2.Count || histograms1.Count == 0)
            return 0.0;

        var totalCorrelation = 0.0;
        for (int i = 0; i < histograms1.Count; i++)
        {
            // Correlation method returns -1 to 1, where 1 = identical
            var correlation = Cv2.CompareHist(histograms1[i], histograms2[i], HistCompMethods.Correl);
            totalCorrelation += Math.Max(0, correlation); // Clamp negative correlations to 0
        }

        return totalCorrelation / histograms1.Count;
    }

    /// <summary>
    /// Detect frames where text changes (even if overall scene doesn't).
    /// Useful for detecting subtitle changes, book pages, etc.
    /// Focuses on the bottom 25% of the image where subtitles typically appear.
    ///
    /// LEGACY METHOD: Use DetectUniqueTextFrames with ML bounding boxes for better performance.
    /// </summary>
    /// <param name="image">The animated image</param>
    /// <param name="maxTextFrames">Maximum frames to return</param>
    /// <returns>List of frame indices where text regions changed significantly</returns>
    public List<int> DetectTextChangeFrames(Image<Rgba32> image, int maxTextFrames = 4)
    {
        var frameCount = image.Frames.Count;

        if (frameCount <= 1)
            return new List<int> { 0 };

        // Sample frames for text region comparison
        var sampleInterval = frameCount > 50 ? frameCount / 25 : 1;
        var textChangeFrames = new List<int> { 0 }; // Always include first frame

        int[]? prevTextRegionHist = null;
        int prevIdx = 0;

        for (int i = 0; i < frameCount; i += sampleInterval)
        {
            using var frame = image.Frames.CloneFrame(i);
            var textHist = ComputeTextRegionHistogram(frame);

            if (prevTextRegionHist != null)
            {
                // Compare text region only (more sensitive than full frame)
                var diff = CompareHistograms(prevTextRegionHist, textHist);

                // Lower threshold for text regions (text changes are subtle)
                if (diff > 0.05) // 5% difference in text region = text changed
                {
                    // Add the frame where text changed
                    textChangeFrames.Add(i);
                    prevTextRegionHist = textHist;
                    prevIdx = i;

                    _logger?.LogDebug("Text change detected at frame {Frame} (diff={Diff:F3})", i, diff);
                }
            }
            else
            {
                prevTextRegionHist = textHist;
                prevIdx = i;
            }
        }

        // Always include last frame
        if (!textChangeFrames.Contains(frameCount - 1))
            textChangeFrames.Add(frameCount - 1);

        // Limit to maxTextFrames
        if (textChangeFrames.Count > maxTextFrames)
        {
            // Keep first, last, and evenly distributed middle frames
            var result = new List<int> { textChangeFrames[0] };
            var step = textChangeFrames.Count / (maxTextFrames - 1);
            for (int i = step; i < textChangeFrames.Count - 1; i += step)
            {
                result.Add(textChangeFrames[i]);
                if (result.Count >= maxTextFrames - 1) break;
            }
            result.Add(textChangeFrames[^1]);
            return result.Distinct().OrderBy(x => x).ToList();
        }

        return textChangeFrames;
    }

    /// <summary>
    /// Compute histogram for text region (bottom 25% of image).
    /// Subtitles and captions typically appear in this region.
    /// </summary>
    private static int[] ComputeTextRegionHistogram(Image<Rgba32> frame)
    {
        const int binCount = 64;
        var hist = new int[binCount * 3];

        // Text region: bottom 25% of image
        var textRegionStart = (int)(frame.Height * 0.75);
        var sampleStep = Math.Max(1, frame.Width / 64);

        for (int y = textRegionStart; y < frame.Height; y += 2)
        {
            for (int x = 0; x < frame.Width; x += sampleStep)
            {
                var p = frame[x, y];
                hist[p.R * binCount / 256]++;
                hist[binCount + p.G * binCount / 256]++;
                hist[2 * binCount + p.B * binCount / 256]++;
            }
        }

        return hist;
    }

    /// <summary>
    /// Enhanced scene detection that also considers text region changes.
    /// Combines full-frame scene detection with text-region change detection.
    /// </summary>
    public AnimatedSceneResult DetectScenesWithTextAwareness(Image<Rgba32> image, int maxScenes = 4)
    {
        // First, run standard scene detection
        var sceneResult = DetectScenes(image, maxScenes);

        // Then, detect text changes that might be missed by scene detection
        var textFrames = DetectTextChangeFrames(image, maxScenes);

        // Merge scene frames and text change frames
        var allFrames = sceneResult.SceneEndFrameIndices
            .Concat(textFrames)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        // If we have too many, prioritize:
        // 1. First frame (scene start)
        // 2. Last frame (scene end/final text)
        // 3. High-motion scene boundaries
        // 4. Text change frames
        if (allFrames.Count > maxScenes)
        {
            var prioritized = new List<int> { allFrames[0], allFrames[^1] };

            // Add scene boundaries first (they're more significant)
            foreach (var idx in sceneResult.SceneEndFrameIndices)
            {
                if (!prioritized.Contains(idx) && prioritized.Count < maxScenes)
                    prioritized.Add(idx);
            }

            // Then add text change frames
            foreach (var idx in textFrames)
            {
                if (!prioritized.Contains(idx) && prioritized.Count < maxScenes)
                    prioritized.Add(idx);
            }

            allFrames = prioritized.Distinct().OrderBy(x => x).ToList();
        }

        return new AnimatedSceneResult
        {
            TotalFrames = sceneResult.TotalFrames,
            SceneCount = allFrames.Count,
            SceneEndFrameIndices = allFrames,
            LastSceneFrameIndex = allFrames.Count > 0 ? allFrames[^1] : 0,
            SceneMotionScores = sceneResult.SceneMotionScores,
            AverageMotion = sceneResult.AverageMotion,
            UsedMotionDetection = true,
            TextChangeFrameCount = textFrames.Count
        };
    }

    /// <summary>
    /// Quick check if an animated image has significant scene changes.
    /// Useful for early escalation decisions without full scene detection.
    /// </summary>
    public bool HasSignificantSceneChanges(Image<Rgba32> image, double threshold = 0.15)
    {
        if (image.Frames.Count <= 2)
            return false;

        // Sample 3 frames: first, middle, last
        var middleIdx = image.Frames.Count / 2;
        using var first = image.Frames.CloneFrame(0);
        using var middle = image.Frames.CloneFrame(middleIdx);
        using var last = image.Frames.CloneFrame(image.Frames.Count - 1);

        var hist1 = ComputeColorHistogram(first);
        var hist2 = ComputeColorHistogram(middle);
        var hist3 = ComputeColorHistogram(last);

        var diff1 = CompareHistograms(hist1, hist2);
        var diff2 = CompareHistograms(hist2, hist3);

        return diff1 > threshold || diff2 > threshold;
    }

    /// <summary>
    /// Compute color histogram for a frame (64 bins per channel = 192 total).
    /// Histograms can be cached for video processing.
    /// </summary>
    private static int[] ComputeColorHistogram(Image<Rgba32> frame)
    {
        const int binCount = 64;
        var hist = new int[binCount * 3]; // R, G, B histograms concatenated

        var sampleStep = Math.Max(2, Math.Min(frame.Width, frame.Height) / 64);

        for (int y = 0; y < frame.Height; y += sampleStep)
        {
            for (int x = 0; x < frame.Width; x += sampleStep)
            {
                var p = frame[x, y];
                hist[p.R * binCount / 256]++;
                hist[binCount + p.G * binCount / 256]++;
                hist[2 * binCount + p.B * binCount / 256]++;
            }
        }

        return hist;
    }

    /// <summary>
    /// Compare two histograms using histogram intersection.
    /// Returns difference score: 0 = identical, 1 = completely different.
    /// </summary>
    private static double CompareHistograms(int[] hist1, int[] hist2)
    {
        long intersection = 0;
        long total1 = 0;

        for (int i = 0; i < hist1.Length; i++)
        {
            intersection += Math.Min(hist1[i], hist2[i]);
            total1 += hist1[i];
        }

        if (total1 == 0) return 0;

        // Intersection / total gives similarity (0-1), we want difference
        var similarity = (double)intersection / total1;
        return 1.0 - similarity;
    }

    /// <summary>
    /// Remove visually similar frames from the scene list.
    /// </summary>
    private List<int> DeduplicateSceneFrames(Image<Rgba32> image, List<int> frameIndices)
    {
        if (frameIndices.Count <= 2)
            return frameIndices;

        var uniqueIndices = new List<int> { frameIndices[0] };
        int[]? lastUniqueHist = null;

        foreach (var frameIdx in frameIndices)
        {
            using var currentFrame = image.Frames.CloneFrame(frameIdx);
            var currentHist = ComputeColorHistogram(currentFrame);

            if (lastUniqueHist == null)
            {
                lastUniqueHist = currentHist;
                continue;
            }

            // Check if visually different from last unique frame (>8% different)
            var diff = CompareHistograms(lastUniqueHist, currentHist);
            if (diff > 0.08)
            {
                uniqueIndices.Add(frameIdx);
                lastUniqueHist = currentHist;
            }
        }

        return uniqueIndices;
    }
}

/// <summary>
/// Scene detection result for animated images.
/// Exposes scene boundaries for reuse by other processing waves.
/// </summary>
public record AnimatedSceneResult
{
    /// <summary>Total number of frames in the animation.</summary>
    public int TotalFrames { get; init; }

    /// <summary>Number of distinct scenes detected.</summary>
    public int SceneCount { get; init; }

    /// <summary>Frame indices at the end of each scene (where subtitles are complete).</summary>
    public List<int> SceneEndFrameIndices { get; init; } = new();

    /// <summary>Index of the last scene's end frame (best for captioning).</summary>
    public int LastSceneFrameIndex { get; init; }

    /// <summary>Motion scores for each scene transition (higher = more motion).</summary>
    public List<double> SceneMotionScores { get; init; } = new();

    /// <summary>Average motion across all frames (0-1 scale).</summary>
    public double AverageMotion { get; init; }

    /// <summary>Whether motion-based scene detection was used (vs interval sampling).</summary>
    public bool UsedMotionDetection { get; init; }

    /// <summary>Number of frames where text region changed (detected separately from scene changes).</summary>
    public int TextChangeFrameCount { get; init; }

    /// <summary>
    /// Suggest whether LLM escalation is needed based on scene complexity.
    /// More scenes = more complex animation = more likely to need LLM.
    /// Text changes also suggest need for OCR.
    /// </summary>
    public bool SuggestEscalation => SceneCount > 2 || AverageMotion > 0.1 || TextChangeFrameCount > 2;

    /// <summary>
    /// Suggest whether text extraction should be prioritized.
    /// True if text region changes were detected.
    /// </summary>
    public bool SuggestTextExtraction => TextChangeFrameCount > 1;
}
