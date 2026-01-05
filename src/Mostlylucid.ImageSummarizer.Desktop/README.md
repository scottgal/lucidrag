# ImageSummarizer Desktop

A cross-platform Avalonia desktop app for generating alt text, captions, and analyzing images.

![Screenshot](screenshot.png)

## Features

- **Drag & Drop** - Drop images directly onto the window
- **Browse** - Select images via file picker
- **Multiple Pipelines** - caption, alttext, vision, motion, OCR, quality, stats
- **Multiple Output Formats** - alttext, caption, text, json, markdown, signals
- **Vision LLM Integration** - Uses Ollama for AI-powered captions
- **Copy to Clipboard** - One-click copy for easy pasting
- **Shell Integration** - Right-click context menu for images

## Installation

### From Release

1. Download the latest release from [Releases](https://github.com/scottgal/lucidrag/releases)
2. Extract to a folder (e.g., `C:\Tools\ImageSummarizer`)
3. Run `ImageSummarizer.exe`

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

## Usage

### GUI

1. **Browse** or drag-drop an image
2. Select **Pipeline** (what analysis to run):
   - `caption` - Full Vision LLM caption (default)
   - `alttext` - Optimized for alt text
   - `vision` - Vision LLM only (no OCR)
   - `motion` - GIF motion analysis
   - `advancedocr` - Multi-frame OCR with voting
   - `simpleocr` - Basic Tesseract OCR
   - `quality` - Image quality metrics
   - `stats` - Basic statistics only

3. Select **Output** format:
   - `alttext` - Concise alt text (default)
   - `caption` - Full description
   - `text` - Extracted text only
   - `json` - Structured JSON
   - `markdown` - Markdown document
   - `signals` - All analysis signals

4. Click **Analyze** (or it auto-analyzes on load)
5. Click **Copy** to copy result to clipboard

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
