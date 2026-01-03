using Mostlylucid.DocSummarizer.Models;
using System.IO;
using Mostlylucid.DocSummarizer.Services;

namespace Mostlylucid.DocSummarizer.Config;

/// <summary>
///     Configuration for the document summarizer
/// </summary>
public class DocSummarizerConfig
{
    /// <summary>
    ///     Embedding backend (Onnx = fast/zero-config, Ollama = uses Ollama server)
    /// </summary>
    public EmbeddingBackend EmbeddingBackend { get; set; } = EmbeddingBackend.Onnx;

    /// <summary>
    ///     LLM backend for text generation (Ollama = default, None = deterministic only)
    /// </summary>
    public LlmBackend LlmBackend { get; set; } = LlmBackend.Ollama;

    /// <summary>
    ///     ONNX configuration (used when Backend = Onnx)
    /// </summary>
    public OnnxConfig Onnx { get; set; } = new();

    /// <summary>
    ///     Ollama configuration (used when Backend = Ollama)
    /// </summary>
    public OllamaConfig Ollama { get; set; } = new();

    /// <summary>
    ///     BERT extractive summarization configuration (used when Mode = Bert)
    /// </summary>
    public BertConfig Bert { get; set; } = new();

    /// <summary>
    ///     Docling configuration
    /// </summary>
    public DoclingConfig Docling { get; set; } = new();

    /// <summary>
    ///     Qdrant configuration
    /// </summary>
    public QdrantConfig Qdrant { get; set; } = new();

    /// <summary>
    ///     Processing configuration
    /// </summary>
    public ProcessingConfig Processing { get; set; } = new();

    /// <summary>
    ///     Output configuration
    /// </summary>
    public OutputConfig Output { get; set; } = new();

    /// <summary>
    ///     Web fetch configuration
    /// </summary>
    public WebFetchConfig WebFetch { get; set; } = new();

    /// <summary>
    ///     Batch processing configuration
    /// </summary>
    public BatchConfig Batch { get; set; } = new();

    /// <summary>
    ///     Embedding resilience configuration
    /// </summary>
    public EmbeddingConfig Embedding { get; set; } = new();

    /// <summary>
    ///     BertRag pipeline configuration (vector storage, persistence)
    /// </summary>
    public BertRagConfig BertRag { get; set; } = new();
    
    /// <summary>
    ///     Extraction phase configuration (segment parsing and salience scoring)
    /// </summary>
    public ExtractionConfigSection Extraction { get; set; } = new();
    
    /// <summary>
    ///     Retrieval phase configuration (segment selection for synthesis)
    /// </summary>
    public RetrievalConfigSection Retrieval { get; set; } = new();
    
    /// <summary>
    ///     Adaptive retrieval configuration (auto-scales based on document size/type)
    /// </summary>
    public AdaptiveRetrievalConfig AdaptiveRetrieval { get; set; } = new();
}

/// <summary>
///     Embedding service resilience configuration
/// </summary>
public class EmbeddingConfig
{
    /// <summary>
    ///     Maximum requests per second to Ollama embedding endpoint.
    ///     Ollama processes embeddings sequentially, so this prevents overwhelming it.
    ///     Default is 2 requests/second.
    /// </summary>
    public double RequestsPerSecond { get; set; } = 2.0;

    /// <summary>
    ///     Maximum retry attempts for failed embedding requests.
    ///     Default is 5 retries.
    /// </summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    ///     Initial delay before first retry in milliseconds.
    ///     Uses exponential backoff: delay * 2^attempt.
    ///     Default is 1000ms (1 second).
    /// </summary>
    public int InitialRetryDelayMs { get; set; } = 1000;

    /// <summary>
    ///     Maximum delay between retries in milliseconds.
    ///     Default is 30000ms (30 seconds).
    /// </summary>
    public int MaxRetryDelayMs { get; set; } = 30000;

    /// <summary>
    ///     Delay between embedding requests in milliseconds.
    ///     Added on top of rate limiting for extra stability.
    ///     Default is 100ms.
    /// </summary>
    public int DelayBetweenRequestsMs { get; set; } = 100;

    /// <summary>
    ///     Enable circuit breaker to fail fast after repeated failures.
    ///     Default is true.
    /// </summary>
    public bool EnableCircuitBreaker { get; set; } = true;

