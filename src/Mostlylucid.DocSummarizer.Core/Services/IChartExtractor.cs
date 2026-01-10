using Mostlylucid.DocSummarizer.Core.Models;

namespace Mostlylucid.DocSummarizer.Core.Services;

/// <summary>
/// Interface for detecting and extracting charts from documents
/// NOTE: This is a placeholder for future implementation (Phase 3)
/// See DESIGN_ChartExtraction.md for detailed design
/// </summary>
public interface IChartDetector
{
    /// <summary>
    /// Detect chart regions in a document
    /// </summary>
    Task<List<ChartRegion>> DetectChartsAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Check if detector is available (models loaded, dependencies installed)
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

/// <summary>
/// Interface for extracting data from detected charts
/// </summary>
public interface IChartDataExtractor
{
    /// <summary>
    /// Chart type this extractor supports
    /// </summary>
    ChartType SupportedType { get; }

    /// <summary>
    /// Extract data from a chart region
    /// </summary>
    Task<ExtractedChartData> ExtractAsync(
        ChartRegion chart,
        ChartExtractionOptions? options = null,
        CancellationToken ct = default);
}

/// <summary>
/// Represents a detected chart region in a document
/// </summary>
public class ChartRegion
{
    public required string Id { get; init; }
    public required string SourcePath { get; init; }
    public int PageNumber { get; init; }
    public float[]? BoundingBox { get; init; }  // [x0, y0, x1, y1]
    public ChartType Type { get; init; }
    public double Confidence { get; init; }
    public byte[]? ImageData { get; init; }  // Cropped chart image
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Extracted chart data in structured format
/// </summary>
public class ExtractedChartData
{
    public required string ChartId { get; init; }
    public required string SourcePath { get; init; }
    public ChartType Type { get; init; }
    public required string[] ColumnNames { get; init; }
    public required List<Dictionary<string, object>> Data { get; init; }
    public Dictionary<string, string>? AxisLabels { get; init; }
    public string? Title { get; init; }
    public string? Legend { get; init; }
    public double Confidence { get; init; }
    public byte[]? ChartImage { get; init; }

    /// <summary>
    /// Convert chart data to CSV format
    /// </summary>
    public string ToCsv()
    {
        var sb = new System.Text.StringBuilder();

        // Header
        sb.AppendLine(string.Join(",", ColumnNames));

        // Rows
        foreach (var row in Data)
        {
            var values = ColumnNames.Select(col =>
            {
                if (row.TryGetValue(col, out var value))
                {
                    return CsvEscape(value?.ToString() ?? "");
                }
                return "";
            });
            sb.AppendLine(string.Join(",", values));
        }

        return sb.ToString();
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }
}

/// <summary>
/// Chart type classification
/// </summary>
public enum ChartType
{
    Unknown,
    BarChart,
    LineChart,
    PieChart,
    ScatterPlot,
    BoxPlot,
    Heatmap,
    RadarChart,
    AreaChart,
    Histogram,
    Waterfall
}

/// <summary>
/// Options for chart data extraction
/// </summary>
public class ChartExtractionOptions
{
    /// <summary>
    /// Whether to use Vision LLM for extraction (higher accuracy, higher cost)
    /// </summary>
    public bool UseVisionLLM { get; init; } = false;

    /// <summary>
    /// Minimum confidence threshold (0-1)
    /// </summary>
    public double MinConfidence { get; init; } = 0.5;

    /// <summary>
    /// Whether to preserve chart image as evidence
    /// </summary>
    public bool SaveChartImage { get; init; } = true;

    /// <summary>
    /// Custom options for specific extractors
    /// </summary>
    public Dictionary<string, object>? CustomOptions { get; init; }
}

/// <summary>
/// Placeholder: Chart extraction service combining detection and extraction
/// </summary>
public interface IChartExtractionService
{
    /// <summary>
    /// Detect and extract all charts from a document
    /// </summary>
    Task<List<ExtractedChartData>> ExtractChartsFromDocumentAsync(
        string filePath,
        ChartExtractionOptions? options = null,
        CancellationToken ct = default);
}
