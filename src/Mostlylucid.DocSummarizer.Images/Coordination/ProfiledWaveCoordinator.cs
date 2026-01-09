using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
// Orchestration types (using aliases to avoid conflict with Signal class)
using WaveManifest = Mostlylucid.DocSummarizer.Images.Orchestration.WaveManifest;
using LaneConfig = Mostlylucid.DocSummarizer.Images.Orchestration.LaneConfig;
using SignalScope = Mostlylucid.DocSummarizer.Images.Orchestration.SignalScope;

namespace Mostlylucid.DocSummarizer.Images.Coordination;

/// <summary>
/// Profiled wave coordinator that uses coordinator profiles for execution.
/// Each profile defines lane configurations, timeouts, and enabled waves.
/// YAML manifests define signal contracts; profiles define execution context.
/// </summary>
public sealed class ProfiledWaveCoordinator : IDisposable
{
    private readonly WaveManifestLoader _manifestLoader;
    private readonly ILogger<ProfiledWaveCoordinator>? _logger;
    private readonly ConcurrentDictionary<string, IAnalysisWave> _waves = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _laneSemaphores = new();

    /// <summary>
    /// Event fired when a signal is emitted.
    /// </summary>
    public event Action<SignalEvent>? OnSignalEmitted;

    public ProfiledWaveCoordinator(
        WaveManifestLoader manifestLoader,
        ILogger<ProfiledWaveCoordinator>? logger = null)
    {
        _manifestLoader = manifestLoader;
        _logger = logger;

        // Load embedded manifests
        _manifestLoader.LoadEmbeddedManifests();
    }

    /// <summary>
    /// Register a wave implementation.
    /// </summary>
    public void RegisterWave(IAnalysisWave wave)
    {
        _waves[wave.Name] = wave;
        _logger?.LogDebug("Registered wave: {Wave}", wave.Name);
    }

    /// <summary>
    /// Execute analysis using a specific profile with parallel wave execution.
    /// Waves without dependencies run first in parallel, then dependent waves.
    /// </summary>
    public async Task<AnalysisResult> ExecuteAsync(
        string imagePath,
        CoordinatorProfile profile,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new ConcurrentBag<Signal>();
        var executionLog = new ConcurrentBag<WaveExecutionLog>();
        var startTime = DateTimeOffset.UtcNow;

        _logger?.LogInformation(
            "Starting {Profile} analysis for {Image}",
            profile.Name, Path.GetFileName(imagePath));

        // Get waves in execution order, filtered by profile
        var allManifests = _manifestLoader.GetOrderedManifests()
            .Where(m => IsWaveEnabledForProfile(m, profile))
            .ToList();

        var availableSignals = new ConcurrentDictionary<string, bool>();
        var completedWaves = new ConcurrentDictionary<string, bool>();

        // Initialize lane semaphores for this profile
        InitializeLanes(profile);

        // Separate waves into groups: no dependencies (can run immediately) vs has dependencies
        var wavesWithoutDeps = allManifests
            .Where(m => !m.Listens.Required.Any() && !m.Triggers.Requires.Any())
            .ToList();
        var wavesWithDeps = allManifests
            .Except(wavesWithoutDeps)
            .ToList();

        _logger?.LogDebug("Running {NoDeps} waves in parallel (no deps), {WithDeps} waves sequentially",
            wavesWithoutDeps.Count, wavesWithDeps.Count);

        // Run waves without dependencies in parallel first
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = profile.Lanes.Values.Sum(l => l.MaxConcurrency),
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(wavesWithoutDeps, parallelOptions, async (manifest, token) =>
        {
            await ExecuteWaveAsync(manifest, imagePath, context, profile, signals, executionLog, availableSignals, token);
            completedWaves[manifest.Name] = true;
        });

        // Run waves with dependencies - batch by satisfied dependencies
        var remainingWaves = new List<WaveManifest>(wavesWithDeps);
        var maxIterations = 10;

        for (var iteration = 0; iteration < maxIterations && remainingWaves.Count > 0; iteration++)
        {
            if (ct.IsCancellationRequested) break;

            // Find waves whose dependencies are now satisfied
            var currentSignals = availableSignals.Keys.ToHashSet();
            var runnableWaves = remainingWaves
                .Where(m => _manifestLoader.CanRun(m, currentSignals))
                .ToList();

            if (runnableWaves.Count == 0)
            {
                _logger?.LogDebug("No more waves can run, {Remaining} waves skipped", remainingWaves.Count);
                break;
            }

            // Remove runnable from remaining
            foreach (var wave in runnableWaves)
                remainingWaves.Remove(wave);

            // Run this batch in parallel
            await Parallel.ForEachAsync(runnableWaves, parallelOptions, async (manifest, token) =>
            {
                await ExecuteWaveAsync(manifest, imagePath, context, profile, signals, executionLog, availableSignals, token);
                completedWaves[manifest.Name] = true;
            });
        }

        var totalDuration = DateTimeOffset.UtcNow - startTime;

        _logger?.LogInformation(
            "{Profile} analysis complete: {WaveCount} waves, {SignalCount} signals, {Duration}ms",
            profile.Name, executionLog.Count, signals.Count, totalDuration.TotalMilliseconds);

        return new AnalysisResult(
            Profile: profile.Name,
            Signals: signals.ToList(),
            ExecutionLog: executionLog.ToList(),
            TotalDuration: totalDuration);
    }

