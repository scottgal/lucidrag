# LucidRAG Image CLI - Demo Guide

## Quick Start Demo

Run the automated demo script:
```powershell
.\demo\image-cli-demo.ps1
```

## Manual Demo Steps

### 1. Console Pixel Art Preview

Preview any image directly in the terminal:

```bash
# Full color preview
lucidrag-image preview photo.jpg --mode ColorBlocks --width 80 --height 40

# Compact single-line preview
lucidrag-image preview photo.jpg --compact --width 40

# Dominant color bar
lucidrag-image preview photo.jpg --color-bar --width 60

# Bordered panel
lucidrag-image preview photo.jpg --panel

# High-resolution Braille mode
lucidrag-image preview photo.jpg --mode Braille --width 120 --height 60
```

**Rendering Modes:**
- `ColorBlocks`: Best quality, full RGB color (default)
- `GrayscaleBlocks`: Grayscale intensity blocks
- `Ascii`: Classic ASCII art (`.:-=+*#%@`)
- `Braille`: Highest resolution (2x4 pixels per character)

### 2. Forensic Analysis

Analyze a single image with complete forensics:

```bash
# Basic analysis
lucidrag-image analyze photo.jpg --format table

# With OCR (extracts text with coordinates)
lucidrag-image analyze screenshot.png --include-ocr --format markdown

# With vision LLM verification
lucidrag-image analyze photo.jpg --use-llm --format json

# Generate thumbnail
lucidrag-image analyze photo.jpg --thumbnail thumb.jpg
```

**What Gets Analyzed:**
- ✅ **EXIF Forensics**: Camera info, GPS, timestamps, tampering detection
- ✅ **Digital Fingerprinting**: PDQ hash, color histogram, block mean hash
- ✅ **Error Level Analysis (ELA)**: JPEG manipulation detection
- ✅ **OCR**: Text extraction with bounding boxes (Tesseract)
- ✅ **OCR Verification**: Vision LLM concordance checking (Ollama)
- ✅ **Color Analysis**: Dominant colors, saturation, color grid
- ✅ **Quality Metrics**: Sharpness, blur, edge density, entropy

### 3. Natural Language Queries

Search images using plain English:

```bash
# Find sunset photos
lucidrag-image batch ~/Pictures --query "sunset images with the sea"

# Find abstract art
lucidrag-image batch ~/Downloads --query "green abstract images"

# Find screenshots with text
lucidrag-image batch ~/Desktop --query "screenshots with text in them"

# High-res photos
lucidrag-image batch ~/Photos --query "high resolution photos with people"

# Specific colors
lucidrag-image batch ~/Images --query "red and blue images"
```

**How It Works:**
1. **TinyLlama** decomposes natural language into structured criteria:
   - Keywords: `["sunset", "sea"]`
   - Colors: `["orange", "blue"]`
   - Image type: `"Photo"`
   - Quality: `"high resolution"`

2. **Parallel Matching**: Each criterion is matched against image signals
3. **RRF Recombination**: Reciprocal Rank Fusion combines scores
4. **Results**: Images ranked by relevance with match scores

### 4. Batch Processing

Process folders with glob patterns and filters:

```bash
# Process all JPGs
lucidrag-image batch ~/Pictures --pattern "**/*.jpg" --format table

# Filter by type
lucidrag-image batch ~/Downloads --filter-type Screenshot --max-parallel 8

# Filter by text content
lucidrag-image batch ~/Documents --min-text-score 0.6 --include-ocr

# Export to CSV
lucidrag-image batch ~/Photos --export-csv catalog.csv

# Recursive with limit
lucidrag-image batch ~/Images --pattern "**/*.{jpg,png}" --max-parallel 4
```

**Progress Display:**
```
Worker 1 [████████████████----] 80% (12/15)
Worker 2 [████████████████████] 100% (15/15)
Worker 3 [████████████--------] 60% (9/15)
Worker 4 [██████████----------] 50% (7/15)
```

### 5. Image Deduplication

Find duplicates using perceptual hashing:

```bash
# Report duplicates (dry run)
lucidrag-image dedupe ~/Downloads --threshold 5 --action report

# Move duplicates to folder
lucidrag-image dedupe ~/Photos --threshold 3 --action move --dry-run

# Delete duplicates (with confirmation)
lucidrag-image dedupe ~/Pictures --threshold 5 --action delete
```

**Threshold Guide:**
- `0`: Exact match only (same hash)
- `1-5`: Nearly identical (minor compression, resize)
- `6-10`: Very similar (cropped, color-adjusted)
- `11-20`: Similar composition (rotated, edited)
- `>20`: Different images

**Output:**
```
Group 1 (3 images, threshold: 5)
  ✓ photo1.jpg (2.1 MB) [KEEP - largest]
  → photo1_copy.jpg (1.8 MB) [distance: 2]
  → photo1_resized.jpg (842 KB) [distance: 4]

Group 2 (2 images, threshold: 5)
  ✓ sunset.png (3.5 MB) [KEEP - largest]
  → sunset_edited.png (3.2 MB) [distance: 3]
```

## Natural Language Query Examples

### Semantic Searches

