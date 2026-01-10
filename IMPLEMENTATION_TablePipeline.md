# Table Extraction Pipeline Implementation

## Architecture: DocSummarizer → DataSummarizer → LucidRAG

```
┌─────────────────────────────────────────────────────────────────┐
│                         LucidRAG Web App                        │
│                    (Orchestrates Everything)                    │
└────────────┬────────────────────────────────────┬───────────────┘
             │                                    │
             ▼                                    ▼
┌────────────────────────────┐      ┌────────────────────────────┐
│   DocSummarizer.Core       │      │   DataSummarizer           │
│                            │      │                            │
│  1. Extract text           │      │  3. Profile table          │
│  2. Detect tables          │──────▶  4. Generate statistics    │
│  3. Export tables to CSV   │      │  5. Create embeddings      │
│                            │      │  6. Return profile         │
└────────────────────────────┘      └────────────────────────────┘
             │                                    │
             │                                    │
             ▼                                    ▼
┌────────────────────────────────────────────────────────────────┐
│              Mostlylucid.Storage.Core (Unified)                │
│                                                                │
│  - Text embeddings (from DocSummarizer)                        │
│  - Table embeddings (from DataSummarizer)                      │
│  - Stored in same collection with type metadata               │
└────────────────────────────────────────────────────────────────┘
```

---

## Phase 1: DocSummarizer - Table Detection & Extraction

### 1.1 New Service: `ITableExtractor`

**File**: `src/Mostlylucid.DocSummarizer.Core/Services/ITableExtractor.cs`

```csharp
namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Detects and extracts tables from documents.
/// Returns tables as structured data ready for DataSummarizer profiling.
/// </summary>
public interface ITableExtractor
{
    /// <summary>
    /// Extract all tables from a document.
    /// </summary>
    Task<List<DocumentTable>> ExtractTablesAsync(
        string documentPath,
        TableExtractionOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Check if table extraction is available for this document type.
    /// </summary>
    bool SupportsFormat(string filePath);
}

/// <summary>
/// A table extracted from a document.
/// </summary>
public class DocumentTable
{
    /// <summary>
    /// Page number where table appears (PDF) or section (DOCX).
    /// </summary>
    public int PageNumber { get; set; }

    /// <summary>
    /// Table index on the page (0-based).
    /// </summary>
    public int TableIndex { get; set; }

    /// <summary>
    /// Table caption/title if detected.
    /// </summary>
    public string? Caption { get; set; }

    /// <summary>
    /// Header row (column names).
    /// </summary>
    public List<string> Headers { get; set; } = new();

    /// <summary>
    /// Data rows (excluding header).
    /// Each row is a list of cell values.
    /// </summary>
    public List<List<string>> Rows { get; set; } = new();

    /// <summary>
    /// Bounding box in document coordinates (for visual linking).
    /// </summary>
    public TableBounds? Bounds { get; set; }

    /// <summary>
    /// Confidence score (0-1) from extraction algorithm.
    /// </summary>
    public double Confidence { get; set; } = 1.0;

    /// <summary>
    /// Export table to CSV format for DataSummarizer.
    /// </summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();

        // Write header
        sb.AppendLine(string.Join(",", Headers.Select(CsvEscape)));

        // Write rows
        foreach (var row in Rows)
        {
            sb.AppendLine(string.Join(",", row.Select(CsvEscape)));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Save table as CSV file.
    /// Returns the path to the saved file.
    /// </summary>
    public async Task<string> SaveAsCsvAsync(string outputDirectory, string documentName)
    {
        var fileName = $"{SanitizeFileName(documentName)}_table_p{PageNumber}_{TableIndex}.csv";
        var filePath = Path.Combine(outputDirectory, fileName);

        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(filePath, ToCsv());

        return filePath;
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}

public class TableBounds
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public class TableExtractionOptions
{
    /// <summary>
    /// Minimum number of rows to consider something a table.
    /// </summary>
    public int MinRows { get; set; } = 2;

    /// <summary>
    /// Minimum number of columns to consider something a table.
    /// </summary>
    public int MinColumns { get; set; } = 2;

    /// <summary>
    /// Whether to include tables without clear borders.
    /// </summary>
    public bool DetectBorderlessTables { get; set; } = true;

    /// <summary>
    /// Extraction strategy to use.
    /// </summary>
    public TableExtractionStrategy Strategy { get; set; } = TableExtractionStrategy.Auto;
}

public enum TableExtractionStrategy
{
    /// <summary>
    /// Automatically choose best strategy for document type.
    /// </summary>
    Auto,

    /// <summary>
    /// Use pdfplumber (Python) for PDF tables - most accurate.
    /// </summary>
    PdfPlumber,

    /// <summary>
    /// Use Docling for PDF/DOCX - .NET native.
    /// </summary>
    Docling,

    /// <summary>
    /// Simple heuristic detection - fastest, least accurate.
    /// </summary>
    Heuristic
}
```

