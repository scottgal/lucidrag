using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
///     SQLite-backed entity graph store adapter for the sqlite-vec vector backend.
///     Delegates to StorageService.EntityGraph.cs methods for basic entity operations.
///     Advanced features (entity profiles, HNSW similarity) are not supported —
///     these require DuckDB's vector capabilities.
///     <para>
///         Use this when Config.Storage.VectorBackend is "sqlite-vec".
///         For full entity graph features, use DuckDbEntityGraphStore with DuckDB backend.
///     </para>
/// </summary>
public class SqliteEntityGraphStore : IEntityGraphStore
{
    private readonly StorageService _storage;

    public SqliteEntityGraphStore(StorageService storage)
    {
        _storage = storage;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task InitializeAsync() => Task.CompletedTask; // Tables already created by StorageService

    // --- Entity CRUD (delegated to StorageService) ---

    public Task UpsertEntityAsync(string id, string name, string type, double confidence, float[]? embedding = null)
        => _storage.UpsertEntityAsync(id, name, type, confidence, embedding);

    public Task UpsertEntityMentionAsync(string entityId, string itemId, double confidence, string? context = null)
        => _storage.UpsertEntityMentionAsync(entityId, itemId, confidence, context);

    public Task UpsertRelationshipAsync(string sourceId, string targetId, string relType = "co_occurs")
        => _storage.UpsertRelationshipAsync(sourceId, targetId, relType);

    // --- Entity Retrieval (delegated) ---

    public Task<List<GraphEntity>> GetTopEntitiesAsync(int limit = 20, string? type = null, int? daysBack = null)
        => _storage.GetTopEntitiesAsync(limit, type, daysBack);

    public Task<List<GraphRelationship>> GetRelationshipsAsync(string entityId)
        => _storage.GetRelationshipsAsync(entityId);

    public Task<List<(string itemId, string title, string? url, double confidence)>> GetArticlesForEntityAsync(string entityId)
        => _storage.GetArticlesForEntityAsync(entityId);

    /// <summary>Not supported in SQLite mode — requires HNSW (DuckDB).</summary>
    public Task<List<(GraphEntity entity, float similarity)>> FindSimilarEntitiesAsync(float[] queryEmbedding, int topK = 10)
        => Task.FromResult(new List<(GraphEntity, float)>());

    // --- Entity Embeddings ---

    public Task<Dictionary<string, float[]>> GetEntityEmbeddingsAsync(IEnumerable<string> entityIds)
        => Task.FromResult(new Dictionary<string, float[]>());

    public Task UpdateEntityEmbeddingsBatchAsync(Dictionary<string, float[]> embeddings)
        => Task.CompletedTask;

    // --- Entity Profiles (not supported in SQLite mode) ---

    public Task UpsertItemEntityProfileAsync(string itemId, float[] entityProfile)
        => Task.CompletedTask;

    public Task<List<(string itemId, string title, float similarity)>> FindRelatedByEntityProfileAsync(
        float[] queryEntityProfile, int topK = 5, float minSimilarity = 0.3f)
        => Task.FromResult(new List<(string, string, float)>());

    public Task<bool> HasEntityProfilesAsync()
        => Task.FromResult(false);

    public Task<Dictionary<string, float[]>> GetEntityProfilesAsync(IEnumerable<string> itemIds)
        => Task.FromResult(new Dictionary<string, float[]>());

    public Task<List<string>> GetItemsWithoutEntityProfilesAsync(int limit = 100)
        => Task.FromResult(new List<string>());

    // --- Entity-Item Lookups ---

    public Task<Dictionary<string, int>> GetEntityDocCountsAsync()
        => Task.FromResult(new Dictionary<string, int>());

    public Task<int> GetTotalDocsWithEntitiesAsync()
        => Task.FromResult(0);

    public Task<List<(string entityId, string name, float confidence, int mentions)>> GetEntitiesForItemAsync(string itemId)
        => Task.FromResult(new List<(string, string, float, int)>());

    public Task<List<(string itemId, string entityId, string name, string type, float confidence, int mentions)>> GetEntitiesForItemsAsync(IEnumerable<string> itemIds)
        => Task.FromResult(new List<(string, string, string, string, float, int)>());

    // --- Statistics & Maintenance ---

    public async Task<(int entities, int relationships, int mentions, int itemEmbeddings)> GetStatsAsync()
    {
        var (entities, relationships, mentions) = await _storage.GetGraphStatsAsync();
        return (entities, relationships, mentions, 0);
    }

    public Task ClearAllAsync() => Task.CompletedTask; // Would need to clear entity tables — defer to StorageService

    public Task CleanupAsync(int retentionDays) => Task.CompletedTask;
}
