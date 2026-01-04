# ImageCli - Standalone Image OCR and Analysis Tool

Zero-friction image OCR with auto-downloading dictionaries and models.

## Features

- **Advanced Multi-Frame OCR**: Temporal processing, frame stabilization, voting
- **Auto-Download**: Dictionaries and models download automatically on first use
- **Spell-Check Quality Detection**: Detects garbled OCR output (< 50% correct words)
- **Multiple Pipelines**: Simple, Advanced, Quality
- **Multiple Output Formats**: Text, JSON, Signals, Metrics
- **MCP Server Support**: Integrates with Claude Desktop and other MCP clients

## Installation

### As Global Tool
```bash
dotnet pack src/ImageCli/ImageCli.csproj
dotnet tool install --global --add-source ./nupkg LucidRAG.ImageCli
```

### Standalone Build
```bash
dotnet build src/ImageCli/ImageCli.csproj -c Release
```

## Usage

### Basic Text Extraction
```bash
# Extract text only
imagecli image.gif

# Or with dotnet run
dotnet run --project src/ImageCli/ImageCli.csproj -- image.gif
```

### JSON Output (for scripts/tools)
```bash
imagecli image.gif --output json
```

Output:
```json
{
  "image": "image.gif",
  "duration_ms": 1838,
  "waves": ["ColorWave", "OcrWave", "AdvancedOcrWave", "OcrQualityWave"],
  "text": "I'm not even mad. That's amazing.",
  "confidence": 0.71,
  "quality": {
    "spell_check_score": 0.82,
    "is_garbled": false,
    "text_likeliness": 0.45
  },
  "metadata": {
    "frames_processed": 93,
    "stabilization_quality": 0.89,
    "frame_agreement": 1.0
  }
}
```

### Quality Metrics Only
```bash
imagecli image.gif --output metrics
```

### All Signals (detailed diagnostics)
```bash
imagecli image.gif --output signals
```

## Pipelines

Pipelines are now fully configurable via JSON! See [PIPELINES.md](../Mostlylucid.DocSummarizer.Images/PIPELINES.md) for complete documentation.

### List Available Pipelines

```bash
imagecli list-pipelines
```

### Advanced OCR (Default)
- **Speed**: 2-3s per GIF
- **Accuracy**: +25% vs simple OCR
- **Features**: Stabilization, temporal median, voting, spell-check

```bash
imagecli image.gif --pipeline advancedocr
```

### Quality Pipeline
- **Speed**: 10-12s per GIF
- **Accuracy**: +45% vs simple OCR
- **Features**: All advanced features + higher frame counts + stricter thresholds

```bash
imagecli image.gif --pipeline quality
```

### Simple OCR
- **Speed**: < 1s
- **Accuracy**: Baseline Tesseract
- **Features**: Basic OCR only

```bash
imagecli image.gif --pipeline simpleocr
```

### Alt Text Generation
- **Speed**: ~3.5s per image
- **Features**: OCR + vision analysis optimized for accessibility
- **Use Case**: Generating descriptive alt text for images

```bash
imagecli image.gif --pipeline alttext
```

### Custom Pipelines

Create your own pipelines by editing `Config/pipelines.json`:

```json
{
  "name": "my-pipeline",
  "displayName": "My Custom Pipeline",
  "phases": [
    {
      "id": "color",
      "waveType": "ColorWave",
      "priority": 100,
      "enabled": true
    }
  ]
}
```

See [PIPELINES.md](../Mostlylucid.DocSummarizer.Images/PIPELINES.md) for full configuration reference.

## MCP Server Integration

### Claude Desktop Setup

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "lucidrag-ocr": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "E:/source/lucidrag/src/ImageCli/ImageCli.csproj",
        "-c",
        "Release",
        "--",
        "{image}",
        "--output",
        "json",
        "--pipeline",
        "advancedocr"
      ]
    }
  }
}
```

### Using in Claude Desktop

```
User: Extract text from F:/Gifs/meme.gif
Claude: [Calls extract_text_from_image tool]

        The image says: "I'm not even mad. That's amazing."
        (OCR confidence: 0.82, spell-check quality: 82% correct)
```

## Output Formats

| Format | Use Case | Example |
|--------|----------|---------|
| `text` | Simple scripts, piping | `imagecli image.gif \| grep "error"` |
| `json` | MCP servers, APIs | Parse with `jq '.text'` |
| `metrics` | Monitoring, quality checks | Track OCR accuracy over time |
| `signals` | Debugging, diagnostics | See all wave emissions |

## Quality Signals

The tool automatically assesses OCR quality via spell-checking:

- **spell_check_score**: 0.0-1.0 (percentage of correctly spelled words)
- **is_garbled**: true if < 50% correct words (triggers LLM correction recommendation)
- **text_likeliness**: Pre-OCR estimate of text presence

Example:
```json
{
  "text": "of * I'm not even mad.",
  "quality": {
    "spell_check_score": 0.82,
    "is_garbled": false  // Good quality, no LLM correction needed
  }
}
```

## Auto-Downloaded Resources

On first run, the tool automatically downloads:

- **English Dictionary**: 539KB (from LibreOffice)
- **Tesseract Data**: 4MB
- **Location**: `%APPDATA%/LucidRAG/models/dictionaries/`

Supported languages: en_US, en_GB, es_ES, fr_FR, de_DE, it_IT, pt_BR, ru_RU, zh_CN, ja_JP

Change language:
```bash
imagecli image.gif --language es_ES
```

## Performance

| Pipeline | Speed | Accuracy Gain | Use Case |
|----------|-------|---------------|----------|
| Simple | <1s | Baseline | Quick extraction |
| Advanced | 2-3s | +20-30% | Default, best balance |
| Quality | 10-15s | +35-45% | High accuracy needed |

## Examples

### Extract text from GIF meme
```bash
$ imagecli meme.gif
I'm not even mad. That's amazing.
```

### Get quality metrics for batch processing
```bash
$ for f in *.gif; do
    imagecli "$f" --output metrics >> metrics.jsonl
  done
```

### Use in Python
```python
import subprocess
import json

result = subprocess.run([
    "imagecli", "image.gif", "--output", "json"
], capture_output=True, text=True)

data = json.loads(result.stdout)
print(f"Text: {data['text']}")
print(f"Quality: {data['quality']['spell_check_score']}")
```

### Pipeline selection based on quality
```bash
# Try fast pipeline first
QUALITY=$(imagecli image.gif --output metrics | jq '.spell_check_score')

# If quality < 0.5 (garbled), retry with quality pipeline
if (( $(echo "$QUALITY < 0.5" | bc -l) )); then
    imagecli image.gif --pipeline quality
fi
```

## Troubleshooting

### "Dictionary not available"
Dictionaries auto-download on first use. Check internet connection or manually download to:
`%APPDATA%/LucidRAG/models/dictionaries/`

### "Tesseract not found"
Ensure tessdata is in project root or `./tessdata/`

### Low OCR quality (< 50% spell check)
Try higher quality pipeline:
```bash
imagecli image.gif --pipeline quality
```

## Architecture

The tool uses a wave-based signal architecture:

1. **ColorWave** (Priority 100): Visual analysis, text-likeliness detection
2. **OcrWave** (Priority 60): Simple baseline OCR
3. **AdvancedOcrWave** (Priority 59): Multi-frame temporal OCR
4. **OcrQualityWave** (Priority 58): Spell-check quality assessment

Each wave emits structured signals with confidence scores and metadata.
