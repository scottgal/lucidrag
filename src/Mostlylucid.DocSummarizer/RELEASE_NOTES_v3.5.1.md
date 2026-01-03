# DocSummarizer v3.5.1 - Enhanced Embeddings, Intelligent Retrieval & Multi-Framework

> **Turn documents or URLs into evidence-grounded summaries — for humans or AI agents — without sending anything to the cloud.**

## What's New

### Higher Quality Embedding Models

**New default: `BgeBaseEnV15` (768d)** - 2x better quality than previous `AllMiniLmL6V2` (384d).

8 new embedding models added:

| Model | Dimensions | Context | Use Case |
|-------|-----------|---------|----------|
| `BgeBaseEnV15` | 768 | 512 | **New default** - best quality/speed |
| `BgeLargeEnV15` | 1024 | 512 | Maximum quality |
| `GteBase` | 768 | 512 | Strong MTEB performer |
| `GteLarge` | 1024 | 512 | Top-tier quality |
| `JinaEmbeddingsV2BaseEn` | 768 | **8192** | Long context specialist |
| `SnowflakeArcticEmbedM` | 768 | 512 | Top MTEB retrieval |
| `NomicEmbedTextV15` | 768 | **8192** | Long context + Matryoshka |

```bash
# Use maximum quality model
docsummarizer -f doc.pdf --embedding-model BgeLargeEnV15

# Use long-context model for huge documents
docsummarizer -f doc.pdf --embedding-model JinaEmbeddingsV2BaseEn
```

### Adaptive Sampling for Smaller Documents

New inverse-scaling algorithm ensures smaller documents get higher coverage:

| Document Size | Coverage | Example |
|--------------|----------|---------|
| ≤50 segments | 40-50% | Nearly all content |
| 150-400 segments | 10-20% | 310 segments → 43 retrieved (13.6%) |
| 400-1000 segments | 5-10% | Balanced coverage |
| >1000 segments | 5% | Large document optimization |

**Before**: 310 segments → 16 retrieved (5.2%)
**After**: 310 segments → 43 retrieved (13.6%)

### Cross-Encoder Reranking

New second-stage precision reranker using:
- Exact term overlap with early-match bonus
- Query term density analysis
- Exact phrase matching (huge boost)
- Structural signals (heading/section relevance)
- Embedding similarity integration

Enable with `retrieval.useReranking: true` (default: enabled).

### Document Metadata & arXiv Banner

Automatic metadata extraction and display:
- Detects arXiv IDs from filenames (e.g., `1506.01057v2.pdf`)
- Fetches metadata from arXiv API (title, authors, date, abstract)
- Extracts PDF embedded metadata as fallback
- Displays "sanity banner" to confirm correct document

```
--- Document Metadata ---
Title: A Hierarchical Neural Autoencoder for Paragraphs and Documents
Authors: Jiwei Li, Minh-Thang Luong, Dan Jurafsky
Date: 2015-06-02
ArXiv: 1506.01057
```

### Fully Configurable Scoring Components

All hardcoded scoring values now have sensible defaults but are fully configurable:

**Hierarchical Encoder** (`HierarchicalEncoderConfig`):
- Section context blending weight (default: 15%)
- Per-section-type boosts (introduction: 1.3x, conclusion: 1.25x, results: 1.2x)
- Position boosts (first sentence: 1.2x, last sentence: 1.1x)
- Heading level boosts (H1: 1.15x, H2: 1.1x, H3: 1.05x)

**Cross-Encoder Reranker** (`RerankerConfig`):
- 12 configurable scoring signals
- Term overlap, exact phrase, heading match, density, position weights

**Adaptive Sampling** (`RetrievalConfig`):
- Document size thresholds (50, 150, 400, 1000 segments)
- Coverage percentages per tier (50%, 40%, 20%, 10%, 5%)
- All configurable via JSON config

### Clearer Coverage Labels

Changed from misleading "Coverage: 5%" to:
```
Evidence: 43 segments (13.6% of 310)
Confidence: Medium
```

