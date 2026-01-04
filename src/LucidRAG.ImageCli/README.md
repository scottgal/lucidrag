# LucidRAG Image CLI

Command-line interface for intelligent image analysis with vision LLM integration, caching, and batch processing.

## Installation
 
### Install from NuGet (when published)

```bash
dotnet tool install -g LucidRAG.ImageCli
```

### Build from Source

```bash
cd E:\source\lucidrag
dotnet pack src/LucidRAG.ImageCli/LucidRAG.ImageCli.csproj
dotnet tool install -g LucidRAG.ImageCli --add-source ./nupkg
```

### Verify Installation

```bash
lucidrag-image --version
lucidrag-image --help
```

## Quick Start

```bash
# Analyze single image
lucidrag-image analyze photo.jpg

# Batch process directory
lucidrag-image batch ./photos --max-parallel 8

# Find duplicates
lucidrag-image dedupe ./downloads --threshold 5
```

## Commands

### `analyze` - Single Image Analysis

Analyze a single image and display results in table format.

```bash
lucidrag-image analyze <image-path> [options]
```

**Options:**
- `--format <table|json|markdown>` - Output format (default: table)
- `--include-ocr` - Run OCR if text detected
- `--include-clip` - Generate CLIP embeddings
- `--bypass-escalation` - Skip vision LLM escalation

**Examples:**

```bash
# Basic analysis (table output)
lucidrag-image analyze photo.jpg

# JSON output for piping
lucidrag-image analyze screenshot.png --format json > output.json

# With OCR for screenshots
lucidrag-image analyze document.png --include-ocr

# Skip LLM escalation (faster)
lucidrag-image analyze icon.png --bypass-escalation
```

**Output Example:**

```
╔════════════════════════╦═══════════════════════════════════════════════╗
║ Property               ║ Value                                         ║
╠════════════════════════╬═══════════════════════════════════════════════╣
║ File                   ║ photo.jpg                                     ║
║ Type                   ║ Photo (87% confidence)                        ║
║ Dimensions             ║ 1920x1080 (16:9)                              ║
║ Sharpness              ║ 1248.5 (Sharp)                                ║
║ Text Likeliness        ║ 12% (Low)                                     ║
║ Dominant Colors        ║ Navy (45%), White (28%), Gray (15%)           ║
║ LLM Caption            ║ A serene landscape with mountains...          ║
║ Was Escalated          ║ No                                            ║
║ From Cache             ║ No                                            ║
╚════════════════════════╩═══════════════════════════════════════════════╝
```

---

### `batch` - Batch Processing

Process multiple images in a directory with parallel execution.

```bash
lucidrag-image batch <directory> [options]
```

**Options:**
- `--pattern <glob>` - File pattern (default: `**/*`)
- `--max-parallel <N>` - Worker threads (default: CPU count)
- `--filter-type <type>` - Filter by image type (Photo, Screenshot, etc.)
- `--min-text-score <0.0-1.0>` - Minimum text likeliness
- `--order-by <property>` - Sort results (see below)
- `--descending` - Sort descending
- `--export-jsonld <file>` - Export fingerprints as JSON-LD
- `--export-csv <file>` - Export results as CSV
- `--bypass-escalation` - Skip vision LLM (faster)

**Sorting Options:**
- `color` - By dominant color hue
- `resolution` - By pixel count
- `sharpness` - By Laplacian variance
- `brightness` - By mean luminance
- `saturation` - By color intensity
- `type` - By detected type
- `text-score` - By text likeliness

**Examples:**

```bash
# Process all JPEGs in directory
lucidrag-image batch ./photos --pattern "**/*.jpg" --max-parallel 8

# Filter screenshots only
lucidrag-image batch ./images --filter-type Screenshot

# Find text-heavy images
lucidrag-image batch ./mixed --min-text-score 0.5

# Sort by sharpness (best first)
lucidrag-image batch ./photos --order-by sharpness --descending

# Export fingerprints for inspection
lucidrag-image batch ./images --export-jsonld fingerprints.jsonld

# Fast batch (no LLM)
lucidrag-image batch ./icons --bypass-escalation --max-parallel 16
```

