namespace Mostlylucid.DocSummarizer.Images.Config;

/// <summary>
/// Configuration for image analysis and processing
/// </summary>
public class ImageConfig
{
    public const string SectionName = "Images";

    /// <summary>
    /// Processing mode: ProfileOnly (deterministic), Caption (with model), DocImage (OCR-focused)
    /// </summary>
    public ImageSummaryMode Mode { get; set; } = ImageSummaryMode.Caption;

    /// <summary>
    /// Enable OCR when text-likeliness threshold is crossed
    /// </summary>
    public bool EnableOcr { get; set; } = true;

    /// <summary>
    /// Enable CLIP embedding for image similarity search
    /// </summary>
    public bool EnableClipEmbedding { get; set; } = true;

    /// <summary>
    /// Text-likeliness threshold (0-1) above which OCR is triggered
    /// </summary>
    public double TextLikelinessThreshold { get; set; } = 0.4;

    /// <summary>
    /// Directory to store downloaded models (tessdata, ONNX, etc.)
    /// Defaults to %LOCALAPPDATA%/LucidRAG/models on Windows, ~/.local/share/LucidRAG/models on Linux/macOS
    /// </summary>
    public string ModelsDirectory { get; set; } = GetDefaultModelsDirectory();

    private static string GetDefaultModelsDirectory()
    {
        // Check for environment variable override first
        var envOverride = Environment.GetEnvironmentVariable("LUCIDRAG_MODELS_DIR");
        if (!string.IsNullOrEmpty(envOverride))
        {
            return envOverride;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            // Fallback for systems without LocalApplicationData
            localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }
        return Path.Combine(localAppData, "LucidRAG", "models");
    }

    /// <summary>
    /// Maximum image dimension before resizing (preserves aspect ratio)
    /// Deprecated: Use MaxImageWidth/MaxImageHeight instead
    /// </summary>
    public int MaxImageSize { get; set; } = 2048;

    /// <summary>
    /// Maximum image width before automatic downscaling (default: 4096)
    /// </summary>
    public int? MaxImageWidth { get; set; } = 4096;

    /// <summary>
    /// Maximum image height before automatic downscaling (default: 4096)
    /// </summary>
    public int? MaxImageHeight { get; set; } = 4096;

    /// <summary>
    /// Maximum total pixels before automatic downscaling (default: 16 megapixels)
    /// </summary>
    public long? MaxImagePixels { get; set; } = 16_777_216; // 4096 * 4096

    /// <summary>
    /// Maximum file size in bytes before warning/downscaling (default: 50 MB)
    /// </summary>
    public long? MaxImageFileSize { get; set; } = 50_000_000;

    /// <summary>
    /// Enable streaming image processing to reduce memory usage
    /// </summary>
    public bool UseStreamingProcessing { get; set; } = true;

    /// <summary>
    /// Automatically downscale images that exceed size limits
    /// </summary>
    public bool AutoDownscaleLargeImages { get; set; } = true;

    /// <summary>
    /// Size for generated thumbnails
    /// </summary>
    public int ThumbnailSize { get; set; } = 256;

    /// <summary>
    /// Color grid analysis configuration
    /// </summary>
    public ColorGridConfig ColorGrid { get; set; } = new();

    /// <summary>
    /// Supported image file extensions
    /// </summary>
    public List<string> SupportedExtensions { get; set; } =
    [
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tiff", ".tif"
    ];

    /// <summary>
    /// Tesseract data path (for OCR). If empty, uses system default.
    /// </summary>
    public string? TesseractDataPath { get; set; }

    /// <summary>
    /// Tesseract language (default: eng)
    /// </summary>
    public string TesseractLanguage { get; set; } = "eng";

    /// <summary>
    /// Advanced OCR pipeline configuration (multi-phase processing for animated/static images)
    /// </summary>
    public OcrConfig Ocr { get; set; } = new();

    /// <summary>
    /// Enable vision LLM for captions and entity extraction (requires Ollama with vision model)
    /// </summary>
    public bool EnableVisionLlm { get; set; } = true;

    /// <summary>
    /// Enable Florence-2 for fast local captioning and OCR (ONNX-based, no external services required).
    /// Florence-2 is faster but less accurate than Vision LLM. Good for first-pass analysis.
    /// </summary>
    public bool EnableFlorence2 { get; set; } = true;

    /// <summary>
    /// Florence-2 complexity threshold for escalation to Vision LLM.
    /// Images with edge density above this threshold will escalate to Vision LLM.
    /// Range: 0.0-1.0 (default: 0.3)
    /// </summary>
    public double Florence2ComplexityThreshold { get; set; } = 0.3;

