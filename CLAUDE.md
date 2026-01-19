# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

LucidRAG is a multi-document Agentic RAG (Retrieval-Augmented Generation) system with GraphRAG-style entity extraction
and knowledge graph visualization. Built with .NET 10.0, it supports uploading PDFs, DOCX, Markdown, HTML, and TXT
documents for AI-powered semantic search and conversation.

## Build Commands

```bash
# Build
dotnet build LucidRAG.sln
dotnet build LucidRAG.sln -c Release

# Run web app (https://localhost:5020)
dotnet run --project src/LucidRAG/LucidRAG.csproj
dotnet run --project src/LucidRAG/LucidRAG.csproj -- --standalone  # SQLite mode

# Run CLI
dotnet run --project src/LucidRAG.Cli/LucidRAG.Cli.csproj

# Tests
dotnet test src/LucidRAG.Tests/LucidRAG.Tests.csproj
dotnet test src/LucidRAG.Tests/LucidRAG.Tests.csproj --filter "Category!=Browser"

# Frontend CSS (from src/LucidRAG/)
npm install
npm run build:css
npm run watch:css

# Docker
docker-compose -f src/LucidRAG/docker-compose.yml up -d
```

## Architecture

Multi-project solution with unified pipeline architecture:

```
# Applications
LucidRAG (web)              → ASP.NET Core 10 + Razor/HTMX + Tailwind/DaisyUI
LucidRAG.Cli                → Unified CLI (auto-routes by extension)
LucidRAG.Tests              → xUnit + PostgreSQL test containers
Mostlylucid.ImageSummarizer.Cli → Standalone image analysis tool + MCP server

# Core Pipeline Infrastructure (Unified Processing)
Mostlylucid.Summarizer.Core         → Unified pipeline interfaces, XxHash64 content hashing
Mostlylucid.DocSummarizer.Core      → Document pipeline (PDF, DOCX, Markdown, HTML, TXT)
ImageSummarizer.Core                → Image pipeline (22-wave ML, OCR, motion, vision LLM)
DataSummarizer.Core                 → Data pipeline (CSV, Excel, Parquet, JSON)

# LLM Providers
Mostlylucid.DocSummarizer.Anthropic → Claude integration
Mostlylucid.DocSummarizer.OpenAI    → OpenAI/GPT-4o integration

# Specialized Services
Mostlylucid.GraphRag                → Entity extraction & knowledge graph
Mostlylucid.RAG                     → Vector store abstraction (DuckDB/Qdrant)
```

**Dependency flow**

- Applications → Core pipelines → Summarizer.Core
- Each pipeline owns its domain-specific processing
- All pipelines implement `IPipeline` interface
- Unified `ContentHasher` utility (XxHash64) for all content hashing

**Unified Pipeline Pattern**

```csharp
// Each Core project registers its pipeline
services.AddDocSummarizer();          // DocumentPipeline
services.AddDocSummarizerImages();    // ImagePipeline
services.AddDataSummarizer();         // DataPipeline
services.AddPipelineRegistry();       // Discovery service

// Auto-routing by extension
var registry = services.GetRequiredService<IPipelineRegistry>();
var pipeline = registry.FindForFile("document.pdf");
var result = await pipeline.ProcessAsync("document.pdf");
```

## Features

### Table Extraction (NEW - 2026-01-10)

Automatic table extraction from PDF and DOCX documents:

- **Extractors**: .NET native (DocumentFormat.OpenXml for DOCX, PdfPig for PDF)
- **Storage**: Tables stored as evidence artifacts (CSV + JSON metadata)
- **Entity Linking**: Tables linked to parent document as RetrievalEntityRecords
- **Pipeline**: Integrated into DocumentQueueProcessor (runs at 60-70% progress)
- **Confidence Scoring**: DOCX (0.7-1.0), PDF heuristic (0.4-0.7)

**Key Services**:

- `TableExtractorFactory` - Selects appropriate extractor (PDF/DOCX)
- `TableProcessingService` - Stores tables as evidence, creates entities
- `DocxTableExtractor` - Extracts tables from DOCX (high accuracy)
- `PdfTableExtractor` - Extracts tables from PDF (heuristic word positioning)