    /// <summary>
    ///     Number of consecutive failures before opening circuit.
    ///     Default is 3.
    /// </summary>
    public int CircuitBreakerThreshold { get; set; } = 3;

    /// <summary>
    ///     How long to keep circuit open before trying again in seconds.
    ///     Default is 30 seconds.
    /// </summary>
    public int CircuitBreakerDurationSeconds { get; set; } = 30;
}

/// <summary>
///     Ollama service configuration
/// </summary>
public class OllamaConfig
{
    /// <summary>
    ///     Base URL for Ollama service
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>
    ///     Model to use for generation (main summarization work)
    /// </summary>
    public string Model { get; set; } = "llama3.2:3b";

    /// <summary>
    ///     Model to use for embeddings
    /// </summary>
    public string EmbedModel { get; set; } = "nomic-embed-text";

    /// <summary>
    ///     Small/fast model for document classification (sentinel).
    ///     Uses first chunk to classify fiction vs technical.
    ///     Set to empty string to use main model, or use a tiny model like "tinyllama" or "qwen2.5:1.5b".
    /// </summary>
    public string ClassifierModel { get; set; } = "tinyllama";

    /// <summary>
    ///     Temperature for generation
    /// </summary>
    public double Temperature { get; set; } = 0.3;

    /// <summary>
    ///     Request timeout in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 1200;
}

/// <summary>
///     Docling service configuration
/// </summary>
public class DoclingConfig
{
    /// <summary>
    ///     Docling service base URL
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:5001";

    /// <summary>
    ///     Request timeout in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 1200;

    /// <summary>
    ///     Enable split processing for large PDFs.
    ///     For CUDA-enabled Docling, set to false to let GPU process entire document.
    ///     For CPU-only Docling, keep true to avoid memory issues.
    /// </summary>
    public bool EnableSplitProcessing { get; set; } = true;

    /// <summary>
    ///     Pages per chunk for split processing.
    ///     Larger chunks = fewer API calls = faster for GPU.
    ///     Smaller chunks = lower memory usage = better for CPU.
    ///     Default: 50 pages (good for GPU with 8GB+ VRAM).
    ///     For CPU or low VRAM, try 20-30.
    /// </summary>
    public int PagesPerChunk { get; set; } = 50;

    /// <summary>
    ///     Maximum concurrent chunks to process.
    ///     For GPU: use 1-2 (GPU handles parallelism internally).
    ///     For CPU: use 2-4 depending on cores.
    ///     Higher values can cause GPU OOM or CPU thrashing.
    /// </summary>
    public int MaxConcurrentChunks { get; set; } = 2;

    /// <summary>
    ///     PDF backend to use: "pypdfium2" (fast) or "docling" (more accurate for complex layouts).
    /// </summary>
    public string PdfBackend { get; set; } = "pypdfium2";
    
    /// <summary>
    ///     Fallback PDF backend for OCR when text layer is garbled.
    ///     Set to "docling" to force OCR-based extraction.
    /// </summary>
    public string OcrPdfBackend { get; set; } = "docling";
    
    /// <summary>
    ///     Enable OCR fallback when extracted text looks corrupt/garbled.
    /// </summary>
    public bool EnableOcrFallback { get; set; } = true;
    
    /// <summary>
    ///     Minimum pages before enabling split processing.
    ///     Documents smaller than this are processed as a single chunk.
    ///     Default: 60 pages.
    /// </summary>
    public int MinPagesForSplit { get; set; } = 60;
    
    /// <summary>
    ///     Auto-detect GPU and adapt settings accordingly.
    ///     When true, queries Docling for CUDA/GPU support and optimizes chunk sizes.
    ///     Default: true.
    /// </summary>
    public bool AutoDetectGpu { get; set; } = true;
}

/// <summary>
///     Detected Docling capabilities
/// </summary>
public class DoclingCapabilities
{
    /// <summary>
    ///     Whether Docling service is available
    /// </summary>
    public bool Available { get; set; }
    
    /// <summary>
    ///     Whether GPU/CUDA acceleration is available (null = unknown)
    /// </summary>
    public bool? HasGpu { get; set; }
    
