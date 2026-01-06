using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Ocr;

// Use explicit namespace to avoid confusion
using OcrTextRegion = Mostlylucid.DocSummarizer.Images.Services.Ocr.OcrTextRegion;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Structure Wave - Detects document structure (headings, paragraphs, lists) from OCR output.
/// Priority: 52 (runs after OCR waves, before Vision LLM)
///
/// Emits granular signals for each detected element:
/// - structure.title.*
/// - structure.heading.*
/// - structure.paragraph.*
/// - structure.list_item.*
/// - structure.caption.*
///
/// Also emits summary signals:
/// - structure.detected (bool)
/// - structure.heading_count (int)
/// - structure.markdown (string)
/// </summary>
public class StructureWave : IAnalysisWave
{
    private readonly DocumentStructureAnalyzer _structureAnalyzer;
    private readonly IOcrEngine? _ocrEngine;
    private readonly ILogger<StructureWave>? _logger;

    public string Name => "StructureWave";
    public int Priority => 52; // After OCR (50), before Florence2 (55)
    public IReadOnlyList<string> Tags => new[] { "structure", "markdown", "headings" };

    public StructureWave(
        IOcrEngine? ocrEngine = null,
        ILogger<StructureWave>? logger = null)
    {
        _ocrEngine = ocrEngine;
        _structureAnalyzer = new DocumentStructureAnalyzer(logger as ILogger<DocumentStructureAnalyzer>);
        _logger = logger;
    }

    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        // Only run if OCR found text
        var ocrText = context.GetValue<string>("ocr.text");
        var mlText = context.GetValue<string>("ocr.ml.text");

        // Need OCR regions with bounding boxes for structure analysis
        var hasOcrRegions = context.GetCached<List<OcrTextRegion>>("ocr.regions") != null;

        // Skip for animated GIFs (structure detection is for documents)
        var isAnimated = context.GetValue<bool>("identity.is_animated");
        if (isAnimated) return false;

        return !string.IsNullOrWhiteSpace(ocrText) || !string.IsNullOrWhiteSpace(mlText) || hasOcrRegions;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        try
        {
            // Get image dimensions
            var width = context.GetValue<int>("identity.width");
            var height = context.GetValue<int>("identity.height");

            if (width == 0 || height == 0)
            {
                // Try to get from image directly
                using var img = await SixLabors.ImageSharp.Image.LoadAsync(imagePath, ct);
                width = img.Width;
                height = img.Height;
            }

            // Get OCR regions (prefer cached, otherwise run OCR)
            var regions = context.GetCached<List<Mostlylucid.DocSummarizer.Images.Services.Ocr.OcrTextRegion>>("ocr.regions");

            if (regions == null && _ocrEngine != null)
            {
                regions = _ocrEngine.ExtractTextWithCoordinates(imagePath);
                context.SetCached("ocr.regions", regions);
            }

            if (regions == null || regions.Count == 0)
            {
                signals.Add(new Signal
                {
                    Key = "structure.detected",
                    Value = false,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "structure" },
                    Metadata = new Dictionary<string, object> { ["reason"] = "no_ocr_regions" }
                });
                return signals;
            }

            // Analyze structure
            var structure = _structureAnalyzer.Analyze(regions, width, height);

            // Emit summary signals
            signals.Add(new Signal
            {
                Key = "structure.detected",
                Value = structure.Elements.Count > 0,
                Confidence = 0.85,
                Source = Name,
                Tags = new List<string> { "structure" }
            });

            signals.Add(new Signal
            {
                Key = "structure.heading_count",
                Value = structure.DetectedHeadingCount,
                Confidence = 0.85,
                Source = Name,
                Tags = new List<string> { "structure", "summary" }
            });

            signals.Add(new Signal
            {
                Key = "structure.paragraph_count",
                Value = structure.DetectedParagraphCount,
                Confidence = 0.85,
                Source = Name,
                Tags = new List<string> { "structure", "summary" }
            });

            signals.Add(new Signal
            {
                Key = "structure.list_item_count",
                Value = structure.DetectedListItemCount,
                Confidence = 0.85,
                Source = Name,
                Tags = new List<string> { "structure", "summary" }
            });

            // Emit font statistics
            signals.Add(new Signal
            {
                Key = "structure.font.body_height",
                Value = structure.FontStatistics.EstimatedBodyTextHeight,
                Confidence = 0.9,
                Source = Name,
                Tags = new List<string> { "structure", "font" }
            });

            signals.Add(new Signal
            {
                Key = "structure.font.heading_threshold",
                Value = structure.FontStatistics.HeadingThreshold,
                Confidence = 0.9,
                Source = Name,
                Tags = new List<string> { "structure", "font" }
            });