    /// <summary>
    /// Vision LLM model to use (e.g., "llava", "llava:13b", "minicpm-v", "bakllava")
    /// </summary>
    public string? VisionLlmModel { get; set; } = "minicpm-v:8b";

    /// <summary>
    /// Ollama base URL for vision LLM requests
    /// </summary>
    public string? OllamaBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Generate detailed descriptions for complex images (slower, more tokens)
    /// </summary>
    public bool VisionLlmGenerateDetailedDescription { get; set; } = false;

    /// <summary>
    /// Timeout for vision LLM requests (milliseconds)
    /// </summary>
    public int VisionLlmTimeout { get; set; } = 30000;

    /// <summary>
    /// Path to CLIP ONNX model for image embeddings
    /// If not specified, looks in ModelsDirectory/clip/clip-vit-b-32-visual.onnx
    /// </summary>
    public string? ClipModelPath { get; set; }

    /// <summary>
    /// ONNX execution provider configuration.
    /// Controls GPU acceleration for CLIP, EAST, and other ONNX models.
    /// </summary>
    public OnnxExecutionConfig OnnxExecution { get; set; } = new();

    /// <summary>
    /// Contradiction detection configuration for signal validation
    /// </summary>
    public ContradictionConfig Contradiction { get; set; } = new();

    /// <summary>
    /// Motion detection configuration for animated GIF analysis
    /// </summary>
    public MotionConfig Motion { get; set; } = new();

    /// <summary>
    /// Signal importance weights for ranking signals by salience.
    /// Override defaults to customize which signals are prioritized.
    /// </summary>
    public SignalImportanceConfig SignalImportance { get; set; } = new();

    /// <summary>
    /// Complex mode configuration for segment-based parallel document analysis.
    /// Segments pages into regions (text blocks, images, charts, tables) and processes in parallel.
    /// </summary>
    public ComplexModeConfig ComplexMode { get; set; } = new();
}

/// <summary>
/// Configuration for signal importance weights used in signal ranking and context building
/// </summary>
public class SignalImportanceConfig
{
    /// <summary>
    /// Custom signal importance weights (signal key -> weight).
    /// Higher weight = more important. These override default weights.
    /// Example: {"motion.moving_objects": 9.5, "vision.llm.caption": 10.0}
    /// </summary>
    public Dictionary<string, double> CustomWeights { get; set; } = new();

    /// <summary>
    /// Default importance for signals not in CustomWeights or defaults
    /// </summary>
    public double DefaultWeight { get; set; } = 5.0;
}

/// <summary>
/// Configuration for motion detection in animated images
/// </summary>
public class MotionConfig
{
    /// <summary>
    /// Enable motion detection for animated GIFs
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum number of frames to analyze (0 = all frames)
    /// </summary>
    public int MaxFramesToAnalyze { get; set; } = 30;

    /// <summary>
    /// Minimum motion magnitude to consider as significant motion
    /// </summary>
    public double MinMagnitudeThreshold { get; set; } = 0.5;

    /// <summary>
    /// Minimum motion activity (fraction of image) to report motion regions
    /// </summary>
    public double MinActivityThreshold { get; set; } = 0.05;

    /// <summary>
    /// Enable Vision LLM-based identification of WHAT is moving
    /// </summary>
    public bool EnableMotionIdentification { get; set; } = true;
}

/// <summary>
/// Configuration for contradiction detection between analysis signals
/// </summary>
public class ContradictionConfig
{
    /// <summary>
    /// Enable contradiction detection wave
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Reject images with critical contradictions (halt processing)
    /// </summary>
    public bool RejectOnCritical { get; set; } = false;

    /// <summary>
    /// Minimum confidence threshold for signals to be checked
    /// </summary>
    public double MinConfidenceThreshold { get; set; } = 0.5;

    /// <summary>
    /// Enable escalation to Vision LLM for contradiction resolution
    /// </summary>
    public bool EnableLlmEscalation { get; set; } = true;

    /// <summary>
    /// Custom contradiction rules (added to default rules)
    /// </summary>
    public List<CustomContradictionRule>? CustomRules { get; set; }
}

/// <summary>
/// Configuration-based custom contradiction rule
/// </summary>
public class CustomContradictionRule
{
    /// <summary>
    /// Unique identifier for this rule
    /// </summary>
    public required string RuleId { get; set; }

    /// <summary>
    /// Human-readable description
    /// </summary>
    public required string Description { get; set; }

    /// <summary>
    /// First signal key to compare
    /// </summary>
    public required string SignalKeyA { get; set; }

    /// <summary>
    /// Second signal key to compare
    /// </summary>
    public required string SignalKeyB { get; set; }

