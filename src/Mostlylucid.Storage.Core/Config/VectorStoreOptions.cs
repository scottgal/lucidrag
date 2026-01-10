using Mostlylucid.Storage.Core.Abstractions;

namespace Mostlylucid.Storage.Core.Config;

/// <summary>
/// Configuration options for vector storage.
/// </summary>
public class VectorStoreOptions
{
    public const string SectionName = "VectorStore";

    /// <summary>
    /// Vector store backend to use.
    /// - InMemory: Fast, no persistence (best for tool/MCP mode, one-shot analysis)
    /// - DuckDB: Embedded, persistent (best for standalone mode, development)
    /// - Qdrant: Server-based, persistent (best for production)
    /// </summary>
    public VectorStoreBackend Backend { get; set; } = VectorStoreBackend.DuckDB;

    /// <summary>
    /// Default collection name.
    /// </summary>
    public string CollectionName { get; set; } = "default";

    /// <summary>
    /// Whether to persist vectors (only relevant for backends that support it).
    /// </summary>
    public bool PersistVectors { get; set; } = true;

    /// <summary>
    /// Whether to reuse existing embeddings when content hash matches.
    /// </summary>
    public bool ReuseExistingEmbeddings { get; set; } = true;

    /// <summary>
    /// Whether to re-index all documents on startup (only for ephemeral backends).
    /// </summary>
    public bool ReindexOnStartup { get; set; } = false;

    /// <summary>
    /// DuckDB-specific configuration.
    /// </summary>
    public DuckDBOptions DuckDB { get; set; } = new();

    /// <summary>
    /// Qdrant-specific configuration.
    /// </summary>
    public QdrantOptions Qdrant { get; set; } = new();

    /// <summary>
    /// InMemory-specific configuration.
    /// </summary>
    public InMemoryOptions InMemory { get; set; } = new();

    /// <summary>
    /// Get default options for tool/MCP mode (no persistence, fastest).
    /// </summary>
    public static VectorStoreOptions ForToolMode()
    {
        return new VectorStoreOptions
        {
            Backend = VectorStoreBackend.InMemory,
            PersistVectors = false,
            ReuseExistingEmbeddings = false,
            ReindexOnStartup = false,
            CollectionName = "tool_temp"
        };
    }

    /// <summary>
    /// Get default options for standalone mode (persistent, embedded).
    /// </summary>
    public static VectorStoreOptions ForStandaloneMode(string dataDirectory = "./data")
    {
        return new VectorStoreOptions
        {
            Backend = VectorStoreBackend.DuckDB,
            PersistVectors = true,
            ReuseExistingEmbeddings = true,
            ReindexOnStartup = false,
            CollectionName = "documents",
            DuckDB = new DuckDBOptions
            {
                DatabasePath = Path.Combine(dataDirectory, "vectors.duckdb"),
                EnableVSS = true,
                EnablePersistence = true
            }
        };
    }

    /// <summary>
    /// Get default options for production mode (Qdrant).
    /// </summary>
    public static VectorStoreOptions ForProductionMode(string qdrantHost = "localhost", int qdrantPort = 6334)
    {
        return new VectorStoreOptions
        {
            Backend = VectorStoreBackend.Qdrant,
            PersistVectors = true,
            ReuseExistingEmbeddings = true,
            CollectionName = "documents",
            Qdrant = new QdrantOptions
            {
                Host = qdrantHost,
                Port = qdrantPort
            }
        };
    }
}

/// <summary>
/// DuckDB-specific configuration.
/// </summary>
public class DuckDBOptions
{
    /// <summary>
    /// Path to DuckDB database file.
    /// </summary>
    public string DatabasePath { get; set; } = "./data/vectors.duckdb";

    /// <summary>
    /// Whether to try to use the VSS extension for HNSW indexes.
    /// Falls back to in-memory cosine similarity if unavailable.
    /// </summary>
    public bool EnableVSS { get; set; } = true;

    /// <summary>
    /// Whether to enable experimental HNSW persistence.
    /// Allows indexes to survive restarts (with known WAL recovery limitations).
    /// </summary>
    public bool EnablePersistence { get; set; } = true;

    /// <summary>
    /// Vector dimension (must match embedding model).
    /// </summary>
    public int VectorDimension { get; set; } = 384;

    /// <summary>
    /// HNSW index parameters (if VSS available).
    /// </summary>
    public HNSWIndexOptions HNSW { get; set; } = new();
}

/// <summary>
/// HNSW index configuration.
/// </summary>
public class HNSWIndexOptions
{
    /// <summary>
    /// Number of bi-directional links per node (default: 16).
    /// Higher = better recall, more memory.
    /// </summary>
    public int M { get; set; } = 16;

    /// <summary>
    /// Size of dynamic candidate list during construction (default: 200).
    /// Higher = better quality, slower indexing.
    /// </summary>
    public int EfConstruction { get; set; } = 200;

    /// <summary>
    /// Size of candidate list during search (default: 100).
    /// Higher = better recall, slower search.
    /// </summary>
    public int EfSearch { get; set; } = 100;
}

/// <summary>
/// Qdrant-specific configuration.
/// </summary>
public class QdrantOptions
{
    /// <summary>
    /// Qdrant server hostname.
    /// </summary>
    public string Host { get; set; } = "localhost";

    /// <summary>
    /// Qdrant gRPC port (default: 6334).
    /// </summary>
    public int Port { get; set; } = 6334;

    /// <summary>
    /// Optional API key for authentication.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Vector dimension (must match embedding model).
    /// </summary>
    public int VectorSize { get; set; } = 384;

    /// <summary>
    /// Whether to use HTTPS for gRPC connection.
    /// </summary>
    public bool UseHttps { get; set; } = false;

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// InMemory-specific configuration.
/// </summary>
public class InMemoryOptions
{
    /// <summary>
    /// Maximum number of documents to keep in memory (0 = unlimited).
    /// Used for LRU eviction when limit is reached.
    /// </summary>
    public int MaxDocuments { get; set; } = 0;

    /// <summary>
    /// Whether to enable verbose logging for debugging.
    /// </summary>
    public bool Verbose { get; set; } = false;
}
