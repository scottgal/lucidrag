using LucidRAG.Lenses;

namespace LucidRAG.Services.Lenses;

/// <summary>
/// Service for rendering Liquid templates with lens-specific data.
/// </summary>
public interface ILensRenderService
{
    /// <summary>
    /// Renders the system prompt template for LLM synthesis.
    /// </summary>
    /// <param name="lens">The lens package containing the template</param>
    /// <param name="context">Context data (tenant_name, collection_description, source_count, etc.)</param>
    string RenderSystemPrompt(LensPackage lens, object context);

    /// <summary>
    /// Renders a single source citation template.
    /// </summary>
    /// <param name="lens">The lens package containing the template</param>
    /// <param name="source">The source citation to format</param>
    string RenderCitation(LensPackage lens, SourceCitation source);

    /// <summary>
    /// Renders the full response template (if defined).
    /// Returns null if the lens doesn't define a response template.
    /// </summary>
    /// <param name="lens">The lens package containing the template</param>
    /// <param name="answer">The LLM-generated answer</param>
    /// <param name="sources">All source citations</param>
    string? RenderResponse(LensPackage lens, string answer, List<SourceCitation> sources);
}
