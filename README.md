# LucidRAG

Multi-document Agentic RAG with GraphRAG-style entity extraction.

## Features

- **Multi-document RAG** - Upload PDFs, DOCX, Markdown, HTML, TXT
- **Hybrid Search** - BM25 + BERT embeddings with RRF fusion
- **GraphRAG** - Entity extraction and knowledge graph visualization
- **Agentic Chat** - Conversation memory, query clarification, self-correction
- **ONNX Embeddings** - Zero-config local embeddings (no API keys needed)
- **D3.js Visualization** - Interactive knowledge graph explorer

## Quick Start

### Docker (Recommended)

```bash
# Pull the image
docker pull scottgal/lucidrag:latest

# Start with docker-compose
cd src/LucidRAG
docker-compose up -d
```

Then open http://localhost:5080 in your browser.

### CLI Tool

Download from [Releases](https://github.com/scottgal/lucidrag/releases) or build from source:

```bash
# Build CLI
dotnet publish src/LucidRAG.Cli/LucidRAG.Cli.csproj -c Release -o ./publish

# Usage
./publish/lucidrag-cli index document.pdf
./publish/lucidrag-cli search "your query"
./publish/lucidrag-cli chat
./publish/lucidrag-cli serve
```

### ImageSummarizer (Standalone OCR Tool)

Standalone image analysis and OCR tool with MCP server support:

```bash
# Install as global tool
dotnet pack src/Mostlylucid.ImageSummarizer.Cli/Mostlylucid.ImageSummarizer.Cli.csproj
dotnet tool install --global --add-source ./nupkg Mostlylucid.ImageSummarizer.Cli

# Extract text from images/GIFs
imagesummarizer screenshot.png
imagesummarizer animation.gif --pipeline advancedocr

# MCP server mode for Claude Desktop
imagesummarizer --mcp
```

**Features:**
- **9 MCP Tools**: OCR, quality analysis, GIF summarization, captions, templates
- **Advanced GIF OCR**: Temporal processing, frame stabilization, multi-frame voting
- **Template System**: 9 predefined output templates (social media, accessibility, SEO, etc.)
- **Zero Dependencies**: Runs entirely offline with ONNX models

See [ImageSummarizer README](src/Mostlylucid.ImageSummarizer.Cli/README.md) and [MCP Enhancements Summary](MCP-ENHANCEMENTS-SUMMARY.md) for details.

## Project Structure

```
src/
  LucidRAG/                            # Main web application
  LucidRAG.Cli/                        # CLI tool
  LucidRAG.Tests/                      # Integration tests
  Mostlylucid.DocSummarizer.Core/      # Document processing & embeddings
  Mostlylucid.DocSummarizer.Images/    # Image analysis & OCR engine
  Mostlylucid.ImageSummarizer.Cli/     # Standalone OCR tool with MCP support
  Mostlylucid.GraphRag/                # Entity extraction & graph
  Mostlylucid.RAG/                     # Vector store services
```

## Building from Source

```bash
# Clone the repository
git clone https://github.com/scottgal/lucidrag.git
cd lucidrag

# Build all projects
dotnet build LucidRAG.sln

# Run web app
dotnet run --project src/LucidRAG/LucidRAG.csproj

# Run tests
dotnet test src/LucidRAG.Tests/LucidRAG.Tests.csproj
```

## Configuration

Configuration is done via `appsettings.json`:

```json
{
  "RagDocuments": {
    "OllamaUrl": "http://localhost:11434",
    "OllamaModel": "llama3.2:latest",
    "EmbeddingsMode": "ONNX",
    "DemoMode": false
  }
}
```

## Requirements

- .NET 10.0 SDK
- PostgreSQL 16+ (or SQLite for standalone)
- Optional: Ollama for LLM-enhanced features
- Optional: Docling for advanced PDF/DOCX conversion
- Optional: Qdrant for scalable vector storage

## License

MIT License - see [LICENSE](LICENSE)
