# DocSummarizer v3.1.0 - Documentation Improvements & Template Expansion

> **Turn documents or URLs into evidence-grounded summaries — for humans or AI agents — without sending anything to the cloud.**

## 🎉 What's New

### 📚 Comprehensive Documentation Series

Three detailed blog articles now cover every aspect of DocSummarizer:

- **[Part 1: Architecture & Patterns](/blog/building-a-document-summarizer-with-rag)** - Why pipeline beats naive LLM, mode selection, design principles
- **[Part 2: Quick-Start Guide](/blog/docsummarizer-tool)** - Installation, templates, common workflows
- **[Part 3: Technical Deep Dive](/blog/docsummarizer-advanced-concepts)** - BERT, ONNX, embeddings, hybrid search internals

### ✨ Two New Templates (Total: 13)

**`prose`** - Clean multi-paragraph summary without metadata:
```bash
docsummarizer -f doc.pdf -t prose
```
- 400 words across 4 paragraphs
- No citations, no metadata, just flowing prose
- Perfect for embedding in reports or presentations

**`strict`** - Token-efficient with hard constraints:
```bash
docsummarizer -f doc.pdf -t strict
```
- Exactly 3 bullets, ≤60 words total
- No hedging ("appears to", "seems", "possibly")
- Highest-confidence facts only
- Optimized for token-constrained contexts

**All 13 Templates:**
default, **prose**, brief, oneliner, bullets, executive, detailed, technical, academic, citations, bookreport, meeting, **strict**

### 🔧 Documentation Improvements

- ✅ **Terminology Consistency**: Standardized "BertRag" across all docs
- ✅ **Accurate Claims**: "validated citations" (not "perfect") to avoid overselling
- ✅ **Mode Corrections**: Updated legacy mode references to current production modes
- ✅ **Performance Verified**: All benchmarks validated against source code
- ✅ **Feature Complete**: Every user-facing feature now documented

## 📥 Installation

### Pre-built Binaries

Download from [GitHub Releases](https://github.com/scottgal/mostlylucidweb/releases/tag/docsummarizer-v3.1.0):

| Platform | Architecture | Download |
|----------|--------------|----------|
| Windows | x64 | `docsummarizer-win-x64.zip` |
| Windows | ARM64 | `docsummarizer-win-arm64.zip` |
| Linux | x64 | `docsummarizer-linux-x64.tar.gz` |
| Linux | ARM64 | `docsummarizer-linux-arm64.tar.gz` |
| macOS | Intel | `docsummarizer-osx-x64.tar.gz` |
| macOS | Apple Silicon | `docsummarizer-osx-arm64.tar.gz` |

### Quick Start

```bash
# 1. Install Ollama (https://ollama.ai)
ollama pull llama3.2:3b && ollama serve

# 2. Run (ONNX models auto-download on first use)
docsummarizer -f document.pdf
```

### Offline Mode (No LLM)

```bash
# Pure BERT extraction - works completely offline
docsummarizer -f document.pdf -m Bert
```

## 🚀 Key Features

- **🤖 Auto Mode**: Smart mode selection based on document and query
- **⚡ Bert Mode**: Pure extractive - no LLM, ~3-5s, works offline
- **🔬 BertRag Pipeline**: Production-grade BERT→retrieval→LLM synthesis
- **📋 13 Templates**: From one-liners to comprehensive 1000-word analyses
- **🛠️ Tool Mode**: Structured JSON for AI agents, MCP servers, CI pipelines
- **🌐 Web Fetching**: Security-hardened (SSRF protection, HTML sanitization)
- **🎭 Playwright**: Headless browser for JavaScript-rendered pages
- **💾 ONNX Embeddings**: Zero-config local embeddings, auto-download models
- **📖 Large Docs**: Handles 500+ pages with hierarchical processing
- **🔒 Local Only**: Nothing leaves your machine

## 📖 Usage Examples

```bash
# Fast offline summary
docsummarizer -f doc.pdf -m Bert

# Production mode with validated citations
docsummarizer -f contract.pdf -m BertRag

# Clean prose for reports
docsummarizer -f whitepaper.pdf -t prose

# Token-efficient for APIs
docsummarizer -f spec.md -t strict

# JSON for agents
docsummarizer tool -f doc.pdf | jq '.summary.keyFacts'

# Batch processing
docsummarizer -d ./documents -m BertRag -o Markdown --output-dir ./summaries
```

## 🐛 Bug Fixes

- Fixed inconsistent mode naming in documentation
- Corrected template count from 11 to 13
- Fixed terminology inconsistencies (BertRag vs BERT-RAG)

## 🔄 Upgrade from v3.0.0

No breaking changes! Just download and run:

```bash
# Check version
docsummarizer --version

# Verify dependencies
docsummarizer check --verbose

# Try new templates
docsummarizer -f doc.pdf -t prose
docsummarizer -f doc.pdf -t strict
```

## 📚 Documentation

- **README**: [Mostlylucid.DocSummarizer/README.md](https://github.com/scottgal/mostlylucidweb/blob/main/Mostlylucid.DocSummarizer/README.md)
- **Blog Series**:
  - [Part 1: Architecture](/blog/building-a-document-summarizer-with-rag)
  - [Part 2: Quick-Start](/blog/docsummarizer-tool)
  - [Part 3: Advanced Concepts](/blog/docsummarizer-advanced-concepts)
- **CHANGELOG**: [CHANGELOG.md](https://github.com/scottgal/mostlylucidweb/blob/main/Mostlylucid.DocSummarizer/CHANGELOG.md)

## 🤝 Contributing

Issues, questions, or contributions welcome at [scottgal/mostlylucidweb](https://github.com/scottgal/mostlylucidweb)

## 📝 License

MIT License - See project repository for details

---

**Full Changelog**: [v3.0.0...v3.1.0](https://github.com/scottgal/mostlylucidweb/compare/docsummarizer-v3.0.0...docsummarizer-v3.1.0)
