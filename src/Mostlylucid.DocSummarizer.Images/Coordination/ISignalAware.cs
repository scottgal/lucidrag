using System.Collections.Generic;
using System.Linq;
// Orchestration types
using LaneConfig = Mostlylucid.DocSummarizer.Images.Orchestration.LaneConfig;
using WaveManifest = Mostlylucid.DocSummarizer.Images.Orchestration.WaveManifest;

namespace Mostlylucid.DocSummarizer.Images.Coordination;

/// <summary>
/// Interface for waves that declare their signal contracts.
/// YAML manifests are the source of truth - this interface provides programmatic access.
/// </summary>
public interface ISignalAware
{
    /// <summary>
    /// Name of the wave (must match YAML manifest name).
    /// </summary>
    string ManifestName { get; }

    /// <summary>
    /// Signals this wave emits on successful completion.
    /// </summary>
    IReadOnlyList<string> EmittedSignals { get; }

    /// <summary>
    /// Signals this wave requires to run.
    /// </summary>
    IReadOnlyList<string> RequiredSignals { get; }

    /// <summary>
    /// Signals this wave optionally uses if available.
    /// </summary>
    IReadOnlyList<string> OptionalSignals { get; }

    /// <summary>
    /// Cache keys this wave produces for downstream waves.
    /// </summary>
    IReadOnlyList<string> CacheEmits { get; }

    /// <summary>
    /// Cache keys this wave consumes from upstream waves.
    /// </summary>
    IReadOnlyList<string> CacheUses { get; }
}

/// <summary>
/// Base class for manifest-backed signal-aware waves.
/// Reads signal contracts from YAML manifests, making them LLM-composable.
/// </summary>
public abstract class ManifestBackedWave : ISignalAware
{
    private readonly WaveManifestLoader _manifestLoader;
    private WaveManifest? _manifest;

    /// <summary>
    /// Name of the wave - must match the YAML manifest name.
    /// </summary>
    public abstract string ManifestName { get; }

    protected ManifestBackedWave(WaveManifestLoader manifestLoader)
    {
        _manifestLoader = manifestLoader;
    }

    /// <summary>
    /// Gets the wave's manifest, loading it on first access.
    /// </summary>
    protected WaveManifest Manifest => _manifest ??= _manifestLoader.GetManifest(ManifestName)
        ?? throw new InvalidOperationException($"No manifest found for wave: {ManifestName}");

    /// <summary>
    /// Signals emitted on completion, from YAML manifest.
    /// </summary>
    public IReadOnlyList<string> EmittedSignals =>
        Manifest.Emits.OnComplete.Select(s => s.Key).ToList();

    /// <summary>
    /// Required signals to trigger this wave, from YAML manifest.
    /// </summary>
    public IReadOnlyList<string> RequiredSignals =>
        Manifest.Listens.Required;

    /// <summary>
    /// Optional signals this wave can use, from YAML manifest.
    /// </summary>
    public IReadOnlyList<string> OptionalSignals =>
        Manifest.Listens.Optional;

    /// <summary>
    /// Cache keys this wave emits, from YAML manifest.
    /// </summary>
    public IReadOnlyList<string> CacheEmits =>
        Manifest.Cache.Emits.Select(c => c.Key).ToList();

    /// <summary>
    /// Cache keys this wave uses, from YAML manifest.
    /// </summary>
    public IReadOnlyList<string> CacheUses =>
        Manifest.Cache.Uses;

    /// <summary>
    /// Wave priority from manifest.
    /// </summary>
    public int Priority => Manifest.Priority;

    /// <summary>
    /// Wave tags from manifest.
    /// </summary>
    public IReadOnlyList<string> Tags => Manifest.Tags;

    /// <summary>
    /// Lane configuration from manifest.
    /// </summary>
    public LaneConfig Lane => Manifest.Lane;
}

/// <summary>
/// Helper to get signal contracts from a manifest.
/// Used by waves that don't inherit from ManifestBackedWave.
/// </summary>
public static class ManifestSignalContract
{
    /// <summary>
    /// Get signal contract for a wave from its manifest.
    /// </summary>
    public static SignalContract FromManifest(WaveManifest manifest)
    {
        return new SignalContract
        {
            WaveName = manifest.Name,
            EmittedSignals = manifest.Emits.OnComplete.Select(s => s.Key).ToList(),
            RequiredSignals = manifest.Listens.Required,
            OptionalSignals = manifest.Listens.Optional,
            CacheEmits = manifest.Cache.Emits.Select(c => c.Key).ToList(),
            CacheUses = manifest.Cache.Uses,
            StartSignals = manifest.Emits.OnStart,
            FailureSignals = manifest.Emits.OnFailure,
            Priority = manifest.Priority,
            Lane = manifest.Lane.Name,
            Tags = manifest.Tags
        };
    }
}

