using System.Text.Json;

namespace Mostlylucid.DocSummarizer.Images.Models.Dynamic;

/// <summary>
/// Extensible image profile that aggregates signals from multiple analysis waves.
/// Supports both structured access and dynamic querying of signals.
/// </summary>
public class DynamicImageProfile
{
    private readonly Dictionary<string, List<Signal>> _signals = new();
    private readonly Dictionary<string, object> _aggregatedCache = new();
    private bool _cacheValid = false;

    /// <summary>
    /// Path to the analyzed image.
    /// </summary>
    public string? ImagePath { get; set; }

    /// <summary>
    /// When this profile was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Total analysis duration in milliseconds.
    /// </summary>
    public long AnalysisDurationMs { get; set; }

    /// <summary>
    /// Names of waves that contributed to this profile.
    /// </summary>
    public List<string> ContributingWaves { get; } = new();

    /// <summary>
    /// Add a signal to the profile.
    /// </summary>
    public void AddSignal(Signal signal)
    {
        if (!_signals.ContainsKey(signal.Key))
        {
            _signals[signal.Key] = new List<Signal>();
        }
        _signals[signal.Key].Add(signal);
        _cacheValid = false;

        if (!ContributingWaves.Contains(signal.Source))
        {
            ContributingWaves.Add(signal.Source);
        }
    }

    /// <summary>
    /// Add multiple signals.
    /// </summary>
    public void AddSignals(IEnumerable<Signal> signals)
    {
        foreach (var signal in signals)
        {
            AddSignal(signal);
        }
    }

    /// <summary>
    /// Get all signals for a specific key.
    /// </summary>
    public IEnumerable<Signal> GetSignals(string key)
    {
        return _signals.TryGetValue(key, out var signals) ? signals : Enumerable.Empty<Signal>();
    }

    /// <summary>
    /// Get signal with highest confidence for a key.
    /// </summary>
    public Signal? GetBestSignal(string key)
    {
        return GetSignals(key).OrderByDescending(s => s.Confidence).FirstOrDefault();
    }

