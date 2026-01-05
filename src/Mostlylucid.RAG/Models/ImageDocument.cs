namespace Mostlylucid.RAG.Models;

/// <summary>
/// Represents an image document with multi-vector embeddings for semantic search.
/// Supports multiple embedding types: text (OCR), visual (CLIP), color, and motion.
/// </summary>
public record ImageDocument
{
    /// <summary>
    /// Unique identifier (typically SHA256 hash)
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// File path or URL
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Image format (GIF, PNG, JPG, etc.)
    /// </summary>
    public required string Format { get; init; }

    /// <summary>
    /// Width in pixels
    /// </summary>
    public int Width { get; init; }

    /// <summary>
    /// Height in pixels
    /// </summary>
    public int Height { get; init; }

    /// <summary>
    /// Detected image type (Photo, Screenshot, Diagram, Chart, Meme, Illustration)
    /// </summary>
    public string? DetectedType { get; init; }

    /// <summary>
    /// Type detection confidence (0-1)
    /// </summary>
    public double TypeConfidence { get; init; }

    /// <summary>
    /// Text extracted via OCR (if any)
    /// </summary>
    public string? ExtractedText { get; init; }

    /// <summary>
    /// LLM-generated caption/description (if escalated)
    /// </summary>
    public string? LlmCaption { get; init; }

    /// <summary>
    /// Confidence-weighted salience summary (RRF-fused signals).
    /// Purpose-optimized for semantic search.
    /// </summary>
    public string? SalienceSummary { get; init; }

    /// <summary>
    /// Structured salient signals for filtering/faceted search.
    /// Keys are signal categories, values are the synthesized values.
    /// </summary>
    public Dictionary<string, object?>? SalientSignals { get; init; }

    /// <summary>
    /// Source URL for the image (if from web)
    /// </summary>
    public string? SourceUrl { get; init; }

    /// <summary>
    /// Dominant colors (hex codes)
    /// </summary>
    public string[]? DominantColors { get; init; }

    /// <summary>
    /// GIF/WebP motion direction (for animated images)
    /// </summary>
    public string? MotionDirection { get; init; }

    /// <summary>
    /// Animation complexity type (static, simple-loop, smooth-animation, complex-animation, slideshow)
    /// </summary>
    public string? AnimationType { get; init; }

    /// <summary>
    /// Custom tags or labels
    /// </summary>
    public string[] Tags { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Metadata key-value pairs
    /// </summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>
/// Multi-vector embeddings for an image
/// </summary>
public record ImageEmbeddings
{
    /// <summary>
    /// CLIP text embedding from OCR'd text (768-dimensional for CLIP ViT-B/32)
    /// </summary>
    public float[]? TextEmbedding { get; init; }

    /// <summary>
    /// CLIP visual embedding from image (768-dimensional for CLIP ViT-B/32)
    /// </summary>
    public float[]? VisualEmbedding { get; init; }

    /// <summary>
    /// Color palette embedding (compact representation: RGB histograms or dominant colors)
    /// Typically 64-128 dimensional
    /// </summary>
    public float[]? ColorEmbedding { get; init; }

    /// <summary>
    /// Motion signature for GIF/WebP (direction vector + magnitude + complexity metrics)
    /// Typically 16-32 dimensional
    /// </summary>
    public float[]? MotionEmbedding { get; init; }
}

/// <summary>
/// Search query for multi-vector image search
/// </summary>
public record ImageSearchQuery
{
    /// <summary>
    /// Text query (will be embedded using CLIP text encoder)
    /// </summary>
    public string? TextQuery { get; init; }

    /// <summary>
    /// Visual embedding for similarity search (provide pre-computed embedding)
    /// </summary>
    public float[]? VisualEmbedding { get; init; }

    /// <summary>
    /// Color palette query (provide pre-computed embedding)
    /// </summary>
    public float[]? ColorEmbedding { get; init; }

    /// <summary>
    /// Motion signature query (provide pre-computed embedding)
    /// </summary>
    public float[]? MotionEmbedding { get; init; }

    /// <summary>
    /// Which vectors to search (if null, searches all available vectors with fusion)
    /// </summary>
    public string[]? VectorNames { get; init; }

    /// <summary>
    /// Maximum number of results
    /// </summary>
    public int Limit { get; init; } = 10;

    /// <summary>
    /// Minimum similarity score threshold (0-1)
    /// </summary>
    public float ScoreThreshold { get; init; } = 0.5f;

    /// <summary>
    /// Optional filters (e.g., format, type, has_text)
    /// </summary>
    public Dictionary<string, object>? Filters { get; init; }
}

/// <summary>
/// Image search result with score
/// </summary>
public record ImageSearchResult
{
    /// <summary>
    /// Image document ID (SHA256 hash)
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// File path
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Similarity score (0-1, higher is better)
    /// </summary>
    public float Score { get; init; }

    /// <summary>
    /// Which vector contributed to this match (text, visual, color, motion, fusion)
    /// </summary>
    public string? MatchVector { get; init; }

    /// <summary>
    /// Document metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}
