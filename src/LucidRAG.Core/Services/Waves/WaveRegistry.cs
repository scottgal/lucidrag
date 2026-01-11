using LucidRAG.Manifests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LucidRAG.Services.Waves;

/// <summary>
/// Manages the collection of loaded wave manifests.
/// Singleton service initialized on application startup.
/// </summary>
public sealed class WaveRegistry : IWaveRegistry
{
    private readonly IManifestLoader<WaveManifest> _loader;
    private readonly IConfiguration _config;
    private readonly ILogger<WaveRegistry> _logger;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private volatile List<WaveManifest> _waves = new();

    public IReadOnlyList<WaveManifest> AvailableWaves => _waves;

    public WaveRegistry(
        IManifestLoader<WaveManifest> loader,
        IConfiguration config,
        ILogger<WaveRegistry> logger)
    {
        _loader = loader;
        _config = config;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _reloadLock.WaitAsync(ct);
        try
        {
            var waves = await _loader.LoadAllAsync(ct);
            _waves = waves
                .Where(w => w.Enabled)
                .OrderByDescending(w => w.Priority)
                .ToList();

            _logger.LogInformation(
                "Wave registry initialized with {Count} wave(s)",
                _waves.Count);

            foreach (var wave in _waves)
            {
                _logger.LogDebug(
                    "  - {WaveId} ({WaveName}) v{Version} [kind: {Kind}, priority: {Priority}]",
                    wave.Name,
                    wave.DisplayName,
                    wave.Version,
                    wave.Taxonomy.Kind,
                    wave.Priority);
            }
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    public WaveManifest? GetWave(string waveId)
    {
        if (string.IsNullOrWhiteSpace(waveId))
            return null;

        return _waves.FirstOrDefault(w =>
            w.Name.Equals(waveId, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<WaveManifest> GetWavesByKind(WaveKind kind)
    {
        return _waves
            .Where(w => w.Taxonomy.Kind == kind)
            .ToList();
    }

    public IReadOnlyList<WaveManifest> GetWavesByTag(string tag)
    {
        return _waves
            .Where(w => w.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Reloading wave registry...");
        await InitializeAsync(ct);
    }
}
