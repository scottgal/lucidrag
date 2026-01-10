using System.Collections.Immutable;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.DocSummarizer.Images.Orchestration;

/// <summary>
///     Base class for contributing waves that read their configuration from YAML manifests.
///     Provides easy access to weights, confidence values, and parameters - NO MAGIC NUMBERS in code!
///     Follows the BotDetection ConfiguredContributorBase pattern.
/// </summary>
/// <remarks>
///     Configuration hierarchy (highest precedence first):
///     1. appsettings.json DocSummarizer:Images:Waves:{Name}:Defaults:*
///     2. YAML manifest defaults (from *.wave.yaml)
///     3. Built-in code defaults
///
///     Waves should use the Config property to access all configurable values.
/// </remarks>
public abstract class ConfiguredWaveBase : IContributingWave
{
    private readonly IWaveConfigProvider _configProvider;
    private WaveDefaults? _cachedConfig;
    private WaveManifest? _cachedManifest;

    protected ConfiguredWaveBase(IWaveConfigProvider configProvider)
    {
        _configProvider = configProvider;
    }

    /// <summary>
    ///     The manifest name to load configuration from.
    ///     Override if your wave name doesn't match the manifest name.
    /// </summary>
    protected virtual string ManifestName => Name;

    /// <summary>
    ///     Get the resolved configuration (YAML + appsettings overrides).
    /// </summary>
    protected WaveDefaults Config => _cachedConfig ??= _configProvider.GetDefaults(ManifestName);

    /// <summary>
    ///     Get the raw manifest (for signal contracts, triggers, etc.)
    /// </summary>
    protected WaveManifest? Manifest => _cachedManifest ??= _configProvider.GetManifest(ManifestName);

    // ===== IContributingWave Implementation =====

    public abstract string Name { get; }

    public virtual int Priority => Manifest?.Priority ?? 100;

    public virtual IReadOnlyList<string> Tags => (IReadOnlyList<string>?)Manifest?.Tags ?? Array.Empty<string>();

    public virtual bool IsEnabled => Manifest?.Enabled ?? true;

    public virtual IReadOnlyList<TriggerCondition> TriggerConditions => BuildTriggerConditions();

    public virtual TimeSpan TriggerTimeout => TimeSpan.FromMilliseconds(500);

    public virtual TimeSpan ExecutionTimeout => TimeSpan.FromMilliseconds(Config.Timing.TimeoutMs);

    public virtual bool IsOptional => true;