**Output Example:**

```
Processing 555 images with 8 workers...

Worker 1 ████████████████████████████████████████ 100% (69 images)
Worker 2 ████████████████████████████████████████ 100% (70 images)
Worker 3 ████████████████████████████████████████ 100% (69 images)
Worker 4 ████████████████████████████████████████ 100% (70 images)
Worker 5 ████████████████████████████████████████ 100% (69 images)
Worker 6 ████████████████████████████████████████ 100% (69 images)
Worker 7 ████████████████████████████████████████ 100% (70 images)
Worker 8 ████████████████████████████████████████ 100% (69 images)

✓ Processed: 555/555 images
✓ Cache hits: 312 (56%)
✓ Escalated: 92 (17%)
✓ Errors: 0

Completed in 2m 34s (3.6 images/sec)
```

---

### `dedupe` - Find Duplicates

Find duplicate or similar images using perceptual hashing.

```bash
lucidrag-image dedupe <directory> [options]
```

**Options:**
- `--threshold <N>` - Hamming distance threshold (default: 5)
  - 0-2: Identical or near-identical
  - 3-5: Very similar
  - 6-10: Somewhat similar
  - 11+: Different
- `--action <report|move|delete>` - What to do with duplicates
- `--target-dir <path>` - Move duplicates here (when action=move)
- `--dry-run` - Show what would happen without doing it

**Examples:**

```bash
# Find duplicates (report only)
lucidrag-image dedupe ./downloads --threshold 5

# Move duplicates to folder
lucidrag-image dedupe ./photos --threshold 3 --action move --target-dir ./duplicates

# Delete duplicates (careful!)
lucidrag-image dedupe ./temp --threshold 2 --action delete

# Preview before deleting
lucidrag-image dedupe ./temp --threshold 2 --action delete --dry-run
```

**Output Example:**

```
Scanning 1,234 images for duplicates...

Found 48 duplicate groups (128 duplicate files, 96 unique):

Group 1 (5 images, keep photo_001.jpg):
  ✓ photo_001.jpg (1920x1080, 2.3 MB) [KEEP - largest]
  ~ photo_001_copy.jpg (1920x1080, 2.3 MB) [hash distance: 0]
  ~ IMG_5234.jpg (1920x1080, 2.2 MB) [hash distance: 1]
  ~ resized.jpg (1280x720, 890 KB) [hash distance: 4]
  ~ thumbnail.jpg (800x600, 320 KB) [hash distance: 5]

Group 2 (3 images, keep landscape_01.jpg):
  ✓ landscape_01.jpg (3840x2160, 4.1 MB) [KEEP - largest]
  ~ landscape_copy.jpg (3840x2160, 4.1 MB) [hash distance: 0]
  ~ landscape_small.jpg (1920x1080, 1.8 MB) [hash distance: 3]

Space saved: 15.2 MB
```

---

## Configuration

### Global Configuration File

Create `~/.lucidrag/appsettings.json`:

```json
{
  "Escalation": {
    "AutoEscalateEnabled": true,
    "ConfidenceThreshold": 0.7,
    "TextLikelinessThreshold": 0.4,
    "BlurThreshold": 300,
    "EnableCaching": true
  },
  "VisionLlm": {
    "BaseUrl": "http://localhost:11434",
    "Model": "minicpm-v:8b",
    "Temperature": 0.3,
    "Timeout": 120
  },
  "Cache": {
    "DatabasePath": "~/.lucidrag/image-cache.db"
  }
}
```

### Per-Project Configuration

Place `appsettings.json` in the current directory to override global settings.

---

## Performance Tips

### Batch Processing Optimization