### .NET 8 + .NET 10 Multi-Targeting

DocSummarizer now builds for **both .NET 8 and .NET 10**:

| Framework | LTS Status | Use Case |
|-----------|------------|----------|
| .NET 8 | LTS (Nov 2026) | Production servers, enterprise environments |
| .NET 10 | Current | Latest performance, new features |

```bash
# Build for .NET 8 LTS
dotnet publish -c Release -r win-x64 -f net8.0

# Build for .NET 10 (latest)
dotnet publish -c Release -r win-x64 -f net10.0
```

## Installation

### Pre-built Binaries

Download from [GitHub Releases](https://github.com/scottgal/mostlylucidweb/releases/tag/docsummarizer-v3.5.1):

| Platform | .NET 8 | .NET 10 |
|----------|--------|---------|
| Windows x64 | `docsummarizer-net8-win-x64.zip` | `docsummarizer-win-x64.zip` |
| Linux x64 | `docsummarizer-net8-linux-x64.tar.gz` | `docsummarizer-linux-x64.tar.gz` |
| macOS ARM64 | `docsummarizer-net8-osx-arm64.tar.gz` | `docsummarizer-osx-arm64.tar.gz` |

### Quick Start

```bash
# 1. Install Ollama (https://ollama.ai)
ollama pull llama3.2:3b && ollama serve

# 2. Run (ONNX models auto-download on first use)
docsummarizer -f document.pdf

# 3. Or use offline mode (no LLM)
docsummarizer -f document.pdf -m Bert
```

## Breaking Changes

**Default embedding model changed**: `AllMiniLmL6V2` → `BgeBaseEnV15`

The new model is ~4x larger (110MB vs 23MB) but produces significantly better quality embeddings. First run will auto-download the new model.

To use the old default: `--embedding-model AllMiniLmL6V2`

## Files Added

```
Services/
├── HierarchicalEncoder.cs      # Section-aware document encoding
├── CrossEncoderReranker.cs     # Precision reranking service

Models/
└── DocumentMetadata.cs         # arXiv/DOI detection and API lookup
```

## Files Modified

```
Config/BackendConfig.cs              # New embedding models, new defaults
Config/DocSummarizerConfig.cs        # UseReranking, adaptive sampling options
Services/Onnx/OnnxModelRegistry.cs   # Model registry expansion
Services/BertRagSummarizer.cs        # Adaptive sampling, reranking integration
Services/OutputFormatter.cs          # Clearer coverage labels
Services/DocumentSummarizer.cs       # Metadata extraction and display
Mostlylucid.DocSummarizer.csproj     # Multi-targeting net8.0;net10.0
```

## Test Coverage

- **276 tests passing** across all embedding models and configurations
- Tests for new model defaults (BgeBaseEnV15, 512 sequence length)
- Tests for long-context models (Jina, Nomic with 8192 tokens)
- Tests for models without quantized variants
- Tests for HuggingFace repo validation (Xenova/, nomic-ai/)

## Documentation

- **README**: [Mostlylucid.DocSummarizer/README.md](https://github.com/scottgal/mostlylucidweb/blob/main/Mostlylucid.DocSummarizer/README.md)
- **CHANGELOG**: [CHANGELOG.md](https://github.com/scottgal/mostlylucidweb/blob/main/Mostlylucid.DocSummarizer/CHANGELOG.md)
- **Blog Series**:
  - [Part 1: Architecture](/blog/building-a-document-summarizer-with-rag)
  - [Part 2: Quick-Start](/blog/docsummarizer-tool)
  - [Part 3: Advanced Concepts](/blog/docsummarizer-advanced-concepts)

## Contributing

Issues, questions, or contributions welcome at [scottgal/mostlylucidweb](https://github.com/scottgal/mostlylucidweb)

## License

MIT License - See project repository for details

---

**Full Changelog**: [v3.1.0...v3.5.1](https://github.com/scottgal/mostlylucidweb/compare/docsummarizer-v3.1.0...docsummarizer-v3.5.1)