```bash
# Nature scenes
"sunset over mountains"
"beach with palm trees"
"forest in autumn"

# Urban scenes
"city skyline at night"
"street photography with rain"
"modern architecture"

# People
"group photos with smiling people"
"portrait with blurred background"
"children playing outdoors"
```

### Attribute-Based Searches

```bash
# By color
"images with dominant blue color"
"green and yellow images"
"black and white photos"

# By type
"abstract art"
"technical diagrams"
"flowcharts and schemas"
"logos and icons"

# By content
"images with text overlay"
"screenshots of code"
"graphs and charts with data"
```

### Quality Filters

```bash
# Resolution
"high resolution landscape photos"
"low quality images that need replacing"

# Sharpness
"sharp macro photography"
"blurry images to delete"

# Artistic
"images with high saturation"
"minimalist compositions"
```

### Combined Criteria

```bash
# Complex queries
"high resolution sunset photos with warm colors"
"abstract art with green and blue, no text"
"sharp portraits with people, natural lighting"
"technical diagrams in PNG format with text"
```

## Advanced Features

### Console Preview in Workflows

Use pixel art previews for conversational filtering:

```bash
# Show preview before analysis
lucidrag-image preview image.jpg --panel
lucidrag-image analyze image.jpg --format table

# Quick thumbnail review
for f in *.jpg; do
    lucidrag-image preview "$f" --compact
    read -p "Analyze? (y/n): " answer
    [ "$answer" = "y" ] && lucidrag-image analyze "$f"
done
```

### OCR with Coordinate Tracking

Extract text with precise locations (EasyOCR-compatible format):

```json
{
  "text": "Hello World",
  "confidence": 0.92,
  "boundingBox": {
    "x1": 120,
    "y1": 45,
    "x2": 280,
    "y2": 78,
    "width": 160,
    "height": 33,
    "centerX": 200,
    "centerY": 61
  }
}
```

### Vision LLM Verification

OCR results automatically verified by MiniCPM-V:

```
OCR (Tesseract):        "Helo World" (confidence: 0.65)
Vision LLM (MiniCPM-V): "Hello World" (confidence: 0.95)
Concordance:            0.88 (high agreement)
Suggested Text:         "Hello World" (from Vision LLM)
```

### SQLite Storage & Feedback Loop

Signals are persisted for learning:

```bash
# Analyze and store
lucidrag-image analyze photo.jpg --store-signals

# Load previous analysis
lucidrag-image analyze photo.jpg --load-cached

# Submit feedback
lucidrag-image feedback photo.jpg --correct-type "Diagram" --notes "Misclassified as Photo"
```

## Output Formats

### Table (Default)

```
┌─────────────────────────────────────────────────┐
│ Image Analysis: photo.jpg                       │
├─────────────────┬───────────────────────────────┤
│ Type            │ Photo                         │
│ Dimensions      │ 1920x1080 (16:9)              │
│ Sharpness       │ High (0.87)                   │
│ Colors          │ Blue, Orange, White           │
│ Text Detected   │ No                            │
│ Fingerprint     │ A3F7C2...                     │
└─────────────────┴───────────────────────────────┘
```

### JSON

```json
{
  "imagePath": "photo.jpg",
  "signals": [
    {
      "key": "content.type",
      "value": "Photo",
      "confidence": 0.92,
      "source": "TypeDetectionWave"
    }
  ],
  "statistics": {
    "totalSignals": 47,
    "averageConfidence": 0.89,
    "waveCount": 8
  }
}
```

### Markdown

```markdown
# Image: photo.jpg

**Type:** Photo (confidence: 0.92)
**Dimensions:** 1920 x 1080 (Full HD)

## Visual Properties
- Sharpness: High (0.87)
- Edge Density: 0.42
- Dominant Colors: Blue (45%), Orange (32%), White (18%)

## Forensic Analysis
- Digital Fingerprint: A3F7C2E8...
- EXIF Data: Canon EOS 5D Mark IV
- Tampering Detected: No
```

## Troubleshooting

### Ollama Not Available

If Ollama is not running, vision LLM features are gracefully disabled:

```
⚠ Ollama not available at http://localhost:11434
  Vision LLM features disabled
  Falling back to Tesseract OCR only
```

Start Ollama:
```bash
ollama serve
ollama pull minicpm-v:8b
ollama pull tinyllama
```

### Tesseract Data Not Found

If Tesseract language data is missing:

```
✗ Tesseract data not found in ./tessdata
  Download from: https://github.com/tesseract-ocr/tessdata
  Extract to: ./tessdata/eng.traineddata
```

### Performance Tuning

Adjust parallel workers based on CPU:

```bash
# 4 cores
lucidrag-image batch ~/Photos --max-parallel 4

# 8 cores
lucidrag-image batch ~/Photos --max-parallel 8

# Memory-constrained
lucidrag-image batch ~/Photos --max-parallel 2
```

## Next Steps

1. **Explore the codebase**: See `SOLID_REVIEW_AND_TESTS.md`
2. **Run unit tests**: `dotnet test` (151/152 passing)
3. **Extend with custom waves**: Implement `IAnalysisWave`
4. **Add new OCR engines**: Implement `IOcrEngine`
5. **Custom vision models**: Implement `IVisionLlmClient`

Happy analyzing! 🎨🔍
