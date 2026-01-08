namespace Mostlylucid.DataSummarizer.Configuration;

/// <summary>
/// ONNX embedding configuration for DataSummarizer
/// </summary>
public class OnnxConfig
{
    /// <summary>
    /// Enable ONNX-based embeddings (auto-downloads model on first run)
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Embedding model to use
    /// </summary>
    public OnnxEmbeddingModel EmbeddingModel { get; set; } = OnnxEmbeddingModel.AllMiniLmL6V2;

    /// <summary>
    /// Use quantized model (smaller, faster, slightly less accurate)
    /// </summary>
    public bool UseQuantized { get; set; } = true;

    /// <summary>
    /// Directory to store downloaded models.
    /// Defaults to {AppDirectory}/models
    /// </summary>
    public string ModelDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "models");

    /// <summary>
    /// Maximum sequence length for embeddings (truncates longer texts)
    /// </summary>
    public int MaxEmbeddingSequenceLength { get; set; } = 256;

    /// <summary>
    /// Number of threads for inference (0 = auto)
    /// </summary>
    public int InferenceThreads { get; set; } = 0;

    /// <summary>
    /// Use parallel execution mode for batched inference
    /// </summary>
    public bool UseParallelExecution { get; set; } = true;

    /// <summary>
    /// Inter-op threads for parallel execution (0 = auto)
    /// </summary>
    public int InterOpThreads { get; set; } = 0;

    /// <summary>
    /// Execution provider preference
    /// </summary>
    public OnnxExecutionProvider ExecutionProvider { get; set; } = OnnxExecutionProvider.Auto;

    /// <summary>
    /// GPU device ID (for CUDA/DirectML)
    /// </summary>
    public int GpuDeviceId { get; set; } = 0;

    /// <summary>
    /// Batch size for embedding multiple texts
    /// </summary>
    public int EmbeddingBatchSize { get; set; } = 16;
}

/// <summary>
/// Supported ONNX embedding models
/// </summary>
public enum OnnxEmbeddingModel
{
    /// <summary>
    /// all-MiniLM-L6-v2: General purpose, 384 dimensions, ~23MB quantized
    /// Best balance of speed and quality for most use cases
    /// </summary>
    AllMiniLmL6V2,

    /// <summary>
    /// bge-small-en-v1.5: Higher quality, 384 dimensions, ~34MB quantized
    /// Better for semantic search, requires instruction prefix
    /// </summary>
    BgeSmallEnV15,

    /// <summary>
    /// gte-small: General Text Embeddings, 384 dimensions, ~34MB quantized
    /// Good for diverse text types
    /// </summary>
    GteSmall,

    /// <summary>
    /// multi-qa-MiniLM-L6-cos-v1: Optimized for QA retrieval, 384 dimensions
    /// Best for question-answering scenarios
    /// </summary>
    MultiQaMiniLm,

    /// <summary>
    /// paraphrase-MiniLM-L3-v2: Fastest, smallest, 384 dimensions, ~17MB quantized
    /// Best for paraphrase detection and simple similarity
    /// </summary>
    ParaphraseMiniLmL3
}

/// <summary>
/// ONNX execution provider preference
/// </summary>
public enum OnnxExecutionProvider
{
    /// <summary>
    /// CPU-only execution
    /// </summary>
    Cpu,

    /// <summary>
    /// Auto-detect best available (DirectML > CUDA > CPU)
    /// </summary>
    Auto,

    /// <summary>
    /// NVIDIA CUDA (requires CUDA toolkit)
    /// </summary>
    Cuda,

    /// <summary>
    /// DirectML (Windows GPU acceleration)
    /// </summary>
    DirectMl
}
