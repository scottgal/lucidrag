namespace Mostlylucid.RAG.Models;

/// <summary>
/// Represents a blog post document for indexing in the vector database.
/// Only contains what's needed for embedding generation - actual post details come from PostgreSQL.
/// Includes PublishedDate and Languages array for filtering purposes.
/// </summary>
public class BlogPostDocument
{
    /// <summary>
    /// Unique identifier (the slug)
    /// </summary>
    public required string Id { get; set; }

    /// <summary>
    /// Blog post slug
    /// </summary>
    public required string Slug { get; set; }

    /// <summary>
    /// Blog post title (used for embedding weighting)
    /// </summary>
    public required string Title { get; set; }

    /// <summary>
    /// Plain text content (for embedding generation)
    /// </summary>
    public required string Content { get; set; }

    /// <summary>
    /// Content hash (to detect changes and avoid re-indexing)
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// Published date (for filtering search results by date range)
    /// </summary>
    public DateTimeOffset PublishedDate { get; set; }

    /// <summary>
    /// Available languages for this post (for filtering by language availability)
    /// Example: ["en", "fr", "es", "de"] means the post has English, French, Spanish, German translations
    /// </summary>
    public string[] Languages { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Categories for this post (for filtering search results by category)
    /// Example: ["ASP.NET", "HTMX", "Docker"]
    /// </summary>
    public string[] Categories { get; set; } = Array.Empty<string>();
}
