using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mostlylucid.DocSummarizer.Images.Orchestration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mostlylucid.DocSummarizer.Images.Coordination;

/// <summary>
/// Loads wave manifests from YAML files for dynamic composition.
/// Uses Orchestration.WaveManifest as the canonical manifest type.
/// </summary>
public sealed class WaveManifestLoader
{
    private readonly IDeserializer _deserializer;
    private readonly Dictionary<string, WaveManifest> _manifests = new();

    public WaveManifestLoader()
    {
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    /// <summary>
    /// Load all wave manifests from embedded resources.
    /// </summary>
    public IReadOnlyDictionary<string, WaveManifest> LoadEmbeddedManifests()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".wave.yaml", StringComparison.OrdinalIgnoreCase));

        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) continue;

            using var reader = new StreamReader(stream);
            var yaml = reader.ReadToEnd();
            var manifest = _deserializer.Deserialize<WaveManifest>(yaml);

            if (manifest != null)
            {
                _manifests[manifest.Name] = manifest;
            }
        }

        return _manifests;
    }

    /// <summary>
    /// Load wave manifests from a directory.
    /// </summary>
    public IReadOnlyDictionary<string, WaveManifest> LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return _manifests;

        var files = Directory.GetFiles(directory, "*.wave.yaml", SearchOption.TopDirectoryOnly);

        foreach (var file in files)
        {
            var yaml = File.ReadAllText(file);
            var manifest = _deserializer.Deserialize<WaveManifest>(yaml);

            if (manifest != null)
            {
                _manifests[manifest.Name] = manifest;
            }
        }

        return _manifests;
    }

    /// <summary>
    /// Get a specific manifest by wave name.
    /// </summary>
    public WaveManifest? GetManifest(string waveName)
    {
        return _manifests.TryGetValue(waveName, out var manifest) ? manifest : null;
    }

    /// <summary>
    /// Get all loaded manifests.
    /// </summary>
    public IReadOnlyDictionary<string, WaveManifest> GetAllManifests() => _manifests;

    /// <summary>
    /// Get all manifests sorted by priority (highest first).
    /// </summary>
    public IReadOnlyList<WaveManifest> GetOrderedManifests()
    {
        return _manifests.Values
            .Where(m => m.Enabled)
            .OrderByDescending(m => m.Priority)
            .ToList();
    }

    /// <summary>
    /// Get all signal contracts for LLM consumption.
    /// Returns a summary of what each wave emits and consumes.
    /// </summary>
    public string GetSignalContractsSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Wave Signal Contracts");
        sb.AppendLine();

        foreach (var manifest in GetOrderedManifests())
        {
            var contract = ManifestSignalContract.FromManifest(manifest);
            sb.AppendLine(contract.ToDescription());
        }

        return sb.ToString();
    }

    /// <summary>
    /// Get all signal contracts as structured data.
    /// </summary>
    public IReadOnlyList<SignalContract> GetAllContracts()
    {
        return GetOrderedManifests()
            .Select(ManifestSignalContract.FromManifest)
            .ToList();
    }

    /// <summary>
    /// Get manifests that can run given available signals.
    /// </summary>
    public IReadOnlyList<WaveManifest> GetRunnableManifests(IReadOnlySet<string> availableSignals)
    {
        return GetOrderedManifests()
            .Where(m => CanRun(m, availableSignals))
            .ToList();
    }

    /// <summary>
    /// Check if a wave can run given available signals.
    /// </summary>
    public bool CanRun(WaveManifest manifest, IReadOnlySet<string> availableSignals)
    {
        // Check skip conditions first
        foreach (var skipSignal in manifest.Triggers.SkipWhen)
        {
            var resolved = ResolveSignalPattern(skipSignal, manifest.Name);
            if (availableSignals.Contains(resolved))
                return false;
        }

        // Check required signals
        foreach (var requirement in manifest.Triggers.Requires)
        {
            if (!availableSignals.Contains(requirement.Signal))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Get all signals that a wave emits.
    /// </summary>
    public IReadOnlySet<string> GetEmittedSignals(WaveManifest manifest)
    {
        var signals = new HashSet<string>();

        signals.UnionWith(manifest.Emits.OnStart);
        signals.UnionWith(manifest.Emits.OnComplete.Select(s => s.Key));
        signals.UnionWith(manifest.Emits.OnFailure);
        signals.UnionWith(manifest.Emits.Conditional.Select(s => s.Key));

        return signals;
    }

    /// <summary>
    /// Get all signals that a wave listens to.
    /// </summary>
    public IReadOnlySet<string> GetListenedSignals(WaveManifest manifest)
    {
        var signals = new HashSet<string>();

        signals.UnionWith(manifest.Listens.Required);
        signals.UnionWith(manifest.Listens.Optional);
        signals.UnionWith(manifest.Triggers.Requires.Select(r => r.Signal));
        signals.UnionWith(manifest.Cache.Uses);

        return signals;
    }

    /// <summary>
    /// Build a dependency graph of waves.
    /// </summary>
    public Dictionary<string, HashSet<string>> BuildDependencyGraph()
    {
        var graph = new Dictionary<string, HashSet<string>>();
        var signalToWave = new Dictionary<string, string>();

        // First pass: map signals to producing waves
        foreach (var manifest in _manifests.Values)
        {
            foreach (var signal in GetEmittedSignals(manifest))
            {
                signalToWave[signal] = manifest.Name;
            }
        }

        // Second pass: build dependencies
        foreach (var manifest in _manifests.Values)
        {
            graph[manifest.Name] = new HashSet<string>();

            foreach (var signal in GetListenedSignals(manifest))
            {
                if (signalToWave.TryGetValue(signal, out var producer) && producer != manifest.Name)
                {
                    graph[manifest.Name].Add(producer);
                }
            }
        }

        return graph;
    }

    /// <summary>
    /// Get topologically sorted wave execution order.
    /// </summary>
    public IReadOnlyList<string> GetExecutionOrder()
    {
        var graph = BuildDependencyGraph();
        var sorted = new List<string>();
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();

        void Visit(string wave)
        {
            if (visited.Contains(wave)) return;
            if (visiting.Contains(wave))
                throw new InvalidOperationException($"Circular dependency detected involving {wave}");

            visiting.Add(wave);

            if (graph.TryGetValue(wave, out var deps))
            {
                foreach (var dep in deps)
                {
                    Visit(dep);
                }
            }

            visiting.Remove(wave);
            visited.Add(wave);
            sorted.Add(wave);
        }

        foreach (var wave in graph.Keys.OrderByDescending(w => _manifests[w].Priority))
        {
            Visit(wave);
        }

        return sorted;
    }

    private static string ResolveSignalPattern(string pattern, string waveName)
    {
        return pattern.Replace("{name}", waveName);
    }
}
