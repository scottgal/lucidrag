using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;
using Mostlylucid.DataSummarizer.Configuration;
using Mostlylucid.DataSummarizer.Models;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Simple DuckDB-based vector store using the vss extension.
/// Persists profiles, summaries, and embeddings for reuse across sessions.
/// </summary>
public class VectorStoreService : IDisposable
{
    private readonly string _dbPath;
    private readonly bool _verbose;
    private readonly OnnxConfig? _onnxConfig;
    private DuckDBConnection? _conn;
    private IEmbeddingService? _embeddingService;
    private bool _available;
    private bool _useVss;
    private int _embeddingDimension = 128; // Default for hash-based

    public bool IsAvailable => _available;
    internal DuckDBConnection? Connection => _conn;
    
    /// <summary>
    /// Embedding dimension (384 for ONNX, 128 for hash-based fallback)
    /// </summary>
    public int EmbeddingDimension => _embeddingDimension;

    public VectorStoreService(string dbPath, bool verbose = false, OnnxConfig? onnxConfig = null)
    {
        _dbPath = dbPath;
        _verbose = verbose;
        _onnxConfig = onnxConfig;
    }

    public async Task InitializeAsync()
    {
        try
        {
            // Initialize embedding service first
            _embeddingService = await EmbeddingServiceFactory.GetOrCreateAsync(_onnxConfig, _verbose);
            _embeddingDimension = _embeddingService.EmbeddingDimension;
            
            if (_verbose)
            {
                Console.WriteLine($"[VectorStore] Using embeddings with dimension {_embeddingDimension}");
            }
            
            _conn = new DuckDBConnection($"Data Source={_dbPath}");
            await _conn.OpenAsync();

            // Try to install and load the VSS extension for HNSW indexes
            try
            {
                await ExecAsync("INSTALL vss; LOAD vss;");
                // Enable experimental persistence for HNSW indexes on disk-backed databases
                // This allows indexes to persist across restarts (with known WAL recovery limitations)
                await ExecAsync("SET hnsw_enable_experimental_persistence = true;");
                _useVss = true;
                if (_verbose) Console.WriteLine($"[VectorStore] VSS extension loaded with HNSW persistence enabled");
            }
            catch (Exception ex)
            {
                _useVss = false;
                if (_verbose) Console.WriteLine($"[VectorStore] VSS extension unavailable ({ex.Message}), using in-memory similarity");
            }

            // Tables - use dynamic embedding dimension based on the model
            var dim = _embeddingDimension;
            
        await ExecAsync(@"
            CREATE TABLE IF NOT EXISTS registry_files (
                file_path TEXT PRIMARY KEY,
                row_count BIGINT,
                column_count INTEGER,
                profile_json TEXT,
                content_hash TEXT,
                file_size BIGINT,
                updated_at TIMESTAMP DEFAULT NOW()
            );
        ");
        
        // Migration: add content_hash and file_size columns if they don't exist
        try { await ExecAsync("ALTER TABLE registry_files ADD COLUMN content_hash TEXT"); } catch { }
        try { await ExecAsync("ALTER TABLE registry_files ADD COLUMN file_size BIGINT"); } catch { }

        // Drop and recreate embedding tables if dimension changed (schema migration)
        // Check current dimension by querying table info
        var needsMigration = await CheckEmbeddingDimensionMismatchAsync(dim);
        if (needsMigration)
        {
            if (_verbose) Console.WriteLine($"[VectorStore] Migrating embedding tables to dimension {dim}");
            try { await ExecAsync("DROP TABLE IF EXISTS registry_embeddings"); } catch { }
            try { await ExecAsync("DROP TABLE IF EXISTS registry_conversations"); } catch { }
            try { await ExecAsync("DROP TABLE IF EXISTS registry_patterns"); } catch { }
        }

        await ExecAsync($@"
            CREATE TABLE IF NOT EXISTS registry_embeddings (
                id BIGINT PRIMARY KEY,
                file_path TEXT,
                label TEXT,
                kind TEXT,
                metadata TEXT,
                embedding FLOAT[{dim}],
                embedding_json TEXT
            );
        ");

        await ExecAsync($@"
            CREATE TABLE IF NOT EXISTS registry_conversations (
                session_id TEXT,
                turn_id BIGINT,
                role TEXT,
                content TEXT,
                embedding FLOAT[{dim}],
                embedding_json TEXT,
                created_at TIMESTAMP DEFAULT NOW(),
                PRIMARY KEY (session_id, turn_id)
            );
        ");

        await ExecAsync(@"CREATE SEQUENCE IF NOT EXISTS registry_embeddings_seq;");
        await ExecAsync(@"CREATE SEQUENCE IF NOT EXISTS registry_conversations_seq;");
        await ExecAsync(@"CREATE SEQUENCE IF NOT EXISTS registry_patterns_seq;");
        
        // Novel patterns table - stores detected patterns with their regex and examples
        await ExecAsync($@"
            CREATE TABLE IF NOT EXISTS registry_patterns (
                id BIGINT PRIMARY KEY,
                pattern_name TEXT,
                column_name TEXT,
                file_path TEXT,
                pattern_type TEXT,
                detected_regex TEXT,
                improved_regex TEXT,
                description TEXT,
                examples_json TEXT,
                match_percent DOUBLE,
                is_identifier BOOLEAN DEFAULT FALSE,
                is_sensitive BOOLEAN DEFAULT FALSE,
                validation_rules_json TEXT,
                embedding FLOAT[{dim}],
                embedding_json TEXT,
                created_at TIMESTAMP DEFAULT NOW(),
                updated_at TIMESTAMP DEFAULT NOW()
            );
        ");
        
        if (_useVss)
        {
            try
            {
                // Try to create HNSW indexes for vector similarity search
                // Note: DuckDB VSS uses "USING HNSW" syntax (not "USING vss")
                await ExecAsync(@"CREATE INDEX IF NOT EXISTS idx_registry_embeddings_hnsw ON registry_embeddings USING HNSW(embedding);");
                await ExecAsync(@"CREATE INDEX IF NOT EXISTS idx_registry_conversations_hnsw ON registry_conversations USING HNSW(embedding);");
                await ExecAsync(@"CREATE INDEX IF NOT EXISTS idx_registry_patterns_hnsw ON registry_patterns USING HNSW(embedding);");
                if (_verbose) Console.WriteLine($"[VectorStore] HNSW indexes created for vector similarity search");
            }
            catch (Exception ex)
            {
                _useVss = false;
                if (_verbose) Console.WriteLine($"[VectorStore] HNSW index unavailable, using in-memory fallback: {ex.Message}");
            }
        }

        _available = true;
    }
    catch (Exception ex)
    {
        _available = false;
        if (_verbose) Console.WriteLine($"[VectorStore] Disabled: {ex.Message}");
    }
    }
    
    /// <summary>
    /// Check if existing embedding tables have a different dimension than expected.
    /// Returns true if migration is needed.
    /// </summary>
    private async Task<bool> CheckEmbeddingDimensionMismatchAsync(int expectedDim)
    {
        try
        {
            // Check if table exists and has data
            await using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM registry_embeddings LIMIT 1";
            var count = await cmd.ExecuteScalarAsync();
            if (count == null || Convert.ToInt64(count) == 0)
                return false; // No data, no migration needed
            
            // Try to get the column type info
            await using var cmd2 = _conn.CreateCommand();
            cmd2.CommandText = "DESCRIBE registry_embeddings";
            await using var reader = await cmd2.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var colName = reader.GetString(0);
                if (colName == "embedding")
                {
                    var colType = reader.GetString(1); // e.g., "FLOAT[128]"
                    if (colType.Contains($"[{expectedDim}]"))
                        return false; // Dimension matches
                    return true; // Dimension mismatch, need migration
                }
            }
        }
        catch
        {
            // Table doesn't exist yet, no migration needed
        }
        return false;
    }

    public async Task UpsertProfileAsync(DataProfile profile, string? contentHash = null, long? fileSize = null)
    {
        Ensure();
        var json = JsonSerializer.Serialize(profile);
        var sql = "INSERT OR REPLACE INTO registry_files (file_path, row_count, column_count, profile_json, content_hash, file_size, updated_at) VALUES (?, ?, ?, ?, ?, ?, NOW())";
        await ExecAsync(sql, profile.SourcePath, profile.RowCount, profile.ColumnCount, json, contentHash, fileSize);
    }

    /// <summary>
    /// Get cached profile if file unchanged (based on content hash)
    /// </summary>
    public async Task<DataProfile?> GetCachedProfileAsync(string filePath, string currentContentHash)
    {
        if (!_available) return null;
        Ensure();
        
        var sql = "SELECT profile_json, content_hash FROM registry_files WHERE file_path = ?";
        await using var cmd = Connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = filePath });
        
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var cachedHash = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (cachedHash == currentContentHash)
            {
                var profileJson = reader.GetString(0);
                return JsonSerializer.Deserialize<DataProfile>(profileJson);
            }
        }
        
        return null;
    }