---

### 1.2 Implementation: `PdfTableExtractor`

**File**: `src/Mostlylucid.DocSummarizer.Core/Services/PdfTableExtractor.cs`

```csharp
using System.Diagnostics;
using System.Text.Json;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Extracts tables from PDF using pdfplumber (Python subprocess).
/// Falls back to heuristic detection if Python not available.
/// </summary>
public class PdfTableExtractor : ITableExtractor
{
    private readonly ILogger<PdfTableExtractor> _logger;
    private readonly string? _pythonPath;
    private bool _pdfPlumberAvailable;

    public PdfTableExtractor(ILogger<PdfTableExtractor> logger, string? pythonPath = null)
    {
        _logger = logger;
        _pythonPath = pythonPath ?? "python3";
        _pdfPlumberAvailable = CheckPdfPlumberAvailability();
    }

    public bool SupportsFormat(string filePath)
    {
        return Path.GetExtension(filePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<DocumentTable>> ExtractTablesAsync(
        string documentPath,
        TableExtractionOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new TableExtractionOptions();

        if (_pdfPlumberAvailable)
        {
            try
            {
                return await ExtractWithPdfPlumberAsync(documentPath, options, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "pdfplumber extraction failed, falling back to heuristic");
            }
        }

        // Fallback to heuristic extraction
        return await ExtractWithHeuristicAsync(documentPath, options, ct);
    }

    private async Task<List<DocumentTable>> ExtractWithPdfPlumberAsync(
        string documentPath,
        TableExtractionOptions options,
        CancellationToken ct)
    {
        // Create Python script inline
        var script = $$$"""
import sys
import json
import pdfplumber

pdf_path = sys.argv[1]
tables_data = []

with pdfplumber.open(pdf_path) as pdf:
    for page_num, page in enumerate(pdf.pages):
        tables = page.extract_tables()
        for table_idx, table in enumerate(tables):
            if not table or len(table) < {{{options.MinRows}}}:
                continue

            # First row is header
            headers = table[0] if table else []
            rows = table[1:] if len(table) > 1 else []

            # Filter empty columns
            if len(headers) < {{{options.MinColumns}}}:
                continue

            tables_data.append({
                "page_number": page_num + 1,
                "table_index": table_idx,
                "headers": headers,
                "rows": rows,
                "confidence": 0.9
            })

print(json.dumps(tables_data))
""";

        var scriptPath = Path.GetTempFileName() + ".py";
        await File.WriteAllTextAsync(scriptPath, script, ct);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = $"\"{scriptPath}\" \"{documentPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                throw new InvalidOperationException("Failed to start Python process");

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            var error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"pdfplumber failed: {error}");
            }

            var tablesJson = JsonSerializer.Deserialize<List<PdfPlumberTable>>(output);
            return tablesJson?.Select(t => new DocumentTable
            {
                PageNumber = t.page_number,
                TableIndex = t.table_index,
                Headers = t.headers,
                Rows = t.rows,
                Confidence = t.confidence
            }).ToList() ?? new List<DocumentTable>();
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    private Task<List<DocumentTable>> ExtractWithHeuristicAsync(
        string documentPath,
        TableExtractionOptions options,
        CancellationToken ct)
    {
        // TODO: Simple heuristic - look for aligned text blocks
        // For now, return empty list
        _logger.LogWarning("Heuristic table extraction not yet implemented for PDF");
        return Task.FromResult(new List<DocumentTable>());
    }

    private bool CheckPdfPlumberAvailability()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = "-c \"import pdfplumber\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(5000);
            var available = process?.ExitCode == 0;

            if (!available)
            {
                _logger.LogWarning("pdfplumber not available. Install with: pip install pdfplumber");
            }

            return available;
        }
        catch
        {
            return false;
        }
    }

    private class PdfPlumberTable
    {
        public int page_number { get; set; }
        public int table_index { get; set; }
        public List<string> headers { get; set; } = new();
        public List<List<string>> rows { get; set; } = new();
        public double confidence { get; set; }
    }
}
```

---

### 1.3 Integration with BertRagSummarizer

**Modify**: `src/Mostlylucid.DocSummarizer.Core/Services/BertRagSummarizer.cs`

