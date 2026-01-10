using Mostlylucid.DocSummarizer.Images.Models;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Strategy;

/// <summary>
/// Analysis strategy that defines preprocessing and optimization approaches
/// for different image types and analysis goals.
/// </summary>
public record AnalysisStrategy
{
    /// <summary>
    /// Unique identifier for the strategy
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable name
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Description of what this strategy does and when to use it
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Target image types (e.g., "document", "screenshot", "photo", "diagram")
    /// </summary>
    public List<string> TargetTypes { get; init; } = new();

    /// <summary>
    /// Analysis goals this strategy optimizes for (e.g., "ocr", "object_detection", "caption")
    /// </summary>
    public List<string> Goals { get; init; } = new();

    /// <summary>
    /// Preprocessing steps to apply in order
    /// </summary>
    public List<PreprocessingStep> Steps { get; init; } = new();

    /// <summary>
    /// Strategy-specific parameters
    /// </summary>
    public Dictionary<string, object> Parameters { get; init; } = new();

    /// <summary>
    /// Priority when multiple strategies match (higher = preferred)
    /// </summary>
    public int Priority { get; init; } = 0;

    /// <summary>
    /// Check if this strategy is applicable to the given profile and goal
    /// </summary>
    public bool IsApplicable(ImageProfile profile, string goal)
    {
        // Check goal match
        if (Goals.Count > 0 && !Goals.Contains(goal, StringComparer.OrdinalIgnoreCase))
            return false;

        // Check type match
        if (TargetTypes.Count > 0)
        {
            var profileType = DetermineProfileType(profile);
            if (!TargetTypes.Contains(profileType, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static string DetermineProfileType(ImageProfile profile)
    {
        // Determine type based on signals
        if (profile.TextLikeliness > 0.5) return "document";
        if (profile.DetectedType == ImageType.Screenshot) return "screenshot";
        if (profile.DetectedType == ImageType.Diagram || profile.DetectedType == ImageType.Chart) return "diagram";
        if (profile.Format?.Equals("GIF", StringComparison.OrdinalIgnoreCase) == true) return "gif";
        if (profile.TypeConfidence > 0.6) return "photo";

        return "unknown";
    }
}

/// <summary>
/// A preprocessing step in an analysis strategy
/// </summary>
public record PreprocessingStep
{
    /// <summary>
    /// Step identifier
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable name
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Description of what this step does
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Step type (e.g., "binarize", "upscale", "denoise", "grayscale")
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Step-specific parameters
    /// </summary>
    public Dictionary<string, object> Parameters { get; init; } = new();
}

/// <summary>
/// Built-in strategy definitions
/// </summary>
public static class BuiltInStrategies
{
    /// <summary>
    /// Strategy for OCR on book pages and documents
    /// Converts to high-res monochrome for best text extraction
    /// </summary>
    public static AnalysisStrategy BookPageOCR => new()
    {
        Id = "book_page_ocr",
        Name = "Book Page OCR",
        Description = "High-resolution monochrome preprocessing for optimal text extraction from book pages and documents",
        TargetTypes = new() { "document" },
        Goals = new() { "ocr", "text_extraction" },
        Priority = 10,
        Steps = new()
        {
            new PreprocessingStep
            {
                Id = "deskew",
                Name = "Deskew",
                Description = "Correct page rotation and skew",
                Type = "geometric",
                Parameters = new() { { "max_angle", 45 } }
            },
            new PreprocessingStep
            {
                Id = "binarize",
                Name = "Binarize",
                Description = "Convert to high-contrast black and white",
                Type = "color",
                Parameters = new() { { "method", "adaptive" }, { "threshold", 128 } }
            },
            new PreprocessingStep
            {
                Id = "upscale",
                Name = "Upscale",
                Description = "Increase resolution for small text",
                Type = "resize",
                Parameters = new() { { "scale_factor", 2.0 }, { "method", "lanczos" } }
            },
            new PreprocessingStep
            {
                Id = "denoise",
                Name = "Denoise",
                Description = "Remove scanning artifacts and noise",
                Type = "filter",
                Parameters = new() { { "strength", "medium" } }
            }
        }
    };

    /// <summary>
    /// Strategy for object recognition in photos
    /// Black and white conversion simplifies shape detection
    /// </summary>
    public static AnalysisStrategy ObjectRecognition => new()
    {
        Id = "object_recognition",
        Name = "Object Recognition",
        Description = "Grayscale conversion and edge enhancement for easier object detection",
        TargetTypes = new() { "photo", "unknown" },
        Goals = new() { "object_detection", "caption" },
        Priority = 5,
        Steps = new()
        {
            new PreprocessingStep
            {
                Id = "grayscale",
                Name = "Grayscale",
                Description = "Convert to grayscale to focus on shapes",
                Type = "color",
                Parameters = new() { { "method", "luminance" } }
            },
            new PreprocessingStep
            {
                Id = "contrast_boost",
                Name = "Contrast Boost",
                Description = "Enhance contrast for better edge detection",
                Type = "filter",
                Parameters = new() { { "amount", 1.5 } }
            },
            new PreprocessingStep
            {
                Id = "edge_enhance",
                Name = "Edge Enhancement",
                Description = "Sharpen edges for object boundaries",
                Type = "filter",
                Parameters = new() { { "method", "unsharp_mask" } }
            }
        }
    };

    /// <summary>
    /// Strategy for GIF text extraction
    /// Uses temporal voting across frames for consensus
    /// </summary>
    public static AnalysisStrategy GifTextExtraction => new()
    {
        Id = "gif_text_extraction",
        Name = "GIF Text Extraction",
        Description = "Multi-frame temporal voting for robust text extraction from animated GIFs",
        TargetTypes = new() { "gif" },
        Goals = new() { "ocr", "text_extraction" },
        Priority = 15,
        Steps = new()
        {
            new PreprocessingStep
            {
                Id = "frame_extraction",
                Name = "Frame Extraction",
                Description = "Extract all frames from GIF",
                Type = "temporal",
                Parameters = new() { { "max_frames", 100 } }
            },
            new PreprocessingStep
            {
                Id = "stabilization",
                Name = "Frame Stabilization",
                Description = "Align frames using optical flow",
                Type = "temporal",
                Parameters = new() { { "method", "optical_flow" } }
            },
            new PreprocessingStep
            {
                Id = "temporal_voting",
                Name = "Temporal Voting",
                Description = "Vote across frames for consensus text",
                Type = "temporal",
                Parameters = new() { { "voting_method", "majority" }, { "min_confidence", 0.7 } }
            }
        }
    };

    /// <summary>
    /// Strategy for screenshot analysis
    /// Preserves UI elements and text clarity
    /// </summary>
    public static AnalysisStrategy ScreenshotAnalysis => new()
    {
        Id = "screenshot_analysis",
        Name = "Screenshot Analysis",
        Description = "Preserve text clarity and UI element boundaries",
        TargetTypes = new() { "screenshot" },
        Goals = new() { "caption", "ocr", "ui_analysis" },
        Priority = 8,
        Steps = new()
        {
            new PreprocessingStep
            {
                Id = "text_region_detection",
                Name = "Text Region Detection",
                Description = "Identify text regions for selective processing",
                Type = "detection",
                Parameters = new() { { "method", "mser" } }
            },
            new PreprocessingStep
            {
                Id = "selective_sharpen",
                Name = "Selective Sharpening",
                Description = "Sharpen text regions only",
                Type = "filter",
                Parameters = new() { { "region_based", true } }
            }
        }
    };

    /// <summary>
    /// Strategy for diagram and chart analysis
    /// Vectorization and color quantization for structure extraction
    /// </summary>
    public static AnalysisStrategy DiagramAnalysis => new()
    {
        Id = "diagram_analysis",
        Name = "Diagram Analysis",
        Description = "Color quantization and vectorization for structural understanding",
        TargetTypes = new() { "diagram" },
        Goals = new() { "caption", "structure_extraction" },
        Priority = 12,
        Steps = new()
        {
            new PreprocessingStep
            {
                Id = "color_quantization",
                Name = "Color Quantization",
                Description = "Reduce to key colors for region segmentation",
                Type = "color",
                Parameters = new() { { "num_colors", 8 }, { "method", "kmeans" } }
            },
            new PreprocessingStep
            {
                Id = "edge_detection",
                Name = "Edge Detection",
                Description = "Extract diagram structure",
                Type = "filter",
                Parameters = new() { { "method", "canny" } }
            },
            new PreprocessingStep
            {
                Id = "line_detection",
                Name = "Line Detection",
                Description = "Detect arrows, connections, boundaries",
                Type = "detection",
                Parameters = new() { { "method", "hough" } }
            }
        }
    };

    /// <summary>
    /// Get all built-in strategies
    /// </summary>
    public static List<AnalysisStrategy> All => new()
    {
        BookPageOCR,
        ObjectRecognition,
        GifTextExtraction,
        ScreenshotAnalysis,
        DiagramAnalysis
    };
}
