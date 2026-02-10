namespace DoomSummarizer.Services;

/// <summary>
///     DoomSummarizer item vector store abstraction.
///     Three tiers of vector storage across the platform:
///     <list type="bullet">
///         <item><description>LucidRAG (web/server): Qdrant + PostgreSQL via <c>Mostlylucid.Storage.Core.IVectorStore</c></description></item>
///         <item><description>LucidResearch: DuckDB with HNSW via <c>DuckDbVectorStore</c></description></item>
///         <item><description>DoomSummarizer (CLI): sqlite-vec brute-force via <c>SqliteVecItemVectorStore</c></description></item>
///     </list>
///     This interface covers the DoomSummarizer item embedding use case only.
/// </summary>
public interface IItemVectorStore : IAsyncDisposable
{
    /// <summary>Initialize the store (create tables, indexes).</summary>
    Task InitializeAsync();

    /// <summary>Whether this backend supports HNSW indexing (DuckDB: true, sqlite-vec: false).</summary>
    bool SupportsHnsw { get; }

    /// <summary>Upsert an item embedding for similarity search.</summary>
    Task UpsertItemEmbeddingAsync(string itemId, string title, string? source, string? url, float[]? embedding);

    /// <summary>Find similar items by cosine similarity.</summary>
    Task<List<(string itemId, string title, string? url, float similarity)>> FindSimilarItemsAsync(
        float[] queryEmbedding, int topK = 10, float minSimilarity = 0.5f);

    /// <summary>Get total count of indexed item embeddings.</summary>
    Task<int> GetItemCountAsync();

    /// <summary>Delete all item embeddings.</summary>
    Task ClearAllAsync();

    /// <summary>Cleanup old embeddings past retention window.</summary>
    Task CleanupAsync(int retentionDays);

    /// <summary>
    ///     Get the underlying database connection, if available.
    ///     DuckDB: returns DuckDBConnection (for shared entity graph store).
    ///     sqlite-vec: returns null (entity graph uses SQLite StorageService).
    /// </summary>
    object? GetUnderlyingConnection();
}