    public async Task UpsertEmbeddingsAsync(DataProfile profile)
    {
        if (!_available) return;
        Ensure();
        // Remove previous entries for this file
        await ExecAsync("DELETE FROM registry_embeddings WHERE file_path = ?", profile.SourcePath);

        // Dataset-level summary
        var summaryText = BuildDatasetSummary(profile);
        await InsertEmbeddingAsync(profile.SourcePath, "dataset_summary", "summary", "{}", await MakeVectorAsync(summaryText));

        // Columns
        foreach (var col in profile.Columns)
        {
            var meta = JsonSerializer.Serialize(new
            {
                col.InferredType,
                col.NullPercent,
                col.UniquePercent,
                col.Mean,
                col.StdDev,
                col.Min,
                col.Max,
                col.Distribution,
                col.Trend,
                col.TimeSeries
            });
            var text = BuildColumnSummary(col);
            await InsertEmbeddingAsync(profile.SourcePath, col.Name, "column", meta, await MakeVectorAsync(text));
        }

        // Insights
        foreach (var insight in profile.Insights.Take(20))
        {
            var meta = JsonSerializer.Serialize(new { insight.Title, insight.Source, insight.RelatedColumns });
            var text = $"{insight.Title}: {insight.Description}";
            await InsertEmbeddingAsync(profile.SourcePath, insight.Title, "insight", meta, await MakeVectorAsync(text));
        }
    }

