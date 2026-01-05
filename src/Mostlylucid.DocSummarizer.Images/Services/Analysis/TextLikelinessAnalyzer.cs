using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Analyzer for detecting text-like regions in images.
/// Uses heuristics based on high-frequency edges, contrast patterns, and horizontal stroke bias.
/// </summary>
public class TextLikelinessAnalyzer
{
    /// <summary>
    /// Calculate text-likeliness score (0-1).
    /// Higher values indicate the image likely contains readable text.
    /// </summary>
    /// <param name="image">Image to analyze</param>
    /// <returns>Text-likeliness score (0-1)</returns>
    public double CalculateTextLikeliness(Image<Rgba32> image)
    {
        // Work on smaller version
        using var workImage = image.Clone();
        var targetWidth = Math.Min(512, workImage.Width);
        if (workImage.Width > targetWidth)
        {
            var scale = targetWidth / (double)workImage.Width;
            workImage.Mutate(x => x.Resize((int)(workImage.Width * scale), (int)(workImage.Height * scale)));
        }

        var width = workImage.Width;
        var height = workImage.Height;

        // Feature 1: High-frequency edge density (text has lots of fine edges)
        var highFreqScore = CalculateHighFrequencyScore(workImage);

        // Feature 2: Bimodal luminance distribution (text is usually dark on light or vice versa)
        var bimodalScore = CalculateBimodalScore(workImage);

        // Feature 3: Horizontal stroke bias (Latin text has horizontal strokes)
        var horizontalBias = CalculateHorizontalBias(workImage);

        // Feature 4: Local contrast variation (text regions have consistent high contrast)
        var contrastScore = CalculateLocalContrastScore(workImage);

        // Combine features with weights
        var score = 0.3 * highFreqScore
                    + 0.25 * bimodalScore
                    + 0.2 * horizontalBias
                    + 0.25 * contrastScore;

        return Math.Clamp(score, 0, 1);
    }

    /// <summary>
    /// Calculate high-frequency edge score
    /// </summary>
    private double CalculateHighFrequencyScore(Image<Rgba32> image)
    {
        var width = image.Width;
        var height = image.Height;
        var highFreqCount = 0;
        var totalCount = 0;

        for (var y = 1; y < height - 1; y += 2)
        {
            var prevRow = image.DangerousGetPixelRowMemory(y - 1).Span;
            var row = image.DangerousGetPixelRowMemory(y).Span;
            var nextRow = image.DangerousGetPixelRowMemory(y + 1).Span;

            for (var x = 1; x < width - 1; x += 2)
            {
                totalCount++;

                // Check for sharp transitions in all directions
                var center = GetLuminance(row[x]);
                var transitions = 0;

                if (Math.Abs(center - GetLuminance(row[x - 1])) > 40) transitions++;
                if (Math.Abs(center - GetLuminance(row[x + 1])) > 40) transitions++;
                if (Math.Abs(center - GetLuminance(prevRow[x])) > 40) transitions++;
                if (Math.Abs(center - GetLuminance(nextRow[x])) > 40) transitions++;

                // High frequency = multiple sharp transitions
                if (transitions >= 2) highFreqCount++;
            }
        }

        return totalCount > 0 ? (double)highFreqCount / totalCount : 0;
    }

    /// <summary>
    /// Calculate bimodal distribution score (text is usually two distinct colors)
    /// </summary>
    private double CalculateBimodalScore(Image<Rgba32> image)
    {
        var histogram = new int[256];
        var totalPixels = 0;

        for (var y = 0; y < image.Height; y += 2)
        {
            var row = image.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < image.Width; x += 2)
            {
                var lum = GetLuminance(row[x]);
                histogram[lum]++;
                totalPixels++;
            }
        }

        if (totalPixels == 0) return 0;

        // Find peaks in histogram (simplified: check dark and light regions)
        var darkSum = 0;
        var lightSum = 0;
        var midSum = 0;

