# LucidRAG

**Production-ready Multi-Document RAG System with GraphRAG Entity Extraction, 22-Wave Image Intelligence, and Multi-Tenant SaaS Support**

> 🚨🚨 PRERELEASE - I will update here when it's stable 🚨🚨

LucidRAG is a feature-rich Retrieval-Augmented Generation system that goes far beyond basic RAG. It combines hybrid search (BM25 + semantic embeddings), agentic query decomposition, knowledge graph construction, and a powerful 22-wave image analysis engine - all deployable with zero API keys using local ONNX & Ollama models.

## Key Differentiators

- **Truly Local**: ONNX embeddings + DuckDB/SQLite = no API keys, no cloud dependencies
- **22-Wave Image Analysis**: Modular, composable ML pipeline with signal-based coordination
- **Agentic Query Decomposition**: Sentinel service breaks complex queries into sub-queries
- **GraphRAG Integration**: Entity extraction with community detection and summarization
- **Multi-Provider LLM**: Swap between Ollama (local) / LMStudio (local) / Anthropic (paid), OpenAI (paid) at runtime
- **Evidence Artifacts**: Structured storage of OCR results, frame data, transcripts
- **Multi-Tenant SaaS**: Schema-per-tenant isolation with automatic provisioning

---

## Quick Start

### Docker (Recommended)

```bash
docker pull scottgal/lucidrag:latest
cd src/LucidRAG
docker-compose up -d
```

Open http://localhost:5080

### Standalone Mode (Zero Config)

```bash
dotnet run --project src/LucidRAG/LucidRAG.csproj -- --standalone
```

Uses SQLite + InMemory vectors locally - no PostgreSQL or Qdrant needed.

**⚠️ Limitation:** Document embeddings are **not persisted** in standalone mode. All documents must be re-indexed on each startup. For persistent embeddings, use Docker with Qdrant (see docker-compose.yml).

---

## Supported Document Types

| Category | Formats |
|----------|---------|
| **Documents** | PDF, DOCX, DOC, Markdown, HTML, TXT, RTF |
| **Images** | PNG, JPG, JPEG, GIF, WebP, BMP, TIFF, TIF |
| **Data** | CSV, XLSX, XLS, Parquet, JSON |

---

## Architecture

```
src/
  LucidRAG/                            # Main web application (ASP.NET Core 10)
  LucidRAG.Cli/                        # Command-line tool with unified pipeline
  LucidRAG.Tests/                      # Integration tests with TestContainers

  # Core Pipeline Infrastructure
  Mostlylucid.Summarizer.Core/         # Unified pipeline interfaces & registry
  Mostlylucid.DocSummarizer.Core/      # Document processing (PDF, DOCX, MD, HTML)
  ImageSummarizer.Core/                # 22-wave image analysis engine
  DataSummarizer.Core/                 # Structured data (CSV, Excel, Parquet, JSON)

  # LLM Providers
  Mostlylucid.DocSummarizer.Anthropic/ # Claude provider
  Mostlylucid.DocSummarizer.OpenAI/    # OpenAI/GPT-4o provider

  # Standalone Tools
  Mostlylucid.ImageSummarizer.Cli/     # Standalone OCR tool with MCP support

  # Specialized Features
  Mostlylucid.GraphRag/                # Entity extraction & knowledge graph
  Mostlylucid.RAG/                     # Vector store abstraction (Qdrant)
```

---

## Unified Pipeline Architecture

All content processing flows through a unified `IPipeline` interface:

- **DocumentPipeline** - PDF, DOCX, Markdown, HTML, TXT
- **ImagePipeline** - GIF, PNG, JPG, WebP, BMP, TIFF
- **DataPipeline** - CSV, Excel, Parquet, JSON

Each pipeline:
- Owns its modality-specific processing
- Returns standardized `ContentChunk` objects
- Registers with `IPipelineRegistry` for auto-routing
- Supports progress reporting and cancellation

```csharp
// CLI auto-routes based on file extension
lucidrag process document.pdf image.gif data.csv --collection mydata

// Each file routed to appropriate pipeline automatically
```

**Content Hashing**: All pipelines use XxHash64 for fast, consistent content hashing and deduplication.

---

## Features

### Hybrid Search & Retrieval

- **BM25 + Semantic**: Full-text lexical search combined with BERT embeddings
- **RRF Fusion**: Reciprocal Rank Fusion for optimal result merging
- **Configurable Alpha**: Tune semantic vs lexical weighting
- **Minimum Similarity Threshold**: Filter low-confidence matches

### Agentic Query Decomposition (Sentinel)

