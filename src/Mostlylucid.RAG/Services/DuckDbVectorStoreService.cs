using Microsoft.Extensions.Logging;
using Mostlylucid.RAG.Config;
using Mostlylucid.RAG.Models;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Models;
using DuckDbStore = Mostlylucid.DocSummarizer.Services.DuckDbVectorStore;

namespace Mostlylucid.RAG.Services;

/// <summary>
/// DuckDB-backed vector store for semantic search.
/// Uses DocSummarizer.Core's DuckDbVectorStore with HNSW indexing.
/// Zero external dependencies - everything in a single file.
/// </summary>
public class DuckDbVectorStoreService : IVectorStoreService, IAsyncDisposable, IDisposable
{
    private readonly ILogger<DuckDbVectorStoreService> _logger;
    private readonly SemanticSearchConfig _config;
    private readonly DuckDbStore _store;
    private bool _initialized;

    public DuckDbVectorStoreService(
        ILogger<DuckDbVectorStoreService> logger,
        SemanticSearchConfig config)
    {
        _logger = logger;
        _config = config;
        
        // Ensure directory exists
        var dbPath = GetDatabasePath();
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        
        _store = new DuckDbStore(dbPath, _config.VectorSize, verbose: true);
        _logger.LogInformation("DuckDB vector store initialized at {Path}", dbPath);
    }

