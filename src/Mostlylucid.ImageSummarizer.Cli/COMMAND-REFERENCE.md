# ImageSummarizer - Complete Command Reference

## Command Syntax

```bash
imagesummarizer <image> [options]
imagesummarizer list-pipelines
imagesummarizer  # Interactive mode
```

## Arguments

### `<image>` (required)
Path to image file.

**Supported formats**: All ImageSharp formats - JPEG, PNG, GIF, BMP, TIFF, TGA, WebP, PBM, PGM, PPM, PFM

**Examples**:
```bash
imagesummarizer screenshot.png
imagesummarizer F:/Gifs/meme.gif
imagesummarizer "path with spaces/image.jpg"
```

## Options

### `--pipeline <name>`
**Default**: `advancedocr`

Select OCR processing pipeline.

**Available pipelines**:
- `simpleocr` - Baseline Tesseract OCR (~1s)
- `advancedocr` - Multi-frame temporal processing (~2-3s) [DEFAULT]
- `quality` - Maximum accuracy mode (~10-15s)
- `alttext` - Optimized for accessibility descriptions (~3.5s)

**Examples**:
```bash
imagesummarizer image.gif --pipeline simpleocr      # Fast
imagesummarizer image.gif --pipeline advancedocr    # Balanced (default)
imagesummarizer image.gif --pipeline quality        # Best quality
```

### `--output <format>`
**Default**: `text`

Output format for results.

**Available formats**:
- `text` - Plain text only (for piping)
- `json` - Structured JSON with metadata
- `signals` - All signal emissions (debugging)
- `metrics` - Performance and quality metrics

**Examples**:
```bash
imagesummarizer image.gif --output text     # Plain text (default)
imagesummarizer image.gif --output json     # JSON for scripting
imagesummarizer image.gif --output signals  # All signals for debugging
imagesummarizer image.gif --output metrics  # Quality metrics only
```

### `--language <code>`
**Default**: `en_US`

Language for spell-checking.

**Available languages**:
- `en_US` - English (US) [DEFAULT]
- `en_GB` - English (UK)
- `es_ES` - Spanish (Spain)
- `fr_FR` - French
- `de_DE` - German
- `it_IT` - Italian
- `pt_BR` - Portuguese (Brazil)
- `ru_RU` - Russian
- `zh_CN` - Chinese (Simplified)
- `ja_JP` - Japanese

**Examples**:
```bash
imagesummarizer image.gif --language es_ES  # Spanish spell-check
imagesummarizer image.gif --language fr_FR  # French spell-check
```

### `--verbose`
**Default**: `false`

Enable detailed logging.

**Examples**:
```bash
imagesummarizer image.gif --verbose         # Show debug output
```

### `--mcp`
**Default**: `false`

Run as MCP (Model Context Protocol) server for Claude Desktop integration.

**Usage**:
```bash
imagesummarizer --mcp                       # Start MCP server mode
```

**Claude Desktop Configuration**:
Add to `claude_desktop_config.json`:
```json
{
  "mcpServers": {
    "image-ocr": {
      "command": "imagesummarizer",
      "args": ["--mcp"],
      "env": {
        "OCR_PIPELINE": "advancedocr",
        "OCR_LANGUAGE": "en_US"
      }
    }
  }
}
```

**Available MCP Tools**:
- `extract_text_from_image` - Extract text with configurable pipeline
- `analyze_image_quality` - Fast quality metrics without full OCR
- `list_ocr_pipelines` - List available pipelines with details
- `batch_extract_text` - Process multiple images in a directory

## Commands

### `list-pipelines`
List all available OCR pipelines with details.

**Example**:
```bash
imagesummarizer list-pipelines
```

**Output**:
```
╔═══════════════════════════════════════════════════════════╗
║              Available OCR Pipelines                     ║
╚═══════════════════════════════════════════════════════════╝

  simpleocr
    Simple OCR Pipeline
    Fast baseline OCR using Tesseract
    ⏱️  ~0.5s
    Phases: 2 (Identity, OCR)

  advancedocr (default)
    Advanced OCR Pipeline
    Multi-frame temporal processing with voting
    ⏱️  ~2.5s
    📈 +25% accuracy
    Phases: 5 (Identity, Color, OCR, AdvancedOCR...)

  quality
    Quality OCR Pipeline
    Maximum accuracy with all enhancements
    ⏱️  ~12.0s
    📈 +45% accuracy
    Phases: 7 (Identity, Color, OCR, AdvancedOCR...)
```