```bash
# Use all CPU cores for large batches
lucidrag-image batch ./photos --max-parallel $(nproc)

# Skip LLM for fast analysis (10x faster)
lucidrag-image batch ./photos --bypass-escalation

# Combine both for maximum speed
lucidrag-image batch ./photos --bypass-escalation --max-parallel 16
```

### Cache Benefits

- **First run**: Analyzes all images, stores in cache
- **Second run**: 231x faster (3.2ms vs 740ms per image)
- **Cache location**: `~/.lucidrag/image-cache.db`
- **Cache is content-based**: Renaming files doesn't invalidate cache

### Ollama Performance

Vision LLM analysis is the slowest part (~4.2s per image):

```bash
# Check Ollama is running
ollama list

# Warm up model before batch
ollama run minicpm-v:8b "test" < test.jpg

# Use faster model for large batches
# Edit appsettings.json: "Model": "llava:7b"
```

---

## Documentation

For comprehensive documentation on the underlying library and analysis process:

- **[Core Library README](../Mostlylucid.DocSummarizer.Images/README.md)** - Analysis pipeline, process stages, vision LLM integration
- **[ANALYZERS.md](../Mostlylucid.DocSummarizer.Images/ANALYZERS.md)** - Detailed analyzer algorithms and metrics
  - ColorAnalyzer (quantization, color grids, Lanczos3)
  - EdgeAnalyzer (Sobel operators, entropy)
  - BlurAnalyzer (Laplacian variance, sharpness categories)
  - TextLikelinessAnalyzer (heuristic scoring)
  - TypeDetector (decision tree rules)
- **[ARCHITECTURE_VISION.md](../../ARCHITECTURE_VISION.md)** - Future signal-based pipeline architecture
- **[GLOBAL_TOOL_SETUP.md](./GLOBAL_TOOL_SETUP.md)** - Publishing as .NET Global Tool

---

## Troubleshooting

### "Ollama not available"

```bash
# Check Ollama is running
ollama list

# If not installed:
winget install Ollama.Ollama  # Windows
brew install ollama            # macOS

# Pull vision model
ollama pull minicpm-v:8b
```

### "Cache database locked"

The cache uses SQLite with WAL mode for concurrency. If you see lock errors:

```bash
# Stop all lucidrag-image processes
taskkill /IM lucidrag-image.exe /F

# Clean up lock files
rm ~/.lucidrag/image-cache.db-wal
rm ~/.lucidrag/image-cache.db-shm
```

### Performance Issues

```bash
# Check worker thread count (should match CPU cores)
lucidrag-image batch ./photos --max-parallel 8

# Disable vision LLM if not needed
lucidrag-image batch ./photos --bypass-escalation

# Clear cache if corrupted
rm ~/.lucidrag/image-cache.db
```

---

## Examples

### Find All Screenshots

```bash
lucidrag-image batch ./images --filter-type Screenshot --export-csv screenshots.csv
```

### Extract Text from Documents

```bash
lucidrag-image batch ./scans --min-text-score 0.6 --include-ocr > text_content.txt
```

### Organize Photos by Color

```bash
lucidrag-image batch ./photos --order-by color --export-jsonld color_sorted.jsonld
```

### Quality Control (Filter Blurry Photos)

```bash
lucidrag-image batch ./photos --order-by sharpness | grep "Blurry"
```

### Generate Image Catalog

```bash
lucidrag-image batch ./collection --export-csv catalog.csv --export-jsonld fingerprints.jsonld
```

---

## License

MIT License - See [LICENSE](../../LICENSE) file for details

---

## Support

- **GitHub Issues**: https://github.com/scottgal/LucidRAG/issues
- **Core Library Docs**: [Mostlylucid.DocSummarizer.Images](../Mostlylucid.DocSummarizer.Images/README.md)
- **LucidRAG Project**: https://github.com/scottgal/LucidRAG

---

*Part of the LucidRAG project - Multi-document Agentic RAG with GraphRAG capabilities*