    /// <summary>
    ///     Detected accelerator type (e.g., "cuda", "cpu", "mps")
    /// </summary>
    public string? Accelerator { get; set; }
}

/// <summary>
///     Qdrant service configuration
/// </summary>
public class QdrantConfig
{
    /// <summary>
    ///     Qdrant host
    /// </summary>
    public string Host { get; set; } = "localhost";

    /// <summary>
    ///     Qdrant gRPC port (6334 is gRPC, 6333 is REST - we use gRPC client)
    /// </summary>
    public int Port { get; set; } = 6334;

    /// <summary>
    ///     Qdrant API key (optional)
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    ///     Collection name for documents
    /// </summary>
    public string CollectionName { get; set; } = "documents";

    /// <summary>
    ///     Vector size for embeddings (384 for ONNX all-MiniLM, 768 for Ollama nomic-embed-text)
    /// </summary>
    public int VectorSize { get; set; } = 384;

    /// <summary>
    ///     Delete collection after summarization
    /// </summary>
    public bool DeleteCollectionAfterSummarization { get; set; } = true;
}

/// <summary>
///     Processing configuration
/// </summary>
public class ProcessingConfig
{
    /// <summary>
    ///     Maximum heading level for chunking
    /// </summary>
    public int MaxHeadingLevel { get; set; } = 2;

    /// <summary>
    ///     Maximum chunk size in tokens
    /// </summary>
    public int MaxChunkSize { get; set; } = 2000;

    /// <summary>
    ///     Chunk overlap in tokens
    /// </summary>
    public int ChunkOverlap { get; set; } = 100;

    /// <summary>
    ///     Maximum concurrent chunks
    /// </summary>
    public int MaxConcurrentChunks { get; set; } = 4;

    /// <summary>
    ///     Enable split processing
    /// </summary>
    public bool EnableSplitProcessing { get; set; } = true;

    /// <summary>
    ///     Maximum LLM parallelism
    /// </summary>
    public int MaxLlmParallelism { get; set; } = 2;

    /// <summary>
    ///     Target chunk tokens
    /// </summary>
    public int TargetChunkTokens { get; set; } = 1500;

    /// <summary>
    ///     Minimum chunk tokens
    /// </summary>
    public int MinChunkTokens { get; set; } = 200;

    /// <summary>
    ///     Memory management settings
    /// </summary>
    public MemoryConfig Memory { get; set; } = new();
 
    /// <summary>
    ///     Chunk cache settings for reusing Docling output
    /// </summary>
    public ChunkCacheConfig ChunkCache { get; set; } = new();
 
    /// <summary>
    ///     Summary length adaptation settings
    /// </summary>
    public SummaryLengthConfig SummaryLength { get; set; } = new();
 }


/// <summary>
///     Memory management configuration for large document processing
/// </summary>
public class MemoryConfig
{
    /// <summary>
    ///     Enable disk-backed chunk storage for large documents.
    ///     When enabled, chunk content is stored on disk instead of memory
    ///     when the document exceeds DiskStorageThreshold chunks.
    /// </summary>
    public bool EnableDiskStorage { get; set; } = true;

    /// <summary>
    ///     Number of chunks before switching to disk storage.
    ///     Default is 100 chunks (~400KB-1.6MB of content).
    /// </summary>
    public int DiskStorageThreshold { get; set; } = 100;

    /// <summary>
    ///     Use streaming chunker for markdown files larger than this size (in bytes).
    ///     Streaming processes line-by-line without loading entire file.
    ///     Default is 5MB.
    /// </summary>
    public long StreamingThresholdBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    ///     Batch size for embedding operations. Smaller batches use less memory
    ///     but may be slower. Default is 10.
    /// </summary>
    public int EmbeddingBatchSize { get; set; } = 10;

    /// <summary>
    ///     Trigger GC after processing this many chunks.
    ///     Set to 0 to disable periodic GC.
    /// </summary>
    public int GcIntervalChunks { get; set; } = 50;

    /// <summary>
    ///     Maximum memory usage in MB before forcing GC.
    ///     Set to 0 to disable memory-based GC triggers.
    /// </summary>
    public int MaxMemoryMB { get; set; } = 0;
}