    private async Task ExecuteWaveAsync(
        WaveManifest manifest,
        string imagePath,
        AnalysisContext context,
        CoordinatorProfile profile,
        ConcurrentBag<Signal> signals,
        ConcurrentBag<WaveExecutionLog> executionLog,
        ConcurrentDictionary<string, bool> availableSignals,
        CancellationToken ct)
    {
        if (!_waves.TryGetValue(manifest.Name, out var wave))
        {
            _logger?.LogTrace("No implementation for wave: {Wave}", manifest.Name);
            return;
        }

        // Get lane semaphore
        var lane = GetLaneForWave(manifest, profile);
        var semaphore = GetLaneSemaphore(lane);

        try
        {
            // Acquire lane slot
            var acquired = await semaphore.WaitAsync(profile.DefaultTimeout, ct);
            if (!acquired)
            {
                _logger?.LogWarning("Timeout acquiring lane {Lane} for {Wave}", lane.Name, manifest.Name);
                EmitSignal($"wave.timeout.{manifest.Name}", true, manifest.Name, signals, profile.Scope);
                return;
            }

            try
            {
                // Emit start signal
                foreach (var startSignal in manifest.Emits.OnStart)
                {
                    EmitSignal(startSignal, true, manifest.Name, signals, profile.Scope);
                }

                var waveStart = DateTimeOffset.UtcNow;

                // Execute wave with timeout
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(profile.DefaultTimeout);

                var waveSignals = await wave.AnalyzeAsync(imagePath, context, cts.Token);

                var duration = DateTimeOffset.UtcNow - waveStart;
                var signalCount = 0;

                // Process emitted signals
                foreach (var signal in waveSignals)
                {
                    signals.Add(signal);
                    availableSignals[signal.Key] = true;
                    signalCount++;

                    // Notify subscribers
                    OnSignalEmitted?.Invoke(new SignalEvent(
                        Signal: signal.Key,
                        Value: signal.Value,
                        Source: signal.Source,
                        Confidence: signal.Confidence,
                        Timestamp: DateTimeOffset.UtcNow
                    ));
                }

                executionLog.Add(new WaveExecutionLog(
                    manifest.Name,
                    duration,
                    signalCount,
                    Success: true,
                    Error: null));

                _logger?.LogDebug("{Wave} completed in {Duration}ms ({Signals} signals)",
                    manifest.Name, duration.TotalMilliseconds, signalCount);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogWarning("{Wave} timed out", manifest.Name);
                EmitSignal($"wave.timeout.{manifest.Name}", true, manifest.Name, signals, profile.Scope);
                executionLog.Add(new WaveExecutionLog(manifest.Name, TimeSpan.Zero, 0, Success: false, Error: "Timeout"));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "{Wave} failed", manifest.Name);
                foreach (var failSignal in manifest.Emits.OnFailure)
                {
                    EmitSignal(failSignal, true, manifest.Name, signals, profile.Scope);
                }
                executionLog.Add(new WaveExecutionLog(manifest.Name, TimeSpan.Zero, 0, Success: false, Error: ex.Message));
            }
            finally
            {
                semaphore.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to execute {Wave}", manifest.Name);
        }
    }

