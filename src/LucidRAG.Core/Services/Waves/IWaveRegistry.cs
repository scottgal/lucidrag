using LucidRAG.Manifests;

namespace LucidRAG.Services.Waves;

/// <summary>
/// Registry for managing available wave manifests.
/// Provides access to waves by ID and manages wave lifecycle.
/// </summary>
public interface IWaveRegistry
{
    /// <summary>
    /// All available waves, sorted by priority (descending).
    /// </summary>
    IReadOnlyList<WaveManifest> AvailableWaves { get; }

    /// <summary>
    /// Gets a specific wave by its ID.
    /// Returns null if no wave with the given ID is found.
    /// </summary>
    WaveManifest? GetWave(string waveId);

    /// <summary>
    /// Gets waves by kind (e.g., all retrievers, all rankers).
    /// </summary>
    IReadOnlyList<WaveManifest> GetWavesByKind(WaveKind kind);

    /// <summary>
    /// Gets waves by tag.
    /// </summary>
    IReadOnlyList<WaveManifest> GetWavesByTag(string tag);

    /// <summary>
    /// Initializes the registry by loading waves from configured directory.
    /// Called automatically on startup.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Reloads all waves from filesystem.
    /// Useful for hot-reload scenarios.
    /// </summary>
    Task ReloadAsync(CancellationToken ct = default);
}
