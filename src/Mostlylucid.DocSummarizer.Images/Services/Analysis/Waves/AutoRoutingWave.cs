using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Storage;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Auto-routing wave that uses fast signals to determine optimal processing path.
/// Runs early (after Identity/Color) to route images through fast/balanced/quality paths.
///
/// Routes:
/// - fast: Simple static images → Florence2 only, skip advanced OCR
/// - balanced: Standard images → Florence2 + OCR, escalate to VisionLLM if needed
/// - quality: Complex images (animation, text, faces) → Full pipeline
///
/// This implements "probability proposes, determinism persists" at the routing level:
/// fast deterministic signals (identity, color, edge density) gate expensive operations.
/// </summary>
public class AutoRoutingWave : IAnalysisWave
{
    private readonly ISignalDatabase? _signalDb;
    private readonly ILogger<AutoRoutingWave>? _logger;

    public string Name => "AutoRoutingWave";
    public int Priority => 98; // After Color (100), before ExifForensics (90)
    public IReadOnlyList<string> Tags => new[] { "routing", "auto", "optimization" };

    public AutoRoutingWave(
        ISignalDatabase? signalDb = null,
        ILogger<AutoRoutingWave>? logger = null)
    {
        _signalDb = signalDb;
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
            return Task.FromResult(EmitRoutingSignals(signals, cachedRoute.Value, "cached_decision"));
        }

        // Route selection logic based on fast signals
        var (route, reasons) = SelectRoute(
            isAnimated, frameCount, pixelCount, format,
            textLikeliness, edgeDensity, isGrayscale, contentType);

        _logger?.LogInformation(
            "Auto-routing: {Route} ({Reasons}) for {Path}",
            route, string.Join(", ", reasons), Path.GetFileName(imagePath));

        // Cache the decision for future runs
        if (!string.IsNullOrEmpty(imageHash))
        {
            CacheRoute(imageHash, route);
        }

        return Task.FromResult(EmitRoutingSignals(signals, route, string.Join("; ", reasons)));
    }

    /// <summary>
    /// Select optimal route based on fast signals.
    /// Returns route and list of reasons for the decision.
    /// </summary>
    private (Route route, List<string> reasons) SelectRoute(
        bool isAnimated, int frameCount, int pixelCount, string format,
        double textLikeliness, double edgeDensity, bool isGrayscale, string contentType)
    {
        var reasons = new List<string>();

        // Quality indicators (any of these → quality path)
        var qualityIndicators = 0;

        // Animated GIF with many frames needs full analysis
        if (isAnimated && frameCount > 3)
        {
            qualityIndicators += 2;
            reasons.Add($"animated:{frameCount}frames");
        }

        // High text likeliness needs good OCR
        if (textLikeliness > 0.5)
        {
            qualityIndicators += 2;
            reasons.Add($"text_likely:{textLikeliness:F2}");
        }
        else if (textLikeliness > 0.2)
        {
            qualityIndicators++;
            reasons.Add($"text_possible:{textLikeliness:F2}");
        }

        // Complex content types need quality analysis
        if (contentType is "Diagram" or "Chart" or "ScannedDocument" or "Screenshot")
        {
            qualityIndicators += 2;
            reasons.Add($"content_type:{contentType}");
        }

        // High edge density = complex image
        if (edgeDensity > 0.15)
        {
            qualityIndicators++;
            reasons.Add($"high_complexity:{edgeDensity:F2}");
        }

        // Large images need more careful analysis
        if (pixelCount > 2_000_000)
        {
            qualityIndicators++;
            reasons.Add("large_image");
        }

        // Fast indicators (all of these → fast path)
        var fastIndicators = 0;

        // Static, simple images
        if (!isAnimated && frameCount <= 1)
        {
            fastIndicators++;
            reasons.Add("static");
        }

        // Low text likeliness = skip heavy OCR
        if (textLikeliness < 0.1)
        {
            fastIndicators++;
            reasons.Add("no_text_expected");
        }

        // Simple grayscale images
        if (isGrayscale && edgeDensity < 0.05)
        {
            fastIndicators++;
            reasons.Add("simple_grayscale");
        }

        // Small images
        if (pixelCount < 100_000)
        {
            fastIndicators++;
            reasons.Add("small_image");
        }

        // Route decision
        if (qualityIndicators >= 3)
        {
            return (Route.Quality, reasons);
        }
        else if (qualityIndicators >= 1 || fastIndicators < 2)
        {
            return (Route.Balanced, reasons);
        }
        else
        {
            return (Route.Fast, reasons);
        }
    }

    /// <summary>
    /// Emit routing signals that downstream waves can check.
    /// </summary>
    private IEnumerable<Signal> EmitRoutingSignals(List<Signal> signals, Route route, string reason)
    {
        signals.Add(new Signal
        {
            Key = "route.selected",
            Value = route.ToString().ToLowerInvariant(),
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "routing", "auto" }
        });

        signals.Add(new Signal
        {
            Key = "route.reason",
            Value = reason,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "routing" }
        });

        // Emit skip signals for downstream waves to check
        var skipWaves = GetSkipWaves(route);
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
    /// Get list of waves to skip based on route.
    /// </summary>
    private static List<string> GetSkipWaves(Route route)
    {
        return route switch
        {
            Route.Fast => new List<string>
            {
                "VisionLlmWave",        // Use Florence2 only
                "AdvancedOcrWave",      // Skip multi-frame OCR
                "OcrVerificationWave",  // Skip OCR verification
                "ClipEmbeddingWave",    // Skip CLIP (expensive)
                "FaceDetectionWave"     // Skip face detection
            },
            Route.Balanced => new List<string>
            {
                "AdvancedOcrWave",      // Skip advanced OCR unless escalated
                "OcrVerificationWave",  // Skip verification unless needed
                "ClipEmbeddingWave"     // Skip CLIP
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

        // TODO: Check SignalDatabase for persistent routing history
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

        // TODO: Persist to SignalDatabase for long-term memory
    }

    #endregion

    public enum Route
    {
        Fast,
        Balanced,
        Quality
    }
}