    public async Task<List<RegistryHit>> SearchAsync(string query, int topK = 6)
    {
        if (!_available) return [];
        Ensure();
        var queryVec = await MakeVectorAsync(query);

        // If VSS available, use index
        if (_useVss)
        {
            var vecLiteral = VectorLiteral(queryVec);
            var sql = $@"
                SELECT file_path, label, kind, metadata, array_distance(embedding, {vecLiteral}) AS distance
                FROM registry_embeddings
                ORDER BY distance ASC
                LIMIT {topK};";

            var hits = new List<RegistryHit>();
            await using var cmd = _conn!.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                hits.Add(new RegistryHit
                {
                    FilePath = reader.GetString(0),
                    Label = reader.GetString(1),
                    Kind = reader.GetString(2),
                    Metadata = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Score = reader.IsDBNull(4) ? 1.0 : Convert.ToDouble(reader.GetValue(4))
                });
            }
            return hits;
        }

        // Fallback: brute-force cosine similarity in-process
        var fallbackHits = new List<RegistryHit>();
        var sqlAll = "SELECT file_path, label, kind, metadata, embedding_json FROM registry_embeddings";
        await using var cmdAll = _conn!.CreateCommand();
        cmdAll.CommandText = sqlAll;
        await using var readerAll = await cmdAll.ExecuteReaderAsync();
        while (await readerAll.ReadAsync())
        {
            var filePath = readerAll.GetString(0);
            var label = readerAll.GetString(1);
            var kind = readerAll.GetString(2);
            var metadata = readerAll.IsDBNull(3) ? "" : readerAll.GetString(3);
            var json = readerAll.IsDBNull(4) ? null : readerAll.GetString(4);
            if (json is null) continue;

            try
            {
                var emb = JsonSerializer.Deserialize<float[]>(json);
                if (emb == null || emb.Length == 0) continue;
                var score = CosineDistance(queryVec, emb);
                fallbackHits.Add(new RegistryHit
                {
                    FilePath = filePath,
                    Label = label,
                    Kind = kind,
                    Metadata = metadata,
                    Score = score
                });
            }
            catch { /* ignore bad rows */ }
        }