**Evidence Artifacts**:

- `table_csv` - Exported CSV data (for DataSummarizer profiling)
- `table_json` - Table metadata (structure, confidence, extraction method)

**Test Coverage**: 3/3 integration tests passing
**Documentation**: See `INTEGRATION_COMPLETE_TableExtraction.md`

**Future Enhancements** (Phase 3):

- Chart extraction (See `DESIGN_ChartExtraction.md`)
- Table embeddings for semantic search
- Table-aware queries ("find tables with column X")

### Segment Deduplication

Two-phase deduplication strategy to eliminate redundant content while preserving important signals.
Fully configurable via `DocSummarizer:Deduplication` section in `appsettings.json`.

**Phase 1 - Ingestion (Intra-Document):**
- Deduplicates within each document before indexing
- Near-duplicates (same meaning, different text) boost salience
- Supports Linear or Logarithmic boost decay modes
- Exact duplicates (same ContentHash) dropped without boost
- Configurable: `Deduplication:Ingestion` section

**Phase 2 - Retrieval (Cross-Document):**
- Deduplicates across documents after RRF ranking
- Keeps segment with highest RRF score when similar content found
- Prevents LLM from receiving redundant information
- Configurable: `Deduplication:Retrieval` section

**Key Services**:
- `IDeduplicationService` - Injectable service for both phases
- `DeduplicationConfig` - Full configuration with analytics
- `DeduplicationResult<T>` - Metrics tracking for observability

**Configuration Example**:
```json
{
  "DocSummarizer": {
    "Deduplication": {
      "Ingestion": {
        "SimilarityThreshold": 0.90,
        "BoostDecayMode": "Logarithmic"
      },
      "Retrieval": {
        "SimilarityThreshold": 0.90,
        "MinRelevanceScore": 0.25
      }
    }
  }
}
```

**Documentation**: See `docs/DEDUPLICATION_STRATEGY.md`

### Docling Integration (Optional External Service)

