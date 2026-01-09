using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using SixLabors.ImageSharp;

namespace Mostlylucid.DocSummarizer.Images.Orchestration;

/// <summary>
///     Immutable snapshot of the blackboard state passed to waves.
///     Contains all signals from prior waves.
///     Follows the BotDetection BlackboardState pattern.
/// </summary>
public sealed class ImageBlackboardState
{
    /// <summary>
    ///     Path to the image being analyzed.
    /// </summary>
    public required string ImagePath { get; init; }

    /// <summary>
    ///     All signals collected so far from previous waves.
    /// </summary>
    public required IReadOnlyDictionary<string, object> Signals { get; init; }

    /// <summary>
    ///     Current aggregated confidence score.
    /// </summary>
    public double CurrentConfidence { get; init; }

    /// <summary>
    ///     Which waves have already run.
    /// </summary>
    public required IReadOnlySet<string> CompletedWaves { get; init; }

    /// <summary>
    ///     Which waves failed.
    /// </summary>
    public required IReadOnlySet<string> FailedWaves { get; init; }

    /// <summary>
    ///     Contributions received so far.
    /// </summary>
    public required IReadOnlyList<DetectionContribution> Contributions { get; init; }

    /// <summary>
    ///     Session ID for correlation.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    ///     Time elapsed since analysis started.
    /// </summary>
    public TimeSpan Elapsed { get; init; }

    // ===== Image-specific Properties =====

    /// <summary>
    ///     The loaded image (if available). Waves should check for null.
    ///     Loaded once by the orchestrator to avoid repeated file I/O.
    /// </summary>
    public Image? LoadedImage { get; init; }

    /// <summary>
    ///     Raw image bytes (useful for hashing, format detection).
    /// </summary>
    public byte[]? ImageBytes { get; init; }

    /// <summary>
    ///     MIME type of the image (e.g., "image/png").
    /// </summary>
    public string? MimeType { get; init; }

    // ===== Routing Properties =====

    /// <summary>
    ///     The selected processing route (fast/balanced/quality).
    /// </summary>
    public string? SelectedRoute => GetSignal<string>(ImageSignalKeys.RouteSelected);

    /// <summary>
    ///     Check if we're in fast mode (skip expensive operations).
    /// </summary>
    public bool IsFastRoute => SelectedRoute == "fast";

    /// <summary>
    ///     Check if we're in quality mode (run everything).
    /// </summary>
    public bool IsQualityRoute => SelectedRoute == "quality";

    /// <summary>
    ///     Check if a wave is skipped by auto-routing.
    /// </summary>
    public bool IsWaveSkippedByRouting(string waveName)
    {
        return GetSignal<bool>($"{ImageSignalKeys.RouteSkipPrefix}{waveName}");
    }

    // ===== Image Properties from Signals =====

    /// <summary>
    ///     Image width in pixels.
    /// </summary>
    public int ImageWidth => GetSignal<int>(ImageSignalKeys.ImageWidth);

    /// <summary>
    ///     Image height in pixels.
    /// </summary>
    public int ImageHeight => GetSignal<int>(ImageSignalKeys.ImageHeight);

    /// <summary>
    ///     Whether the image is animated (GIF).
    /// </summary>
    public bool IsAnimated => GetSignal<bool>(ImageSignalKeys.IsAnimated);

    /// <summary>
    ///     Number of frames for animated images.
    /// </summary>
    public int FrameCount => GetSignal<int>(ImageSignalKeys.FrameCount);

    /// <summary>
    ///     Whether text was detected in the image.
    /// </summary>
    public bool HasText => GetSignal<bool>(ImageSignalKeys.TextDetected);

    /// <summary>
    ///     Get a typed signal value.
    /// </summary>
    public T? GetSignal<T>(string key)
    {
        return Signals.TryGetValue(key, out var value) && value is T typed ? typed : default;
    }

    /// <summary>
    ///     Check if a signal exists.
    /// </summary>
    public bool HasSignal(string key)
    {
        return Signals.ContainsKey(key);
    }

    /// <summary>
    ///     Get all signals matching a prefix.
    /// </summary>
    public IEnumerable<KeyValuePair<string, object>> GetSignalsWithPrefix(string prefix)
    {
        return Signals.Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
///     Cache for sharing expensive computed data between waves.
///     Separate from signals to allow for large binary data.
/// </summary>
public sealed class ImageAnalysisCache
{
    private readonly Dictionary<string, object> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>
    ///     Store data in cache.
    /// </summary>
    public void Set<T>(string key, T value)
    {
        lock (_lock)
        {
            _cache[key] = value!;
        }
    }

    /// <summary>
    ///     Retrieve data from cache.
    /// </summary>
    public T? Get<T>(string key)
    {
        lock (_lock)
        {
            return _cache.TryGetValue(key, out var value) && value is T typed ? typed : default;
        }
    }

    /// <summary>
    ///     Check if key exists in cache.
    /// </summary>
    public bool Contains(string key)
    {
        lock (_lock)
        {
            return _cache.ContainsKey(key);
        }
    }

    /// <summary>
    ///     Clear all cached data (call after analysis to free memory).
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            // Dispose any disposable values
            foreach (var value in _cache.Values)
            {
                if (value is IDisposable disposable)
                    disposable.Dispose();
            }

            _cache.Clear();
        }
    }
}

/// <summary>
///     Well-known cache keys.
/// </summary>
public static class ImageCacheKeys
{
    /// <summary>
    ///     Grayscale version of the image for text detection.
    /// </summary>
    public const string GrayscaleImage = "cache.grayscale_image";

    /// <summary>
    ///     Edge-detected image for structure analysis.
    /// </summary>
    public const string EdgeImage = "cache.edge_image";

    /// <summary>
    ///     Color histogram data.
    /// </summary>
    public const string ColorHistogram = "cache.color_histogram";

    /// <summary>
    ///     Decoded GIF frames.
    /// </summary>
    public const string GifFrames = "cache.gif_frames";

    /// <summary>
    ///     EXIF metadata dictionary.
    /// </summary>
    public const string ExifData = "cache.exif_data";

    /// <summary>
    ///     Text regions detected by text detection wave.
    /// </summary>
    public const string TextRegions = "cache.text_regions";

    /// <summary>
    ///     CLIP embedding vector.
    /// </summary>
    public const string ClipEmbedding = "cache.clip_embedding";

    /// <summary>
    ///     Florence-2 caption result.
    /// </summary>
    public const string Florence2Result = "cache.florence2_result";
}