        return fallbackHits
            .OrderBy(h => h.Score)
            .Take(topK)
            .ToList();
    }

    private static double CosineDistance(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 1.0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 1.0;
        var sim = dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        // distance style: lower is better
        return 1 - sim;
    }

    private static string BuildDatasetSummary(DataProfile profile)
    {
        return $"Dataset {Path.GetFileName(profile.SourcePath)}: {profile.RowCount} rows, {profile.ColumnCount} columns. " +
               $"Numeric: {profile.Columns.Count(c => c.InferredType == ColumnType.Numeric)}, " +
               $"Categorical: {profile.Columns.Count(c => c.InferredType == ColumnType.Categorical)}, " +
               $"Date/time: {profile.Columns.Count(c => c.InferredType == ColumnType.DateTime)}.";
    }

    private static string BuildColumnSummary(ColumnProfile col)
    {
        var parts = new List<string> { $"Column {col.Name} ({col.InferredType})" };
        if (col.Mean.HasValue) parts.Add($"mean {col.Mean.Value:F2}");
        if (col.StdDev.HasValue) parts.Add($"std {col.StdDev.Value:F2}");
        if (col.Mad.HasValue) parts.Add($"mad {col.Mad.Value:F2}");
        if (col.Min.HasValue && col.Max.HasValue) parts.Add($"range {col.Min:F2}-{col.Max:F2}");
        if (col.Distribution.HasValue && col.Distribution != DistributionType.Unknown) parts.Add($"dist {col.Distribution}");
        if (col.Trend?.Direction is TrendDirection.Increasing or TrendDirection.Decreasing)
            parts.Add($"trend {col.Trend.Direction} (R2={col.Trend.RSquared:F2})");
        if (col.TimeSeries != null) parts.Add($"time series {col.TimeSeries.Granularity}");
        if (col.TextPatterns.Count > 0) parts.Add($"text {col.TextPatterns[0].PatternType}");
        return string.Join(", ", parts);
    }

    private async Task<float[]> MakeVectorAsync(string text)
    {
        if (_embeddingService == null)
            throw new InvalidOperationException("Embedding service not initialized");
        return await _embeddingService.EmbedAsync(text);
    }
    
    // Synchronous fallback for backward compatibility (uses blocking)
    private float[] MakeVector(string text)
    {
        if (_embeddingService == null)
            return EmbeddingHelper.EmbedText(text); // Fallback to static
        return _embeddingService.EmbedAsync(text).GetAwaiter().GetResult();
    }

    private static string VectorLiteral(float[] vector)
    {
        // Use array_value() to create a proper fixed-size FLOAT array for DuckDB
        var parts = vector.Select(v => v.ToString("G", CultureInfo.InvariantCulture));
        return $"[{string.Join(",", parts)}]::FLOAT[{vector.Length}]";
    }

    private async Task InsertEmbeddingAsync(string filePath, string label, string kind, string metadata, float[] embedding)
    {
        var vecLiteral = VectorLiteral(embedding);
        var json = JsonSerializer.Serialize(embedding);
        var sql = $@"INSERT INTO registry_embeddings (id, file_path, label, kind, metadata, embedding, embedding_json)
                     VALUES (nextval('registry_embeddings_seq'), ?, ?, ?, ?, {vecLiteral}, ?);";
        await ExecAsync(sql, filePath, label, kind, metadata, json);
    }

    public async Task AppendConversationTurnAsync(string sessionId, string role, string content)
    {
        if (!_available) return;
        Ensure();
        var embedding = await MakeVectorAsync(content);
        var vecLiteral = VectorLiteral(embedding);
        var json = JsonSerializer.Serialize(embedding);
        var sql = $@"INSERT INTO registry_conversations (session_id, turn_id, role, content, embedding, embedding_json)
                     VALUES (?, nextval('registry_conversations_seq'), ?, ?, {vecLiteral}, ?);";
        await ExecAsync(sql, sessionId, role, content, json);
    }

    /// <summary>
    /// Save a novel pattern to the registry for future reference and vector search
    /// </summary>
    public async Task UpsertNovelPatternAsync(NovelPatternRecord pattern)
    {
        if (!_available) return;
        Ensure();
        
        // Build searchable text for embedding
        var searchText = BuildPatternSearchText(pattern);
        var embedding = await MakeVectorAsync(searchText);
        var vecLiteral = VectorLiteral(embedding);
        var embeddingJson = JsonSerializer.Serialize(embedding);
        var examplesJson = JsonSerializer.Serialize(pattern.Examples ?? new List<string>());
        var rulesJson = JsonSerializer.Serialize(pattern.ValidationRules ?? new List<string>());
        
        // Check if pattern already exists for this column/file
        var existsSql = "SELECT id FROM registry_patterns WHERE column_name = ? AND file_path = ? LIMIT 1";
        await using var checkCmd = _conn!.CreateCommand();
        checkCmd.CommandText = existsSql;
        checkCmd.Parameters.Add(new DuckDBParameter { Value = pattern.ColumnName });
        checkCmd.Parameters.Add(new DuckDBParameter { Value = pattern.FilePath });
        var existingId = await checkCmd.ExecuteScalarAsync();
        
        if (existingId != null && existingId != DBNull.Value)
        {
            // Update existing
            var updateSql = $@"
                UPDATE registry_patterns SET
                    pattern_name = ?,
                    pattern_type = ?,
                    detected_regex = ?,
                    improved_regex = ?,
                    description = ?,
                    examples_json = ?,
                    match_percent = ?,
                    is_identifier = ?,
                    is_sensitive = ?,
                    validation_rules_json = ?,
                    embedding = {vecLiteral},
                    embedding_json = ?,
                    updated_at = NOW()
                WHERE id = ?";
            await ExecAsync(updateSql, 
                pattern.PatternName, pattern.PatternType, pattern.DetectedRegex, pattern.ImprovedRegex,
                pattern.Description, examplesJson, pattern.MatchPercent, pattern.IsIdentifier, pattern.IsSensitive,
                rulesJson, embeddingJson, existingId);
        }
        else
        {
            // Insert new
            var insertSql = $@"
                INSERT INTO registry_patterns (
                    id, pattern_name, column_name, file_path, pattern_type, detected_regex, improved_regex,
                    description, examples_json, match_percent, is_identifier, is_sensitive, validation_rules_json,
                    embedding, embedding_json
                ) VALUES (
                    nextval('registry_patterns_seq'), ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, {vecLiteral}, ?
                )";
            await ExecAsync(insertSql,
                pattern.PatternName, pattern.ColumnName, pattern.FilePath, pattern.PatternType,
                pattern.DetectedRegex, pattern.ImprovedRegex, pattern.Description, examplesJson,
                pattern.MatchPercent, pattern.IsIdentifier, pattern.IsSensitive, rulesJson, embeddingJson);
        }
        
        if (_verbose) Console.WriteLine($"[VectorStore] Saved pattern '{pattern.PatternName}' for column {pattern.ColumnName}");
    }

    /// <summary>
    /// Search for similar patterns across all stored patterns
    /// </summary>
    public async Task<List<PatternSearchHit>> SearchPatternsAsync(string query, int topK = 5)
    {
        if (!_available) return [];
        Ensure();
        var queryVec = await MakeVectorAsync(query);
        var hits = new List<PatternSearchHit>();

        if (_useVss)
        {
            var vecLiteral = VectorLiteral(queryVec);
            var sql = $@"
                SELECT id, pattern_name, column_name, file_path, pattern_type, detected_regex, improved_regex,
                       description, examples_json, match_percent, is_identifier, is_sensitive, validation_rules_json,
                       array_distance(embedding, {vecLiteral}) AS distance
                FROM registry_patterns
                ORDER BY distance ASC
                LIMIT {topK};";

            await using var cmd = _conn!.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                hits.Add(ReadPatternHit(reader));
            }
            return hits;
        }

        // Fallback: brute-force cosine similarity
        var sqlAll = @"SELECT id, pattern_name, column_name, file_path, pattern_type, detected_regex, improved_regex,
                              description, examples_json, match_percent, is_identifier, is_sensitive, validation_rules_json,
                              embedding_json FROM registry_patterns";
        await using var cmdAll = _conn!.CreateCommand();
        cmdAll.CommandText = sqlAll;
        await using var readerAll = await cmdAll.ExecuteReaderAsync();
        var temp = new List<(PatternSearchHit hit, float[] emb)>();
        
        while (await readerAll.ReadAsync())
        {
            var json = readerAll.IsDBNull(13) ? null : readerAll.GetString(13);
            if (json is null) continue;
            
            try
            {
                var emb = JsonSerializer.Deserialize<float[]>(json);
                if (emb == null || emb.Length == 0) continue;
                var hit = ReadPatternHitWithoutDistance(readerAll);
                temp.Add((hit, emb));
            }
            catch { /* ignore bad rows */ }
        }

        return temp
            .Select(t => t.hit with { Score = CosineDistance(queryVec, t.emb) })
            .OrderBy(h => h.Score)
            .Take(topK)
            .ToList();
    }

    /// <summary>
    /// Get all patterns for a specific file
    /// </summary>
    public async Task<List<PatternSearchHit>> GetPatternsForFileAsync(string filePath)
    {
        if (!_available) return [];
        Ensure();
        
        var sql = @"SELECT id, pattern_name, column_name, file_path, pattern_type, detected_regex, improved_regex,
                           description, examples_json, match_percent, is_identifier, is_sensitive, validation_rules_json, 0.0 as distance
                    FROM registry_patterns WHERE file_path = ?";
        
        var hits = new List<PatternSearchHit>();
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new DuckDBParameter { Value = filePath });
        await using var reader = await cmd.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            hits.Add(ReadPatternHit(reader));
        }
        
        return hits;
    }

    /// <summary>
    /// Find patterns similar to given examples (useful for matching new data to known patterns)
    /// </summary>
    public async Task<PatternSearchHit?> FindMatchingPatternAsync(List<string> examples, double maxDistance = 0.3)
    {
        if (!_available || examples.Count == 0) return null;
        
        // Create a search query from the examples
        var searchQuery = $"text pattern examples: {string.Join(", ", examples.Take(5))}";
        var hits = await SearchPatternsAsync(searchQuery, topK: 1);
        
        if (hits.Count > 0 && hits[0].Score <= maxDistance)
        {
            return hits[0];
        }
        
        return null;
    }

    private static string BuildPatternSearchText(NovelPatternRecord pattern)
    {
        var parts = new List<string>
        {
            $"Pattern: {pattern.PatternName}",
            $"Column: {pattern.ColumnName}",
            $"Type: {pattern.PatternType}",
            $"Description: {pattern.Description}"
        };
        
        if (pattern.Examples?.Count > 0)
        {
            parts.Add($"Examples: {string.Join(", ", pattern.Examples.Take(5))}");
        }
        
        if (!string.IsNullOrEmpty(pattern.DetectedRegex))
        {
            parts.Add($"Regex: {pattern.DetectedRegex}");
        }
        
        return string.Join(". ", parts);
    }

    private static PatternSearchHit ReadPatternHit(System.Data.Common.DbDataReader reader)
    {
        var examplesJson = reader.IsDBNull(8) ? "[]" : reader.GetString(8);
        var rulesJson = reader.IsDBNull(12) ? "[]" : reader.GetString(12);
        
        return new PatternSearchHit
        {
            Id = reader.GetInt64(0),
            PatternName = reader.IsDBNull(1) ? "" : reader.GetString(1),
            ColumnName = reader.IsDBNull(2) ? "" : reader.GetString(2),
            FilePath = reader.IsDBNull(3) ? "" : reader.GetString(3),
            PatternType = reader.IsDBNull(4) ? "" : reader.GetString(4),
            DetectedRegex = reader.IsDBNull(5) ? null : reader.GetString(5),
            ImprovedRegex = reader.IsDBNull(6) ? null : reader.GetString(6),
            Description = reader.IsDBNull(7) ? "" : reader.GetString(7),
            Examples = JsonSerializer.Deserialize<List<string>>(examplesJson) ?? [],
            MatchPercent = reader.IsDBNull(9) ? 0 : Convert.ToDouble(reader.GetValue(9)),
            IsIdentifier = !reader.IsDBNull(10) && reader.GetBoolean(10),
            IsSensitive = !reader.IsDBNull(11) && reader.GetBoolean(11),
            ValidationRules = JsonSerializer.Deserialize<List<string>>(rulesJson) ?? [],
            Score = reader.IsDBNull(13) ? 0 : Convert.ToDouble(reader.GetValue(13))
        };
    }

    private static PatternSearchHit ReadPatternHitWithoutDistance(System.Data.Common.DbDataReader reader)
    {
        var examplesJson = reader.IsDBNull(8) ? "[]" : reader.GetString(8);
        var rulesJson = reader.IsDBNull(12) ? "[]" : reader.GetString(12);
        
        return new PatternSearchHit
        {
            Id = reader.GetInt64(0),
            PatternName = reader.IsDBNull(1) ? "" : reader.GetString(1),
            ColumnName = reader.IsDBNull(2) ? "" : reader.GetString(2),
            FilePath = reader.IsDBNull(3) ? "" : reader.GetString(3),
            PatternType = reader.IsDBNull(4) ? "" : reader.GetString(4),
            DetectedRegex = reader.IsDBNull(5) ? null : reader.GetString(5),
            ImprovedRegex = reader.IsDBNull(6) ? null : reader.GetString(6),
            Description = reader.IsDBNull(7) ? "" : reader.GetString(7),
            Examples = JsonSerializer.Deserialize<List<string>>(examplesJson) ?? [],
            MatchPercent = reader.IsDBNull(9) ? 0 : Convert.ToDouble(reader.GetValue(9)),
            IsIdentifier = !reader.IsDBNull(10) && reader.GetBoolean(10),
            IsSensitive = !reader.IsDBNull(11) && reader.GetBoolean(11),
            ValidationRules = JsonSerializer.Deserialize<List<string>>(rulesJson) ?? [],
            Score = 0
        };
    }

    public async Task<List<ConversationTurn>> GetConversationContextAsync(string sessionId, string query, int topK = 5)
    {
        var result = new List<ConversationTurn>();
        if (!_available) return result;
        Ensure();
        var queryVec = await MakeVectorAsync(query);

        if (_useVss)
        {
            var vecLiteral = VectorLiteral(queryVec);
            var sql = $@"SELECT role, content, array_distance(embedding, {vecLiteral}) as distance
                         FROM registry_conversations
                         WHERE session_id = ?
                         ORDER BY distance ASC, created_at DESC
                         LIMIT {topK};";
            await using var cmd = _conn!.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.Add(new DuckDBParameter { Value = sessionId });
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new ConversationTurn
                {
                    Role = reader.GetString(0),
                    Content = reader.GetString(1)
                });
            }
            return result;
        }

        // Fallback cosine within session
        var sqlAll = "SELECT role, content, embedding_json, created_at FROM registry_conversations WHERE session_id = ?";
        await using var cmdAll = _conn!.CreateCommand();
        cmdAll.CommandText = sqlAll;
        cmdAll.Parameters.Add(new DuckDBParameter { Value = sessionId });
        await using var readerAll = await cmdAll.ExecuteReaderAsync();
        var temp = new List<(string role, string content, float[] emb, DateTime created)>();
        while (await readerAll.ReadAsync())
        {
            var role = readerAll.GetString(0);
            var content = readerAll.GetString(1);
            var json = readerAll.IsDBNull(2) ? null : readerAll.GetString(2);
            var created = readerAll.IsDBNull(3) ? DateTime.UtcNow : readerAll.GetDateTime(3);
            if (json is null) continue;
            var emb = JsonSerializer.Deserialize<float[]>(json);
            if (emb == null || emb.Length == 0) continue;
            temp.Add((role, content, emb, created));
        }
        result = temp
            .Select(t => new { t.role, t.content, score = CosineDistance(queryVec, t.emb), t.created })
            .OrderBy(x => x.score)
            .ThenByDescending(x => x.created)
            .Take(topK)
            .Select(x => new ConversationTurn { Role = x.role, Content = x.content })
            .ToList();
        return result;
    }

    private async Task ExecAsync(string sql, params object?[] args)
    {
        if (_conn is null) throw new InvalidOperationException("Vector store connection not initialized");
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++)
        {
            cmd.Parameters.Add(new DuckDBParameter { Value = args[i] ?? DBNull.Value });
        }
        await cmd.ExecuteNonQueryAsync();
    }

    private void Ensure()
    {
        if (_conn == null) throw new InvalidOperationException("Vector store not initialized");
    }

    public void Dispose()
    {
        _conn?.Dispose();
        _conn = null;
    }
}

