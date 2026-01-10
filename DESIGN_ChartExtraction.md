# Chart Extraction Pipeline Design

**Status**: Design Phase (Not Implemented)
**Complexity**: Hard Problem - Requires Vision Models + Data Extraction
**Priority**: Phase 3 (After Table Extraction)

---

## Problem Statement

Extract structured data from charts/graphs embedded in documents (PDF, DOCX, PowerPoint, images) and convert to tabular format for DataSummarizer profiling.

### Why This Is Hard

1. **Chart Type Diversity**: Bar, line, pie, scatter, box plots, heatmaps, radar charts, etc.
2. **Axis Label OCR**: Reading tick labels, axis titles, legends
3. **Data Point Detection**: Extracting precise values from visual representations
4. **Scale Reconstruction**: Inferring scales from partial axis labels
5. **Multi-Series Charts**: Handling multiple data series with different colors/styles
6. **Image Quality**: Charts may be low-resolution, screenshots, or photos
7. **Occlusion**: Labels, gridlines, annotations overlapping with data

---

## Proposed Architecture

### 1. Two-Stage Pipeline

```
┌────────────────────────────────────────────────────────┐
│              Stage 1: Chart Detection                  │
│                                                        │
│  Input: Document (PDF/DOCX/Image)                     │
│    ↓                                                   │
│  Vision Model: Detect chart regions + classify type   │
│    ↓                                                   │
│  Output: ChartRegion[] (bbox, type, confidence)       │
└────────────────────────────────────────────────────────┘
                           ↓
┌────────────────────────────────────────────────────────┐
│              Stage 2: Data Extraction                  │
│                                                        │
│  For each ChartRegion:                                 │
│    ↓                                                   │
│  1. Extract chart image (crop to bbox)                │
│  2. Classify chart type (if not already known)        │
│  3. Route to type-specific extractor                  │
│  4. Extract data using appropriate strategy            │
│  5. Export as CSV/JSON                                 │
└────────────────────────────────────────────────────────┘
```

### 2. Component Breakdown

#### A. Chart Detection Service

**Interface**: `IChartDetector`

```csharp
public interface IChartDetector
{
    Task<List<ChartRegion>> DetectChartsAsync(string filePath, CancellationToken ct = default);
}

public class ChartRegion
{
    public required string Id { get; init; }
    public required string SourcePath { get; init; }
    public int PageNumber { get; init; }
    public float[] BoundingBox { get; init; }  // [x0, y0, x1, y1]
    public ChartType Type { get; init; }
    public double Confidence { get; init; }
    public byte[] ImageData { get; init; }  // Cropped chart image
}

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
```

**Implementation Options**:

1. **Vision LLM Approach** (GPT-4V, Claude Vision):
   - Send document pages as images
   - Prompt: "Detect all charts/graphs and their types"
   - High accuracy, expensive, requires API keys

2. **YOLO-based Object Detection**:
   - Train custom YOLO model on chart detection dataset
   - Examples: ChartOCR dataset, FigureQA, DVQA
   - Fast, runs locally, requires training

3. **Hybrid**: Use layout analysis first (PdfPig/pdfplumber) to find figure regions, then classify

#### B. Chart Data Extraction Service

**Interface**: `IChartDataExtractor`

```csharp
public interface IChartDataExtractor
{
    ChartType SupportedType { get; }
    Task<ExtractedChartData> ExtractAsync(ChartRegion chart, ChartExtractionOptions options, CancellationToken ct = default);
}

public class ExtractedChartData
{
    public required string ChartId { get; init; }
    public ChartType Type { get; init; }
    public required string[] ColumnNames { get; init; }
    public required List<Dictionary<string, object>> Data { get; init; }
    public Dictionary<string, string>? AxisLabels { get; init; }
    public string? Title { get; init; }
    public string? Legend { get; init; }
    public double Confidence { get; init; }

    // Convert to CSV
    public string ToCsv() { /* ... */ }
}
```

**Type-Specific Extractors**:

1. **BarChartExtractor**
2. **LineChartExtractor**
3. **PieChartExtractor**
4. **ScatterPlotExtractor**
5. **GenericChartExtractor** (fallback using Vision LLM)

#### C. Vision-Based Extraction (Fallback)

For complex or unknown chart types, use multimodal LLM:

```csharp
public class VisionLLMChartExtractor : IChartDataExtractor
{
    public ChartType SupportedType => ChartType.Unknown;

    public async Task<ExtractedChartData> ExtractAsync(ChartRegion chart, ...)
    {
        var prompt = @"
You are a chart data extraction expert. Analyze this chart image and extract the data in CSV format.

Requirements:
1. Identify chart type
2. Extract all data points with accurate values
3. Include axis labels and legend
4. Output as CSV with column headers

Chart Image: [attached]
";

        var response = await _visionClient.AnalyzeImageAsync(chart.ImageData, prompt);
        return ParseLLMResponse(response);
    }
}
```

---

## Implementation Approaches

### Approach 1: Vision LLM Only (Simplest, Most Expensive)

**Pros**:
- Works for all chart types
- No training required
- Handles complex/unusual charts
- Can extract text from images

**Cons**:
- Requires API keys (GPT-4V, Claude)
- Expensive at scale ($0.01-0.10 per chart)
- Network dependency
- Rate limits

**Best For**: Low-volume, high-accuracy needs

### Approach 2: Specialized Libraries (Medium Complexity)

**Tools**:
- **ChartOCR**: Research model for chart data extraction
- **PlotDigitizer**: Open-source digitization tool
- **DeepRule** / **ChartSense**: Academic research implementations

**Pros**:
- No API costs
- Runs locally
- Good accuracy on standard charts

**Cons**:
- Limited chart type support
- Complex to integrate (Python dependencies)
- May require fine-tuning
- Poor on low-quality images

**Best For**: Production use with standard chart types

### Approach 3: Hybrid (Recommended)

1. **Fast Path** (80% of cases):
   - Detect chart type with YOLO/object detection
   - If standard type (bar/line/pie), use specialized extractor
   - OCR for axis labels (Tesseract/EasyOCR)
   - Rule-based data point detection

2. **Slow Path** (20% of cases):
   - Fall back to Vision LLM for:
     - Unknown chart types
     - Complex multi-series charts
     - Poor image quality
     - Extraction failures

**Pros**:
- Cost-effective (Vision LLM only for hard cases)
- Fast for common charts
- High accuracy overall

**Cons**:
- More complex implementation
- Need to manage fallback logic

---

## Technology Stack

### Chart Detection
- **YOLO v8** (PyTorch) - Object detection
- **LayoutLMv3** - Document layout understanding
- **OpenCV** - Image preprocessing
- **PyMuPDF** - PDF rendering to images

### Data Extraction
- **EasyOCR** / **Tesseract** - Text extraction
- **OpenCV** - Line/shape detection
- **scikit-image** - Image processing
- **GPT-4V** / **Claude Vision** - Fallback extraction

### Workflow Orchestration
- **Python subprocess** (like table extraction)
- Or: **gRPC service** for long-running chart extraction server

---

## Data Flow

```
Document (PDF/DOCX)
  ↓
[Chart Detection]
  ↓
ChartRegion[] (images + metadata)
  ↓
[Type-Specific Extraction]
  ├─ BarChartExtractor → CSV
  ├─ LineChartExtractor → CSV
  ├─ PieChartExtractor → CSV
  └─ VisionLLMExtractor (fallback) → CSV
  ↓
ExtractedChartData[]
  ↓
[Store as Evidence Artifacts]
  ├─ CSV data → evidence_artifacts (type: chart_data)
  ├─ Chart image → evidence_artifacts (type: chart_image)
  └─ Metadata → retrieval_entities
  ↓
[Generate Embeddings]
  ├─ Embed chart title + axis labels
  ├─ Embed data as text (for semantic search)
  └─ Store in vector DB linked to parent document
  ↓
[DataSummarizer Profiling]
  └─ Profile extracted CSV like any table
```

---

## Example: Bar Chart Extraction

