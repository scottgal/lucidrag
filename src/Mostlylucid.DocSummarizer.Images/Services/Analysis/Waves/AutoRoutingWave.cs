using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Storage;
using Mostlylucid.DocSummarizer.Images.Services.Vision;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Auto-routing wave that uses fast signals to determine optimal processing path.
/// Runs early (after Identity/Color) to route images through fast/balanced/quality paths.
///
/// Routes:
/// - fast: Simple static images → Florence2 + OpenCV only, skip Tesseract OCR
/// - balanced: Standard images → Florence2 + Tesseract OCR, escalate to VisionLLM if needed
/// - quality: Complex images (animation, heavy text, documents) → Full pipeline
///
/// Uses fast OpenCV MSER detection (~5-20ms) to assess text quantity for routing:
/// - Low text coverage (&lt;10%): FAST route (Florence-2 captions sufficient)
/// - Medium coverage (10-40%): BALANCED route (need Tesseract for accuracy)
/// - High coverage (&gt;40%): QUALITY route (document scan needs full pipeline)
///
/// This implements "probability proposes, determinism persists" at the routing level:
/// fast deterministic signals (identity, color, text detection) gate expensive operations.
/// </summary>
public class AutoRoutingWave : IAnalysisWave
{
    private readonly ISignalDatabase? _signalDb;
    private readonly OpenCvTextDetector? _textDetector;
    private readonly ILogger<AutoRoutingWave>? _logger;

    public string Name => "AutoRoutingWave";
    public int Priority => 98; // After Color (100), before ExifForensics (90)
    public IReadOnlyList<string> Tags => new[] { "routing", "auto", "optimization" };

    public AutoRoutingWave(
        ISignalDatabase? signalDb = null,
        ILogger<AutoRoutingWave>? logger = null)
    {
        _signalDb = signalDb;
        _textDetector = new OpenCvTextDetector(logger as ILogger<OpenCvTextDetector>);
        _logger = logger;
    }

    public Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        // Gather fast signals from Identity and Color waves
        var isAnimated = context.GetValue<bool>("identity.is_animated");
        var frameCount = context.GetValue<int>("identity.frame_count");
        var pixelCount = context.GetValue<int>("identity.pixel_count");
        var format = context.GetValue<string>("identity.format") ?? "";
        var imageWidth = context.GetValue<int>("identity.width");
        var imageHeight = context.GetValue<int>("identity.height");

        var textLikeliness = context.GetValue<double>("content.text_likeliness");
        var edgeDensity = context.GetValue<double>("quality.edge_density");
        var isGrayscale = context.GetValue<bool>("color.is_grayscale");
        var contentType = context.GetValue<string>("content.type") ?? "";

        // Check for cached routing decision (memory)
        var imageHash = context.GetValue<string>("identity.sha256");
        var cachedRoute = GetCachedRoute(imageHash);
        if (cachedRoute != null)
        {
            _logger?.LogDebug("Using cached route '{Route}' for image {Hash}", cachedRoute, imageHash?.Substring(0, 8));
            return Task.FromResult(EmitRoutingSignals(signals, cachedRoute.Value, "cached_decision", 0, 0));
        }

        // FAST text detection with OpenCV MSER (~5-20ms)
        // This gives us accurate text quantity info for routing decisions
        double textCoverage = 0;
        int textRegionCount = 0;
        bool hasSubtitles = false;

        if (_textDetector != null)
        {
            try
            {
                var detection = _textDetector.DetectTextRegions(imagePath);
                textRegionCount = detection.TextRegionCount;
                textCoverage = detection.TextAreaRatio;
                hasSubtitles = _textDetector.HasSubtitleRegion(imagePath);

                // Cache detection results for downstream waves (MlOcrWave, etc.)
                if (detection.TextBoundingBoxes.Count > 0)
                {
                    var boxesData = detection.TextBoundingBoxes.Select(b => new Dictionary<string, int>
                    {
                        ["x"] = b.X, ["y"] = b.Y, ["width"] = b.Width, ["height"] = b.Height
                    }).ToList();
                    context.SetCached("ocr.opencv.text_regions", boxesData);
                }

                signals.Add(new Signal
                {
                    Key = "route.text_detection",
                    Value = new { regions = textRegionCount, coverage = textCoverage, hasSubtitles, ms = detection.DetectionTimeMs },
                    Confidence = detection.Confidence,
                    Source = Name,
                    Tags = new List<string> { "routing", "text", "opencv" }
                });

                _logger?.LogDebug("Fast text detection: {Regions} regions, {Coverage:P1} coverage, subtitles={Subtitles}",
                    textRegionCount, textCoverage, hasSubtitles);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Fast text detection failed, using fallback signals");
            }
        }

