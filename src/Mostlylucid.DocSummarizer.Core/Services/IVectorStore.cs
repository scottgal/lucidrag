using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Abstraction for vector storage in BertRag pipeline.
/// Allows switching between in-memory (default) and persistent (Qdrant) storage.
/// Supports both segment storage (for retrieval) and summary caching (to avoid re-LLM).
/// </summary>
public interface IVectorStore : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Initialize the vector store (create collection if needed)
    /// </summary>
    /// <param name="collectionName">Name of the collection/index</param>
    /// <param name="vectorSize">Dimension of the embedding vectors</param>
    /// <param name="ct">Cancellation token</param>
    Task InitializeAsync(string collectionName, int vectorSize, CancellationToken ct = default);
    
    /// <summary>
    /// Check if a collection exists and has segments for the given document
    /// </summary>
    /// <param name="collectionName">Name of the collection</param>
    /// <param name="docId">Document identifier (content-hash based for stability)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if the document is already indexed</returns>
    Task<bool> HasDocumentAsync(string collectionName, string docId, CancellationToken ct = default);
    
    /// <summary>
    /// Store segments with their embeddings
    /// </summary>
    /// <param name="collectionName">Name of the collection</param>
    /// <param name="segments">Segments to store (must have embeddings)</param>
    /// <param name="ct">Cancellation token</param>
    Task UpsertSegmentsAsync(string collectionName, IEnumerable<Segment> segments, CancellationToken ct = default);
    
    /// <summary>
    /// Search for similar segments by embedding vector
    /// </summary>
    /// <param name="collectionName">Name of the collection</param>
    /// <param name="queryEmbedding">Query vector</param>
    /// <param name="topK">Number of results to return</param>
    /// <param name="docId">Optional: filter by document ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Segments ordered by similarity (descending)</returns>
    Task<List<Segment>> SearchAsync(
        string collectionName, 
        float[] queryEmbedding, 
        int topK, 
        string? docId = null,
        CancellationToken ct = default);
    
    /// <summary>
    /// Get all segments for a document (for salience-based retrieval without query)
    /// </summary>
    /// <param name="collectionName">Name of the collection</param>
    /// <param name="docId">Document identifier</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>All segments for the document</returns>
    Task<List<Segment>> GetDocumentSegmentsAsync(string collectionName, string docId, CancellationToken ct = default);
    
    /// <summary>
    /// Delete a collection
    /// </summary>
    /// <param name="collectionName">Name of the collection to delete</param>
    /// <param name="ct">Cancellation token</param>
    Task DeleteCollectionAsync(string collectionName, CancellationToken ct = default);
    
    /// <summary>
    /// Delete segments for a specific document
    /// </summary>
    /// <param name="collectionName">Name of the collection</param>
    /// <param name="docId">Document identifier</param>
    /// <param name="ct">Cancellation token</param>
    Task DeleteDocumentAsync(string collectionName, string docId, CancellationToken ct = default);
    
    /// <summary>
    /// Whether this store persists data between runs
    /// </summary>
    bool IsPersistent { get; }
    
    // === Segment-Level Caching (Granular Invalidation) ===
    
    /// <summary>
    /// Get segments by their content hashes (for drift detection).
    /// Returns only segments whose content hash matches - unchanged segments.
    /// </summary>
    /// <param name="collectionName">Name of the collection</param>
    /// <param name="contentHashes">Content hashes to look up</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Dictionary of contentHash → Segment for found segments</returns>
    Task<Dictionary<string, Segment>> GetSegmentsByHashAsync(
        string collectionName, 
        IEnumerable<string> contentHashes, 
        CancellationToken ct = default);
    
    /// <summary>
    /// Remove segments that no longer exist in the document (drift cleanup).
    /// </summary>
    /// <param name="collectionName">Name of the collection</param>
    /// <param name="docId">Document ID</param>
    /// <param name="validContentHashes">Hashes of segments that still exist</param>
    /// <param name="ct">Cancellation token</param>
    Task RemoveStaleSegmentsAsync(
        string collectionName, 
        string docId, 
        IEnumerable<string> validContentHashes, 
        CancellationToken ct = default);
    
    // === Summary Caching ===
    
    /// <summary>
    /// Get a cached summary if it exists.
    /// Cache key should be evidence-based (hash of retrieved segment IDs + content hashes).
    /// </summary>
    /// <param name="collectionName">Name of the collection</param>
    /// <param name="evidenceHash">Hash of evidence set (segment identities)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Cached summary or null if not found</returns>
    Task<DocumentSummary?> GetCachedSummaryAsync(string collectionName, string evidenceHash, CancellationToken ct = default);
    
    /// <summary>
    /// Store a summary in the cache, keyed by evidence set.
    /// </summary>
    /// <param name="collectionName">Name of the collection</param>
    /// <param name="evidenceHash">Hash of evidence set</param>
    /// <param name="summary">Summary to cache</param>
    /// <param name="ct">Cancellation token</param>
    Task CacheSummaryAsync(string collectionName, string evidenceHash, DocumentSummary summary, CancellationToken ct = default);
}

/// <summary>
/// Vector store backend type
/// </summary>
public enum VectorStoreBackend
{
    /// <summary>
    /// In-memory storage (default) - no external dependencies, vectors lost on exit
    /// </summary>
    InMemory,
    
    /// <summary>
    /// Qdrant vector database - persistent storage, requires Qdrant server.
    /// Best for: Enterprise deployments, distributed systems, multi-node setups.
    /// </summary>
    Qdrant,
    
    /// <summary>
    /// DuckDB embedded database - persistent storage in a single file, no external services.
    /// Best for: Local development, CLI tools, single-machine deployments, offline use.
    /// </summary>
    DuckDB
}
