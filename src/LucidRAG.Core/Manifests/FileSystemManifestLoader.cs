using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LucidRAG.Manifests;

/// <summary>
/// Loads manifests from filesystem directories.
/// Supports hot reload via file system watching.
/// Follows StyloFlow pattern.
/// </summary>
public sealed class FileSystemManifestLoader<TManifest> : IManifestLoader<TManifest>
    where TManifest : class
{
    private readonly ILogger _logger;
    private readonly string[] _directories;
    private readonly string _filePattern;
    private readonly IDeserializer _deserializer;
    private readonly ConcurrentDictionary<string, TManifest> _cache = new();
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    public FileSystemManifestLoader(
        ILogger logger,
        string[] directories,
        string filePattern = "*.yaml")
    {
        _logger = logger;
        _directories = directories;
        _filePattern = filePattern;

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

            foreach (var directory in _directories)
            {
                if (!Directory.Exists(directory))
                {
                    _logger.LogWarning("Manifest directory does not exist: {Directory}", directory);
                    continue;
                }

                await LoadFromDirectoryAsync(directory, ct);
            }

            _logger.LogInformation(
                "Loaded {Count} {Type} manifest(s) from {Directories} director(ies)",
                _cache.Count,
                typeof(TManifest).Name,
                _directories.Length);

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

        foreach (var directory in _directories)
        {
            if (!Directory.Exists(directory))
                continue;

            var manifest = await TryLoadManifestAsync(directory, name, ct);
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
        _logger.LogInformation("Reloading {Type} manifests from filesystem", typeof(TManifest).Name);
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

    private async Task LoadFromDirectoryAsync(string directory, CancellationToken ct)
    {
        var files = Directory.GetFiles(directory, _filePattern, SearchOption.AllDirectories);

        foreach (var file in files)
        {
            try
            {
                var yaml = await File.ReadAllTextAsync(file, ct);
                var manifest = _deserializer.Deserialize<TManifest>(yaml);

                if (manifest == null)
                {
                    _logger.LogWarning("Failed to deserialize manifest from {File}", file);
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
                    _logger.LogWarning("Manifest in {File} has empty name", file);
                    continue;
                }

                _cache[name] = manifest;

                _logger.LogDebug(
                    "Loaded {Type} manifest '{Name}' from {File}",
                    typeof(TManifest).Name,
                    name,
                    file);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading manifest from {File}", file);
            }
        }
    }

    private async Task<TManifest?> TryLoadManifestAsync(string directory, string name, CancellationToken ct)
    {
        // Try common naming patterns: name.yaml, name.lens.yaml, name.wave.yaml, name.processor.yaml
        var patterns = new[]
        {
            $"{name}.yaml",
            $"{name}.*.yaml"
        };

        foreach (var pattern in patterns)
        {
            var files = Directory.GetFiles(directory, pattern, SearchOption.AllDirectories);
            if (files.Length == 0)
                continue;

            try
            {
                var yaml = await File.ReadAllTextAsync(files[0], ct);
                return _deserializer.Deserialize<TManifest>(yaml);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading manifest from {File}", files[0]);
            }
        }

        return null;
    }
}
