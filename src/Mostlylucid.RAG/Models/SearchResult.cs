namespace Mostlylucid.RAG.Models;

/// <summary>
/// Represents a semantic search result - just the slug and similarity score.
/// Actual post details should be looked up from PostgreSQL.
/// </summary>
public class SearchResult
{
    /// <summary>
    /// Blog post slug
    /// </summary>
    public required string Slug { get; set; }

    /// <summary>
    /// Similarity score (0-1, higher is more similar)
    /// </summary>
    public float Score { get; set; }
}