    [Obsolete("Use ExecuteAsync which supports parallel execution")]
    private async Task<AnalysisResult> ExecuteSequentialAsync(
        string imagePath,
        CoordinatorProfile profile,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();
        var executionLog = new List<WaveExecutionLog>();
        var startTime = DateTimeOffset.UtcNow;

        var manifests = _manifestLoader.GetOrderedManifests()
            .Where(m => IsWaveEnabledForProfile(m, profile))
            .ToList();

        var availableSignals = new HashSet<string>();

        InitializeLanes(profile);

        foreach (var manifest in manifests)
        {
            if (ct.IsCancellationRequested) break;

            if (!_waves.TryGetValue(manifest.Name, out var wave))
            {
                _logger?.LogTrace("No implementation for wave: {Wave}", manifest.Name);
                continue;
            }

            // Check if wave can run based on signals
            if (!_manifestLoader.CanRun(manifest, availableSignals))
            {
                _logger?.LogDebug("Skipping {Wave}: dependencies not met", manifest.Name);
                EmitSignal($"wave.skipped.{manifest.Name}", true, manifest.Name, signals, profile.Scope);
                continue;
            }

            // Get lane semaphore
            var lane = GetLaneForWave(manifest, profile);
            var semaphore = GetLaneSemaphore(lane);

            try
            {
                // Acquire lane slot
                var acquired = await semaphore.WaitAsync(
                    profile.DefaultTimeout,
                    ct);

                if (!acquired)
                {
                    _logger?.LogWarning("Timeout acquiring lane {Lane} for {Wave}", lane.Name, manifest.Name);
                    EmitSignal($"wave.timeout.{manifest.Name}", true, manifest.Name, signals, profile.Scope);
                    continue;
                }

                // Emit start signal
                foreach (var startSignal in manifest.Emits.OnStart)
                {
                    EmitSignal(startSignal, true, manifest.Name, signals, profile.Scope);
                }

                var waveStart = DateTimeOffset.UtcNow;

                try
                {
                    // Execute wave with timeout
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(profile.DefaultTimeout);

                    var waveSignals = await wave.AnalyzeAsync(imagePath, context, cts.Token);

                    var duration = DateTimeOffset.UtcNow - waveStart;
                    var signalCount = 0;

                    // Process emitted signals
                    foreach (var signal in waveSignals)
                    {
                        signals.Add(signal);
                        availableSignals.Add(signal.Key);
                        signalCount++;

                        // Notify subscribers
                        OnSignalEmitted?.Invoke(new SignalEvent(
                            Signal: signal.Key,
                            Value: signal.Value,
                            Source: signal.Source,
                            Confidence: signal.Confidence,
                            Timestamp: DateTimeOffset.UtcNow
                        ));
                    }

                    executionLog.Add(new WaveExecutionLog(
                        manifest.Name,
                        duration,
                        signalCount,
                        Success: true,
                        Error: null));

                    _logger?.LogDebug(
                        "{Wave} completed in {Duration}ms ({Signals} signals)",
                        manifest.Name, duration.TotalMilliseconds, signalCount);
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogWarning("{Wave} timed out", manifest.Name);
                    EmitSignal($"wave.timeout.{manifest.Name}", true, manifest.Name, signals, profile.Scope);

                    executionLog.Add(new WaveExecutionLog(
                        manifest.Name,
                        DateTimeOffset.UtcNow - waveStart,
                        0,
                        Success: false,
                        Error: "Timeout"));
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "{Wave} failed", manifest.Name);

                    foreach (var failSignal in manifest.Emits.OnFailure)
                    {
                        EmitSignal(failSignal, true, manifest.Name, signals, profile.Scope);
                    }

                    executionLog.Add(new WaveExecutionLog(
                        manifest.Name,
                        DateTimeOffset.UtcNow - waveStart,
                        0,
                        Success: false,
                        Error: ex.Message));
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        var totalDuration = DateTimeOffset.UtcNow - startTime;

        _logger?.LogInformation(
            "{Profile} analysis complete: {WaveCount} waves, {SignalCount} signals, {Duration}ms",
            profile.Name, executionLog.Count, signals.Count, totalDuration.TotalMilliseconds);

        return new AnalysisResult(
            Profile: profile.Name,
            Signals: signals,
            ExecutionLog: executionLog,
            TotalDuration: totalDuration);
    }

    /// <summary>
    /// Execute using the single-request profile (default for API/UI).
    /// </summary>
    public Task<AnalysisResult> ExecuteSingleRequestAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        return ExecuteAsync(imagePath, CoordinatorProfiles.SingleRequest, context, ct);
    }

    /// <summary>
    /// Execute using the batch profile (for bulk processing).
    /// </summary>
    public Task<AnalysisResult> ExecuteBatchAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        return ExecuteAsync(imagePath, CoordinatorProfiles.Batch, context, ct);
    }

