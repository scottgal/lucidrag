using LucidRAG.Lenses;
using LucidRAG.Manifests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LucidRAG.Services.Lenses;

/// <summary>
/// Loads lens packages from YAML manifests.
/// Replaces the old JSON-based lens loader.
/// </summary>
public sealed class YamlLensLoader : ILensLoader
{
    private readonly IManifestLoader<Manifests.LensManifest> _manifestLoader;
    private readonly ILogger<YamlLensLoader> _logger;

    public YamlLensLoader(
        IManifestLoader<Manifests.LensManifest> manifestLoader,
        ILogger<YamlLensLoader> logger)
    {
        _manifestLoader = manifestLoader;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LensPackage>> LoadFromDirectoryAsync(string directory, CancellationToken ct = default)
    {
        var manifests = await _manifestLoader.LoadAllAsync(ct);

        var packages = manifests
            .Where(m => m.Enabled)
            .Select(ConvertToLensPackage)
            .OrderByDescending(p => p.Manifest.Priority)
            .ToList();

        _logger.LogInformation("Loaded {Count} lens package(s) from YAML manifests", packages.Count);

        return packages;
    }

    public async Task<LensPackage?> LoadPackageAsync(string packagePath, CancellationToken ct = default)
    {
        // Extract lens name from path (e.g., "lenses/blog" -> "blog")
        var lensName = Path.GetFileName(packagePath);

        var manifest = await _manifestLoader.LoadByNameAsync(lensName, ct);
        if (manifest == null)
            return null;

        return ConvertToLensPackage(manifest);
    }

    private LensPackage ConvertToLensPackage(Manifests.LensManifest manifest)
    {
        return new LensPackage
        {
            Manifest = new global::LucidRAG.Lenses.LensManifest(
                Id: manifest.Name,
                Name: manifest.DisplayName,
                Description: manifest.Description,
                Version: manifest.Version,
                Author: manifest.Author,
                Priority: manifest.Priority,
                Scoring: new global::LucidRAG.Lenses.LensScoringConfig(
                    DenseWeight: manifest.Scoring.DenseWeight,
                    Bm25Weight: manifest.Scoring.Bm25Weight,
                    SalienceWeight: manifest.Scoring.SalienceWeight,
                    FreshnessWeight: manifest.Scoring.FreshnessWeight
                ),
                Templates: new global::LucidRAG.Lenses.LensTemplatesConfig(
                    SystemPrompt: "inline",  // Templates are inline in YAML
                    Citation: "inline",
                    Response: manifest.Templates.Response != null ? "inline" : null
                ),
                Styles: manifest.Styles?.InlineCss != null || manifest.Styles?.CssFile != null ? "inline" : null,
                Settings: manifest.Defaults
            ),
            BasePath = "",  // Not needed for YAML-based lenses
            SystemPromptTemplate = manifest.Templates.SystemPrompt ?? "",
            CitationTemplate = manifest.Templates.Citation ?? "",
            ResponseTemplate = manifest.Templates.Response,
            Styles = manifest.Styles?.InlineCss ?? (manifest.Styles?.CssFile != null
                ? LoadCssFile(manifest.Styles.CssFile)
                : null)
        };
    }

    private string? LoadCssFile(string cssFile)
    {
        try
        {
            if (File.Exists(cssFile))
                return File.ReadAllText(cssFile);

            _logger.LogWarning("CSS file not found: {File}", cssFile);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading CSS file: {File}", cssFile);
            return null;
        }
    }
}
