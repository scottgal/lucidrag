using System.Data;
using System.Data.Common;
using System.Text.Json;
using DuckDB.NET.Data;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// DuckDB-backed vector store for document segments and embeddings.
/// Uses native HNSW index for fast vector similarity search.
/// 
/// Features:
/// - Vector similarity search via HNSW index (cosine distance)
/// - Metadata filtering with SQL
/// - Persistent storage in a single file
/// - Word lists and caches in the same database
/// 
/// For enterprise/distributed deployments, use QdrantVectorStore instead.
/// </summary>
public sealed class DuckDbVectorStore : IVectorStore, IDisposable
{
    private readonly DuckDBConnection _connection;
    private readonly string _dbPath;
    private readonly bool _verbose;
    private int _vectorDimension;
    private bool _initialized;
    private bool _vssLoaded;

    public DuckDbVectorStore(string? dbPath = null, int vectorDimension = 384, bool verbose = false)
    {
        _dbPath = dbPath ?? ":memory:";
        _vectorDimension = vectorDimension;
        _verbose = verbose;
        _connection = new DuckDBConnection($"DataSource={_dbPath}");
        _connection.Open();
    }

    public bool IsPersistent => _dbPath != ":memory:";

    public async Task InitializeAsync(string collectionName, int vectorSize, CancellationToken ct = default)
    {
        _vectorDimension = vectorSize;
        if (_initialized) return;

        await TryLoadVssExtensionAsync(ct);
        await CreateSchemaAsync(ct);
        _initialized = true;

        if (_verbose)
        {
            Console.WriteLine($"[DuckDbVectorStore] Initialized at {_dbPath}");
            Console.WriteLine($"[DuckDbVectorStore] VSS extension: {(_vssLoaded ? "loaded (HNSW enabled)" : "not available, using fallback")}");
        }
    }