LucidRAG supports [Docling](https://github.com/docling-project/docling) as an optional extraction backend.
Docling provides advanced document understanding with layout analysis, table extraction, OCR, and audio transcription.

**When Disabled (Default)**:
- Native .NET handlers process documents (PdfPig, OpenXml, Tesseract)
- ImagePipeline uses Florence-2, Tesseract, and vision LLMs for OCR
- AudioPipeline uses WhisperX for transcription

**When Enabled**:
- Docling handlers have higher priority and are tried first
- Falls back gracefully to native handlers if Docling service is unavailable
- Supports: PDF, DOCX, PPTX, XLSX, HTML (documents), PNG/JPG/TIFF (images), WAV/MP3 (audio)

**Configuration**:
```json
{
  "DocSummarizer": {
    "Docling": {
      "Enabled": true,
      "BaseUrl": "http://localhost:5001",
      "TimeoutSeconds": 300,
      "Documents": {
        "Enabled": true,
        "Extensions": [".pdf", ".docx", ".pptx", ".xlsx", ".html"],
        "Priority": 20
      },
      "Images": {
        "Enabled": true,
        "Extensions": [".png", ".jpg", ".jpeg", ".tiff", ".webp", ".bmp"],
        "UseAsPrimaryOcr": false
      },
      "Audio": {
        "Enabled": true,
        "Extensions": [".wav", ".mp3", ".m4a", ".flac", ".ogg"],
        "UseAsPrimaryTranscription": false
      }
    }
  }
}
```

**Key Components**:
- `DoclingDocumentHandler` - Document extraction (priority 20, highest)
- `DoclingOcrWave` - Image OCR wave (priority 58)
- `DoclingTranscriptionWave` - Audio ASR wave (priority 62)
- `DoclingClient` - HTTP client for Docling service communication

**Handler Priority Order** (Document Pipeline):
1. `DoclingDocumentHandler` (priority 20) - when enabled
2. `EnhancedPdfDocumentHandler` (priority 10) - VLM OCR fallback
3. Native handlers (priority 0) - PdfPig, OpenXml

**Running Docling Service**:
```bash
# Using Docker
docker run -p 5001:5001 ds4sd/docling-serve:latest

# Or install locally
pip install docling-serve
docling-serve --port 5001
```

---

## Key Directories

### Applications

- `src/LucidRAG/Controllers/Api/` - REST endpoints (Chat, Search, Documents, Graph, Config)
- `src/LucidRAG/Services/` - Core services: DocumentProcessingService, ConversationService, AgenticSearchService,
  EntityGraphService
- `src/LucidRAG/Data/` - EF Core DbContext and migrations
- `src/LucidRAG.Cli/` - Unified CLI tool with pipeline auto-routing
- `src/Mostlylucid.ImageSummarizer.Cli/` - Standalone image analysis + MCP server

### Core Pipeline Infrastructure

- `src/Mostlylucid.Summarizer.Core/` - Unified pipeline interfaces, `PipelineBase`, `ContentHasher` utility
- `src/Mostlylucid.DocSummarizer.Core/` - Document processing pipeline, ONNX embeddings, BM25
- `src/ImageSummarizer.Core/` - 22-wave image analysis, OCR, motion detection, filmstrip optimization
- `src/DataSummarizer.Core/` - Data profiling, DuckDB analytics, constraint validation

### Specialized Services

- `src/Mostlylucid.GraphRag/GraphRagPipeline.cs` - Entity extraction orchestration
- `src/Mostlylucid.RAG/` - Vector store abstraction (DuckDB/Qdrant backends)

## Configuration

Primary configuration in `src/LucidRAG/appsettings.json`:

- `ConnectionStrings:DefaultConnection` - PostgreSQL (SQLite in standalone mode)
- `DocSummarizer:EmbeddingBackend` - "Onnx" (default), "Ollama", or "Docling"
- `DocSummarizer:BertRag:VectorStore` - "DuckDB" (default) or "Qdrant"
- `DocSummarizer:Ollama:BaseUrl/Model` - LLM backend for chat
- `Prompts` section - Query clarification, decomposition, self-correction toggles

## Storage Backends

- **PostgreSQL 16+** - Production database (EF Core)
- **SQLite** - Standalone/CLI mode (metadata only)
- **Qdrant** - Production vector store (persistent embeddings)
- **InMemory** - Standalone vector store (ephemeral, no persistence)
- **ONNX** - Local embeddings (all-MiniLM-L6-v2, no API keys needed)

### ⚠️ Standalone Mode Limitations

**Current State**

- `VectorStoreBackend.DuckDB` is defined in the enum but **NOT IMPLEMENTED**
- Code falls back to `InMemory` vector store in standalone mode
- Document embeddings are **lost on restart**
- Must re-index all documents on every startup

**Working Standalone**

- ✅ **ImageSummarizer.Cli**: Fully standalone, no database needed
- ✅ **DataSummarizer**: Uses DuckDB with VSS extension for persistent vectors
- ⚠️ **LucidRAG (web)**: SQLite metadata + InMemory vectors (no persistence)
- ⚠️ **LucidRAG.Cli**: SQLite metadata + InMemory vectors (no persistence)

**For Persistent Embeddings** Use docker-compose.yml with Qdrant

## CI/CD

- `.github/workflows/build.yml` - PR/push: builds + tests with PostgreSQL
- `.github/workflows/release-lucidrag.yml` - Docker multi-arch (amd64/arm64) on `lucidrag-v*` tags
- `.github/workflows/release-lucidrag-cli.yml` - CLI binary releases
- `.github/workflows/publish-docsummarizer-nuget.yml` - NuGet publishing

## Testing Notes

Tests use Testcontainers.PostgreSql for integration tests. Browser tests (PuppeteerSharp) are excluded in CI with
`--filter "Category!=Browser"`.

## Test Data Paths

- **Markdown test corpus**: `C:\Blog\mostlylucidweb\Mostlylucid\Markdown` - Great for RAG/bulk ingestion testing
