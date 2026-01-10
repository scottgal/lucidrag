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
using EscalationCondition = Mostlylucid.DocSummarizer.Images.Orchestration.EscalationCondition;

namespace Mostlylucid.DocSummarizer.Images.Coordination;

/// <summary>
/// Coordinates wave execution based on YAML manifests and signal dependencies.
/// Integrates with ephemeral patterns for signal emission and subscription.
/// </summary>
public sealed class WaveSignalCoordinator : IDisposable
{
    private readonly WaveManifestLoader _manifestLoader;
    private readonly ILogger<WaveSignalCoordinator>? _logger;
    private readonly ConcurrentDictionary<string, IAnalysisWave> _waves = new();
    private readonly List<Signal> _signals = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _laneSemaphores = new();

    // Signal event for reactive composition
    public event Action<SignalEvent>? OnSignalEmitted;

    public WaveSignalCoordinator(
        WaveManifestLoader manifestLoader,
        ILogger<WaveSignalCoordinator>? logger = null)
    {
        _manifestLoader = manifestLoader;
        _logger = logger;
    }

    /// <summary>
    /// Register a wave implementation with the coordinator.
    /// </summary>
    public void RegisterWave(IAnalysisWave wave)
    {
        _waves[wave.Name] = wave;
        _logger?.LogDebug("Registered wave: {Wave}", wave.Name);
    }

    /// <summary>
    /// Execute waves based on manifest declarations and signal dependencies.
    /// </summary>
    public async Task<IReadOnlyList<Signal>> ExecuteAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var manifests = _manifestLoader.GetOrderedManifests();
        var availableSignals = new HashSet<string>();

        // Track execution for debugging
        var executionLog = new List<(string Wave, TimeSpan Duration, int SignalsEmitted)>();

        foreach (var manifest in manifests)
        {
            if (!manifest.Enabled) continue;
            if (!_waves.TryGetValue(manifest.Name, out var wave)) continue;

            // Check if wave can run based on signals
            if (!_manifestLoader.CanRun(manifest, availableSignals))
            {
                _logger?.LogDebug("Skipping {Wave}: dependencies not met", manifest.Name);
                EmitSignal($"wave.skipped.{manifest.Name}", true, manifest.Name);
                continue;
            }

            // Check config bindings
            if (!CheckConfigBindings(manifest, context))
            {
                _logger?.LogDebug("Skipping {Wave}: config binding disabled", manifest.Name);
                EmitSignal($"wave.skipped.{manifest.Name}", true, manifest.Name);
                continue;
            }

            // Acquire lane semaphore if configured
            var semaphore = GetLaneSemaphore(manifest.Lane);

            try
            {
                await semaphore.WaitAsync(ct);

                // Emit start signal
                foreach (var startSignal in manifest.Emits.OnStart)
                {
                    EmitSignal(startSignal, true, manifest.Name);
                }

                var startTime = DateTime.UtcNow;

                // Execute wave
                var signals = await wave.AnalyzeAsync(imagePath, context, ct);

                var duration = DateTime.UtcNow - startTime;
                var signalCount = 0;

                // Process emitted signals
                foreach (var signal in signals)
                {
                    _signals.Add(signal);
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

                executionLog.Add((manifest.Name, duration, signalCount));

                _logger?.LogDebug(
                    "Executed {Wave} in {Duration}ms, emitted {Signals} signals",
                    manifest.Name, duration.TotalMilliseconds, signalCount);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Wave {Wave} failed", manifest.Name);

                // Emit failure signals
                foreach (var failSignal in manifest.Emits.OnFailure)
                {
                    EmitSignal(failSignal, true, manifest.Name);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        // Log execution summary
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            var summary = string.Join(", ", executionLog.Select(e => $"{e.Wave}:{e.Duration.TotalMilliseconds:F0}ms"));
            _logger.LogDebug("Wave execution: {Summary}", summary);
        }

        return _signals;
    }

    /// <summary>
    /// Get waves that would run given current signals.
    /// Useful for planning/visualization.
    /// </summary>
    public IReadOnlyList<WaveManifest> GetPendingWaves(IReadOnlySet<string> availableSignals)
    {
        return _manifestLoader.GetRunnableManifests(availableSignals);
    }

    /// <summary>
    /// Get the dependency graph for visualization.
    /// </summary>
    public Dictionary<string, HashSet<string>> GetDependencyGraph()
    {
        return _manifestLoader.BuildDependencyGraph();
    }

    /// <summary>
    /// Check if a specific escalation should occur based on signals.
    /// </summary>
    public bool ShouldEscalate(WaveManifest manifest, string escalationType, AnalysisContext context)
    {
        if (manifest.Escalation?.Targets == null) return false;

        // Look up the escalation rule by name in the Targets dictionary
        if (!manifest.Escalation.Targets.TryGetValue(escalationType, out var rule))
            return false;

        if (rule == null) return false;

        // Check skip conditions first
        foreach (var skipCondition in rule.SkipWhen)
        {
            if (EvaluateCondition(skipCondition, context))
                return false;
        }

        // Check trigger conditions - any match triggers escalation
        foreach (var condition in rule.When)
        {
            if (EvaluateCondition(condition, context))
                return true;
        }

        return false;
    }

    private bool EvaluateCondition(EscalationCondition condition, AnalysisContext context)
    {
        var signalValue = context.GetValue<object>(condition.Signal);

        if (condition.Value != null)
        {
            return Equals(signalValue, condition.Value);
        }

        if (condition.Condition != null)
        {
            return condition.Condition switch
            {
                "IsNullOrWhiteSpace" => signalValue == null || string.IsNullOrWhiteSpace(signalValue.ToString()),
                "HasValue" => signalValue != null && !string.IsNullOrWhiteSpace(signalValue.ToString()),
                "> 0" => signalValue is int i && i > 0,
                _ => false
            };
        }

        return signalValue != null;
    }

    private bool CheckConfigBindings(WaveManifest manifest, AnalysisContext context)
    {
        foreach (var binding in manifest.Config.Bindings)
        {
            if (binding.SkipIfFalse)
            {
                var value = context.GetValue<bool>($"config.{binding.ConfigKey}");
                if (!value) return false;
            }
        }
        return true;
    }

    private void EmitSignal(string key, object value, string source)
    {
        var signal = new Signal
        {
            Key = key,
            Value = value,
            Source = source,
            Confidence = 1.0,
            Tags = new List<string> { "coordinator" }
        };

        _signals.Add(signal);

        OnSignalEmitted?.Invoke(new SignalEvent(
            Signal: key,
            Value: value,
            Source: source,
            Confidence: 1.0,
            Timestamp: DateTimeOffset.UtcNow
        ));
    }

    private SemaphoreSlim GetLaneSemaphore(LaneConfig lane)
    {
        return _laneSemaphores.GetOrAdd(
            lane.Name,
            _ => new SemaphoreSlim(lane.MaxConcurrency, lane.MaxConcurrency)
        );
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
/// Signal event for reactive composition.
/// </summary>
public readonly record struct SignalEvent(
    string Signal,
    object? Value,
    string Source,
    double Confidence,
    DateTimeOffset Timestamp
)
{
    public bool Is(string name) => Signal.Equals(name, StringComparison.OrdinalIgnoreCase);
    public bool StartsWith(string prefix) => Signal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
