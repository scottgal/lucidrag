using Microsoft.Extensions.Logging;
using Mostlylucid.RAG.Config;
using Mostlylucid.RAG.Models;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Mostlylucid.RAG.Services;

/// <summary>
/// Qdrant-based vector store service for semantic search
/// </summary>
public class QdrantVectorStoreService : IVectorStoreService
{
    private readonly ILogger<QdrantVectorStoreService> _logger;
    private readonly SemanticSearchConfig _config;
    private readonly QdrantClient? _client;
    private bool _collectionInitialized;

    public QdrantVectorStoreService(
        ILogger<QdrantVectorStoreService> logger,
        SemanticSearchConfig config)
    {
        _logger = logger;
        _config = config;

        if (!_config.Enabled)
        {
            _logger.LogInformation("Semantic search is disabled");
            return;
        }

        try
        {
            // Enable HTTP/2 unencrypted support for gRPC
            // This is required for Qdrant gRPC on Windows without TLS
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            // Parse the Qdrant URL
            var uri = new Uri(_config.QdrantUrl);
            var host = uri.Host;

            // Use the port from URL if specified, otherwise default to 6334 (gRPC)
            // If port is 6333 (HTTP), we need to use gRPC port 6334
            var port = uri.Port > 0 && uri.Port != 6333 ? uri.Port : 6334;

            // Create Qdrant client with API key
            // Use WriteApiKey for full access (read + write operations)
            // If only ReadApiKey is set, use that (read-only mode)
            var apiKey = !string.IsNullOrEmpty(_config.WriteApiKey)
                ? _config.WriteApiKey
                : _config.ReadApiKey;

            _client = new QdrantClient(host, port, https: uri.Scheme == "https", apiKey: apiKey);

            _logger.LogInformation("Connected to Qdrant at {Host}:{Port} (gRPC), API key: {HasKey}",
                host, port, !string.IsNullOrEmpty(apiKey) ? "configured" : "not set");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Qdrant at {Url}", _config.QdrantUrl);
        }
    }

    public async Task InitializeCollectionAsync(CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled || _collectionInitialized)
            return;

