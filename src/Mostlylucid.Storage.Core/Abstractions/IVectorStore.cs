using Mostlylucid.Storage.Core.Abstractions.Models;

namespace Mostlylucid.Storage.Core.Abstractions;

/// <summary>
/// Unified vector store interface for embedding storage and retrieval.
/// Supports multiple backends: InMemory (ephemeral), DuckDB (persistent, embedded), Qdrant (persistent, server).
/// </summary>
public interface IVectorStore : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Whether this vector store persists data between restarts.
    /// - InMemory: false (data lost on restart)
    /// - DuckDB: true (file-based persistence)
    /// - Qdrant: true (server-based persistence)
    /// </summary>
    bool IsPersistent { get; }

    /// <summary>
    /// Backend type for this vector store.
    /// </summary>
    VectorStoreBackend Backend { get; }

    // ========== Collection Management ==========

    /// <summary>
    /// Initialize a collection with specified schema.
    /// Creates collection if it doesn't exist, validates schema if it does.
    /// </summary>
    Task InitializeAsync(string collectionName, VectorStoreSchema schema, CancellationToken ct = default);

    /// <summary>
    /// Check if a collection exists.
    /// </summary>
    Task<bool> CollectionExistsAsync(string collectionName, CancellationToken ct = default);

    /// <summary>
    /// Delete an entire collection and all its documents.
    /// </summary>
    Task DeleteCollectionAsync(string collectionName, CancellationToken ct = default);

    /// <summary>
    /// Get collection statistics (document count, size, etc.).
    /// </summary>
    Task<CollectionStats> GetCollectionStatsAsync(string collectionName, CancellationToken ct = default);

    // ========== Document Operations ==========

    /// <summary>
    /// Check if a document exists in a collection.
    /// </summary>
    Task<bool> HasDocumentAsync(string collectionName, string documentId, CancellationToken ct = default);

    /// <summary>
    /// Insert or update documents (upsert).
    /// If a document with the same ID exists, it is replaced.
    /// </summary>
    Task UpsertDocumentsAsync(string collectionName, IEnumerable<VectorDocument> documents, CancellationToken ct = default);

    /// <summary>
    /// Delete a specific document by ID.
    /// </summary>
    Task DeleteDocumentAsync(string collectionName, string documentId, CancellationToken ct = default);

    /// <summary>
    /// Get a single document by ID.
    /// Returns null if not found.
    /// </summary>
    Task<VectorDocument?> GetDocumentAsync(string collectionName, string documentId, CancellationToken ct = default);

    /// <summary>
    /// Get all documents from a collection, optionally filtered by parent ID.
    /// Useful for retrieving all segments of a parent document.
    /// </summary>
    Task<List<VectorDocument>> GetAllDocumentsAsync(string collectionName, string? parentId = null, CancellationToken ct = default);

    // ========== Search Operations ==========

    /// <summary>
    /// Perform vector similarity search.
    /// Returns top-K most similar documents ranked by distance/similarity.
    /// </summary>
    Task<List<VectorSearchResult>> SearchAsync(string collectionName, VectorSearchQuery query, CancellationToken ct = default);

    /// <summary>
    /// Find similar documents to a given document ID (using its embedding).
    /// Useful for "find related" functionality.
    /// </summary>
    Task<List<VectorSearchResult>> FindSimilarAsync(string collectionName, string documentId, int topK = 10, CancellationToken ct = default);

    // ========== Content Hash-Based Caching ==========

    /// <summary>
    /// Get documents by their content hashes.
    /// Used for deduplication and cache reuse.
    /// Returns dictionary of hash -> document.
    /// </summary>
    Task<Dictionary<string, VectorDocument>> GetDocumentsByHashAsync(string collectionName, IEnumerable<string> contentHashes, CancellationToken ct = default);

    /// <summary>
    /// Remove stale documents for a parent ID that don't match the valid hashes.
    /// Used for incremental updates - remove old segments, keep new ones.
    /// </summary>
    Task RemoveStaleDocumentsAsync(string collectionName, string parentId, IEnumerable<string> validHashes, CancellationToken ct = default);

    // ========== Summary Caching (Optional) ==========

    /// <summary>
    /// Get cached summary for a document (if supported).
    /// Returns null if no cached summary exists.
    /// </summary>
    Task<CachedSummary?> GetCachedSummaryAsync(string collectionName, string documentId, CancellationToken ct = default);

    /// <summary>
    /// Cache a summary for future retrieval.
    /// </summary>
    Task CacheSummaryAsync(string collectionName, CachedSummary summary, CancellationToken ct = default);
}

/// <summary>
/// Vector store backend type.
/// </summary>
public enum VectorStoreBackend
{
    /// <summary>
    /// In-memory storage - no external dependencies, vectors lost on exit.
    /// Best for: Testing, prototyping, ephemeral workloads.
    /// </summary>
    InMemory,

    /// <summary>
    /// DuckDB embedded database - persistent storage, file-based, no external server required.
    /// Uses VSS extension for HNSW indexes when available, falls back to in-memory cosine similarity.
    /// Best for: Standalone deployments, development, embedded scenarios.
    /// </summary>
    DuckDB,

    /// <summary>
    /// Qdrant vector database - persistent storage, requires Qdrant server.
    /// Best for: Production deployments, distributed systems, multi-node setups.
    /// Supports advanced features: multi-vector embeddings, hybrid search, filtering.
    /// </summary>
    Qdrant
}

/// <summary>
/// Collection statistics.
/// </summary>
public class CollectionStats
{
    public required string CollectionName { get; init; }
    public long DocumentCount { get; init; }
    public int VectorDimension { get; init; }
    public long? SizeBytes { get; init; }
}
