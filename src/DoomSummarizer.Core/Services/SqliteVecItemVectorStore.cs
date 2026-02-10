using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DoomSummarizer.Services;

/// <summary>
///     sqlite-vec backed item vector store for DoomSummarizer CLI.
///     Uses the vec0 virtual table for brute-force cosine similarity search.
///     Lightweight alternative to DuckDB — suitable for &lt;10K vectors (~5-20ms queries).
///     <para>
///         Three-tier vector store hierarchy:
///         <list type="bullet">
///             <item><description>LucidRAG (server): Qdrant + PostgreSQL</description></item>
///             <item><description>LucidResearch: DuckDB with HNSW</description></item>
///             <item><description>DoomSummarizer (CLI): sqlite-vec brute-force (this class)</description></item>
///         </list>
///     </para>
/// </summary>
public class SqliteVecItemVectorStore : IItemVectorStore
{
    private readonly string _dbPath;
    private readonly int _dim;
    private SqliteConnection? _conn;

    /// <summary>
    ///     Create a sqlite-vec backed vector store.
    /// </summary>
    /// <param name="dbPath">Path to the .db file (separate from main SQLite DB to avoid locking)</param>
    /// <param name="embeddingDimension">Embedding vector dimension (384 for all-MiniLM-L6-v2)</param>
    public SqliteVecItemVectorStore(string dbPath, int embeddingDimension = 384)
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

    /// <inheritdoc />
    public bool SupportsHnsw => false;

    /// <inheritdoc />
    public object? GetUnderlyingConnection() => null;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={_dbPath}");
        await _conn.OpenAsync();

        // Enable WAL mode for better concurrent access
        await ExecAsync("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");

