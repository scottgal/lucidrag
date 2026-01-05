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
    /// Directory to store downloaded ONNX models (Florence-2, CLIP)
    /// </summary>
    public string ModelsDirectory { get; set; } = "./models";

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
