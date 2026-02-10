using DuckDB.NET.Data;

namespace DoomSummarizer.Services;

/// <summary>
///     DuckDB-backed vector store for item embeddings with HNSW indexing.
///     Handles only item embedding storage and similarity search.
///     Entity operations are in <see cref="IEntityGraphStore" /> / <see cref="DuckDbEntityGraphStore" />.
///     Single-file database (~/.doomsummarizer/vectors.duckdb).
/// </summary>
public class DuckDbVectorStore : IItemVectorStore
{
    private readonly string _dbPath;
    private readonly int _dim;
    private DuckDBConnection? _conn;

    /// <summary>
    ///     The underlying DuckDB connection. Available after <see cref="InitializeAsync" />.
    ///     Used to share a single connection with <see cref="DuckDbEntityGraphStore" />.
    /// </summary>
    public DuckDBConnection? Connection => _conn;

    /// <summary>
    ///     Create a new DuckDB vector store.
    /// </summary>
    /// <param name="dbPath">Path to the .duckdb file</param>
    /// <param name="embeddingDimension">Embedding vector dimension (384 for all-MiniLM-L6-v2)</param>
    public DuckDbVectorStore(string dbPath, int embeddingDimension = 384)
    {
        _dbPath = dbPath;
        _dim = embeddingDimension;
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn != null)
        {
            await _conn.CloseAsync();
            await _conn.DisposeAsync();
        }
    }

    public async Task InitializeAsync()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _conn = new DuckDBConnection($"Data Source={_dbPath}");
        await _conn.OpenAsync();

        // Install and load VSS extension with persistent HNSW
        await ExecAsync("INSTALL vss; LOAD vss; SET hnsw_enable_experimental_persistence = true;");

        await CreateTablesAsync();
    }

    private async Task CreateTablesAsync()
    {
        // Item embeddings - for finding similar articles
        await ExecAsync($"""
                         CREATE TABLE IF NOT EXISTS item_embeddings (
                             item_id VARCHAR PRIMARY KEY,
                             title VARCHAR NOT NULL,
                             source VARCHAR,
                             url VARCHAR,
                             embedding FLOAT[{_dim}],
                             entity_profile FLOAT[{_dim}],
                             indexed_at TIMESTAMP DEFAULT current_timestamp
                         )
                         """);

        // Migration: add entity_profile column if not exists
        try
        {
            await ExecAsync($"ALTER TABLE item_embeddings ADD COLUMN entity_profile FLOAT[{_dim}]");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DuckDB entity_profile migration (expected if exists): {ex.Message}");
        }

        // HNSW index for fast cosine similarity search on item embeddings
        try
        {
            await ExecAsync("""
                            CREATE INDEX IF NOT EXISTS item_emb_hnsw
                            ON item_embeddings USING HNSW (embedding)
                            WITH (metric = 'cosine')
                            """);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DuckDB HNSW index creation skipped: {ex.Message}");
        }
    }

    // --- Item Embeddings ---

    /// <summary>
    ///     Upsert an item embedding for HNSW-backed similarity search.
    /// </summary>
    public async Task UpsertItemEmbeddingAsync(string itemId, string title, string? source, string? url,
        float[]? embedding)
    {
        // Skip items without embeddings (low-salience deferred chunks)
        if (embedding == null || embedding.Length == 0) return;

        await ExecAsync(
            """
            INSERT INTO item_embeddings (item_id, title, source, url, embedding, indexed_at)
            VALUES ($1, $2, $3, $4, $5, now())
            ON CONFLICT (item_id) DO UPDATE SET
                embedding = $5,
                indexed_at = now()
            """,
            itemId, title, source, url, embedding);
    }

    /// <summary>
    ///     Find similar items using HNSW cosine similarity search.
    /// </summary>
    public async Task<List<(string itemId, string title, string? url, float similarity)>> FindSimilarItemsAsync(
        float[] queryEmbedding, int topK = 10, float minSimilarity = 0.5f)
    {
        var results = new List<(string, string, string?, float)>();
        using var cmd = _conn!.CreateCommand();

        cmd.CommandText = $"""
                           SELECT item_id, title, url,
                                  1.0 - array_cosine_distance(embedding, $1::FLOAT[{_dim}]) as similarity
                           FROM item_embeddings
                           WHERE embedding IS NOT NULL
                           ORDER BY array_cosine_distance(embedding, $1::FLOAT[{_dim}])
                           LIMIT $2
                           """;
        cmd.Parameters.Add(new DuckDBParameter { Value = queryEmbedding });
        cmd.Parameters.Add(new DuckDBParameter { Value = topK });

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var similarity = reader.GetFloat(3);
            if (similarity >= minSimilarity)
                results.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    similarity));
        }

        return results;
    }

    /// <summary>
    ///     Get the number of indexed item embeddings.
    /// </summary>
    public async Task<int> GetItemCountAsync()
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM item_embeddings";
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result ?? 0);
    }

    /// <summary>
    ///     Delete all item embeddings.
    /// </summary>
    public async Task ClearAllAsync()
    {
        await ExecAsync("DELETE FROM item_embeddings;");
    }

    /// <summary>
    ///     Cleanup old item embeddings past retention window.
    /// </summary>
    public async Task CleanupAsync(int retentionDays)
    {
        await ExecAsync($"""
                         DELETE FROM item_embeddings
                         WHERE indexed_at < current_timestamp - INTERVAL '{retentionDays} days';
                         """);
    }

    /// <inheritdoc />
    public bool SupportsHnsw => true;

    /// <inheritdoc />
    public object? GetUnderlyingConnection() => _conn;

    // --- Helpers ---

    private async Task ExecAsync(string sql, params object?[] parameters)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;

        for (var i = 0; i < parameters.Length; i++)
            cmd.Parameters.Add(new DuckDBParameter { Value = parameters[i] ?? DBNull.Value });

        await cmd.ExecuteNonQueryAsync();
    }
}