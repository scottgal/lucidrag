using LucidRAG.Lenses;

namespace LucidRAG.Services.Lenses;

/// <summary>
/// Registry for managing available lens packages.
/// Provides access to lenses by ID and manages defaults.
/// </summary>
public interface ILensRegistry
{
    /// <summary>
    /// All available lenses, sorted by priority (descending).
    /// </summary>
    IReadOnlyList<LensPackage> AvailableLenses { get; }

    /// <summary>
    /// Gets a specific lens by its ID.
    /// Returns null if no lens with the given ID is found.
    /// </summary>
    LensPackage? GetLens(string lensId);

    /// <summary>
    /// Gets the default lens (from configuration or first available).
    /// Throws if no lenses are available.
    /// </summary>
    LensPackage GetDefaultLens();

    /// <summary>
    /// Initializes the registry by loading lenses from configured directory.
    /// Called automatically on startup.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Reloads all lenses from filesystem.
    /// Useful for hot-reload scenarios (future enhancement).
    /// </summary>
    Task ReloadAsync(CancellationToken ct = default);
}