    /// <summary>
    /// Get value from the best signal for a key.
    /// </summary>
    public T? GetValue<T>(string key)
    {
        var signal = GetBestSignal(key);
        if (signal?.Value == null)
            return default;

        // Handle direct type match
        if (signal.Value is T directValue)
            return directValue;

        // Handle JsonElement deserialization (from database)
        if (signal.Value is JsonElement jsonElement)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(jsonElement.GetRawText());
            }
            catch
            {
                return default;
            }
        }

        // Try to convert
        try
        {
            return (T)Convert.ChangeType(signal.Value, typeof(T));
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Get value with fallback.
    /// </summary>
    public T GetValueOrDefault<T>(string key, T defaultValue)
    {
        return GetValue<T>(key) ?? defaultValue;
    }

    /// <summary>
    /// Get aggregated value using specified strategy.
    /// </summary>
    public object? GetAggregatedValue(string key, AggregationStrategy strategy = AggregationStrategy.HighestConfidence)
    {
        var signals = GetSignals(key).ToList();
        if (!signals.Any()) return null;

        return SignalAggregator.Aggregate(signals, strategy);
    }

    /// <summary>
    /// Check if profile has signals for a key.
    /// </summary>
    public bool HasSignal(string key)
    {
        return _signals.ContainsKey(key) && _signals[key].Any();
    }

    /// <summary>
    /// Get all signal keys.
    /// </summary>
    public IEnumerable<string> GetAllKeys()
    {
        return _signals.Keys;
    }

    /// <summary>
    /// Get all signals.
    /// </summary>
    public IEnumerable<Signal> GetAllSignals()
    {
        return _signals.Values.SelectMany(s => s);
    }

    /// <summary>
    /// Get signals by tag.
    /// </summary>
    public IEnumerable<Signal> GetSignalsByTag(string tag)
    {
        return GetAllSignals().Where(s => s.Tags?.Contains(tag) == true);
    }

    /// <summary>
    /// Get signals by source wave.
    /// </summary>
    public IEnumerable<Signal> GetSignalsBySource(string source)
    {
        return GetAllSignals().Where(s => s.Source == source);
    }

    /// <summary>
    /// Get aggregated view as dictionary (cached).
    /// Uses highest confidence strategy by default.
    /// </summary>
    public Dictionary<string, object> GetAggregatedView()
    {
        if (_cacheValid)
        {
            return new Dictionary<string, object>(_aggregatedCache);
        }

        _aggregatedCache.Clear();
        foreach (var key in GetAllKeys())
        {
            var value = GetAggregatedValue(key);
            if (value != null)
            {
                _aggregatedCache[key] = value;
            }
        }
        _cacheValid = true;

        return new Dictionary<string, object>(_aggregatedCache);
    }

    /// <summary>
    /// Convert to backward-compatible static ImageProfile.
    /// Extracts standard fields using well-known signal keys.
    /// </summary>
    public ImageProfile ToStaticProfile()
    {
        return new ImageProfile
        {
            Sha256 = GetValueOrDefault<string>("identity.sha256", string.Empty),
            Format = GetValueOrDefault<string>("identity.format", "Unknown"),
            Width = GetValueOrDefault<int>("identity.width", 0),
            Height = GetValueOrDefault<int>("identity.height", 0),
            AspectRatio = GetValueOrDefault<double>("identity.aspect_ratio", 0),
            HasExif = GetValueOrDefault<bool>("metadata.has_exif", false),

            EdgeDensity = GetValueOrDefault<double>("visual.edge_density", 0),
            LuminanceEntropy = GetValueOrDefault<double>("visual.luminance_entropy", 0),
            CompressionArtifacts = GetValueOrDefault<double>("quality.compression_artifacts", 0),

            MeanLuminance = GetValueOrDefault<double>("visual.mean_luminance", 0),
            LuminanceStdDev = GetValueOrDefault<double>("visual.luminance_stddev", 0),
            ClippedBlacksPercent = GetValueOrDefault<double>("visual.clipped_blacks_percent", 0),
            ClippedWhitesPercent = GetValueOrDefault<double>("visual.clipped_whites_percent", 0),

            LaplacianVariance = GetValueOrDefault<double>("quality.sharpness", 0),
            TextLikeliness = GetValueOrDefault<double>("content.text_likeliness", 0),
            SalientRegions = GetValue<List<SaliencyRegion>>("content.salient_regions"),

            DominantColors = GetValue<List<DominantColor>>("color.dominant_colors") ?? new List<DominantColor>(),
            ColorGrid = GetValue<ColorGrid>("color.grid"),
            MeanSaturation = GetValueOrDefault<double>("color.mean_saturation", 0),
            IsMostlyGrayscale = GetValueOrDefault<bool>("color.is_grayscale", false),

            DetectedType = GetValueOrDefault("content.type", ImageType.Unknown),
            TypeConfidence = GetValueOrDefault<double>("content.type_confidence", 0),

            PerceptualHash = GetValue<string>("identity.perceptual_hash") ?? string.Empty
        };
    }

    /// <summary>
    /// Export profile as JSON.
    /// </summary>
    public string ToJson(bool includeMetadata = true)
    {
        var data = new
        {
            imagePath = ImagePath,
            createdAt = CreatedAt,
            analysisDurationMs = AnalysisDurationMs,
            contributingWaves = ContributingWaves,
            signals = (object)(includeMetadata ? GetAllSignals() : GetAllSignals().Select(s => new
            {
                s.Key,
                s.Value,
                s.Confidence,
                s.Source
            }))
        };

        return JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    /// <summary>
    /// Get summary statistics about the profile.
    /// </summary>
    public ProfileStatistics GetStatistics()
    {
        var signals = GetAllSignals().ToList();

        return new ProfileStatistics
        {
            TotalSignals = signals.Count,
            UniqueKeys = _signals.Count,
            WaveCount = ContributingWaves.Count,
            AverageConfidence = signals.Any() ? signals.Average(s => s.Confidence) : 0,
            SignalsByTag = signals
                .Where(s => s.Tags != null)
                .SelectMany(s => s.Tags!)
                .GroupBy(t => t)
                .ToDictionary(g => g.Key, g => g.Count()),
            SignalsBySource = signals
                .GroupBy(s => s.Source)
                .ToDictionary(g => g.Key, g => g.Count())
        };
    }

    /// <summary>
    /// Get structured ledger of salient features for LLM synthesis.
    /// Accumulates most important signals into categorized, constrained data.
    /// </summary>
    public ImageLedger GetLedger()
    {
        return ImageLedger.FromProfile(this);
    }
}

/// <summary>
/// Statistics about a dynamic profile.
/// </summary>
public record ProfileStatistics
{
    public int TotalSignals { get; init; }
    public int UniqueKeys { get; init; }
    public int WaveCount { get; init; }
    public double AverageConfidence { get; init; }
    public Dictionary<string, int> SignalsByTag { get; init; } = new();
    public Dictionary<string, int> SignalsBySource { get; init; } = new();
}
