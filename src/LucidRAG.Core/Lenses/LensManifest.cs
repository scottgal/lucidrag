namespace LucidRAG.Lenses;

/// <summary>
/// Manifest metadata for a lens package.
/// Defines how the lens customizes search results and synthesis.
/// </summary>
public record LensManifest(
    string Id,
    string Name,
    string Description,
    string Version,
    string? Author,
    int Priority,
    LensScoringConfig Scoring,
    LensTemplatesConfig Templates,
    string? Styles,
    Dictionary<string, object>? Settings
);

/// <summary>
/// Scoring weights for Reciprocal Rank Fusion (RRF).
/// Controls how different signals are weighted in result ranking.
/// </summary>
public record LensScoringConfig(
    double DenseWeight,
    double Bm25Weight,
    double SalienceWeight,
    double FreshnessWeight
);

/// <summary>
/// Template file paths within the lens package.
/// All paths are relative to the lens package directory.
/// </summary>
public record LensTemplatesConfig(
    string SystemPrompt,
    string Citation,
    string? Response
);
