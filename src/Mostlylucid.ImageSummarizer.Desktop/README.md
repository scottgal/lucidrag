# ImageSummarizer Desktop

A cross-platform Avalonia desktop app for generating alt text, captions, and analyzing images.

## Features

- **Drag & Drop** - Drop images directly onto the window
- **Browse** - Select images via file picker
- **Live Status Indicators** - Traffic lights showing OCR, OpenCV, and LLM availability
- **Auto Vision Model Detection** - Discovers installed Ollama vision models
- **Multiple Pipelines** - caption, alttext, vision, motion, OCR, quality, stats
- **Multiple Output Formats** - alttext, caption, text, json, markdown, signals
- **Vision LLM Integration** - Uses Ollama with minicpm-v:8b (preferred) or llava
- **Copy to Clipboard** - One-click copy for easy pasting
- **Shell Integration** - Right-click context menu for images (Windows)

## Installation

### From Release

1. Download `mostlylucid-imagesummarizer-desktop-{platform}.zip` from [Releases](https://github.com/scottgal/lucidrag/releases)
2. Extract to a folder (e.g., `C:\Tools\ImageSummarizer`)
3. Run `ImageSummarizer.exe` (Windows) or `ImageSummarizer` (macOS/Linux)

### From Source

```bash
cd src/Mostlylucid.ImageSummarizer.Desktop
dotnet build -c Release
dotnet run
```

## Shell Integration (Windows)

Add a right-click context menu entry for images:

```powershell
# Run as Administrator
.\install-context-menu.ps1

# Or specify the exe path explicitly
.\install-context-menu.ps1 -ExePath "C:\Tools\ImageSummarizer\ImageSummarizer.exe"

# To uninstall
.\install-context-menu.ps1 -Uninstall
```

After installation, right-click any image file and select **"Get Alt Text"**.

## Status Indicators

The top bar shows service availability:

| Indicator | Green | Red |
|-----------|-------|-----|
| **OCR** | Tesseract ready | Tesseract failed |
| **CV** | OpenCV ready | OpenCV failed |
| **LLM** | Ollama connected | Ollama not running |

Hover over indicators for detailed status.

## Usage

### GUI

1. **Browse** or drag-drop an image
2. Select **Pipeline** (what analysis to run) - see [Pipeline Details](#pipeline-details) below
3. Select **Output** format - see [Output Formats](#output-formats) below
4. Configure **Speed options** (checkboxes)
5. Click **Analyze** (or it auto-analyzes on load)
6. Click **Copy** to copy result to clipboard

### Pipeline Details

Different pipelines produce different results because they use different analysis approaches:

| Pipeline | What It Does | Best For | Speed |
|----------|--------------|----------|-------|
| **caption** | Full wave pipeline + Vision LLM | Complete analysis with description | ~5s |
| **vision** | Direct Vision LLM only, skips heuristics | Quick captions without OCR | ~3s |
| **alttext** | Optimized for accessibility descriptions | WCAG-compliant alt text | ~4s |
| **motion** | Optical flow analysis for animations | Understanding GIF movement | ~1s |
| **advancedocr** | Multi-frame OCR with temporal voting | Document/screenshot text | ~3s |
| **simpleocr** | Basic Tesseract OCR on first frame | Quick text extraction | <1s |
| **quality** | Image quality metrics only | Filtering/sorting by quality | <1s |
| **stats** | Basic dimensions, colors, format | Quick metadata only | <1s |

**Why do different pipelines give different results?**

- **caption** runs the full wave pipeline (color, motion, OCR, then LLM) - the LLM receives analysis context
- **vision** sends the image directly to the Vision LLM with a minimal prompt - no context from heuristics
- **alttext** is optimized for brevity and screen reader compatibility
- **motion** focuses only on animation analysis (optical flow, frame deduplication)

For most images, `caption` and `vision` produce similar results. The difference is visible when:
- The image has complex motion (caption includes motion context)
- The image has text (caption includes OCR results)
- You need speed (vision is faster by skipping analysis)

### Output Formats

| Format | Description | Use Case |
|--------|-------------|----------|
| **alttext** | Concise single-line description | Accessibility, HTML alt attributes |
| **caption** | Full natural language description | Documentation, image galleries |
| **text** | Only extracted text (OCR or LLM-read) | When you just need the text |
| **json** | Structured data with all signals | Programmatic use, APIs |
| **markdown** | Formatted markdown document | Documentation generation |
| **signals** | Raw signal dump from all waves | Debugging, development |

### Speed Options (Checkboxes)

| Option | Effect | When to Use |
|--------|--------|-------------|
| **Fast** | Skips all heuristic waves, direct LLM call | Quick captions, high volume |
| **No Ctx** | Uses minimal prompt without analysis context | Simpler output, less prompt influence |

**Combinations:**
- `Fast + No Ctx` = Fastest possible caption (simple prompt, no waves)
- `Fast` only = Fast caption with detailed prompt
- `No Ctx` only = Full pipeline but minimal LLM prompt
- Neither = Full pipeline with context-enriched prompt

### Understanding the Results

The desktop app uses a **shared FastCaptionService** that ensures consistent prompts across all clients (CLI, Desktop, MCP). For GIFs, it automatically creates a frame strip for the Vision LLM to understand motion and read subtitles.

For detailed architecture information, see:
- [SIGNAL-ARCHITECTURE.md](../Mostlylucid.DocSummarizer.Images/SIGNAL-ARCHITECTURE.md) - Signals vs Captions design
- [Images Library README](../Mostlylucid.DocSummarizer.Images/README.md) - Full wave architecture

### Command Line

```bash
# Open with specific image
ImageSummarizer.exe "C:\path\to\image.jpg"
```

## Configuration

### Ollama Setup

For Vision LLM features, you need [Ollama](https://ollama.ai) running locally:

```bash
# Install Ollama
winget install Ollama.Ollama

# Pull a vision model
ollama pull minicpm-v:8b
```

The app defaults to:
- **URL**: `http://localhost:11434`
- **Model**: `minicpm-v:8b`

You can change these in the status bar at the bottom of the window.

### Alternative Models

```bash
ollama pull llava:7b      # General vision
ollama pull llava:13b     # Higher quality
ollama pull bakllava:7b   # Fast
```

## Supported Formats

- JPEG (.jpg, .jpeg)
- PNG (.png)
- GIF (.gif) - including animated
- WebP (.webp)
- BMP (.bmp)

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| Ctrl+O | Browse for image |
| Ctrl+C | Copy result |
| F5 | Re-analyze |

## Requirements

- .NET 10.0 Runtime
- Windows 10/11, macOS, or Linux
- Ollama (optional, for Vision LLM)
- Tesseract (optional, for OCR)

## License

Unlicense - See LICENSE file
