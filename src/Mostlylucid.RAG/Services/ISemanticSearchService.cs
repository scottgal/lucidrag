using Mostlylucid.RAG.Models;

namespace Mostlylucid.RAG.Services;

/// <summary>
/// High-level semantic search service for blog posts
/// </summary>
public interface ISemanticSearchService
{
    /// <summary>
    /// Initialize the semantic search system
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Index a blog post for semantic search
    /// </summary>
    /// <param name="document">Blog post document to index</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task IndexPostAsync(BlogPostDocument document, CancellationToken cancellationToken = default);

    /// <summary>
    /// Index multiple blog posts in batch
    /// </summary>
    /// <param name="documents">Blog post documents to index</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task IndexPostsAsync(IEnumerable<BlogPostDocument> documents, CancellationToken cancellationToken = default);

    /// <summary>
    /// Search for blog posts using natural language query
    /// </summary>
    /// <param name="query">Search query</param>
    /// <param name="limit">Maximum number of results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<List<SearchResult>> SearchAsync(string query, int limit = 10, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get related posts for a specific blog post
    /// </summary>
    /// <param name="slug">Blog post slug</param>
    /// <param name="limit">Maximum number of related posts</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<List<SearchResult>> GetRelatedPostsAsync(string slug, int limit = 5, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a blog post from the index
    /// </summary>
    /// <param name="slug">Blog post slug</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeletePostAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a post needs re-indexing based on content hash
    /// </summary>
    /// <param name="slug">Blog post slug</param>
    /// <param name="currentHash">Current content hash</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<bool> NeedsReindexingAsync(string slug, string currentHash, CancellationToken cancellationToken = default);
}
