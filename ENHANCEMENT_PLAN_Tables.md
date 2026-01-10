# Enhancement Plan: Table Extraction & Advanced Data Support

## Current State Analysis

### DataSummarizer ✅ Already Supports
- **CSV/TSV**: Full support with error tolerance
- **Excel (.xlsx/.xls)**: Via DuckDB spatial extension (`st_read`)
- **Parquet, JSON, Avro**: Native DuckDB support
- **Databases**: SQLite, PostgreSQL, MySQL
- **Cloud**: S3, Azure, GCS
- **Lakehouse**: Delta Lake, Iceberg
- **Log Files**: Apache, IIS (converted to Parquet)

### DocSummarizer ✅ Currently Handles
- **Text extraction**: PDF, DOCX, Markdown, HTML, TXT
- **OCR**: Tesseract for scanned documents (via ImageSummarizer)
- **Chunking**: Semantic segmentation
- **Embeddings**: Text-only vector storage

### Critical Gap ❌ Tables in Documents
**Problem**: When DocSummarizer processes a PDF/DOCX with tables, it extracts table content as raw text, losing the structure.

**Example:**
```
PDF contains:
┌────────┬────────┬────────┐
│ Name   │ Age    │ City   │
├────────┼────────┼────────┤
│ Alice  │ 30     │ NYC    │
│ Bob    │ 25     │ LA     │
└────────┴────────┴────────┘

Current output (text):
"Name Age City Alice 30 NYC Bob 25 LA"

Desired output (structured):
CSV/Parquet with proper columns → DataSummarizer profiling
```

---

## Proposed Enhancements

### Phase 1: Table Detection & Extraction (DocSummarizer)

#### 1.1 New Service: `TableExtractorService`

**Location**: `src/Mostlylucid.DocSummarizer.Core/Services/TableExtractorService.cs`

**Features:**
- Detect tables in PDF/DOCX using multiple strategies:
  - **pdfplumber** (Python library - best for PDF tables)
  - **python-docx** (for DOCX table extraction)
  - **Azure Document Intelligence** (cloud, most accurate)
  - **Fallback**: Simple heuristics (aligned text detection)

**API:**
```csharp
public interface ITableExtractor
{
    Task<List<ExtractedTable>> ExtractTablesAsync(
        string documentPath,
        TableExtractionOptions options,
        CancellationToken ct = default);
}

public class ExtractedTable
{
    public int PageNumber { get; set; }
    public int TableIndex { get; set; }
    public string[][] Cells { get; set; } // Raw cell data
    public List<string> Headers { get; set; }
    public string? Caption { get; set; }
    public Rectangle BoundingBox { get; set; }

    // Export to formats DataSummarizer understands
    public string ExportToCsv();
    public byte[] ExportToParquet();
}

public class TableExtractionOptions
{
    public TableExtractionStrategy Strategy { get; set; } = TableExtractionStrategy.Auto;
    public bool IncludeBorderlessTables { get; set; } = true;
    public int MinRows { get; set; } = 2;
    public int MinColumns { get; set; } = 2;
}

public enum TableExtractionStrategy
{
    Auto,           // Try multiple, pick best
    PdfPlumber,     // Python pdfplumber (most accurate)
    AzureDocIntel,  // Azure Document Intelligence
    Heuristic       // Pattern-based (last resort)
}
```

#### 1.2 Integration with Document Pipeline

**Modify**: `src/Mostlylucid.DocSummarizer.Core/Services/BertRagSummarizer.cs`

Add table extraction phase BEFORE text chunking:
```csharp
public async Task<SummarizationResult> SummarizeDocumentAsync(string path)
{
    // 1. Extract text (existing)
    var text = await ExtractTextAsync(path);

    // 2. NEW: Extract tables
    var tables = await _tableExtractor.ExtractTablesAsync(path);

    // 3. Process tables separately
    var tableProfiles = new List<TableProfile>();
    foreach (var table in tables)
    {
        // Export table to temp CSV
        var csvPath = ExportTableToCsv(table);

        // Profile with DataSummarizer
        var profile = await _dataSummarizer.ProfileAsync(csvPath);

        tableProfiles.Add(new TableProfile
        {
            PageNumber = table.PageNumber,
            Caption = table.Caption,
            Profile = profile,
            Embedding = await _embedder.EmbedAsync(profile.Summary)
        });
    }

    // 4. Chunk text (existing, but skip table regions)
    var chunks = ChunkText(text, excludeRegions: tables.Select(t => t.BoundingBox));

    // 5. Store both text embeddings AND table embeddings
    await StoreTextEmbeddingsAsync(chunks);
    await StoreTableEmbeddingsAsync(tableProfiles);
}
```

**Benefits:**
- ✅ Tables searchable as structured data
- ✅ Text and tables indexed separately
- ✅ RAG can retrieve table summaries
- ✅ Preserves table structure for downstream analysis

---

### Phase 2: Enhanced Excel Support (DataSummarizer)

#### 2.1 Multi-Sheet Analysis

**Current limitation**: Excel files require `--sheet` parameter to specify sheet name.

**Enhancement**: Auto-detect and analyze ALL sheets by default.

**New API**:
```csharp
public async Task<ExcelWorkbookProfile> ProfileWorkbookAsync(string excelPath)
{
    var sheets = await DetectSheetsAsync(excelPath);

    var profiles = new List<SheetProfile>();
    foreach (var sheet in sheets)
    {
        var profile = await ProfileAsync(excelPath, sheet);
        profiles.Add(profile);
    }

    // Detect relationships between sheets
    var relationships = DetectSheetRelationships(profiles);

    return new ExcelWorkbookProfile
    {
        FilePath = excelPath,
        Sheets = profiles,
        Relationships = relationships // Foreign keys, shared columns, etc.
    };
}
```