    public abstract Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        ImageBlackboardState state,
        CancellationToken cancellationToken = default);

    // ===== Weight Shortcuts =====

    /// <summary>Base weight for contributions.</summary>
    protected double WeightBase => Config.Weights.Base;

    /// <summary>Weight multiplier for high-confidence signals.</summary>
    protected double WeightHighConfidence => Config.Weights.HighConfidence;

    /// <summary>Weight multiplier for low-confidence signals.</summary>
    protected double WeightLowConfidence => Config.Weights.LowConfidence;

    /// <summary>Weight multiplier for verified results.</summary>
    protected double WeightVerified => Config.Weights.Verified;

    // ===== Confidence Shortcuts =====

    /// <summary>Default confidence for neutral results.</summary>
    protected double ConfidenceNeutral => Config.Confidence.Neutral;

    /// <summary>Confidence for high-quality results.</summary>
    protected double ConfidenceHigh => Config.Confidence.High;

    /// <summary>Confidence for medium-quality results.</summary>
    protected double ConfidenceMedium => Config.Confidence.Medium;

    /// <summary>Confidence for low-quality results.</summary>
    protected double ConfidenceLow => Config.Confidence.Low;

    /// <summary>High confidence threshold.</summary>
    protected double ThresholdHigh => Config.Confidence.HighThreshold;

    /// <summary>Escalation threshold.</summary>
    protected double ThresholdEscalation => Config.Confidence.EscalationThreshold;

    // ===== Feature Shortcuts =====

    /// <summary>Whether detailed logging is enabled.</summary>
    protected bool DetailedLogging => Config.Features.DetailedLogging;

    /// <summary>Whether caching is enabled.</summary>
    protected bool CacheEnabled => Config.Features.EnableCache;

    /// <summary>Whether this wave can trigger early exit.</summary>
    protected bool CanTriggerEarlyExit => Config.Features.CanEarlyExit;

    /// <summary>Whether this wave can escalate to AI.</summary>
    protected bool CanEscalateToAi => Config.Features.CanEscalate;

    // ===== Parameter Access =====

    /// <summary>
    ///     Get a typed parameter from the manifest.
    /// </summary>
    protected T GetParam<T>(string name, T defaultValue)
    {
        return _configProvider.GetParameter(ManifestName, name, defaultValue);
    }

    /// <summary>
    ///     Get a list parameter from the manifest.
    /// </summary>
    protected IReadOnlyList<string> GetStringListParam(string name)
    {
        if (Config.Parameters.TryGetValue(name, out var value))
        {
            if (value is IEnumerable<object> enumerable)
                return enumerable.Select(x => x?.ToString() ?? "").ToList();
            if (value is IEnumerable<string> strings)
                return strings.ToList();
        }

        return Array.Empty<string>();
    }

    // ===== Trigger Condition Building =====

    private IReadOnlyList<TriggerCondition> BuildTriggerConditions()
    {
        var manifest = Manifest;
        if (manifest == null)
            return Array.Empty<TriggerCondition>();

        var conditions = new List<TriggerCondition>();

        // Required signals
        foreach (var req in manifest.Triggers.Requires)
        {
            conditions.Add(new SignalExistsTrigger(req.Signal));
        }

        // Any-of signals
        if (manifest.Triggers.Signals.Count > 0)
        {
            conditions.Add(new AnyOfTrigger(
                manifest.Triggers.Signals.Select(s => new SignalExistsTrigger(s) as TriggerCondition).ToList()));
        }

        return conditions;
    }

    // ===== Contribution Helpers =====

    /// <summary>
    ///     Helper to return a single contribution.
    /// </summary>
    protected static IReadOnlyList<DetectionContribution> Single(DetectionContribution contribution)
    {
        return new[] { contribution };
    }

    /// <summary>
    ///     Helper to return multiple contributions.
    /// </summary>
    protected static IReadOnlyList<DetectionContribution> Multiple(params DetectionContribution[] contributions)
    {
        return contributions;
    }

    /// <summary>
    ///     Helper to return no contributions.
    /// </summary>
    protected static IReadOnlyList<DetectionContribution> None()
    {
        return Array.Empty<DetectionContribution>();
    }

    /// <summary>
    ///     Create an info contribution with signals.
    /// </summary>
    protected DetectionContribution Info(
        string category,
        string reason,
        Dictionary<string, object>? signals = null)
    {
        return DetectionContribution.Info(Name, category, reason, signals);
    }

    /// <summary>
    ///     Create a high-confidence contribution.
    /// </summary>
    protected DetectionContribution HighConfidenceContribution(
        string category,
        string reason,
        Dictionary<string, object>? signals = null)
    {
        return new DetectionContribution
        {
            DetectorName = Name,
            Category = category,
            ConfidenceDelta = ConfidenceHigh - 0.5, // Convert to delta from 0.5 baseline
            Weight = WeightBase * WeightHighConfidence,
            Salience = ConfidenceHigh,
            Reason = reason,
            Signals = signals?.ToImmutableDictionary() ?? ImmutableDictionary<string, object>.Empty
        };
    }

    /// <summary>
    ///     Create a medium-confidence contribution.
    /// </summary>
    protected DetectionContribution MediumConfidenceContribution(
        string category,
        string reason,
        Dictionary<string, object>? signals = null)
    {
        return new DetectionContribution
        {
            DetectorName = Name,
            Category = category,
            ConfidenceDelta = ConfidenceMedium - 0.5,
            Weight = WeightBase,
            Salience = ConfidenceMedium,
            Reason = reason,
            Signals = signals?.ToImmutableDictionary() ?? ImmutableDictionary<string, object>.Empty
        };
    }

    /// <summary>
    ///     Create a low-confidence contribution.
    /// </summary>
    protected DetectionContribution LowConfidenceContribution(
        string category,
        string reason,
        Dictionary<string, object>? signals = null)
    {
        return new DetectionContribution
        {
            DetectorName = Name,
            Category = category,
            ConfidenceDelta = ConfidenceLow - 0.5,
            Weight = WeightBase * WeightLowConfidence,
            Salience = ConfidenceLow,
            Reason = reason,
            Signals = signals?.ToImmutableDictionary() ?? ImmutableDictionary<string, object>.Empty
        };
    }

    /// <summary>
    ///     Create a neutral/informational contribution.
    /// </summary>
    protected DetectionContribution NeutralContribution(string category, string reason)
    {
        return DetectionContribution.Info(Name, category, reason);
    }

    /// <summary>
    ///     Create a signal-only contribution (no confidence impact).
    /// </summary>
    protected DetectionContribution SignalContribution(
        string category,
        string reason,
        Dictionary<string, object> signals)
    {
        return new DetectionContribution
        {
            DetectorName = Name,
            Category = category,
            ConfidenceDelta = 0,
            Weight = 0,
            Salience = 0.5,
            Reason = reason,
            Signals = signals.ToImmutableDictionary()
        };
    }

    /// <summary>
    ///     Create an early-exit contribution (for definitive results).
    /// </summary>
    protected DetectionContribution EarlyExitContribution(
        string category,
        string reason,
        string verdict)
    {
        return new DetectionContribution
        {
            DetectorName = Name,
            Category = category,
            ConfidenceDelta = 0,
            Weight = WeightBase * WeightVerified,
            Salience = 1.0,
            Reason = reason,
            TriggerEarlyExit = true,
            EarlyExitVerdict = verdict
        };
    }
}