Add table extraction BEFORE text chunking:

```csharp
public class BertRagSummarizer
{
    private readonly ITableExtractor _tableExtractor;

    public async Task<ProcessingResult> ProcessDocumentAsync(
        string documentPath,
        ProcessingOptions options,
        CancellationToken ct = default)
    {
        var result = new ProcessingResult { DocumentPath = documentPath };

        // 1. Extract text (existing)
        var text = await ExtractTextAsync(documentPath, ct);

        // 2. NEW: Extract tables
        List<DocumentTable> tables = new();
        if (options.ExtractTables && _tableExtractor.SupportsFormat(documentPath))
        {
            tables = await _tableExtractor.ExtractTablesAsync(documentPath, ct: ct);
            _logger.LogInformation("Extracted {Count} tables from {Document}",
                tables.Count, Path.GetFileName(documentPath));
        }

        // 3. Export tables to temp directory for DataSummarizer
        var tablePaths = new List<string>();
        if (tables.Any())
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "lucidrag_tables", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            foreach (var table in tables)
            {
                var csvPath = await table.SaveAsCsvAsync(tempDir, Path.GetFileNameWithoutExtension(documentPath));
                tablePaths.Add(csvPath);
            }

            result.ExtractedTables = tablePaths;
        }

        // 4. Chunk text (existing, but skip table regions if we have bounding boxes)
        var chunks = await ChunkTextAsync(text, options, ct);

        // 5. Generate embeddings for text chunks (existing)
        await GenerateEmbeddingsAsync(chunks, ct);

        result.TextChunks = chunks;
        return result;
    }
}

public class ProcessingOptions
{
    // Existing options...

    /// <summary>
    /// Whether to detect and extract tables from documents.
    /// </summary>
    public bool ExtractTables { get; set; } = true;
}

public class ProcessingResult
{
    public string DocumentPath { get; set; } = "";
    public List<Segment> TextChunks { get; set; } = new();

    /// <summary>
    /// Paths to extracted table CSV files (for DataSummarizer).
    /// </summary>
    public List<string> ExtractedTables { get; set; } = new();
}
```

---

## Phase 2: LucidRAG - Orchestration

### 2.1 Modify DocumentProcessingService

**File**: `src/LucidRAG/Services/DocumentProcessingService.cs`

```csharp
public class DocumentProcessingService
{
    private readonly BertRagSummarizer _docSummarizer;
    private readonly DataSummarizerService _dataSummarizer;
    private readonly IVectorStore _vectorStore;

    public async Task<ProcessedDocument> ProcessDocumentAsync(
        string documentPath,
        CancellationToken ct = default)
    {
        // 1. DocSummarizer: Extract text + tables
        var docResult = await _docSummarizer.ProcessDocumentAsync(documentPath, new ProcessingOptions
        {
            ExtractTables = true
        }, ct);

        var processedDoc = new ProcessedDocument
        {
            DocumentId = GenerateDocumentId(documentPath),
            Path = documentPath,
            TextSegmentCount = docResult.TextChunks.Count,
            TableCount = docResult.ExtractedTables.Count
        };

        // 2. Store text embeddings (existing)
        await StoreTextEmbeddingsAsync(processedDoc.DocumentId, docResult.TextChunks, ct);

        // 3. NEW: Profile and store tables
        if (docResult.ExtractedTables.Any())
        {
            var tableProfiles = new List<TableProfile>();

            foreach (var tablePath in docResult.ExtractedTables)
            {
                // DataSummarizer: Profile the table
                var profile = await _dataSummarizer.SummarizeAsync(
                    filePath: tablePath,
                    useLlm: true,
                    maxLlmInsights: 3
                );

                // Generate embedding from table summary
                var summaryText = $"Table: {profile.Profile.SourcePath}\n" +
                                $"Columns: {string.Join(", ", profile.Profile.Columns.Select(c => c.Name))}\n" +
                                $"Rows: {profile.Profile.RowCount}\n" +
                                $"Summary: {profile.ExecutiveSummary}";

                var embedding = await GenerateEmbeddingAsync(summaryText, ct);

                var tableProfile = new TableProfile
                {
                    TablePath = tablePath,
                    Profile = profile,
                    Embedding = embedding,
                    SourceDocument = documentPath
                };

                tableProfiles.Add(tableProfile);

                // Store in vector database with special type
                await _vectorStore.UpsertDocumentsAsync("documents", new[]
                {
                    new VectorDocument
                    {
                        Id = $"{processedDoc.DocumentId}_table_{tableProfiles.Count}",
                        ParentId = processedDoc.DocumentId,
                        Embedding = embedding,
                        Text = summaryText,
                        Metadata = new Dictionary<string, object>
                        {
                            ["type"] = "table",
                            ["source_document"] = documentPath,
                            ["table_csv_path"] = tablePath,
                            ["row_count"] = profile.Profile.RowCount,
                            ["column_count"] = profile.Profile.Columns.Count,
                            ["columns"] = string.Join(",", profile.Profile.Columns.Select(c => c.Name))
                        }
                    }
                }, ct);
            }

            processedDoc.Tables = tableProfiles;
        }

        return processedDoc;
    }
}

public class ProcessedDocument
{
    public string DocumentId { get; set; } = "";
    public string Path { get; set; } = "";
    public int TextSegmentCount { get; set; }
    public int TableCount { get; set; }
    public List<TableProfile> Tables { get; set; } = new();
}

public class TableProfile
{
    public string TablePath { get; set; } = "";
    public DataSummaryReport Profile { get; set; } = null!;
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public string SourceDocument { get; set; } = "";
}
```

