namespace Mostlylucid.DocSummarizer.Images.Models;

/// <summary>
/// Motion analysis results for animated GIFs using optical flow.
/// </summary>
public record GifMotionProfile
{
    /// <summary>
    /// Number of frames in the GIF.
    /// </summary>
    public int FrameCount { get; init; }

    /// <summary>
    /// Frame delay in milliseconds (average).
    /// </summary>
    public int FrameDelayMs { get; init; }

    /// <summary>
    /// Frames per second (calculated).
    /// </summary>
    public double Fps => FrameDelayMs > 0 ? 1000.0 / FrameDelayMs : 0;

    /// <summary>
    /// Total duration in milliseconds.
    /// </summary>
    public int TotalDurationMs { get; init; }

    /// <summary>
    /// Whether the GIF loops.
    /// </summary>
    public bool Loops { get; init; }

    /// <summary>
    /// Dominant motion direction (left, right, up, down, radial, static).
    /// </summary>
    public string MotionDirection { get; init; } = "static";

    /// <summary>
    /// Average motion magnitude in pixels per frame.
    /// </summary>
    public double MotionMagnitude { get; init; }

    /// <summary>
    /// Maximum motion magnitude observed.
    /// </summary>
    public double MaxMotionMagnitude { get; init; }

    /// <summary>
    /// Percentage of frames with significant motion (> threshold).
    /// </summary>
    public double MotionPercentage { get; init; }

    /// <summary>
    /// Regions with significant motion.
    /// </summary>
    public List<MotionRegion>? MotionRegions { get; init; }

    /// <summary>
    /// Per-frame motion data (optional, for detailed analysis).
    /// </summary>
    public List<FrameMotionData>? FrameMotionData { get; init; }

    /// <summary>
    /// Confidence in motion detection (0.0-1.0).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Animation complexity metrics (deterministic analysis).
    /// </summary>
    public GifComplexityProfile? Complexity { get; init; }
}

/// <summary>
/// Motion data for a specific region.
/// </summary>
public record MotionRegion
{
    /// <summary>
    /// X coordinate of region (normalized 0.0-1.0).
    /// </summary>
    public double X { get; init; }

    /// <summary>
    /// Y coordinate of region (normalized 0.0-1.0).
    /// </summary>
    public double Y { get; init; }

    /// <summary>
    /// Width of region (normalized 0.0-1.0).
    /// </summary>
    public double Width { get; init; }

    /// <summary>
    /// Height of region (normalized 0.0-1.0).
    /// </summary>
    public double Height { get; init; }

    /// <summary>
    /// Motion magnitude in this region.
    /// </summary>
    public double Magnitude { get; init; }

    /// <summary>
    /// Dominant direction in this region.
    /// </summary>
    public string Direction { get; init; } = "static";
}

/// <summary>
/// Motion data for a single frame.
/// </summary>
public record FrameMotionData
{
    /// <summary>
    /// Frame index.
    /// </summary>
    public int FrameIndex { get; init; }

    /// <summary>
    /// Average motion magnitude for this frame.
    /// </summary>
    public double Magnitude { get; init; }

    /// <summary>
    /// Dominant direction for this frame.
    /// </summary>
    public string Direction { get; init; } = "static";

    /// <summary>
    /// Horizontal motion component (average).
    /// </summary>
    public double HorizontalMotion { get; init; }

    /// <summary>
    /// Vertical motion component (average).
    /// </summary>
    public double VerticalMotion { get; init; }
}

/// <summary>
/// Complexity profile for animated images (deterministic analysis).
/// </summary>
public record GifComplexityProfile
{
    /// <summary>
    /// Total number of frames in the animation.
    /// </summary>
    public required int FrameCount { get; init; }

    /// <summary>
    /// Visual stability score (0-1).
    /// 1.0 = very stable (consistent frames), 0.0 = chaotic (rapid changes).
    /// </summary>
    public required double VisualStability { get; init; }

    /// <summary>
    /// Color variation across frames (0-1).
    /// 0.0 = consistent colors, 1.0 = wildly changing palette.
    /// </summary>
    public required double ColorVariation { get; init; }

    /// <summary>
    /// Entropy variation (0-1).
    /// Measures how much visual complexity changes across frames.
    /// </summary>
    public required double EntropyVariation { get; init; }

    /// <summary>
    /// Number of detected scene changes (abrupt transitions).
    /// </summary>
    public required int SceneChangeCount { get; init; }

    /// <summary>
    /// Classified animation type:
    /// - "static" - single frame
    /// - "simple-loop" - basic 2-4 frame toggle
    /// - "smooth-animation" - gradual motion
    /// - "complex-animation" - high variation
    /// - "slideshow" - scene cuts/transitions
    /// </summary>
    public required string AnimationType { get; init; }

    /// <summary>
    /// Overall complexity score (0-1).
    /// 0.0 = simple/static, 1.0 = very complex.
    /// </summary>
    public required double OverallComplexity { get; init; }

    /// <summary>
    /// Average frame-to-frame difference (0-1).
    /// </summary>
    public double AverageFrameDifference { get; init; }

    /// <summary>
    /// Maximum frame-to-frame difference (0-1).
    /// </summary>
    public double MaxFrameDifference { get; init; }
}