    /// <summary>
    /// Execute using the streaming profile (low latency).
    /// </summary>
    public Task<AnalysisResult> ExecuteStreamingAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        return ExecuteAsync(imagePath, CoordinatorProfiles.Streaming, context, ct);
    }

    /// <summary>
    /// Execute using the quality profile (comprehensive analysis).
    /// </summary>
    public Task<AnalysisResult> ExecuteQualityAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        return ExecuteAsync(imagePath, CoordinatorProfiles.Quality, context, ct);
    }

    /// <summary>
    /// Get signal contracts summary for LLM consumption.
    /// </summary>
    public string GetSignalContractsSummary() => _manifestLoader.GetSignalContractsSummary();

    /// <summary>
    /// Get all signal contracts.
    /// </summary>
    public IReadOnlyList<SignalContract> GetAllContracts() => _manifestLoader.GetAllContracts();

    /// <summary>
    /// Get dependency graph for visualization.
    /// </summary>
    public Dictionary<string, HashSet<string>> GetDependencyGraph() => _manifestLoader.BuildDependencyGraph();

    private bool IsWaveEnabledForProfile(WaveManifest manifest, CoordinatorProfile profile)
    {
        // If EnabledWaves is null, all waves are enabled
        if (profile.EnabledWaves == null)
            return manifest.Enabled;

        // Check if wave is in the enabled list
        return manifest.Enabled && profile.EnabledWaves.Contains(manifest.Name);
    }

    private LaneConfig GetLaneForWave(WaveManifest manifest, CoordinatorProfile profile)
    {
        // Use profile's lane config if it exists for this lane name
        if (profile.Lanes.TryGetValue(manifest.Lane.Name, out var profileLane))
        {
            return profileLane;
        }

        // Fall back to manifest's lane config
        return manifest.Lane;
    }

    private void InitializeLanes(CoordinatorProfile profile)
    {
        foreach (var (name, config) in profile.Lanes)
        {
            _laneSemaphores.GetOrAdd(
                $"{profile.Name}:{name}",
                _ => new SemaphoreSlim(config.MaxConcurrency, config.MaxConcurrency));
        }
    }

    private SemaphoreSlim GetLaneSemaphore(LaneConfig lane)
    {
        return _laneSemaphores.GetOrAdd(
            lane.Name,
            _ => new SemaphoreSlim(lane.MaxConcurrency, lane.MaxConcurrency));
    }

    private void EmitSignal(
        string key,
        object value,
        string source,
        List<Signal> signals,
        SignalScope scope)
    {
        var signal = new Signal
        {
            Key = key,
            Value = value,
            Source = source,
            Confidence = 1.0,
            Tags = new List<string> { "coordinator", scope.Coordinator }
        };

        signals.Add(signal);

        OnSignalEmitted?.Invoke(new SignalEvent(
            Signal: key,
            Value: value,
            Source: source,
            Confidence: 1.0,
            Timestamp: DateTimeOffset.UtcNow
        ));
    }

    private void EmitSignal(
        string key,
        object value,
        string source,
        ConcurrentBag<Signal> signals,
        SignalScope scope)
    {
        var signal = new Signal
        {
            Key = key,
            Value = value,
            Source = source,
            Confidence = 1.0,
            Tags = new List<string> { "coordinator", scope.Coordinator }
        };

        signals.Add(signal);

        OnSignalEmitted?.Invoke(new SignalEvent(
            Signal: key,
            Value: value,
            Source: source,
            Confidence: 1.0,
            Timestamp: DateTimeOffset.UtcNow
        ));
    }

    public void Dispose()
    {
        foreach (var semaphore in _laneSemaphores.Values)
        {
            semaphore.Dispose();
        }
        _laneSemaphores.Clear();
    }
}

/// <summary>
/// Wave execution log entry.
/// </summary>
public readonly record struct WaveExecutionLog(
    string WaveName,
    TimeSpan Duration,
    int SignalsEmitted,
    bool Success,
    string? Error
);

/// <summary>
/// Analysis result from a profiled coordinator execution.
/// </summary>
public sealed record AnalysisResult(
    string Profile,
    IReadOnlyList<Signal> Signals,
    IReadOnlyList<WaveExecutionLog> ExecutionLog,
    TimeSpan TotalDuration
)
{
    /// <summary>
    /// Get a signal value by key.
    /// </summary>
    public T? GetValue<T>(string key)
    {
        var signal = Signals.FirstOrDefault(s => s.Key == key);
        if (signal == null) return default;
        return signal.Value is T typed ? typed : default;
    }

    /// <summary>
    /// Get all signals matching a prefix.
    /// </summary>
    public IEnumerable<Signal> GetSignals(string prefix)
    {
        return Signals.Where(s => s.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Get execution summary for logging/debugging.
    /// </summary>
    public string GetExecutionSummary()
    {
        var successful = ExecutionLog.Count(e => e.Success);
        var failed = ExecutionLog.Count(e => !e.Success);

        return $"Profile: {Profile}, " +
               $"Waves: {successful} success / {failed} failed, " +
               $"Signals: {Signals.Count}, " +
               $"Duration: {TotalDuration.TotalMilliseconds:F0}ms";
    }
}