    /// <summary>
    /// Type: ValueConflict, NumericDivergence, BooleanOpposite, MutuallyExclusive, MissingImplied
    /// </summary>
    public string Type { get; set; } = "ValueConflict";

    /// <summary>
    /// Threshold for numeric comparisons
    /// </summary>
    public double? Threshold { get; set; }

    /// <summary>
    /// Severity: Info, Warning, Error, Critical
    /// </summary>
    public string Severity { get; set; } = "Warning";

    /// <summary>
    /// Resolution: PreferHigherConfidence, PreferMostRecent, MarkConflicting, RemoveBoth, EscalateToLlm, ManualReview
    /// </summary>
    public string Resolution { get; set; } = "PreferHigherConfidence";

    /// <summary>
    /// Whether this rule is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Color grid analysis configuration
/// </summary>
public class ColorGridConfig
{
    /// <summary>
    /// Number of rows in the color grid
    /// </summary>
    public int Rows { get; set; } = 3;

    /// <summary>
    /// Number of columns in the color grid
    /// </summary>
    public int Cols { get; set; } = 3;

    /// <summary>
    /// Target width for downscaling before analysis
    /// </summary>
    public int TargetWidth { get; set; } = 384;

    /// <summary>
    /// Pixel sampling step (higher = faster but less accurate)
    /// </summary>
    public int SampleStep { get; set; } = 2;

    /// <summary>
    /// Bits per channel for color quantization (4 = 16 buckets per channel)
    /// </summary>
    public int BucketBits { get; set; } = 4;
}

/// <summary>
/// Complex mode configuration for segment-based parallel document analysis
/// </summary>
public class ComplexModeConfig
{
    /// <summary>
    /// Enable complex mode processing
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Minimum number of segments required to trigger complex mode
    /// </summary>
    public int MinSegments { get; set; } = 3;

    /// <summary>
    /// Maximum degree of parallelism for segment processing
    /// </summary>
    public int MaxParallelism { get; set; } = 4;

    /// <summary>
    /// Image segment configuration
    /// </summary>
    public ImageSegmentConfig Images { get; set; } = new();
}

/// <summary>
/// Configuration for image segment processing in complex mode
/// </summary>
public class ImageSegmentConfig
{
    /// <summary>
    /// Prefer Vision LLM over Florence-2 for image descriptions
    /// </summary>
    public bool PreferVisionLlm { get; set; } = false;
}

/// <summary>
/// Image summary processing mode
/// </summary>
public enum ImageSummaryMode
{
    /// <summary>
    /// Deterministic analysis only - no vision model
    /// </summary>
    ProfileOnly,

    /// <summary>
    /// Profile + bounded caption from vision model
    /// </summary>
    Caption,

    /// <summary>
    /// Profile + OCR-focused (for screenshots/documents)
    /// </summary>
    DocImage
}

/// <summary>
/// ONNX execution provider configuration for GPU acceleration.
/// Supports cross-platform GPU: DirectML (Windows), CUDA (Linux), CoreML (macOS).
/// </summary>
public class OnnxExecutionConfig
{
    /// <summary>
    /// Preferred execution provider. Auto = best available, CPU = force CPU.
    /// Options: Auto, CPU, DirectML, CUDA, CoreML
    /// </summary>
    public OnnxExecutionProvider PreferredProvider { get; set; } = OnnxExecutionProvider.Auto;

    /// <summary>
    /// Device ID for GPU execution (0 = first GPU, 1 = second GPU, etc.)
    /// </summary>
    public int DeviceId { get; set; } = 0;

    /// <summary>
    /// Enable graph optimization for better performance
    /// </summary>
    public bool EnableGraphOptimization { get; set; } = true;

    /// <summary>
    /// Number of threads for CPU execution (0 = auto based on CPU cores)
    /// </summary>
    public int CpuThreads { get; set; } = 0;

    /// <summary>
    /// Log performance metrics when loading/running models
    /// </summary>
    public bool LogPerformanceMetrics { get; set; } = false;
}

/// <summary>
/// ONNX execution provider type
/// </summary>
public enum OnnxExecutionProvider
{
    /// <summary>
    /// Auto-detect best available provider (GPU if available, fallback to CPU)
    /// </summary>
    Auto,

    /// <summary>
    /// Force CPU execution
    /// </summary>
    CPU,

    /// <summary>
    /// DirectML (Windows GPU - works with AMD, Intel, NVIDIA)
    /// </summary>
    DirectML,

    /// <summary>
    /// CUDA (Linux/Windows NVIDIA GPU)
    /// </summary>
    CUDA,

    /// <summary>
    /// CoreML (macOS GPU/Neural Engine)
    /// </summary>
    CoreML
}
