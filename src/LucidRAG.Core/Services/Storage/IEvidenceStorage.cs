namespace LucidRAG.Services.Storage;

/// <summary>
/// Abstraction for evidence artifact storage.
/// Implementations: filesystem, S3, Azure Blob, etc.
/// </summary>
public interface IEvidenceStorage
{
    /// <summary>
    /// Provider name for identification (e.g., "filesystem", "s3").
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Store content at the specified path.
    /// </summary>
    /// <param name="content">Content stream to store.</param>
    /// <param name="path">Relative path within storage (e.g., "{entityId}/{type}/{filename}").</param>
    /// <param name="mimeType">MIME type of the content.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The full path/key where content was stored.</returns>
    Task<string> StoreAsync(
        Stream content,
        string path,
        string mimeType,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieve content from the specified path.
    /// </summary>
    /// <param name="path">Path to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Content stream, or null if not found.</returns>
    Task<Stream?> RetrieveAsync(
        string path,
        CancellationToken ct = default);

    /// <summary>
    /// Check if content exists at the specified path.
    /// </summary>
    Task<bool> ExistsAsync(
        string path,
        CancellationToken ct = default);

    /// <summary>
    /// Delete content at the specified path.
    /// </summary>
    Task DeleteAsync(
        string path,
        CancellationToken ct = default);

    /// <summary>
    /// Get storage info for an artifact.
    /// </summary>
    Task<EvidenceStorageInfo?> GetInfoAsync(
        string path,
        CancellationToken ct = default);

    /// <summary>
    /// Generate a public/presigned URL for direct download.
    /// May return null if not supported by the provider.
    /// </summary>
    /// <param name="path">Path to the artifact.</param>
    /// <param name="expiry">How long the URL should be valid.</param>
    /// <returns>Download URL or null.</returns>
    Uri? GetPublicUri(
        string path,
        TimeSpan? expiry = null);
}

/// <summary>
/// Information about stored evidence.
/// </summary>
public record EvidenceStorageInfo(
    long SizeBytes,
    string? ContentHash,
    DateTimeOffset LastModified,
    Dictionary<string, string>? Metadata);
