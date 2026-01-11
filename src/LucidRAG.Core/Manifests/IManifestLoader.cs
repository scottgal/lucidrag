namespace LucidRAG.Manifests;

/// <summary>
/// Interface for loading manifests from various sources.
/// Supports lenses, waves, and processors.
/// </summary>
public interface IManifestLoader<TManifest> where TManifest : class
{
    /// <summary>
    /// Loads all manifests from the configured source.
    /// </summary>
    Task<IReadOnlyList<TManifest>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Loads a specific manifest by name.
    /// Returns null if not found.
    /// </summary>
    Task<TManifest?> LoadByNameAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Reloads all manifests from source (for hot reload scenarios).
    /// </summary>
    Task ReloadAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all currently loaded manifests (cached).
    /// </summary>
    IReadOnlyList<TManifest> GetAll();

    /// <summary>
    /// Gets a specific manifest by name from cache.
    /// Returns null if not found.
    /// </summary>
    TManifest? GetByName(string name);
}