        try
        {
            // Check if collection exists
            var collections = await _client.ListCollectionsAsync(cancellationToken);
            var collectionExists = collections.Contains(_config.CollectionName);

            if (!collectionExists)
            {
                _logger.LogInformation("Creating collection {CollectionName}", _config.CollectionName);

                // Create collection with cosine similarity
                await _client.CreateCollectionAsync(
                    collectionName: _config.CollectionName,
                    vectorsConfig: new VectorParams
                    {
                        Size = (ulong)_config.VectorSize,
                        Distance = Distance.Cosine
                    },
                    cancellationToken: cancellationToken
                );

                _logger.LogInformation("Collection {CollectionName} created successfully", _config.CollectionName);
            }
            else
            {
                _logger.LogInformation("Collection {CollectionName} already exists", _config.CollectionName);
            }

            _collectionInitialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize collection {CollectionName}", _config.CollectionName);
            throw;
        }
    }

    public async Task IndexDocumentAsync(BlogPostDocument document, float[] embedding, CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled)
            return;

        await InitializeCollectionAsync(cancellationToken);

        try
        {
            // Use deterministic ulong based on document ID to ensure upsert works correctly
            var pointId = GenerateDeterministicId(document.Id);

            var payload = new Dictionary<string, Value>
            {
                ["slug"] = document.Slug,
                ["content_hash"] = document.ContentHash ?? "",
                ["published_date"] = document.PublishedDate.ToUnixTimeSeconds(),
                ["languages"] = new Value
                {
                    ListValue = new ListValue
                    {
                        Values = { document.Languages.Select(lang => new Value { StringValue = lang }) }
                    }
                },
                ["categories"] = new Value
                {
                    ListValue = new ListValue
                    {
                        Values = { document.Categories.Select(cat => new Value { StringValue = cat }) }
                    }
                }
            };

            var point = new PointStruct
            {
                Id = pointId,
                Vectors = embedding,
                Payload = { payload }
            };

            await _client.UpsertAsync(
                collectionName: _config.CollectionName,
                points: new[] { point },
                cancellationToken: cancellationToken
            );

            _logger.LogDebug("Indexed document {Id} ({Title})", document.Id, document.Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index document {Id}", document.Id);
            throw;
        }
    }

    public async Task IndexDocumentsAsync(IEnumerable<(BlogPostDocument Document, float[] Embedding)> documents, CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled)
            return;

        await InitializeCollectionAsync(cancellationToken);

        var documentsList = documents.ToList();
        if (documentsList.Count == 0)
            return;

        try
        {
            var points = documentsList.Select(doc =>
            {
                var payload = new Dictionary<string, Value>
                {
                    ["slug"] = doc.Document.Slug,
                    ["content_hash"] = doc.Document.ContentHash ?? "",
                    ["published_date"] = doc.Document.PublishedDate.ToUnixTimeSeconds(),
                    ["languages"] = new Value
                    {
                        ListValue = new ListValue
                        {
                            Values = { doc.Document.Languages.Select(lang => new Value { StringValue = lang }) }
                        }
                    },
                    ["categories"] = new Value
                    {
                        ListValue = new ListValue
                        {
                            Values = { doc.Document.Categories.Select(cat => new Value { StringValue = cat }) }
                        }
                    }
                };

                return new PointStruct
                {
                    // Use deterministic ulong based on document ID to ensure upsert works correctly
                    Id = GenerateDeterministicId(doc.Document.Id),
                    Vectors = doc.Embedding,
                    Payload = { payload }
                };
            }).ToList();

            await _client.UpsertAsync(
                collectionName: _config.CollectionName,
                points: points,
                cancellationToken: cancellationToken
            );

            _logger.LogInformation("Indexed {Count} documents", documentsList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index {Count} documents", documentsList.Count);
            throw;
        }
    }

    public async Task<List<SearchResult>> SearchAsync(float[] queryEmbedding, int limit = 10, float scoreThreshold = 0.5f, CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled)
            return new List<SearchResult>();

        try
        {
            var searchResults = await _client.SearchAsync(
                collectionName: _config.CollectionName,
                vector: queryEmbedding,
                limit: (ulong)limit,
                scoreThreshold: scoreThreshold,
                cancellationToken: cancellationToken
            );

            return searchResults.Select(result => new SearchResult
            {
                Slug = result.Payload["slug"].StringValue,
                Score = result.Score
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed");
            return new List<SearchResult>();
        }
    }

    public async Task<List<SearchResult>> FindRelatedPostsAsync(string slug, int limit = 5, CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled)
            return new List<SearchResult>();

        try
        {
            // Find the document by slug
            var scrollResults = await _client.ScrollAsync(
                collectionName: _config.CollectionName,
                filter: new Filter
                {
                    Must =
                    {
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key = "slug",
                                Match = new Match { Keyword = slug }
                            }
                        }
                    }
                },
                limit: 1,
                cancellationToken: cancellationToken
            );

            var point = scrollResults.Result.FirstOrDefault();
            if (point == null)
            {
                _logger.LogWarning("Post {Slug} not found in vector store", slug);
                return new List<SearchResult>();
            }

            // Use the document's vector to find similar posts
            var searchResults = await _client.SearchAsync(
                collectionName: _config.CollectionName,
                vector: point.Vectors.Vector.Data.ToArray(),
                limit: (ulong)(limit + 1), // +1 because the first result will be the post itself
                scoreThreshold: _config.MinimumSimilarityScore,
                cancellationToken: cancellationToken
            );

            // Filter out the original post and return top N similar posts
            return searchResults
                .Where(r => r.Payload["slug"].StringValue != slug)
                .Take(limit)
                .Select(result => new SearchResult
                {
                    Slug = result.Payload["slug"].StringValue,
                    Score = result.Score
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find related posts for {Slug}", slug);
            return new List<SearchResult>();
        }
    }

    public async Task DeleteDocumentAsync(string id, CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled)
            return;

        try
        {
            // Note: 'id' parameter is actually the slug (see SemanticSearchService.DeletePostAsync)
            await _client.DeleteAsync(
                collectionName: _config.CollectionName,
                filter: new Filter
                {
                    Must =
                    {
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key = "slug",
                                Match = new Match { Keyword = id }
                            }
                        }
                    }
                },
                cancellationToken: cancellationToken
            );

            _logger.LogDebug("Deleted document {Slug}", id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete document {Slug}", id);
        }
    }

    public async Task<string?> GetDocumentHashAsync(string id, CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled)
            return null;

        try
        {
            // Note: 'id' parameter is actually the slug (see SemanticSearchService.NeedsReindexingAsync)
            var scrollResults = await _client.ScrollAsync(
                collectionName: _config.CollectionName,
                filter: new Filter
                {
                    Must =
                    {
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key = "slug",
                                Match = new Match { Keyword = id }
                            }
                        }
                    }
                },
                limit: 1,
                cancellationToken: cancellationToken
            );

            var point = scrollResults.Result.FirstOrDefault();
            return point?.Payload.TryGetValue("content_hash", out var hash) == true
                ? hash.StringValue
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get document hash for {Slug}", id);
            return null;
        }
    }

    public async Task UpdateLanguagesAsync(string slug, string[] languages, CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled)
            return;

        await InitializeCollectionAsync(cancellationToken);

        try
        {
            // Update the payload for documents matching this slug
            await _client.SetPayloadAsync(
                collectionName: _config.CollectionName,
                payload: new Dictionary<string, Value>
                {
                    ["languages"] = new Value
                    {
                        ListValue = new ListValue
                        {
                            Values = { languages.Select(lang => new Value { StringValue = lang }) }
                        }
                    }
                },
                filter: new Filter
                {
                    Must =
                    {
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key = "slug",
                                Match = new Match { Keyword = slug }
                            }
                        }
                    }
                },
                cancellationToken: cancellationToken
            );

            _logger.LogDebug("Updated languages for {Slug}: {Languages}", slug, string.Join(", ", languages));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update languages for {Slug}", slug);
        }
    }

    public async Task AddLanguageAsync(string slug, string language, CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled)
            return;

        await InitializeCollectionAsync(cancellationToken);

        try
        {
            // First, get the current languages array
            var scrollResults = await _client.ScrollAsync(
                collectionName: _config.CollectionName,
                filter: new Filter
                {
                    Must =
                    {
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key = "slug",
                                Match = new Match { Keyword = slug }
                            }
                        }
                    }
                },
                limit: 1,
                cancellationToken: cancellationToken
            );

            var point = scrollResults.Result.FirstOrDefault();
            if (point == null)
            {
                _logger.LogDebug("Post {Slug} not found in vector store, skipping language update", slug);
                return;
            }

            // Get existing languages
            var existingLanguages = new List<string>();
            if (point.Payload.TryGetValue("languages", out var langValue) && langValue.ListValue != null)
            {
                existingLanguages = langValue.ListValue.Values
                    .Select(v => v.StringValue)
                    .ToList();
            }

            // Add new language if not already present
            if (existingLanguages.Contains(language))
            {
                _logger.LogDebug("Language {Language} already exists for {Slug}", language, slug);
                return;
            }

            existingLanguages.Add(language);
            var sortedLanguages = existingLanguages.OrderBy(l => l).ToArray();

            // Update with new languages array
            await _client.SetPayloadAsync(
                collectionName: _config.CollectionName,
                payload: new Dictionary<string, Value>
                {
                    ["languages"] = new Value
                    {
                        ListValue = new ListValue
                        {
                            Values = { sortedLanguages.Select(lang => new Value { StringValue = lang }) }
                        }
                    }
                },
                filter: new Filter
                {
                    Must =
                    {
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key = "slug",
                                Match = new Match { Keyword = slug }
                            }
                        }
                    }
                },
                cancellationToken: cancellationToken
            );

            _logger.LogInformation("Added language {Language} to {Slug} in semantic search", language, slug);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add language {Language} for {Slug}", language, slug);
        }
    }

    public async Task ClearCollectionAsync(CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled)
            return;

        try
        {
            // Check if collection exists
            var collections = await _client.ListCollectionsAsync(cancellationToken);
            if (!collections.Contains(_config.CollectionName))
            {
                _logger.LogInformation("Collection {CollectionName} does not exist, nothing to clear", _config.CollectionName);
                return;
            }

            // Delete the collection
            await _client.DeleteCollectionAsync(_config.CollectionName, cancellationToken: cancellationToken);
            _logger.LogInformation("Deleted collection {CollectionName}", _config.CollectionName);

            // Reset initialization flag so it will be recreated
            _collectionInitialized = false;

            // Recreate the collection
            await InitializeCollectionAsync(cancellationToken);
            _logger.LogInformation("Recreated collection {CollectionName}", _config.CollectionName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear collection {CollectionName}", _config.CollectionName);
            throw;
        }
    }

    /// <summary>
    /// Generate a deterministic ulong from a string ID using xxHash64.
    /// This ensures the same document ID always gets the same point ID for proper upsert behavior.
    /// </summary>
    private static ulong GenerateDeterministicId(string id)
    {
        return System.IO.Hashing.XxHash64.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(id));
    }
}
