namespace Mostlylucid.Summarizer.Core.Pipeline;

/// <summary>
/// Common interface for all content processing pipelines.
/// Each modality (doc, image, data) implements this interface.
/// </summary>
public interface IPipeline
{
    /// <summary>
    /// Pipeline identifier (e.g., "doc", "image", "data").
    /// </summary>
    string PipelineId { get; }

    /// <summary>
    /// Human-readable name for the pipeline.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// File extensions this pipeline can process.
    /// </summary>
    IReadOnlySet<string> SupportedExtensions { get; }

    /// <summary>
    /// Check if a file can be processed by this pipeline.
    /// </summary>
    bool CanProcess(string filePath);

    /// <summary>
    /// Process a file and return the result.
    /// </summary>
    Task<PipelineResult> ProcessAsync(
        string filePath,
        PipelineOptions? options = null,
        IProgress<PipelineProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Options for pipeline processing.
/// </summary>
public class PipelineOptions
{
    /// <summary>
    /// Target collection for the processed content.
    /// </summary>
    public string? CollectionId { get; set; }

    /// <summary>
    /// Whether to extract entities (for GraphRAG).
    /// </summary>
    public bool ExtractEntities { get; set; } = true;

    /// <summary>
    /// Additional metadata to attach to results.
    /// </summary>
    public Dictionary<string, object?> Metadata { get; set; } = [];
}

/// <summary>
/// Result of pipeline processing.
/// </summary>
public class PipelineResult
{
    /// <summary>
    /// Whether processing succeeded.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error message if processing failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Source file path.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// Pipeline that processed the file.
    /// </summary>
    public required string PipelineId { get; init; }

    /// <summary>
    /// Extracted content chunks ready for embedding.
    /// </summary>
    public IReadOnlyList<ContentChunk> Chunks { get; init; } = [];

    /// <summary>
    /// Processing time.
    /// </summary>
    public TimeSpan ProcessingTime { get; init; }

    /// <summary>
    /// Additional result metadata.
    /// </summary>
    public Dictionary<string, object?> Metadata { get; init; } = [];

    /// <summary>
    /// Create a successful result.
    /// </summary>
    public static PipelineResult Ok(string filePath, string pipelineId, IReadOnlyList<ContentChunk> chunks, TimeSpan time)
        => new() { Success = true, FilePath = filePath, PipelineId = pipelineId, Chunks = chunks, ProcessingTime = time };

    /// <summary>
    /// Create a failed result.
    /// </summary>
    public static PipelineResult Fail(string filePath, string pipelineId, string error, TimeSpan time)
        => new() { Success = false, FilePath = filePath, PipelineId = pipelineId, Error = error, ProcessingTime = time };
}

/// <summary>
/// Progress information during pipeline processing.
/// </summary>
public record PipelineProgress(
    string Stage,
    string Message,
    double PercentComplete,
    int ItemsProcessed = 0,
    int TotalItems = 0);