            // Emit per-element signals
            var elementIndex = 0;
            var headingIndex = 0;
            var paragraphIndex = 0;
            var listItemIndex = 0;

            foreach (var element in structure.Elements)
            {
                var prefix = element.Type switch
                {
                    StructureType.Title => "structure.title",
                    StructureType.Heading => $"structure.heading.{headingIndex++}",
                    StructureType.Paragraph => $"structure.paragraph.{paragraphIndex++}",
                    StructureType.ListItem => $"structure.list_item.{listItemIndex++}",
                    StructureType.Caption => $"structure.caption.{elementIndex}",
                    StructureType.PageHeader => "structure.page_header",
                    StructureType.PageFooter => "structure.page_footer",
                    _ => $"structure.element.{elementIndex}"
                };

                // Text content signal
                signals.Add(new Signal
                {
                    Key = $"{prefix}.text",
                    Value = element.Text,
                    Confidence = element.Confidence,
                    Source = Name,
                    Tags = new List<string> { "structure", element.Type.ToString().ToLowerInvariant(), "content" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["type"] = element.Type.ToString(),
                        ["line_index"] = element.LineIndex,
                        ["section_id"] = element.SectionId,
                        ["font_height"] = element.FontHeight,
                        ["bounding_box"] = new Dictionary<string, int>
                        {
                            ["x"] = element.BoundingBox.X1,
                            ["y"] = element.BoundingBox.Y1,
                            ["width"] = element.BoundingBox.Width,
                            ["height"] = element.BoundingBox.Height
                        }
                    }
                });

                // For headings, emit level signal
                if (element.Type is StructureType.Title or StructureType.Heading && element.HeadingLevel.HasValue)
                {
                    signals.Add(new Signal
                    {
                        Key = $"{prefix}.level",
                        Value = element.HeadingLevel.Value,
                        Confidence = element.Confidence,
                        Source = Name,
                        Tags = new List<string> { "structure", "heading", "level" }
                    });
                }

                // Position signal
                signals.Add(new Signal
                {
                    Key = $"{prefix}.position",
                    Value = new Dictionary<string, object>
                    {
                        ["x"] = element.BoundingBox.X1,
                        ["y"] = element.BoundingBox.Y1,
                        ["width"] = element.BoundingBox.Width,
                        ["height"] = element.BoundingBox.Height,
                        ["relative_y"] = (double)element.BoundingBox.Y1 / height
                    },
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "structure", "position" }
                });

                elementIndex++;
            }

            // Generate and emit markdown
            var markdown = _structureAnalyzer.ToMarkdown(structure);
            signals.Add(new Signal
            {
                Key = "structure.markdown",
                Value = markdown,
                Confidence = 0.8,
                Source = Name,
                Tags = new List<string> { "structure", "markdown", "output" },
                Metadata = new Dictionary<string, object>
                {
                    ["element_count"] = structure.Elements.Count,
                    ["has_title"] = structure.Elements.Any(e => e.Type == StructureType.Title),
                    ["heading_levels_used"] = structure.Elements
                        .Where(e => e.HeadingLevel.HasValue)
                        .Select(e => e.HeadingLevel!.Value)
                        .Distinct()
                        .OrderBy(l => l)
                        .ToList()
                }
            });

            // Cache structure for downstream use
            context.SetCached("structure.analysis", structure);

            // Emit document outline (headings only)
            var outline = structure.Elements
                .Where(e => e.Type is StructureType.Title or StructureType.Heading)
                .Select(e => new Dictionary<string, object>
                {
                    ["text"] = e.Text,
                    ["level"] = e.HeadingLevel ?? 1,
                    ["y_position"] = e.BoundingBox.Y1
                })
                .ToList();

            if (outline.Count > 0)
            {
                signals.Add(new Signal
                {
                    Key = "structure.outline",
                    Value = outline,
                    Confidence = 0.85,
                    Source = Name,
                    Tags = new List<string> { "structure", "outline", "toc" }
                });
            }

            _logger?.LogInformation(
                "Structure detected: {Headings} headings, {Paragraphs} paragraphs, {ListItems} list items",
                structure.DetectedHeadingCount,
                structure.DetectedParagraphCount,
                structure.DetectedListItemCount);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Structure analysis failed");

            signals.Add(new Signal
            {
                Key = "structure.error",
                Value = ex.Message,
                Confidence = 0,
                Source = Name,
                Tags = new List<string> { "structure", "error" }
            });
        }

        return signals;
    }
}
