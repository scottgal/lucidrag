using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.Storage.Core.Abstractions;
using Mostlylucid.Storage.Core.Abstractions.Models;
using Mostlylucid.Storage.Core.Config;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Mostlylucid.Storage.Core.Implementations;

/// <summary>
/// Qdrant-backed vector store for production deployments.
/// Provides persistent storage, distributed support, and advanced filtering.
/// Requires Qdrant server to be running.
/// </summary>
public class QdrantVectorStore : IVectorStore
{
    private readonly QdrantClient _client;
    private readonly ILogger<QdrantVectorStore> _logger;
    private readonly QdrantOptions _options;
    private readonly Dictionary<string, CollectionMetadata> _collections = new();
    private bool _disposed;

    public bool IsPersistent => true;
    public VectorStoreBackend Backend => VectorStoreBackend.Qdrant;

    public QdrantVectorStore(IOptions<VectorStoreOptions> options, ILogger<QdrantVectorStore> logger)
    {
        _logger = logger;
        _options = options.Value.Qdrant;

        _client = _options.UseHttps
            ? new QdrantClient($"https://{_options.Host}:{_options.Port}", apiKey: _options.ApiKey)
            : new QdrantClient(_options.Host, _options.Port, apiKey: _options.ApiKey);
    }

    // ========== Collection Management ==========

