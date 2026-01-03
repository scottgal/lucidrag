namespace Mostlylucid.DocSummarizer.Config;

/// <summary>
///     Backend type for embedding operations
/// </summary>
public enum EmbeddingBackend
{
    /// <summary>
    ///     ONNX Runtime - zero-config, auto-downloads models, fast
    /// </summary>
    Onnx,

    /// <summary>
    ///     Ollama - requires Ollama server
    /// </summary>
    Ollama
}

/// <summary>
///     Backend type for LLM text generation
/// </summary>
public enum LlmBackend
{
    /// <summary>
    ///     No LLM - deterministic mode only (BERT extraction without synthesis)
    /// </summary>
    None,

    /// <summary>
    ///     Ollama - local LLM server (default)
    /// </summary>
    Ollama,

    /// <summary>
    ///     Anthropic Claude API
    /// </summary>
    Anthropic,

    /// <summary>
    ///     OpenAI API (GPT-4o, etc.)
    /// </summary>
    OpenAI
}

/// <summary>
///     ONNX execution provider (GPU vs CPU)
/// </summary>
public enum OnnxExecutionProvider
{
    /// <summary>CPU only (default, always works)</summary>
    Cpu,
    /// <summary>NVIDIA CUDA GPU</summary>
    Cuda,
    /// <summary>DirectML (Windows GPU, works with AMD/Intel/NVIDIA)</summary>
    DirectMl,
    /// <summary>Auto-detect best available</summary>
    Auto
}

/// <summary>
///     ONNX embedding configuration
/// </summary>
public class OnnxConfig
{
    /// <summary>
    ///     Directory to store downloaded models.
    ///     Default: [app directory]/models (travels with the tool)
    /// </summary>
    public string ModelDirectory { get; set; } = GetDefaultModelDirectory();
    
    /// <summary>
    ///     Embedding model to use
    /// </summary>
    public OnnxEmbeddingModel EmbeddingModel { get; set; } = OnnxEmbeddingModel.AllMiniLmL6V2;
    
    /// <summary>
    ///     Use quantized models (smaller, faster, slightly lower quality)
    /// </summary>
    public bool UseQuantized { get; set; } = true;
    
    /// <summary>
    ///     Maximum sequence length for embedding
    /// </summary>
    public int MaxEmbeddingSequenceLength { get; set; } = 256;
    
    /// <summary>
    ///     Number of threads for ONNX inference (0 = auto)
    /// </summary>
    public int InferenceThreads { get; set; } = 0;
    
    /// <summary>
    ///     Execution provider for ONNX Runtime.
    ///     Cpu = CPU only (always works, default for stability)
    ///     Cuda = NVIDIA GPU (requires CUDA runtime)
    ///     DirectMl = Windows GPU (AMD/Intel/NVIDIA - may crash on some systems)
    ///     Auto = Try DirectML first, then CUDA, fall back to CPU
    /// </summary>
    public OnnxExecutionProvider ExecutionProvider { get; set; } = OnnxExecutionProvider.Cpu;
    
    /// <summary>
    ///     GPU device ID (0 = first GPU, 1 = second GPU, etc.)
    ///     Use this to select NVIDIA GPU when you have integrated graphics.
    ///     Run 'nvidia-smi -L' to list GPU IDs.
    /// </summary>
    public int GpuDeviceId { get; set; } = 0;
    
    /// <summary>
    ///     Batch size for embedding inference. Higher = more throughput but more memory.
    ///     Default: 32. Set to 1 for sequential processing.
    /// </summary>
    public int EmbeddingBatchSize { get; set; } = 32;
    
    /// <summary>
    ///     Enable parallel execution mode in ONNX Runtime.
    ///     True = better throughput for batched inference on multi-core CPUs.
    ///     False = sequential (lower latency for single embeddings).
    /// </summary>
    public bool UseParallelExecution { get; set; } = true;
    
    /// <summary>
    ///     Number of threads for inter-op parallelism (between graph nodes).
    ///     0 = auto (uses ProcessorCount). Only applies when UseParallelExecution = true.
    /// </summary>
    public int InterOpThreads { get; set; } = 0;
    
    private static string GetDefaultModelDirectory()
    {
        // Use app directory/models so models travel with the tool
        var appDir = AppContext.BaseDirectory;
        return Path.Combine(appDir, "models");
    }
}

/// <summary>
///     Available ONNX embedding models
/// </summary>
public enum OnnxEmbeddingModel
{
    /// <summary>
    ///     all-MiniLM-L6-v2: 384 dims, 256 seq, ~23MB quantized. Fast general-purpose.
    /// </summary>
    AllMiniLmL6V2,
    
    /// <summary>
    ///     bge-small-en-v1.5: 384 dims, 512 seq, ~34MB quantized. Best quality for size.
    /// </summary>
    BgeSmallEnV15,
    
    /// <summary>
    ///     gte-small: 384 dims, 512 seq, ~34MB quantized. Good all-around.
    /// </summary>
    GteSmall,
    
    /// <summary>
    ///     multi-qa-MiniLM-L6-cos-v1: 384 dims, 512 seq, ~23MB quantized. QA-optimized.
    /// </summary>
    MultiQaMiniLm,
    
    /// <summary>
    ///     paraphrase-MiniLM-L3-v2: 384 dims, 128 seq, ~17MB quantized. Smallest/fastest.
    /// </summary>
    ParaphraseMiniLmL3
}
