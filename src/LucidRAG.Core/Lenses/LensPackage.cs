namespace LucidRAG.Lenses;

/// <summary>
/// Represents a loaded lens package with all templates and assets.
/// </summary>
public class LensPackage
{
    public required LensManifest Manifest { get; init; }
    public required string BasePath { get; init; }

    /// <summary>
    /// Liquid template for system prompt used in LLM synthesis.
    /// </summary>
    public string SystemPromptTemplate { get; set; } = "";

    /// <summary>
    /// Liquid template for formatting individual source citations.
    /// </summary>
    public string CitationTemplate { get; set; } = "";

    /// <summary>
    /// Optional Liquid template for wrapping the entire response.
    /// If null, response is returned as-is.
    /// </summary>
    public string? ResponseTemplate { get; set; }

    /// <summary>
    /// Optional CSS styles to inject when this lens is active.
    /// </summary>
    public string? Styles { get; set; }

    /// <summary>
    /// Constructs the full path to a template file within the package.
    /// </summary>
    public string GetTemplatePath(string templateFile)
        => Path.Combine(BasePath, templateFile);
}
