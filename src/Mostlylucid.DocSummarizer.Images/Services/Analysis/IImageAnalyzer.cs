using Mostlylucid.DocSummarizer.Images.Models;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Main interface for deterministic image analysis.
/// Produces an ImageProfile containing measured facts about an image.
/// </summary>
public interface IImageAnalyzer
{
    /// <summary>
    /// Analyze an image file and produce a deterministic profile
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Image profile with measured properties</returns>
    Task<ImageProfile> AnalyzeAsync(string imagePath, CancellationToken ct = default);

    /// <summary>
    /// Analyze image bytes and produce a deterministic profile
    /// </summary>
    /// <param name="imageBytes">Image file bytes</param>
    /// <param name="fileName">Original filename (for format detection)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Image profile with measured properties</returns>
    Task<ImageProfile> AnalyzeAsync(byte[] imageBytes, string fileName, CancellationToken ct = default);

    /// <summary>
    /// Generate a perceptual hash for deduplication
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Perceptual hash string</returns>
    Task<string> GeneratePerceptualHashAsync(string imagePath, CancellationToken ct = default);

    /// <summary>
    /// Generate a thumbnail for the image
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <param name="maxSize">Maximum dimension (width or height)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Thumbnail bytes (WebP format)</returns>
    Task<byte[]> GenerateThumbnailAsync(string imagePath, int maxSize = 256, CancellationToken ct = default);
}