/// <summary>
///     Persistent chunk cache configuration
/// </summary>
public class ChunkCacheConfig
{
    /// <summary>
    ///     Enable disk chunk cache to avoid re-converting unchanged documents.
    /// </summary>
    public bool EnableChunkCache { get; set; } = true;

    /// <summary>
    ///     Directory to store cached chunks. Defaults to user profile .docsummarizer/chunks.
    /// </summary>
    public string? CacheDirectory { get; set; }
        = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docsummarizer", "chunks");

    /// <summary>
    ///     Retention period in days before cached chunks are deleted.
    /// </summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>
    ///     Version token to invalidate old cache layouts.
    /// </summary>
    public string VersionToken { get; set; } = "v2";

    /// <summary>
    ///     Enable lazy content loading - keeps only metadata in memory and loads content on-demand.
    ///     Significantly reduces memory usage for large documents.
    ///     Default is true.
    /// </summary>
    public bool LazyLoadContent { get; set; } = true;

    /// <summary>
    ///     Chunk count threshold below which content is kept in memory even with LazyLoadContent enabled.
    ///     Small documents don't benefit from lazy loading and the overhead isn't worth it.
    ///     Default is 20 chunks.
    /// </summary>
    public int LazyLoadThreshold { get; set; } = 20;
}

/// <summary>
///     Summary length adaptation configuration
/// </summary>
public class SummaryLengthConfig
{
    /// <summary>
    ///     Minimum word count before generating a summary. Documents shorter than this are returned as-is.
    /// </summary>
    public int MinWordsForSummary { get; set; } = 150;

    /// <summary>
    ///     Tier for very short documents.
    /// </summary>
    public SummaryLengthTier Tiny { get; set; } = new()
    {
        Name = "tiny",
        MaxWords = 600,
        Topics = 2,
        ChunksPerTopic = 2,
        BulletCount = 2,
        WordsPerBullet = 18,
        TopClaims = 5,
        MaxCharacters = 4,
        MaxLocations = 3,
        MaxOther = 2
    };

    /// <summary>
    ///     Tier for short documents.
    /// </summary>
    public SummaryLengthTier Small { get; set; } = new()
    {
        Name = "small",
        MaxWords = 2000,
        Topics = 3,
        ChunksPerTopic = 3,
        BulletCount = 3,
        WordsPerBullet = 20,
        TopClaims = 8,
        MaxCharacters = 5,
        MaxLocations = 4,
        MaxOther = 3
    };

    /// <summary>
    ///     Tier for medium-length documents.
    /// </summary>
    public SummaryLengthTier Medium { get; set; } = new()
    {
        Name = "medium",
        MaxWords = 6000,
        Topics = 4,
        ChunksPerTopic = 3,
        BulletCount = 3,
        WordsPerBullet = 20,
        TopClaims = 10,
        MaxCharacters = 6,
        MaxLocations = 4,
        MaxOther = 3
    };

    /// <summary>
    ///     Tier for long documents.
    /// </summary>
    public SummaryLengthTier Large { get; set; } = new()
    {
        Name = "large",
        MaxWords = 20000,
        Topics = 5,
        ChunksPerTopic = 4,
        BulletCount = 4,
        WordsPerBullet = 20,
        TopClaims = 12,
        MaxCharacters = 8,
        MaxLocations = 5,
        MaxOther = 4
    };

    /// <summary>
    ///     Tier for very large documents.
    /// </summary>
    public SummaryLengthTier VeryLarge { get; set; } = new()
    {
        Name = "very large",
        MaxWords = int.MaxValue,
        Topics = 6,
        ChunksPerTopic = 5,
        BulletCount = 5,
        WordsPerBullet = 18,
        TopClaims = 15,
        MaxCharacters = 10,
        MaxLocations = 6,
        MaxOther = 5
    };

    /// <summary>
    ///     Get tiers ordered by max word count.
    /// </summary>
    public IEnumerable<SummaryLengthTier> GetOrderedTiers()
    {
        return new[] { Tiny, Small, Medium, Large, VeryLarge }
            .OrderBy(t => t.MaxWords)
            .ThenBy(t => t.Name);
    }
}

/// <summary>
///     Configuration for a specific summary length tier
/// </summary>
public class SummaryLengthTier
{
    /// <summary>
    ///     Human-readable name for logging
    /// </summary>
    public string Name { get; set; } = "tier";

