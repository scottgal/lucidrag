using LucidRAG.Lenses;

namespace LucidRAG.Services.Lenses;

/// <summary>
/// Service for loading lens packages from filesystem.
/// </summary>
public interface ILensLoader
{
    /// <summary>
    /// Loads all lens packages from a directory.
    /// Each subdirectory is expected to contain a lens package with manifest.json.
    /// </summary>
    Task<IReadOnlyList<LensPackage>> LoadFromDirectoryAsync(string directory, CancellationToken ct = default);

    /// <summary>
    /// Loads a single lens package from a directory.
    /// Returns null if the package is invalid or cannot be loaded.
    /// </summary>
    Task<LensPackage?> LoadPackageAsync(string packagePath, CancellationToken ct = default);
}