## Interactive Mode

When run without arguments, enters interactive mode with visual feedback.

```bash
imagesummarizer
```

**Features**:
- ASCII preview of image
- Animated processing spinner
- Colorized output
- Quality recommendations

## Output Formats

### Text Format
```bash
imagesummarizer image.gif
```
**Output**:
```
I'm not even mad. That's amazing.
```

**Use case**: Piping, scripts
```bash
imagesummarizer image.gif | grep "error"
cat images.txt | xargs -I {} imagesummarizer {} >> all-text.txt
```

### JSON Format
```bash
imagesummarizer image.gif --output json
```
**Output**:
```json
{
  "image": "image.gif",
  "duration_ms": 1838,
  "waves": ["ColorWave", "OcrWave", "AdvancedOcrWave"],
  "text": "I'm not even mad. That's amazing.",
  "confidence": 0.92,
  "quality": {
    "spell_check_score": 1.0,
    "is_garbled": false,
    "text_likeliness": 0.45
  },
  "metadata": {
    "frames_processed": 93,
    "stabilization_quality": 0.89,
    "frame_agreement": 1.0
  },
  "ledger": {
    "identity": { ... },
    "colors": { ... },
    "text": {
      "extracted_text": "I'm not even mad. That's amazing.",
      "confidence": 0.92,
      "method": "temporal_voting"
    }
  }
}
```

**Use case**: MCP servers, APIs, structured processing
```bash
imagesummarizer image.gif --output json | jq '.text'
```

### Signals Format
```bash
imagesummarizer image.gif --output signals
```
**Output** (truncated):
```json
[
  {
    "source": "ColorWave",
    "key": "content.text_likeliness",
    "value": "0.45",
    "confidence": 0.85
  },
  {
    "source": "AdvancedOcrWave",
    "key": "ocr.voting.consensus_text",
    "value": "I'm not even mad. That's amazing.",
    "confidence": 0.92
  },
  {
    "source": "AdvancedOcrWave",
    "key": "ocr.frames.extracted",
    "value": "93",
    "confidence": 1.0
  }
]
```

**Use case**: Debugging, system building, discriminator training
```bash
imagesummarizer image.gif --output signals | jq '.[] | select(.key == "content.text_likeliness")'
```

### Metrics Format
```bash
imagesummarizer image.gif --output metrics
```
**Output**:
```json
{
  "analysis_duration_ms": 1838,
  "waves_executed": 4,
  "signals_emitted": 47,
  "frames_processed": 93,
  "text_length": 35,
  "confidence": 0.92,
  "spell_check_score": 1.0,
  "is_garbled": false,
  "stabilization_quality": 0.89,
  "frame_agreement": 1.0
}
```

**Use case**: Monitoring, quality tracking, batch processing
```bash
for f in *.gif; do
  imagesummarizer "$f" --output metrics >> metrics.jsonl
done
```

## Pipeline Details

### Simple Pipeline (`simpleocr`)
**Speed**: ⚡⚡⚡ (< 1s)
**Quality**: ⭐⭐ (Baseline)
**Phases**: Identity, OCR

**When to use**:
- Clear, high-quality text
- Speed critical
- Batch processing thousands of images

**Not recommended for**:
- GIFs or animations
- Low quality images
- Noisy screenshots

### Advanced Pipeline (`advancedocr`)
**Speed**: ⚡⚡ (2-3s)
**Quality**: ⭐⭐⭐ (+25% accuracy)
**Phases**: Identity, Color, OCR, AdvancedOCR, OcrQuality

**When to use**:
- Default for most use cases
- GIFs and animations
- Screenshots with noise
- Balance of speed and quality

**Features**:
- Frame extraction and deduplication
- Temporal median filtering
- Multi-frame voting
- Spell-check quality assessment

### Quality Pipeline (`quality`)
**Speed**: ⚡ (10-15s)
**Quality**: ⭐⭐⭐⭐ (+45% accuracy)
**Phases**: Identity, Color, OCR, AdvancedOCR, OcrQuality, OcrVerification, VisionLlm

**When to use**:
- Forensic analysis
- Compliance/legal documents
- Critical accuracy needed
- Willing to wait for best results

**Features**:
- All advanced features
- Higher frame sampling
- Stricter quality thresholds
- Vision LLM verification (if available)
- Dictionary-based post-correction

## Usage Patterns

### Basic Text Extraction
```bash
# Simplest usage
imagesummarizer screenshot.png

# With specific pipeline
imagesummarizer screenshot.png --pipeline quality
```

