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
    /// </summary>
    public int MaxImageSize { get; set; } = 2048;

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
