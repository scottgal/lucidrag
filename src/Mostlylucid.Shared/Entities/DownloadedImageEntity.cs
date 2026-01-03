namespace Mostlylucid.Shared.Entities;

/// <summary>
/// Tracks external images that have been downloaded and stored locally
/// </summary>
public class DownloadedImageEntity
{
    public int Id { get; set; }

    /// <summary>
    /// The blog post slug this image belongs to
    /// </summary>
    public string PostSlug { get; set; } = string.Empty;

    /// <summary>
    /// The original external URL of the image
    /// </summary>
    public string OriginalUrl { get; set; } = string.Empty;

    /// <summary>
    /// The local filename (format: slug-imagename.ext)
    /// </summary>
    public string LocalFileName { get; set; } = string.Empty;

    /// <summary>
    /// When the image was downloaded
    /// </summary>
    public DateTimeOffset DownloadedDate { get; set; }

    /// <summary>
    /// Last time we verified this image is still referenced in the post
    /// </summary>
    public DateTimeOffset LastVerifiedDate { get; set; }

    /// <summary>
    /// File size in bytes
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// Content type (e.g., image/jpeg, image/png)
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// Width of the image (if available)
    /// </summary>
    public int? Width { get; set; }

    /// <summary>
    /// Height of the image (if available)
    /// </summary>
    public int? Height { get; set; }
}