    /// <summary>
    ///     Maximum word count included in this tier
    /// </summary>
    public int MaxWords { get; set; } = 1000;

    /// <summary>
    ///     Number of topics to extract
    /// </summary>
    public int Topics { get; set; } = 3;

    /// <summary>
    ///     Number of chunks to retrieve per topic
    /// </summary>
    public int ChunksPerTopic { get; set; } = 3;

    /// <summary>
    ///     Number of bullets in the executive summary
    /// </summary>
    public int BulletCount { get; set; } = 3;

    /// <summary>
    ///     Maximum words per bullet in the executive summary
    /// </summary>
    public int WordsPerBullet { get; set; } = 20;

    /// <summary>
    ///     Number of claims to prioritize during synthesis
    /// </summary>
    public int TopClaims { get; set; } = 8;

    /// <summary>
    ///     Maximum number of characters to surface in grounding
    /// </summary>
    public int MaxCharacters { get; set; } = 6;

    /// <summary>
    ///     Maximum number of locations to surface in grounding
    /// </summary>
    public int MaxLocations { get; set; } = 4;

    /// <summary>
    ///     Maximum number of events/organizations/dates to surface
    /// </summary>
    public int MaxOther { get; set; } = 3;
}

/// <summary>
///     Output configuration
/// </summary>
public class OutputConfig
{
    /// <summary>
    ///     Output format
    /// </summary>
    public OutputFormat Format { get; set; } = OutputFormat.Console;

    /// <summary>
    ///     Output directory for file outputs
    /// </summary>
    public string? OutputDirectory { get; set; }

    /// <summary>
    ///     Show detailed progress information
    /// </summary>
    public bool Verbose { get; set; } = false;

    /// <summary>
    ///     Include processing trace in output
    /// </summary>
    public bool IncludeTrace { get; set; } = false;

    /// <summary>
    ///     Include topics in output
    /// </summary>
    public bool IncludeTopics { get; set; } = true;

    /// <summary>
    ///     Include open questions in output
    /// </summary>
    public bool IncludeOpenQuestions { get; set; } = false;

    /// <summary>
    ///     Include document structure/chunk index in output
    /// </summary>
    public bool IncludeChunkIndex { get; set; } = false;
}

/// <summary>
///     Web fetch mode - determines how web pages are fetched
/// </summary>
public enum WebFetchMode
{
    /// <summary>
    ///     Simple HTTP client fetch - fast but cannot execute JavaScript
    /// </summary>
    Simple,
    
    /// <summary>
    ///     Playwright headless browser - slower but handles JavaScript-rendered pages (SPAs, React apps)
    /// </summary>
    Playwright
}

/// <summary>
///     Web fetch configuration
/// </summary>
public class WebFetchConfig
{
    /// <summary>
    ///     Enable web fetching functionality
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    ///     Fetch mode: Simple (HTTP client) or Playwright (headless browser for JS pages).
    ///     When set to Playwright, Chromium browser will be auto-installed on first use.
    /// </summary>
    public WebFetchMode Mode { get; set; } = WebFetchMode.Simple;

    /// <summary>
    ///     Browser executable path (optional, for Playwright mode)
    /// </summary>
    public string? BrowserExecutablePath { get; set; }

    /// <summary>
    ///     Default timeout for web requests in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    ///     User agent to use for web requests
    /// </summary>
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 DocSummarizer/1.0";
}

/// <summary>
///     Batch processing configuration
/// </summary>
public class BatchConfig
{
    /// <summary>
    ///     File extensions to process
    /// </summary>
    public List<string> FileExtensions { get; set; } = new() 
    { 
        ".txt", ".md", ".pdf", ".docx", ".xlsx", ".pptx", 
        ".html", ".csv", ".png", ".jpg", ".tiff", ".vtt", ".adoc" 
    };

    /// <summary>
    ///     Process directories recursively
    /// </summary>
    public bool Recursive { get; set; } = false;

    /// <summary>
    ///     Maximum concurrent files to process
    /// </summary>
    public int MaxConcurrentFiles { get; set; } = 4;

    /// <summary>
    ///     Continue on error
    /// </summary>
    public bool ContinueOnError { get; set; } = true;

