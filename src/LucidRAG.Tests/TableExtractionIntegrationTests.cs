using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace LucidRAG.Tests;

public class TableExtractionIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<TableExtractorFactory> _factoryLogger;
    private readonly ILogger<DocxTableExtractor> _docxLogger;
    private readonly ILogger<PdfTableExtractor> _pdfLogger;

    public TableExtractionIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());
        _factoryLogger = loggerFactory.CreateLogger<TableExtractorFactory>();
        _docxLogger = loggerFactory.CreateLogger<DocxTableExtractor>();
        _pdfLogger = loggerFactory.CreateLogger<PdfTableExtractor>();
    }

    [Fact]
    public async Task ExtractTablesFromDocx_SimpleTable_Success()
    {
        // Arrange
        var testFile = Path.Combine(Path.GetTempPath(), "test_table.docx");
        CreateTestDocxWithTable(testFile);

        try
        {
            var factory = new TableExtractorFactory(_factoryLogger, LoggerFactory.Create(b => b.AddDebug()));

            // Act
            var extractor = await factory.GetExtractorForFileAsync(testFile);
            Assert.NotNull(extractor);
            Assert.IsType<DocxTableExtractor>(extractor);

            var result = await extractor.ExtractTablesAsync(testFile);

            // Assert
            Assert.True(result.Success);
            Assert.Single(result.Tables);

            var table = result.Tables[0];
            Assert.Equal(4, table.RowCount); // 1 header + 3 data rows
            Assert.Equal(3, table.ColumnCount);
            Assert.True(table.HasHeader);
            Assert.NotNull(table.ColumnNames);
            Assert.Equal(new[] { "Product", "Quantity", "Price" }, table.ColumnNames);

            // Verify CSV export
            var csv = table.ToCsv();
            _output.WriteLine("Extracted CSV:");
            _output.WriteLine(csv);

            Assert.Contains("Product,Quantity,Price", csv);
            Assert.Contains("Widget A,100,10.99", csv);

            _output.WriteLine($"Confidence: {table.Confidence:F2}");
            Assert.True(table.Confidence >= 0.7);
        }
        finally
        {
            if (File.Exists(testFile))
            {
                File.Delete(testFile);
            }
        }
    }

    [Fact]
    public async Task ExtractTablesFromDocx_MultipleTables_Success()
    {
        // Arrange
        var testFile = Path.Combine(Path.GetTempPath(), "test_multiple_tables.docx");
        CreateDocxWithMultipleTables(testFile);

        try
        {
            var factory = new TableExtractorFactory(_factoryLogger, LoggerFactory.Create(b => b.AddDebug()));
            var extractor = await factory.GetExtractorForFileAsync(testFile);

            // Act
            var result = await extractor!.ExtractTablesAsync(testFile);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(2, result.Tables.Count);

            _output.WriteLine($"Extracted {result.Tables.Count} tables:");
            foreach (var table in result.Tables)
            {
                _output.WriteLine($"  Table {table.TableNumber}: {table.RowCount}×{table.ColumnCount}");
                _output.WriteLine($"    Confidence: {table.Confidence:F2}");
                if (table.ColumnNames != null)
                {
                    _output.WriteLine($"    Columns: {string.Join(", ", table.ColumnNames)}");
                }
            }
        }
        finally
        {
            if (File.Exists(testFile))
            {
                File.Delete(testFile);
            }
        }
    }

    [Fact]
    public async Task TableExtractorFactory_UnsupportedExtension_ReturnsNull()
    {
        // Arrange
        var testFile = Path.Combine(Path.GetTempPath(), "test.txt");
        File.WriteAllText(testFile, "Not a supported file");

        try
        {
            var factory = new TableExtractorFactory(_factoryLogger, LoggerFactory.Create(b => b.AddDebug()));

            // Act
            var extractor = await factory.GetExtractorForFileAsync(testFile);

            // Assert
            Assert.Null(extractor);
        }
        finally
        {
            if (File.Exists(testFile))
            {
                File.Delete(testFile);
            }
        }
    }

    private void CreateTestDocxWithTable(string filePath)
    {
        using var doc = WordprocessingDocument.Create(filePath, WordprocessingDocumentType.Document);
        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document();
        var body = mainPart.Document.AppendChild(new Body());

        // Add a paragraph before the table
        body.AppendChild(new Paragraph(new Run(new Text("Sales Report"))));

        // Create table
        var table = new Table();

        // Table properties
        var tblProp = new TableProperties(
            new TableBorders(
                new TopBorder { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4 },
                new BottomBorder { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4 },
                new LeftBorder { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4 },
                new RightBorder { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4 },
                new InsideHorizontalBorder { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4 },
                new InsideVerticalBorder { Val = new EnumValue<BorderValues>(BorderValues.Single), Size = 4 }
            )
        );
        table.AppendChild(tblProp);

        // Header row
        var headerRow = new TableRow();
        headerRow.Append(
            CreateTableCell("Product"),
            CreateTableCell("Quantity"),
            CreateTableCell("Price")
        );
        table.Append(headerRow);

        // Data rows
        table.Append(CreateDataRow("Widget A", "100", "10.99"));
        table.Append(CreateDataRow("Widget B", "250", "5.99"));
        table.Append(CreateDataRow("Gadget X", "75", "25.00"));

        body.Append(table);
        mainPart.Document.Save();
    }

    private void CreateDocxWithMultipleTables(string filePath)
    {
        using var doc = WordprocessingDocument.Create(filePath, WordprocessingDocumentType.Document);
        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document();
        var body = mainPart.Document.AppendChild(new Body());

        // Table 1: Sales Data
        body.AppendChild(new Paragraph(new Run(new Text("Table 1: Sales Data"))));
        var table1 = CreateSimpleTable(
            new[] { "Product", "Q1", "Q2" },
            new[]
            {
                new[] { "Alpha", "100", "150" },
                new[] { "Beta", "75", "90" }
            }
        );
        body.Append(table1);

        // Paragraph between tables
        body.AppendChild(new Paragraph(new Run(new Text(""))));

        // Table 2: Regional Data
        body.AppendChild(new Paragraph(new Run(new Text("Table 2: Regional Data"))));
        var table2 = CreateSimpleTable(
            new[] { "Region", "Revenue" },
            new[]
            {
                new[] { "North", "125000" },
                new[] { "South", "98000" },
                new[] { "East", "156000" }
            }
        );
        body.Append(table2);

        mainPart.Document.Save();
    }

    private Table CreateSimpleTable(string[] headers, string[][] rows)
    {
        var table = new Table();

        // Header row
        var headerRow = new TableRow();
        foreach (var header in headers)
        {
            headerRow.Append(CreateTableCell(header));
        }
        table.Append(headerRow);

        // Data rows
        foreach (var row in rows)
        {
            var tableRow = new TableRow();
            foreach (var cell in row)
            {
                tableRow.Append(CreateTableCell(cell));
            }
            table.Append(tableRow);
        }

        return table;
    }

    private TableRow CreateDataRow(params string[] cells)
    {
        var row = new TableRow();
        foreach (var cellText in cells)
        {
            row.Append(CreateTableCell(cellText));
        }
        return row;
    }

    private TableCell CreateTableCell(string text)
    {
        return new TableCell(
            new Paragraph(
                new Run(
                    new Text(text)
                )
            )
        );
    }
}
