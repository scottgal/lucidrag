using Mostlylucid.RAG.Models;

namespace Mostlylucid.RAG.Services;

/// <summary>
/// Service for image multi-vector search using Qdrant named vectors.
/// Supports text (OCR), visual (CLIP), color, and motion embeddings.
/// </summary>
public interface IImageVectorStoreService
{
    /// <summary>
    /// Initialize the image collection with multi-vector support
    /// </summary>
    Task InitializeCollectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Index an image with multi-vector embeddings
    /// </summary>
    /// <param name="document">Image document metadata</param>
    /// <param name="embeddings">Multi-vector embeddings (text, visual, color, motion)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task IndexImageAsync(
        ImageDocument document,
        ImageEmbeddings embeddings,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Index multiple images in batch
    /// </summary>
    Task IndexImagesAsync(
        IEnumerable<(ImageDocument Document, ImageEmbeddings Embeddings)> images,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Search for similar images using multi-vector query
    /// Supports fusion across multiple embedding types
    /// </summary>
    Task<List<ImageSearchResult>> SearchAsync(
        ImageSearchQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Find similar images based on a reference image ID
    /// </summary>
    Task<List<ImageSearchResult>> FindSimilarImagesAsync(
        string imageId,
        int limit = 10,
        string[]? vectorNames = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Search by text query (uses CLIP text encoder)
    /// </summary>
    Task<List<ImageSearchResult>> SearchByTextAsync(
        string query,
        int limit = 10,
        float scoreThreshold = 0.5f,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Search by visual similarity (uses CLIP image encoder)
    /// </summary>
    Task<List<ImageSearchResult>> SearchByVisualAsync(
        float[] visualEmbedding,
        int limit = 10,
        float scoreThreshold = 0.5f,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Search by color palette
    /// </summary>
    Task<List<ImageSearchResult>> SearchByColorAsync(
        float[] colorEmbedding,
        int limit = 10,
        float scoreThreshold = 0.5f,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Search by motion signature (for GIFs/WebP)
    /// </summary>
    Task<List<ImageSearchResult>> SearchByMotionAsync(
        float[] motionEmbedding,
        int limit = 10,
        float scoreThreshold = 0.5f,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete an image from the index
    /// </summary>
    Task DeleteImageAsync(string imageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get image document by ID
    /// </summary>
    Task<ImageDocument?> GetImageAsync(string imageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update image metadata (tags, caption, etc.) without re-embedding
    /// </summary>
    Task UpdateMetadataAsync(
        string imageId,
        Dictionary<string, object> metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear all images from the collection
    /// </summary>
    Task ClearCollectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get collection statistics (count, vector coverage, etc.)
    /// </summary>
    Task<ImageCollectionStats> GetStatsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Statistics about the image collection
/// </summary>
public record ImageCollectionStats
{
    public int TotalImages { get; init; }
    public int ImagesWithTextEmbedding { get; init; }
    public int ImagesWithVisualEmbedding { get; init; }
    public int ImagesWithColorEmbedding { get; init; }
    public int ImagesWithMotionEmbedding { get; init; }
    public Dictionary<string, int> FormatDistribution { get; init; } = new();
    public Dictionary<string, int> TypeDistribution { get; init; } = new();
}
