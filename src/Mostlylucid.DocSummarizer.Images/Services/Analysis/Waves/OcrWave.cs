using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.Models;
using Tesseract;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// OCR wave using Tesseract for text extraction with bounding box coordinates.
/// Provides EasyOCR-compatible output format (text, coordinates, confidence).
/// Auto-downloads tessdata on first use for zero-setup experience.
///
/// References:
/// - Tesseract.NET: https://github.com/charlesw/tesseract
/// - EasyOCR format: https://github.com/JaidedAI/EasyOCR
/// </summary>
public class OcrWave : IAnalysisWave
{
    private readonly ModelDownloader? _modelDownloader;
    private readonly string? _tesseractDataPath;
    private readonly string _language;
    private readonly ILogger<OcrWave>? _logger;
    private readonly bool _enabled;
    private readonly double _textLikelinessThreshold;
    private string? _resolvedTessdataPath;
    private readonly object _initLock = new();
    private bool _initialized;

    public string Name => "OcrWave";
    public int Priority => 60; // Medium priority - runs after forensics
    public IReadOnlyList<string> Tags => new[] { SignalTags.Content, "ocr", "text" };

    /// <summary>
    /// Check if this wave should run based on routing decisions.
    /// FAST route with caption tier skips Tesseract OCR entirely.
    /// </summary>
    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        if (!_enabled)
            return false;

        // Skip if auto-routing says to skip this wave (FAST + caption tier)
        if (context.IsWaveSkippedByRouting(Name))
        {
            _logger?.LogDebug("OcrWave skipped by routing (FAST route)");
            return false;
        }

        // Skip if MlOcrWave explicitly said to skip Tesseract
        if (context.GetValue<bool>("ocr.escalation.skip_tesseract"))
        {
            _logger?.LogDebug("OcrWave skipped: MlOcrWave detected no text regions");
            return false;
        }