---

## Phase 3: Search & Retrieval

### 3.1 Hybrid Search (Text + Tables)

**Modify**: `src/LucidRAG/Services/AgenticSearchService.cs`

```csharp
public async Task<SearchResult> SearchAsync(string query, SearchOptions options)
{
    // Generate query embedding
    var queryEmbedding = await _embedder.EmbedAsync(query);

    // Search both text and table embeddings
    var results = await _vectorStore.SearchAsync("documents", new VectorSearchQuery
    {
        QueryEmbedding = queryEmbedding,
        TopK = options.TopK,
        IncludeDocument = true
    });

    // Separate text and table results
    var textResults = results.Where(r => r.Metadata.GetValueOrDefault("type")?.ToString() != "table").ToList();
    var tableResults = results.Where(r => r.Metadata.GetValueOrDefault("type")?.ToString() == "table").ToList();

    return new SearchResult
    {
        Query = query,
        TextMatches = textResults.Select(r => new TextMatch
        {
            Text = r.Text,
            Score = r.Score,
            Source = r.Metadata.GetValueOrDefault("source_document")?.ToString()
        }).ToList(),
        TableMatches = tableResults.Select(r => new TableMatch
        {
            Summary = r.Text,
            Score = r.Score,
            CsvPath = r.Metadata.GetValueOrDefault("table_csv_path")?.ToString(),
            RowCount = Convert.ToInt32(r.Metadata.GetValueOrDefault("row_count")),
            Columns = r.Metadata.GetValueOrDefault("columns")?.ToString()?.Split(',').ToList() ?? new()
        }).ToList()
    };
}

public class SearchResult
{
    public string Query { get; set; } = "";
    public List<TextMatch> TextMatches { get; set; } = new();
    public List<TableMatch> TableMatches { get; set; } = new();
}

public class TableMatch
{
    public string Summary { get; set; } = "";
    public double Score { get; set; }
    public string? CsvPath { get; set; }
    public int RowCount { get; set; }
    public List<string> Columns { get; set; } = new();
}
```

---

## Configuration

### appsettings.json

```json
{
  "DocSummarizer": {
    "TableExtraction": {
      "Enabled": true,
      "Strategy": "PdfPlumber",
      "PythonPath": "python3",
      "MinRows": 2,
      "MinColumns": 2,
      "DetectBorderlessTables": true
    }
  },
  "DataSummarizer": {
    "ProfileTables": true,
    "UseLlmForTables": true,
    "MaxLlmInsightsPerTable": 3
  }
}
```

---

## CLI Usage

```bash
# Process document with table extraction
lucidrag process report.pdf --extract-tables

# Search that returns both text and tables
lucidrag search "quarterly revenue by region"
# Returns:
# - Text matches from document body
# - Table matches with structured data

# View table details
lucidrag table show <table-id>
# Displays:
# - Table schema
# - Statistics from DataSummarizer
# - Preview of data
```

---

## Next Steps

1. ✅ **Review architecture** - Confirm DocSummarizer → DataSummarizer flow
2. ✅ **Implement `ITableExtractor` interface**
3. ✅ **Create `PdfTableExtractor` with pdfplumber**
4. ✅ **Modify `BertRagSummarizer` to call table extraction**
5. ✅ **Update `DocumentProcessingService` to profile tables**
6. ✅ **Add table-specific vector storage**
7. ✅ **Implement hybrid search**
8. ✅ **Add UI for table results**

**Should I start implementing Phase 1 (ITableExtractor + PdfTableExtractor)?**