    public async Task InitializeAsync(string collectionName, VectorStoreSchema schema, CancellationToken ct = default)
    {
        try
        {
            var collections = await _client.ListCollectionsAsync(ct);
            var exists = collections.Any(c => c == collectionName);

            if (!exists)
            {
                await _client.CreateCollectionAsync(
                    collectionName,
                    new VectorParams
                    {
                        Size = (ulong)schema.VectorDimension,
                        Distance = schema.DistanceMetric switch
                        {
                            VectorDistance.Cosine => Distance.Cosine,
                            VectorDistance.Euclidean => Distance.Euclid,
                            VectorDistance.DotProduct => Distance.Dot,
                            _ => Distance.Cosine
                        }
                    },
                    cancellationToken: ct);

                _logger.LogInformation("Created Qdrant collection {Collection} (dim={Dim}, distance={Distance})",
                    collectionName, schema.VectorDimension, schema.DistanceMetric);
            }
            else
            {
                _logger.LogDebug("Using existing Qdrant collection {Collection}", collectionName);
            }

            _collections[collectionName] = new CollectionMetadata
            {
                Name = collectionName,
                VectorDimension = schema.VectorDimension,
                DistanceMetric = schema.DistanceMetric,
                StoreText = schema.StoreText
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to initialize Qdrant collection '{collectionName}': {ex.Message}", ex);
        }
    }

    public async Task<bool> CollectionExistsAsync(string collectionName, CancellationToken ct = default)
    {
        try
        {
            var collections = await _client.ListCollectionsAsync(ct);
            return collections.Any(c => c == collectionName);
        }
        catch
        {
            return false;
        }
    }

    public async Task DeleteCollectionAsync(string collectionName, CancellationToken ct = default)
    {
        try
        {
            await _client.DeleteCollectionAsync(collectionName, cancellationToken: ct);
            _collections.Remove(collectionName);

            _logger.LogInformation("Deleted Qdrant collection {Collection}", collectionName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete Qdrant collection {Collection}", collectionName);
        }
    }

    public async Task<CollectionStats> GetCollectionStatsAsync(string collectionName, CancellationToken ct = default)
    {
        try
        {
            var info = await _client.GetCollectionInfoAsync(collectionName, cancellationToken: ct);

            return new CollectionStats
            {
                CollectionName = collectionName,
                DocumentCount = (long)info.PointsCount,
                VectorDimension = _collections.GetValueOrDefault(collectionName)?.VectorDimension ?? 0,
                SizeBytes = null  // Qdrant doesn't expose this directly
            };
        }
        catch
        {
            return new CollectionStats
            {
                CollectionName = collectionName,
                DocumentCount = 0,
                VectorDimension = 0,
                SizeBytes = null
            };
        }
    }

    // ========== Document Operations ==========

    public async Task<bool> HasDocumentAsync(string collectionName, string documentId, CancellationToken ct = default)
    {
        try
        {
            var collections = await _client.ListCollectionsAsync(ct);
            if (!collections.Any(c => c == collectionName))
                return false;

            var scrollResult = await _client.ScrollAsync(
                collectionName,
                filter: new Filter
                {
                    Must =
                    {
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key = "id",
                                Match = new Match { Keyword = documentId }
                            }
                        }
                    }
                },
                limit: 1,
                cancellationToken: ct);

            return scrollResult.Result.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task UpsertDocumentsAsync(string collectionName, IEnumerable<VectorDocument> documents, CancellationToken ct = default)
    {
        var docList = documents.ToList();

        if (docList.Count == 0)
            return;

        var metadata = _collections.GetValueOrDefault(collectionName);
        if (metadata == null)
        {
            throw new InvalidOperationException($"Collection '{collectionName}' not initialized. Call InitializeAsync first.");
        }

        // Validate dimensions
        foreach (var doc in docList)
        {
            if (doc.Embedding.Length != metadata.VectorDimension)
            {
                throw new ArgumentException(
                    $"Document {doc.Id} embedding dimension {doc.Embedding.Length} does not match collection dimension {metadata.VectorDimension}");
            }
        }

        // Convert documents to Qdrant points
        var points = docList.Select(d =>
        {
            var point = new PointStruct
            {
                Id = new PointId { Uuid = GenerateUuidFromId(d.ContentHash ?? d.Id) },
                Vectors = d.Embedding,
                Payload =
                {
                    ["id"] = d.Id,
                    ["parentId"] = d.ParentId ?? "",
                    ["contentHash"] = d.ContentHash ?? "",
                    ["createdAt"] = d.CreatedAt.ToString("O"),
                    ["updatedAt"] = (d.UpdatedAt ?? DateTimeOffset.UtcNow).ToString("O")
                }
            };

            // Store text if configured
            if (metadata.StoreText && d.Text != null)
            {
                point.Payload["text"] = d.Text;
            }

            // Store metadata as JSON
            if (d.Metadata.Count > 0)
            {
                point.Payload["metadata"] = JsonSerializer.Serialize(d.Metadata);
            }

            return point;
        }).ToList();

        // Upsert in batches
        const int batchSize = 100;
        for (int i = 0; i < points.Count; i += batchSize)
        {
            var batch = points.Skip(i).Take(batchSize).ToList();
            await _client.UpsertAsync(collectionName, batch, cancellationToken: ct);

            if (points.Count > batchSize)
            {
                _logger.LogDebug("Upserted batch {Current}/{Total} to Qdrant collection {Collection}",
                    i / batchSize + 1, (points.Count + batchSize - 1) / batchSize, collectionName);
            }
        }

        _logger.LogDebug("Upserted {Count} documents to Qdrant collection {Collection}",
            docList.Count, collectionName);
    }

    public async Task DeleteDocumentAsync(string collectionName, string documentId, CancellationToken ct = default)
    {
        await _client.DeleteAsync(
            collectionName,
            new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "id",
                            Match = new Match { Keyword = documentId }
                        }
                    }
                }
            },
            cancellationToken: ct);
    }

    public async Task<VectorDocument?> GetDocumentAsync(string collectionName, string documentId, CancellationToken ct = default)
    {
        var scrollResult = await _client.ScrollAsync(
            collectionName,
            filter: new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "id",
                            Match = new Match { Keyword = documentId }
                        }
                    }
                }
            },
            limit: 1,
            payloadSelector: true,
            vectorsSelector: true,
            cancellationToken: ct);

        if (scrollResult.Result.Count == 0)
            return null;

        var point = scrollResult.Result[0];
        return PayloadToDocument(point.Payload, ExtractVectorData(point.Vectors));
    }

    public async Task<List<VectorDocument>> GetAllDocumentsAsync(string collectionName, string? parentId = null, CancellationToken ct = default)
    {
        var documents = new List<VectorDocument>();
        PointId? offset = null;

        Filter? filter = null;
        if (parentId != null)
        {
            filter = new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "parentId",
                            Match = new Match { Keyword = parentId }
                        }
                    }
                }
            };
        }

        while (true)
        {
            var scrollResult = await _client.ScrollAsync(
                collectionName,
                filter: filter,
                limit: 100,
                offset: offset,
                payloadSelector: true,
                vectorsSelector: true,
                cancellationToken: ct);

            foreach (var point in scrollResult.Result)
            {
                var doc = PayloadToDocument(point.Payload, ExtractVectorData(point.Vectors));
                if (doc != null)
                    documents.Add(doc);
            }

            if (scrollResult.NextPageOffset == null)
                break;

            offset = scrollResult.NextPageOffset;
        }

        return documents;
    }

    // ========== Search Operations ==========

    public async Task<List<VectorSearchResult>> SearchAsync(string collectionName, VectorSearchQuery query, CancellationToken ct = default)
    {
        var metadata = _collections.GetValueOrDefault(collectionName);
        if (metadata == null)
        {
            throw new InvalidOperationException($"Collection '{collectionName}' not initialized. Call InitializeAsync first.");
        }

        if (query.QueryEmbedding.Length != metadata.VectorDimension)
        {
            throw new ArgumentException(
                $"Query embedding dimension {query.QueryEmbedding.Length} does not match collection dimension {metadata.VectorDimension}");
        }

        Filter? filter = null;
        if (query.ParentId != null || (query.Filters != null && query.Filters.Count > 0))
        {
            filter = new Filter();

            if (query.ParentId != null)
            {
                filter.Must.Add(new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "parentId",
                        Match = new Match { Keyword = query.ParentId }
                    }
                });
            }

            // TODO: Add support for complex metadata filters
        }

        var results = await _client.SearchAsync(
            collectionName,
            query.QueryEmbedding,
            filter: filter,
            limit: (ulong)query.TopK,
            payloadSelector: query.IncludeDocument,
            vectorsSelector: query.IncludeEmbedding,
            cancellationToken: ct);

        return results
            .Select(r =>
            {
                VectorDocument? doc = null;
                if (query.IncludeDocument)
                {
                    doc = PayloadToDocument(r.Payload, query.IncludeEmbedding ? ExtractVectorData(r.Vectors) : null);
                }

                return new VectorSearchResult
                {
                    Id = r.Payload.TryGetValue("id", out var idVal) ? idVal.StringValue : "",
                    Score = r.Score,
                    Distance = null,  // Qdrant returns score, not distance
                    Document = doc,
                    Metadata = doc?.Metadata ?? new Dictionary<string, object>(),
                    Text = query.IncludeDocument && doc != null ? doc.Text : null,
                    ParentId = doc?.ParentId
                };
            })
            .Where(r => r.Score >= query.MinScore && (!query.MaxScore.HasValue || r.Score <= query.MaxScore.Value))
            .ToList();
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
        var result = new Dictionary<string, VectorDocument>();
        var hashList = contentHashes.ToList();

        if (hashList.Count == 0)
            return result;

        try
        {
            // Query in batches of 50 hashes using OR conditions
            foreach (var hashBatch in hashList.Chunk(50))
            {
                var filter = new Filter();
                foreach (var hash in hashBatch)
                {
                    filter.Should.Add(new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "contentHash",
                            Match = new Match { Keyword = hash }
                        }
                    });
                }

                var scrollResult = await _client.ScrollAsync(
                    collectionName,
                    filter: filter,
                    limit: (uint)hashBatch.Length,
                    payloadSelector: true,
                    vectorsSelector: true,
                    cancellationToken: ct);

                foreach (var point in scrollResult.Result)
                {
                    var doc = PayloadToDocument(point.Payload, ExtractVectorData(point.Vectors));
                    if (doc != null && !string.IsNullOrEmpty(doc.ContentHash))
                    {
                        result[doc.ContentHash] = doc;
                    }
                }
            }

            if (result.Count > 0)
            {
                _logger.LogDebug("Found {Found}/{Total} cached documents by hash in collection {Collection}",
                    result.Count, hashList.Count, collectionName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetDocumentsByHash failed for collection {Collection}", collectionName);
        }

        return result;
    }

    public async Task RemoveStaleDocumentsAsync(string collectionName, string parentId, IEnumerable<string> validHashes, CancellationToken ct = default)
    {
        try
        {
            var validHashSet = validHashes.ToHashSet();

            // Get all documents for this parent
            var parentDocs = await GetAllDocumentsAsync(collectionName, parentId, ct);

            // Find documents to delete (those not in valid hashes)
            var staleDocs = parentDocs
                .Where(d => d.ContentHash != null && !validHashSet.Contains(d.ContentHash))
                .ToList();

            if (staleDocs.Count == 0)
                return;

            // Delete stale documents one by one using filter
            foreach (var doc in staleDocs)
            {
                await _client.DeleteAsync(
                    collectionName,
                    new Filter
                    {
                        Must =
                        {
                            new Condition
                            {
                                Field = new FieldCondition
                                {
                                    Key = "contentHash",
                                    Match = new Match { Keyword = doc.ContentHash! }
                                }
                            }
                        }
                    },
                    cancellationToken: ct);
            }

            _logger.LogDebug("Removed {Count} stale documents from collection {Collection}",
                staleDocs.Count, collectionName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RemoveStaleDocuments failed for collection {Collection}", collectionName);
        }
    }

    // ========== Summary Caching ==========

    public async Task<CachedSummary?> GetCachedSummaryAsync(string collectionName, string documentId, CancellationToken ct = default)
    {
        try
        {
            var collections = await _client.ListCollectionsAsync(ct);
            if (!collections.Any(c => c == collectionName))
                return null;

            var scrollResult = await _client.ScrollAsync(
                collectionName,
                filter: new Filter
                {
                    Must =
                    {
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key = "type",
                                Match = new Match { Keyword = "summary" }
                            }
                        },
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key = "documentId",
                                Match = new Match { Keyword = documentId }
                            }
                        }
                    }
                },
                limit: 1,
                payloadSelector: true,
                cancellationToken: ct);

            if (scrollResult.Result.Count == 0)
                return null;

            var point = scrollResult.Result[0];
            if (!point.Payload.TryGetValue("summaryJson", out var jsonVal))
                return null;

            var summary = JsonSerializer.Deserialize<CachedSummary>(jsonVal.StringValue);

            _logger.LogDebug("Cache hit for document {DocumentId} in collection {Collection}",
                documentId, collectionName);

            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetCachedSummary failed for document {DocumentId} in collection {Collection}",
                documentId, collectionName);
            return null;
        }
    }

    public async Task CacheSummaryAsync(string collectionName, CachedSummary summary, CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(summary);

            // Create a dummy vector (Qdrant requires a vector even for metadata-only points)
            var metadata = _collections.GetValueOrDefault(collectionName);
            var dummyVector = new float[metadata?.VectorDimension ?? 384];

            var point = new PointStruct
            {
                Id = new PointId { Uuid = GenerateUuidFromId($"summary:{summary.DocumentId}") },
                Vectors = dummyVector,
                Payload =
                {
                    ["type"] = "summary",
                    ["documentId"] = summary.DocumentId,
                    ["summaryJson"] = json,
                    ["cachedAt"] = summary.CachedAt.ToString("O")
                }
            };

            await _client.UpsertAsync(collectionName, new[] { point }, cancellationToken: ct);

            _logger.LogDebug("Cached summary for document {DocumentId} in collection {Collection}",
                summary.DocumentId, collectionName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CacheSummary failed for document {DocumentId} in collection {Collection}",
                summary.DocumentId, collectionName);
        }
    }

    // ========== Private Helpers ==========

    private static string GenerateUuidFromId(string id)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(id));
        return new Guid(hash).ToString();
    }

    private VectorDocument? PayloadToDocument(IDictionary<string, Value>? payload, float[]? embedding)
    {
        if (payload == null) return null;

        try
        {
            var id = payload.TryGetValue("id", out var idVal) ? idVal.StringValue : "";
            var parentId = payload.TryGetValue("parentId", out var pidVal) && !string.IsNullOrEmpty(pidVal.StringValue)
                ? pidVal.StringValue
                : null;
            var contentHash = payload.TryGetValue("contentHash", out var chVal) && !string.IsNullOrEmpty(chVal.StringValue)
                ? chVal.StringValue
                : null;
            var text = payload.TryGetValue("text", out var txtVal) && !string.IsNullOrEmpty(txtVal.StringValue)
                ? txtVal.StringValue
                : null;

            var metadataJson = payload.TryGetValue("metadata", out var metaVal) ? metaVal.StringValue : null;
            var metadata = string.IsNullOrEmpty(metadataJson)
                ? new Dictionary<string, object>()
                : JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson) ?? new Dictionary<string, object>();

            var createdAtStr = payload.TryGetValue("createdAt", out var caVal) ? caVal.StringValue : null;
            var updatedAtStr = payload.TryGetValue("updatedAt", out var uaVal) ? uaVal.StringValue : null;

            return new VectorDocument
            {
                Id = id,
                ParentId = parentId,
                ContentHash = contentHash,
                Text = text,
                Embedding = embedding ?? Array.Empty<float>(),
                Metadata = metadata,
                CreatedAt = string.IsNullOrEmpty(createdAtStr) ? DateTimeOffset.UtcNow : DateTimeOffset.Parse(createdAtStr),
                UpdatedAt = string.IsNullOrEmpty(updatedAtStr) ? null : DateTimeOffset.Parse(updatedAtStr)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Qdrant payload to VectorDocument");
            return null;
        }
    }

#pragma warning disable CS0612 // Suppress obsolete warning
    private static float[]? ExtractVectorData(object? vectors)
    {
        if (vectors == null) return null;

        try
        {
            // Use dynamic to handle API changes across Qdrant client versions
            dynamic v = vectors;
            if (v.Vector == null) return null;
            var vectorData = v.Vector.Data;
            return vectorData?.ToArray() as float[];
        }
        catch
        {
            return null;
        }
    }
#pragma warning restore CS0612

    public void Dispose()
    {
        if (_disposed) return;

        _client.Dispose();
        _disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private class CollectionMetadata
    {
        public required string Name { get; init; }
        public required int VectorDimension { get; init; }
        public required VectorDistance DistanceMetric { get; init; }
        public required bool StoreText { get; init; }
    }
}
