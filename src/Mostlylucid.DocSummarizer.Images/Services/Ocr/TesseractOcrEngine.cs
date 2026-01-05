using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.Models;
using Tesseract;

namespace Mostlylucid.DocSummarizer.Images.Services.Ocr;

/// <summary>
/// Tesseract implementation of IOcrEngine.
/// Provides text extraction with word-level bounding boxes.
/// Auto-downloads tessdata on first use for zero-setup experience.
/// </summary>
public class TesseractOcrEngine : IOcrEngine
{
    private readonly ModelDownloader? _modelDownloader;
    private readonly string? _tesseractDataPath;
    private readonly string _language;
    private readonly ILogger<TesseractOcrEngine>? _logger;
    private string? _resolvedTessdataPath;
    private readonly object _initLock = new();
    private bool _initialized;

    public TesseractOcrEngine(
        ModelDownloader? modelDownloader = null,
        string? tesseractDataPath = null,
        string language = "eng",
        ILogger<TesseractOcrEngine>? logger = null)
    {
        _modelDownloader = modelDownloader;
        _tesseractDataPath = tesseractDataPath;
        _language = language;
        _logger = logger;
    }

    private string GetTessdataPath()
    {
        if (_initialized && _resolvedTessdataPath != null)
        {
            return _resolvedTessdataPath;
        }

        lock (_initLock)
        {
            if (_initialized && _resolvedTessdataPath != null)
            {
                return _resolvedTessdataPath;
            }

            // Priority 1: Explicit path provided
            if (!string.IsNullOrEmpty(_tesseractDataPath) && Directory.Exists(_tesseractDataPath))
            {
                _resolvedTessdataPath = _tesseractDataPath;
                _initialized = true;
                _logger?.LogDebug("Using explicit tessdata path: {Path}", _resolvedTessdataPath);
                return _resolvedTessdataPath;
            }

            // Priority 2: Auto-download via ModelDownloader
            if (_modelDownloader != null)
            {
                try
                {
                    _logger?.LogInformation("Auto-downloading tessdata for first-time setup...");
                    _resolvedTessdataPath = _modelDownloader.GetTessdataDirectory();
                    _initialized = true;
                    _logger?.LogInformation("Tessdata ready at: {Path}", _resolvedTessdataPath);
                    return _resolvedTessdataPath;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to auto-download tessdata, falling back to local paths");
                }
            }

            // Priority 3: Check common local paths
            var localPaths = new[]
            {
                "./tessdata",
                Path.Combine(AppContext.BaseDirectory, "tessdata"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LucidRAG", "models", "tessdata")
            };

            foreach (var path in localPaths)
            {
                var engFile = Path.Combine(path, $"{_language}.traineddata");
                if (File.Exists(engFile))
                {
                    _resolvedTessdataPath = path;
                    _initialized = true;
                    _logger?.LogDebug("Found tessdata at: {Path}", _resolvedTessdataPath);
                    return _resolvedTessdataPath;
                }
            }

            // Fallback: Use default and let Tesseract throw if not found
            _resolvedTessdataPath = "./tessdata";
            _initialized = true;
            _logger?.LogWarning("No tessdata found, using default path: {Path}", _resolvedTessdataPath);
            return _resolvedTessdataPath;
        }
    }

    /// <inheritdoc/>
    public List<OcrTextRegion> ExtractTextWithCoordinates(string imagePath)
    {
        var regions = new List<OcrTextRegion>();
        var tessdataPath = GetTessdataPath();

        using var engine = new TesseractEngine(tessdataPath, _language, EngineMode.Default);

        // Load image from disk
        using var img = Pix.LoadFromFile(imagePath);
        using var page = engine.Process(img);

        // Iterate through layout hierarchy to get word-level bounding boxes
        using var iter = page.GetIterator();
        iter.Begin();

        do
        {
            // Get text and confidence at word level
            var text = iter.GetText(PageIteratorLevel.Word);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            // Get bounding box
            if (iter.TryGetBoundingBox(PageIteratorLevel.Word, out var bbox))
            {
                var confidence = iter.GetConfidence(PageIteratorLevel.Word) / 100.0; // Convert 0-100 to 0-1

                regions.Add(new OcrTextRegion
                {
                    Text = text.Trim(),
                    Confidence = confidence,
                    BoundingBox = new BoundingBox
                    {
                        X1 = bbox.X1,
                        Y1 = bbox.Y1,
                        X2 = bbox.X2,
                        Y2 = bbox.Y2,
                        Width = bbox.Width,
                        Height = bbox.Height
                    }
                });
            }
        } while (iter.Next(PageIteratorLevel.Word));

        return regions;
    }
}