public record RegistryHit
{
    public string FilePath { get; init; } = "";
    public string Label { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Metadata { get; init; } = "";
    public double Score { get; init; }
}

public record ConversationTurn
{
    public string Role { get; init; } = "";
    public string Content { get; init; } = "";
}

/// <summary>
/// Record for storing a novel pattern in the registry
/// </summary>
public record NovelPatternRecord
{
    public string PatternName { get; init; } = "";
    public string ColumnName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string PatternType { get; init; } = "Novel";
    public string? DetectedRegex { get; init; }
    public string? ImprovedRegex { get; init; }
    public string Description { get; init; } = "";
    public List<string>? Examples { get; init; }
    public double MatchPercent { get; init; }
    public bool IsIdentifier { get; init; }
    public bool IsSensitive { get; init; }
    public List<string>? ValidationRules { get; init; }
}

/// <summary>
/// Search result for pattern queries
/// </summary>
public record PatternSearchHit
{
    public long Id { get; init; }
    public string PatternName { get; init; } = "";
    public string ColumnName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string PatternType { get; init; } = "";
    public string? DetectedRegex { get; init; }
    public string? ImprovedRegex { get; init; }
    public string Description { get; init; } = "";
    public List<string> Examples { get; init; } = [];
    public double MatchPercent { get; init; }
    public bool IsIdentifier { get; init; }
    public bool IsSensitive { get; init; }
    public List<string> ValidationRules { get; init; } = [];
    public double Score { get; init; }
}
