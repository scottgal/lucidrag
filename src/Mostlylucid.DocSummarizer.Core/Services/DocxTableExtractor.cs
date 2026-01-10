using System.Diagnostics;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Core.Models;
using OpenXmlTableCell = DocumentFormat.OpenXml.Wordprocessing.TableCell;
using OpenXmlTable = DocumentFormat.OpenXml.Wordprocessing.Table;
using OpenXmlTableRow = DocumentFormat.OpenXml.Wordprocessing.TableRow;
using OpenXmlParagraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;

namespace Mostlylucid.DocSummarizer.Core.Services;

/// <summary>
/// Extract tables from DOCX documents using DocumentFormat.OpenXml (.NET native)
/// </summary>
public class DocxTableExtractor : ITableExtractor
{
    private readonly ILogger<DocxTableExtractor> _logger;

    public DocxTableExtractor(ILogger<DocxTableExtractor> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<string> SupportedExtensions => new[] { ".docx" };
    public string Name => "DocumentFormat.OpenXml";

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

        try
        {
            using var doc = WordprocessingDocument.Open(filePath, false);
            var body = doc.MainDocumentPart?.Document?.Body;

            if (body == null)
            {
                errors.Add("Document body is null");
                return new TableExtractionResult
                {
                    SourcePath = filePath,
                    Tables = extractedTables,
                    Duration = stopwatch.Elapsed,
                    Errors = errors
                };
            }

            var tables = body.Descendants<OpenXmlTable>().ToList();
            var tableNumber = 1;

            foreach (var table in tables)
            {
                try
                {
                    var extractedTable = ExtractTable(table, filePath, tableNumber, options);

                    if (extractedTable != null &&
                        extractedTable.RowCount >= options.MinRows &&
                        extractedTable.ColumnCount >= options.MinColumns)
                    {
                        extractedTables.Add(extractedTable);
                        tableNumber++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to extract table {TableNumber} from {File}",
                        tableNumber, Path.GetFileName(filePath));
                    errors.Add($"Table {tableNumber}: {ex.Message}");
                }
            }

            stopwatch.Stop();

            return new TableExtractionResult
            {
                SourcePath = filePath,
                Tables = extractedTables,
                TotalPages = 1, // DOCX doesn't have page concept
                Duration = stopwatch.Elapsed,
                Errors = errors.Count > 0 ? errors : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract tables from {File}", filePath);

            return new TableExtractionResult
            {
                SourcePath = filePath,
                Tables = extractedTables,
                Duration = stopwatch.Elapsed,
                Errors = new List<string> { ex.Message }
            };
        }
    }

    private ExtractedTable? ExtractTable(
        OpenXmlTable table,
        string sourcePath,
        int tableNumber,
        TableExtractionOptions options)
    {
        var rows = new List<List<TableCell>>();

        foreach (var row in table.Elements<OpenXmlTableRow>())
        {
            var cellsInRow = new List<TableCell>();

            foreach (var cell in row.Elements<OpenXmlTableCell>())
            {
                var cellText = GetCellText(cell);
                cellsInRow.Add(TableCell.FromText(cellText));
            }

            if (cellsInRow.Count > 0)
            {
                rows.Add(cellsInRow);
            }
        }

        if (rows.Count == 0)
        {
            return null;
        }

        // Detect header (first row typically has different styling or non-numeric content)
        var hasHeader = DetectHeader(rows);

        // Extract column names if header detected
        List<string>? columnNames = null;
        if (hasHeader && rows.Count > 0)
        {
            columnNames = rows[0].Select(c => c.Text ?? "").ToList();
        }

        var tableId = $"{Path.GetFileNameWithoutExtension(sourcePath)}_table_{tableNumber}";

        return new ExtractedTable
        {
            Id = tableId,
            SourcePath = sourcePath,
            PageOrSection = tableNumber,
            TableNumber = tableNumber,
            BoundingBox = null, // Not available in OpenXml
            Rows = rows,
            HasHeader = hasHeader,
            ColumnNames = columnNames,
            Confidence = EstimateConfidence(rows),
            ExtractionMethod = Name,
            Metadata = new Dictionary<string, object>
            {
                ["rowCount"] = rows.Count,
                ["columnCount"] = rows.FirstOrDefault()?.Count ?? 0
            }
        };
    }

    private static string GetCellText(OpenXmlTableCell cell)
    {
        var paragraphs = cell.Elements<OpenXmlParagraph>();
        var texts = new List<string>();

        foreach (var para in paragraphs)
        {
            var paraText = para.InnerText.Trim();
            if (!string.IsNullOrEmpty(paraText))
            {
                texts.Add(paraText);
            }
        }

        return string.Join(" ", texts);
    }

    private static bool DetectHeader(List<List<TableCell>> rows)
    {
        if (rows.Count < 2)
        {
            return false;
        }

        var firstRow = rows[0];

        // Heuristic: if most cells in first row are non-numeric, likely a header
        var nonNumericCount = firstRow.Count(cell => !IsNumeric(cell.Text ?? ""));

        return nonNumericCount >= firstRow.Count * 0.6;
    }

    private static bool IsNumeric(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Remove common formatting
        var cleaned = value.Replace(",", "").Replace("$", "").Replace("%", "").Trim();

        return double.TryParse(cleaned, out _);
    }

    private static double EstimateConfidence(List<List<TableCell>> rows)
    {
        if (rows.Count == 0)
        {
            return 0.0;
        }

        // Check column consistency
        var colCounts = rows.Select(r => r.Count).Distinct().ToList();
        var consistent = colCounts.Count == 1;

        // Check fill rate
        var totalCells = rows.Sum(r => r.Count);
        var nonEmptyCells = rows.Sum(r => r.Count(c => !c.IsEmpty));
        var fillRate = totalCells > 0 ? (double)nonEmptyCells / totalCells : 0;

        var confidence = 0.7; // Base confidence (DOCX tables are reliable)

        if (consistent)
        {
            confidence += 0.2;
        }

        confidence += fillRate * 0.1;

        return Math.Min(1.0, confidence);
    }
}
