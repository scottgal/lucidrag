using Mostlylucid.RAG.Config;

namespace Mostlylucid.RAG.Config;

/// <summary>
/// Vector store backend type for semantic search
/// </summary>
public enum VectorStoreBackend
{
    /// <summary>
    /// Qdrant vector database - requires external Qdrant server.
    /// Best for: distributed deployments, existing Qdrant infrastructure.
    /// </summary>
    Qdrant,
    
    /// <summary>
    /// DuckDB embedded database - zero external dependencies, single file.
    /// Best for: simple deployments, local development, low friction.
    /// Uses DocSummarizer.Core's DuckDbVectorStore with HNSW indexing.
    /// </summary>
    DuckDB
}

/// <summary>
/// Configuration for semantic search functionality
/// </summary>
public class SemanticSearchConfig : IConfigSection
{
    public static string Section => "SemanticSearch";
    
    /// <summary>
    /// Enable or disable semantic search
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Vector store backend: DuckDB (default, zero dependencies) or Qdrant (external server)
    /// </summary>
    public VectorStoreBackend Backend { get; set; } = VectorStoreBackend.DuckDB;

    /// <summary>
    /// Path to DuckDB database file (only used when Backend = DuckDB)
    /// Defaults to "data/semantic-search.duckdb" relative to app directory
    /// </summary>
    public string DuckDbPath { get; set; } = "data/semantic-search.duckdb";

    /// <summary>
    /// Qdrant server URL (e.g., http://localhost:6333) - only used when Backend = Qdrant
    /// </summary>
    public string QdrantUrl { get; set; } = "http://localhost:6333";

    /// <summary>
    /// Optional read-only API key for Qdrant (used for search operations)
    /// </summary>
    public string? ReadApiKey { get; set; }

    /// <summary>
    /// Optional read-write API key for Qdrant (used for indexing operations)
    /// </summary>
    public string? WriteApiKey { get; set; }

    /// <summary>
    /// Collection name in Qdrant for blog posts
    /// </summary>
    public string CollectionName { get; set; } = "blog_posts";

    /// <summary>
    /// Path to the ONNX embedding model file
    /// </summary>
    public string EmbeddingModelPath { get; set; } = "models/all-MiniLM-L6-v2.onnx";

    /// <summary>
    /// Path to the tokenizer vocabulary file
    /// </summary>
    public string VocabPath { get; set; } = "models/vocab.txt";

    /// <summary>
    /// Embedding vector size (384 for all-MiniLM-L6-v2)
    /// </summary>
    public int VectorSize { get; set; } = 384;

    /// <summary>
    /// Number of related posts to return
    /// </summary>
    public int RelatedPostsCount { get; set; } = 5;

    /// <summary>
    /// Minimum similarity score (0-1) for related posts
    /// </summary>
    public float MinimumSimilarityScore { get; set; } = 0.5f;

    /// <summary>
    /// Number of search results to return
    /// </summary>
    public int SearchResultsCount { get; set; } = 10;
}