    /// <summary>
    ///     Include patterns for files
    /// </summary>
    public List<string> IncludePatterns { get; set; } = new();

    /// <summary>
    ///     Exclude patterns for files
    /// </summary>
    public List<string> ExcludePatterns { get; set; } = new();
}

/// <summary>
///     BertRag pipeline configuration
/// </summary>
public class BertRagConfig
{
    /// <summary>
    ///     Vector store backend for segment storage.
    ///     DuckDB (default) = embedded persistent storage in a single file, no external services.
    ///     InMemory = no external dependencies, vectors lost on exit.
    ///     Qdrant = persistent storage, requires Qdrant server.
    /// </summary>
    public VectorStoreBackend VectorStore { get; set; } = VectorStoreBackend.DuckDB;

    /// <summary>
    ///     Collection name for vector storage.
    ///     Used by both InMemory and Qdrant backends.
    /// </summary>
    public string CollectionName { get; set; } = "docsummarizer";

    /// <summary>
    ///     Whether to persist vectors between runs (only applies to Qdrant backend).
    ///     When true, documents are only re-embedded if content has changed.
    ///     When false, collection is deleted after summarization.
    /// </summary>
    public bool PersistVectors { get; set; } = true;

    /// <summary>
    ///     Whether to reuse existing embeddings if document is already indexed.
    ///     Only applies when PersistVectors = true and VectorStore = Qdrant.
    /// </summary>
    public bool ReuseExistingEmbeddings { get; set; } = true;
    
    /// <summary>
    ///     Whether to clear and rebuild the vector index on application startup.
    ///     When true, all existing embeddings are deleted and documents are re-indexed.
    ///     Useful during development when embedding models or extraction logic changes.
    ///     Default is true (safe for development). Set to false in production for faster startup.
    /// </summary>
    /// <remarks>
    ///     In appsettings.Development.json, this defaults to true so changes to
    ///     embedding models or segment extraction are immediately reflected.
    ///     In appsettings.json (production), set to false to preserve indexed data.
    /// </remarks>
    public bool ReindexOnStartup { get; set; } = true;
}

/// <summary>
///     Extraction phase configuration (segment parsing and salience scoring)
/// </summary>
public class ExtractionConfigSection
{
    /// <summary>
    ///     Fraction of segments to keep in salience ranking (0.15 = top 15%)
    /// </summary>
    public double ExtractionRatio { get; set; } = 0.15;
    
    /// <summary>
    ///     Minimum segments to extract regardless of ratio
    /// </summary>
    public int MinSegments { get; set; } = 10;
    
    /// <summary>
    ///     Maximum segments to extract regardless of ratio
    /// </summary>
    public int MaxSegments { get; set; } = 100;
    
    /// <summary>
    ///     Maximum segments to embed (pre-filter if document has more)
    /// </summary>
    public int MaxSegmentsToEmbed { get; set; } = 200;
    
    /// <summary>
    ///     MMR lambda: 0=diversity, 1=relevance (0.7 = slight relevance bias)
    /// </summary>
    public double MmrLambda { get; set; } = 0.7;
    
    // === Length-based quality scoring ===
    
    /// <summary>
    ///     Minimum character length for a segment to receive full quality score.
    ///     Segments shorter than this are penalized proportionally.
    ///     Default: 80 characters (~15-20 words, a substantive sentence)
    /// </summary>
    public int IdealMinLength { get; set; } = 80;
    
    /// <summary>
    ///     Maximum character length for quality scoring. Segments beyond this
    ///     receive no additional benefit. Default: 500 characters (~80-100 words)
    /// </summary>
    public int IdealMaxLength { get; set; } = 500;
    
    /// <summary>
    ///     Minimum quality score for very short segments. Prevents short
    ///     headings from being excluded but de-prioritizes them.
    ///     Range 0-1. Default: 0.3
    /// </summary>
    public double MinLengthQualityScore { get; set; } = 0.3;
    
    /// <summary>
    ///     Boost for headings. Default: 1.15 (reduced from 1.5 to prevent
    ///     short headings from dominating top segments)
    /// </summary>
    public double HeadingBoost { get; set; } = 1.15;
    