### Scripting Integration
```bash
# Pipe to grep
imagesummarizer log.gif | grep "ERROR"

# JSON output to jq
imagesummarizer image.gif --output json | jq '.confidence'

# Batch processing
find . -name "*.gif" -exec imagesummarizer {} --output text \; >> all-text.txt
```

### Quality Monitoring
```bash
# Check quality before committing to slow pipeline
QUALITY=$(imagesummarizer image.gif --output metrics | jq '.spell_check_score')

if (( $(echo "$QUALITY < 0.5" | bc -l) )); then
  echo "Low quality detected, retrying with quality pipeline..."
  imagesummarizer image.gif --pipeline quality
fi
```

### Python Integration
```python
import subprocess
import json

def extract_text(image_path, pipeline='advancedocr'):
    result = subprocess.run([
        'imagesummarizer',
        image_path,
        '--pipeline', pipeline,
        '--output', 'json'
    ], capture_output=True, text=True)

    return json.loads(result.stdout)

# Usage
data = extract_text('screenshot.png')
print(f"Text: {data['text']}")
print(f"Confidence: {data['confidence']}")
print(f"Spell check: {data['quality']['spell_check_score']}")
```

### MCP Server Integration (JSON Output)
While there's no dedicated `--mcp` flag, use JSON output for MCP integration:

```json
{
  "mcpServers": {
    "lucidrag-ocr": {
      "command": "imagesummarizer",
      "args": [
        "{image}",
        "--output", "json",
        "--pipeline", "advancedocr"
      ]
    }
  }
}
```

Claude Desktop will parse the JSON output and extract the text field.

## Environment Variables

None currently supported. Configuration is done via:
1. Command-line flags
2. Pipeline JSON files (`Config/pipelines.json`)
3. AppSettings.json

## Exit Codes

- `0` - Success
- `1` - Error (file not found, processing failed, no text extracted)

## Auto-Downloaded Resources

On first use, ImageCli automatically downloads:

**Dictionaries** (for spell-checking):
- Location: `%APPDATA%/LucidRAG/models/dictionaries/`
- Size: ~539KB per language
- Source: LibreOffice dictionaries

**Tesseract Data** (for OCR):
- Location: `./tessdata/` (project root)
- Size: ~4MB
- Languages: eng (English)

## Performance Tips

1. **Use appropriate pipeline**: Don't use `quality` when `simple` will suffice
2. **Batch processing**: Process similar images together (better cache utilization)
3. **Output format**: Use `text` for pure extraction (avoid JSON serialization overhead)
4. **Pre-filter**: Use `content.text_likeliness` signal to skip non-text images

## Troubleshooting

### "Dictionary not available"
First-time use triggers auto-download. Requires internet connection.

**Manual fix**:
Download `.dic` and `.aff` files to `%APPDATA%/LucidRAG/models/dictionaries/<lang>/`

### "Tesseract not found"
Ensure `tessdata/` folder exists in project root with `eng.traineddata`

### Low quality output (garbled text)
Try higher quality pipeline:
```bash
imagesummarizer image.gif --pipeline quality --verbose
```

Check `spell_check_score` in metrics:
```bash
imagesummarizer image.gif --output metrics | jq '.spell_check_score'
```

### No text extracted
Image may not contain text. Check `text_likeliness`:
```bash
imagesummarizer image.gif --output json | jq '.quality.text_likeliness'
```

Values < 0.3 indicate low text presence.

## Examples Repository

### Extract all GIF text to file
```bash
for f in F:/Gifs/*.gif; do
  echo "=== $(basename "$f") ===" >> output.txt
  imagesummarizer "$f" >> output.txt
  echo "" >> output.txt
done
```

### Quality report
```bash
#!/bin/bash
for f in *.gif; do
  METRICS=$(imagesummarizer "$f" --output metrics)
  echo "$f: $(echo $METRICS | jq '.confidence') confidence, $(echo $METRICS | jq '.spell_check_score') spell check"
done
```

### Conditional processing
```bash
# Try fast pipeline, escalate if needed
TEXT=$(imagesummarizer image.gif --pipeline simpleocr --output json)
QUALITY=$(echo $TEXT | jq '.quality.spell_check_score')

if (( $(echo "$QUALITY < 0.7" | bc -l) )); then
  echo "Quality insufficient ($QUALITY), retrying with quality pipeline"
  imagesummarizer image.gif --pipeline quality
else
  echo $TEXT | jq -r '.text'
fi
```
