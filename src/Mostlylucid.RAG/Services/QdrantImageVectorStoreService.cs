using Microsoft.Extensions.Logging;
using Mostlylucid.RAG.Config;
using Mostlylucid.RAG.Models;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Mostlylucid.RAG.Services;

/// <summary>
/// Qdrant implementation of multi-vector image search using named vectors.
/// Supports text, visual, color, and motion embeddings for comprehensive image retrieval.
/// </summary>
public class QdrantImageVectorStoreService : IImageVectorStoreService
{
    private readonly ILogger<QdrantImageVectorStoreService> _logger;
    private readonly SemanticSearchConfig _config;
    private readonly QdrantClient? _client;
    private bool _collectionInitialized;

    // Named vector configuration
    private const string TextVectorName = "text";
    private const string VisualVectorName = "visual";
    private const string ColorVectorName = "color";
    private const string MotionVectorName = "motion";

    // Vector dimensions (configurable, but these are typical defaults)
    private const ulong TextVectorSize = 768;    // CLIP ViT-B/32 text embedding
    private const ulong VisualVectorSize = 768;  // CLIP ViT-B/32 image embedding
    private const ulong ColorVectorSize = 64;    // Compact color histogram/palette
    private const ulong MotionVectorSize = 16;   // Motion signature (direction + magnitude + complexity)