    /// <summary>
    ///     Boost for the document title (first H1). Important but balanced.
    ///     Default: 1.8 (reduced from 4.5x to allow substantive content to rank)
    /// </summary>
    public double DocumentTitleBoost { get; set; } = 1.8;
    
    /// <summary>
    ///     Convert to the internal ExtractionConfig model
    /// </summary>
    public ExtractionConfig ToExtractionConfig() => new()
    {
        ExtractionRatio = ExtractionRatio,
        MinSegments = MinSegments,
        MaxSegments = MaxSegments,
        MaxSegmentsToEmbed = MaxSegmentsToEmbed,
        MmrLambda = MmrLambda,
        IdealMinLength = IdealMinLength,
        IdealMaxLength = IdealMaxLength,
        MinLengthQualityScore = MinLengthQualityScore,
        HeadingBoost = HeadingBoost,
        DocumentTitleBoost = DocumentTitleBoost
    };
}

/// <summary>
///     Retrieval phase configuration (segment selection for synthesis)
/// </summary>
public class RetrievalConfigSection
{
    /// <summary>
    ///     Base number of segments to retrieve for synthesis.
    ///     May be scaled by adaptive retrieval based on document size/type.
    /// </summary>
    public int TopK { get; set; } = 25;
    
    /// <summary>
    ///     Always include top-N salient segments regardless of query match
    /// </summary>
    public int FallbackCount { get; set; } = 5;
    
    /// <summary>
    ///     Use RRF (Reciprocal Rank Fusion) for combining scores - recommended
    /// </summary>
    public bool UseRRF { get; set; } = true;
    
    /// <summary>
    ///     RRF k parameter (standard is 60)
    /// </summary>
    public int RrfK { get; set; } = 60;
    
    /// <summary>
    ///     Use hybrid search (BM25 + dense + salience) - recommended
    /// </summary>
    public bool UseHybridSearch { get; set; } = true;
    
    /// <summary>
    ///     Query-salience blend alpha (only used when UseRRF = false)
    /// </summary>
    public double Alpha { get; set; } = 0.6;
    
    /// <summary>
    ///     Minimum similarity threshold (only for non-RRF mode)
    /// </summary>
    public double MinSimilarity { get; set; } = 0.3;
    
    /// <summary>
    ///     Convert to the internal RetrievalConfig model
    /// </summary>
    public RetrievalConfig ToRetrievalConfig() => new()
    {
        TopK = TopK,
        FallbackCount = FallbackCount,
        UseRRF = UseRRF,
        RrfK = RrfK,
        UseHybridSearch = UseHybridSearch,
        Alpha = Alpha,
        MinSimilarity = MinSimilarity
    };
}

/// <summary>
///     Adaptive retrieval configuration - auto-scales TopK based on document characteristics
/// </summary>
public class AdaptiveRetrievalConfig
{
    /// <summary>
    ///     Enable adaptive scaling of TopK based on document size and content type.
    ///     When enabled, longer documents and narrative content get more segments.
    /// </summary>
    public bool Enabled { get; set; } = true;
    
    /// <summary>
    ///     Minimum coverage percentage to aim for (5.0 = retrieve ~5% of segments).
    ///     Higher values improve summary quality but increase synthesis time.
    /// </summary>
    public double MinCoveragePercent { get; set; } = 5.0;
    
    /// <summary>
    ///     Minimum TopK regardless of document size
    /// </summary>
    public int MinTopK { get; set; } = 15;
    
    /// <summary>
    ///     Maximum TopK regardless of document size (limited by LLM context)
    /// </summary>
    public int MaxTopK { get; set; } = 100;
    
    /// <summary>
    ///     Boost factor for narrative content (fiction, stories).
    ///     Fiction needs more context to avoid hallucinations.
    ///     1.5 = retrieve 50% more segments for narrative content.
    /// </summary>
    public double NarrativeBoost { get; set; } = 1.5;
    
    /// <summary>
    ///     Apply adaptive settings to a RetrievalConfig
    /// </summary>
    public void ApplyTo(RetrievalConfig config)
    {
        config.AdaptiveTopK = Enabled;
        config.MinCoveragePercent = MinCoveragePercent;
        config.MinTopK = MinTopK;
        config.MaxTopK = MaxTopK;
        config.NarrativeBoost = NarrativeBoost;
    }
}
