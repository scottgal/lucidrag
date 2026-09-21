using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
///     Handler for PowerPoint presentations (.pptx).
///     Extracts slide content, speaker notes, and marks embedded media for downstream processing.
/// </summary>
public class PptxDocumentHandler : IDocumentHandler
{
    public IReadOnlyList<string> SupportedExtensions => [".pptx"];
    public int Priority => 0;
    public string HandlerName => "PowerPoint";

    public bool CanHandle(string filePath)
    {
        if (!File.Exists(filePath))
            return false;

        // Only handle .pptx (Open XML), not .ppt (binary)
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext == ".pptx";
    }

    public Task<DocumentContent> ProcessAsync(string filePath, DocumentHandlerOptions options)
    {
        var sb = new StringBuilder();
        string? documentTitle = null;
        var embeddedAssets = new List<ExtractedAsset>();
        var metadata = new Dictionary<string, object>();

        using (var presentation = PresentationDocument.Open(filePath, false))
        {
            var presentationPart = presentation.PresentationPart;
            if (presentationPart == null)
                return Task.FromResult(new DocumentContent
                {
                    Markdown = "",
                    Title = Path.GetFileNameWithoutExtension(filePath),
                    ContentType = "pptx"
                });

            // Try to get title from core properties
            if (presentation.PackageProperties?.Title != null)
                documentTitle = presentation.PackageProperties.Title;

            // Get slide IDs in order
            var slideIdList = presentationPart.Presentation?.SlideIdList;
            if (slideIdList == null)
                return Task.FromResult(new DocumentContent
                {
                    Markdown = "",
                    Title = documentTitle ?? Path.GetFileNameWithoutExtension(filePath),
                    ContentType = "pptx"
                });

            var slideNumber = 0;
            var totalSlides = slideIdList.Count();
            metadata["slideCount"] = totalSlides;

            foreach (var slideId in slideIdList.Elements<SlideId>())
            {
                slideNumber++;
                var relationshipId = slideId.RelationshipId;
                if (relationshipId == null) continue;

                var slidePart = (SlidePart)presentationPart.GetPartById(relationshipId!);

                // Add slide marker for chunking
                sb.AppendLine($"<!-- SLIDE:{slideNumber} -->");
                sb.AppendLine($"## Slide {slideNumber}");
                sb.AppendLine();

                // Extract slide content
                var slideContent = ExtractSlideContent(slidePart);
                if (!string.IsNullOrWhiteSpace(slideContent))
                {
                    sb.AppendLine(slideContent);
                    sb.AppendLine();
                }

                // Extract speaker notes
                var notes = ExtractSpeakerNotes(slidePart);
                if (!string.IsNullOrWhiteSpace(notes))
                {
                    sb.AppendLine("> **Speaker Notes:**");
                    foreach (var line in notes.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        sb.AppendLine($"> {line.Trim()}");
                    sb.AppendLine();
                }

                // Extract embedded charts
                var chartIndex = 0;
                foreach (var chartPart in slidePart.ChartParts ?? Enumerable.Empty<ChartPart>())
                {
                    chartIndex++;
                    var chartData = ExtractChartData(chartPart, slideNumber, chartIndex);
                    if (!string.IsNullOrWhiteSpace(chartData))
                    {
                        sb.AppendLine(chartData);
                        sb.AppendLine();
                    }
                    else
                    {
                        sb.AppendLine($"[CHART: Embedded chart {chartIndex} on slide {slideNumber}]");
                        sb.AppendLine();
                    }
                }

                // Extract embedded images
                var imageIndex = 0;
                foreach (var imagePart in slidePart.ImageParts)
                {
                    imageIndex++;
                    var extension = GetImageExtension(imagePart.ContentType);
                    var assetName = $"slide{slideNumber}_image{imageIndex}{extension}";

                    // Read image data
                    using var stream = imagePart.GetStream();
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    var imageData = ms.ToArray();

                    embeddedAssets.Add(new ExtractedAsset(
                        assetName,
                        imagePart.ContentType,
                        imageData,
                        null
                    ));

                    sb.AppendLine($"[IMAGE: {assetName}]");
                }

                if (imageIndex > 0)
                    sb.AppendLine();
            }
        }

        // Use first heading as title if no document title
        if (string.IsNullOrEmpty(documentTitle))
        {
            var content = sb.ToString();
            var lines = content.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("### "))
                {
                    documentTitle = trimmed[4..].Trim();
                    break;
                }
            }
        }

