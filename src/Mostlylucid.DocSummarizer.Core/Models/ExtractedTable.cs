namespace Mostlylucid.DocSummarizer.Core.Models;

/// <summary>
/// Represents a table extracted from a document (PDF, DOCX, etc.)
/// </summary>
public class ExtractedTable
{
    /// <summary>
    /// Table identifier (auto-generated or from document metadata)
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Source document path
    /// </summary>
    public required string SourcePath { get; init; }

    /// <summary>
    /// Page number where table was found (PDF) or section (DOCX)
    /// </summary>
    public int PageOrSection { get; init; }

    /// <summary>
    /// Table number on the page/section (1-indexed)
    /// </summary>
    public int TableNumber { get; init; }

    /// <summary>
    /// Bounding box coordinates (if available) - [x0, y0, x1, y1]
    /// </summary>
    public float[]? BoundingBox { get; init; }

    /// <summary>
    /// Table rows (including header row)
    /// </summary>
    public required List<List<TableCell>> Rows { get; init; }

    /// <summary>
    /// Number of columns detected
    /// </summary>
    public int ColumnCount => Rows.FirstOrDefault()?.Count ?? 0;

    /// <summary>
    /// Number of rows (including header)
    /// </summary>
    public int RowCount => Rows.Count;

    /// <summary>
    /// Whether the first row is a header
    /// </summary>
    public bool HasHeader { get; init; } = true;

    /// <summary>
    /// Column names (if header detected)
    /// </summary>
    public List<string>? ColumnNames { get; init; }

    /// <summary>
    /// Extraction confidence score (0-1)
    /// </summary>
    public double? Confidence { get; init; }

    /// <summary>
    /// Extraction method used (e.g., "pdfplumber", "python-docx", "camelot")
    /// </summary>
    public string? ExtractionMethod { get; init; }

    /// <summary>
    /// Additional metadata
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Convert to CSV format
    /// </summary>
    public string ToCsv(bool includeHeader = true)
    {
        var sb = new System.Text.StringBuilder();

        int startRow = 0;
        if (HasHeader && !includeHeader)
        {
            startRow = 1;
        }

        for (int i = startRow; i < Rows.Count; i++)
        {
            var row = Rows[i];
            var cells = row.Select(c => CsvEscape(c.Text ?? ""));
            sb.AppendLine(string.Join(",", cells));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Escape CSV cell value
    /// </summary>
    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    /// <summary>
    /// Get table as 2D string array (for easy access)
    /// </summary>
    public string[][] ToArray()
    {
        return Rows.Select(row =>
            row.Select(cell => cell.Text ?? "").ToArray()
        ).ToArray();
    }
}

/// <summary>
/// Represents a single cell in an extracted table
/// </summary>
public class TableCell
{
    /// <summary>
    /// Cell text content
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// Row span (for merged cells)
    /// </summary>
    public int RowSpan { get; init; } = 1;

    /// <summary>
    /// Column span (for merged cells)
    /// </summary>
    public int ColSpan { get; init; } = 1;

    /// <summary>
    /// Cell bounding box (if available)
    /// </summary>
    public float[]? BoundingBox { get; init; }

    /// <summary>
    /// Text alignment (left, center, right)
    /// </summary>
    public string? Alignment { get; init; }

    /// <summary>
    /// Cell formatting metadata (bold, italic, etc.)
    /// </summary>
    public Dictionary<string, object>? Formatting { get; init; }

    /// <summary>
    /// Whether this cell is empty
    /// </summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    /// <summary>
    /// Create a simple text cell
    /// </summary>
    public static TableCell FromText(string text) => new() { Text = text };
}

/// <summary>
/// Result of table extraction operation
/// </summary>
public class TableExtractionResult
{
    /// <summary>
    /// Extracted tables
    /// </summary>
    public required List<ExtractedTable> Tables { get; init; }

    /// <summary>
    /// Source document path
    /// </summary>
    public required string SourcePath { get; init; }

    /// <summary>
    /// Total number of pages/sections processed
    /// </summary>
    public int TotalPages { get; init; }

    /// <summary>
    /// Extraction duration
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Errors encountered during extraction
    /// </summary>
    public List<string>? Errors { get; init; }

    /// <summary>
    /// Warnings
    /// </summary>
    public List<string>? Warnings { get; init; }

    /// <summary>
    /// Whether extraction was successful
    /// </summary>
    public bool Success => (Errors?.Count ?? 0) == 0;
}