    private string GetDatabasePath()
    {
        var path = _config.DuckDbPath;
        
        // If relative path, make it relative to app directory
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, path);
        }
        
        return path;
    }

    public async Task InitializeCollectionAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        
        await _store.InitializeAsync(_config.CollectionName, _config.VectorSize, cancellationToken);
        _initialized = true;
        
        _logger.LogInformation("DuckDB collection '{Collection}' initialized", _config.CollectionName);
    }

    public async Task IndexDocumentAsync(BlogPostDocument document, float[] embedding, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        
        var segment = BlogPostToSegment(document, embedding);
        await _store.UpsertSegmentsAsync(_config.CollectionName, new[] { segment }, cancellationToken);
        
        _logger.LogDebug("Indexed document {Slug}", document.Slug);
    }

    public async Task IndexDocumentsAsync(IEnumerable<(BlogPostDocument Document, float[] Embedding)> documents, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        
        var segments = documents.Select(d => BlogPostToSegment(d.Document, d.Embedding)).ToList();
        
        if (segments.Count == 0) return;
        
        await _store.UpsertSegmentsAsync(_config.CollectionName, segments, cancellationToken);
        
        _logger.LogInformation("Indexed {Count} documents", segments.Count);
    }

    public async Task<List<SearchResult>> SearchAsync(float[] queryEmbedding, int limit = 10, float scoreThreshold = 0.5f, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        
        var segments = await _store.SearchAsync(_config.CollectionName, queryEmbedding, limit, docId: null, cancellationToken);
        
        return segments
            .Where(s => s.QuerySimilarity >= scoreThreshold)
            .Select(SegmentToSearchResult)
            .ToList();
    }

    public async Task<List<SearchResult>> FindRelatedPostsAsync(string slug, int limit = 5, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        
        // Sanitize slug to match stored doc_id format
        var sanitizedDocId = SanitizeSlugForDocId(slug);
        
        // Get the document's embedding
        var docSegments = await _store.GetDocumentSegmentsAsync(_config.CollectionName, sanitizedDocId, cancellationToken);
        var docSegment = docSegments.FirstOrDefault();
        
        if (docSegment?.Embedding == null)
        {
            _logger.LogDebug("No embedding found for slug {Slug} (docId: {DocId})", slug, sanitizedDocId);
            return new List<SearchResult>();
        }
        
        // Search for similar documents, excluding the original
        var allResults = await _store.SearchAsync(_config.CollectionName, docSegment.Embedding, limit + 1, docId: null, cancellationToken);
        
        return allResults
            .Where(s => ExtractSlugFromSegment(s) != slug) // Exclude self (using original slug format)
            .Take(limit)
            .Select(SegmentToSearchResult)
            .ToList();
    }

    public async Task DeleteDocumentAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var sanitizedDocId = SanitizeSlugForDocId(id);
        await _store.DeleteDocumentAsync(_config.CollectionName, sanitizedDocId, cancellationToken);
        _logger.LogDebug("Deleted document {Id}", id);
    }

    public async Task<string?> GetDocumentHashAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        
        var sanitizedDocId = SanitizeSlugForDocId(id);
        var segments = await _store.GetDocumentSegmentsAsync(_config.CollectionName, sanitizedDocId, cancellationToken);
        var segment = segments.FirstOrDefault();
        
        return segment?.ContentHash;
    }

    public async Task UpdateLanguagesAsync(string slug, string[] languages, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        
        var sanitizedDocId = SanitizeSlugForDocId(slug);
        
        // Get existing segment and recreate with updated languages
        var segments = await _store.GetDocumentSegmentsAsync(_config.CollectionName, sanitizedDocId, cancellationToken);
        var existingSegment = segments.FirstOrDefault();
        
        if (existingSegment != null)
        {
            // Create a new segment with updated SectionTitle (init-only property)
            // IMPORTANT: Preserve the ContentHash to avoid constant reindexing
            var updatedSegment = new Segment(
                slug,
                existingSegment.Text,
                existingSegment.Type,
                existingSegment.Index,
                existingSegment.StartChar,
                existingSegment.EndChar,
                existingSegment.ContentHash)  // Preserve original hash
            {
                SectionTitle = string.Join(",", languages),
                HeadingLevel = existingSegment.HeadingLevel,
                SalienceScore = existingSegment.SalienceScore,
                Embedding = existingSegment.Embedding
            };

            await _store.UpsertSegmentsAsync(_config.CollectionName, new[] { updatedSegment }, cancellationToken);
        }
    }

    public async Task AddLanguageAsync(string slug, string language, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        
        var sanitizedDocId = SanitizeSlugForDocId(slug);
        
        var segments = await _store.GetDocumentSegmentsAsync(_config.CollectionName, sanitizedDocId, cancellationToken);
        var existingSegment = segments.FirstOrDefault();
        
        if (existingSegment != null)
        {
            var existingLanguages = existingSegment.SectionTitle?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            if (!existingLanguages.Contains(language, StringComparer.OrdinalIgnoreCase))
            {
                var newLanguages = existingLanguages.Append(language).ToArray();

                // Create a new segment with updated SectionTitle (init-only property)
                // IMPORTANT: Preserve the ContentHash to avoid constant reindexing
                var updatedSegment = new Segment(
                    slug,
                    existingSegment.Text,
                    existingSegment.Type,
                    existingSegment.Index,
                    existingSegment.StartChar,
                    existingSegment.EndChar,
                    existingSegment.ContentHash)  // Preserve original hash
                {
                    SectionTitle = string.Join(",", newLanguages),
                    HeadingLevel = existingSegment.HeadingLevel,
                    SalienceScore = existingSegment.SalienceScore,
                    Embedding = existingSegment.Embedding
                };

                await _store.UpsertSegmentsAsync(_config.CollectionName, new[] { updatedSegment }, cancellationToken);
            }
        }
    }

    public async Task ClearCollectionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _store.DeleteCollectionAsync(_config.CollectionName, cancellationToken);
        _logger.LogInformation("Cleared collection '{Collection}'", _config.CollectionName);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await InitializeCollectionAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Convert BlogPostDocument to DocSummarizer Segment for storage
    /// </summary>
    private static Segment BlogPostToSegment(BlogPostDocument doc, float[] embedding)
    {
        // Ensure we have valid values - DuckDB parameter binding fails on nulls
        var slug = doc.Slug ?? throw new ArgumentNullException(nameof(doc), "BlogPostDocument.Slug cannot be null");
        var content = doc.Content ?? "";
        var contentHash = doc.ContentHash ?? "";
        
        // Use slug as the document ID for consistent lookups
        // Use the constructor overload that accepts contentHashOverride to preserve the original hash
        var segment = new Segment(slug, content, SegmentType.Sentence, 0, 0, content.Length, contentHash)
        {
            SectionTitle = string.Join(",", doc.Languages ?? Array.Empty<string>()),
            HeadingLevel = 0,
            SalienceScore = 1.0, // Blog posts are all equally important
            Embedding = embedding
        };
        
        return segment;
    }

    /// <summary>
    /// Convert DocSummarizer Segment back to SearchResult
    /// </summary>
    private static SearchResult SegmentToSearchResult(Segment segment)
    {
        return new SearchResult
        {
            Slug = ExtractSlugFromSegment(segment),
            Score = (float)segment.QuerySimilarity
        };
    }

    private static string ExtractSlugFromSegment(Segment segment)
    {
        // The segment ID format is "{docId}_{type}_{index}"
        // For blog posts, docId = sanitized slug, so we extract and restore it
        var parts = segment.Id.Split('_');
        if (parts.Length >= 3)
        {
            // Rejoin all parts except the last two (type and index)
            var sanitizedSlug = string.Join("_", parts.Take(parts.Length - 2));
            // Convert back to original slug format (underscores -> hyphens)
            return sanitizedSlug.Replace("_", "-");
        }
        return segment.Id;
    }
    
    /// <summary>
    /// Sanitize slug the same way Segment constructor does, for consistent lookups
    /// </summary>
    private static string SanitizeSlugForDocId(string slug)
    {
        // Match the Segment.SanitizeDocId behavior
        var sb = new System.Text.StringBuilder();
        foreach (var c in slug)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
            else if (c == '.' || c == '-' || c == ' ')
                sb.Append('_');
        }
        return sb.ToString().ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
    }

    public void Dispose()
    {
        _store.Dispose();
    }
}
