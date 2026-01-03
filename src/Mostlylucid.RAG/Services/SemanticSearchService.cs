using Microsoft.Extensions.Logging;
using Mostlylucid.RAG.Config;
using Mostlylucid.RAG.Models;
using System.Security.Cryptography;
using System.Text;

namespace Mostlylucid.RAG.Services;

/// <summary>
/// High-level semantic search service that coordinates embedding and vector storage
/// </summary>
public class SemanticSearchService : ISemanticSearchService
{
    private readonly ILogger<SemanticSearchService> _logger;
    private readonly SemanticSearchConfig _config;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStoreService _vectorStoreService;

    public SemanticSearchService(
        ILogger<SemanticSearchService> logger,
        SemanticSearchConfig config,
        IEmbeddingService embeddingService,
        IVectorStoreService vectorStoreService)
    {
        _logger = logger;
        _config = config;
        _embeddingService = embeddingService;
        _vectorStoreService = vectorStoreService;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("Semantic search is disabled");
            return;
        }

        try
        {
            // Initialize embedding service (downloads model if needed)
            _logger.LogInformation("Initializing embedding service...");
            await _embeddingService.EnsureInitializedAsync(cancellationToken);

            // Initialize vector store
            await _vectorStoreService.InitializeCollectionAsync(cancellationToken);
            _logger.LogInformation("Semantic search initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize semantic search");
        }
    }

    public async Task IndexPostAsync(BlogPostDocument document, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
            return;

        try
        {
            // Prepare text for embedding: combine title and content
            var textToEmbed = PrepareTextForEmbedding(document);

            // Generate embedding
            var embedding = await _embeddingService.GenerateEmbeddingAsync(textToEmbed, cancellationToken);

            // Compute content hash if not provided
            if (string.IsNullOrEmpty(document.ContentHash))
            {
                document.ContentHash = ComputeContentHash(document.Content);
            }

            // Store in vector database
            await _vectorStoreService.IndexDocumentAsync(document, embedding, cancellationToken);

            _logger.LogInformation("Indexed post {Slug}", document.Slug);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index post {Slug}", document.Slug);
        }
    }

    public async Task IndexPostsAsync(IEnumerable<BlogPostDocument> documents, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
            return;

        var documentsList = documents.ToList();
        if (documentsList.Count == 0)
            return;

        _logger.LogInformation("Indexing {Count} posts in batch", documentsList.Count);

        try
        {
            var documentsWithEmbeddings = new List<(BlogPostDocument Document, float[] Embedding)>();

            foreach (var document in documentsList)
            {
                try
                {
                    var textToEmbed = PrepareTextForEmbedding(document);
                    var embedding = await _embeddingService.GenerateEmbeddingAsync(textToEmbed, cancellationToken);

                    if (string.IsNullOrEmpty(document.ContentHash))
                    {
                        document.ContentHash = ComputeContentHash(document.Content);
                    }

                    documentsWithEmbeddings.Add((document, embedding));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to generate embedding for {Slug}", document.Slug);
                }
            }

            if (documentsWithEmbeddings.Count > 0)
            {
                await _vectorStoreService.IndexDocumentsAsync(documentsWithEmbeddings, cancellationToken);
                _logger.LogInformation("Successfully indexed {Count} posts", documentsWithEmbeddings.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index posts in batch");
        }
    }

    public async Task<List<SearchResult>> SearchAsync(string query, int limit = 10, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || string.IsNullOrWhiteSpace(query))
            return new List<SearchResult>();

        try
        {
            // Generate embedding for the search query
            var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(query, cancellationToken);

            // Search in vector store
            var results = await _vectorStoreService.SearchAsync(
                queryEmbedding,
                Math.Min(limit, _config.SearchResultsCount),
                _config.MinimumSimilarityScore,
                cancellationToken);

            _logger.LogDebug("Search for '{Query}' returned {Count} results", query, results.Count);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed for query '{Query}'", query);
            return new List<SearchResult>();
        }
    }

    public async Task<List<SearchResult>> GetRelatedPostsAsync(string slug, int limit = 5, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
            return new List<SearchResult>();

        try
        {
            var results = await _vectorStoreService.FindRelatedPostsAsync(
                slug,
                Math.Min(limit, _config.RelatedPostsCount),
                cancellationToken);

            _logger.LogDebug("Found {Count} related posts for {Slug}", results.Count, slug);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get related posts for {Slug}", slug);
            return new List<SearchResult>();
        }
    }

    public async Task DeletePostAsync(string slug, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
            return;

        try
        {
            await _vectorStoreService.DeleteDocumentAsync(slug, cancellationToken);
            _logger.LogInformation("Deleted post {Slug} from index", slug);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete post {Slug}", slug);
        }
    }

    public async Task<bool> NeedsReindexingAsync(string slug, string currentHash, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
            return false;

        try
        {
            var existingHash = await _vectorStoreService.GetDocumentHashAsync(slug, cancellationToken);

            // If document doesn't exist or hash is different, needs reindexing
            return existingHash == null || existingHash != currentHash;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check reindexing status for {Slug}", slug);
            return true; // Err on the side of reindexing
        }
    }

    private string PrepareTextForEmbedding(BlogPostDocument document)
    {
        // Convert slug to readable text (e.g., "my-blog-post" -> "my blog post")
        // This helps match broken links with typos in the URL
        var slugAsText = document.Slug.Replace("-", " ").Replace("_", " ");

        // Combine slug, title and content
        // Slug gets highest weight (4x) for 404 typo recovery - users mistype URLs
        // Title gets medium weight (2x) for search relevance
        // Content gets base weight (1x)
        var text = $"{slugAsText}. {slugAsText}. {slugAsText}. {slugAsText}. {document.Title}. {document.Title}. {document.Content}";

        // Truncate to a reasonable length (embedding models have token limits)
        // For all-MiniLM-L6-v2, max tokens is 256, which is roughly 1000-1500 characters
        const int maxLength = 2000;
        if (text.Length > maxLength)
        {
            text = text[..maxLength];
        }

        return text;
    }

    private string ComputeContentHash(string content)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(content);
        var hashBytes = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hashBytes);
    }
}