The Sentinel service analyzes incoming queries and intelligently decomposes them:

- **Query Classification**: Keyword, Semantic, Comparison, Aggregation, Navigation
- **Sub-Query Generation**: Breaks complex questions into focused searches
- **Clarification Requests**: Asks for ambiguous queries (configurable threshold)
- **Confidence Scoring**: Tracks decomposition confidence
- **Plan Caching**: 15-minute TTL for repeated queries

### Knowledge Graph (GraphRAG)

- **Entity Extraction**: Person, Organization, Location, Event, Concept
- **Relationship Tracking**: Source → Relationship Type → Target with weights
- **Community Detection**: Louvain algorithm for clustering
- **Community Summarization**: LLM-generated summaries
- **D3.js Visualization**: Interactive graph explorer with pan/zoom

### Conversational AI

- **Multi-Turn Memory**: Persistent conversation state
- **Custom System Prompts**: 4 predefined + custom injection
- **Streaming Responses**: Server-sent events (SSE)
- **Source Citations**: Inline `[N]` references with hover previews
- **Off-Topic Detection**: Filters irrelevant queries in demo mode

### 22-Wave Image Analysis

A modular, signal-based ML pipeline for comprehensive image understanding:

| Wave | Purpose |
|------|---------|
| AdvancedOcrWave | Multi-frame OCR with temporal voting |
| MlOcrWave | ML-based text detection |
| Florence2Wave | Vision foundation model |
| VisionLlmWave | Vision LLM fallback (Claude/GPT-4o) |
| ClipEmbeddingWave | CLIP embeddings for visual search |
| ColorWave | Dominant color extraction (3x3 grid) |
| MotionWave | Optical flow motion detection |
| FaceDetectionWave | Face detection with bounding boxes |
| SceneDetectionWave | Indoor/outdoor/meme classification |
| OcrQualityWave | OCR confidence validation |
| TextDetectionWave | EAST text region detection |
| ExifForensicsWave | EXIF metadata extraction |
| ContradictionWave | Logical consistency checks |
| AutoRoutingWave | Intelligent pipeline selection |
| *...and 8 more* | |

**Execution Profiles**: Fast (~100ms), Balanced, Quality

### Animated GIF/WebP Processing

- **Frame Deduplication**: SSIM-based (0.95 threshold)
- **Subtitle Extraction**: Text-only strip mode
- **Filmstrip Generation**: 30x token reduction for Vision LLMs
- **Temporal Voting**: Multi-frame consensus
- **Motion Intensity Tracking**: Optical flow analysis

### Evidence Artifacts

Structured storage for all extracted data:

```
Evidence Types:
- ocr_text          : Extracted text with confidence
- ocr_word_boxes    : Bounding box coordinates
- llm_summary       : AI-generated summaries
- filmstrip         : Video frame strips
- key_frame         : Representative frames
- transcript        : Audio transcriptions
- signal_dump       : Raw wave outputs
```

### Multi-Tenancy

- **Schema-per-Tenant**: PostgreSQL schema isolation
- **Automatic Provisioning**: Create tenants on first access
- **Domain-Based Routing**: Subdomain or path-based detection
- **Per-Tenant Collections**: Isolated Qdrant collections

### Ingestion Sources

Pull documents from multiple sources:

- **Local Directory**: Recursive with pattern matching
- **GitHub Repositories**: Track commits for incremental sync
- **FTP Servers**: Standard FTP with credentials
- **S3 Buckets**: AWS-compatible object storage

### Web Crawler

```json
POST /api/crawl
{
  "seedUrls": ["https://example.com"],
  "maxDepth": 3,
  "maxPages": 100,
  "contentSelector": "article"
}
```

---

## API Endpoints

| Endpoint | Methods | Description |
|----------|---------|-------------|
| `/api/chat` | POST, GET | Conversational AI with memory |
| `/api/search` | POST | Stateless semantic search |
| `/api/documents` | GET, POST, DELETE | Document management |
| `/api/collections` | CRUD | Collection management |
| `/api/graph` | GET | Knowledge graph data |
| `/api/communities` | GET, POST | Community detection |
| `/api/evidence` | GET | Artifact retrieval |
| `/api/tenants` | CRUD | Multi-tenant management |
| `/api/ingestion` | CRUD | Source management |
| `/api/crawl` | POST, GET | Web crawling |

Full OpenAPI documentation at `/scalar/v1`

---

## Configuration

### Embedding Backends

```json
{
  "DocSummarizer": {
    "EmbeddingBackend": "Onnx",  // Onnx, Ollama, OpenAI, Anthropic
    "BertRag": {
      "VectorStore": "DuckDB",   // DuckDB, Qdrant
      "CollectionName": "ragdocs"
    }
  }
}
```