/// <summary>
/// Complete signal contract for a wave, derived from YAML manifest.
/// This is the LLM-readable representation of what a wave does.
/// </summary>
public sealed class SignalContract
{
    public required string WaveName { get; init; }
    public IReadOnlyList<string> EmittedSignals { get; init; } = [];
    public IReadOnlyList<string> RequiredSignals { get; init; } = [];
    public IReadOnlyList<string> OptionalSignals { get; init; } = [];
    public IReadOnlyList<string> CacheEmits { get; init; } = [];
    public IReadOnlyList<string> CacheUses { get; init; } = [];
    public IReadOnlyList<string> StartSignals { get; init; } = [];
    public IReadOnlyList<string> FailureSignals { get; init; } = [];
    public int Priority { get; init; }
    public string Lane { get; init; } = "default";
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Get a human/LLM-readable description of this wave's contract.
    /// </summary>
    public string ToDescription()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Wave: {WaveName} (priority: {Priority}, lane: {Lane})");

        if (RequiredSignals.Count > 0)
            sb.AppendLine($"  Requires: {string.Join(", ", RequiredSignals)}");

        if (OptionalSignals.Count > 0)
            sb.AppendLine($"  Uses: {string.Join(", ", OptionalSignals)}");

        if (EmittedSignals.Count > 0)
            sb.AppendLine($"  Emits: {string.Join(", ", EmittedSignals)}");

        if (CacheEmits.Count > 0)
            sb.AppendLine($"  Caches: {string.Join(", ", CacheEmits)}");

        if (Tags.Count > 0)
            sb.AppendLine($"  Tags: {string.Join(", ", Tags)}");

        return sb.ToString();
    }
}

/// <summary>
/// Base class for signal-aware waves with declarative contracts (legacy).
/// Prefer ManifestBackedWave for new waves.
/// </summary>
public abstract class SignalAwareWaveBase : ISignalAware
{
    public abstract string Name { get; }
    public string ManifestName => Name;
    public abstract int Priority { get; }

    public virtual IReadOnlyList<string> EmittedSignals => [];
    public virtual IReadOnlyList<string> RequiredSignals => [];
    public virtual IReadOnlyList<string> OptionalSignals => [];
    public virtual IReadOnlyList<string> CacheEmits => [];
    public virtual IReadOnlyList<string> CacheUses => [];

    public virtual IReadOnlyList<string> Tags => [];
}

/// <summary>
/// Attribute for declaring emitted signals on wave classes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class EmitsSignalAttribute : Attribute
{
    public string SignalKey { get; }
    public string? Type { get; init; }
    public string? Description { get; init; }
    public double MinConfidence { get; init; } = 0.5;
    public double MaxConfidence { get; init; } = 1.0;

    public EmitsSignalAttribute(string signalKey)
    {
        SignalKey = signalKey;
    }
}

/// <summary>
/// Attribute for declaring required signals on wave classes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequiresSignalAttribute : Attribute
{
    public string SignalKey { get; }
    public string? Condition { get; init; }

    public RequiresSignalAttribute(string signalKey)
    {
        SignalKey = signalKey;
    }
}

/// <summary>
/// Attribute for declaring optional signals on wave classes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class UsesSignalAttribute : Attribute
{
    public string SignalKey { get; }

    public UsesSignalAttribute(string signalKey)
    {
        SignalKey = signalKey;
    }
}

/// <summary>
/// Attribute for declaring cache emissions on wave classes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class CachesAttribute : Attribute
{
    public string CacheKey { get; }
    public string? Type { get; init; }

    public CachesAttribute(string cacheKey)
    {
        CacheKey = cacheKey;
    }
}

/// <summary>
/// Attribute for declaring cache usage on wave classes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class UsesCacheAttribute : Attribute
{
    public string CacheKey { get; }

    public UsesCacheAttribute(string cacheKey)
    {
        CacheKey = cacheKey;
    }
}