    public QdrantImageVectorStoreService(
        ILogger<QdrantImageVectorStoreService> logger,
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
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            var uri = new Uri(_config.QdrantUrl);
            var host = uri.Host;
            var port = uri.Port > 0 && uri.Port != 6333 ? uri.Port : 6334;

            var apiKey = !string.IsNullOrEmpty(_config.WriteApiKey)
                ? _config.WriteApiKey
                : _config.ReadApiKey;

            _client = new QdrantClient(host, port, https: uri.Scheme == "https", apiKey: apiKey);

            _logger.LogInformation("Connected to Qdrant for image vectors at {Host}:{Port}", host, port);
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
            var collectionName = $"{_config.CollectionName}_images";

            // Check if collection exists
            var collections = await _client.ListCollectionsAsync(cancellationToken);
            var collectionExists = collections.Contains(collectionName);

            if (!collectionExists)
            {
                _logger.LogInformation("Creating multi-vector image collection {CollectionName}", collectionName);

                // Create collection with named vectors (multi-vector support)
                var vectorsConfig = new VectorParamsMap
                {
                    Map =
                    {
                        // Text embedding from OCR (CLIP text encoder)
                        [TextVectorName] = new VectorParams
                        {
                            Size = TextVectorSize,
                            Distance = Distance.Cosine
                        },
                        // Visual embedding from image (CLIP image encoder)
                        [VisualVectorName] = new VectorParams
                        {
                            Size = VisualVectorSize,
                            Distance = Distance.Cosine
                        },
                        // Color palette embedding
                        [ColorVectorName] = new VectorParams
                        {
                            Size = ColorVectorSize,
                            Distance = Distance.Cosine
                        },
                        // Motion signature embedding (for GIF/WebP)
                        [MotionVectorName] = new VectorParams
                        {
                            Size = MotionVectorSize,
                            Distance = Distance.Cosine
                        }
                    }
                };

                await _client.CreateCollectionAsync(
                    collectionName: collectionName,
                    vectorsConfig: vectorsConfig,
                    cancellationToken: cancellationToken
                );

                _logger.LogInformation("Multi-vector collection {CollectionName} created with vectors: {Vectors}",
                    collectionName, string.Join(", ", vectorsConfig.Map.Keys));
            }
            else
            {
                _logger.LogInformation("Multi-vector collection {CollectionName} already exists", collectionName);
            }

            _collectionInitialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize multi-vector image collection");
            throw;
        }
    }

    public async Task IndexImageAsync(
        ImageDocument document,
        ImageEmbeddings embeddings,
        CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled)
            return;

        await InitializeCollectionAsync(cancellationToken);

        try
        {
            var collectionName = $"{_config.CollectionName}_images";
            var pointId = GenerateDeterministicId(document.Id);

            // Build payload with image metadata
            var payload = new Dictionary<string, Value>
            {
                ["id"] = document.Id,
                ["path"] = document.Path,
                ["format"] = document.Format,
                ["width"] = (long)document.Width,
                ["height"] = (long)document.Height,
                ["aspect_ratio"] = document.Width / (double)document.Height
            };

            if (!string.IsNullOrEmpty(document.DetectedType))
            {
                payload["detected_type"] = document.DetectedType;
                payload["type_confidence"] = document.TypeConfidence;
            }

            if (!string.IsNullOrEmpty(document.ExtractedText))
            {
                payload["extracted_text"] = document.ExtractedText;
                payload["has_text"] = true;
            }
            else
            {
                payload["has_text"] = false;
            }

            if (!string.IsNullOrEmpty(document.LlmCaption))
            {
                payload["llm_caption"] = document.LlmCaption;
            }

            // Salience summary (confidence-weighted RRF fusion)
            if (!string.IsNullOrEmpty(document.SalienceSummary))
            {
                payload["salience_summary"] = document.SalienceSummary;
            }

            // Structured salient signals for filtering
            if (document.SalientSignals?.Any() == true)
            {
                // Flatten signals for Qdrant payload (nested objects need special handling)
                foreach (var (key, value) in document.SalientSignals)
                {
                    if (value != null)
                    {
                        var payloadKey = $"signal_{key.Replace(".", "_")}";
                        if (value is string strVal)
                            payload[payloadKey] = strVal;
                        else if (value is int intVal)
                            payload[payloadKey] = intVal;
                        else if (value is double dblVal)
                            payload[payloadKey] = dblVal;
                        else if (value is bool boolVal)
                            payload[payloadKey] = boolVal;
                        else if (value is IEnumerable<string> strList)
                            payload[payloadKey] = new Value { ListValue = new ListValue { Values = { strList.Select(s => new Value { StringValue = s }) } } };
                        else
                            payload[payloadKey] = value.ToString();
                    }
                }
            }

            if (!string.IsNullOrEmpty(document.SourceUrl))
            {
                payload["source_url"] = document.SourceUrl;
            }

            if (document.DominantColors?.Any() == true)
            {
                payload["dominant_colors"] = new Value
                {
                    ListValue = new ListValue
                    {
                        Values = { document.DominantColors.Select(c => new Value { StringValue = c }) }
                    }
                };
            }

            if (!string.IsNullOrEmpty(document.MotionDirection))
            {
                payload["motion_direction"] = document.MotionDirection;
            }

            if (!string.IsNullOrEmpty(document.AnimationType))
            {
                payload["animation_type"] = document.AnimationType;
            }

            if (document.Tags?.Any() == true)
            {
                payload["tags"] = new Value
                {
                    ListValue = new ListValue
                    {
                        Values = { document.Tags.Select(t => new Value { StringValue = t }) }
                    }
                };
            }

            // Add custom metadata
            foreach (var (key, value) in document.Metadata)
            {
                payload[$"meta_{key}"] = value;
            }

            // Build named vectors dictionary
            var namedVectors = new Dictionary<string, float[]>();

            if (embeddings.TextEmbedding != null)
            {
                namedVectors[TextVectorName] = embeddings.TextEmbedding;
                payload["has_text_embedding"] = true;
            }

            if (embeddings.VisualEmbedding != null)
            {
                namedVectors[VisualVectorName] = embeddings.VisualEmbedding;
                payload["has_visual_embedding"] = true;
            }

            if (embeddings.ColorEmbedding != null)
            {
                namedVectors[ColorVectorName] = embeddings.ColorEmbedding;
                payload["has_color_embedding"] = true;
            }

            if (embeddings.MotionEmbedding != null)
            {
                namedVectors[MotionVectorName] = embeddings.MotionEmbedding;
                payload["has_motion_embedding"] = true;
            }

            var point = new PointStruct
            {
                Id = pointId,
                Vectors = namedVectors,
                Payload = { payload }
            };

            await _client.UpsertAsync(
                collectionName: collectionName,
                points: new[] { point },
                cancellationToken: cancellationToken
            );

            _logger.LogDebug("Indexed image {Id} with {VectorCount} vectors", document.Id, namedVectors.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index image {Id}", document.Id);
            throw;
        }
    }

    public async Task IndexImagesAsync(
        IEnumerable<(ImageDocument Document, ImageEmbeddings Embeddings)> images,
        CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled)
            return;

        await InitializeCollectionAsync(cancellationToken);

        var imagesList = images.ToList();
        if (imagesList.Count == 0)
            return;

        try
        {
            var collectionName = $"{_config.CollectionName}_images";
            var points = new List<PointStruct>();

            foreach (var (document, embeddings) in imagesList)
            {
                var pointId = GenerateDeterministicId(document.Id);

                var payload = new Dictionary<string, Value>
                {
                    ["id"] = document.Id,
                    ["path"] = document.Path,
                    ["format"] = document.Format,
                    ["width"] = (long)document.Width,
                    ["height"] = (long)document.Height
                };

                if (!string.IsNullOrEmpty(document.DetectedType))
                {
                    payload["detected_type"] = document.DetectedType;
                    payload["type_confidence"] = document.TypeConfidence;
                }

                if (!string.IsNullOrEmpty(document.ExtractedText))
                {
                    payload["extracted_text"] = document.ExtractedText;
                    payload["has_text"] = true;
                }
                else
                {
                    payload["has_text"] = false;
                }

                // Build named vectors
                var namedVectors = new Dictionary<string, float[]>();

                if (embeddings.TextEmbedding != null)
                    namedVectors[TextVectorName] = embeddings.TextEmbedding;

                if (embeddings.VisualEmbedding != null)
                    namedVectors[VisualVectorName] = embeddings.VisualEmbedding;

                if (embeddings.ColorEmbedding != null)
                    namedVectors[ColorVectorName] = embeddings.ColorEmbedding;

                if (embeddings.MotionEmbedding != null)
                    namedVectors[MotionVectorName] = embeddings.MotionEmbedding;

                var point = new PointStruct
                {
                    Id = pointId,
                    Vectors = namedVectors,
                    Payload = { payload }
                };

                points.Add(point);
            }

            await _client.UpsertAsync(
                collectionName: collectionName,
                points: points,
                cancellationToken: cancellationToken
            );

            _logger.LogInformation("Indexed {Count} images in batch", imagesList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index {Count} images", imagesList.Count);
            throw;
        }
    }

    public Task<List<ImageSearchResult>> SearchAsync(
        ImageSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        // TODO: Implement multi-vector fusion search
        // This requires Qdrant query fusion (available in recent versions)
        throw new NotImplementedException("Multi-vector fusion search coming in next iteration");
    }

    public Task<List<ImageSearchResult>> FindSimilarImagesAsync(
        string imageId,
        int limit = 10,
        string[]? vectorNames = null,
        CancellationToken cancellationToken = default)
    {
        // TODO: Implement similar image search
        throw new NotImplementedException("Similar image search coming in next iteration");
    }

    public async Task<List<ImageSearchResult>> SearchByTextAsync(
        string query,
        int limit = 10,
        float scoreThreshold = 0.5f,
        CancellationToken cancellationToken = default)
    {
        // Note: This assumes query is already a CLIP text embedding
        // In practice, you'd call CLIP text encoder here
        throw new NotImplementedException("Text search requires CLIP text encoder integration");
    }

    public async Task<List<ImageSearchResult>> SearchByVisualAsync(
        float[] visualEmbedding,
        int limit = 10,
        float scoreThreshold = 0.5f,
        CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled)
            return new List<ImageSearchResult>();

        try
        {
            var collectionName = $"{_config.CollectionName}_images";

            var searchResults = await _client.SearchAsync(
                collectionName: collectionName,
                vector: visualEmbedding,
                vectorName: VisualVectorName,  // Search in visual vector space
                limit: (ulong)limit,
                scoreThreshold: scoreThreshold,
                cancellationToken: cancellationToken
            );

            return searchResults.Select(result => new ImageSearchResult
            {
                Id = result.Payload["id"].StringValue,
                Path = result.Payload["path"].StringValue,
                Score = result.Score,
                MatchVector = VisualVectorName,
                Metadata = result.Payload.ToDictionary(
                    kvp => kvp.Key,
                    kvp => (object)kvp.Value.StringValue
                )
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Visual search failed");
            return new List<ImageSearchResult>();
        }
    }

    public async Task<List<ImageSearchResult>> SearchByColorAsync(
        float[] colorEmbedding,
        int limit = 10,
        float scoreThreshold = 0.5f,
        CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled)
            return new List<ImageSearchResult>();

        try
        {
            var collectionName = $"{_config.CollectionName}_images";

            var searchResults = await _client.SearchAsync(
                collectionName: collectionName,
                vector: colorEmbedding,
                vectorName: ColorVectorName,  // Search in color vector space
                limit: (ulong)limit,
                scoreThreshold: scoreThreshold,
                cancellationToken: cancellationToken
            );

            return searchResults.Select(result => new ImageSearchResult
            {
                Id = result.Payload["id"].StringValue,
                Path = result.Payload["path"].StringValue,
                Score = result.Score,
                MatchVector = ColorVectorName
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Color search failed");
            return new List<ImageSearchResult>();
        }
    }

    public async Task<List<ImageSearchResult>> SearchByMotionAsync(
        float[] motionEmbedding,
        int limit = 10,
        float scoreThreshold = 0.5f,
        CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled)
            return new List<ImageSearchResult>();

        try
        {
            var collectionName = $"{_config.CollectionName}_images";

            var searchResults = await _client.SearchAsync(
                collectionName: collectionName,
                vector: motionEmbedding,
                vectorName: MotionVectorName,  // Search in motion vector space
                limit: (ulong)limit,
                scoreThreshold: scoreThreshold,
                cancellationToken: cancellationToken
            );

            return searchResults.Select(result => new ImageSearchResult
            {
                Id = result.Payload["id"].StringValue,
                Path = result.Payload["path"].StringValue,
                Score = result.Score,
                MatchVector = MotionVectorName
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Motion search failed");
            return new List<ImageSearchResult>();
        }
    }

    public async Task DeleteImageAsync(string imageId, CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled)
            return;

        try
        {
            var collectionName = $"{_config.CollectionName}_images";
            var pointId = GenerateDeterministicId(imageId);

            await _client.DeleteAsync(
                collectionName: collectionName,
                ids: new[] { new PointId { Num = pointId } },
                cancellationToken: cancellationToken
            );

            _logger.LogDebug("Deleted image {ImageId}", imageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete image {ImageId}", imageId);
        }
    }

    public async Task<ImageDocument?> GetImageAsync(string imageId, CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled)
            return null;

        try
        {
            var collectionName = $"{_config.CollectionName}_images";
            var pointId = GenerateDeterministicId(imageId);

            var points = await _client.RetrieveAsync(
                collectionName: collectionName,
                ids: new[] { new PointId { Num = pointId } },
                withPayload: true,
                cancellationToken: cancellationToken
            );

            var point = points.FirstOrDefault();
            if (point == null)
                return null;

            // Reconstruct ImageDocument from payload
            var payload = point.Payload;

            // Extract salient signals from flattened payload
            var salientSignals = new Dictionary<string, object?>();
            foreach (var (key, value) in payload)
            {
                if (key.StartsWith("signal_"))
                {
                    var signalKey = key.Substring(7).Replace("_", ".");
                    salientSignals[signalKey] = value.KindCase switch
                    {
                        Value.KindOneofCase.StringValue => value.StringValue,
                        Value.KindOneofCase.IntegerValue => value.IntegerValue,
                        Value.KindOneofCase.DoubleValue => value.DoubleValue,
                        Value.KindOneofCase.BoolValue => value.BoolValue,
                        Value.KindOneofCase.ListValue => value.ListValue.Values.Select(v => v.StringValue).ToList(),
                        _ => value.ToString()
                    };
                }
            }

            return new ImageDocument
            {
                Id = payload["id"].StringValue,
                Path = payload["path"].StringValue,
                Format = payload["format"].StringValue,
                Width = (int)payload["width"].IntegerValue,
                Height = (int)payload["height"].IntegerValue,
                DetectedType = payload.TryGetValue("detected_type", out var dt) ? dt.StringValue : null,
                TypeConfidence = payload.TryGetValue("type_confidence", out var tc) ? tc.DoubleValue : 0,
                ExtractedText = payload.TryGetValue("extracted_text", out var et) ? et.StringValue : null,
                LlmCaption = payload.TryGetValue("llm_caption", out var lc) ? lc.StringValue : null,
                SalienceSummary = payload.TryGetValue("salience_summary", out var ss) ? ss.StringValue : null,
                SalientSignals = salientSignals.Count > 0 ? salientSignals : null,
                SourceUrl = payload.TryGetValue("source_url", out var su) ? su.StringValue : null,
                MotionDirection = payload.TryGetValue("motion_direction", out var md) ? md.StringValue : null,
                AnimationType = payload.TryGetValue("animation_type", out var at) ? at.StringValue : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get image {ImageId}", imageId);
            return null;
        }
    }

    public async Task UpdateMetadataAsync(
        string imageId,
        Dictionary<string, object> metadata,
        CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled)
            return;

        try
        {
            var collectionName = $"{_config.CollectionName}_images";
            var pointId = GenerateDeterministicId(imageId);

            var payload = metadata.ToDictionary(
                kvp => kvp.Key,
                kvp => new Value { StringValue = kvp.Value.ToString() ?? "" }
            );

            await _client.SetPayloadAsync(
                collectionName: collectionName,
                payload: payload,
                filter: new Filter
                {
                    Must =
                    {
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key = "id",
                                Match = new Match { Keyword = imageId }
                            }
                        }
                    }
                },
                cancellationToken: cancellationToken
            );

            _logger.LogDebug("Updated metadata for image {ImageId}", imageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update metadata for image {ImageId}", imageId);
        }
    }

    public async Task ClearCollectionAsync(CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled)
            return;

        try
        {
            var collectionName = $"{_config.CollectionName}_images";

            var collections = await _client.ListCollectionsAsync(cancellationToken);
            if (!collections.Contains(collectionName))
            {
                _logger.LogInformation("Collection {CollectionName} does not exist", collectionName);
                return;
            }

            await _client.DeleteCollectionAsync(collectionName, cancellationToken: cancellationToken);
            _logger.LogInformation("Deleted image collection {CollectionName}", collectionName);

            _collectionInitialized = false;
            await InitializeCollectionAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear image collection");
            throw;
        }
    }

    public async Task<ImageCollectionStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        if (_client == null || !_config.Enabled)
            return new ImageCollectionStats();

        try
        {
            var collectionName = $"{_config.CollectionName}_images";

            // Get collection info
            var collectionInfo = await _client.GetCollectionInfoAsync(collectionName, cancellationToken);

            // TODO: Compute detailed stats by scrolling through collection
            // For now, return basic stats

            return new ImageCollectionStats
            {
                TotalImages = (int)collectionInfo.PointsCount
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get collection stats");
            return new ImageCollectionStats();
        }
    }

    /// <summary>
    /// Generate a deterministic ulong from a string ID using xxHash64.
    /// </summary>
    private static ulong GenerateDeterministicId(string id)
    {
        return System.IO.Hashing.XxHash64.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(id));
    }
}