        for (var i = 0; i < 85; i++) darkSum += histogram[i];
        for (var i = 85; i < 170; i++) midSum += histogram[i];
        for (var i = 170; i < 256; i++) lightSum += histogram[i];

        // Bimodal = high dark + high light, low mid
        var bimodal = (darkSum + lightSum) / (double)totalPixels;
        var midRatio = midSum / (double)totalPixels;

        // Score higher if we have both extremes and low middle
        return bimodal * (1 - midRatio);
    }

    /// <summary>
    /// Calculate horizontal stroke bias (text typically has horizontal alignment)
    /// </summary>
    private double CalculateHorizontalBias(Image<Rgba32> image)
    {
        var width = image.Width;
        var height = image.Height;
        var horizontalEdges = 0;
        var verticalEdges = 0;

        for (var y = 1; y < height - 1; y += 2)
        {
            var row = image.DangerousGetPixelRowMemory(y).Span;
            var nextRow = image.DangerousGetPixelRowMemory(y + 1).Span;

            for (var x = 1; x < width - 1; x += 2)
            {
                var current = GetLuminance(row[x]);
                var right = GetLuminance(row[x + 1]);
                var below = GetLuminance(nextRow[x]);

                // Horizontal edge (vertical line)
                if (Math.Abs(current - right) > 30) verticalEdges++;

                // Vertical edge (horizontal line)
                if (Math.Abs(current - below) > 30) horizontalEdges++;
            }
        }

        var total = horizontalEdges + verticalEdges;
        if (total == 0) return 0;

        // Text typically has slight horizontal bias due to baseline alignment
        // but not too much (would indicate lines/borders)
        var ratio = horizontalEdges / (double)total;

        // Optimal ratio for text is around 0.55-0.65
        if (ratio >= 0.5 && ratio <= 0.7)
            return 1.0 - Math.Abs(ratio - 0.58) * 4; // Peak at 0.58

        return 0;
    }

    /// <summary>
    /// Calculate local contrast score (text regions have consistent high contrast)
    /// </summary>
    private double CalculateLocalContrastScore(Image<Rgba32> image)
    {
        var width = image.Width;
        var height = image.Height;
        var highContrastBlocks = 0;
        var totalBlocks = 0;

        var blockSize = 16;

        for (var by = 0; by < height - blockSize; by += blockSize)
        for (var bx = 0; bx < width - blockSize; bx += blockSize)
        {
            totalBlocks++;

            var min = 255;
            var max = 0;

            for (var y = by; y < by + blockSize && y < height; y++)
            {
                var row = image.DangerousGetPixelRowMemory(y).Span;
                for (var x = bx; x < bx + blockSize && x < width; x++)
                {
                    var lum = GetLuminance(row[x]);
                    min = Math.Min(min, lum);
                    max = Math.Max(max, lum);
                }
            }

            // High local contrast (typical for text)
            if (max - min > 100) highContrastBlocks++;
        }

        return totalBlocks > 0 ? (double)highContrastBlocks / totalBlocks : 0;
    }

    private static int GetLuminance(Rgba32 p) =>
        (int)(0.299 * p.R + 0.587 * p.G + 0.114 * p.B);

    /// <summary>
    /// Detect text regions using OpenCV MSER (Maximally Stable Extremal Regions).
    /// MSER is specifically designed for text detection - finds stable connected regions.
    /// </summary>
    public List<TextRegionResult> DetectTextRegionsMser(string imagePath)
    {
        var results = new List<TextRegionResult>();

        try
        {
            using var mat = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (mat.Empty()) return results;

            using var gray = new Mat();
            Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);

            // MSER for dark text on light background
            using var mser = MSER.Create(
                delta: 5,           // Stability threshold
                minArea: 60,        // Min region size (filter noise)
                maxArea: 14400,     // Max region size (filter large blobs)
                maxVariation: 0.25, // Max area variation between levels
                minDiversity: 0.2); // Min diversity of regions

            mser.DetectRegions(gray, out var regions, out var bboxes);

            // Filter and group regions into text-like candidates
            var candidates = new List<(Rect bbox, double score)>();

            foreach (var bbox in bboxes)
            {
                // Calculate aspect ratio (text chars are typically taller than wide or square)
                var aspectRatio = bbox.Width / (double)Math.Max(1, bbox.Height);

                // Text-like aspect ratios: between 0.2 (tall) and 3.0 (wide like "m" or "w")
                if (aspectRatio < 0.1 || aspectRatio > 4.0) continue;

                // Calculate fill ratio (how much of bounding box is filled)
                // Text characters typically have moderate fill ratio
                var area = bbox.Width * bbox.Height;
                if (area < 20 || area > 50000) continue;

                // Score based on aspect ratio (prefer square-ish characters)
                var aspectScore = 1.0 - Math.Abs(aspectRatio - 0.7) / 3.0;
                candidates.Add((bbox, Math.Max(0, aspectScore)));
            }

            // Group nearby regions into text lines
            var grouped = GroupIntoLines(candidates);

            foreach (var line in grouped)
            {
                results.Add(new TextRegionResult
                {
                    BoundingBox = line.bbox,
                    Confidence = line.confidence,
                    CharacterCount = line.charCount,
                    IsHorizontal = line.bbox.Width > line.bbox.Height,
                    RegionType = line.charCount > 3 ? "text_line" : "text_fragment"
                });
            }
        }
        catch
        {
            // MSER detection failed, return empty
        }

        return results;
    }

    /// <summary>
    /// Group MSER regions into text lines based on proximity and alignment.
    /// </summary>
    private List<(Rect bbox, double confidence, int charCount)> GroupIntoLines(
        List<(Rect bbox, double score)> candidates)
    {
        var lines = new List<(Rect bbox, double confidence, int charCount)>();
        if (candidates.Count == 0) return lines;

        // Sort by Y position (top to bottom), then X (left to right)
        var sorted = candidates.OrderBy(c => c.bbox.Y).ThenBy(c => c.bbox.X).ToList();
        var used = new bool[sorted.Count];

        for (int i = 0; i < sorted.Count; i++)
        {
            if (used[i]) continue;

            var lineBoxes = new List<(Rect bbox, double score)> { sorted[i] };
            used[i] = true;

            var baseY = sorted[i].bbox.Y;
            var baseHeight = sorted[i].bbox.Height;

            // Find horizontally adjacent regions at similar Y
            for (int j = i + 1; j < sorted.Count; j++)
            {
                if (used[j]) continue;

                var candidate = sorted[j];

                // Check vertical alignment (within 50% of height)
                var yDiff = Math.Abs(candidate.bbox.Y - baseY);
                if (yDiff > baseHeight * 0.5) continue;

                // Check horizontal proximity (within 3x character width)
                var lastBox = lineBoxes.Last().bbox;
                var xGap = candidate.bbox.X - (lastBox.X + lastBox.Width);
                if (xGap < 0 || xGap > lastBox.Width * 3) continue;

                lineBoxes.Add(candidate);
                used[j] = true;
            }

            // Create merged bounding box for the line
            if (lineBoxes.Count >= 1)
            {
                var minX = lineBoxes.Min(b => b.bbox.X);
                var minY = lineBoxes.Min(b => b.bbox.Y);
                var maxX = lineBoxes.Max(b => b.bbox.X + b.bbox.Width);
                var maxY = lineBoxes.Max(b => b.bbox.Y + b.bbox.Height);

                var avgScore = lineBoxes.Average(b => b.score);
                // Boost confidence for longer lines (more likely to be real text)
                var lengthBoost = Math.Min(1.0, lineBoxes.Count / 5.0);

                lines.Add((
                    new Rect(minX, minY, maxX - minX, maxY - minY),
                    Math.Min(1.0, avgScore + lengthBoost * 0.3),
                    lineBoxes.Count
                ));
            }
        }

        return lines.Where(l => l.charCount >= 2).ToList(); // Filter single-char "lines"
    }

    /// <summary>
    /// Detect if image has structured text layout (document, screenshot, etc.)
    /// using OpenCV contour analysis and stroke width transform approximation.
    /// </summary>
    public (bool IsDocument, double DocumentScore, List<Rect> TextBlocks) DetectDocumentLayout(string imagePath)
    {
        var textBlocks = new List<Rect>();

        try
        {
            using var mat = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (mat.Empty()) return (false, 0, textBlocks);

            using var gray = new Mat();
            using var binary = new Mat();
            using var morphed = new Mat();

            Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);

            // Adaptive threshold for varying lighting
            Cv2.AdaptiveThreshold(gray, binary, 255,
                AdaptiveThresholdTypes.GaussianC,
                ThresholdTypes.BinaryInv, 11, 2);

            // Dilate to connect text characters into blocks
            using var horizontalKernel = Cv2.GetStructuringElement(
                MorphShapes.Rect, new OpenCvSharp.Size(15, 1));
            using var verticalKernel = Cv2.GetStructuringElement(
                MorphShapes.Rect, new OpenCvSharp.Size(1, 3));

            Cv2.Dilate(binary, morphed, horizontalKernel);
            Cv2.Dilate(morphed, morphed, verticalKernel);

            // Find contours (text blocks)
            Cv2.FindContours(morphed, out var contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            var imageArea = mat.Width * mat.Height;
            var validBlocks = 0;

            foreach (var contour in contours)
            {
                var rect = Cv2.BoundingRect(contour);
                var blockArea = rect.Width * rect.Height;

                // Filter by size (not too small, not covering whole image)
                if (blockArea < 200 || blockArea > imageArea * 0.8) continue;

                // Filter by aspect ratio (text blocks are typically wider than tall)
                var aspectRatio = rect.Width / (double)Math.Max(1, rect.Height);
                if (aspectRatio < 0.3 || aspectRatio > 20) continue;

                textBlocks.Add(rect);
                validBlocks++;
            }

            // Calculate document score based on:
            // 1. Number of text blocks
            // 2. Vertical alignment (document layout)
            // 3. Coverage
            var blockScore = Math.Min(1.0, validBlocks / 10.0);
            var coverageScore = textBlocks.Sum(b => b.Width * b.Height) / (double)imageArea;
            var alignmentScore = CalculateVerticalAlignment(textBlocks);

            var documentScore = blockScore * 0.4 + coverageScore * 0.3 + alignmentScore * 0.3;
            var isDocument = documentScore > 0.4 && validBlocks >= 3;

            return (isDocument, documentScore, textBlocks);
        }
        catch
        {
            return (false, 0, textBlocks);
        }
    }

    private double CalculateVerticalAlignment(List<Rect> blocks)
    {
        if (blocks.Count < 2) return 0;

        // Check if blocks share common left edges (document-like alignment)
        var leftEdges = blocks.Select(b => b.X).OrderBy(x => x).ToList();
        var edgeClusters = new Dictionary<int, int>();

        foreach (var edge in leftEdges)
        {
            // Round to nearest 10px for clustering
            var cluster = (edge / 10) * 10;
            edgeClusters[cluster] = edgeClusters.GetValueOrDefault(cluster) + 1;
        }

        // Score based on how many blocks share common alignment
        var maxCluster = edgeClusters.Values.Max();
        return (double)maxCluster / blocks.Count;
    }
}

/// <summary>
/// Result of text region detection.
/// </summary>
public class TextRegionResult
{
    public Rect BoundingBox { get; set; }
    public double Confidence { get; set; }
    public int CharacterCount { get; set; }
    public bool IsHorizontal { get; set; }
    public string RegionType { get; set; } = "unknown";
}
