# Vector Store Tiers

The LucidRAG platform uses a three-tier vector store architecture, each tier optimized for its deployment target.

## Tier Overview

| Tier | Application | Backend | Index Type | Size | Use Case |
|------|-------------|---------|------------|------|----------|
| **Server** | LucidRAG (web) | Qdrant + PostgreSQL | HNSW (server) | N/A (external) | Production multi-user, persistent |
| **Research** | LucidResearch | DuckDB + VSS | HNSW (embedded) | ~96MB | Desktop research, moderate scale |
| **CLI** | DoomSummarizer | sqlite-vec | Brute-force cosine | ~369KB | CLI tool, <10K vectors |

## Tier 1: LucidRAG Server (Qdrant + PostgreSQL)

- **Interface**: `Mostlylucid.Storage.Core.Abstractions.IVectorStore`
- **Backend**: Qdrant vector database + PostgreSQL for metadata
- **Index**: Server-side HNSW with configurable parameters
- **Persistence**: External server (Docker container)
- **Scale**: Millions of vectors, multi-tenant
- **Config**: `appsettings.json` → `DocSummarizer:BertRag:VectorStore = "Qdrant"`

Best for: Production web deployments, multi-user access, horizontal scaling.

## Tier 2: LucidResearch (DuckDB + HNSW)

- **Interface**: `IItemVectorStore` (implemented by `DuckDbVectorStore`)
- **Backend**: DuckDB embedded database with VSS extension
- **Index**: Persistent HNSW via `hnsw_enable_experimental_persistence`
- **Persistence**: Single file (`~/.doomsummarizer/vectors.duckdb`)
- **Scale**: 10K-100K vectors, single-process
- **Config**: `storage.vector_backend: duckdb` (default)

Best for: Desktop research workflows, moderate vector counts, HNSW acceleration needed.

### Limitations
- Single-writer (DuckDB.NET limitation) — must share connection for entity graph
- ~96MB binary size for DuckDB + VSS extension
- VSS extension installation requires network on first run

## Tier 3: DoomSummarizer CLI (sqlite-vec)

- **Interface**: `IItemVectorStore` (implemented by `SqliteVecItemVectorStore`)
- **Backend**: SQLite with sqlite-vec extension
- **Index**: Brute-force cosine distance (vec0 virtual table)
- **Persistence**: File-based (`~/.doomsummarizer/vectors.vec.db`)
- **Scale**: <10K vectors (~5-20ms queries)
- **Config**: `storage.vector_backend: sqlite-vec`

Best for: CLI tools, resource-constrained environments, minimal dependencies.

### Trade-offs
- No HNSW index — brute-force scan, O(N) per query
- Fast enough for small vector counts (<10K items)
- Entity graph features (profiles, HNSW entity search) not available
- sqlite-vec extension is ~369KB vs DuckDB's ~96MB

## Configuration

In `~/.doomsummarizer/config.yaml`:

```yaml
storage:
  db_path: ~/.doomsummarizer/doom.db
  vector_db_path: ~/.doomsummarizer/vectors.duckdb
  vector_backend: sqlite-vec  # or "duckdb" (default)
  retention_days: 30
```

## Migration (DuckDB to sqlite-vec)

When switching from `duckdb` to `sqlite-vec`, a one-time migration runs automatically on first launch:

1. Detects existing DuckDB file at `vector_db_path`
2. Reads all item embeddings from `item_embeddings` table
3. Inserts into sqlite-vec `vec_items` virtual table + `item_metadata`
4. Prints migration count to console

The DuckDB file is preserved (not deleted) in case you want to switch back.

## IItemVectorStore Interface

All DoomSummarizer/LucidResearch vector operations go through:

```csharp
public interface IItemVectorStore : IAsyncDisposable
{
    Task InitializeAsync();
    bool SupportsHnsw { get; }
    Task UpsertItemEmbeddingAsync(string itemId, string title, string? source, string? url, float[]? embedding);
    Task<List<(string itemId, string title, string? url, float similarity)>> FindSimilarItemsAsync(
        float[] queryEmbedding, int topK = 10, float minSimilarity = 0.5f);
    Task<int> GetItemCountAsync();
    Task ClearAllAsync();
    Task CleanupAsync(int retentionDays);
    object? GetUnderlyingConnection(); // DuckDB: returns connection, sqlite-vec: null
}
```

## Entity Graph Impact

The entity graph store (`IEntityGraphStore`) behavior differs by tier:

| Feature | DuckDB | sqlite-vec |
|---------|--------|------------|
| Entity CRUD | Full (DuckDbEntityGraphStore) | Basic (SqliteEntityGraphStore via StorageService) |
| Entity embedding search (HNSW) | Yes | No (returns empty) |
| Entity profiles | Yes | No (stubs) |
| Co-occurrence graph | Yes | Yes |
| Entity-article mentions | Yes | Yes |

For full entity graph features, use the DuckDB backend.
