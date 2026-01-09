using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace LucidRAG.Services.Storage;

/// <summary>
/// Filesystem-based evidence storage.
/// Stores artifacts at: {basePath}/{entityId}/{artifactType}/{filename}
/// </summary>
public class FilesystemEvidenceStorage : IEvidenceStorage
{
    private readonly string _basePath;
    private readonly ILogger<FilesystemEvidenceStorage> _logger;

    public FilesystemEvidenceStorage(
        IOptions<EvidenceStorageOptions> options,
        ILogger<FilesystemEvidenceStorage> logger)
    {
        _basePath = options.Value.BasePath ?? Path.Combine(AppContext.BaseDirectory, "evidence");
        _logger = logger;

        // Ensure base directory exists
        Directory.CreateDirectory(_basePath);
    }

    public string ProviderName => "filesystem";

    public async Task<string> StoreAsync(
        Stream content,
        string path,
        string mimeType,
        CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var fileStream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        await content.CopyToAsync(fileStream, ct);

        _logger.LogDebug("Stored evidence at {Path} ({Size} bytes)", path, fileStream.Length);

        return path;
    }

    public async Task<Stream?> RetrieveAsync(
        string path,
        CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            _logger.LogDebug("Evidence not found at {Path}", path);
            return null;
        }

        // Return a read-only stream
        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        return await Task.FromResult<Stream>(stream);
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        return Task.FromResult(File.Exists(fullPath));
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            _logger.LogDebug("Deleted evidence at {Path}", path);

            // Try to clean up empty directories
            TryCleanupEmptyDirectories(fullPath);
        }

        return Task.CompletedTask;
    }

    public async Task<EvidenceStorageInfo?> GetInfoAsync(
        string path,
        CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            return null;
        }

        var fileInfo = new FileInfo(fullPath);

        // Compute hash
        string? hash = null;
        try
        {
            await using var stream = File.OpenRead(fullPath);
            var hashBytes = await SHA256.HashDataAsync(stream, ct);
            hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute hash for {Path}", path);
        }

        return new EvidenceStorageInfo(
            SizeBytes: fileInfo.Length,
            ContentHash: hash,
            LastModified: new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero),
            Metadata: null);
    }

    public Uri? GetPublicUri(string path, TimeSpan? expiry = null)
    {
        // Filesystem storage doesn't support public URIs
        // Files must be served through the API
        return null;
    }

    private string GetFullPath(string relativePath)
    {
        // Sanitize path to prevent directory traversal
        var sanitized = relativePath
            .Replace('\\', '/')
            .Split('/')
            .Where(p => !string.IsNullOrEmpty(p) && p != ".." && p != ".")
            .Aggregate(_basePath, Path.Combine);

        return sanitized;
    }

    private void TryCleanupEmptyDirectories(string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            while (!string.IsNullOrEmpty(directory) &&
                   directory != _basePath &&
                   directory.StartsWith(_basePath))
            {
                if (Directory.Exists(directory) &&
                    !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                    directory = Path.GetDirectoryName(directory);
                }
                else
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to cleanup empty directories");
        }
    }
}

/// <summary>
/// Configuration for evidence storage.
/// </summary>
public class EvidenceStorageOptions
{
    public const string SectionName = "EvidenceStorage";

    /// <summary>
    /// Base path for filesystem storage.
    /// Default: {AppContext.BaseDirectory}/evidence
    /// </summary>
    public string? BasePath { get; set; }

    /// <summary>
    /// Default storage provider: "filesystem", "s3".
    /// </summary>
    public string DefaultProvider { get; set; } = "filesystem";

    /// <summary>
    /// Days to retain temporary artifacts before cleanup.
    /// </summary>
    public int TemporaryArtifactRetentionDays { get; set; } = 7;

    /// <summary>
    /// Days to retain processing logs.
    /// </summary>
    public int ProcessingLogRetentionDays { get; set; } = 30;
}
