using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using LucidRAG.Lenses;

namespace LucidRAG.Services.Lenses;

/// <summary>
/// Manages the collection of loaded lens packages.
/// Singleton service initialized on application startup.
/// </summary>
public class LensRegistry : ILensRegistry
{
    private readonly ILensLoader _loader;
    private readonly IConfiguration _config;
    private readonly ILogger<LensRegistry> _logger;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private volatile List<LensPackage> _lenses = new();

    public IReadOnlyList<LensPackage> AvailableLenses => _lenses;

    public LensRegistry(ILensLoader loader, IConfiguration config, ILogger<LensRegistry> logger)
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
            var lensDirectory = _config["Lenses:Directory"] ?? "./lenses";

            // Make path absolute if relative
            if (!Path.IsPathRooted(lensDirectory))
            {
                lensDirectory = Path.GetFullPath(lensDirectory);
            }

            _logger.LogInformation("Loading lenses from directory: {Directory}", lensDirectory);

            var packages = await _loader.LoadFromDirectoryAsync(lensDirectory, ct);
            _lenses = packages.ToList();

            if (_lenses.Count == 0)
            {
                _logger.LogWarning("No lens packages found in {Directory}. Creating default lens.", lensDirectory);
                _lenses.Add(CreateDefaultLens());
            }

            _logger.LogInformation("Lens registry initialized with {Count} lens(es)", _lenses.Count);

            foreach (var lens in _lenses)
            {
                _logger.LogDebug("  - {LensId} ({LensName}) v{Version} [priority: {Priority}]",
                    lens.Manifest.Id, lens.Manifest.Name, lens.Manifest.Version, lens.Manifest.Priority);
            }
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    public LensPackage? GetLens(string lensId)
    {
        if (string.IsNullOrWhiteSpace(lensId))
            return null;

        return _lenses.FirstOrDefault(l =>
            l.Manifest.Id.Equals(lensId, StringComparison.OrdinalIgnoreCase));
    }

    public LensPackage GetDefaultLens()
    {
        // Try configured default
        var defaultId = _config["Lenses:DefaultLens"];
        if (!string.IsNullOrEmpty(defaultId))
        {
            var configuredDefault = GetLens(defaultId);
            if (configuredDefault != null)
                return configuredDefault;

            _logger.LogWarning("Configured default lens '{LensId}' not found, using first available", defaultId);
        }

        // Fallback to first available lens
        if (_lenses.Count > 0)
            return _lenses[0];

        throw new InvalidOperationException("No lenses available in registry. Ensure lens packages are properly configured.");
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Reloading lens registry...");
        await InitializeAsync(ct);
    }

    /// <summary>
    /// Creates a minimal default lens for fallback scenarios.
    /// </summary>
    private static LensPackage CreateDefaultLens()
    {
        return new LensPackage
        {
            Manifest = new LensManifest(
                Id: "default",
                Name: "Default",
                Description: "Standard response formatting",
                Version: "1.0.0",
                Author: "LucidRAG",
                Priority: 0,
                Scoring: new LensScoringConfig(
                    DenseWeight: 0.3,
                    Bm25Weight: 0.3,
                    SalienceWeight: 0.2,
                    FreshnessWeight: 0.2
                ),
                Templates: new LensTemplatesConfig(
                    SystemPrompt: "system-prompt",
                    Citation: "citation",
                    Response: null
                ),
                Styles: null,
                Settings: null
            ),
            BasePath = "",
            SystemPromptTemplate = "You are a helpful assistant. Answer the question using only the evidence provided.",
            CitationTemplate = "[{{source.sequence_number}}] {{source.title}}",
            ResponseTemplate = null,
            Styles = null
        };
    }
}
