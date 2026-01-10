using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Mostlylucid.DocSummarizer.Core.Services;

/// <summary>
/// Extract tables from PDF documents using PdfPig (.NET native)
/// Uses heuristic-based detection: looks for grid patterns in text positioning
/// </summary>
public class PdfTableExtractor : ITableExtractor
{
    private readonly ILogger<PdfTableExtractor> _logger;

    public PdfTableExtractor(ILogger<PdfTableExtractor> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<string> SupportedExtensions => new[] { ".pdf" };
    public string Name => "PdfPig";

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // Always available (no external dependencies)
        return Task.FromResult(true);
    }

    public async Task<TableExtractionResult> ExtractTablesAsync(
        string filePath,
        TableExtractionOptions? options = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        options ??= new TableExtractionOptions();
        var extractedTables = new List<ExtractedTable>();
        var errors = new List<string>();

        await Task.Run(() =>
        {
            try
            {
                using var document = PdfDocument.Open(filePath);

                var pagesToProcess = options.Pages ?? Enumerable.Range(1, document.NumberOfPages).ToList();

                var globalTableNumber = 1;

                foreach (var pageNum in pagesToProcess)
                {
                    if (pageNum < 1 || pageNum > document.NumberOfPages)
                    {
                        continue;
                    }

                    try
                    {
                        var page = document.GetPage(pageNum);
                        var tables = ExtractTablesFromPage(page, filePath, pageNum, options, ref globalTableNumber);

                        extractedTables.AddRange(tables);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to extract tables from page {Page} of {File}",
                            pageNum, Path.GetFileName(filePath));
                        errors.Add($"Page {pageNum}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open PDF {File}", filePath);
                errors.Add(ex.Message);
            }
        }, ct);

        stopwatch.Stop();

        return new TableExtractionResult
        {
            SourcePath = filePath,
            Tables = extractedTables,
            TotalPages = options.Pages?.Count ?? 0,
            Duration = stopwatch.Elapsed,
            Errors = errors.Count > 0 ? errors : null
        };
    }

    private List<ExtractedTable> ExtractTablesFromPage(
        Page page,
        string sourcePath,
        int pageNumber,
        TableExtractionOptions options,
        ref int globalTableNumber)
    {
        var tables = new List<ExtractedTable>();

        // Simple heuristic: group words by Y-coordinate (rows) and X-coordinate (columns)
        var words = page.GetWords().OrderBy(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left).ToList();

        if (words.Count == 0)
        {
            return tables;
        }

        // Group words into rows based on Y-coordinate proximity
        var rows = GroupWordsIntoRows(words);

        // Detect table-like regions (rows with consistent column structure)
        var tableRegions = DetectTableRegions(rows);

        foreach (var region in tableRegions)
        {
            try
            {
                var table = BuildTableFromRegion(region, sourcePath, pageNumber, globalTableNumber, options);

                if (table != null &&
                    table.RowCount >= options.MinRows &&
                    table.ColumnCount >= options.MinColumns)
                {
                    tables.Add(table);
                    globalTableNumber++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to build table from region on page {Page}", pageNumber);
            }
        }

        return tables;
    }

    private static List<List<Word>> GroupWordsIntoRows(List<Word> words)
    {
        var rows = new List<List<Word>>();
        var currentRow = new List<Word>();
        double lastY = double.MinValue;
        const double rowTolerance = 5.0; // Points

        foreach (var word in words)
        {
            var wordY = word.BoundingBox.Bottom;

            if (Math.Abs(wordY - lastY) > rowTolerance && currentRow.Count > 0)
            {
                rows.Add(currentRow.OrderBy(w => w.BoundingBox.Left).ToList());
                currentRow = new List<Word>();
            }

            currentRow.Add(word);
            lastY = wordY;
        }

        if (currentRow.Count > 0)
        {
            rows.Add(currentRow.OrderBy(w => w.BoundingBox.Left).ToList());
        }

        return rows;
    }

    private static List<List<List<Word>>> DetectTableRegions(List<List<Word>> rows)
    {
        var regions = new List<List<List<Word>>>();
        var currentRegion = new List<List<Word>>();

        foreach (var row in rows)
        {
            // Heuristic: a table row has multiple "columns" (words separated by consistent gaps)
            if (row.Count >= 2 && LooksLikeTableRow(row))
            {
                currentRegion.Add(row);
            }
            else if (currentRegion.Count > 0)
            {
                // End of table region
                if (currentRegion.Count >= 2) // Minimum 2 rows for a table
                {
                    regions.Add(currentRegion);
                }
                currentRegion = new List<List<Word>>();
            }
        }

        if (currentRegion.Count >= 2)
        {
            regions.Add(currentRegion);
        }

        return regions;
    }

    private static bool LooksLikeTableRow(List<Word> row)
    {
        if (row.Count < 2)
        {
            return false;
        }

        // Check if words are reasonably spaced (not continuous text)
        var gaps = new List<double>();
        for (int i = 0; i < row.Count - 1; i++)
        {
            var gap = row[i + 1].BoundingBox.Left - row[i].BoundingBox.Right;
            gaps.Add(gap);
        }

        // If there are significant gaps, likely a table row
        var avgGap = gaps.Average();
        return avgGap > 10.0; // Points
    }

    private ExtractedTable? BuildTableFromRegion(
        List<List<Word>> region,
        string sourcePath,
        int pageNumber,
        int tableNumber,
        TableExtractionOptions options)
    {
        // Convert region to table structure
        var tableRows = new List<List<TableCell>>();

        // Align words into columns by X-coordinate
        var columnBoundaries = DetectColumnBoundaries(region);

        foreach (var row in region)
        {
            var tableCells = new List<TableCell>();

            foreach (var boundary in columnBoundaries)
            {
                var cellWords = row.Where(w =>
                    w.BoundingBox.Left >= boundary.Start &&
                    w.BoundingBox.Left < boundary.End
                ).ToList();

                var cellText = string.Join(" ", cellWords.Select(w => w.Text));
                tableCells.Add(TableCell.FromText(cellText));
            }

            if (tableCells.Any(c => !c.IsEmpty))
            {
                tableRows.Add(tableCells);
            }
        }

        if (tableRows.Count == 0)
        {
            return null;
        }

        var hasHeader = DetectHeader(tableRows);

        List<string>? columnNames = null;
        if (hasHeader && tableRows.Count > 0)
        {
            columnNames = tableRows[0].Select(c => c.Text ?? "").ToList();
        }

        var tableId = $"{Path.GetFileNameWithoutExtension(sourcePath)}_table_{tableNumber}";

        return new ExtractedTable
        {
            Id = tableId,
            SourcePath = sourcePath,
            PageOrSection = pageNumber,
            TableNumber = tableNumber,
            BoundingBox = null,
            Rows = tableRows,
            HasHeader = hasHeader,
            ColumnNames = columnNames,
            Confidence = 0.6, // Lower confidence for heuristic extraction
            ExtractionMethod = Name,
            Metadata = new Dictionary<string, object>
            {
                ["pageNumber"] = pageNumber,
                ["extractionMethod"] = "heuristic",
                ["rowCount"] = tableRows.Count,
                ["columnCount"] = columnBoundaries.Count
            }
        };
    }

    private static List<(double Start, double End)> DetectColumnBoundaries(List<List<Word>> region)
    {
        // Collect all X-coordinates
        var xCoords = new List<double>();

        foreach (var row in region)
        {
            foreach (var word in row)
            {
                xCoords.Add(word.BoundingBox.Left);
            }
        }

        if (xCoords.Count == 0)
        {
            return new List<(double, double)>();
        }

        // Cluster X-coordinates to find column starts
        xCoords.Sort();

        var columns = new List<(double Start, double End)>();
        double currentStart = xCoords[0];
        const double columnTolerance = 20.0; // Points

        for (int i = 1; i < xCoords.Count; i++)
        {
            if (xCoords[i] - xCoords[i - 1] > columnTolerance)
            {
                // New column
                columns.Add((currentStart, xCoords[i - 1] + columnTolerance));
                currentStart = xCoords[i];
            }
        }

        columns.Add((currentStart, xCoords.Last() + columnTolerance));

        return columns;
    }

    private static bool DetectHeader(List<List<TableCell>> rows)
    {
        if (rows.Count < 2)
        {
            return false;
        }

        var firstRow = rows[0];
        var nonNumericCount = firstRow.Count(cell => !IsNumeric(cell.Text ?? ""));

        return nonNumericCount >= firstRow.Count * 0.6;
    }

    private static bool IsNumeric(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var cleaned = value.Replace(",", "").Replace("$", "").Replace("%", "").Trim();
        return double.TryParse(cleaned, out _);
    }
}