#### 2.2 Formula & Pivot Table Extraction

**Problem**: Excel formulas and pivot tables not currently analyzed.

**Solution**: Extract formulas as metadata:
```csharp
public class ColumnProfile
{
    // Existing fields...
    public List<string> Formulas { get; set; } // =SUM(A1:A10), etc.
    public bool IsPivotTable { get; set; }
    public PivotTableDefinition? PivotDefinition { get; set; }
}
```

Use **EPPlus** or **ClosedXML** libraries to read Excel internals:
```xml
<PackageReference Include="EPPlus" Version="7.0.0" />
```

---

### Phase 3: Advanced Table Understanding

#### 3.1 Relationship Detection

**Auto-detect foreign keys between tables/sheets:**
```csharp
public class TableRelationship
{
    public string SourceTable { get; set; }
    public string SourceColumn { get; set; }
    public string TargetTable { get; set; }
    public string TargetColumn { get; set; }
    public double Confidence { get; set; } // 0-1
    public RelationshipType Type { get; set; } // OneToMany, ManyToMany
}

public enum RelationshipType
{
    OneToOne,
    OneToMany,
    ManyToMany
}
```

**Detection heuristics:**
- Same column name → potential foreign key
- Value overlap > 80% → likely relationship
- Cardinality analysis → detect relationship type

#### 3.2 Schema Understanding with LLM

**Use LLM to infer semantic meaning:**
```csharp
var schemaPrompt = $@"
You are a database expert. Analyze this table schema:

Table: {tableName}
Columns:
{string.Join("\n", columns.Select(c => $"- {c.Name} ({c.Type}): {c.Sample}"))}

Provide:
1. Primary key candidate
2. Foreign key candidates
3. Likely table purpose
4. Data quality issues
";

var insights = await _llm.GenerateAsync(schemaPrompt);
```

---

## Implementation Priority

### Must-Have (Phase 1 - Q1 2025)
1. ✅ **Table extraction from PDF** using pdfplumber
   - Subprocess wrapper to Python
   - Fallback to heuristic detection
2. ✅ **Table extraction from DOCX** using python-docx
3. ✅ **Hybrid document processing** (text + tables)
4. ✅ **Separate table embeddings** in vector store

### Nice-to-Have (Phase 2 - Q2 2025)
1. **Multi-sheet Excel profiling** (auto-analyze all sheets)
2. **Formula extraction** from Excel cells
3. **Pivot table detection** and analysis
4. **Sheet relationship detection**

### Future (Phase 3 - Q3 2025)
1. **Azure Document Intelligence integration** (most accurate, cloud-based)
2. **LLM-based schema understanding**
3. **Automatic table joins** suggestion
4. **Cross-document table matching** (find related tables across files)

---

## Library Recommendations

### Table Extraction
| Library | Language | Best For | License |
|---------|----------|----------|---------|
| **pdfplumber** | Python | PDF tables | MIT |
| **python-docx** | Python | DOCX tables | MIT |
| **Tabula** | Java | PDF tables | MIT |
| **Camelot** | Python | Complex PDFs | MIT |
| **Azure Doc Intel** | Cloud API | Highest accuracy | Commercial |

### Excel Manipulation
| Library | Language | Best For | License |
|---------|----------|----------|---------|
| **EPPlus** | C# | Formula extraction | Polyform Non-commercial |
| **ClosedXML** | C# | .NET native | MIT |
| **DuckDB spatial** | SQL | Already integrated | MIT |

---

## Sample CLI Usage (After Enhancement)

```bash
# Extract tables from PDF and profile them
docsummarizer process report.pdf --extract-tables --profile-tables

# Profile Excel workbook (all sheets)
datasummarizer profile sales.xlsx --all-sheets --detect-relationships

# Hybrid analysis: text + tables
lucidrag analyze mixed-document.pdf --mode hybrid

# Search tables semantically
lucidrag search "quarterly revenue by region" --type table
```

---

## Database Schema Changes

### New Tables for Table Storage

```sql
-- Store extracted tables separately from text segments
CREATE TABLE document_tables (
    id UUID PRIMARY KEY,
    document_id UUID REFERENCES documents(id),
    page_number INT,
    table_index INT,
    caption TEXT,
    row_count INT,
    column_count INT,
    csv_export TEXT,  -- Inline CSV for small tables
    parquet_path TEXT, -- Path for large tables
    embedding FLOAT[384],
    metadata JSONB,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- Table column metadata
CREATE TABLE table_columns (
    id UUID PRIMARY KEY,
    table_id UUID REFERENCES document_tables(id),
    column_name TEXT,
    column_type TEXT,
    sample_values TEXT[],
    distinct_count INT,
    null_count INT,
    metadata JSONB
);
```

---

## Breaking Changes

**None** - All enhancements are additive:
- Default behavior unchanged (text-only extraction)
- Table extraction opt-in via `--extract-tables` flag
- Backward compatible with existing vector stores

---

## Success Metrics

After implementation, we should see:
- ✅ **95%+ table detection accuracy** on standard PDFs
- ✅ **Zero data loss** from structured tables
- ✅ **Semantic table search** working via RAG
- ✅ **Multi-sheet Excel** profiled automatically
- ✅ **Table relationships** detected with >80% accuracy

---

## Next Steps

1. **Review this plan** - Confirm priorities and scope
2. **Select table extraction library** - pdfplumber vs Azure Doc Intel
3. **Create `TableExtractorService`** - Start with PDF support
4. **Extend DocSummarizer pipeline** - Add table detection phase
5. **Update vector store schema** - Support table embeddings
6. **CLI integration** - Add `--extract-tables` flag
7. **Testing** - Sample PDFs with various table formats

---

**Should we proceed with Phase 1 (Table Extraction)?**
