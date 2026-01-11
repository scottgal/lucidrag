using DotLiquid;
using Microsoft.Extensions.Logging;
using LucidRAG.Lenses;

namespace LucidRAG.Services.Lenses;

/// <summary>
/// Renders Liquid templates for lens-specific formatting.
/// Uses DotLiquid library for template processing.
/// </summary>
public class LensRenderService : ILensRenderService
{
    private readonly ILogger<LensRenderService> _logger;

    static LensRenderService()
    {
        // Register custom filters
        Template.RegisterFilter(typeof(LensLiquidFilters));

        // Register safe types for Liquid templates
        Template.RegisterSafeType(typeof(SourceCitation), member => true);
        Template.RegisterSafeType(typeof(SourceCitationDrop), new[] {
            "number", "document_id", "document_name", "segment_id",
            "text", "page_or_section", "url", "title", "author",
            "publish_date", "excerpt", "sequence_number"
        });
    }

    public LensRenderService(ILogger<LensRenderService> logger)
    {
        _logger = logger;
    }

    public string RenderSystemPrompt(LensPackage lens, object context)
    {
        try
        {
            var template = Template.Parse(lens.SystemPromptTemplate);
            return template.Render(Hash.FromAnonymousObject(context));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rendering system prompt for lens {LensId}", lens.Manifest.Id);
            throw new InvalidOperationException($"Failed to render system prompt for lens '{lens.Manifest.Id}': {ex.Message}", ex);
        }
    }

    public string RenderCitation(LensPackage lens, SourceCitation source)
    {
        try
        {
            var template = Template.Parse(lens.CitationTemplate);
            var drop = new SourceCitationDrop(source);
            return template.Render(Hash.FromAnonymousObject(new { source = drop }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rendering citation for lens {LensId}", lens.Manifest.Id);

            // Fallback to simple citation format
            return $"[{source.Number}] {source.DocumentName}";
        }
    }

    public string? RenderResponse(LensPackage lens, string answer, List<SourceCitation> sources)
    {
        if (string.IsNullOrEmpty(lens.ResponseTemplate))
            return null;

        try
        {
            var template = Template.Parse(lens.ResponseTemplate);
            var sourcesDrops = sources.Select(s => new SourceCitationDrop(s)).ToList();

            var context = Hash.FromAnonymousObject(new
            {
                answer,
                sources = sourcesDrops
            });

            return template.Render(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rendering response for lens {LensId}", lens.Manifest.Id);

            // Return answer as-is if template fails
            return answer;
        }
    }
}

/// <summary>
/// Liquid drop for SourceCitation - makes citation properties accessible in templates.
/// Maps C# naming (PascalCase) to liquid naming (snake_case).
/// </summary>
public class SourceCitationDrop : Drop
{
    private readonly SourceCitation _source;

    public SourceCitationDrop(SourceCitation source)
    {
        _source = source;
    }

    // Standard Liquid properties (snake_case)
    public int number => _source.Number;
    public string document_id => _source.DocumentId.ToString();
    public string document_name => _source.DocumentName;
    public string segment_id => _source.SegmentId;
    public string text => _source.Text;
    public string? page_or_section => _source.PageOrSection;

    // Convenient aliases for blog lens templates
    public string url => $"/documents/{_source.DocumentId}";
    public string title => _source.DocumentName;
    public string excerpt => TruncateText(_source.Text, 200);
    public int sequence_number => _source.Number;

    // Metadata (future enhancement - could extract from document metadata)
    public string? author => null;
    public string? publish_date => null;

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;

        var truncated = text.Substring(0, maxLength);
        var lastSpace = truncated.LastIndexOf(' ');

        if (lastSpace > maxLength / 2)
            truncated = truncated.Substring(0, lastSpace);

        return truncated + "...";
    }
}

/// <summary>
/// Custom Liquid filters for lens templates.
/// </summary>
public static class LensLiquidFilters
{
    /// <summary>
    /// Renders a citation using the current lens (placeholder for future enhancement).
    /// </summary>
    public static string render_citation(object input)
    {
        if (input is SourceCitationDrop drop)
            return $"[{drop.number}] {drop.title}";

        return input?.ToString() ?? "";
    }
}