        return true;
    }

    public OcrWave(
        ModelDownloader? modelDownloader = null,
        string? tesseractDataPath = null,
        string language = "eng",
        bool enabled = true,
        double textLikelinessThreshold = 0.3,
        ILogger<OcrWave>? logger = null)
    {
        _modelDownloader = modelDownloader;
        _tesseractDataPath = tesseractDataPath;
        _language = language;
        _enabled = enabled;
        _textLikelinessThreshold = textLikelinessThreshold;
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

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        if (!_enabled)
        {
            signals.Add(new Signal
            {
                Key = "ocr.enabled",
                Value = false,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "config" }
            });
            return signals;
        }

        // Check if AdvancedOcrWave has already processed this image
        var advancedProcessed = context.HasSignal("ocr.advanced.performance") ||
                               context.HasSignal("ocr.corrected.text") ||
                               context.HasSignal("ocr.voting.consensus_text");

        if (advancedProcessed)
        {
            signals.Add(new Signal
            {
                Key = "ocr.simple.skipped",
                Value = true,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr" },
                Metadata = new Dictionary<string, object>
                {
                    ["reason"] = "Advanced OCR pipeline already processed this image"
                }
            });
            return signals;
        }

        // Check MlOcrWave escalation signals first (priority over text-likeliness)
        var skipTesseract = context.GetValue<bool>("ocr.escalation.skip_tesseract");
        if (skipTesseract)
        {
            _logger?.LogDebug("MlOcrWave signaled to skip Tesseract OCR");
            signals.Add(new Signal
            {
                Key = "ocr.skipped",
                Value = true,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr" },
                Metadata = new Dictionary<string, object>
                {
                    ["reason"] = "MlOcrWave escalation signal",
                    ["signal"] = "ocr.escalation.skip_tesseract"
                }
            });
            return signals;
        }

        // Check if MlOcrWave found text and recommends Tesseract
        var runTesseract = context.GetValue<bool>("ocr.escalation.run_tesseract");
        var opencvHasText = context.GetValue<bool>("ocr.opencv.has_text");

        // Check if image has sufficient text-likeliness to warrant OCR
        // Skip this check if MlOcrWave explicitly requested Tesseract
        if (!runTesseract && _textLikelinessThreshold > 0)
        {
            var textLikeliness = context.GetValue<double>("content.text_likeliness");
            if (textLikeliness < _textLikelinessThreshold && !opencvHasText)
            {
                signals.Add(new Signal
                {
                    Key = "ocr.skipped",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "ocr" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["reason"] = "Low text-likeliness score",
                        ["text_likeliness"] = textLikeliness,
                        ["threshold"] = _textLikelinessThreshold
                    }
                });
                return signals;
            }
        }

        // Get OpenCV text regions if available (for targeted OCR)
        var opencvRegions = context.GetCached<List<Dictionary<string, int>>>("ocr.opencv.text_regions");

        try
        {
            // Check if Tesseract data is available before attempting OCR
            var tessdataPath = GetTessdataPath();
            var tessFile = Path.Combine(tessdataPath, $"{_language}.traineddata");
            if (!File.Exists(tessFile))
            {
                _logger?.LogWarning("Tesseract data not found at {Path}, OCR unavailable. Use 'vision' pipeline instead.", tessdataPath);
                signals.Add(new Signal
                {
                    Key = "ocr.unavailable",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "ocr", "config" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["reason"] = "Tesseract data not found",
                        ["expected_path"] = tessFile,
                        ["hint"] = "Use '--pipeline vision' for Vision LLM-only mode without Tesseract"
                    }
                });
                return signals;
            }

            // Perform OCR with Tesseract - use OpenCV regions if available for targeted OCR
            List<OcrTextRegion> textRegions;

            if (opencvRegions != null && opencvRegions.Count > 0)
            {
                _logger?.LogDebug("Using {Count} OpenCV text regions for targeted Tesseract OCR", opencvRegions.Count);
                textRegions = await Task.Run(() => ExtractTextFromRegions(imagePath, opencvRegions), ct);

                signals.Add(new Signal
                {
                    Key = "ocr.targeted",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "ocr", "opencv" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["opencv_region_count"] = opencvRegions.Count
                    }
                });
            }
            else
            {
                textRegions = await Task.Run(() => ExtractTextWithCoordinates(imagePath), ct);
            }

            if (textRegions.Count == 0)
            {
                signals.Add(new Signal
                {
                    Key = "ocr.no_text_found",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "ocr" }
                });
                return signals;
            }

            // Add individual text regions as signals (indexed to avoid duplicate keys)
            for (var i = 0; i < textRegions.Count; i++)
            {
                var region = textRegions[i];
                signals.Add(new Signal
                {
                    Key = $"ocr.text_region.{i}",
                    Value = region,
                    Confidence = region.Confidence,
                    Source = Name,
                    Tags = new List<string> { "ocr", SignalTags.Content },
                    Metadata = new Dictionary<string, object>
                    {
                        ["text"] = region.Text,
                        ["bbox"] = region.BoundingBox,
                        ["confidence"] = region.Confidence,
                        ["index"] = i
                    }
                });
            }

            // Also emit all regions as a single collection signal
            signals.Add(new Signal
            {
                Key = "ocr.text_regions",
                Value = textRegions,
                Confidence = textRegions.Count > 0 ? textRegions.Average(r => r.Confidence) : 0,
                Source = Name,
                Tags = new List<string> { "ocr", SignalTags.Content },
                Metadata = new Dictionary<string, object>
                {
                    ["count"] = textRegions.Count
                }
            });

            // Add combined full text
            var fullText = string.Join("\n", textRegions.Select(r => r.Text));
            signals.Add(new Signal
            {
                Key = "ocr.full_text",
                Value = fullText,
                Confidence = textRegions.Average(r => r.Confidence),
                Source = Name,
                Tags = new List<string> { "ocr", SignalTags.Content },
                Metadata = new Dictionary<string, object>
                {
                    ["region_count"] = textRegions.Count,
                    ["total_characters"] = fullText.Length
                }
            });

            // Add summary statistics
            signals.Add(new Signal
            {
                Key = "ocr.statistics",
                Value = new
                {
                    RegionCount = textRegions.Count,
                    TotalCharacters = fullText.Length,
                    AverageConfidence = textRegions.Average(r => r.Confidence),
                    HighConfidenceRegions = textRegions.Count(r => r.Confidence > 0.8)
                },
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr", "statistics" }
            });

            _logger?.LogInformation("OCR extracted {RegionCount} text regions from {ImagePath}",
                textRegions.Count, imagePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "OCR failed for {ImagePath}", imagePath);

            signals.Add(new Signal
            {
                Key = "ocr.error",
                Value = ex.Message,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "error" }
            });
        }

        return signals;
    }

    /// <summary>
    /// Extract text with bounding boxes using Tesseract.
    /// Output format compatible with EasyOCR: [text, confidence, bounding_box]
    /// </summary>
    private List<OcrTextRegion> ExtractTextWithCoordinates(string imagePath)
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

    /// <summary>
    /// Extract text from specific OpenCV-detected regions.
    /// More accurate than full-image OCR as it focuses on text areas only.
    /// </summary>
    private List<OcrTextRegion> ExtractTextFromRegions(
        string imagePath,
        List<Dictionary<string, int>> opencvRegions)
    {
        var allRegions = new List<OcrTextRegion>();
        var tessdataPath = GetTessdataPath();
        var tempPaths = new List<string>();

        try
        {
            using var engine = new TesseractEngine(tessdataPath, _language, EngineMode.Default);
            using var fullImg = SixLabors.ImageSharp.Image.Load(imagePath);

            foreach (var region in opencvRegions.Take(10)) // Limit to 10 regions for performance
            {
                try
                {
                    // Extract region bounds with padding
                    var padding = 5;
                    var x = Math.Max(0, region["x"] - padding);
                    var y = Math.Max(0, region["y"] - padding);
                    var width = Math.Min(fullImg.Width - x, region["width"] + padding * 2);
                    var height = Math.Min(fullImg.Height - y, region["height"] + padding * 2);

                    if (width < 10 || height < 10) continue; // Skip tiny regions

                    // Crop the region from the image using ImageSharp
                    var cropRect = new Rectangle(x, y, width, height);
                    using var cropped = fullImg.Clone(ctx => ctx.Crop(cropRect));

                    // Save to temp file for Tesseract
                    var tempPath = Path.Combine(Path.GetTempPath(), $"ocr_region_{Guid.NewGuid()}.png");
                    tempPaths.Add(tempPath);
                    cropped.SaveAsPng(tempPath);

                    // Process the cropped region with Tesseract
                    using var pixCropped = Pix.LoadFromFile(tempPath);
                    using var page = engine.Process(pixCropped);

                    // Get text from this region
                    var text = page.GetText()?.Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var confidence = page.GetMeanConfidence();

                    allRegions.Add(new OcrTextRegion
                    {
                        Text = text,
                        Confidence = confidence,
                        BoundingBox = new BoundingBox
                        {
                            X1 = x,
                            Y1 = y,
                            X2 = x + width,
                            Y2 = y + height,
                            Width = width,
                            Height = height
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger?.LogTrace(ex, "Failed to OCR region, skipping");
                }
            }

            _logger?.LogDebug("Extracted text from {Count}/{Total} OpenCV regions",
                allRegions.Count, opencvRegions.Count);
        }
        finally
        {
            // Cleanup temp files
            foreach (var path in tempPaths)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        return allRegions;
    }
}

/// <summary>
/// Text region with coordinates and confidence (EasyOCR-compatible format).
/// </summary>
public record OcrTextRegion
{
    /// <summary>
    /// Detected text content.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// OCR confidence score (0.0 - 1.0).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Bounding box coordinates for this text region.
    /// </summary>
    public required BoundingBox BoundingBox { get; init; }
}

/// <summary>
/// Bounding box coordinates for a text region.
/// Compatible with EasyOCR format.
/// </summary>
public record BoundingBox
{
    /// <summary>
    /// Left X coordinate.
    /// </summary>
    public int X1 { get; init; }

    /// <summary>
    /// Top Y coordinate.
    /// </summary>
    public int Y1 { get; init; }

    /// <summary>
    /// Right X coordinate.
    /// </summary>
    public int X2 { get; init; }

    /// <summary>
    /// Bottom Y coordinate.
    /// </summary>
    public int Y2 { get; init; }

    /// <summary>
    /// Width of bounding box.
    /// </summary>
    public int Width { get; init; }

    /// <summary>
    /// Height of bounding box.
    /// </summary>
    public int Height { get; init; }

    /// <summary>
    /// Center point X coordinate.
    /// </summary>
    public int CenterX => (X1 + X2) / 2;

    /// <summary>
    /// Center point Y coordinate.
    /// </summary>
    public int CenterY => (Y1 + Y2) / 2;
}
