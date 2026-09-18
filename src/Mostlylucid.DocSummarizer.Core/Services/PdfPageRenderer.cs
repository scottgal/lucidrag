using Microsoft.Extensions.Logging;
using PDFtoImage;
using SkiaSharp;
using System.Runtime.Versioning;

namespace Mostlylucid.DocSummarizer.Core.Services;

/// <summary>
///     Renders PDF pages as images for VLM-based OCR processing.
///     Uses PDFium via PDFtoImage for high-quality rendering.
/// </summary>
public class PdfPageRenderer
{
    private readonly ILogger<PdfPageRenderer>? _logger;
    private readonly string _outputDirectory;

    public PdfPageRenderer(ILogger<PdfPageRenderer>? logger = null)
    {
        _logger = logger;
        _outputDirectory = Path.Combine(Path.GetTempPath(), "lucidrag", "pdf_pages");
        Directory.CreateDirectory(_outputDirectory);
    }

    /// <summary>
    ///     Default DPI for rendering (300 is good for OCR)
    /// </summary>
    public int DefaultDpi { get; set; } = 300;

    /// <summary>
    ///     Maximum dimension (width or height) before downscaling
    /// </summary>
    public int MaxDimension { get; set; } = 4096;

    [SupportedOSPlatformGuard("windows")]
    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("macos")]
    [SupportedOSPlatformGuard("android31.0")]
    [SupportedOSPlatformGuard("ios13.6")]
    [SupportedOSPlatformGuard("maccatalyst13.5")]
    private static bool IsRenderingSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() ||
        OperatingSystem.IsAndroidVersionAtLeast(31) || OperatingSystem.IsIOSVersionAtLeast(13, 6) ||
        OperatingSystem.IsMacCatalystVersionAtLeast(13, 5);

    /// <summary>
    ///     Get the number of pages in a PDF document.
    /// </summary>
    public int GetPageCount(string pdfPath)
    {
        if (!IsRenderingSupported) throw new PlatformNotSupportedException("PDF rendering is not supported on this platform.");
        using var stream = File.OpenRead(pdfPath);
        return Conversion.GetPageCount(stream);
    }

    /// <summary>
    ///     Render a single page to an image file.
    /// </summary>
    /// <param name="pdfPath">Path to the PDF file</param>
    /// <param name="pageIndex">Zero-based page index</param>
    /// <param name="dpi">Resolution (default: 300)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Path to the rendered image</returns>
    public async Task<string> RenderPageAsync(
        string pdfPath,
        int pageIndex,
        int? dpi = null,
        CancellationToken ct = default)
    {
        if (!IsRenderingSupported) throw new PlatformNotSupportedException("PDF rendering is not supported on this platform.");
        var effectiveDpi = dpi ?? DefaultDpi;
        var baseName = Path.GetFileNameWithoutExtension(pdfPath);
        var outputPath = Path.Combine(_outputDirectory, $"{baseName}_page_{pageIndex + 1}.png");

        _logger?.LogDebug("Rendering page {Page} of {Pdf} at {Dpi} DPI",
            pageIndex + 1, Path.GetFileName(pdfPath), effectiveDpi);

        await Task.Run(() =>
        {
            if (!IsRenderingSupported) throw new PlatformNotSupportedException("PDF rendering is not supported on this platform.");
            using var inputStream = File.OpenRead(pdfPath);
            var options = new RenderOptions(effectiveDpi);
            // Use Index for page parameter (PDFtoImage 5.x API)
            using var bitmap = Conversion.ToImage(inputStream, pageIndex, options: options);

            // Downscale if too large
            var finalBitmap = DownscaleIfNeeded(bitmap);

            using var outputStream = File.Create(outputPath);
            finalBitmap.Encode(outputStream, SKEncodedImageFormat.Png, 90);

            if (finalBitmap != bitmap)
                finalBitmap.Dispose();
        }, ct);

        _logger?.LogDebug("Rendered page {Page} to {Path}", pageIndex + 1, outputPath);
        return outputPath;
    }

    /// <summary>
    ///     Render all pages of a PDF to images.
    /// </summary>
    /// <param name="pdfPath">Path to the PDF file</param>
    /// <param name="dpi">Resolution (default: 300)</param>
    /// <param name="progress">Optional progress callback</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of rendered image paths in page order</returns>
    public async Task<List<RenderedPage>> RenderAllPagesAsync(
        string pdfPath,
        int? dpi = null,
        IProgress<(int current, int total)>? progress = null,
        CancellationToken ct = default)
    {
        var pageCount = GetPageCount(pdfPath);
        var results = new List<RenderedPage>();

        _logger?.LogInformation("Rendering {Count} pages from {Pdf}", pageCount, Path.GetFileName(pdfPath));

        for (var i = 0; i < pageCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            var imagePath = await RenderPageAsync(pdfPath, i, dpi, ct);
            results.Add(new RenderedPage
            {
                PageNumber = i + 1,
                ImagePath = imagePath,
                SourcePdf = pdfPath
            });

            progress?.Report((i + 1, pageCount));
        }

        _logger?.LogInformation("Rendered {Count} pages from {Pdf}", results.Count, Path.GetFileName(pdfPath));
        return results;
    }

    /// <summary>
    ///     Render specific pages of a PDF.
    /// </summary>
    /// <param name="pdfPath">Path to the PDF file</param>
    /// <param name="pageNumbers">One-based page numbers to render</param>
    /// <param name="dpi">Resolution (default: 300)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of rendered image paths</returns>
    public async Task<List<RenderedPage>> RenderPagesAsync(
        string pdfPath,
        IEnumerable<int> pageNumbers,
        int? dpi = null,
        CancellationToken ct = default)
    {
        var pageList = pageNumbers.ToList();
        var results = new List<RenderedPage>();

        foreach (var pageNum in pageList)
        {
            ct.ThrowIfCancellationRequested();

            var imagePath = await RenderPageAsync(pdfPath, pageNum - 1, dpi, ct);
            results.Add(new RenderedPage
            {
                PageNumber = pageNum,
                ImagePath = imagePath,
                SourcePdf = pdfPath
            });
        }

        return results;
    }

    /// <summary>
    ///     Downscale bitmap if it exceeds maximum dimensions.
    /// </summary>
    private SKBitmap DownscaleIfNeeded(SKBitmap bitmap)
    {
        if (bitmap.Width <= MaxDimension && bitmap.Height <= MaxDimension)
            return bitmap;

        var scale = Math.Min(
            (float)MaxDimension / bitmap.Width,
            (float)MaxDimension / bitmap.Height);

        var newWidth = (int)(bitmap.Width * scale);
        var newHeight = (int)(bitmap.Height * scale);

        _logger?.LogDebug("Downscaling from {W}x{H} to {NW}x{NH}",
            bitmap.Width, bitmap.Height, newWidth, newHeight);

        var resized = new SKBitmap(newWidth, newHeight);
        bitmap.ScalePixels(resized, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return resized;
    }

    /// <summary>
    ///     Clean up rendered page images.
    /// </summary>
    public void CleanupRenderedPages(IEnumerable<RenderedPage> pages)
    {
        foreach (var page in pages)
            try
            {
                if (File.Exists(page.ImagePath))
                    File.Delete(page.ImagePath);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to cleanup rendered page: {Path}", page.ImagePath);
            }
    }
}

/// <summary>
///     Represents a rendered PDF page.
/// </summary>
public record RenderedPage
{
    /// <summary>One-based page number</summary>
    public int PageNumber { get; init; }

    /// <summary>Path to the rendered image file</summary>
    public required string ImagePath { get; init; }

    /// <summary>Source PDF path</summary>
    public required string SourcePdf { get; init; }
}
