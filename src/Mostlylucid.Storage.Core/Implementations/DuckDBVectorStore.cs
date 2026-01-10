using System.Data;
using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.Storage.Core.Abstractions;
using Mostlylucid.Storage.Core.Abstractions.Models;
using Mostlylucid.Storage.Core.Config;

namespace Mostlylucid.Storage.Core.Implementations;

/// <summary>
/// DuckDB-based vector store with VSS extension support for HNSW indexes.
/// Falls back to in-memory cosine similarity if VSS unavailable.
/// </summary>
public class DuckDBVectorStore : IVectorStore
{
    private readonly ILogger<DuckDBVectorStore> _logger;
    private readonly DuckDBOptions _options;
    private readonly string _dbPath;
    private DuckDBConnection? _connection;
    private bool _useVss;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly Dictionary<string, CollectionMetadata> _collections = new();
    private bool _disposed;

    public bool IsPersistent => _options.EnablePersistence;
    public VectorStoreBackend Backend => VectorStoreBackend.DuckDB;

    public DuckDBVectorStore(IOptions<VectorStoreOptions> options, ILogger<DuckDBVectorStore> logger)
    {
        _logger = logger;
        _options = options.Value.DuckDB;
        _dbPath = _options.DatabasePath;

        // Ensure directory exists
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private async Task EnsureConnectionAsync(CancellationToken ct = default)
    {
        if (_connection != null) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_connection != null) return;

            _connection = new DuckDBConnection($"Data Source={_dbPath}");
            await _connection.OpenAsync(ct);

            // Try to load VSS extension
            try
            {
                await ExecAsync("INSTALL vss;", ct);
                await ExecAsync("LOAD vss;", ct);

                if (_options.EnablePersistence)
                {
                    await ExecAsync("SET hnsw_enable_experimental_persistence = true;", ct);
                }

                _useVss = true;
                _logger.LogInformation("DuckDB VSS extension loaded successfully (HNSW indexes enabled)");
            }
            catch (Exception ex)
            {
                _useVss = false;
                _logger.LogWarning(ex, "VSS extension unavailable - falling back to in-memory cosine similarity");
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ========== Collection Management ==========

    public async Task InitializeAsync(string collectionName, VectorStoreSchema schema, CancellationToken ct = default)
    {
        await EnsureConnectionAsync(ct);

        // Check if collection exists
        var exists = await CollectionExistsAsync(collectionName, ct);
        if (exists)
        {
            // Validate schema matches
            var existing = _collections[collectionName];
            if (existing.VectorDimension != schema.VectorDimension)
            {
                _logger.LogWarning(
                    "Collection {Collection} dimension mismatch: existing={Existing}, requested={Requested}. Dropping and recreating.",
                    collectionName, existing.VectorDimension, schema.VectorDimension);

                await DeleteCollectionAsync(collectionName, ct);
            }
            else
            {
                _logger.LogInformation("Collection {Collection} already exists with matching schema", collectionName);
                return;
            }
        }

        // Create tables
        var dim = schema.VectorDimension;
        var tableName = GetTableName(collectionName);

        await ExecAsync($@"
            CREATE TABLE IF NOT EXISTS {tableName} (
                id TEXT PRIMARY KEY,
                parent_id TEXT,
                content_hash TEXT,
                text TEXT,
                embedding FLOAT[{dim}],
                embedding_json TEXT,
                metadata TEXT,
                created_at TIMESTAMP,
                updated_at TIMESTAMP
            );
        ", ct);

        // Create indexes
        await ExecAsync($"CREATE INDEX IF NOT EXISTS idx_{tableName}_parent ON {tableName}(parent_id);", ct);
        await ExecAsync($"CREATE INDEX IF NOT EXISTS idx_{tableName}_hash ON {tableName}(content_hash);", ct);

        if (_useVss)
        {
            try
            {
                await ExecAsync($@"
                    CREATE INDEX IF NOT EXISTS idx_{tableName}_hnsw
                    ON {tableName} USING HNSW(embedding)
                    WITH (M = {_options.HNSW.M}, ef_construction = {_options.HNSW.EfConstruction});
                ", ct);

                _logger.LogInformation("Created HNSW index for collection {Collection}", collectionName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create HNSW index for {Collection}", collectionName);
            }
        }

        // Create summary cache table
        var summaryTable = GetSummaryTableName(collectionName);
        await ExecAsync($@"
            CREATE TABLE IF NOT EXISTS {summaryTable} (
                document_id TEXT PRIMARY KEY,
                summary TEXT,
                model TEXT,
                metadata TEXT,
                cached_at TIMESTAMP
            );
        ", ct);

        _collections[collectionName] = new CollectionMetadata
        {
            Name = collectionName,
            VectorDimension = dim,
            DistanceMetric = schema.DistanceMetric,
            StoreText = schema.StoreText
        };

        _logger.LogInformation("Initialized collection {Collection} (dim={Dim}, VSS={VSS})",
            collectionName, dim, _useVss);
    }

    public async Task<bool> CollectionExistsAsync(string collectionName, CancellationToken ct = default)
    {
        if (_collections.ContainsKey(collectionName))
            return true;

        await EnsureConnectionAsync(ct);

        var tableName = GetTableName(collectionName);
        var sql = $@"
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_name = '{tableName}';
        ";

        var count = await QueryScalarAsync<long>(sql, ct);
        if (count > 0)
        {
            // Load metadata from existing table
            var dim = await GetVectorDimensionAsync(tableName, ct);
            _collections[collectionName] = new CollectionMetadata
            {
                Name = collectionName,
                VectorDimension = dim,
                DistanceMetric = VectorDistance.Cosine,
                StoreText = true
            };
            return true;
        }

        return false;
    }

    public async Task DeleteCollectionAsync(string collectionName, CancellationToken ct = default)
    {
        await EnsureConnectionAsync(ct);

        var tableName = GetTableName(collectionName);
        var summaryTable = GetSummaryTableName(collectionName);

        await ExecAsync($"DROP TABLE IF EXISTS {tableName};", ct);
        await ExecAsync($"DROP TABLE IF EXISTS {summaryTable};", ct);

        _collections.Remove(collectionName);

        _logger.LogInformation("Deleted collection {Collection}", collectionName);
    }

    public async Task<CollectionStats> GetCollectionStatsAsync(string collectionName, CancellationToken ct = default)
    {
        await EnsureConnectionAsync(ct);

        var tableName = GetTableName(collectionName);
        var count = await QueryScalarAsync<long>($"SELECT COUNT(*) FROM {tableName};", ct);

        var metadata = _collections.GetValueOrDefault(collectionName);

        return new CollectionStats
        {
            CollectionName = collectionName,
            DocumentCount = count,
            VectorDimension = metadata?.VectorDimension ?? 0,
            SizeBytes = null  // DuckDB doesn't expose this easily
        };
    }

    // ========== Document Operations ==========

    public async Task<bool> HasDocumentAsync(string collectionName, string documentId, CancellationToken ct = default)
    {
        await EnsureConnectionAsync(ct);

        var tableName = GetTableName(collectionName);
        var sql = $"SELECT COUNT(*) FROM {tableName} WHERE id = @id;";

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        AddParameter(cmd, "@id", documentId);

        var count = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        return count > 0;
    }

    public async Task UpsertDocumentsAsync(string collectionName, IEnumerable<VectorDocument> documents, CancellationToken ct = default)
    {
        await EnsureConnectionAsync(ct);

        var tableName = GetTableName(collectionName);
        var metadata = _collections[collectionName];

        foreach (var doc in documents)
        {
            if (doc.Embedding.Length != metadata.VectorDimension)
            {
                throw new ArgumentException(
                    $"Document {doc.Id} embedding dimension {doc.Embedding.Length} does not match collection dimension {metadata.VectorDimension}");
            }

            var vecLiteral = VectorLiteral(doc.Embedding);
            var embeddingJson = JsonSerializer.Serialize(doc.Embedding);
            var metadataJson = JsonSerializer.Serialize(doc.Metadata);

            var sql = $@"
                INSERT OR REPLACE INTO {tableName}
                (id, parent_id, content_hash, text, embedding, embedding_json, metadata, created_at, updated_at)
                VALUES (@id, @parent_id, @hash, @text, {vecLiteral}, @embedding_json, @metadata, @created_at, @updated_at);
            ";

            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = sql;
            AddParameter(cmd, "@id", doc.Id);
            AddParameter(cmd, "@parent_id", doc.ParentId);
            AddParameter(cmd, "@hash", doc.ContentHash);
            AddParameter(cmd, "@text", metadata.StoreText ? doc.Text : null);
            AddParameter(cmd, "@embedding_json", embeddingJson);
            AddParameter(cmd, "@metadata", metadataJson);
            AddParameter(cmd, "@created_at", doc.CreatedAt);
            AddParameter(cmd, "@updated_at", doc.UpdatedAt ?? DateTimeOffset.UtcNow);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        _logger.LogDebug("Upserted {Count} documents to collection {Collection}",
            documents.Count(), collectionName);
    }

    public async Task DeleteDocumentAsync(string collectionName, string documentId, CancellationToken ct = default)
    {
        await EnsureConnectionAsync(ct);

        var tableName = GetTableName(collectionName);
        var sql = $"DELETE FROM {tableName} WHERE id = @id;";

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        AddParameter(cmd, "@id", documentId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<VectorDocument?> GetDocumentAsync(string collectionName, string documentId, CancellationToken ct = default)
    {
        await EnsureConnectionAsync(ct);

        var tableName = GetTableName(collectionName);
        var sql = $@"
            SELECT id, parent_id, content_hash, text, embedding_json, metadata, created_at, updated_at
            FROM {tableName}
            WHERE id = @id;
        ";

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        AddParameter(cmd, "@id", documentId);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ParseDocumentRow(reader);
        }

        return null;
    }

    public async Task<List<VectorDocument>> GetAllDocumentsAsync(string collectionName, string? parentId = null, CancellationToken ct = default)
    {
        await EnsureConnectionAsync(ct);

        var tableName = GetTableName(collectionName);
        var sql = parentId == null
            ? $"SELECT id, parent_id, content_hash, text, embedding_json, metadata, created_at, updated_at FROM {tableName};"
            : $"SELECT id, parent_id, content_hash, text, embedding_json, metadata, created_at, updated_at FROM {tableName} WHERE parent_id = @parent_id;";

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        if (parentId != null)
        {
            AddParameter(cmd, "@parent_id", parentId);
        }

        var results = new List<VectorDocument>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ParseDocumentRow(reader));
        }

        return results;
    }

    // ========== Search Operations ==========

    public async Task<List<VectorSearchResult>> SearchAsync(string collectionName, VectorSearchQuery query, CancellationToken ct = default)
    {
        await EnsureConnectionAsync(ct);

        var tableName = GetTableName(collectionName);
        var metadata = _collections[collectionName];

        if (query.QueryEmbedding.Length != metadata.VectorDimension)
        {
            throw new ArgumentException(
                $"Query embedding dimension {query.QueryEmbedding.Length} does not match collection dimension {metadata.VectorDimension}");
        }

        List<VectorSearchResult> results;

        if (_useVss)
        {
            // Use VSS extension
            results = await SearchWithVssAsync(tableName, query, ct);
        }
        else
        {
            // Fallback to in-memory cosine similarity
            results = await SearchWithCosineAsync(tableName, query, ct);
        }

        // Apply filters
        if (query.MinScore > 0)
        {
            results = results.Where(r => r.Score >= query.MinScore).ToList();
        }

        if (query.MaxScore.HasValue)
        {
            results = results.Where(r => r.Score <= query.MaxScore.Value).ToList();
        }

        return results.Take(query.TopK).ToList();
    }

    public async Task<List<VectorSearchResult>> FindSimilarAsync(string collectionName, string documentId, int topK = 10, CancellationToken ct = default)
    {
        var doc = await GetDocumentAsync(collectionName, documentId, ct);
        if (doc == null)
        {
            return new List<VectorSearchResult>();
        }

        var query = new VectorSearchQuery
        {
            QueryEmbedding = doc.Embedding,
            TopK = topK + 1,  // +1 to exclude self
            IncludeDocument = false
        };

        var results = await SearchAsync(collectionName, query, ct);

        // Remove the document itself from results
        return results.Where(r => r.Id != documentId).Take(topK).ToList();
    }

    // ========== Content Hash-Based Caching ==========

    public async Task<Dictionary<string, VectorDocument>> GetDocumentsByHashAsync(string collectionName, IEnumerable<string> contentHashes, CancellationToken ct = default)
    {
        await EnsureConnectionAsync(ct);

        var tableName = GetTableName(collectionName);
        var hashes = contentHashes.ToList();
        var results = new Dictionary<string, VectorDocument>();

        if (!hashes.Any()) return results;

        var placeholders = string.Join(",", hashes.Select((_, i) => $"@hash{i}"));
        var sql = $@"
            SELECT id, parent_id, content_hash, text, embedding_json, metadata, created_at, updated_at
            FROM {tableName}
            WHERE content_hash IN ({placeholders});
        ";

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < hashes.Count; i++)
        {
            AddParameter(cmd, $"@hash{i}", hashes[i]);
        }

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var doc = ParseDocumentRow(reader);
            if (doc.ContentHash != null)
            {
                results[doc.ContentHash] = doc;
            }
        }

        return results;
    }

    public async Task RemoveStaleDocumentsAsync(string collectionName, string parentId, IEnumerable<string> validHashes, CancellationToken ct = default)
    {
        await EnsureConnectionAsync(ct);

        var tableName = GetTableName(collectionName);
        var hashes = validHashes.ToList();

        if (!hashes.Any())
        {
            // Remove all documents for this parent
            var sql = $"DELETE FROM {tableName} WHERE parent_id = @parent_id;";
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = sql;
            AddParameter(cmd, "@parent_id", parentId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        else
        {
            // Remove documents that don't match valid hashes
            var placeholders = string.Join(",", hashes.Select((_, i) => $"@hash{i}"));
            var sql = $@"
                DELETE FROM {tableName}
                WHERE parent_id = @parent_id
                AND content_hash NOT IN ({placeholders});
            ";

            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = sql;
            AddParameter(cmd, "@parent_id", parentId);
            for (int i = 0; i < hashes.Count; i++)
            {
                AddParameter(cmd, $"@hash{i}", hashes[i]);
            }

            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    // ========== Summary Caching ==========

    public async Task<CachedSummary?> GetCachedSummaryAsync(string collectionName, string documentId, CancellationToken ct = default)
    {
        await EnsureConnectionAsync(ct);

        var summaryTable = GetSummaryTableName(collectionName);
        var sql = $@"
            SELECT document_id, summary, model, metadata, cached_at
            FROM {summaryTable}
            WHERE document_id = @id;
        ";

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        AddParameter(cmd, "@id", documentId);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var metadataJson = reader.GetStringOrNull(3);
            var metadata = string.IsNullOrEmpty(metadataJson)
                ? new Dictionary<string, object>()
                : JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson) ?? new Dictionary<string, object>();

            return new CachedSummary
            {
                DocumentId = reader.GetString(0),
                Summary = reader.GetString(1),
                Model = reader.GetStringOrNull(2),
                Metadata = metadata,
                CachedAt = DateTimeOffset.Parse(reader.GetString(4))
            };
        }

        return null;
    }

    public async Task CacheSummaryAsync(string collectionName, CachedSummary summary, CancellationToken ct = default)
    {
        await EnsureConnectionAsync(ct);

        var summaryTable = GetSummaryTableName(collectionName);
        var metadataJson = JsonSerializer.Serialize(summary.Metadata);

        var sql = $@"
            INSERT OR REPLACE INTO {summaryTable}
            (document_id, summary, model, metadata, cached_at)
            VALUES (@id, @summary, @model, @metadata, @cached_at);
        ";

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        AddParameter(cmd, "@id", summary.DocumentId);
        AddParameter(cmd, "@summary", summary.Summary);
        AddParameter(cmd, "@model", summary.Model);
        AddParameter(cmd, "@metadata", metadataJson);
        AddParameter(cmd, "@cached_at", summary.CachedAt);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ========== Private Helpers ==========

    private async Task<List<VectorSearchResult>> SearchWithVssAsync(string tableName, VectorSearchQuery query, CancellationToken ct)
    {
        var vecLiteral = VectorLiteral(query.QueryEmbedding);
        var sql = $@"
            SELECT id, parent_id, content_hash, text, embedding_json, metadata, created_at, updated_at,
                   array_distance(embedding, {vecLiteral}) AS distance
            FROM {tableName}
            {BuildFilterClause(query)}
            ORDER BY distance ASC
            LIMIT {query.TopK * 2};
        ";

        var results = new List<VectorSearchResult>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var doc = query.IncludeDocument ? ParseDocumentRow(reader) : null;
            var distance = reader.GetDouble(8);
            var score = 1.0 - distance;  // Convert distance to similarity score

            results.Add(new VectorSearchResult
            {
                Id = reader.GetString(0),
                Score = score,
                Distance = distance,
                Document = doc,
                Metadata = doc?.Metadata ?? new Dictionary<string, object>(),
                Text = query.IncludeDocument ? reader.GetStringOrNull(3) : null,
                ParentId = reader.GetStringOrNull(1)
            });
        }

        return results;
    }

    private async Task<List<VectorSearchResult>> SearchWithCosineAsync(string tableName, VectorSearchQuery query, CancellationToken ct)
    {
        // Load all documents and compute cosine similarity in-memory
        var sql = $@"
            SELECT id, parent_id, content_hash, text, embedding_json, metadata, created_at, updated_at
            FROM {tableName}
            {BuildFilterClause(query)};
        ";

        var results = new List<VectorSearchResult>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var doc = ParseDocumentRow(reader);
            var distance = CosineDistance(query.QueryEmbedding, doc.Embedding);
            var score = 1.0 - distance;

            results.Add(new VectorSearchResult
            {
                Id = doc.Id,
                Score = score,
                Distance = distance,
                Document = query.IncludeDocument ? doc : null,
                Metadata = doc.Metadata,
                Text = query.IncludeDocument ? doc.Text : null,
                ParentId = doc.ParentId
            });
        }

        return results.OrderByDescending(r => r.Score).ToList();
    }

    private static string BuildFilterClause(VectorSearchQuery query)
    {
        var conditions = new List<string>();

        if (query.ParentId != null)
        {
            conditions.Add($"parent_id = '{query.ParentId}'");
        }

        // TODO: Add support for metadata filters

        return conditions.Any() ? $"WHERE {string.Join(" AND ", conditions)}" : "";
    }

    private static double CosineDistance(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 1.0;

        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0) return 1.0;

        var similarity = dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
        return 1.0 - similarity;  // Convert similarity to distance
    }

    private static string VectorLiteral(float[] vector)
    {
        var parts = vector.Select(v => v.ToString("G", CultureInfo.InvariantCulture));
        return $"[{string.Join(",", parts)}]::FLOAT[{vector.Length}]";
    }

    private VectorDocument ParseDocumentRow(IDataReader reader)
    {
        var embeddingJson = reader.GetString(4);
        var embedding = JsonSerializer.Deserialize<float[]>(embeddingJson) ?? Array.Empty<float>();

        var metadataJson = reader.GetStringOrNull(5);
        var metadata = string.IsNullOrEmpty(metadataJson)
            ? new Dictionary<string, object>()
            : JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson) ?? new Dictionary<string, object>();

        // Parse timestamps as strings (DuckDB returns them as strings)
        var createdAtStr = reader.GetString(6);
        var updatedAtStr = reader.GetStringOrNull(7);

        return new VectorDocument
        {
            Id = reader.GetString(0),
            ParentId = reader.GetStringOrNull(1),
            ContentHash = reader.GetStringOrNull(2),
            Text = reader.GetStringOrNull(3),
            Embedding = embedding,
            Metadata = metadata,
            CreatedAt = DateTimeOffset.Parse(createdAtStr),
            UpdatedAt = string.IsNullOrEmpty(updatedAtStr) ? null : DateTimeOffset.Parse(updatedAtStr)
        };
    }

    private async Task<int> GetVectorDimensionAsync(string tableName, CancellationToken ct)
    {
        // Get dimension from first row
        var sql = $"SELECT embedding_json FROM {tableName} LIMIT 1;";
        var json = await QueryScalarAsync<string>(sql, ct);

        if (string.IsNullOrEmpty(json))
        {
            return _options.VectorDimension;
        }

        var embedding = JsonSerializer.Deserialize<float[]>(json);
        return embedding?.Length ?? _options.VectorDimension;
    }

    private async Task ExecAsync(string sql, CancellationToken ct = default)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<T> QueryScalarAsync<T>(string sql, CancellationToken ct = default)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync(ct);
        return result == null || result == DBNull.Value ? default! : (T)result;
    }

    private static void AddParameter(DuckDBCommand cmd, string name, object? value)
    {
        var param = cmd.CreateParameter();
        param.ParameterName = name;
        param.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(param);
    }

    private static string GetTableName(string collectionName) => $"vec_{collectionName}";
    private static string GetSummaryTableName(string collectionName) => $"summary_{collectionName}";

    public void Dispose()
    {
        if (_disposed) return;

        _connection?.Dispose();
        _initLock.Dispose();
        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }

        _initLock.Dispose();
        _disposed = true;
    }

    private class CollectionMetadata
    {
        public required string Name { get; init; }
        public required int VectorDimension { get; init; }
        public required VectorDistance DistanceMetric { get; init; }
        public required bool StoreText { get; init; }
    }
}

// Extension methods for IDataReader
internal static class DataReaderExtensions
{
    public static string? GetStringOrNull(this IDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
