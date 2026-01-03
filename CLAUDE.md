# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

LucidRAG is a multi-document Agentic RAG (Retrieval-Augmented Generation) system with GraphRAG-style entity extraction and knowledge graph visualization. Built with .NET 10.0, it supports uploading PDFs, DOCX, Markdown, HTML, and TXT documents for AI-powered semantic search and conversation.

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

Six-project solution with layered dependencies:

```
LucidRAG (web)          → ASP.NET Core 10 + Razor/HTMX + Tailwind/DaisyUI
LucidRAG.Cli            → System.CommandLine CLI wrapping web services
LucidRAG.Tests          → xUnit + PostgreSQL test containers
Mostlylucid.DocSummarizer.Core → NuGet package: ONNX embeddings, document parsing, BM25
Mostlylucid.GraphRag    → Entity/relationship extraction pipeline
Mostlylucid.RAG         → Vector store abstraction (DuckDB/Qdrant backends)
```

**Dependency flow:** Web/CLI → GraphRag/RAG → DocSummarizer.Core

## Key Directories

- `src/LucidRAG/Controllers/Api/` - REST endpoints (Chat, Search, Documents, Graph, Config)
- `src/LucidRAG/Services/` - Core services: DocumentProcessingService, ConversationService, AgenticSearchService, EntityGraphService
- `src/LucidRAG/Data/` - EF Core DbContext and migrations
- `src/Mostlylucid.DocSummarizer.Core/Services/BertRagSummarizer.cs` - Core AI/embedding logic
- `src/Mostlylucid.GraphRag/GraphRagPipeline.cs` - Entity extraction orchestration

## Configuration

Primary configuration in `src/LucidRAG/appsettings.json`:

- `ConnectionStrings:DefaultConnection` - PostgreSQL (SQLite in standalone mode)
- `DocSummarizer:EmbeddingBackend` - "Onnx" (default), "Ollama", or "Docling"
- `DocSummarizer:BertRag:VectorStore` - "DuckDB" (default) or "Qdrant"
- `DocSummarizer:Ollama:BaseUrl/Model` - LLM backend for chat
- `Prompts` section - Query clarification, decomposition, self-correction toggles

## Storage Backends

- **PostgreSQL 16+** - Production database (EF Core)
- **SQLite** - Standalone/CLI mode
- **DuckDB** - Default vector embeddings
- **Qdrant** - Optional scalable vector store
- **ONNX** - Local embeddings (all-MiniLM-L6-v2, no API keys needed)

## CI/CD

- `.github/workflows/build.yml` - PR/push: builds + tests with PostgreSQL
- `.github/workflows/release-lucidrag.yml` - Docker multi-arch (amd64/arm64) on `lucidrag-v*` tags
- `.github/workflows/release-lucidrag-cli.yml` - CLI binary releases
- `.github/workflows/publish-docsummarizer-nuget.yml` - NuGet publishing

## Testing Notes

Tests use Testcontainers.PostgreSql for integration tests. Browser tests (PuppeteerSharp) are excluded in CI with `--filter "Category!=Browser"`.
