using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
///     Document handler that uses Docling service for extraction.
///     Supports PDF, DOCX, PPTX, XLSX, HTML with advanced layout analysis,
///     table extraction, and OCR capabilities.
///     Higher priority than native handlers when enabled.
/// </summary>
public class DoclingDocumentHandler : IDocumentHandler, IAsyncDisposable
{
    private readonly DoclingConfig _config;
    private readonly ILogger<DoclingDocumentHandler>? _logger;
    private readonly Lazy<DoclingClient> _clientLazy;
    private bool? _isAvailable;
    private DateTime _lastAvailabilityCheck = DateTime.MinValue;
    private static readonly TimeSpan AvailabilityCacheDuration = TimeSpan.FromMinutes(5);

    public DoclingDocumentHandler(
        IOptions<DocSummarizerConfig> config,
        ILogger<DoclingDocumentHandler>? logger = null)
    {
        _config = config.Value.Docling;
        _logger = logger;
        _clientLazy = new Lazy<DoclingClient>(() => new DoclingClient(_config));
    }

    private DoclingClient Client => _clientLazy.Value;

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedExtensions => _config.Documents.Extensions;

    /// <inheritdoc />
    public int Priority => _config.Documents.Priority;

    /// <inheritdoc />
    public string HandlerName => "Docling";

