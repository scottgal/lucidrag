# ImageSummarizer - Image Intelligence & RAG Ingestion Pipeline

[![License: Unlicense](https://img.shields.io/badge/license-Unlicense-blue.svg)](http://unlicense.org/)
[![Release](https://img.shields.io/github/v/release/scottgal/lucidrag?label=release)](https://github.com/scottgal/lucidrag/releases)
[![Build](https://img.shields.io/github/actions/workflow/status/scottgal/lucidrag/release-imagesummarizer.yml?label=build)](https://github.com/scottgal/lucidrag/actions)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)

An image understanding and ingestion pipeline for RAG systems. Extracts structured metadata, text, captions, and visual signals from images and GIFs using an intelligent wave-based architecture that escalates from fast local analysis to Vision LLMs only when heuristics or OCR signals indicate low confidence.

## What It Does

- **RAG-Ready Output**: Generates structured JSON with text, captions, colors, quality metrics, and embeddings for vector search
- **Intelligent Pipeline**: Wave-based architecture routes images through analysis stages - Vision LLMs are used selectively, never as the sole source of truth
- **Multi-Provider Vision**: Ollama (local), OpenAI (GPT-4o), Anthropic (Claude) - switch providers on the fly
- **Animated Text & Subtitle Extraction**: Frame strip technology for GIFs, memes, and subtitled clips
- **Visual Signal Extraction**: Dominant colors, sharpness, motion analysis, confidence-scored image type classification
- **MCP Server**: Integrate with Claude Desktop for AI-assisted image analysis workflows

## What This Is Not

- Not a generic OCR wrapper
- Not a single-model vision API client
- Not a "CLIP-only" image search tool
- Not a black-box caption generator

ImageSummarizer prioritizes deterministic signals, confidence scoring, and auditability.
Vision LLMs are used selectively, never as the sole source of truth.

## Key Capabilities

### For RAG Ingestion
- Extract searchable text from images (OCR + Vision LLM fallback)
- Generate semantic captions for vector embedding
- Structured JSON output with confidence scores
- Batch process entire directories with parallel processing
- Quality signals help filter low-value images

### For Image Analysis
- **Caption Generation**: Rich descriptions using Vision LLMs (minicpm-v, llava, llama3.2-vision, GPT-4o, Claude)
- **Alt Text**: WCAG-compliant accessibility descriptions
- **Color Analysis**: Dominant colors with RGB values, contrast ratios
- **Quality Metrics**: Sharpness, text-likeliness, spell-check scores
- **Animation Analysis**: Frame deduplication, motion detection, subtitle extraction

### Intelligent Escalation
The agentic pipeline starts fast and escalates intelligently:
1. **ColorWave** (instant): Visual analysis, text-likeliness detection
2. **OcrWave** (<1s): Fast Tesseract OCR
3. **OcrQualityWave** (instant): Spell-check, garbled text detection
4. **VisionLlmWave** (~3-5s): Only triggered when OCR quality is poor or captions requested

Each wave emits structured signals with confidence scores; downstream waves are activated only when earlier signals fall below quality thresholds.

## Key Features

- **Frame Strip Technology**: Creates horizontal film strips from animated GIFs for Vision LLM subtitle reading
- **Temporal Voting**: Multi-frame OCR with voting consensus for higher accuracy
- **Interactive Mode**: Live configuration switching (pipeline, model, provider, output format)
- **Auto-Download Resources**: Dictionaries and tessdata download on first use
- **Multiple Pipelines**: stats, simpleocr, advancedocr, quality, caption, alttext
- **Multiple Output Formats**: text, json, visual, markdown, caption, alttext, metrics, signals

**Pipeline rule of thumb**:
- `simpleocr` → speed
- `advancedocr` → default (balanced)
- `quality` → archival accuracy
- `caption` → stylized text, memes, subtitles
- `alttext` → accessibility

## Installation

### Download Pre-Built Binaries (Recommended)

Download the latest release for your platform from [GitHub Releases](https://github.com/scottgal/lucidrag/releases):

| Platform | Download |
|----------|----------|
| Windows x64 | `imagesummarizer-win-x64.zip` |
| Windows ARM64 | `imagesummarizer-win-arm64.zip` |
| Linux x64 | `imagesummarizer-linux-x64.tar.gz` |
| Linux ARM64 | `imagesummarizer-linux-arm64.tar.gz` |
| macOS x64 | `imagesummarizer-osx-x64.tar.gz` |
| macOS ARM64 (Apple Silicon) | `imagesummarizer-osx-arm64.tar.gz` |

Extract and add to your PATH:
```bash
# Windows (PowerShell)
Expand-Archive imagesummarizer-win-x64.zip -DestinationPath C:\tools\imagesummarizer
$env:PATH += ";C:\tools\imagesummarizer"

# Linux/macOS
tar -xzf imagesummarizer-linux-x64.tar.gz -C ~/.local/bin
chmod +x ~/.local/bin/imagesummarizer
```

### Build from Source

```bash
# Clone the repository
git clone https://github.com/scottgal/lucidrag.git
cd lucidrag

# Build
dotnet build src/Mostlylucid.ImageSummarizer.Cli/Mostlylucid.ImageSummarizer.Cli.csproj -c Release

# Or install as global tool
dotnet pack src/Mostlylucid.ImageSummarizer.Cli/Mostlylucid.ImageSummarizer.Cli.csproj -c Release
dotnet tool install --global --add-source ./nupkg Mostlylucid.ImageSummarizer.Cli
```

## Usage

### Basic Text Extraction
```bash
# Extract text only (automatically escalates to Vision LLM for stylized text)
imagesummarizer image.gif

# Or with dotnet run
dotnet run --project src/Mostlylucid.ImageSummarizer.Cli/Mostlylucid.ImageSummarizer.Cli.csproj -- image.gif
```

### Interactive Mode
```bash
# Start interactive mode (no image argument)
imagesummarizer

# In interactive mode, commands available:
# /help     - Show available commands
# /pipeline - Change OCR pipeline
# /output   - Change output format
# /llm      - Toggle Vision LLM on/off
# /model    - Change Vision LLM model
# /ollama   - Change Ollama server URL
# /models   - List available Ollama models
# /quit     - Exit
```

### Directory Processing
```bash
# Process all images in a directory
imagesummarizer F:\Gifs\ --pipeline caption --output visual
```

### Vision LLM Options
```bash
# Enable Vision LLM explicitly
imagesummarizer image.gif --llm true

# Use a specific Vision model
imagesummarizer image.gif --model llava:13b

# Use a different Ollama server
imagesummarizer image.gif --ollama http://my-server:11434
```

### Caption Pipeline (Best for Movie Subtitles)
```bash
# Uses Vision LLM with frame strip for animated GIFs
imagesummarizer movie-meme.gif --pipeline caption --output text
```

Example output (Princess Bride meme):
```
You keep using that word.
I do not think it means what you think it means.
```

### JSON Output (for scripts/tools)
```bash
imagesummarizer image.gif --output json
```

Output:
```json
{
  "image": "image.gif",
  "duration_ms": 1838,
  "waves": ["ColorWave", "OcrWave", "AdvancedOcrWave", "VisionLlmWave"],
  "text": "You keep using that word.\nI do not think it means what you think it means.",
  "confidence": 0.95,
  "caption": "Three men stand together in a rocky landscape...",
  "quality": {
    "spell_check_score": 0.82,
    "is_garbled": false,
    "text_likeliness": 0.45
  },
  "metadata": {
    "frames_processed": 4,
    "stabilization_quality": 0.89,
    "frame_agreement": 1.0
  }
}
```

### Visual Output (with color swatches)
```bash
imagesummarizer image.gif --output visual
```

Shows rich terminal output with:
- Image dimensions and format
- Actual RGB color swatches for dominant colors
- OCR text and confidence
- Motion analysis for animated images

### Quality Metrics Only
```bash
imagesummarizer image.gif --output metrics
```

### All Signals (detailed diagnostics)
```bash
imagesummarizer image.gif --output signals
```

## Pipelines

Pipelines are fully configurable via JSON! See [PIPELINES.md](../Mostlylucid.DocSummarizer.Images/PIPELINES.md) for complete documentation.

### List Available Pipelines

```bash
imagesummarizer list-pipelines
```

### Caption Pipeline (NEW - Best for Subtitles)
- **Speed**: ~5s per GIF
- **Features**: Vision LLM with frame strip, OCR, color analysis
- **Use Case**: Movie memes, subtitled GIFs, stylized text

```bash
imagesummarizer movie-meme.gif --pipeline caption
```

### Advanced OCR (Default)
- **Speed**: 2-3s per GIF
- **Accuracy**: +25% vs simple OCR
- **Features**: Stabilization, temporal median, voting, spell-check

```bash
imagesummarizer image.gif --pipeline advancedocr
```

### Quality Pipeline
- **Speed**: 10-12s per GIF
- **Accuracy**: +45% vs simple OCR
- **Features**: All advanced features + higher frame counts + stricter thresholds

```bash
imagesummarizer image.gif --pipeline quality
```

### Simple OCR
- **Speed**: < 1s
- **Accuracy**: Baseline Tesseract
- **Features**: Basic OCR only

```bash
imagesummarizer image.gif --pipeline simpleocr
```

### Alt Text Generation
- **Speed**: ~3.5s per image
- **Features**: OCR + vision analysis optimized for accessibility
- **Use Case**: Generating descriptive alt text for images

```bash
imagesummarizer image.gif --pipeline alttext
```

## Vision LLM Integration

The tool automatically escalates to Vision LLM when OCR quality is poor (garbled text, stylized fonts).

### Frame Strip Technology

For animated GIFs with subtitles, the tool creates a horizontal strip of all unique frames:
- Preserves temporal order (subtitles read left-to-right)
- Uses model-specific max dimensions (2048px for MiniCPM-V)
- Never upscales beyond source resolution
- Deduplicates repeated subtitle lines

### Supported Vision Models

| Model | Size | Quality | Speed |
|-------|------|---------|-------|
| minicpm-v:8b | 8B | Excellent | Fast |
| llava:7b | 7B | Good | Fast |
| llava:13b | 13B | Very Good | Medium |
| llama3.2-vision:11b | 11B | Very Good | Medium |

### Configuration

Via environment variables:
```bash
export VISION_MODEL=minicpm-v:8b
export OLLAMA_BASE_URL=http://localhost:11434
```

Or via CLI options:
```bash
imagesummarizer image.gif --model minicpm-v:8b --ollama http://localhost:11434
```

## MCP Server Integration

### Running MCP Server Mode

```bash
# Start MCP server (listens on stdio for MCP protocol)
imagesummarizer --mcp
```

### Claude Desktop Setup

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "image-ocr": {
      "command": "imagesummarizer",
      "args": ["--mcp"],
      "env": {
        "OCR_PIPELINE": "caption",
        "OCR_LANGUAGE": "en_US",
        "VISION_MODEL": "minicpm-v:8b"
      }
    }
  }
}
```

### Available MCP Tools

#### Core OCR Tools
- **`extract_text_from_image`**: Extract text with configurable pipeline and Vision LLM fallback
- **`analyze_image_quality`**: Fast quality metrics without full OCR
- **`list_ocr_pipelines`**: List all available pipelines with performance details
- **`batch_extract_text`**: Process multiple images in a directory

#### Content Generation Tools
- **`summarize_animated_gif`**: Generate motion-aware summaries with subtitle extraction
- **`generate_caption`**: Create accessibility-optimized captions
- **`generate_detailed_description`**: Comprehensive image analysis

#### Template System Tools
- **`analyze_with_template`**: Format analysis using predefined templates
- **`list_output_templates`**: List all available output templates

## Output Formats

| Format | Use Case | Example |
|--------|----------|---------|
| `text` | Simple scripts, piping | `imagesummarizer image.gif \| grep "error"` |
| `json` | MCP servers, APIs | Parse with `jq '.text'` |
| `visual` | Terminal display | Color swatches, formatted output |
| `markdown` | Documentation | Formatted markdown report |
| `caption` | Accessibility | Short, descriptive caption |
| `alttext` | WCAG compliance | Alt text for images |
| `metrics` | Monitoring | Track OCR accuracy over time |
| `signals` | Debugging | See all wave emissions |

## Quality Signals

The tool automatically assesses OCR quality and escalates to Vision LLM when needed:

- **spell_check_score**: 0.0-1.0 (percentage of correctly spelled words)
- **is_garbled**: true if < 50% correct words (triggers Vision LLM escalation)
- **text_likeliness**: Pre-OCR estimate of text presence

Example:
```json
{
  "text": "You keep using that word.",
  "quality": {
    "spell_check_score": 0.95,
    "is_garbled": false
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
imagesummarizer image.gif --language es_ES
```

## Performance

| Pipeline | Speed | Accuracy | Use Case |
|----------|-------|----------|----------|
| Simple | <1s | Baseline | Quick extraction |
| Advanced | 2-3s | +20-30% | Default, best balance |
| Quality | 10-15s | +35-45% | High accuracy needed |
| Caption | ~5s | +50% (with LLM) | Stylized text, subtitles |

## Examples

### Extract text from movie meme GIF
```bash
$ imagesummarizer princess-bride.gif --pipeline caption
You keep using that word.
I do not think it means what you think it means.
```

### Interactive session
```bash
$ imagesummarizer
ImageSummarizer Interactive Mode
Pipeline: advancedocr | Output: auto | LLM: auto
Commands: /help, /pipeline, /output, /llm, /model, /ollama, /models, /quit

Enter image path (or drag & drop): F:\Gifs\meme.gif
Processing...
I'm not even mad. That's amazing.

Enter image path: /llm true
Vision LLM: enabled

Enter image path: /model minicpm-v:8b
Vision model: minicpm-v:8b
```

### Batch process directory
```bash
$ imagesummarizer F:\Gifs\ --pipeline caption --output json > results.jsonl
```

### Use in Python
```python
import subprocess
import json

result = subprocess.run([
    "imagesummarizer", "image.gif",
    "--pipeline", "caption",
    "--output", "json"
], capture_output=True, text=True)

data = json.loads(result.stdout)
print(f"Text: {data['text']}")
print(f"Confidence: {data['confidence']}")
```

## Troubleshooting

### "Dictionary not available"
Dictionaries auto-download on first use. Check internet connection or manually download to:
`%APPDATA%/LucidRAG/models/dictionaries/`

### "Tesseract not found"
Ensure tessdata is in project root or `./tessdata/`

### Low OCR quality on stylized text
Use the caption pipeline which automatically uses Vision LLM:
```bash
imagesummarizer image.gif --pipeline caption
```

### Vision LLM not responding
Ensure Ollama is running:
```bash
ollama serve
ollama pull minicpm-v:8b
```

### List available models
```bash
imagesummarizer --mcp  # Then use /models command
# Or in interactive mode: /models
```

## Architecture

The tool uses a wave-based signal architecture:

1. **IdentityWave** (Priority 100): Basic image properties
2. **ColorWave** (Priority 100): Visual analysis, text-likeliness detection
3. **OcrWave** (Priority 60): Simple baseline OCR
4. **AdvancedOcrWave** (Priority 59): Multi-frame temporal OCR with voting
5. **OcrQualityWave** (Priority 58): Spell-check quality assessment
6. **VisionLlmWave** (Priority 50): Vision LLM text extraction and captioning
7. **ClipEmbeddingWave** (Priority 45): Semantic image embeddings

Each wave emits structured signals with confidence scores and metadata.

### Frame Strip Processing (for animated GIFs)

When processing animated GIFs with subtitles:

1. Extract unique frames via SSIM deduplication
2. Create horizontal strip preserving temporal order
3. Size to model's max resolution (no upscaling)
4. Send to Vision LLM with subtitle-aware prompt
5. Deduplicate repeated subtitle lines in response

This approach correctly extracts movie subtitles like:
```
"You keep using that word."
"I do not think it means what you think it means."
```