        // Route selection logic based on fast signals + text detection
        var (route, reasons) = SelectRoute(
            isAnimated, frameCount, pixelCount, format,
            textLikeliness, edgeDensity, isGrayscale, contentType,
            textCoverage, textRegionCount, hasSubtitles);

        _logger?.LogInformation(
            "Auto-routing: {Route} ({Reasons}) for {Path}",
            route, string.Join(", ", reasons), Path.GetFileName(imagePath));

        // Cache the decision for future runs
        if (!string.IsNullOrEmpty(imageHash))
        {
            CacheRoute(imageHash, route);
        }

        return Task.FromResult(EmitRoutingSignals(signals, route, string.Join("; ", reasons), textCoverage, textRegionCount));
    }

    /// <summary>
    /// Select optimal route based on fast signals + OpenCV text detection.
    /// Uses text coverage to determine OCR complexity needs:
    /// - Low coverage (&lt;10%): FAST - Florence-2 sufficient for captions
    /// - Medium coverage (10-40%): BALANCED - need Tesseract for accuracy
    /// - High coverage (&gt;40%): QUALITY - document scan, full pipeline
    /// </summary>
    private (Route route, List<string> reasons) SelectRoute(
        bool isAnimated, int frameCount, int pixelCount, string format,
        double textLikeliness, double edgeDensity, bool isGrayscale, string contentType,
        double textCoverage, int textRegionCount, bool hasSubtitles)
    {
        var reasons = new List<string>();

        // Quality indicators (any of these → quality path)
        var qualityIndicators = 0;

        // HIGH TEXT COVERAGE = Document/Screenshot → needs quality OCR pipeline
        if (textCoverage > 0.40)
        {
            qualityIndicators += 3; // Strong indicator
            reasons.Add($"document_text:{textCoverage:P0}");
        }
        else if (textCoverage > 0.20)
        {
            qualityIndicators += 2;
            reasons.Add($"significant_text:{textCoverage:P0}");
        }
        else if (textCoverage > 0.10)
        {
            qualityIndicators++;
            reasons.Add($"moderate_text:{textCoverage:P0}");
        }

        // Many text regions = complex layout (tables, multiple text blocks)
        if (textRegionCount > 10)
        {
            qualityIndicators += 2;
            reasons.Add($"complex_layout:{textRegionCount}regions");
        }

        // Animated GIF with many frames needs full analysis
        if (isAnimated && frameCount > 3)
        {
            // But if it has subtitles, Florence-2 can handle it in FAST mode
            if (hasSubtitles && textCoverage < 0.15)
            {
                reasons.Add($"animated_subtitles:{frameCount}f");
                // Don't add quality indicators - FAST route can handle subtitles
            }
            else
            {
                qualityIndicators += 2;
                reasons.Add($"animated:{frameCount}frames");
            }
        }

        // High text likeliness (from edge analysis) combined with low OpenCV detection
        // suggests stylized text that needs VisionLLM
        if (textLikeliness > 0.5 && textCoverage < 0.05)
        {
            qualityIndicators++;
            reasons.Add("stylized_text_likely");
        }

        // Complex content types need quality analysis
        if (contentType is "Diagram" or "Chart" or "ScannedDocument" or "Screenshot")
        {
            qualityIndicators += 2;
            reasons.Add($"content_type:{contentType}");
        }

        // High edge density with many text regions = complex document
        if (edgeDensity > 0.15 && textRegionCount > 5)
        {
            qualityIndicators++;
            reasons.Add($"complex_doc:{edgeDensity:F2}");
        }

        // Large images with text need more careful analysis
        if (pixelCount > 2_000_000 && textCoverage > 0.05)
        {
            qualityIndicators++;
            reasons.Add("large_text_image");
        }

        // ========== FAST INDICATORS ==========
        var fastIndicators = 0;

        // LOW TEXT COVERAGE = Caption/meme/photo → Florence-2 sufficient
        if (textCoverage < 0.10 && textRegionCount <= 3)
        {
            fastIndicators += 2;
            reasons.Add($"minimal_text:{textCoverage:P0}");
        }

        // Static, simple images with little text
        if (!isAnimated && frameCount <= 1 && textCoverage < 0.15)
        {
            fastIndicators++;
            reasons.Add("static_simple");
        }

        // No text detected at all
        if (textRegionCount == 0 && textLikeliness < 0.1)
        {
            fastIndicators += 2;
            reasons.Add("no_text_detected");
        }

        // Subtitle GIF (text only in bottom region) - Florence-2 handles well
        if (hasSubtitles && textCoverage < 0.15 && !contentType.Contains("Document"))
        {
            fastIndicators++;
            reasons.Add("subtitle_only");
        }

        // Small images
        if (pixelCount < 100_000)
        {
            fastIndicators++;
            reasons.Add("small_image");
        }

        // ========== ROUTE DECISION ==========
        // Priority: Quality if high text coverage, else Fast if low text coverage
        if (qualityIndicators >= 3)
        {
            return (Route.Quality, reasons);
        }
        else if (fastIndicators >= 3 || (fastIndicators >= 2 && qualityIndicators == 0))
        {
            return (Route.Fast, reasons);
        }
        else
        {
            return (Route.Balanced, reasons);
        }
    }

    /// <summary>
    /// Emit routing signals that downstream waves can check.
    /// </summary>
    private IEnumerable<Signal> EmitRoutingSignals(
        List<Signal> signals, Route route, string reason,
        double textCoverage, int textRegionCount)
    {
        signals.Add(new Signal
        {
            Key = "route.selected",
            Value = route.ToString().ToLowerInvariant(),
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "routing", "auto" },
            Metadata = new Dictionary<string, object>
            {
                ["text_coverage"] = textCoverage,
                ["text_regions"] = textRegionCount
            }
        });

        signals.Add(new Signal
        {
            Key = "route.reason",
            Value = reason,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "routing" }
        });

        // Text quantity tier for OCR decisions
        // FAST: Florence-2 only, BALANCED: + Tesseract, QUALITY: + Advanced pipeline
        var textTier = textCoverage switch
        {
            < 0.10 => "caption",     // Minimal text - Florence-2 sufficient
            < 0.25 => "moderate",    // Some text - need Tesseract
            < 0.40 => "substantial", // Lots of text - full OCR
            _ => "document"          // Document scan - quality pipeline
        };

        signals.Add(new Signal
        {
            Key = "route.text_tier",
            Value = textTier,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "routing", "text" },
            Metadata = new Dictionary<string, object>
            {
                ["coverage"] = textCoverage,
                ["regions"] = textRegionCount,
                ["ocr_recommended"] = textTier != "caption" // True if Tesseract recommended
            }
        });

        // Emit skip signals for downstream waves to check
        var skipWaves = GetSkipWaves(route, textTier);
        signals.Add(new Signal
        {
            Key = "route.skip_waves",
            Value = skipWaves,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "routing", "optimization" },
            Metadata = new Dictionary<string, object>
            {
                ["route"] = route.ToString(),
                ["text_tier"] = textTier,
                ["skip_count"] = skipWaves.Count
            }
        });

        // Emit individual skip flags for easy checking
        foreach (var wave in skipWaves)
        {
            signals.Add(new Signal
            {
                Key = $"route.skip.{wave}",
                Value = true,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "routing", "skip" }
            });
        }

        // Emit quality tier for output display
        signals.Add(new Signal
        {
            Key = "route.quality_tier",
            Value = route switch
            {
                Route.Fast => 1,
                Route.Balanced => 2,
                Route.Quality => 3,
                _ => 2
            },
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "routing" }
        });

        return signals;
    }

    /// <summary>
    /// Get list of waves to skip based on route and text complexity.
    /// FAST + caption tier: Florence-2 only, skip all heavy OCR
    /// FAST + other tiers: Still use Florence-2 but may escalate
    /// BALANCED: Skip advanced OCR unless text tier is substantial+
    /// QUALITY: Run everything
    /// </summary>
    private static List<string> GetSkipWaves(Route route, string textTier)
    {
        return route switch
        {
            Route.Fast when textTier == "caption" => new List<string>
            {
                // Caption-only: Florence-2 is sufficient, skip ALL heavy processing
                "OcrWave",              // Skip Tesseract - Florence-2 handles captions
                "AdvancedOcrWave",      // Skip multi-frame OCR
                "OcrVerificationWave",  // Skip OCR verification
                "TextDetectionWave",    // Already did fast detection in routing
                "ClipEmbeddingWave",    // Skip CLIP (expensive)
                "FaceDetectionWave"     // Skip face detection
            },
            Route.Fast => new List<string>
            {
                // FAST with more text: Use Florence-2 but keep Tesseract available
                "AdvancedOcrWave",      // Skip multi-frame OCR
                "OcrVerificationWave",  // Skip OCR verification
                "ClipEmbeddingWave",    // Skip CLIP (expensive)
                "FaceDetectionWave"     // Skip face detection
            },
            Route.Balanced when textTier is "caption" or "moderate" => new List<string>
            {
                "AdvancedOcrWave",      // Skip advanced OCR for light text
                "OcrVerificationWave",  // Skip verification unless needed
                "ClipEmbeddingWave"     // Skip CLIP
            },
            Route.Balanced => new List<string>
            {
                // Balanced with substantial text: enable most OCR
                "ClipEmbeddingWave"     // Still skip CLIP
            },
            Route.Quality => new List<string>(), // Run everything
            _ => new List<string>()
        };
    }

    #region Routing Memory

    // In-memory cache for routing decisions (keyed by image hash)
    private static readonly Dictionary<string, (Route route, DateTime cachedAt)> _routeCache = new();
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromHours(24);

    private Route? GetCachedRoute(string? imageHash)
    {
        if (string.IsNullOrEmpty(imageHash))
            return null;

        lock (_routeCache)
        {
            if (_routeCache.TryGetValue(imageHash, out var cached))
            {
                if (DateTime.UtcNow - cached.cachedAt < CacheExpiry)
                {
                    return cached.route;
                }
                _routeCache.Remove(imageHash);
            }
        }

        // Check SignalDatabase for persistent routing history
        if (_signalDb != null)
        {
            try
            {
                var profile = _signalDb.LoadProfileAsync(imageHash).GetAwaiter().GetResult();
                if (profile != null)
                {
                    var routeValue = profile.GetValue<string>("route.selected");
                    if (!string.IsNullOrEmpty(routeValue) &&
                        Enum.TryParse<Route>(routeValue, ignoreCase: true, out var persistedRoute))
                    {
                        _logger?.LogDebug("Found persisted route '{Route}' in SignalDatabase for {Hash}",
                            persistedRoute, imageHash.Substring(0, 8));

                        // Cache it in memory for faster subsequent lookups
                        lock (_routeCache)
                        {
                            _routeCache[imageHash] = (persistedRoute, DateTime.UtcNow);
                        }
                        return persistedRoute;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load route from SignalDatabase for {Hash}", imageHash.Substring(0, 8));
            }
        }

        return null;
    }

    private void CacheRoute(string imageHash, Route route)
    {
        lock (_routeCache)
        {
            _routeCache[imageHash] = (route, DateTime.UtcNow);

            // Limit cache size
            if (_routeCache.Count > 10000)
            {
                var oldest = _routeCache
                    .OrderBy(kv => kv.Value.cachedAt)
                    .Take(1000)
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var key in oldest)
                    _routeCache.Remove(key);
            }
        }

        // Note: Persistence to SignalDatabase happens automatically when the profile
        // is stored after analysis completes. The route.selected signal emitted by
        // EmitRoutingSignals() will be persisted with the full profile.
    }

    #endregion

    public enum Route
    {
        Fast,
        Balanced,
        Quality
    }
}
