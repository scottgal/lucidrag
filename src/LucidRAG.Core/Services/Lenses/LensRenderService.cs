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
/// EXPOSES ALL SIGNALS AND SCORES FOR TRANSPARENT RETRIEVAL.
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

    // Convenient aliases for templates
    public string url => $"/documents/{_source.DocumentId}";
    public string title => _source.DocumentName;
    public string excerpt => TruncateText(_source.Text, 200);
    public int sequence_number => _source.Number;

    // SIGNAL SCORES - All retrieval signals exposed!
    public double rrf_score => _source.RrfScore;
    public double dense_score => _source.DenseScore;
    public double bm25_score => _source.Bm25Score;
    public double salience_score => _source.SalienceScore;
    public double freshness_score => _source.FreshnessScore;

    // MATCHING INFORMATION - Why this source was selected
    public List<string> matched_salient_terms => _source.MatchedSalientTerms ?? new();
    public List<string> matched_entities => _source.MatchedEntities ?? new();
    public List<string> signal_explanations => _source.SignalExplanations ?? new();

    // METADATA - Document-level information
    public string? author => _source.Author;
    public string? publish_date => _source.PublishDate;
    public string? document_type => _source.DocumentType;
    public Dictionary<string, object>? metadata => _source.Metadata;

    // SUMMARY/SEGMENT HANDLING - Intelligent text selection
    public string? extractive_summary => _source.ExtractiveSummary;
    public string? llm_summary => _source.LlmSummary;
    public string text_type => _source.TextType;
    public int original_length => _source.OriginalLength;
    public bool is_ocr_source => _source.IsOcrSource;
    public int character_count => _source.CharacterCount;

    // Smart text selection - use summary if available and source is long
    public string smart_text => GetSmartText();

    private string GetSmartText()
    {
        // If we have an LLM summary and source is long, use it
        if (!string.IsNullOrEmpty(_source.LlmSummary) && _source.CharacterCount > 1000)
            return _source.LlmSummary;

        // If we have extractive summary and source is long, use it
        if (!string.IsNullOrEmpty(_source.ExtractiveSummary) && _source.CharacterCount > 1000)
            return _source.ExtractiveSummary;

        // Otherwise use the segment text as-is
        return _source.Text;
    }

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
