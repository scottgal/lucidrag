namespace Mostlylucid.DocSummarizer.Images.Models;

/// <summary>
/// Deterministic image profile containing measured facts about an image.
/// All properties are computed without AI/ML models - just pure image analysis.
/// </summary>
public record ImageProfile
{
    // === Identity ===

    /// <summary>
    /// SHA256 hash of the image file bytes
    /// </summary>
    public required string Sha256 { get; init; }

    /// <summary>
    /// Detected image format (JPEG, PNG, GIF, etc.)
    /// </summary>
    public required string Format { get; init; }

    /// <summary>
    /// Image width in pixels
    /// </summary>
    public required int Width { get; init; }

    /// <summary>
    /// Image height in pixels
    /// </summary>
    public required int Height { get; init; }

    /// <summary>
    /// Width/Height aspect ratio
    /// </summary>
    public required double AspectRatio { get; init; }

    /// <summary>
    /// Whether the image has EXIF metadata
    /// </summary>
    public bool HasExif { get; init; }

    // === Visual Complexity ===

    /// <summary>
    /// Edge density (0-1) - higher = more edges/detail
    /// </summary>
    public required double EdgeDensity { get; init; }

    /// <summary>
    /// Luminance entropy (0-8ish) - higher = more complexity/variation
    /// </summary>
    public required double LuminanceEntropy { get; init; }

    /// <summary>
    /// JPEG compression artifact score (null if not JPEG)
    /// </summary>
    public double? CompressionArtifacts { get; init; }

    // === Brightness/Contrast ===

    /// <summary>
    /// Mean luminance (0-255)
    /// </summary>
    public required double MeanLuminance { get; init; }

    /// <summary>
    /// Standard deviation of luminance
    /// </summary>
    public required double LuminanceStdDev { get; init; }

    /// <summary>
    /// Percentage of pixels that are clipped black (0-100)
    /// </summary>
    public required double ClippedBlacksPercent { get; init; }

    /// <summary>
    /// Percentage of pixels that are clipped white (0-100)
    /// </summary>
    public required double ClippedWhitesPercent { get; init; }

    // === Color ===

    /// <summary>
    /// Dominant colors extracted from the image
    /// </summary>
    public required IReadOnlyList<DominantColor> DominantColors { get; init; }

    /// <summary>
    /// Color grid showing dominant color per cell
    /// </summary>
    public ColorGrid? ColorGrid { get; init; }

    /// <summary>
    /// Mean saturation (0-1)
    /// </summary>
    public required double MeanSaturation { get; init; }

    /// <summary>
    /// Whether the image is mostly grayscale
    /// </summary>
    public required bool IsMostlyGrayscale { get; init; }

    // === Sharpness ===

    /// <summary>
    /// Laplacian variance - higher values indicate sharper images
    /// </summary>
    public required double LaplacianVariance { get; init; }

    // === Text Detection ===

    /// <summary>
    /// Text-likeliness score (0-1) based on edge patterns and high-contrast regions
    /// </summary>
    public required double TextLikeliness { get; init; }

    // === Regions ===

    /// <summary>
    /// Salient regions detected in the image
    /// </summary>
    public IReadOnlyList<SaliencyRegion>? SalientRegions { get; init; }

    // === Type Detection ===

    /// <summary>
    /// Detected image type based on deterministic analysis
    /// </summary>
    public required ImageType DetectedType { get; init; }

    /// <summary>
    /// Confidence in the detected type (0-1)
    /// </summary>
    public double TypeConfidence { get; init; }

    // === Perceptual Hash ===

    /// <summary>
    /// Perceptual hash for deduplication (pHash or dHash)
    /// </summary>
    public string? PerceptualHash { get; init; }
}

/// <summary>
/// A dominant color extracted from an image
/// </summary>
/// <param name="Hex">Hex color code (e.g., "#FF5733")</param>
/// <param name="Percentage">Percentage of the image this color covers (0-100)</param>
/// <param name="Name">Human-readable color name (e.g., "Blue", "Dark Gray")</param>
public record DominantColor(string Hex, double Percentage, string Name);

/// <summary>
/// Color grid showing dominant color per cell
/// </summary>
/// <param name="Cells">List of cell colors</param>
/// <param name="Rows">Number of rows in the grid</param>
/// <param name="Cols">Number of columns in the grid</param>
public record ColorGrid(IReadOnlyList<CellColor> Cells, int Rows, int Cols);

/// <summary>
/// A cell in the color grid
/// </summary>
/// <param name="Row">Row index (0-based)</param>
/// <param name="Col">Column index (0-based)</param>
/// <param name="Hex">Dominant hex color in this cell</param>
/// <param name="Coverage">Coverage percentage (0-1) - how dominant is this color</param>
public record CellColor(int Row, int Col, string Hex, double Coverage);

/// <summary>
/// A salient region detected in the image
/// </summary>
/// <param name="X">X coordinate of the region</param>
/// <param name="Y">Y coordinate of the region</param>
/// <param name="Width">Width of the region</param>
/// <param name="Height">Height of the region</param>
/// <param name="Score">Saliency score (0-1)</param>
public record SaliencyRegion(int X, int Y, int Width, int Height, double Score);
