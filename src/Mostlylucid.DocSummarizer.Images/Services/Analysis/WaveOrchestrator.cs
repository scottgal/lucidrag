using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Orchestrates execution of analysis waves to build a dynamic image profile.
/// Executes waves in priority order and manages signal aggregation.
/// </summary>
public class WaveOrchestrator
{
    private readonly IEnumerable<IAnalysisWave> _waves;
    private readonly ILogger<WaveOrchestrator>? _logger;

    public WaveOrchestrator(
        IEnumerable<IAnalysisWave> waves,
        ILogger<WaveOrchestrator>? logger = null)
    {
        _waves = waves.OrderByDescending(w => w.Priority).ToList();
        _logger = logger;
    }

    /// <summary>
    /// Analyze an image using all registered waves.
    /// </summary>
    public async Task<DynamicImageProfile> AnalyzeAsync(
        string imagePath,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var profile = new DynamicImageProfile
        {
            ImagePath = imagePath
        };

        var context = new AnalysisContext();

        _logger?.LogInformation("Starting analysis of {ImagePath} with {WaveCount} waves",
            imagePath, _waves.Count());

        // Execute waves in priority order
        foreach (var wave in _waves)
        {
            try
            {
                _logger?.LogDebug("Executing wave: {WaveName} (priority: {Priority})",
                    wave.Name, wave.Priority);

                var waveStopwatch = Stopwatch.StartNew();

                var signals = await wave.AnalyzeAsync(imagePath, context, ct);
                var signalsList = signals.ToList();

                // Add signals to both context and profile
                context.AddSignals(signalsList);
                profile.AddSignals(signalsList);

                waveStopwatch.Stop();

                _logger?.LogDebug("Wave {WaveName} completed in {Duration}ms, produced {SignalCount} signals",
                    wave.Name, waveStopwatch.ElapsedMilliseconds, signalsList.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Wave {WaveName} failed", wave.Name);

                // Add error signal
                profile.AddSignal(new Signal
                {
                    Key = $"error.{wave.Name}",
                    Value = ex.Message,
                    Confidence = 1.0,
                    Source = "WaveOrchestrator",
                    Tags = new List<string> { "error" }
                });
            }
        }

        // Clear context cache to free memory
        context.ClearCache();

        stopwatch.Stop();
        profile.AnalysisDurationMs = stopwatch.ElapsedMilliseconds;

        _logger?.LogInformation("Analysis completed in {Duration}ms, {SignalCount} total signals from {WaveCount} waves",
            stopwatch.ElapsedMilliseconds, profile.GetAllSignals().Count(), profile.ContributingWaves.Count);

        return profile;
    }

    /// <summary>
    /// Analyze an image using only specific waves (filtered by name or tag).
    /// </summary>
    public async Task<DynamicImageProfile> AnalyzeWithFilterAsync(
        string imagePath,
        Func<IAnalysisWave, bool> filter,
        CancellationToken ct = default)
    {
        var filteredWaves = _waves.Where(filter);
        var orchestrator = new WaveOrchestrator(filteredWaves, _logger);
        return await orchestrator.AnalyzeAsync(imagePath, ct);
    }

    /// <summary>
    /// Analyze an image using only waves with specific tags.
    /// </summary>
    public async Task<DynamicImageProfile> AnalyzeByTagsAsync(
        string imagePath,
        IEnumerable<string> requiredTags,
        CancellationToken ct = default)
    {
        var tags = requiredTags.ToHashSet();
        return await AnalyzeWithFilterAsync(
            imagePath,
            wave => wave.Tags.Any(t => tags.Contains(t)),
            ct);
    }

    /// <summary>
    /// Get information about registered waves.
    /// </summary>
    public IEnumerable<WaveInfo> GetRegisteredWaves()
    {
        return _waves.Select(w => new WaveInfo
        {
            Name = w.Name,
            Priority = w.Priority,
            Tags = w.Tags.ToList()
        });
    }
}

/// <summary>
/// Information about a registered analysis wave.
/// </summary>
public record WaveInfo
{
    public required string Name { get; init; }
    public int Priority { get; init; }
    public List<string> Tags { get; init; } = new();
}