    private async Task TryLoadVssExtensionAsync(CancellationToken ct)
    {
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSTALL vss; LOAD vss;";
            await cmd.ExecuteNonQueryAsync(ct);
            
            // Enable experimental HNSW persistence for disk-backed databases
            if (_dbPath != ":memory:")
            {
                await using var cmd2 = _connection.CreateCommand();
                cmd2.CommandText = "SET hnsw_enable_experimental_persistence = true;";
                await cmd2.ExecuteNonQueryAsync(ct);
            }
            
            _vssLoaded = true;
        }
        catch
        {
            _vssLoaded = false;
        }
    }

    private async Task CreateSchemaAsync(CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        
        // Segments table - use native FLOAT[] for embeddings when VSS is available
        var embeddingType = _vssLoaded ? $"FLOAT[{_vectorDimension}]" : "TEXT";
        
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS segments (
                id VARCHAR PRIMARY KEY,
                collection VARCHAR NOT NULL,
                doc_id VARCHAR NOT NULL,
                content_hash VARCHAR NOT NULL,
                text TEXT NOT NULL,
                section_title VARCHAR,
                segment_type VARCHAR,
                heading_level INTEGER DEFAULT 0,
                index_position INTEGER DEFAULT 0,
                salience FLOAT DEFAULT 0.0,
                embedding {embeddingType},
                metadata JSON,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );
            
            CREATE INDEX IF NOT EXISTS idx_segments_collection ON segments(collection);
            CREATE INDEX IF NOT EXISTS idx_segments_doc ON segments(collection, doc_id);
            CREATE INDEX IF NOT EXISTS idx_segments_hash ON segments(collection, content_hash);
            CREATE INDEX IF NOT EXISTS idx_segments_salience ON segments(collection, salience DESC);
            """;
        
        await cmd.ExecuteNonQueryAsync(ct);
        
        // Create HNSW index for vector search if VSS is loaded
        if (_vssLoaded)
        {
            try
            {
                await using var idxCmd = _connection.CreateCommand();
                // Use cosine metric - queries must use array_cosine_distance + ORDER BY + LIMIT to engage index
                idxCmd.CommandText = """
                    CREATE INDEX IF NOT EXISTS idx_segments_embedding ON segments USING HNSW (embedding) WITH (metric = 'cosine')
                    """;
                await idxCmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                if (_verbose)
                    Console.WriteLine($"[DuckDbVectorStore] HNSW index creation failed: {ex.Message}");
            }
        }
        
        // Summary cache and word lists tables
        await using var cmd2 = _connection.CreateCommand();
        cmd2.CommandText = """
            CREATE TABLE IF NOT EXISTS summary_cache (
                cache_key VARCHAR PRIMARY KEY,
                collection VARCHAR NOT NULL,
                evidence_hash VARCHAR NOT NULL,
                summary_json TEXT NOT NULL,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );
            
            CREATE INDEX IF NOT EXISTS idx_cache_collection ON summary_cache(collection);
            CREATE INDEX IF NOT EXISTS idx_cache_evidence ON summary_cache(collection, evidence_hash);
            
            CREATE TABLE IF NOT EXISTS word_lists (
                id INTEGER PRIMARY KEY,
                word VARCHAR NOT NULL,
                word_lower VARCHAR NOT NULL,
                category VARCHAR NOT NULL,
                is_custom BOOLEAN DEFAULT FALSE,
                UNIQUE(word_lower, category)
            );
            
            CREATE INDEX IF NOT EXISTS idx_wordlist_lower ON word_lists(word_lower);
            CREATE INDEX IF NOT EXISTS idx_wordlist_category ON word_lists(category);
            """;
        
        await cmd2.ExecuteNonQueryAsync(ct);
    }

    #region IVectorStore Implementation

    public async Task<bool> HasDocumentAsync(string collectionName, string docId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM segments WHERE collection = $collection AND doc_id = $doc_id LIMIT 1";
        cmd.Parameters.Add(new DuckDBParameter("collection", collectionName));
        cmd.Parameters.Add(new DuckDBParameter("doc_id", docId));
        
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result) > 0;
    }

    public async Task UpsertSegmentsAsync(string collectionName, IEnumerable<Segment> segments, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        
        var segmentList = segments.ToList();
        if (segmentList.Count == 0) return;

        // DuckDB has auto-commit by default, so we don't need explicit transactions
        // for inserts. Just execute the upserts directly.
        foreach (var segment in segmentList)
        {
            await UpsertSegmentInternalAsync(collectionName, segment, ct);
        }
        
        if (_verbose)
            Console.WriteLine($"[DuckDbVectorStore] Upserted {segmentList.Count} segments to '{collectionName}'");
    }

    private async Task UpsertSegmentInternalAsync(string collectionName, Segment segment, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        
        var docId = ExtractDocIdFromSegment(segment);
        
        // Ensure no null values that would cause DuckDB parameter binding to fail
        var segmentId = segment.Id ?? throw new ArgumentNullException(nameof(segment), "Segment.Id cannot be null");
        var segmentText = segment.Text ?? "";
        var contentHash = segment.ContentHash ?? "";
        var sectionTitle = segment.SectionTitle ?? "";
        
        if (_vssLoaded && segment.Embedding != null)
        {
            // Native FLOAT[] storage for VSS
            cmd.CommandText = """
                INSERT INTO segments (id, collection, doc_id, content_hash, text, section_title, segment_type, 
                                      heading_level, index_position, salience, embedding)
                VALUES ($id, $collection, $doc_id, $hash, $text, $section, $type, $level, $idx, $salience, $embedding)
                ON CONFLICT (id) DO UPDATE SET
                    text = $text,
                    content_hash = $hash,
                    section_title = $section,
                    salience = $salience,
                    embedding = $embedding
                """;
            cmd.Parameters.Add(new DuckDBParameter("embedding", segment.Embedding));
        }
        else
        {
            // JSON TEXT fallback
            var embeddingJson = segment.Embedding != null ? JsonSerializer.Serialize(segment.Embedding) : null;
            cmd.CommandText = """
                INSERT INTO segments (id, collection, doc_id, content_hash, text, section_title, segment_type, 
                                      heading_level, index_position, salience, embedding)
                VALUES ($id, $collection, $doc_id, $hash, $text, $section, $type, $level, $idx, $salience, $embedding)
                ON CONFLICT (id) DO UPDATE SET
                    text = $text,
                    content_hash = $hash,
                    section_title = $section,
                    salience = $salience,
                    embedding = $embedding
                """;
            // DuckDB.NET requires explicit DBNull.Value for null strings
            cmd.Parameters.Add(new DuckDBParameter("embedding", embeddingJson != null ? embeddingJson : DBNull.Value));
        }
        
        cmd.Parameters.Add(new DuckDBParameter("id", segmentId));
        cmd.Parameters.Add(new DuckDBParameter("collection", collectionName ?? ""));
        cmd.Parameters.Add(new DuckDBParameter("doc_id", docId ?? ""));
        cmd.Parameters.Add(new DuckDBParameter("hash", contentHash));
        cmd.Parameters.Add(new DuckDBParameter("text", segmentText));
        cmd.Parameters.Add(new DuckDBParameter("section", sectionTitle));
        cmd.Parameters.Add(new DuckDBParameter("type", segment.Type.ToString()));
        cmd.Parameters.Add(new DuckDBParameter("level", segment.HeadingLevel));
        cmd.Parameters.Add(new DuckDBParameter("idx", segment.Index));
        cmd.Parameters.Add(new DuckDBParameter("salience", segment.SalienceScore));
        
        await cmd.ExecuteNonQueryAsync(ct);
    }
    
    private static string ExtractDocIdFromSegment(Segment segment)
    {
        var parts = segment.Id.Split('_');
        if (parts.Length >= 3)
            return string.Join("_", parts.Take(parts.Length - 2));
        return segment.Id;
    }

    /// <summary>
    /// Vector similarity search. Uses HNSW index when VSS is loaded.
    /// Query pattern: ORDER BY array_cosine_distance(...) LIMIT n triggers HNSW_INDEX_SCAN.
    /// </summary>
    public async Task<List<Segment>> SearchAsync(
        string collectionName,
        float[] queryEmbedding,
        int topK,
        string? docId = null,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        
        if (_vssLoaded)
        {
            // Use native HNSW vector search
            return await SearchWithHnswAsync(collectionName, queryEmbedding, topK, docId, ct);
        }
        else
        {
            // Fallback to in-memory cosine similarity
            return await SearchWithFallbackAsync(collectionName, queryEmbedding, topK, docId, ct);
        }
    }

    private async Task<List<Segment>> SearchWithHnswAsync(
        string collectionName, float[] queryEmbedding, int topK, string? docId, CancellationToken ct)
    {
        var segments = new List<Segment>();
        
        await using var cmd = _connection.CreateCommand();
        
        // IMPORTANT: array_cosine_distance + ORDER BY + LIMIT triggers HNSW_INDEX_SCAN
        // Using array_cosine_similarity or other patterns may cause full table scan!
        var docFilter = docId != null ? "AND doc_id = $doc_id" : "";
        
        cmd.CommandText = $"""
            SELECT id, doc_id, content_hash, text, section_title, segment_type, heading_level,
                   index_position, salience, embedding,
                   array_cosine_distance(embedding, $query::FLOAT[{_vectorDimension}]) as distance
            FROM segments
            WHERE collection = $collection {docFilter} AND embedding IS NOT NULL
            ORDER BY distance
            LIMIT $topk
            """;
        
        cmd.Parameters.Add(new DuckDBParameter("collection", collectionName));
        cmd.Parameters.Add(new DuckDBParameter("query", queryEmbedding));
        cmd.Parameters.Add(new DuckDBParameter("topk", topK));
        if (docId != null)
            cmd.Parameters.Add(new DuckDBParameter("doc_id", docId));
        
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var segment = ReadSegmentFromReader(reader, useNativeEmbedding: true);
            if (segment != null)
            {
                // Convert distance to similarity (cosine distance = 1 - similarity for normalized vectors)
                var distance = reader.GetFloat(10);
                segment.QuerySimilarity = 1.0 - distance;
                segments.Add(segment);
            }
        }
        
        return segments;
    }

    private async Task<List<Segment>> SearchWithFallbackAsync(
        string collectionName, float[] queryEmbedding, int topK, string? docId, CancellationToken ct)
    {
        // Load all segments with embeddings, compute similarity in memory
        var allSegments = await GetAllSegmentsWithEmbeddingsAsync(collectionName, docId, ct);
        
        if (allSegments.Count == 0)
            return new List<Segment>();

        var scored = allSegments
            .Where(s => s.Embedding != null)
            .Select(s => (Segment: s, Score: CosineSimilarity(queryEmbedding, s.Embedding!)))
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();

        foreach (var (segment, score) in scored)
            segment.QuerySimilarity = score;

        return scored.Select(x => x.Segment).ToList();
    }

    private async Task<List<Segment>> GetAllSegmentsWithEmbeddingsAsync(string collectionName, string? docId, CancellationToken ct)
    {
        var segments = new List<Segment>();
        
        await using var cmd = _connection.CreateCommand();
        
        var docFilter = docId != null ? "AND doc_id = $doc_id" : "";
        cmd.CommandText = $"""
            SELECT id, doc_id, content_hash, text, section_title, segment_type, heading_level,
                   index_position, salience, embedding
            FROM segments
            WHERE collection = $collection {docFilter} AND embedding IS NOT NULL
            ORDER BY salience DESC
            """;
        
        cmd.Parameters.Add(new DuckDBParameter("collection", collectionName));
        if (docId != null)
            cmd.Parameters.Add(new DuckDBParameter("doc_id", docId));
        
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var segment = ReadSegmentFromReader(reader, useNativeEmbedding: false);
            if (segment != null)
                segments.Add(segment);
        }
        
        return segments;
    }

    public async Task<List<Segment>> GetDocumentSegmentsAsync(string collectionName, string docId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        
        var segments = new List<Segment>();
        
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, doc_id, content_hash, text, section_title, segment_type, heading_level,
                   index_position, salience, embedding
            FROM segments
            WHERE collection = $collection AND doc_id = $doc_id
            ORDER BY index_position
            """;
        cmd.Parameters.Add(new DuckDBParameter("collection", collectionName));
        cmd.Parameters.Add(new DuckDBParameter("doc_id", docId));
        
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var segment = ReadSegmentFromReader(reader, useNativeEmbedding: _vssLoaded);
            if (segment != null)
                segments.Add(segment);
        }
        
        return segments;
    }

    public async Task DeleteCollectionAsync(string collectionName, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        
        // DuckDB's HNSW index has issues with DELETE operations - it can corrupt the index.
        // Workaround: Drop the HNSW index before deletion, then recreate it after.
        if (_vssLoaded)
        {
            try
            {
                await using var dropCmd = _connection.CreateCommand();
                dropCmd.CommandText = "DROP INDEX IF EXISTS idx_segments_embedding";
                await dropCmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                if (_verbose)
                    Console.WriteLine($"[DuckDbVectorStore] Warning: Could not drop HNSW index: {ex.Message}");
            }
        }
        
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM segments WHERE collection = $collection";
        cmd.Parameters.Add(new DuckDBParameter("collection", collectionName));
        
        var deleted = await cmd.ExecuteNonQueryAsync(ct);
        
        await using var cacheCmd = _connection.CreateCommand();
        cacheCmd.CommandText = "DELETE FROM summary_cache WHERE collection = $collection";
        cacheCmd.Parameters.Add(new DuckDBParameter("collection", collectionName));
        await cacheCmd.ExecuteNonQueryAsync(ct);
        
        // Recreate HNSW index after deletion
        if (_vssLoaded)
        {
            try
            {
                await using var idxCmd = _connection.CreateCommand();
                idxCmd.CommandText = """
                    CREATE INDEX IF NOT EXISTS idx_segments_embedding ON segments USING HNSW (embedding) WITH (metric = 'cosine')
                    """;
                await idxCmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                if (_verbose)
                    Console.WriteLine($"[DuckDbVectorStore] Warning: Could not recreate HNSW index: {ex.Message}");
            }
        }
        
        if (_verbose)
            Console.WriteLine($"[DuckDbVectorStore] Deleted {deleted} segments from '{collectionName}'");
    }

    public async Task DeleteDocumentAsync(string collectionName, string docId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        
        // DuckDB's HNSW index has issues with DELETE operations - it can corrupt the index.
        // Workaround: Drop the HNSW index before deletion, then recreate it after.
        if (_vssLoaded)
        {
            try
            {
                await using var dropCmd = _connection.CreateCommand();
                dropCmd.CommandText = "DROP INDEX IF EXISTS idx_segments_embedding";
                await dropCmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                if (_verbose)
                    Console.WriteLine($"[DuckDbVectorStore] Warning: Could not drop HNSW index: {ex.Message}");
            }
        }
        
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM segments WHERE collection = $collection AND doc_id = $doc_id";
        cmd.Parameters.Add(new DuckDBParameter("collection", collectionName));
        cmd.Parameters.Add(new DuckDBParameter("doc_id", docId));
        
        var deleted = await cmd.ExecuteNonQueryAsync(ct);
        
        // Recreate HNSW index after deletion
        if (_vssLoaded)
        {
            try
            {
                await using var idxCmd = _connection.CreateCommand();
                idxCmd.CommandText = """
                    CREATE INDEX IF NOT EXISTS idx_segments_embedding ON segments USING HNSW (embedding) WITH (metric = 'cosine')
                    """;
                await idxCmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                if (_verbose)
                    Console.WriteLine($"[DuckDbVectorStore] Warning: Could not recreate HNSW index: {ex.Message}");
            }
        }
        
        if (_verbose)
            Console.WriteLine($"[DuckDbVectorStore] Deleted {deleted} segments for doc '{docId}'");
    }

    public async Task<Dictionary<string, Segment>> GetSegmentsByHashAsync(
        string collectionName,
        IEnumerable<string> contentHashes,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        
        var hashList = contentHashes.ToList();
        if (hashList.Count == 0)
            return new Dictionary<string, Segment>();

        var result = new Dictionary<string, Segment>();
        
        const int batchSize = 100;
        for (int i = 0; i < hashList.Count; i += batchSize)
        {
            var batch = hashList.Skip(i).Take(batchSize).ToList();
            var placeholders = string.Join(",", batch.Select((_, idx) => $"$hash{idx}"));
            
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT id, doc_id, content_hash, text, section_title, segment_type, heading_level,
                       index_position, salience, embedding
                FROM segments
                WHERE collection = $collection AND content_hash IN ({placeholders})
                """;
            cmd.Parameters.Add(new DuckDBParameter("collection", collectionName));
            
            for (int j = 0; j < batch.Count; j++)
                cmd.Parameters.Add(new DuckDBParameter($"hash{j}", batch[j]));
            
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var segment = ReadSegmentFromReader(reader, useNativeEmbedding: _vssLoaded);
                if (segment != null && !string.IsNullOrEmpty(segment.ContentHash))
                    result[segment.ContentHash] = segment;
            }
        }
        
        return result;
    }

    public async Task RemoveStaleSegmentsAsync(
        string collectionName,
        string docId,
        IEnumerable<string> validContentHashes,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        
        var hashList = validContentHashes.ToList();
        
        if (hashList.Count == 0)
        {
            await DeleteDocumentAsync(collectionName, docId, ct);
            return;
        }

        var placeholders = string.Join(",", hashList.Select((_, idx) => $"$hash{idx}"));
        
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            DELETE FROM segments 
            WHERE collection = $collection 
              AND doc_id = $doc_id 
              AND content_hash NOT IN ({placeholders})
            """;
        cmd.Parameters.Add(new DuckDBParameter("collection", collectionName));
        cmd.Parameters.Add(new DuckDBParameter("doc_id", docId));
        
        for (int i = 0; i < hashList.Count; i++)
            cmd.Parameters.Add(new DuckDBParameter($"hash{i}", hashList[i]));
        
        var deleted = await cmd.ExecuteNonQueryAsync(ct);
        
        if (_verbose && deleted > 0)
            Console.WriteLine($"[DuckDbVectorStore] Removed {deleted} stale segments from '{docId}'");
    }

    public async Task<DocumentSummary?> GetCachedSummaryAsync(string collectionName, string evidenceHash, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT summary_json FROM summary_cache WHERE collection = $collection AND evidence_hash = $hash";
        cmd.Parameters.Add(new DuckDBParameter("collection", collectionName));
        cmd.Parameters.Add(new DuckDBParameter("hash", evidenceHash));
        
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result == null || result == DBNull.Value) return null;
        
        return JsonSerializer.Deserialize<DocumentSummary>(result.ToString()!);
    }

    public async Task CacheSummaryAsync(string collectionName, string evidenceHash, DocumentSummary summary, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        
        var json = JsonSerializer.Serialize(summary);
        var cacheKey = $"{collectionName}_{evidenceHash}";
        
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO summary_cache (cache_key, collection, evidence_hash, summary_json, created_at)
            VALUES ($key, $collection, $hash, $json, current_timestamp)
            ON CONFLICT (cache_key) DO UPDATE SET summary_json = excluded.summary_json, created_at = excluded.created_at
            """;
        cmd.Parameters.Add(new DuckDBParameter("key", cacheKey));
        cmd.Parameters.Add(new DuckDBParameter("collection", collectionName));
        cmd.Parameters.Add(new DuckDBParameter("hash", evidenceHash));
        cmd.Parameters.Add(new DuckDBParameter("json", json));
        
        await cmd.ExecuteNonQueryAsync(ct);
    }

    #endregion

    #region Word Lists

    public async Task LoadWordListAsync(string category, IEnumerable<string> words, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        
        await using var transaction = _connection.BeginTransaction();
        
        try
        {
            foreach (var word in words.Where(w => !string.IsNullOrWhiteSpace(w)))
            {
                await using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO word_lists (word, word_lower, category)
                    VALUES ($word, $lower, $category)
                    ON CONFLICT (word_lower, category) DO NOTHING
                    """;
                cmd.Parameters.Add(new DuckDBParameter("word", word.Trim()));
                cmd.Parameters.Add(new DuckDBParameter("lower", word.Trim().ToLowerInvariant()));
                cmd.Parameters.Add(new DuckDBParameter("category", category));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<HashSet<string>> GetWordListAsync(string category, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT word_lower FROM word_lists WHERE category = $category";
        cmd.Parameters.Add(new DuckDBParameter("category", category));
        
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            words.Add(reader.GetString(0));
        
        return words;
    }

    public async Task<bool> IsInWordListAsync(string word, string category, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM word_lists WHERE word_lower = $word AND category = $category LIMIT 1";
        cmd.Parameters.Add(new DuckDBParameter("word", word.ToLowerInvariant()));
        cmd.Parameters.Add(new DuckDBParameter("category", category));
        
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    #endregion

    #region Statistics & Maintenance

    public async Task<(int Segments, int Collections, int CachedSummaries, long DbSizeBytes)> GetStatsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT 
                (SELECT COUNT(*) FROM segments),
                (SELECT COUNT(DISTINCT collection) FROM segments),
                (SELECT COUNT(*) FROM summary_cache)
            """;
        
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var segments = reader.GetInt32(0);
            var collections = reader.GetInt32(1);
            var cached = reader.GetInt32(2);
            
            long dbSize = 0;
            if (_dbPath != ":memory:" && File.Exists(_dbPath))
                dbSize = new FileInfo(_dbPath).Length;
            
            return (segments, collections, cached, dbSize);
        }
        
        return (0, 0, 0, 0);
    }

    public async Task VacuumAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "VACUUM";
        await cmd.ExecuteNonQueryAsync(ct);
        
        if (_verbose)
            Console.WriteLine("[DuckDbVectorStore] Vacuum completed");
    }

    #endregion

    #region Helpers

    private Segment? ReadSegmentFromReader(DbDataReader reader, bool useNativeEmbedding)
    {
        try
        {
            var id = reader.GetString(0);
            var docId = reader.GetString(1);
            var contentHash = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var text = reader.GetString(3);
            var sectionTitle = reader.IsDBNull(4) ? "" : reader.GetString(4);
            var typeStr = reader.IsDBNull(5) ? "Sentence" : reader.GetString(5);
            var headingLevel = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);
            var index = reader.IsDBNull(7) ? 0 : reader.GetInt32(7);
            var salience = reader.IsDBNull(8) ? 0.0 : reader.GetFloat(8);
            
            float[]? embedding = null;
            if (!reader.IsDBNull(9))
            {
                if (useNativeEmbedding)
                {
                    // Read native FLOAT[] array
                    var value = reader.GetValue(9);
                    embedding = value switch
                    {
                        float[] arr => arr,
                        IEnumerable<float> enumerable => enumerable.ToArray(),
                        IEnumerable<double> doubles => doubles.Select(d => (float)d).ToArray(),
                        _ => null
                    };
                }
                else
                {
                    // Read JSON TEXT
                    var json = reader.GetString(9);
                    if (!string.IsNullOrEmpty(json))
                        embedding = JsonSerializer.Deserialize<float[]>(json);
                }
            }
            
            var type = Enum.TryParse<SegmentType>(typeStr, out var t) ? t : SegmentType.Sentence;
            
            // Create segment using private constructor to restore all stored values
            return new Segment(docId, text, type, index, 0, text.Length, contentHash)
            {
                SectionTitle = sectionTitle,
                HeadingLevel = headingLevel,
                SalienceScore = salience,
                Embedding = embedding
            };
        }
        catch (Exception ex)
        {
            if (_verbose)
            {
                Console.WriteLine($"[DuckDbVectorStore] ReadSegmentFromReader FAILED: {ex.Message}");
                Console.WriteLine($"[DuckDbVectorStore] Stack: {ex.StackTrace}");
            }
            return null;
        }
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;

        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom == 0 ? 0 : dot / denom;
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (!_initialized)
            await InitializeAsync("default", _vectorDimension, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _connection.Close();
        await _connection.DisposeAsync();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    #endregion
}
