using System.Diagnostics;
using DuckDB.NET.Data;

namespace DoomSummarizer.Services;

/// <summary>
///     One-time migration from DuckDB vector store to sqlite-vec.
///     Runs on first launch when vector_backend is set to "sqlite-vec"
///     and a DuckDB file exists but sqlite-vec file doesn't.
///     Exports all item embeddings and reimports them.
/// </summary>
public static class VectorStoreMigration
{
    /// <summary>
    ///     Migrate item embeddings from DuckDB to sqlite-vec if needed.
    ///     Returns the number of items migrated, or 0 if no migration was needed.
    /// </summary>
    public static async Task<int> MigrateIfNeededAsync(
        string duckDbPath, SqliteVecItemVectorStore sqliteVecStore)
    {
        // Only migrate if DuckDB file exists
        if (!File.Exists(duckDbPath))
        {
            Debug.WriteLine("VectorStoreMigration: no DuckDB file found, skipping");
            return 0;
        }

        // Check if sqlite-vec store already has data
        var existingCount = await sqliteVecStore.GetItemCountAsync();
        if (existingCount > 0)
        {
            Debug.WriteLine($"VectorStoreMigration: sqlite-vec already has {existingCount} items, skipping");
            return 0;
        }

        Debug.WriteLine($"VectorStoreMigration: migrating from {duckDbPath}");
        var migrated = 0;

        try
        {
            await using var duckConn = new DuckDBConnection($"Data Source={duckDbPath}");
            await duckConn.OpenAsync();

            // Check if the table exists
            using var checkCmd = duckConn.CreateCommand();
            checkCmd.CommandText = """
                                   SELECT COUNT(*) FROM information_schema.tables
                                   WHERE table_name = 'item_embeddings'
                                   """;
            var tableExists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0;
            if (!tableExists)
            {
                Debug.WriteLine("VectorStoreMigration: no item_embeddings table in DuckDB");
                return 0;
            }

            // Load VSS extension to read FLOAT[] columns
            using var vssCmd = duckConn.CreateCommand();
            vssCmd.CommandText = "INSTALL vss; LOAD vss;";
            try
            {
                await vssCmd.ExecuteNonQueryAsync();
            }
            catch
            {
                // VSS might not be needed for reading
            }

            // Read all embeddings
            using var readCmd = duckConn.CreateCommand();
            readCmd.CommandText = """
                                  SELECT item_id, title, source, url, embedding
                                  FROM item_embeddings
                                  WHERE embedding IS NOT NULL
                                  """;

            using var reader = await readCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var itemId = reader.GetString(0);
                var title = reader.GetString(1);
                var source = reader.IsDBNull(2) ? null : reader.GetString(2);
                var url = reader.IsDBNull(3) ? null : reader.GetString(3);

                // DuckDB FLOAT[] comes back as an array type
                float[]? embedding = null;
                if (!reader.IsDBNull(4))
                {
                    try
                    {
                        var value = reader.GetValue(4);
                        embedding = value switch
                        {
                            float[] f => f,
                            double[] d => d.Select(v => (float)v).ToArray(),
                            _ => null
                        };
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"VectorStoreMigration: failed to read embedding for {itemId}: {ex.Message}");
                        continue;
                    }
                }

                if (embedding != null)
                {
                    await sqliteVecStore.UpsertItemEmbeddingAsync(itemId, title, source, url, embedding);
                    migrated++;
                }
            }

            Debug.WriteLine($"VectorStoreMigration: migrated {migrated} item embeddings");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"VectorStoreMigration: failed: {ex.Message}");
            // Non-fatal — the store will work empty and rebuild over time
        }

        return migrated;
    }
}