    /// <inheritdoc />
    public bool CanHandle(string filePath)
    {
        // Check if Docling is enabled
        if (!_config.Enabled || !_config.Documents.Enabled)
            return false;

        // Check if file exists
        if (!File.Exists(filePath))
            return false;

        // Check if extension is supported
        var extension = Path.GetExtension(filePath);
        if (!_config.Documents.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return false;

        // Check if service is available (with caching to avoid hammering the service)
        return IsServiceAvailable();
    }

    /// <inheritdoc />
    public async Task<DocumentContent> ProcessAsync(string filePath, DocumentHandlerOptions options)
    {
        _logger?.LogDebug("Processing document with Docling: {FilePath}", filePath);

        try
        {
            var startTime = DateTime.UtcNow;

            // Set up progress callback if verbose
            if (options.Verbose)
            {
                Client.OnProgress = progress =>
                {
                    _logger?.LogDebug(
                        "Docling progress: {Status} ({Percent:F1}%)",
                        progress.Status,
                        progress.Percent);
                };
            }

            // Convert document to markdown
            var markdown = await Client.ConvertAsync(filePath, options.CancellationToken);

            var processingTime = DateTime.UtcNow - startTime;

            if (string.IsNullOrWhiteSpace(markdown))
            {
                _logger?.LogWarning("Docling returned empty content for: {FilePath}", filePath);
                return CreateErrorResult(filePath, "Docling returned empty content");
            }

            _logger?.LogInformation(
                "Docling processed document: {FilePath} ({Chars} chars in {TimeMs}ms)",
                filePath,
                markdown.Length,
                processingTime.TotalMilliseconds);

            // Extract title from markdown
            var title = ExtractTitleFromMarkdown(markdown) ?? Path.GetFileNameWithoutExtension(filePath);

            // Detect content type from extension
            var contentType = DetectContentType(filePath);

            return new DocumentContent
            {
                Markdown = markdown,
                Title = title,
                ContentType = contentType,
                Metadata = new Dictionary<string, object>
                {
                    ["extractionMethod"] = "docling",
                    ["processingTimeMs"] = processingTime.TotalMilliseconds,
                    ["hasGpu"] = Client.HasGpu ?? false,
                    ["doclingBaseUrl"] = _config.BaseUrl
                }
            };
        }
        catch (TimeoutException ex)
        {
            _logger?.LogWarning(ex, "Docling timeout for: {FilePath}", filePath);
            return CreateErrorResult(filePath, $"Docling timeout: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "Docling service unavailable for: {FilePath}", filePath);
            // Mark service as unavailable so CanHandle returns false
            _isAvailable = false;
            _lastAvailabilityCheck = DateTime.UtcNow;
            return CreateErrorResult(filePath, $"Docling service error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Docling processing failed for: {FilePath}", filePath);
            return CreateErrorResult(filePath, $"Docling error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Check if the Docling service is available (with caching).
    /// </summary>
    private bool IsServiceAvailable()
    {
        // Use cached result if recent
        if (_isAvailable.HasValue && DateTime.UtcNow - _lastAvailabilityCheck < AvailabilityCacheDuration)
            return _isAvailable.Value;

        // Check availability asynchronously but return cached/default value
        // This prevents blocking on every CanHandle call
        _ = CheckAvailabilityAsync();

        // Return cached value or optimistic true for first check
        return _isAvailable ?? true;
    }

    private async Task CheckAvailabilityAsync()
    {
        try
        {
            _isAvailable = await Client.IsAvailableAsync();
            _lastAvailabilityCheck = DateTime.UtcNow;

            if (!_isAvailable.Value)
                _logger?.LogDebug("Docling service not available at {BaseUrl}", _config.BaseUrl);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to check Docling availability");
            _isAvailable = false;
            _lastAvailabilityCheck = DateTime.UtcNow;
        }
    }

    /// <summary>
    ///     Explicitly check if Docling is available (async version).
    /// </summary>
    public async Task<bool> IsAvailableAsync()
    {
        await CheckAvailabilityAsync();
        return _isAvailable ?? false;
    }

    /// <summary>
    ///     Detect Docling capabilities (GPU, etc.).
    /// </summary>
    public async Task<DoclingCapabilities> DetectCapabilitiesAsync()
    {
        return await Client.DetectCapabilitiesAsync();
    }

    private static DocumentContent CreateErrorResult(string filePath, string error)
    {
        return new DocumentContent
        {
            Markdown = $"<!-- Docling processing failed: {error} -->",
            Title = Path.GetFileNameWithoutExtension(filePath),
            ContentType = DetectContentType(filePath),
            Metadata = new Dictionary<string, object>
            {
                ["error"] = error,
                ["extractionMethod"] = "docling_failed"
            }
        };
    }

    private static string? ExtractTitleFromMarkdown(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return null;

        var lines = markdown.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# "))
                return trimmed[2..].Trim();
        }

        return null;
    }

    private static string DetectContentType(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "pdf",
            ".docx" or ".doc" => "document",
            ".pptx" or ".ppt" => "presentation",
            ".xlsx" or ".xls" => "spreadsheet",
            ".html" or ".htm" => "html",
            _ => "document"
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientLazy.IsValueCreated)
        {
            Client.Dispose();
        }
        await ValueTask.CompletedTask;
    }
}

/// <summary>
///     Extension methods to register Docling document handlers.
/// </summary>
public static class DoclingDocumentHandlerExtensions
{
    /// <summary>
    ///     Register Docling document handler if enabled in configuration.
    ///     The handler has higher priority than enhanced and default handlers.
    /// </summary>
    public static IDocumentHandlerRegistry RegisterDoclingHandlers(
        this IDocumentHandlerRegistry registry,
        IOptions<DocSummarizerConfig> config,
        ILogger<DoclingDocumentHandler>? logger = null)
    {
        var doclingConfig = config.Value.Docling;

        // Only register if Docling is enabled for documents
        if (!doclingConfig.Enabled || !doclingConfig.Documents.Enabled)
        {
            logger?.LogDebug("Docling document handler not registered (disabled in config)");
            return registry;
        }

        var handler = new DoclingDocumentHandler(config, logger);
        registry.Register(handler);

        logger?.LogInformation(
            "Registered Docling document handler (priority: {Priority}, extensions: {Extensions})",
            doclingConfig.Documents.Priority,
            string.Join(", ", doclingConfig.Documents.Extensions));

        return registry;
    }
}
