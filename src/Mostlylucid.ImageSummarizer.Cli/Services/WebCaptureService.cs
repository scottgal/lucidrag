using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace Mostlylucid.ImageSummarizer.Cli.Services;

/// <summary>
/// Service for capturing images from URLs - both direct images and web page screenshots.
/// </summary>
public class WebCaptureService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _cacheDir;

    // Common image extensions
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tiff", ".tif", ".svg"
    };

    // Image content types
    private static readonly HashSet<string> ImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp", "image/bmp",
        "image/tiff", "image/svg+xml"
    };

    public WebCaptureService(string? cacheDir = null)
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        _cacheDir = cacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LucidRAG", "web-cache");

        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>
    /// Check if input looks like a URL
    /// </summary>
    public static bool IsUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        return input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               input.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Capture image from URL - either direct download or web page screenshot
    /// Returns local file path and metadata about the source.
    /// </summary>
    public async Task<WebCaptureResult> CaptureAsync(string url, CancellationToken ct = default)
    {
        var uri = new Uri(url);
        var isDirectImage = IsDirectImageUrl(uri);

        if (isDirectImage)
        {
            return await DownloadImageAsync(uri, ct);
        }
        else
        {
            return await CaptureWebPageAsync(uri, ct);
        }
    }

    /// <summary>
    /// Download a direct image URL
    /// </summary>
    private async Task<WebCaptureResult> DownloadImageAsync(Uri uri, CancellationToken ct)
    {
        var response = await _httpClient.GetAsync(uri, ct);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/unknown";
        var extension = GetExtensionFromContentType(contentType) ??
                       Path.GetExtension(uri.LocalPath) ??
                       ".jpg";

        // Generate cache filename from URL hash
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(uri.ToString()))).Substring(0, 16);
        var fileName = $"img_{hash}{extension}";
        var localPath = Path.Combine(_cacheDir, fileName);

        await using var fileStream = File.Create(localPath);
        await response.Content.CopyToAsync(fileStream, ct);

        return new WebCaptureResult
        {
            LocalPath = localPath,
            SourceUrl = uri.ToString(),
            CaptureType = CaptureType.DirectImage,
            ContentType = contentType,
            FileSize = new FileInfo(localPath).Length
        };
    }

    /// <summary>
    /// Capture screenshot of a web page using Puppeteer (via node subprocess)
    /// </summary>
    private async Task<WebCaptureResult> CaptureWebPageAsync(Uri uri, CancellationToken ct)
    {
        // Check if puppeteer capture script exists
        var scriptPath = FindPuppeteerScript();

        if (scriptPath == null)
        {
            // Fall back to trying to find any image on the page
            return await TryExtractImageFromPageAsync(uri, ct);
        }

        // Generate output filename
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(uri.ToString()))).Substring(0, 16);
        var fileName = $"page_{hash}.png";
        var localPath = Path.Combine(_cacheDir, fileName);

        // Run puppeteer script
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            Arguments = $"\"{scriptPath}\" \"{uri}\" \"{localPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("Failed to start node process");

            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(ct);
                throw new InvalidOperationException($"Puppeteer capture failed: {error}");
            }

            if (!File.Exists(localPath))
                throw new InvalidOperationException("Screenshot file was not created");

            return new WebCaptureResult
            {
                LocalPath = localPath,
                SourceUrl = uri.ToString(),
                CaptureType = CaptureType.WebPageScreenshot,
                ContentType = "image/png",
                FileSize = new FileInfo(localPath).Length,
                PageTitle = await ExtractPageTitleAsync(uri, ct)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // If puppeteer fails, try to extract images from page
            Console.Error.WriteLine($"Web capture failed: {ex.Message}. Trying image extraction...");
            return await TryExtractImageFromPageAsync(uri, ct);
        }
    }

    /// <summary>
    /// Try to extract the first significant image from a web page
    /// </summary>
    private async Task<WebCaptureResult> TryExtractImageFromPageAsync(Uri uri, CancellationToken ct)
    {
        var html = await _httpClient.GetStringAsync(uri, ct);

        // Extract og:image or first img src
        var ogImageMatch = Regex.Match(html, @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']",
            RegexOptions.IgnoreCase);
        var imgMatch = Regex.Match(html, @"<img[^>]+src=[""']([^""']+\.(jpg|jpeg|png|gif|webp))[""']",
            RegexOptions.IgnoreCase);

        string? imageUrl = null;

        if (ogImageMatch.Success)
        {
            imageUrl = ogImageMatch.Groups[1].Value;
        }
        else if (imgMatch.Success)
        {
            imageUrl = imgMatch.Groups[1].Value;
        }

        if (string.IsNullOrEmpty(imageUrl))
        {
            throw new InvalidOperationException(
                $"Could not find any images on {uri}. " +
                "Install Node.js and Puppeteer for full web page screenshots: npm install -g puppeteer");
        }

        // Resolve relative URLs
        if (!imageUrl.StartsWith("http"))
        {
            imageUrl = new Uri(uri, imageUrl).ToString();
        }

        // Extract alt text if available
        string? altText = null;
        if (imgMatch.Success)
        {
            var altMatch = Regex.Match(imgMatch.Value, @"alt=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
            if (altMatch.Success)
            {
                altText = altMatch.Groups[1].Value;
            }
        }

        var result = await DownloadImageAsync(new Uri(imageUrl), ct);
        result.CaptureType = CaptureType.ExtractedImage;
        result.OriginalAltText = altText;
        result.PageUrl = uri.ToString();

        return result;
    }

    /// <summary>
    /// Extract page title from HTML
    /// </summary>
    private async Task<string?> ExtractPageTitleAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            var html = await _httpClient.GetStringAsync(uri, ct);
            var titleMatch = Regex.Match(html, @"<title>([^<]+)</title>", RegexOptions.IgnoreCase);
            return titleMatch.Success ? System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim()) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Check if URL appears to be a direct image link
    /// </summary>
    private bool IsDirectImageUrl(Uri uri)
    {
        var path = uri.LocalPath.ToLowerInvariant();
        return ImageExtensions.Any(ext => path.EndsWith(ext));
    }

    /// <summary>
    /// Get file extension from content type
    /// </summary>
    private static string? GetExtensionFromContentType(string contentType)
    {
        return contentType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tiff",
            "image/svg+xml" => ".svg",
            _ => null
        };
    }

    /// <summary>
    /// Find puppeteer capture script in known locations
    /// </summary>
    private string? FindPuppeteerScript()
    {
        var locations = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "scripts", "capture-page.mjs"),
            Path.Combine(AppContext.BaseDirectory, "capture-page.mjs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LucidRAG", "scripts", "capture-page.mjs")
        };

        return locations.FirstOrDefault(File.Exists);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

/// <summary>
/// Result of a web capture operation
/// </summary>
public class WebCaptureResult
{
    /// <summary>
    /// Local file path to the captured image
    /// </summary>
    public required string LocalPath { get; set; }

    /// <summary>
    /// Original source URL
    /// </summary>
    public required string SourceUrl { get; set; }

    /// <summary>
    /// How the image was captured
    /// </summary>
    public CaptureType CaptureType { get; set; }

    /// <summary>
    /// Content type of the captured image
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// File size in bytes
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// Page title (for web page screenshots)
    /// </summary>
    public string? PageTitle { get; set; }

    /// <summary>
    /// Original alt text from the image element (if extracted)
    /// </summary>
    public string? OriginalAltText { get; set; }

    /// <summary>
    /// Page URL (for extracted images)
    /// </summary>
    public string? PageUrl { get; set; }
}

/// <summary>
/// Type of capture performed
/// </summary>
public enum CaptureType
{
    /// <summary>
    /// Direct image download
    /// </summary>
    DirectImage,

    /// <summary>
    /// Full web page screenshot
    /// </summary>
    WebPageScreenshot,

    /// <summary>
    /// Image extracted from web page (og:image or first img)
    /// </summary>
    ExtractedImage
}
