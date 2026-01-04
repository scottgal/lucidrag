using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Memory-efficient image stream processor with automatic downscaling for large images.
/// Prevents OOM issues by streaming and resizing on-the-fly.
/// </summary>
public class ImageStreamProcessor
{
    private readonly ImageConfig _config;
    private readonly ILogger<ImageStreamProcessor>? _logger;

    // Default limits (can be overridden by config)
    private const int DefaultMaxWidth = 4096;
    private const int DefaultMaxHeight = 4096;
    private const long DefaultMaxPixels = 16_777_216; // 4096 * 4096 = 16 megapixels
    private const long DefaultMaxFileSize = 50_000_000; // 50 MB

    public ImageStreamProcessor(IOptions<ImageConfig> config, ILogger<ImageStreamProcessor>? logger = null)
    {
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    /// Load image with automatic downscaling if needed.
    /// Uses streaming to avoid loading entire file into memory.
    /// </summary>
    public async Task<Image<Rgba32>> LoadImageSafelyAsync(
        string imagePath,
        CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(imagePath);

        // Check file size first (before reading)
        var maxFileSize = _config.MaxImageFileSize ?? DefaultMaxFileSize;
        if (fileInfo.Length > maxFileSize)
        {
            _logger?.LogWarning(
                "Image file size {Size} MB exceeds limit {Limit} MB, will downscale: {Path}",
                fileInfo.Length / 1_000_000.0,
                maxFileSize / 1_000_000.0,
                imagePath);
        }

        // Open stream (doesn't load entire file yet)
        await using var stream = File.OpenRead(imagePath);

        // Peek at format to get dimensions without full decode
        var imageInfo = await Image.IdentifyAsync(stream, ct);
        if (imageInfo == null)
        {
            throw new InvalidOperationException($"Could not identify image format: {imagePath}");
        }

        // Determine if we need to downscale
        var maxWidth = _config.MaxImageWidth ?? DefaultMaxWidth;
        var maxHeight = _config.MaxImageHeight ?? DefaultMaxHeight;
        var maxPixels = _config.MaxImagePixels ?? DefaultMaxPixels;

        var needsDownscale = imageInfo.Width > maxWidth ||
                            imageInfo.Height > maxHeight ||
                            (imageInfo.Width * imageInfo.Height) > maxPixels;

        // Reset stream position
        stream.Position = 0;

        if (!needsDownscale)
        {
            // Load normally - fits within limits
            _logger?.LogDebug(
                "Loading image {Width}x{Height} ({Pixels} px) without downscaling",
                imageInfo.Width,
                imageInfo.Height,
                imageInfo.Width * imageInfo.Height);

            return await Image.LoadAsync<Rgba32>(stream, ct);
        }

        // Calculate target dimensions
        var (targetWidth, targetHeight) = CalculateTargetDimensions(
            imageInfo.Width,
            imageInfo.Height,
            maxWidth,
            maxHeight,
            maxPixels);

        _logger?.LogInformation(
            "Downscaling large image from {OrigWidth}x{OrigHeight} to {TargetWidth}x{TargetHeight}",
            imageInfo.Width,
            imageInfo.Height,
            targetWidth,
            targetHeight);

        // Load and resize in one operation (memory efficient)
        using var originalImage = await Image.LoadAsync<Rgba32>(stream, ct);

        var resized = originalImage.Clone(ctx => ctx.Resize(targetWidth, targetHeight));

        return resized;
    }

    /// <summary>
    /// Load image metadata without decoding pixels (ultra-fast, minimal memory).
    /// </summary>
    public async Task<ImageInfo> IdentifyImageAsync(
        string imagePath,
        CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(imagePath);

        var info = await Image.IdentifyAsync(stream, ct);
        if (info == null)
        {
            throw new InvalidOperationException($"Could not identify image: {imagePath}");
        }

        return info;
    }

    /// <summary>
    /// Process image with callback, auto-downscaling if needed.
    /// Disposes image after callback completes.
    /// </summary>
    public async Task<T> ProcessImageAsync<T>(
        string imagePath,
        Func<Image<Rgba32>, Task<T>> processor,
        CancellationToken ct = default)
    {
        using var image = await LoadImageSafelyAsync(imagePath, ct);
        return await processor(image);
    }

    /// <summary>
    /// Process image stream without loading into memory (for certain operations).
    /// </summary>
    public async Task<T> ProcessImageStreamAsync<T>(
        string imagePath,
        Func<Stream, Task<T>> processor,
        CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(imagePath);
        return await processor(stream);
    }

    /// <summary>
    /// Calculate target dimensions that fit within all constraints.
    /// Maintains aspect ratio.
    /// </summary>
    private static (int width, int height) CalculateTargetDimensions(
        int originalWidth,
        int originalHeight,
        int maxWidth,
        int maxHeight,
        long maxPixels)
    {
        var aspectRatio = originalWidth / (double)originalHeight;

        // Start with max dimensions
        var targetWidth = Math.Min(originalWidth, maxWidth);
        var targetHeight = Math.Min(originalHeight, maxHeight);

        // Constrain by aspect ratio
        if (targetWidth / aspectRatio > targetHeight)
        {
            targetWidth = (int)(targetHeight * aspectRatio);
        }
        else
        {
            targetHeight = (int)(targetWidth / aspectRatio);
        }

        // Constrain by total pixel count
        var currentPixels = targetWidth * targetHeight;
        if (currentPixels > maxPixels)
        {
            var scaleFactor = Math.Sqrt(maxPixels / (double)currentPixels);
            targetWidth = (int)(targetWidth * scaleFactor);
            targetHeight = (int)(targetHeight * scaleFactor);
        }

        // Ensure at least 1 pixel in each dimension
        targetWidth = Math.Max(1, targetWidth);
        targetHeight = Math.Max(1, targetHeight);

        return (targetWidth, targetHeight);
    }

    /// <summary>
    /// Get recommended processing size for an image.
    /// Returns original dimensions if within limits, otherwise scaled dimensions.
    /// </summary>
    public async Task<ImageProcessingInfo> GetProcessingInfoAsync(
        string imagePath,
        CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(imagePath);
        var imageInfo = await IdentifyImageAsync(imagePath, ct);

        var maxWidth = _config.MaxImageWidth ?? DefaultMaxWidth;
        var maxHeight = _config.MaxImageHeight ?? DefaultMaxHeight;
        var maxPixels = _config.MaxImagePixels ?? DefaultMaxPixels;
        var maxFileSize = _config.MaxImageFileSize ?? DefaultMaxFileSize;

        var needsDownscale = imageInfo.Width > maxWidth ||
                            imageInfo.Height > maxHeight ||
                            (imageInfo.Width * imageInfo.Height) > maxPixels ||
                            fileInfo.Length > maxFileSize;

        if (!needsDownscale)
        {
            return new ImageProcessingInfo
            {
                OriginalWidth = imageInfo.Width,
                OriginalHeight = imageInfo.Height,
                ProcessingWidth = imageInfo.Width,
                ProcessingHeight = imageInfo.Height,
                WillDownscale = false,
                FileSize = fileInfo.Length,
                Format = imageInfo.Metadata.DecodedImageFormat?.Name ?? "Unknown"
            };
        }

        var (targetWidth, targetHeight) = CalculateTargetDimensions(
            imageInfo.Width,
            imageInfo.Height,
            maxWidth,
            maxHeight,
            maxPixels);

        return new ImageProcessingInfo
        {
            OriginalWidth = imageInfo.Width,
            OriginalHeight = imageInfo.Height,
            ProcessingWidth = targetWidth,
            ProcessingHeight = targetHeight,
            WillDownscale = true,
            DownscaleReason = DetermineDownscaleReason(imageInfo, fileInfo, maxWidth, maxHeight, maxPixels, maxFileSize),
            FileSize = fileInfo.Length,
            Format = imageInfo.Metadata.DecodedImageFormat?.Name ?? "Unknown"
        };
    }

    private static string DetermineDownscaleReason(
        ImageInfo imageInfo,
        FileInfo fileInfo,
        int maxWidth,
        int maxHeight,
        long maxPixels,
        long maxFileSize)
    {
        var reasons = new List<string>();

        if (imageInfo.Width > maxWidth)
            reasons.Add($"width {imageInfo.Width} > {maxWidth}");
        if (imageInfo.Height > maxHeight)
            reasons.Add($"height {imageInfo.Height} > {maxHeight}");
        if ((imageInfo.Width * imageInfo.Height) > maxPixels)
            reasons.Add($"pixels {imageInfo.Width * imageInfo.Height} > {maxPixels}");
        if (fileInfo.Length > maxFileSize)
            reasons.Add($"file size {fileInfo.Length / 1_000_000}MB > {maxFileSize / 1_000_000}MB");

        return string.Join(", ", reasons);
    }
}

/// <summary>
/// Information about how an image will be processed.
/// </summary>
public class ImageProcessingInfo
{
    public int OriginalWidth { get; set; }
    public int OriginalHeight { get; set; }
    public int ProcessingWidth { get; set; }
    public int ProcessingHeight { get; set; }
    public bool WillDownscale { get; set; }
    public string? DownscaleReason { get; set; }
    public long FileSize { get; set; }
    public string Format { get; set; } = "Unknown";

    public double ScaleFactor => ProcessingWidth / (double)OriginalWidth;
    public long OriginalPixels => OriginalWidth * OriginalHeight;
    public long ProcessingPixels => ProcessingWidth * ProcessingHeight;
    public double MemorySavings => WillDownscale ? (1.0 - (ProcessingPixels / (double)OriginalPixels)) : 0;
}
