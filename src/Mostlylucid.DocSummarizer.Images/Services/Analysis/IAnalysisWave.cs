using Mostlylucid.DocSummarizer.Images.Models.Dynamic;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Interface for pluggable image analysis components that contribute signals to a dynamic profile.
/// Each wave is an independent analyzer that produces signals about different aspects of an image.
/// </summary>
public interface IAnalysisWave
{
    /// <summary>
    /// Unique name identifying this analysis wave.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Priority for execution order. Higher priority waves run first.
    /// Allows dependencies between waves (e.g., forensics wave may depend on color wave results).
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Tags describing what category of analysis this wave provides.
    /// </summary>
    IReadOnlyList<string> Tags { get; }

    /// <summary>
    /// Analyze an image and produce signals.
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <param name="context">Shared context with results from previously executed waves</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Collection of signals produced by this wave</returns>
    Task<IEnumerable<Signal>> AnalyzeAsync(string imagePath, AnalysisContext context, CancellationToken ct = default);
}

/// <summary>
/// Shared context passed between analysis waves.
/// Allows waves to access results from higher-priority waves.
/// </summary>
public class AnalysisContext
{
    private readonly Dictionary<string, List<Signal>> _signals = new();
    private readonly Dictionary<string, object> _cache = new();

    /// <summary>
    /// Add a signal to the context.
    /// </summary>
    public void AddSignal(Signal signal)
    {
        if (!_signals.ContainsKey(signal.Key))
        {
            _signals[signal.Key] = new List<Signal>();
        }
        _signals[signal.Key].Add(signal);
    }

    /// <summary>
    /// Add multiple signals to the context.
    /// </summary>
    public void AddSignals(IEnumerable<Signal> signals)
    {
        foreach (var signal in signals)
        {
            AddSignal(signal);
        }
    }

    /// <summary>
    /// Get all signals for a given key.
    /// </summary>
    public IEnumerable<Signal> GetSignals(string key)
    {
        return _signals.TryGetValue(key, out var signals) ? signals : Enumerable.Empty<Signal>();
    }

    /// <summary>
    /// Get the most confident signal for a key.
    /// </summary>
    public Signal? GetBestSignal(string key)
    {
        return GetSignals(key).OrderByDescending(s => s.Confidence).FirstOrDefault();
    }

    /// <summary>
    /// Get value from the most confident signal.
    /// </summary>
    public T? GetValue<T>(string key)
    {
        var signal = GetBestSignal(key);
        return signal?.Value is T value ? value : default;
    }

    /// <summary>
    /// Check if a signal exists for a key.
    /// </summary>
    public bool HasSignal(string key)
    {
        return _signals.ContainsKey(key) && _signals[key].Any();
    }

    /// <summary>
    /// Get all signals.
    /// </summary>
    public IEnumerable<Signal> GetAllSignals()
    {
        return _signals.Values.SelectMany(s => s);
    }

    /// <summary>
    /// Cache arbitrary data for sharing between waves.
    /// </summary>
    public void SetCached<T>(string key, T value)
    {
        _cache[key] = value!;
    }

    /// <summary>
    /// Retrieve cached data.
    /// </summary>
    public T? GetCached<T>(string key)
    {
        return _cache.TryGetValue(key, out var value) && value is T typed ? typed : default;
    }

    /// <summary>
    /// Clear all cached data (useful for freeing memory after analysis).
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
    }
}