        return Task.FromResult(new DocumentContent
        {
            Markdown = sb.ToString().Trim(),
            Title = documentTitle ?? Path.GetFileNameWithoutExtension(filePath),
            ContentType = "pptx",
            Metadata = metadata,
            Assets = embeddedAssets.Count > 0 ? embeddedAssets : null
        });
    }

    /// <summary>
    ///     Extract text content from a slide, including title and body text.
    /// </summary>
    private static string ExtractSlideContent(SlidePart slidePart)
    {
        var sb = new StringBuilder();
        var slide = slidePart.Slide;

        if (slide?.CommonSlideData?.ShapeTree == null)
            return string.Empty;

        // Get all shapes with text
        foreach (var shape in slide.CommonSlideData.ShapeTree.Elements<Shape>())
        {
            var textBody = shape.TextBody;
            if (textBody == null) continue;

            // Check if this is a title placeholder
            var isTitle = IsPlaceholderType(shape, PlaceholderValues.Title) ||
                          IsPlaceholderType(shape, PlaceholderValues.CenteredTitle);

            var shapeText = ExtractTextFromTextBody(textBody);
            if (string.IsNullOrWhiteSpace(shapeText)) continue;

            if (isTitle)
                // Format as heading
                sb.AppendLine($"### {shapeText}");
            else
                // Regular content
                sb.AppendLine(shapeText);
        }

        // Also extract text from group shapes
        foreach (var groupShape in slide.CommonSlideData.ShapeTree.Elements<GroupShape>())
        {
            var groupText = ExtractTextFromGroupShape(groupShape);
            if (!string.IsNullOrWhiteSpace(groupText)) sb.AppendLine(groupText);
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    ///     Extract text from a TextBody element, preserving paragraph structure.
    /// </summary>
    private static string ExtractTextFromTextBody(TextBody textBody)
    {
        var sb = new StringBuilder();

        foreach (var para in textBody.Elements<A.Paragraph>())
        {
            var paraText = new StringBuilder();

            foreach (var run in para.Elements<A.Run>())
            {
                var text = run.Text?.Text;
                if (!string.IsNullOrEmpty(text)) paraText.Append(text);
            }

            // Get level for indentation
            var level = para.ParagraphProperties?.Level?.Value ?? 0;

            var lineText = paraText.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(lineText))
            {
                // Check if it has bullet styling (level > 0 or explicit bullet)
                if (level > 0 || HasBulletStyling(para))
                {
                    // Indent based on level
                    var indent = new string(' ', level * 2);
                    sb.AppendLine($"{indent}- {lineText}");
                }
                else
                {
                    sb.AppendLine(lineText);
                }
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    ///     Check if a paragraph has bullet styling.
    /// </summary>
    private static bool HasBulletStyling(A.Paragraph para)
    {
        var props = para.ParagraphProperties;
        if (props == null) return false;

        // Check for any bullet-related elements
        return props.GetFirstChild<A.CharacterBullet>() != null ||
               props.GetFirstChild<A.AutoNumberedBullet>() != null ||
               props.GetFirstChild<A.PictureBullet>() != null ||
               props.GetFirstChild<A.BulletFont>() != null;
    }

    /// <summary>
    ///     Extract text from a group shape (recursive).
    /// </summary>
    private static string ExtractTextFromGroupShape(GroupShape groupShape)
    {
        var sb = new StringBuilder();

        foreach (var shape in groupShape.Elements<Shape>())
        {
            var textBody = shape.TextBody;
            if (textBody != null)
            {
                var text = ExtractTextFromTextBody(textBody);
                if (!string.IsNullOrWhiteSpace(text)) sb.AppendLine(text);
            }
        }

        // Recurse into nested group shapes
        foreach (var nestedGroup in groupShape.Elements<GroupShape>())
        {
            var nestedText = ExtractTextFromGroupShape(nestedGroup);
            if (!string.IsNullOrWhiteSpace(nestedText)) sb.AppendLine(nestedText);
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    ///     Check if a shape is a specific placeholder type.
    /// </summary>
    private static bool IsPlaceholderType(Shape shape, PlaceholderValues placeholderType)
    {
        var placeholder = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?
            .GetFirstChild<PlaceholderShape>();

        return placeholder?.Type?.Value == placeholderType;
    }

    /// <summary>
    ///     Extract speaker notes from a slide.
    /// </summary>
    private static string ExtractSpeakerNotes(SlidePart slidePart)
    {
        var notesPart = slidePart.NotesSlidePart;
        if (notesPart?.NotesSlide?.CommonSlideData?.ShapeTree == null)
            return string.Empty;

        var sb = new StringBuilder();

        foreach (var shape in notesPart.NotesSlide.CommonSlideData.ShapeTree.Elements<Shape>())
        {
            // Find the notes placeholder (body text, not slide image placeholder)
            if (!IsPlaceholderType(shape, PlaceholderValues.Body)) continue;

            var textBody = shape.TextBody;
            if (textBody == null) continue;

            var text = ExtractTextFromTextBody(textBody);
            if (!string.IsNullOrWhiteSpace(text)) sb.AppendLine(text);
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    ///     Get file extension for an image content type.
    /// </summary>
    private static string GetImageExtension(string contentType)
    {
        return contentType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tiff",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "image/x-emf" or "image/x-wmf" => ".emf",
            _ => ".png" // Default
        };
    }

    /// <summary>
    ///     Extract data from an embedded chart.
    /// </summary>
    private static string? ExtractChartData(ChartPart chartPart, int slideNumber, int chartIndex)
    {
        try
        {
            var chartSpace = chartPart.ChartSpace;
            if (chartSpace == null)
                return null;

            var chart = chartSpace.GetFirstChild<C.Chart>();
            if (chart == null)
                return null;

            var sb = new StringBuilder();
            sb.AppendLine($"**Chart {chartIndex} (Slide {slideNumber}):**");

            // Get chart title
            var title = ExtractChartTitle(chart);
            if (!string.IsNullOrEmpty(title))
                sb.AppendLine($"*Title: {title}*");

            var plotArea = chart.PlotArea;
            if (plotArea == null)
                return sb.ToString();

            // Try to extract data from different chart types
            var dataExtracted = false;

            // Bar charts (including column charts)
            foreach (var barChart in plotArea.Elements<C.BarChart>())
            {
                dataExtracted = true;
                sb.AppendLine("Type: Bar/Column Chart");
                ExtractBarChartData(barChart, sb);
            }

            // Pie charts
            foreach (var pieChart in plotArea.Elements<C.PieChart>())
            {
                dataExtracted = true;
                sb.AppendLine("Type: Pie Chart");
                ExtractPieChartData(pieChart, sb);
            }

            // Line charts
            foreach (var lineChart in plotArea.Elements<C.LineChart>())
            {
                dataExtracted = true;
                sb.AppendLine("Type: Line Chart");
                ExtractLineChartData(lineChart, sb);
            }

            // Area charts
            foreach (var areaChart in plotArea.Elements<C.AreaChart>())
            {
                dataExtracted = true;
                sb.AppendLine("Type: Area Chart");
                ExtractAreaChartData(areaChart, sb);
            }

            // Scatter charts
            foreach (var scatterChart in plotArea.Elements<C.ScatterChart>())
            {
                dataExtracted = true;
                sb.AppendLine("Type: Scatter Plot");
                ExtractScatterChartData(scatterChart, sb);
            }

            // Doughnut charts
            foreach (var doughnutChart in plotArea.Elements<C.DoughnutChart>())
            {
                dataExtracted = true;
                sb.AppendLine("Type: Doughnut Chart");
                ExtractDoughnutChartData(doughnutChart, sb);
            }

            if (!dataExtracted)
                sb.AppendLine("[Data extraction not available for this chart type]");

            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Extract chart title.
    /// </summary>
    private static string? ExtractChartTitle(C.Chart chart)
    {
        var title = chart.Title;
        if (title == null)
            return null;

        var chartText = title.ChartText;
        if (chartText?.RichText != null)
        {
            var sb = new StringBuilder();
            foreach (var para in chartText.RichText.Elements<A.Paragraph>())
                foreach (var run in para.Elements<A.Run>())
                {
                    var text = run.Text?.Text;
                    if (!string.IsNullOrEmpty(text))
                        sb.Append(text);
                }

            return sb.ToString().Trim();
        }

        return null;
    }

    /// <summary>
    ///     Extract data from bar chart series.
    /// </summary>
    private static void ExtractBarChartData(C.BarChart barChart, StringBuilder sb)
    {
        foreach (var series in barChart.Elements<C.BarChartSeries>())
        {
            var seriesName = GetSeriesName(series.GetFirstChild<C.SeriesText>());
            sb.AppendLine($"Series: {seriesName ?? "Unnamed"}");

            var categories = GetCategoryValues(series.GetFirstChild<C.CategoryAxisData>());
            var values = GetNumericValues(series.GetFirstChild<C.Values>());

            if (categories != null && values != null)
                for (var i = 0; i < Math.Min(categories.Count, values.Count); i++)
                    sb.AppendLine($"  - {categories[i]}: {values[i]:G}");
            else if (values != null)
                for (var i = 0; i < values.Count; i++)
                    sb.AppendLine($"  - Point {i + 1}: {values[i]:G}");
        }
    }

    /// <summary>
    ///     Extract data from pie chart series.
    /// </summary>
    private static void ExtractPieChartData(C.PieChart pieChart, StringBuilder sb)
    {
        foreach (var series in pieChart.Elements<C.PieChartSeries>())
        {
            var categories = GetCategoryValues(series.GetFirstChild<C.CategoryAxisData>());
            var values = GetNumericValues(series.GetFirstChild<C.Values>());

            if (categories != null && values != null)
            {
                var total = values.Sum();
                for (var i = 0; i < Math.Min(categories.Count, values.Count); i++)
                {
                    var percentage = total > 0 ? values[i] / total * 100 : 0;
                    sb.AppendLine($"  - {categories[i]}: {values[i]:G} ({percentage:F1}%)");
                }
            }
            else if (values != null)
            {
                for (var i = 0; i < values.Count; i++) sb.AppendLine($"  - Segment {i + 1}: {values[i]:G}");
            }
        }
    }

    /// <summary>
    ///     Extract data from line chart series.
    /// </summary>
    private static void ExtractLineChartData(C.LineChart lineChart, StringBuilder sb)
    {
        foreach (var series in lineChart.Elements<C.LineChartSeries>())
        {
            var seriesName = GetSeriesName(series.GetFirstChild<C.SeriesText>());
            sb.AppendLine($"Series: {seriesName ?? "Unnamed"}");

            var categories = GetCategoryValues(series.GetFirstChild<C.CategoryAxisData>());
            var values = GetNumericValues(series.GetFirstChild<C.Values>());

            if (categories != null && values != null)
                for (var i = 0; i < Math.Min(categories.Count, values.Count); i++)
                    sb.AppendLine($"  - {categories[i]}: {values[i]:G}");
            else if (values != null)
                for (var i = 0; i < values.Count; i++)
                    sb.AppendLine($"  - Point {i + 1}: {values[i]:G}");
        }
    }

    /// <summary>
    ///     Extract data from area chart series.
    /// </summary>
    private static void ExtractAreaChartData(C.AreaChart areaChart, StringBuilder sb)
    {
        foreach (var series in areaChart.Elements<C.AreaChartSeries>())
        {
            var seriesName = GetSeriesName(series.GetFirstChild<C.SeriesText>());
            sb.AppendLine($"Series: {seriesName ?? "Unnamed"}");

            var categories = GetCategoryValues(series.GetFirstChild<C.CategoryAxisData>());
            var values = GetNumericValues(series.GetFirstChild<C.Values>());

            if (categories != null && values != null)
                for (var i = 0; i < Math.Min(categories.Count, values.Count); i++)
                    sb.AppendLine($"  - {categories[i]}: {values[i]:G}");
            else if (values != null)
                for (var i = 0; i < values.Count; i++)
                    sb.AppendLine($"  - Point {i + 1}: {values[i]:G}");
        }
    }

    /// <summary>
    ///     Extract data from scatter chart series.
    /// </summary>
    private static void ExtractScatterChartData(C.ScatterChart scatterChart, StringBuilder sb)
    {
        foreach (var series in scatterChart.Elements<C.ScatterChartSeries>())
        {
            var seriesName = GetSeriesName(series.GetFirstChild<C.SeriesText>());
            sb.AppendLine($"Series: {seriesName ?? "Unnamed"}");

            var xValues = GetNumericValues(series.GetFirstChild<C.XValues>());
            var yValues = GetNumericValues(series.GetFirstChild<C.YValues>());

            if (xValues != null && yValues != null)
                for (var i = 0; i < Math.Min(xValues.Count, yValues.Count); i++)
                    sb.AppendLine($"  - ({xValues[i]:G}, {yValues[i]:G})");
        }
    }

    /// <summary>
    ///     Extract data from doughnut chart series.
    /// </summary>
    private static void ExtractDoughnutChartData(C.DoughnutChart doughnutChart, StringBuilder sb)
    {
        foreach (var series in doughnutChart.Elements<C.PieChartSeries>())
        {
            var categories = GetCategoryValues(series.GetFirstChild<C.CategoryAxisData>());
            var values = GetNumericValues(series.GetFirstChild<C.Values>());

            if (categories != null && values != null)
            {
                var total = values.Sum();
                for (var i = 0; i < Math.Min(categories.Count, values.Count); i++)
                {
                    var percentage = total > 0 ? values[i] / total * 100 : 0;
                    sb.AppendLine($"  - {categories[i]}: {values[i]:G} ({percentage:F1}%)");
                }
            }
            else if (values != null)
            {
                for (var i = 0; i < values.Count; i++) sb.AppendLine($"  - Segment {i + 1}: {values[i]:G}");
            }
        }
    }

    /// <summary>
    ///     Get series name from SeriesText element.
    /// </summary>
    private static string? GetSeriesName(C.SeriesText? seriesText)
    {
        if (seriesText?.NumericValue != null)
            return seriesText.NumericValue.Text;

        var stringRef = seriesText?.StringReference;
        if (stringRef?.StringCache != null)
        {
            var pt = stringRef.StringCache.Elements<C.StringPoint>().FirstOrDefault();
            return pt?.NumericValue?.Text;
        }

        return null;
    }

    /// <summary>
    ///     Get category values from CategoryAxisData.
    /// </summary>
    private static List<string>? GetCategoryValues(C.CategoryAxisData? catData)
    {
        if (catData == null)
            return null;

        // Try StringReference first
        var stringRef = catData.StringReference;
        if (stringRef?.StringCache != null)
            return stringRef.StringCache.Elements<C.StringPoint>()
                .OrderBy(p => p.Index?.Value ?? 0)
                .Select(p => p.NumericValue?.Text ?? "")
                .ToList();

        // Try NumberReference (for numeric categories)
        var numRef = catData.NumberReference;
        if (numRef?.NumberingCache != null)
            return numRef.NumberingCache.Elements<C.NumericPoint>()
                .OrderBy(p => p.Index?.Value ?? 0)
                .Select(p => p.NumericValue?.Text ?? "")
                .ToList();

        // Try StringLiteral
        var stringLit = catData.StringLiteral;
        if (stringLit != null)
            return stringLit.Elements<C.StringPoint>()
                .OrderBy(p => p.Index?.Value ?? 0)
                .Select(p => p.NumericValue?.Text ?? "")
                .ToList();

        return null;
    }

    /// <summary>
    ///     Get numeric values from Values element.
    /// </summary>
    private static List<double>? GetNumericValues(C.Values? values)
    {
        if (values == null)
            return null;

        var numRef = values.NumberReference;
        if (numRef?.NumberingCache != null)
            return numRef.NumberingCache.Elements<C.NumericPoint>()
                .OrderBy(p => p.Index?.Value ?? 0)
                .Select(p => double.TryParse(p.NumericValue?.Text, out var v) ? v : 0)
                .ToList();

        var numLit = values.NumberLiteral;
        if (numLit != null)
            return numLit.Elements<C.NumericPoint>()
                .OrderBy(p => p.Index?.Value ?? 0)
                .Select(p => double.TryParse(p.NumericValue?.Text, out var v) ? v : 0)
                .ToList();

        return null;
    }

    /// <summary>
    ///     Get numeric values from XValues element (for scatter charts).
    /// </summary>
    private static List<double>? GetNumericValues(C.XValues? xValues)
    {
        if (xValues == null)
            return null;

        var numRef = xValues.NumberReference;
        if (numRef?.NumberingCache != null)
            return numRef.NumberingCache.Elements<C.NumericPoint>()
                .OrderBy(p => p.Index?.Value ?? 0)
                .Select(p => double.TryParse(p.NumericValue?.Text, out var v) ? v : 0)
                .ToList();

        return null;
    }

    /// <summary>
    ///     Get numeric values from YValues element (for scatter charts).
    /// </summary>
    private static List<double>? GetNumericValues(C.YValues? yValues)
    {
        if (yValues == null)
            return null;

        var numRef = yValues.NumberReference;
        if (numRef?.NumberingCache != null)
            return numRef.NumberingCache.Elements<C.NumericPoint>()
                .OrderBy(p => p.Index?.Value ?? 0)
                .Select(p => double.TryParse(p.NumericValue?.Text, out var v) ? v : 0)
                .ToList();

        return null;
    }
}