**Input**: Bar chart image (sales by region)

**Processing**:
1. Detect chart type: `BarChart`
2. OCR axis labels:
   - X-axis: ["North", "South", "East", "West"]
   - Y-axis: ["0", "50K", "100K", "150K", "200K"]
3. Detect bars:
   - North: height = 145px → 145K (interpolate from y-scale)
   - South: height = 112px → 112K
   - East: height = 178px → 178K
   - West: height = 245px → 245K
4. Output CSV:
   ```csv
   Region,Sales
   North,145000
   South,112000
   East,178000
   West,245000
   ```

---

## Challenges & Mitigations

| Challenge | Mitigation |
|-----------|-----------|
| **Poor OCR accuracy** | Use Vision LLM fallback |
| **Non-standard scales** | Confidence scoring + manual review flag |
| **Overlapping labels** | Image preprocessing (contrast, denoising) |
| **3D charts** | Vision LLM only (too complex for CV) |
| **Multiple series** | Color-based clustering + legend matching |
| **Rotated text** | OCR with rotation detection |
| **Low resolution** | Super-resolution preprocessing (ESRGAN) |
| **Partial charts** | Flag as low-confidence, extract what's visible |

---

## Future Enhancements

### Phase 1: Basic Implementation
- Chart detection (YOLO)
- Bar/Line/Pie extractors
- Vision LLM fallback

### Phase 2: Enhanced Accuracy
- Fine-tune YOLO on domain-specific charts
- Add more chart types (scatter, heatmap)
- Improve OCR with EasyOCR

### Phase 3: Advanced Features
- Multi-page chart detection (charts split across pages)
- Chart reconstruction from partial views
- Interactive chart exploration in UI

---

## Integration with LucidRAG

```csharp
// In DocumentProcessingService
public class DocumentProcessingService
{
    private readonly ITableExtractor _tableExtractor;
    private readonly IChartDetector _chartDetector;
    private readonly IChartExtractionService _chartExtractor;
    private readonly IEvidenceRepository _evidenceRepo;

    public async Task ProcessDocumentAsync(string filePath)
    {
        // 1. Extract text (existing)
        var text = await ExtractTextAsync(filePath);

        // 2. Extract tables (new)
        var tables = await _tableExtractor.ExtractTablesAsync(filePath);
        foreach (var table in tables.Tables)
        {
            await StoreTableAsEvidenceAsync(table);
        }

        // 3. Extract charts (future)
        var charts = await _chartDetector.DetectChartsAsync(filePath);
        foreach (var chart in charts)
        {
            var data = await _chartExtractor.ExtractAsync(chart);
            await StoreChartAsEvidenceAsync(data);
        }

        // 4. Generate embeddings for all entities
        await GenerateEmbeddingsAsync();
    }
}
```

---

## Estimated Complexity

| Component | Effort | Complexity |
|-----------|--------|------------|
| Chart Detection (YOLO) | 2-3 weeks | Medium |
| BarChartExtractor | 1 week | Medium |
| LineChartExtractor | 1 week | Medium |
| PieChartExtractor | 3 days | Low |
| Vision LLM Fallback | 1 week | Low-Medium |
| Integration with LucidRAG | 1 week | Medium |
| **Total** | **6-8 weeks** | **Medium-High** |

---

## Recommendation

**Start Simple**:
1. Implement Vision LLM-only approach first (1 week)
2. Validate on real documents
3. Measure cost vs. accuracy
4. If cost is acceptable → ship
5. If cost is too high → add specialized extractors for common types

**Defer to Phase 3** (after table extraction is stable and tested in production)

---

## References

- **ChartOCR**: https://arxiv.org/abs/2010.02179
- **DeepRule**: https://arxiv.org/abs/2104.06403
- **PlotDigitizer**: https://plotdigitizer.com/
- **GPT-4V Chart Extraction**: https://platform.openai.com/docs/guides/vision
- **FigureQA Dataset**: https://arxiv.org/abs/1710.07300
- **DVQA Dataset**: https://arxiv.org/abs/1801.08163
