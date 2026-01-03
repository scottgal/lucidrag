# LucidRAG

A standalone multi-document RAG (Retrieval-Augmented Generation) web application with GraphRAG entity extraction, knowledge graph visualization, and web crawling.

**Website:** [lucidrag.com](https://lucidrag.com) | **Docker Hub:** [scottgal/lucidrag](https://hub.docker.com/r/scottgal/lucidrag)

## Features

- **Multi-Document Upload**: Support for PDF, DOCX, Markdown, TXT, and HTML files
- **Web Crawling**: Crawl websites with CSS selectors for content extraction, robots.txt compliance
- **Agentic RAG**: Deterministic query planning with bounded LLM steps (decomposition → retrieval → synthesis), including clarification loops when confidence is low
- **GraphRAG Entity Extraction**: Automatic extraction of entities and relationships using IDF-based heuristics and BERT embeddings
- **Knowledge Graph Visualization**: Interactive exploration with depth-limited subgraphs (max 2 hops, entity-type filtering) to prevent visual overload on large corpora
- **Evidence View**: Sentence-level grounding showing exactly which parts of source documents support each answer
- **Conversation Memory**: Chat sessions maintain context across multiple questions
- **Standalone Deployment**: Single executable with SQLite for portable use, or PostgreSQL for production

**What the LLM does NOT do**: The LLM is never used for entity extraction, indexing, or storage — only for reasoning over retrieved, evidence-backed context. All preprocessing is deterministic and inspectable.

---

## Deployment Scenarios

### 1. Minimal (Raspberry Pi / ARM / Air-Gapped)

**Use case**: Single-user, offline/air-gapped environments, low-resource devices

**Requirements**: 2GB RAM, ARM64 or x64

```bash
# Download and run standalone
dotnet run --project Mostlylucid.RagDocuments -- --standalone
```

Or with Docker:

```yaml
# docker-compose.minimal.yml
services:
  lucidrag:
    image: scottgal/lucidrag:latest
    ports:
      - "5080:8080"
    volumes:
      - ./data:/app/data
      - ./uploads:/app/uploads
    environment:
      # Uses SQLite + DuckDB internally, no external services needed
      - ASPNETCORE_ENVIRONMENT=Production
```

**What you get**:
- SQLite for metadata (no PostgreSQL needed)
- DuckDB for vectors (no Qdrant needed)
- ONNX embeddings (no external APIs)
- Heuristic entity extraction (no LLM needed)
- Markdown/TXT/HTML file support

**What you don't get**:
- PDF/DOCX conversion (requires Docling)
- LLM-enhanced answers (requires Ollama)
- Hybrid/LLM extraction modes

**Connect to external Ollama** (optional):
```yaml
environment:
  - DocSummarizer__Ollama__BaseUrl=http://192.168.1.100:11434
  - DocSummarizer__Ollama__Model=llama3.2:3b
```

---

### 2. Typical Self-Hoster

**Use case**: Personal knowledge base, home server, small team

**Requirements**: 4GB RAM, x64

```yaml
# docker-compose.yml
services:
  lucidrag:
    image: scottgal/lucidrag:latest
    ports:
      - "5080:8080"
    environment:
      - ConnectionStrings__DefaultConnection=Host=postgres;Database=ragdocs;Username=postgres;Password=${POSTGRES_PASSWORD}
      - DocSummarizer__Ollama__BaseUrl=http://host.docker.internal:11434
    extra_hosts:
      - "host.docker.internal:host-gateway"
    depends_on:
      - postgres
    volumes:
      - lucidrag_uploads:/app/uploads
      - lucidrag_data:/app/data

  postgres:
    image: postgres:16-alpine
    environment:
      - POSTGRES_PASSWORD=${POSTGRES_PASSWORD}
      - POSTGRES_DB=ragdocs
    volumes:
      - postgres_data:/var/lib/postgresql/data

volumes:
  lucidrag_uploads:
  lucidrag_data:
  postgres_data:
```

**On the host machine**:
```bash
# Install Ollama from https://ollama.ai
ollama pull llama3.2:3b
ollama serve
```

**What you get**:
- PostgreSQL for reliable metadata storage
- Full-text search with PostgreSQL
- LLM-powered chat responses
- All extraction modes (Heuristic, Hybrid, LLM)
- Web crawling

**Optional additions**:
```yaml
  # Add Docling for PDF/DOCX conversion
  docling:
    image: quay.io/docling-project/docling-serve:latest
    ports:
      - "5001:5001"

  # Add Qdrant for persistent vector storage
  qdrant:
    image: qdrant/qdrant:latest
    volumes:
      - qdrant_data:/qdrant/storage
```

---

### 3. Enterprise / Production

**Use case**: Multi-user, high availability, GPU-accelerated

**Requirements**: 16GB+ RAM, GPU recommended for Docling

```yaml
# docker-compose.production.yml
services:
  lucidrag:
    image: scottgal/lucidrag:latest
    ports:
      - "5080:8080"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ConnectionStrings__DefaultConnection=Host=postgres;Database=ragdocs;Username=postgres;Password=${POSTGRES_PASSWORD}
      - DocSummarizer__Ollama__BaseUrl=http://host.docker.internal:11434
      - DocSummarizer__Docling__BaseUrl=http://docling:5001
      - DocSummarizer__Qdrant__Host=qdrant
      - DocSummarizer__Qdrant__Port=6334
      - DocSummarizer__BertRag__VectorStore=Qdrant
      - RagDocuments__RequireApiKey=true
      - RagDocuments__ApiKey=${LUCIDRAG_API_KEY}
    extra_hosts:
      - "host.docker.internal:host-gateway"
    depends_on:
      postgres:
        condition: service_healthy
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8080/healthz"]
      interval: 30s
      timeout: 10s
      retries: 3

  postgres:
    image: postgres:16-alpine
    environment:
      - POSTGRES_PASSWORD=${POSTGRES_PASSWORD}
      - POSTGRES_DB=ragdocs
    volumes:
      - postgres_data:/var/lib/postgresql/data
    restart: unless-stopped
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres"]
      interval: 10s
      retries: 5

  qdrant:
    image: qdrant/qdrant:latest
    volumes:
      - qdrant_data:/qdrant/storage
    restart: unless-stopped

  docling:
    image: quay.io/docling-project/docling-serve:latest
    restart: unless-stopped
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: 1
              capabilities: [gpu]

volumes:
  postgres_data:
  qdrant_data:
  lucidrag_uploads:
  lucidrag_data:
```

**What you get**:
- Full PDF/DOCX conversion with GPU acceleration
- Persistent vector storage with Qdrant
- API key authentication
- Health checks for orchestration
- All features enabled

---

## Quick Start

### Standalone Mode (No Dependencies)

```bash
dotnet run --project Mostlylucid.RagDocuments -- --standalone
```

This starts the app on `http://localhost:5080` with:
- SQLite database (stored in `data/ragdocs.db`)
- DuckDB vector store (stored in `data/`)
- Local file uploads (stored in `uploads/`)

### Docker Deployment

```bash
# Pull and run
docker pull scottgal/lucidrag:latest
docker run -p 5080:8080 scottgal/lucidrag:latest

# Or with docker-compose
docker-compose -f docker-compose.production.yml up -d
```

**Note:** Ollama is NOT included in the compose file. Install it on your host machine:
```bash
# Install from https://ollama.ai, then:
ollama pull llama3.2:3b
ollama serve
```

LucidRAG will connect to Ollama at `host.docker.internal:11434`.

---

## Design Principles

1. **Deterministic preprocessing** — Chunking, embedding, and entity extraction use fixed algorithms, not LLM calls. Results are reproducible.

2. **Evidence-first** — Every answer cites specific source segments. No hallucinated claims.

3. **Inspectable pipelines** — All intermediate state (chunks, embeddings, entities, relationships) is queryable and debuggable.

4. **Local-first execution** — ONNX embeddings, DuckDB storage, optional Ollama. No mandatory cloud dependencies.

5. **Bounded LLM usage** — The LLM synthesizes answers from retrieved context. It doesn't index, extract, or store anything.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        RagDocuments UI                          │
│                    (HTMX + Alpine.js + TailwindCSS)            │
├─────────────────────────────────────────────────────────────────┤
│                         REST API                                │
│  /api/documents  /api/chat  /api/graph  /api/collections       │
│  /api/search     /api/crawl /api/config                        │
├─────────────────┬───────────────────┬───────────────────────────┤
│   DocSummarizer │   EntityGraph     │   ConversationService    │
│   (RAG Engine)  │   (GraphRAG)      │   (Chat Memory)          │
├─────────────────┼───────────────────┼───────────────────────────┤
│   DuckDB/Qdrant │   DuckDB          │   PostgreSQL/SQLite      │
│   (Vectors)     │   (Entity Graph)  │   (Metadata)             │
└─────────────────┴───────────────────┴───────────────────────────┘
```

**Why DuckDB?** DuckDB is used for vector storage to keep indexing local, fast, and inspectable without introducing an external vector database dependency. It's ephemeral by design — you can always rebuild it from source documents.

### Key Components

| Component | Description |
|-----------|-------------|
| `DocumentProcessingService` | Handles file upload, validation, and queue management |
| `DocumentQueueProcessor` | Background service that processes documents through DocSummarizer |
| `WebCrawlerService` | Crawls websites with BFS, robots.txt compliance, CSS selectors |
| `EntityGraphService` | Extracts entities using GraphRag's IDF + BERT heuristics |
| `AgenticSearchService` | Multi-step RAG with query decomposition and self-correction |
| `ConversationService` | Manages chat sessions with memory and context |

---

## API Endpoints

### Documents

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/documents/upload` | Upload a single document |
| POST | `/api/documents/upload-batch` | Upload multiple documents |
| GET | `/api/documents` | List all documents |
| GET | `/api/documents/{id}` | Get document details |
| GET | `/api/documents/{id}/status` | SSE stream of processing progress |
| DELETE | `/api/documents/{id}` | Delete a document (with vector cleanup) |
| GET | `/api/documents/demo-status` | Check if demo mode is enabled |

### Web Crawling

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/crawl` | Start a new crawl job |
| GET | `/api/crawl` | List all crawl jobs |
| GET | `/api/crawl/{id}` | Get crawl job details |
| GET | `/api/crawl/{id}/status` | SSE stream of crawl progress |

**Crawl Request**:
```json
{
  "seedUrls": ["https://example.com/docs"],
  "contentSelector": "article",
  "maxPages": 50,
  "maxDepth": 3,
  "collectionId": "optional-guid"
}
```

**Features**:
- Same-site crawling only (respects domain boundaries)
- robots.txt compliance
- CSS selector for content extraction (or automatic detection)
- Configurable rate limiting
- Two-phase crawl: quick link discovery, then content download

### Search (Standalone)

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/search` | Hybrid search (BM25 + BERT), returns segments |
| POST | `/api/search/answer` | Search with LLM-synthesized answer (stateless) |

### Chat (Conversational)

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/chat` | Send a message (creates new conversation) |
| POST | `/api/chat/stream` | Stream response via SSE |
| GET | `/api/chat/conversations` | List all conversations |
| GET | `/api/chat/conversations/{id}` | Get conversation history |
| DELETE | `/api/chat/conversations/{id}` | Delete conversation |

### Graph

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/graph` | Get full graph data (D3.js format) |
| GET | `/api/graph/stats` | Get graph statistics |
| GET | `/api/graph/subgraph/{entityId}` | Get entity-centered subgraph (max 2 hops) |
| GET | `/api/graph/entities` | Search entities by name/type |
| GET | `/api/graph/entities/{id}` | Get entity details with relationships |
| GET | `/api/graph/paths` | Find paths between two entities |

### Collections

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/collections` | List collections with stats |
| POST | `/api/collections` | Create collection |
| GET | `/api/collections/{id}` | Get collection with documents |
| PUT | `/api/collections/{id}` | Update collection name/description/settings |
| DELETE | `/api/collections/{id}` | Delete collection (cascades to documents) |
| POST | `/api/collections/{id}/documents` | Add documents to collection |
| DELETE | `/api/collections/{id}/documents` | Remove documents from collection |

### Config (Capabilities & Modes)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/config/capabilities` | Get detected services and available features |
| GET | `/api/config/extraction-modes` | Get available extraction modes for UI dropdown |
| PUT | `/api/config/extraction-mode` | Set extraction mode (Heuristic/Hybrid/LLM) |

**Extraction Modes:**
- **Heuristic** (default): Fast, no LLM calls - uses IDF + structural signals
- **Hybrid**: Heuristic candidates + LLM enhancement per document
- **LLM**: Full MSFT GraphRAG style - 2 LLM calls per chunk (requires Ollama)

---

## Configuration

### appsettings.json

```json
{
  "RagDocuments": {
    "RequireApiKey": false,
    "ApiKey": "",
    "UploadPath": "./uploads",
    "MaxFileSizeMB": 100,
    "AllowedExtensions": [".pdf", ".docx", ".md", ".txt", ".html"],
    "ExtractionMode": "Heuristic",
    "Crawler": {
      "UserAgent": "LucidRAG/1.0 (+https://github.com/scottgal/mostlylucidweb)",
      "RequestDelayMs": 1000,
      "TimeoutSeconds": 30,
      "RespectRobotsTxt": true
    }
  },
  "DocSummarizer": {
    "EmbeddingBackend": "Onnx",
    "BertRag": {
      "VectorStore": "DuckDB",
      "PersistVectors": true
    },
    "Ollama": {
      "BaseUrl": "http://localhost:11434",
      "Model": "llama3.2:3b"
    }
  },
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=ragdocs;Username=postgres;Password=..."
  }
}
```

### Environment Variables

| Variable | Description |
|----------|-------------|
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection string |
| `DocSummarizer__Ollama__BaseUrl` | Ollama API URL |
| `DocSummarizer__Ollama__Model` | LLM model to use |
| `DocSummarizer__Docling__BaseUrl` | Docling API URL for PDF/DOCX |
| `DocSummarizer__Qdrant__Host` | Qdrant hostname |
| `DocSummarizer__Qdrant__Port` | Qdrant gRPC port (default: 6334) |
| `DocSummarizer__BertRag__VectorStore` | Vector store backend: `Qdrant`, `DuckDB`, or `InMemory` |
| `RagDocuments__RequireApiKey` | Enable API key authentication |
| `RagDocuments__ApiKey` | The API key (if required) |
| `RagDocuments__Crawler__UserAgent` | Custom User-Agent for web crawling |
| `RagDocuments__DemoMode__Enabled` | Enable demo mode (read-only) |

---

## Document Processing Pipeline

1. **Upload/Crawl**: File is validated, hashed, and stored
2. **Queue**: Document is added to background processing queue
3. **Chunking**: DocSummarizer splits document into semantic segments
4. **Embedding**: ONNX BERT model generates embeddings for each segment
5. **Indexing**: Segments stored in DuckDB/Qdrant with HNSW vector index
6. **Entity Extraction**: GraphRag extracts entities using:
   - IDF-based term importance (rare terms = likely entities)
   - Structural signals (headings, code blocks, links)
   - BERT embedding deduplication
   - Co-occurrence relationship detection

---

## Web Crawling

The web crawler enables you to ingest entire websites into LucidRAG:

### How It Works

1. **Discovery Phase**: Fast BFS traversal finding all same-site links
2. **Download Phase**: Fetch pages with rate limiting, extract content
3. **Processing**: Each page queued as a document for embedding

### CSS Selectors

Specify a CSS selector to extract only the main content:

```json
{
  "seedUrls": ["https://blog.example.com"],
  "contentSelector": "article.post-content"
}
```

**Default fallback chain** (if no selector provided):
1. `article`
2. `main`
3. `[role="main"]`
4. `.content`, `#content`
5. `.post-content`, `.entry-content`
6. `body`

### robots.txt Compliance

The crawler respects `robots.txt` by default:
- Checks each URL against Disallow rules
- Respects specific rules for `LucidRAG` user-agent
- Caches robots.txt per host

---

## Entity Extraction

The GraphRAG integration uses a hybrid approach:

1. **Heuristic Candidate Detection**: Fast, deterministic extraction using:
   - IDF scores (terms rare across corpus)
   - Markdown structure (headings, inline code)
   - Link text and targets
   - PascalCase identifiers

2. **BERT Deduplication**: Merges similar entities using embedding similarity

3. **Relationship Building**:
   - Co-occurrence (entities in same segment)
   - Explicit links (markdown links between documents)
   - Structural hierarchy (heading → content relationships)

---

## UI Features

### Chat Interface
- Real-time streaming responses
- Source citations with confidence scores
- Three view modes: Answer, Evidence, Graph

### Evidence View
- Side-by-side answer and sources
- Sentence-level highlighting
- Click to expand source context

### Graph View
- D3.js force-directed visualization
- Color-coded entity types
- Interactive exploration

### Content Sources
- **Upload Tab**: Drag-and-drop file upload (FilePond)
- **Crawl Tab**: Enter URLs, CSS selector, start crawl with live progress

---

## Development

### Prerequisites

- .NET 10.0 SDK
- Node.js 18+ (for frontend build)
- PostgreSQL 16 (optional, SQLite works for development)
- Ollama (optional, for LLM features)

### Build

```bash
# Backend
dotnet build

# Frontend (TailwindCSS + Alpine.js)
cd Mostlylucid.RagDocuments
npm install
npm run build
```

### Run Tests

```bash
dotnet test Mostlylucid.RagDocuments.Tests
```

### Puppeteer UI Tests

```bash
cd Mostlylucid.RagDocuments
node puppeteer-screenshot.js
```

---

## Publishing

### Single Executable

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Supported runtimes: `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`

### Docker

```bash
docker build -t ragdocuments -f Mostlylucid.RagDocuments/Dockerfile .
```

---

## Project Structure

```
Mostlylucid.RagDocuments/
├── Controllers/
│   ├── Api/
│   │   ├── DocumentsController.cs
│   │   ├── ChatController.cs
│   │   ├── GraphController.cs
│   │   ├── CollectionsController.cs
│   │   ├── CrawlController.cs      # Web crawling API
│   │   └── ConfigController.cs
│   └── UI/
│       └── HomeController.cs
├── Services/
│   ├── DocumentProcessingService.cs
│   ├── ConversationService.cs
│   ├── AgenticSearchService.cs
│   ├── EntityGraphService.cs
│   ├── WebCrawlerService.cs        # Web crawler
│   └── Background/
│       └── DocumentQueueProcessor.cs
├── Config/
│   └── RagDocumentsConfig.cs       # Config with CrawlerConfig
├── Models/
│   └── CrawlModels.cs              # Crawl request/response
├── Data/
│   └── RagDocumentsDbContext.cs
├── Entities/
│   └── DocumentEntity.cs           # Includes SourceUrl
├── Views/
├── wwwroot/
├── Program.cs
├── appsettings.json
├── Dockerfile
└── docker-compose.production.yml
```

---

## Dependencies

| Package | Purpose |
|---------|---------|
| Mostlylucid.DocSummarizer.Core | RAG pipeline, embeddings, vector store |
| Mostlylucid.GraphRag | Entity extraction, knowledge graph |
| AngleSharp | HTML parsing for web crawler |
| Entity Framework Core | Database access (PostgreSQL/SQLite) |
| Serilog | Structured logging |
| HTMX | Server-driven UI interactions |
| Alpine.js | Lightweight reactive UI |
| TailwindCSS + DaisyUI | Styling |

---

## License

MIT License - See LICENSE file for details.
