using Mostlylucid.RAG.Config;

namespace Mostlylucid.RAG.Config;

/// <summary>
/// Vector store backend type for semantic search
/// </summary>
public enum VectorStoreBackend
{
    /// <summary>
    /// DuckDB embedded database - persistent storage, file-based, no external server required.
    /// Best for: Development, standalone deployments, local storage with persistence.
    /// Default option for embedded scenarios.
    /// </summary>
    DuckDB,

    /// <summary>
    /// Qdrant vector database - requires external Qdrant server.
    /// Best for: production deployments, multi-vector embeddings, hybrid search.
    /// Supports advanced features: named vectors, filtering, batch operations.
    /// </summary>
    Qdrant
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
    /// Vector store backend: DuckDB (default, embedded) or Qdrant (requires external server)
    /// </summary>
    public VectorStoreBackend Backend { get; set; } = VectorStoreBackend.DuckDB;

    /// <summary>
    /// Qdrant server URL (e.g., http://localhost:6333)
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
