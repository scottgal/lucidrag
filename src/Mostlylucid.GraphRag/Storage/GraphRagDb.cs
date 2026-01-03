using DuckDB.NET.Data;

namespace Mostlylucid.GraphRag.Storage;

/// <summary>
/// DuckDB storage for GraphRAG. Schema designed for:
/// - Vector search via HNSW index (cosine distance)
/// - Provenance via join tables (entity_mentions, relationship_mentions)
/// - Queryable chunk→entity and chunk→relationship mappings
/// </summary>
public sealed class GraphRagDb : IDisposable
{
    private readonly string _dbPath;
    private readonly int _dim;
    private DuckDBConnection? _conn;

    public GraphRagDb(string dbPath, int embeddingDimension = 384)
    {
        _dbPath = dbPath;
        _dim = embeddingDimension;
    }

    public async Task InitializeAsync()
    {
        _conn = new DuckDBConnection($"Data Source={_dbPath}");
        await _conn.OpenAsync();
        await ExecAsync("INSTALL vss; LOAD vss; SET hnsw_enable_experimental_persistence = true;");
        await CreateTablesAsync();
    }

    private async Task CreateTablesAsync()
    {
        // Documents: just metadata, not full content (chunks hold text)
        await ExecAsync("""
            CREATE TABLE IF NOT EXISTS documents (
                id VARCHAR PRIMARY KEY, 
                path VARCHAR NOT NULL, 
                title VARCHAR, 
                content_hash VARCHAR,
                indexed_at TIMESTAMP DEFAULT now()
            )
            """);

        // Chunks with embeddings
        await ExecAsync($"""
            CREATE TABLE IF NOT EXISTS chunks (
                id VARCHAR PRIMARY KEY, 
                document_id VARCHAR NOT NULL, 
                chunk_index INTEGER NOT NULL, 
                text TEXT NOT NULL, 
                embedding FLOAT[{_dim}], 
                token_count INTEGER
            )
            """);

        // HNSW index for cosine distance - ORDER BY array_cosine_distance(...) LIMIT n will use this
        await ExecAsync("""
            CREATE INDEX IF NOT EXISTS chunks_hnsw_idx ON chunks USING HNSW (embedding) WITH (metric = 'cosine')
            """);

        // Entities with normalized lookup key
        await ExecAsync("""
            CREATE TABLE IF NOT EXISTS entities (
                id VARCHAR PRIMARY KEY, 
                name VARCHAR NOT NULL, 
                display_name VARCHAR NOT NULL,
                normalized_name VARCHAR NOT NULL UNIQUE, 
                type VARCHAR NOT NULL, 
                description TEXT,
                mention_count INTEGER DEFAULT 0
            )
            """);

        // Entity mentions: provenance join table (which chunk mentioned which entity)
        await ExecAsync("""
            CREATE TABLE IF NOT EXISTS entity_mentions (
                entity_id VARCHAR NOT NULL REFERENCES entities(id),
                chunk_id VARCHAR NOT NULL REFERENCES chunks(id),
                mention_count INTEGER DEFAULT 1,
                PRIMARY KEY (entity_id, chunk_id)
            )
            """);

        // Relationships: edges in the knowledge graph
        await ExecAsync("""
            CREATE TABLE IF NOT EXISTS relationships (
                id VARCHAR PRIMARY KEY, 
                source_entity_id VARCHAR NOT NULL REFERENCES entities(id), 
                target_entity_id VARCHAR NOT NULL REFERENCES entities(id), 
                relationship_type VARCHAR NOT NULL,
                description TEXT,
                weight FLOAT DEFAULT 1.0,
                UNIQUE(source_entity_id, target_entity_id, relationship_type)
            )
            """);

        // Relationship mentions: provenance join table
        await ExecAsync("""
            CREATE TABLE IF NOT EXISTS relationship_mentions (
                relationship_id VARCHAR NOT NULL REFERENCES relationships(id),
                chunk_id VARCHAR NOT NULL REFERENCES chunks(id),
                mention_count INTEGER DEFAULT 1,
                PRIMARY KEY (relationship_id, chunk_id)
            )
            """);

        // Communities
        await ExecAsync("""
            CREATE TABLE IF NOT EXISTS communities (
                id VARCHAR PRIMARY KEY, 
                level INTEGER NOT NULL, 
                summary TEXT
            )
            """);

        // Community membership: which entities belong to which community
        await ExecAsync("""
            CREATE TABLE IF NOT EXISTS community_members (
                community_id VARCHAR NOT NULL REFERENCES communities(id),
                entity_id VARCHAR NOT NULL REFERENCES entities(id),
                PRIMARY KEY (community_id, entity_id)
            )
            """);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Documents
    // ═══════════════════════════════════════════════════════════════════════════

    public Task UpsertDocumentAsync(string id, string path, string title, string contentHash) =>
        ExecAsync("""
            INSERT INTO documents (id, path, title, content_hash) VALUES ($1, $2, $3, $4)
            ON CONFLICT(id) DO UPDATE SET path=EXCLUDED.path, title=EXCLUDED.title, content_hash=EXCLUDED.content_hash, indexed_at=now()
            """, id, path, title, contentHash);

    /// <summary>Check if document exists with the same content hash (skip re-indexing if unchanged)</summary>
    public async Task<bool> DocumentExistsWithHashAsync(string id, string contentHash)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM documents WHERE id = $1 AND content_hash = $2";
        cmd.Parameters.Add(new DuckDBParameter { Value = id });
        cmd.Parameters.Add(new DuckDBParameter { Value = contentHash });
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result) > 0;
    }

    /// <summary>Delete all chunks for a document (required before re-indexing due to HNSW constraints)</summary>
    public async Task DeleteDocumentChunksAsync(string docId)
    {
        // First delete entity mentions that reference these chunks
        await ExecAsync("""
            DELETE FROM entity_mentions WHERE chunk_id IN (SELECT id FROM chunks WHERE document_id = $1)
            """, docId);
        
        // Then delete relationship mentions
        await ExecAsync("""
            DELETE FROM relationship_mentions WHERE chunk_id IN (SELECT id FROM chunks WHERE document_id = $1)
            """, docId);
        
        // Finally delete the chunks themselves
        await ExecAsync("DELETE FROM chunks WHERE document_id = $1", docId);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Chunks
    // ═══════════════════════════════════════════════════════════════════════════

    public Task InsertChunkAsync(string id, string docId, int idx, string text, float[] emb, int tokens) =>
        ExecAsync("INSERT INTO chunks VALUES ($1,$2,$3,$4,$5,$6)",
            id, docId, idx, text, emb, tokens);

    /// <summary>
    /// Vector search using HNSW index. Uses array_cosine_distance + ORDER BY + LIMIT 
    /// which triggers HNSW_INDEX_SCAN (verified via EXPLAIN).
    /// </summary>
    public async Task<List<ChunkResult>> SearchChunksAsync(float[] queryEmb, int topK = 10)
    {
        using var cmd = _conn!.CreateCommand();
        // Note: array_cosine_distance (not similarity) is required to use the HNSW index with metric='cosine'
        // The index is used when: ORDER BY array_cosine_distance(...) LIMIT n
        cmd.CommandText = $"""
            SELECT id, document_id, text, chunk_index, array_cosine_distance(embedding, $1::FLOAT[{_dim}]) as distance
            FROM chunks 
            WHERE embedding IS NOT NULL
            ORDER BY distance
            LIMIT $2
            """;
        cmd.Parameters.Add(new DuckDBParameter { Value = queryEmb });
        cmd.Parameters.Add(new DuckDBParameter { Value = topK });
        
        return await ReadAsync(cmd, r => new ChunkResult(
            r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), 
            1.0f - r.GetFloat(4))); // Convert distance to similarity for API consistency
    }

    public async Task<List<ChunkResult>> GetAllChunksAsync()
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT id, document_id, text, chunk_index FROM chunks";
        return await ReadAsync(cmd, r => new ChunkResult(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3)));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Entities with proper provenance
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task UpsertEntityAsync(string id, string name, string type, string? desc, string[] chunkIds)
    {
        var normalized = Normalize(name);
        
        // Upsert entity
        await ExecAsync("""
            INSERT INTO entities (id, name, display_name, normalized_name, type, description, mention_count)
            VALUES ($1, $2, $2, $3, $4, $5, $6)
            ON CONFLICT(normalized_name) DO UPDATE SET
                description = COALESCE(EXCLUDED.description, entities.description),
                mention_count = entities.mention_count + EXCLUDED.mention_count
            """, id, name, normalized, type, desc, chunkIds.Length);

        // Batch insert mentions (provenance) - single SQL with VALUES list
        if (chunkIds.Length > 0)
        {
            await BatchUpsertMentionsAsync("entity_mentions", "entity_id", id, chunkIds);
        }
    }

    public async Task<EntityResult?> GetEntityByNameAsync(string name)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT id, name, type, description, mention_count FROM entities WHERE normalized_name = $1";
        cmd.Parameters.Add(new DuckDBParameter { Value = Normalize(name) });
        var list = await ReadAsync(cmd, r => new EntityResult(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4)));
        return list.FirstOrDefault();
    }

    public async Task<List<EntityResult>> GetAllEntitiesAsync()
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT id, name, type, description, mention_count FROM entities ORDER BY mention_count DESC";
        return await ReadAsync(cmd, r => new EntityResult(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4)));
    }

    /// <summary>Get all chunks that mention a specific entity (provenance query)</summary>
    public async Task<List<string>> GetChunksForEntityAsync(string entityId)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT chunk_id FROM entity_mentions WHERE entity_id = $1";
        cmd.Parameters.Add(new DuckDBParameter { Value = entityId });
        return await ReadAsync(cmd, r => r.GetString(0));
    }

    /// <summary>Get all entities mentioned in a specific chunk (provenance query)</summary>
    public async Task<List<EntityResult>> GetEntitiesInChunkAsync(string chunkId)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            SELECT e.id, e.name, e.type, e.description, e.mention_count
            FROM entities e
            JOIN entity_mentions em ON e.id = em.entity_id
            WHERE em.chunk_id = $1
            """;
        cmd.Parameters.Add(new DuckDBParameter { Value = chunkId });
        return await ReadAsync(cmd, r => new EntityResult(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4)));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Relationships with proper provenance
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task UpsertRelationshipAsync(string id, string srcId, string tgtId, string relType, string? desc, string[] chunkIds)
    {
        // Upsert relationship
        await ExecAsync("""
            INSERT INTO relationships (id, source_entity_id, target_entity_id, relationship_type, description, weight)
            VALUES ($1, $2, $3, $4, $5, 1.0)
            ON CONFLICT(source_entity_id, target_entity_id, relationship_type) DO UPDATE SET
                weight = relationships.weight + 1.0,
                description = COALESCE(EXCLUDED.description, relationships.description)
            """, id, srcId, tgtId, relType, desc);

        // Batch insert mentions (provenance) - single SQL with VALUES list
        if (chunkIds.Length > 0)
        {
            await BatchUpsertMentionsAsync("relationship_mentions", "relationship_id", id, chunkIds);
        }
    }

    public async Task<List<RelationshipResult>> GetRelationshipsForEntityAsync(string entityId)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.source_entity_id, r.target_entity_id, r.relationship_type, r.description, r.weight, s.name, t.name
            FROM relationships r
            JOIN entities s ON r.source_entity_id = s.id
            JOIN entities t ON r.target_entity_id = t.id
            WHERE r.source_entity_id = $1 OR r.target_entity_id = $1
            """;
        cmd.Parameters.Add(new DuckDBParameter { Value = entityId });
        return await ReadAsync(cmd, r => new RelationshipResult(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), 
            r.IsDBNull(4) ? null : r.GetString(4), r.GetFloat(5), r.GetString(6), r.GetString(7)));
    }

    public async Task<List<RelationshipResult>> GetAllRelationshipsAsync()
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.source_entity_id, r.target_entity_id, r.relationship_type, r.description, r.weight, s.name, t.name
            FROM relationships r
            JOIN entities s ON r.source_entity_id = s.id
            JOIN entities t ON r.target_entity_id = t.id
            ORDER BY r.weight DESC
            """;
        return await ReadAsync(cmd, r => new RelationshipResult(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.GetFloat(5), r.GetString(6), r.GetString(7)));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Communities
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task InsertCommunityAsync(string id, int level, string[] entityIds, string? summary)
    {
        await ExecAsync("INSERT INTO communities (id, level, summary) VALUES ($1, $2, $3) ON CONFLICT(id) DO UPDATE SET summary = EXCLUDED.summary",
            id, level, summary);
        
        foreach (var entityId in entityIds)
            await ExecAsync("INSERT INTO community_members (community_id, entity_id) VALUES ($1, $2) ON CONFLICT DO NOTHING", id, entityId);
    }

    public Task UpdateCommunitySummaryAsync(string id, string summary) =>
        ExecAsync("UPDATE communities SET summary = $2 WHERE id = $1", id, summary);

    public async Task<List<CommunityResult>> GetCommunitiesAsync(int? level = null)
    {
        var results = new List<CommunityResult>();
        
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = level.HasValue 
            ? "SELECT c.id, c.level, c.summary, array_agg(cm.entity_id) FROM communities c LEFT JOIN community_members cm ON c.id = cm.community_id WHERE c.level = $1 GROUP BY c.id, c.level, c.summary"
            : "SELECT c.id, c.level, c.summary, array_agg(cm.entity_id) FROM communities c LEFT JOIN community_members cm ON c.id = cm.community_id GROUP BY c.id, c.level, c.summary";
        if (level.HasValue) cmd.Parameters.Add(new DuckDBParameter { Value = level.Value });
        
        return await ReadAsync(cmd, r => {
            var entityIds = r.GetValue(3) switch 
            { 
                string[] a => a, 
                List<string> l => l.ToArray(), 
                IEnumerable<object> e => e.Where(x => x != null).Select(o => o.ToString()!).ToArray(), 
                _ => Array.Empty<string>() 
            };
            return new CommunityResult(r.GetString(0), r.GetInt32(1), entityIds, r.IsDBNull(2) ? null : r.GetString(2));
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Stats
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<DbStats> GetStatsAsync() => new(
        await ScalarAsync<long>("SELECT COUNT(*) FROM documents"),
        await ScalarAsync<long>("SELECT COUNT(*) FROM chunks"),
        await ScalarAsync<long>("SELECT COUNT(*) FROM entities"),
        await ScalarAsync<long>("SELECT COUNT(*) FROM relationships"),
        await ScalarAsync<long>("SELECT COUNT(*) FROM communities"));

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Batch upsert mentions (entity_mentions or relationship_mentions) in a single SQL statement.
    /// Uses VALUES list instead of N individual inserts.
    /// </summary>
    private async Task BatchUpsertMentionsAsync(string table, string idColumn, string id, string[] chunkIds)
    {
        // Build: INSERT INTO table (id_column, chunk_id, mention_count) VALUES ($1, $2, 1), ($1, $3, 1), ...
        // ON CONFLICT DO UPDATE
        
        var paramIdx = 2;
        var valuesClauses = new List<string>(chunkIds.Length);
        var parameters = new List<object?> { id };
        
        foreach (var chunkId in chunkIds)
        {
            valuesClauses.Add($"($1, ${paramIdx}, 1)");
            parameters.Add(chunkId);
            paramIdx++;
        }
        
        var sql = $"""
            INSERT INTO {table} ({idColumn}, chunk_id, mention_count) 
            VALUES {string.Join(", ", valuesClauses)}
            ON CONFLICT({idColumn}, chunk_id) DO UPDATE SET mention_count = {table}.mention_count + 1
            """;
        
        await ExecAsync(sql, parameters.ToArray());
    }

    /// <summary>
    /// Normalize entity name for deduplication. Preserves C#, C++, .NET etc.
    /// </summary>
    private static string Normalize(string s)
    {
        // Collapse whitespace, lowercase, but preserve #, +, . for language names
        var result = System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ").ToLowerInvariant();
        // Remove only truly cosmetic punctuation
        return result.Replace("'", "").Replace("\"", "").Replace("`", "");
    }

    private async Task ExecAsync(string sql, params object?[] p)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        foreach (var param in p) cmd.Parameters.Add(new DuckDBParameter { Value = param });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    private static async Task<List<T>> ReadAsync<T>(DuckDBCommand cmd, Func<System.Data.IDataReader, T> map)
    {
        var list = new List<T>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(map(r));
        return list;
    }

    public void Dispose() => _conn?.Dispose();
}
