using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mostlylucid.DocSummarizer.Images.Orchestration;

/// <summary>
///     Interface for loading wave configuration from YAML manifests and appsettings overrides.
/// </summary>
public interface IWaveConfigProvider
{
    /// <summary>
    ///     Get the resolved defaults for a wave (YAML + appsettings overrides).
    /// </summary>
    WaveDefaults GetDefaults(string waveName);

    /// <summary>
    ///     Get the raw manifest for a wave.
    /// </summary>
    WaveManifest? GetManifest(string waveName);

    /// <summary>
    ///     Get a specific parameter from the wave configuration.
    /// </summary>
    T GetParameter<T>(string waveName, string parameterName, T defaultValue);

    /// <summary>
    ///     Get all loaded manifests.
    /// </summary>
    IReadOnlyList<WaveManifest> GetAllManifests();
}

/// <summary>
///     Loads wave configuration from embedded YAML manifests with appsettings overrides.
///     Follows the BotDetection DetectorConfigProvider pattern.
/// </summary>
public sealed class WaveConfigProvider : IWaveConfigProvider
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<WaveConfigProvider> _logger;
    private readonly Dictionary<string, WaveManifest> _manifests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WaveDefaults> _resolvedDefaults = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public WaveConfigProvider(
        IConfiguration configuration,
        ILogger<WaveConfigProvider> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public WaveDefaults GetDefaults(string waveName)
    {
        EnsureLoaded();

        if (_resolvedDefaults.TryGetValue(waveName, out var cached))
            return cached;

        var manifest = GetManifest(waveName);
        var defaults = manifest?.Defaults ?? new WaveDefaults();

        // Apply appsettings overrides
        var section = _configuration.GetSection($"DocSummarizer:Images:Waves:{waveName}:Defaults");
        if (section.Exists())
        {
            defaults = ApplyOverrides(defaults, section);
        }

        _resolvedDefaults[waveName] = defaults;
        return defaults;
    }

    public WaveManifest? GetManifest(string waveName)
    {
        EnsureLoaded();
        return _manifests.TryGetValue(waveName, out var manifest) ? manifest : null;
    }

    public T GetParameter<T>(string waveName, string parameterName, T defaultValue)
    {
        var defaults = GetDefaults(waveName);

        // Check appsettings override first
        var overrideValue = _configuration.GetValue<T>(
            $"DocSummarizer:Images:Waves:{waveName}:Defaults:Parameters:{parameterName}");
        if (overrideValue != null && !EqualityComparer<T>.Default.Equals(overrideValue, default!))
            return overrideValue;

        // Check YAML parameters
        if (defaults.Parameters.TryGetValue(parameterName, out var value))
        {
            if (value is T typed)
                return typed;

            // Try conversion
            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                _logger.LogWarning(
                    "Failed to convert parameter {Wave}.{Param} value {Value} to {Type}",
                    waveName, parameterName, value, typeof(T).Name);
            }
        }

        return defaultValue;
    }

    public IReadOnlyList<WaveManifest> GetAllManifests()
    {
        EnsureLoaded();
        return _manifests.Values.ToList();
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;

        lock (_manifests)
        {
            if (_loaded) return;

            LoadEmbeddedManifests();
            _loaded = true;
        }
    }

    private void LoadEmbeddedManifests()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        // Look for embedded *.wave.yaml files
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".wave.yaml", StringComparison.OrdinalIgnoreCase));

        foreach (var resourceName in resourceNames)
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    _logger.LogWarning("Could not load embedded resource: {Resource}", resourceName);
                    continue;
                }

                using var reader = new StreamReader(stream);
                var yaml = reader.ReadToEnd();

                var manifest = deserializer.Deserialize<WaveManifest>(yaml);
                if (manifest != null)
                {
                    _manifests[manifest.Name] = manifest;
                    _logger.LogDebug("Loaded wave manifest: {Name} (priority={Priority})",
                        manifest.Name, manifest.Priority);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load wave manifest from {Resource}", resourceName);
            }
        }

        _logger.LogInformation("Loaded {Count} wave manifests", _manifests.Count);
    }

    private WaveDefaults ApplyOverrides(WaveDefaults defaults, IConfigurationSection section)
    {
        // Create a new instance with overrides applied
        var weights = new WeightDefaults
        {
            Base = section.GetValue("Weights:Base", defaults.Weights.Base),
            HighConfidence = section.GetValue("Weights:HighConfidence", defaults.Weights.HighConfidence),
            LowConfidence = section.GetValue("Weights:LowConfidence", defaults.Weights.LowConfidence),
            Verified = section.GetValue("Weights:Verified", defaults.Weights.Verified)
        };

        var confidence = new ConfidenceDefaults
        {
            Neutral = section.GetValue("Confidence:Neutral", defaults.Confidence.Neutral),
            High = section.GetValue("Confidence:High", defaults.Confidence.High),
            Medium = section.GetValue("Confidence:Medium", defaults.Confidence.Medium),
            Low = section.GetValue("Confidence:Low", defaults.Confidence.Low),
            HighThreshold = section.GetValue("Confidence:HighThreshold", defaults.Confidence.HighThreshold),
            EscalationThreshold = section.GetValue("Confidence:EscalationThreshold", defaults.Confidence.EscalationThreshold)
        };

        var timing = new TimingDefaults
        {
            TimeoutMs = section.GetValue("Timing:TimeoutMs", defaults.Timing.TimeoutMs),
            CacheTtlSec = section.GetValue("Timing:CacheTtlSec", defaults.Timing.CacheTtlSec)
        };

        var features = new FeatureDefaults
        {
            DetailedLogging = section.GetValue("Features:DetailedLogging", defaults.Features.DetailedLogging),
            EnableCache = section.GetValue("Features:EnableCache", defaults.Features.EnableCache),
            CanEarlyExit = section.GetValue("Features:CanEarlyExit", defaults.Features.CanEarlyExit),
            CanEscalate = section.GetValue("Features:CanEscalate", defaults.Features.CanEscalate)
        };

        // Merge parameters
        var parameters = new Dictionary<string, object>(defaults.Parameters);
        var paramSection = section.GetSection("Parameters");
        if (paramSection.Exists())
        {
            foreach (var child in paramSection.GetChildren())
            {
                parameters[child.Key] = child.Value ?? "";
            }
        }

        return new WaveDefaults
        {
            Weights = weights,
            Confidence = confidence,
            Timing = timing,
            Features = features,
            Parameters = parameters
        };
    }
}
