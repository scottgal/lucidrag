using System.Text.Json;
using Microsoft.Extensions.Logging;
using LucidRAG.Lenses;

namespace LucidRAG.Services.Lenses;

/// <summary>
/// Loads lens packages from filesystem directories.
/// Validates manifests, loads templates, and handles errors gracefully.
/// </summary>
public class LensLoader : ILensLoader
{
    private readonly ILogger<LensLoader> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public LensLoader(ILogger<LensLoader> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<LensPackage>> LoadFromDirectoryAsync(string directory, CancellationToken ct = default)
    {
        if (!Directory.Exists(directory))
        {
            _logger.LogWarning("Lens directory does not exist: {Directory}", directory);
            return Array.Empty<LensPackage>();
        }

        var packages = new List<LensPackage>();

        foreach (var subdir in Directory.GetDirectories(directory))
        {
            var package = await LoadPackageAsync(subdir, ct);
            if (package != null)
                packages.Add(package);
        }

        // Sort by priority (descending)
        return packages.OrderByDescending(p => p.Manifest.Priority).ToList();
    }

    public async Task<LensPackage?> LoadPackageAsync(string packagePath, CancellationToken ct = default)
    {
        var manifestPath = Path.Combine(packagePath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            _logger.LogWarning("Manifest not found in {Path}", packagePath);
            return null;
        }

        try
        {
            // Load and parse manifest
            var manifestJson = await File.ReadAllTextAsync(manifestPath, ct);
            var manifest = JsonSerializer.Deserialize<LensManifest>(manifestJson, JsonOptions);

            if (manifest == null)
            {
                _logger.LogWarning("Failed to deserialize manifest in {Path}", packagePath);
                return null;
            }

            // Validate manifest
            if (!ValidateManifest(manifest))
            {
                _logger.LogWarning("Invalid manifest in {Path}", packagePath);
                return null;
            }

            var package = new LensPackage
            {
                Manifest = manifest,
                BasePath = packagePath
            };

            // Load templates
            package.SystemPromptTemplate = await LoadTemplateAsync(package, manifest.Templates.SystemPrompt, ct);
            package.CitationTemplate = await LoadTemplateAsync(package, manifest.Templates.Citation, ct);

            if (!string.IsNullOrEmpty(manifest.Templates.Response))
                package.ResponseTemplate = await LoadTemplateAsync(package, manifest.Templates.Response, ct);

            // Load styles (optional)
            if (!string.IsNullOrEmpty(manifest.Styles))
            {
                var stylesPath = Path.Combine(packagePath, manifest.Styles);
                if (File.Exists(stylesPath))
                    package.Styles = await File.ReadAllTextAsync(stylesPath, ct);
            }

            _logger.LogInformation("Loaded lens package: {LensId} v{Version} from {Path}",
                manifest.Id, manifest.Version, packagePath);

            return package;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parsing error loading lens package from {Path}", packagePath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading lens package from {Path}", packagePath);
            return null;
        }
    }

    private async Task<string> LoadTemplateAsync(LensPackage package, string templateFile, CancellationToken ct)
    {
        var path = package.GetTemplatePath(templateFile);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Template not found: {path}");

        return await File.ReadAllTextAsync(path, ct);
    }

    private bool ValidateManifest(LensManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            _logger.LogWarning("Lens manifest missing required field: Id");
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            _logger.LogWarning("Lens manifest missing required field: Name");
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.Templates.SystemPrompt) ||
            string.IsNullOrWhiteSpace(manifest.Templates.Citation))
        {
            _logger.LogWarning("Lens manifest missing required templates");
            return false;
        }

        // Validate scoring weights sum to 1.0 (with tolerance)
        var totalWeight = manifest.Scoring.DenseWeight + manifest.Scoring.Bm25Weight +
                         manifest.Scoring.SalienceWeight + manifest.Scoring.FreshnessWeight;

        if (Math.Abs(totalWeight - 1.0) > 0.01)
        {
            _logger.LogWarning("Lens scoring weights do not sum to 1.0 (sum: {Sum})", totalWeight);
            return false;
        }

        return true;
    }
}