### LLM Providers

```json
{
  "DocSummarizer": {
    "LlmBackend": "Ollama",
    "Ollama": {
      "BaseUrl": "http://localhost:11434",
      "Model": "qwen2.5:3b"
    }
  }
}
```

Or use cloud providers:

```json
{
  "DocSummarizer": {
    "LlmBackend": "Anthropic",
    "Anthropic": {
      "Model": "claude-3-5-haiku-latest"
    }
  }
}
```

### Demo Mode

For public deployments:

```json
{
  "RagDocuments": {
    "DemoMode": {
      "Enabled": true,
      "BannerMessage": "Demo mode - uploads disabled",
      "MinRelevanceScore": 0.3,
      "OffTopicMessage": "I can only answer questions about the indexed documents."
    }
  }
}
```

---

## CLI Tool

```bash
# Process files (unified pipeline - auto-routes by extension)
lucidrag-cli process document.pdf image.gif data.csv --collection my-docs

# List available pipelines
lucidrag-cli process --list-pipelines

# Force specific pipeline
lucidrag-cli process *.jpg --pipeline image --verbose

# Search
lucidrag-cli search "your query" --collection my-docs

# Interactive chat
lucidrag-cli chat

# Run web server
lucidrag-cli serve --port 5080
```

---

## ImageSummarizer (Standalone Tool)

A powerful standalone image analysis tool with MCP server support for Claude Desktop integration:

```bash
# Install globally
dotnet tool install -g Mostlylucid.ImageSummarizer.Cli

# Extract text
imagesummarizer screenshot.png

# Analyze animated GIF
imagesummarizer animation.gif --pipeline advancedocr

# Run as MCP server
imagesummarizer --mcp
```

### MCP Tools (9 Available)

- `summarize_animated_gif` - GIF motion analysis
- `generate_caption` - Accessibility-optimized captions (WCAG)
- `generate_detailed_description` - Comprehensive analysis
- `analyze_with_template` - Template-based formatting
- `ocr_text` - OCR from images/GIFs
- `analyze_quality` - Quality assessment
- `extract_gif_summary` - GIF summarization
- `guess_intent` - Intent detection
- `list_output_templates` - Available templates

### Output Templates

| Template | Use Case |
|----------|----------|
| social_media | 280 chars max |
| accessibility | WCAG-compliant, 125 chars |
| seo | Keyword-optimized |
| technical_report | Detailed analysis |
| animated_gif_summary | Motion-focused |
| custom | User-defined |

---

## Docker Deployment

```yaml
# docker-compose.yml
services:
  lucidrag:
    image: scottgal/lucidrag:latest
    ports:
      - "5080:5080"
    volumes:
      - ./data:/app/data
      - ./uploads:/app/uploads
    environment:
      - ConnectionStrings__DefaultConnection=Host=db;Database=lucidrag;Username=postgres;Password=postgres
    depends_on:
      - db

  db:
    image: postgres:16
    volumes:
      - postgres_data:/var/lib/postgresql/data
    environment:
      - POSTGRES_DB=lucidrag
      - POSTGRES_PASSWORD=postgres
```

---

## Development

```bash
# Build
dotnet build LucidRAG.sln

# Run with hot reload
dotnet watch run --project src/LucidRAG/LucidRAG.csproj

# Build CSS (Tailwind)
cd src/LucidRAG
npm install
npm run build:css

# Run tests
dotnet test --filter "Category!=Browser"
```

---

## Requirements

| Component | Version |
|-----------|---------|
| .NET SDK | 10.0+ |
| PostgreSQL | 16+ (or SQLite for standalone) |
| Node.js | 18+ (for CSS build) |

**Optional:**
- Ollama - Local LLM inference
- Qdrant - Scalable vector storage
- Docling - Advanced PDF/DOCX conversion

---

## CI/CD

- **build.yml** - PR/push builds with PostgreSQL test containers
- **release-lucidrag.yml** - Docker multi-arch (amd64/arm64) on `lucidrag-v*` tags
- **release-lucidrag-cli.yml** - CLI binary releases
- **release-imagesummarizer.yml** - ImageSummarizer releases
- **publish-docsummarizer-nuget.yml** - NuGet publishing

---

## License

MIT License - see [LICENSE](LICENSE)

---

## Links

- **Demo**: https://lucidrag.com (coming soon)
- **Docker Hub**: https://hub.docker.com/r/scottgal/lucidrag
- **Issues**: https://github.com/scottgal/lucidrag/issues