        // Load sqlite-vec extension
        _conn.EnableExtensions(true);
        try
        {
            _conn.LoadExtension("vec0");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"sqlite-vec extension load failed: {ex.Message}");
            // Try alternative extension names
            try
            {
                _conn.LoadExtension("sqlite_vec");
            }
            catch (Exception ex2)
            {
                Debug.WriteLine($"sqlite_vec extension also failed: {ex2.Message}");
                throw new InvalidOperationException(
                    "Failed to load sqlite-vec extension. Ensure the sqlite-vec NuGet package is installed.", ex);
            }
        }

        // Create metadata table for item info (vec0 only stores rowid + embedding)
        await ExecAsync("""
                        CREATE TABLE IF NOT EXISTS item_metadata (
                            rowid INTEGER PRIMARY KEY AUTOINCREMENT,
                            item_id TEXT UNIQUE NOT NULL,
                            title TEXT NOT NULL,
                            source TEXT,
                            url TEXT,
                            indexed_at TEXT NOT NULL
                        );
                        CREATE INDEX IF NOT EXISTS idx_item_meta_item_id ON item_metadata(item_id);
                        CREATE INDEX IF NOT EXISTS idx_item_meta_indexed ON item_metadata(indexed_at);
                        """);

        // Create vec0 virtual table for cosine distance search
        await ExecAsync(
            $"CREATE VIRTUAL TABLE IF NOT EXISTS vec_items USING vec0(embedding float[{_dim}] distance_metric=cosine);");
    }

    /// <inheritdoc />
    public async Task UpsertItemEmbeddingAsync(string itemId, string title, string? source, string? url,
        float[]? embedding)
    {
        if (embedding == null || embedding.Length == 0) return;

        // Check if item already exists
        long? existingRowId = null;
        await using (var checkCmd = _conn!.CreateCommand())
        {
            checkCmd.CommandText = "SELECT rowid FROM item_metadata WHERE item_id = @itemId";
            checkCmd.Parameters.AddWithValue("@itemId", itemId);
            var result = await checkCmd.ExecuteScalarAsync();
            if (result != null)
                existingRowId = (long)result;
        }

        if (existingRowId.HasValue)
        {
            // Update existing
            await ExecAsync(
                "UPDATE item_metadata SET title = @title, source = @source, url = @url, indexed_at = @now WHERE item_id = @itemId",
                ("@title", title), ("@source", (object?)source ?? DBNull.Value),
                ("@url", (object?)url ?? DBNull.Value), ("@now", DateTimeOffset.UtcNow.ToString("O")),
                ("@itemId", itemId));

            // Update vec0 — delete and reinsert (vec0 doesn't support UPDATE)
            await ExecAsync("DELETE FROM vec_items WHERE rowid = @rowid",
                ("@rowid", existingRowId.Value));
            await InsertVecAsync(existingRowId.Value, embedding);
        }
        else
        {
            // Insert metadata first to get rowid
            await using var insertCmd = _conn!.CreateCommand();
            insertCmd.CommandText = """
                                    INSERT INTO item_metadata (item_id, title, source, url, indexed_at)
                                    VALUES (@itemId, @title, @source, @url, @now)
                                    """;
            insertCmd.Parameters.AddWithValue("@itemId", itemId);
            insertCmd.Parameters.AddWithValue("@title", title);
            insertCmd.Parameters.AddWithValue("@source", (object?)source ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@url", (object?)url ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            await insertCmd.ExecuteNonQueryAsync();

            // Get the rowid
            await using var lastIdCmd = _conn.CreateCommand();
            lastIdCmd.CommandText = "SELECT last_insert_rowid()";
            var rowId = (long)(await lastIdCmd.ExecuteScalarAsync())!;

            // Insert into vec0
            await InsertVecAsync(rowId, embedding);
        }
    }

    /// <inheritdoc />
    public async Task<List<(string itemId, string title, string? url, float similarity)>> FindSimilarItemsAsync(
        float[] queryEmbedding, int topK = 10, float minSimilarity = 0.5f)
    {
        var results = new List<(string, string, string?, float)>();

        await using var cmd = _conn!.CreateCommand();
        // vec0 returns distance (0 = identical for cosine), so similarity = 1 - distance
        cmd.CommandText = """
                          SELECT m.item_id, m.title, m.url, v.distance
                          FROM vec_items v
                          INNER JOIN item_metadata m ON m.rowid = v.rowid
                          WHERE v.embedding MATCH @query AND k = @topK
                          ORDER BY v.distance
                          """;
        cmd.Parameters.AddWithValue("@query", SerializeEmbedding(queryEmbedding));
        cmd.Parameters.AddWithValue("@topK", topK);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var distance = reader.GetFloat(3);
            var similarity = 1.0f - distance;
            if (similarity >= minSimilarity)
            {
                results.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    similarity));
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<int> GetItemCountAsync()
    {
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM item_metadata";
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result ?? 0);
    }

    /// <inheritdoc />
    public async Task ClearAllAsync()
    {
        await ExecAsync("DELETE FROM vec_items; DELETE FROM item_metadata;");
    }

    /// <inheritdoc />
    public async Task CleanupAsync(int retentionDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("O");

        // Get rowids to delete
        var rowIds = new List<long>();
        await using (var cmd = _conn!.CreateCommand())
        {
            cmd.CommandText = "SELECT rowid FROM item_metadata WHERE indexed_at < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rowIds.Add(reader.GetInt64(0));
        }

        if (rowIds.Count == 0) return;

        // Delete from vec0 and metadata
        foreach (var batch in rowIds.Chunk(50))
        {
            var placeholders = new List<string>();
            await using var cmd = _conn!.CreateCommand();
            for (var i = 0; i < batch.Length; i++)
            {
                placeholders.Add($"@id{i}");
                cmd.Parameters.AddWithValue($"@id{i}", batch[i]);
            }

            var inClause = string.Join(",", placeholders);
            cmd.CommandText = $"DELETE FROM vec_items WHERE rowid IN ({inClause}); " +
                              $"DELETE FROM item_metadata WHERE rowid IN ({inClause});";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // --- Helpers ---

    private async Task InsertVecAsync(long rowId, float[] embedding)
    {
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "INSERT INTO vec_items (rowid, embedding) VALUES (@rowid, @embedding)";
        cmd.Parameters.AddWithValue("@rowid", rowId);
        cmd.Parameters.AddWithValue("@embedding", SerializeEmbedding(embedding));
        await cmd.ExecuteNonQueryAsync();
    }

    private static string SerializeEmbedding(float[] embedding)
    {
        // sqlite-vec accepts JSON arrays for float embeddings
        return JsonSerializer.Serialize(embedding);
    }

    private async Task ExecAsync(string sql, params (string name, object value)[] parameters)
    {
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
