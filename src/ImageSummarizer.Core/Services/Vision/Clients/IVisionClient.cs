namespace Mostlylucid.DocSummarizer.Images.Services.Vision.Clients;

/// <summary>
/// Interface for vision LLM clients (Ollama, Anthropic, OpenAI, etc.)
/// </summary>
public interface IVisionClient
{
    /// <summary>
    /// Provider name (e.g., "Ollama", "Anthropic", "OpenAI")
    /// </summary>
    string Provider { get; }

    /// <summary>
    /// Check if the vision service is available and configured
    /// </summary>
    Task<(bool Available, string? Message)> CheckAvailabilityAsync(CancellationToken ct = default);

    /// <summary>
    /// Analyze an image with a vision LLM
    /// </summary>
    /// <param name="temperature">Controls randomness: 0.0 = deterministic, 1.0 = creative. Default varies by provider.</param>
    Task<VisionResult> AnalyzeImageAsync(
        string imagePath,
        string prompt,
        string? model = null,
        double? temperature = null,
        CancellationToken ct = default);
}

/// <summary>
/// Result from a vision LLM analysis
/// </summary>
public record VisionResult(
    bool Success,
    string? Error,
    string? Caption,
    string? Model = null,
    string? Provider = null,
    double? ConfidenceScore = null,
    Dictionary<string, object>? Metadata = null,
    List<EvidenceClaim>? Claims = null,
    VisionMetadata? EnhancedMetadata = null);

/// <summary>
/// Evidence-backed claim from vision analysis
/// </summary>
public record EvidenceClaim(
    string Text,
    List<string> Sources,
    List<string>? Evidence = null);

/// <summary>
/// Enhanced metadata extracted from vision models
/// Includes tone, sentiment, style, complexity, and other derived features
/// </summary>
public record VisionMetadata
{
    /// <summary>
    /// Tone/style of the image (e.g., "professional", "casual", "humorous", "formal", "technical")
    /// </summary>
    public string? Tone { get; init; }

    /// <summary>
    /// Sentiment/mood (-1.0 to 1.0: negative to positive)
    /// </summary>
    public double? Sentiment { get; init; }

    /// <summary>
    /// Visual complexity (0.0 to 1.0: simple to complex)
    /// </summary>
    public double? Complexity { get; init; }

    /// <summary>
    /// Aesthetic quality score (0.0 to 1.0)
    /// </summary>
    public double? AestheticScore { get; init; }

    /// <summary>
    /// Primary subject/focus of the image
    /// </summary>
    public string? PrimarySubject { get; init; }

    /// <summary>
    /// Image purpose/intent (e.g., "educational", "entertainment", "commercial", "documentation")
    /// </summary>
    public string? Purpose { get; init; }

    /// <summary>
    /// Target audience (e.g., "general", "technical", "children", "professionals")
    /// </summary>
    public string? TargetAudience { get; init; }

    /// <summary>
    /// Confidence in metadata extraction (0.0 to 1.0)
    /// </summary>
    public double Confidence { get; init; } = 1.0;
};
