using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LucidRAG.Manifests;

/// <summary>
/// Loads manifests from embedded assembly resources.
/// Follows StyloFlow pattern for embedded resource loading.
/// </summary>
public sealed class EmbeddedManifestLoader<TManifest> : IManifestLoader<TManifest>
    where TManifest : class
{
    private readonly ILogger _logger;
    private readonly Assembly[] _sourceAssemblies;
    private readonly string _manifestPattern;
    private readonly IDeserializer _deserializer;
    private readonly ConcurrentDictionary<string, TManifest> _cache = new();
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    public EmbeddedManifestLoader(
        ILogger logger,
        Assembly[] sourceAssemblies,
        string manifestPattern = ".yaml")
    {
        _logger = logger;
        _sourceAssemblies = sourceAssemblies;
        _manifestPattern = manifestPattern;

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public async Task<IReadOnlyList<TManifest>> LoadAllAsync(CancellationToken ct = default)
    {
        await _reloadLock.WaitAsync(ct);
        try
        {
            _cache.Clear();

            foreach (var assembly in _sourceAssemblies)
            {
                await LoadFromAssemblyAsync(assembly, ct);
            }

            _logger.LogInformation(
                "Loaded {Count} {Type} manifest(s) from {Assemblies} assembl(ies)",
                _cache.Count,
                typeof(TManifest).Name,
                _sourceAssemblies.Length);

            return _cache.Values.ToList();
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    public async Task<TManifest?> LoadByNameAsync(string name, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(name, out var cached))
            return cached;

        foreach (var assembly in _sourceAssemblies)
        {
            var manifest = await TryLoadManifestFromAssemblyAsync(assembly, name, ct);
            if (manifest != null)
            {
                _cache[name] = manifest;
                return manifest;
            }
        }

        return null;
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Reloading {Type} manifests from embedded resources", typeof(TManifest).Name);
        await LoadAllAsync(ct);
    }

    public IReadOnlyList<TManifest> GetAll()
    {
        return _cache.Values.ToList();
    }

    public TManifest? GetByName(string name)
    {
        return _cache.TryGetValue(name, out var manifest) ? manifest : null;
    }

    private async Task LoadFromAssemblyAsync(Assembly assembly, CancellationToken ct)
    {
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(r => r.EndsWith(_manifestPattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var resourceName in resourceNames)
        {
            try
            {
                await using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    _logger.LogWarning("Could not load embedded resource: {Resource}", resourceName);
                    continue;
                }

                using var reader = new StreamReader(stream);
                var yaml = await reader.ReadToEndAsync(ct);
                var manifest = _deserializer.Deserialize<TManifest>(yaml);

                if (manifest == null)
                {
                    _logger.LogWarning("Failed to deserialize manifest from {Resource}", resourceName);
                    continue;
                }

                // Extract name from manifest (use reflection to get "Name" property)
                var nameProperty = typeof(TManifest).GetProperty("Name");
                if (nameProperty == null)
                {
                    _logger.LogWarning("Manifest type {Type} does not have a 'Name' property", typeof(TManifest).Name);
                    continue;
                }

                var name = nameProperty.GetValue(manifest)?.ToString();
                if (string.IsNullOrEmpty(name))
                {
                    _logger.LogWarning("Manifest in {Resource} has empty name", resourceName);
                    continue;
                }

                _cache[name] = manifest;

                _logger.LogDebug(
                    "Loaded {Type} manifest '{Name}' from embedded resource {Resource}",
                    typeof(TManifest).Name,
                    name,
                    resourceName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading manifest from embedded resource {Resource}", resourceName);
            }
        }
    }

    private async Task<TManifest?> TryLoadManifestFromAssemblyAsync(Assembly assembly, string name, CancellationToken ct)
    {
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(r => r.Contains(name, StringComparison.OrdinalIgnoreCase) &&
                       r.EndsWith(_manifestPattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (resourceNames.Count == 0)
            return null;

        try
        {
            await using var stream = assembly.GetManifestResourceStream(resourceNames[0]);
            if (stream == null)
                return null;

            using var reader = new StreamReader(stream);
            var yaml = await reader.ReadToEndAsync(ct);
            return _deserializer.Deserialize<TManifest>(yaml);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading manifest from embedded resource {Resource}", resourceNames[0]);
            return null;
        }
    }
}
