using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using LucidRAG.Services.Lenses;

namespace LucidRAG.Controllers.Api;

/// <summary>
/// API endpoints for listing and retrieving lens packages.
/// </summary>
[ApiController]
[Route("api/lenses")]
[AllowAnonymous]  // Lenses are public metadata
public class LensesController : ControllerBase
{
    private readonly ILensRegistry _lensRegistry;
    private readonly IConfiguration _config;
    private readonly ILogger<LensesController> _logger;

    public LensesController(
        ILensRegistry lensRegistry,
        IConfiguration config,
        ILogger<LensesController> logger)
    {
        _lensRegistry = lensRegistry;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Lists all available lens packages.
    /// Returns basic metadata for each lens including styles.
    /// </summary>
    [HttpGet]
    public Ok<List<LensListItem>> List()
    {
        var defaultLensId = GetDefaultLensId();

        var lenses = _lensRegistry.AvailableLenses
            .Select(l => new LensListItem(
                Id: l.Manifest.Id,
                Name: l.Manifest.Name,
                Description: l.Manifest.Description,
                Version: l.Manifest.Version,
                Styles: l.Styles,
                IsDefault: l.Manifest.Id == defaultLensId
            ))
            .ToList();

        _logger.LogDebug("Returning {Count} available lenses", lenses.Count);

        return TypedResults.Ok(lenses);
    }

    /// <summary>
    /// Gets detailed information about a specific lens.
    /// </summary>
    [HttpGet("{id}")]
    public Results<Ok<LensDetailItem>, NotFound> Get(string id)
    {
        var lens = _lensRegistry.GetLens(id);

        if (lens == null)
        {
            _logger.LogWarning("Lens not found: {LensId}", id);
            return TypedResults.NotFound();
        }

        var detail = new LensDetailItem(
            Id: lens.Manifest.Id,
            Name: lens.Manifest.Name,
            Description: lens.Manifest.Description,
            Version: lens.Manifest.Version,
            Author: lens.Manifest.Author,
            Priority: lens.Manifest.Priority,
            Scoring: lens.Manifest.Scoring,
            Styles: lens.Styles,
            Settings: lens.Manifest.Settings
        );

        return TypedResults.Ok(detail);
    }

    /// <summary>
    /// Gets the default lens ID from configuration.
    /// </summary>
    private string GetDefaultLensId()
    {
        var configuredDefault = _config["Lenses:DefaultLens"];
        if (!string.IsNullOrEmpty(configuredDefault))
            return configuredDefault;

        // Fallback to first available lens
        var firstLens = _lensRegistry.AvailableLenses.FirstOrDefault();
        return firstLens?.Manifest.Id ?? "default";
    }
}

/// <summary>
/// List item for available lenses.
/// Includes basic metadata and styles for frontend rendering.
/// </summary>
public record LensListItem(
    string Id,
    string Name,
    string Description,
    string Version,
    string? Styles,
    bool IsDefault
);

/// <summary>
/// Detailed information about a specific lens.
/// </summary>
public record LensDetailItem(
    string Id,
    string Name,
    string Description,
    string Version,
    string? Author,
    int Priority,
    LucidRAG.Lenses.LensScoringConfig Scoring,
    string? Styles,
    Dictionary<string, object>? Settings
);
