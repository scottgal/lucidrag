# Changelog - DocSummarizer

## v4.0.0 - Core Library Refactoring & DI Architecture (2025-12-28)

### Major Changes

This release represents a major architectural refactoring, extracting all document processing logic into a reusable **Mostlylucid.DocSummarizer.Core** NuGet library. The CLI is now a thin wrapper that uses Core via dependency injection.

### Architecture

```
CLI (thin wrapper) → IDocumentSummarizer (DI) → Core services
```

**Before**: ~40 service files duplicated between CLI and any consumers
**After**: Single Core library with `IDocumentSummarizer` interface for all consumers

### Breaking Changes

- **Namespace restructured**: All Core types now live in `Mostlylucid.DocSummarizer.*` namespace (shared between CLI and Core)
- **DI-first architecture**: Services now registered via `services.AddDocSummarizer()` extension method
- **IVectorStore interface updated**: Now implements both `IDisposable` and `IAsyncDisposable` for proper container disposal

### New Features

#### Mostlylucid.DocSummarizer.Core NuGet Package

A standalone library for document summarization that can be consumed by any .NET application:

```csharp
// Register services
services.AddDocSummarizer(config =>
{
    config.Ollama.Model = "llama3.2:3b";
    config.EmbeddingBackend = EmbeddingBackend.Onnx;
});

// Inject and use
public class MyService(IDocumentSummarizer summarizer)
{
    public async Task<string> Summarize(string markdown)
    {
        var result = await summarizer.SummarizeMarkdownAsync(markdown);
        return result.ExecutiveSummary;
    }
}
```

#### IDocumentSummarizer Interface

Clean, DI-friendly API with comprehensive functionality:

- `SummarizeMarkdownAsync()` - Summarize markdown content
- `SummarizeFileAsync()` - Summarize from file path (PDF, DOCX, MD, etc.)
- `SummarizeUrlAsync()` - Fetch and summarize web content
- `QueryAsync()` - Query documents with natural language
- `ExtractSegmentsAsync()` - Extract structured segments with embeddings
- `Template` property - Apply summary templates (brief, executive, detailed, etc.)

### Improvements

- **Proper disposal**: All vector stores (`DuckDbVectorStore`, `QdrantVectorStore`, `InMemoryVectorStore`) now implement both `IDisposable` and `IAsyncDisposable`
- **101 tests passing**: Comprehensive test coverage for Core library
- **CLI simplified**: Program.cs reduced from top-level statements to proper `Program` class with explicit `Main` method
- **Enhanced `tool` command**: Comprehensive JSON output for LLM integration with:
  - Entity extraction (people, organizations, locations, dates, events)
  - Source chunk references for citation tracking
  - Processing metadata (coverage, citation rate, timing, model)
  - Q&A mode via `--ask` flag with evidence segments

### Files Added (Core Library)

```
Mostlylucid.DocSummarizer.Core/
├── IDocumentSummarizer.cs              # Public API interface
├── Extensions/
│   └── ServiceCollectionExtensions.cs  # AddDocSummarizer() registration
├── Services/
│   └── DocumentSummarizerService.cs    # IDocumentSummarizer implementation
└── (all other services moved from CLI)
```

### Files Removed (from CLI)

~40 service files removed from CLI - now consumed from Core library:
- `Services/Onnx/*.cs` (tokenizer, embedding, model registry)
- `Services/Utilities/*.cs` (vector math, word lists, response cleaner)
- `Services/*.cs` (all summarizers, extractors, vector stores)
- `Config/*.cs` (configuration classes)
- `Models/*.cs` (document models)

### Migration Guide

**For CLI users**: No changes required - the CLI works exactly as before.

**For library consumers**: 

1. Add reference to `Mostlylucid.DocSummarizer.Core`
2. Register services: `services.AddDocSummarizer()`
3. Inject `IDocumentSummarizer` where needed

```csharp
// Old way (direct instantiation)
var summarizer = new DocumentSummarizer(model, doclingUrl, ...);

// New way (DI)
services.AddDocSummarizer();
// Then inject IDocumentSummarizer
```

---

## v3.5.1 - .NET 8 Compatibility (2025-12-28)

### Improvements

- Added .NET 8 LTS support alongside .NET 10 for broader environment compatibility
- 276 tests passing across all embedding models and configurations
- Fixed test assumptions for new model defaults (BgeBaseEnV15)

### Breaking Changes

None - this is a compatibility release.

---

## v3.2.0 - Enhanced Embeddings & Intelligent Retrieval (2025-12-28)

### Major Improvements

#### Higher Quality Embedding Models

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
# Use new default (auto)
docsummarizer -f doc.pdf

# Use maximum quality model
docsummarizer -f doc.pdf --embedding-model BgeLargeEnV15

# Use long-context model for huge documents
docsummarizer -f doc.pdf --embedding-model JinaEmbeddingsV2BaseEn
```

#### Adaptive Sampling for Smaller Documents

New inverse-scaling algorithm ensures smaller documents get higher coverage:

| Document Size | Coverage | Example |
|--------------|----------|---------|
| ≤50 segments | 40-50% | Nearly all content |
| 150-400 segments | 10-20% | 310 segments → 43 retrieved (13.6%) |
| 400-1000 segments | 5-10% | Balanced coverage |
| >1000 segments | 5% | Large document optimization |

**Before**: 310 segments → 16 retrieved (5.2%)
**After**: 310 segments → 43 retrieved (13.6%)

#### Cross-Encoder Reranking

New second-stage precision reranker using:
- Exact term overlap with early-match bonus
- Query term density analysis
- Exact phrase matching (huge boost)
- Structural signals (heading/section relevance)
- Embedding similarity integration

Enable with `retrieval.useReranking: true` (default: enabled).

#### Document Metadata & arXiv Banner

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

#### Fully Configurable Scoring Components

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

#### Clearer Coverage Labels

Changed from misleading "Coverage: 5%" to:
```
Evidence: 43 segments (13.6% of 310)
Confidence: Medium
```

### Files Added

```
Services/
├── HierarchicalEncoder.cs      # Section-aware document encoding
├── CrossEncoderReranker.cs     # Precision reranking service

Models/
└── DocumentMetadata.cs         # arXiv/DOI detection and API lookup
```

### Files Modified

```
Config/BackendConfig.cs              # New embedding models, new defaults
Config/DocSummarizerConfig.cs        # UseReranking option
Services/Onnx/OnnxModelRegistry.cs   # Model registry expansion
Services/BertRagSummarizer.cs        # Adaptive sampling, reranking integration
Services/OutputFormatter.cs          # Clearer coverage labels
Services/DocumentSummarizer.cs       # Metadata extraction and display
Models/Segment.cs                    # UseReranking config property
README.md                            # Documentation updates
```

### Breaking Changes

**Default embedding model changed**: `AllMiniLmL6V2` → `BgeBaseEnV15`

The new model is ~4x larger (110MB vs 23MB) but produces significantly better quality embeddings. First run will auto-download the new model.

To use the old default: `--embedding-model AllMiniLmL6V2`

---

## v3.1.0 - Documentation Improvements & Template Expansion (2025-12-18)

### Documentation Updates

#### Comprehensive Blog Series

Three detailed blog articles covering DocSummarizer architecture, usage, and internals:

- **[Part 1: Building a Document Summarizer with RAG](/blog/building-a-document-summarizer-with-rag)** - Architecture, design patterns, and why structure-first beats naive LLM calls
- **[Part 2: Using the Tool](/blog/docsummarizer-tool)** - Quick-start guide, modes, templates, and common workflows
- **[Part 3: Advanced Concepts](/blog/docsummarizer-advanced-concepts)** - Deep dive into BERT, ONNX, embeddings, hybrid search, and the BertRag pipeline

#### Documentation Improvements

- **Terminology Consistency**: Standardized "BertRag" (not "BERT-RAG" or "Bert-Rag") across all documentation
- **Accurate Claims**: Changed "perfect citations" → "validated citations" in user-facing docs to avoid overselling
- **Mode Corrections**: Updated all references from legacy modes (MapReduce/Rag as primary) to current production modes (Auto/BertRag/Bert)
- **Template Count Fix**: Corrected from "11 templates" to "13 templates" with full documentation
- **Performance Verification**: Validated all benchmark numbers against source code (3-5s for Bert, ~15-20s for full pipeline, 500+ page support)
- **Feature Completeness**: Verified all user-facing features are documented (Project Gutenberg ZIP support, all 13 templates, all 7 modes)

### New Features

#### Two New Summary Templates

**`prose`** - Clean multi-paragraph prose summary without metadata:
```bash
docsummarizer -f doc.pdf -t prose
```
- 400-word target, 4 paragraphs
- No citations, no metadata, no formatting
- Just flowing prose for clean presentation
- Perfect for embedding in reports or presentations

**`strict`** - Token-efficient summary with hard constraints:
```bash
docsummarizer -f doc.pdf -t strict
```
- Exactly 3 bullet points, ≤60 words total
- No hedging language ("appears to", "seems", "possibly")
- Highest-confidence facts only
- Optimized for token-constrained contexts

**Total templates now: 13**
- default, prose, brief, oneliner, bullets, executive, detailed, technical, academic, citations, bookreport, meeting, strict

### Improvements

- **README**: Updated version badge references
- **Cross-References**: Added navigation between Part 1, Part 2, and Part 3 articles
- **Honest Limitations**: Clarified coverage scores as "proxy for topical coverage, not proof of full-document reading"
- **Problem-Solution Framing**: Restructured Part 3 to use "problem-solution" pattern for better learning
- **Failure Modes**: Added "Common Failure Modes" section with 6 real issues and fixes

### Files Modified

```
Mostlylucid/Markdown/
├── building-a-document-summarizer-with-rag.md           # Part 1 - consistency fixes
├── docsummarizer-tool.md                                # Part 2 - template updates
├── docsummarizer-advanced-concepts.md                   # Part 3 - technical depth
└── fetching-and-analysing-web-content-with-llms.md     # Cross-reference added

Mostlylucid.DocSummarizer/
├── README.md                                            # Version badge, terminology
├── CHANGELOG.md                                         # This file
└── Config/SummaryTemplates.cs                          # Prose + Strict templates
```

### Bug Fixes

- Fixed inconsistent mode naming in documentation
- Corrected template count from 11 to 13
- Fixed terminology inconsistencies (BertRag vs BERT-RAG)

### Breaking Changes

None - all changes are documentation and template additions.

---

## v2.7.0 - Universal Tokenizer & Unified UI (2025-12-18)

### New Features

#### Universal Tokenizer Support

The ONNX embedding service now supports multiple tokenizer formats via `HuggingFaceTokenizer`:

- **WordPiece** (BERT-style) - existing support, now unified
- **BPE** (GPT-style, RoBERTa) - NEW
- **Unigram** (SentencePiece, T5, XLNet) - NEW

The tokenizer automatically detects the format from `tokenizer.json` files:

```bash
# Same usage - now works with more model types
docsummarizer -f document.pdf -m BertRag
```

**How it works:**
1. Prefers `tokenizer.json` (universal HuggingFace format)
2. Falls back to `vocab.txt` (legacy WordPiece) if needed
3. Auto-detects tokenizer type: WordPiece, BPE, or Unigram
4. Supports all pre-tokenizers: Whitespace, BERT, Metaspace, ByteLevel, Sequence
5. Supports all normalizers: BERT, Lowercase, NFC, NFKC, Sequence

**Models now supported:**
- All existing BERT models (AllMiniLM, BGE, GTE, etc.)
- Future BPE models (GPT-style, RoBERTa, MPNet)
- Future Unigram models (T5, XLNet, SentencePiece)

#### Unified UI Service

New `UIService` consolidates 5+ progress/display implementations into one consistent interface:

```csharp
// Single unified interface for all UI output
IUIService ui = new UIService(verbose: true);

ui.WriteHeader("DocSummarizer", "Batch Mode");
ui.WriteDocumentInfo(fileName, mode, model, focus);
await ui.WithSpinnerAsync("Processing...", async () => await task());
ui.WriteSummary(result.ExecutiveSummary);
ui.WriteCompletion(elapsed);
```

**Features:**
- Automatic fallback to simple console when in batch context
- Prevents nested Spectre.Console progress bar conflicts
- Consistent styling across all operations
- Batch context management via `ui.EnterBatchContext()`

**Replaces:**
- `ProgressService` (plain Console.WriteLine)
- `SpectreProgressService` (rich Spectre output)
- `SimpleProgressService` (simple console)
- `ConsoleProgressReporter` / `NullProgressReporter`
- Direct `Console.WriteLine` and `AnsiConsole` calls in Program.cs

### Improvements

- **Batch Mode**: Now properly enters batch context to prevent nested progress bar errors
- **Code Cleanup**: Removed deprecated `BertTokenizer` class (was ~100 lines in OnnxEmbeddingService.cs)
- **Consistency**: All CLI operations now use unified `IUIService` interface

### Files Added

```
Services/
├── Onnx/HuggingFaceTokenizer.cs    # Universal tokenizer (WordPiece, BPE, Unigram)
└── UIService.cs                     # Unified UI service with IUIService interface
```

### Files Modified

```
Services/Onnx/OnnxEmbeddingService.cs   # Use HuggingFaceTokenizer, removed BertTokenizer
Program.cs                               # Updated to use UIService
```

### Breaking Changes

None - all changes are backward compatible.

### Technical Details

#### HuggingFaceTokenizer Architecture

```
tokenizer.json → TokenizerConfig → ITokenizerModel
                       |                  |
                       |                  +-- WordPieceModel (BERT)
                       |                  +-- BpeModel (GPT/RoBERTa)
                       |                  +-- UnigramModel (T5/XLNet)
                       |
                       +-- PreTokenizer (Whitespace, BERT, Metaspace, ByteLevel)
                       +-- Normalizer (BERT, Lowercase, NFC, NFKC)
```

#### UIService Architecture

```
IUIService
    |
    +-- UIService (Spectre.Console implementation)
            |
            +-- WriteHeader(), WriteSummary(), WriteTopics(), etc.
            +-- WithSpinnerAsync() - automatic fallback in batch mode
            +-- EnterBatchContext() - prevents nested progress bars
            +-- UIServiceProgressAdapter - bridges to legacy IProgressReporter
```

---

## v2.6.0 - LLM Tool Mode, Web Fetching & Security Hardening (2025-12-17)

### New Features

#### LLM Tool Mode (`tool` command)

A new command designed for AI agent integration, MCP servers, and automated pipelines:

```bash
# Summarize and get structured JSON output
docsummarizer tool -f document.pdf
docsummarizer tool -u "https://example.com/docs"
```

Output includes:
- Evidence-grounded claims with confidence levels (high/medium/low)
- Chunk IDs for citation tracking
- Structured topics with evidence references
- Named entities (people, organizations, concepts)
- Processing metadata (time, model, mode)

#### Security-Hardened Web Fetching

New `WebFetcher` service with comprehensive security controls:

- **SSRF Protection**: Blocks private IPs (10.x, 172.16.x, 192.168.x), localhost, link-local, cloud metadata endpoints (169.254.169.254)
- **DNS Rebinding Protection**: Re-validates IP addresses after each redirect
- **Request Limits**: Max 5 redirects, 10MB response size, configurable timeouts
- **Content-Type Gating**: Only accepts safe document types (HTML, PDF, images, text)
- **Decompression Bomb Protection**: Limits expansion ratio to 20x
- **HTML Sanitization**: Removes scripts, event handlers, dangerous URLs via AngleSharp
- **Image Guardrails**: 50 images max, 1920px max dimension, hash deduplication
- **Protocol Downgrade Prevention**: Blocks HTTPS→HTTP redirects

```bash
# Fetch and summarize a web page
docsummarizer -f https://example.com/docs --web-enabled
```

#### Quality Analysis System

New `QualityAnalyzer` service for summary quality assessment:

- **Citation Metrics**: Coverage, density, orphan detection
- **Coherence Metrics**: Flow, redundancy, structure analysis
- **Entity Metrics**: Consistency checking across summary
- **Factuality Metrics**: Hallucination detection heuristics
- **Evidence Density**: Supported vs unsupported claims ratio
- **Quality Grades**: A-F scoring based on composite metrics

#### Text Analysis Service

New `TextAnalysisService` for advanced text processing:

- **Entity Normalization**: Fuzzy matching to deduplicate "John Smith" / "J. Smith"
- **Semantic Deduplication**: Jaro-Winkler similarity for near-duplicate detection
- **TF-IDF Weighting**: Keyword extraction and importance scoring
- **AOT Compatible**: All algorithms implemented inline (no reflection)

#### Structured MapReduce Summarizer

New `StructuredMapReduceSummarizer` for JSON extraction mode:

- Produces structured JSON in map phase for better merging
- Loss-aware reduce phase preserves important details
- Schema-driven output for consistent parsing

#### Rich Terminal UI

New `TuiProgressService` using Terminal.Gui:

- Real-time progress visualization with animated bars
- Chunk-by-chunk status tracking
- LLM activity monitoring
- Batch processing dashboard
- Works alongside existing console progress

#### 11 Summary Templates (was 9)

Two new templates added:

- **`bookreport`** (~400 words): Classic book report style with overview, characters/key players, plot/content, themes, and opinion sections
- **`meeting`** (~200 words): Meeting notes format with summary, key decisions, action items, and open questions

#### Template Word Count Syntax

Specify custom word count inline with template name:

```bash
# Use bookreport template with 500 word target
docsummarizer -f doc.pdf -t bookreport:500

# Executive summary limited to 100 words
docsummarizer -f doc.pdf -t executive:100

# --words flag still works and takes precedence
docsummarizer -f doc.pdf -t detailed --words 300
```

### Improvements

- **CLI**: Added `-t/--template` and `-w/--words` options to root command
- **JSON Context**: Added all Tool output types for AOT serialization
- **Progress Reporting**: New `IProgressReporter` interface for pluggable progress
- **Batch Processing**: Enhanced `TuiBatchProcessor` with Terminal.Gui dashboard

### Files Added

```
Services/
├── WebFetcher.cs                    # Security-hardened web content fetcher
├── QualityAnalyzer.cs               # Summary quality assessment
├── TextAnalysisService.cs           # Entity normalization, TF-IDF
├── StructuredMapReduceSummarizer.cs # JSON extraction mode
├── TuiProgressService.cs            # Terminal.Gui progress UI
├── TuiBatchProcessor.cs             # TUI batch processing
└── IProgressReporter.cs             # Progress abstraction

Models/
└── DocumentModels.cs                # ToolOutput, GroundedClaim, etc.
```

### Files Modified

```
Program.cs                           # tool command, template options
Config/SummaryTemplates.cs           # bookreport, meeting templates
Config/DocSummarizerJsonContext.cs   # Tool output serialization
README.md                            # Updated documentation
```

### Security Notes

The `WebFetcher` implements defense-in-depth:

1. **Pre-flight**: URL scheme validation (http/https only)
2. **DNS Resolution**: IP validation before connection
3. **Request**: Size limits, timeout, no cookies/auth
4. **Redirect**: Re-validate each hop, limit count
5. **Response**: Content-type check, size enforcement
6. **Processing**: HTML sanitization, image resizing

### Breaking Changes

None - all new features are additive.

### Usage Examples

```bash
# LLM Tool Mode - JSON output for agents
docsummarizer tool -f contract.pdf -q "payment terms"
docsummarizer tool -u "https://docs.example.com" | jq '.summary.keyFacts'

# Web fetching (requires explicit opt-in)
docsummarizer -u "https://example.com/whitepaper.pdf" --web-enabled

# New templates
docsummarizer -f novel.pdf -t bookreport
docsummarizer -f transcript.md -t meeting:300

# Template with custom word count
docsummarizer -f report.pdf -t executive:75
```

---

## v2.1.0 - Summary Templates & Expanded Format Support (2025-12-17)

### New Features

#### Expanded Input Format Support

All formats supported by Docling are now documented and supported:

- **Direct read** (no Docling needed): Plain text (.txt), Markdown (.md)
- **Documents**: PDF, DOCX, XLSX, PPTX
- **Web/Text**: HTML, XHTML, CSV, AsciiDoc
- **Images**: PNG, JPEG, TIFF, BMP, WEBP (with OCR)
- **Captions**: WebVTT (.vtt)
- **XML**: USPTO patents, JATS articles

#### Smart Plain Text Chunking

- Automatically detects plain text vs markdown
- Splits plain text by paragraphs (double newlines)
- Extracts titles from short first paragraphs
- Uses first sentence as chunk heading for better traceability

#### Summary Templates System

- **9 built-in templates** for different use cases:
    - `default` - Balanced prose with topic breakdowns
    - `brief` - Quick 2-3 sentence summary
    - `oneliner` - Single sentence (25 words max)
    - `bullets` - Key takeaways as bullet points
    - `executive` - Leadership briefing format
    - `detailed` - Comprehensive 500+ word analysis
    - `technical` - Implementation-focused for tech docs
    - `academic` - Formal abstract structure
    - `citations` - Key quotes with source references

- **Template properties** control:
    - Target word count and bullet limits
    - Output style (Prose, Bullets, Mixed, CitationsOnly)
    - Section visibility (topics, citations, questions, trace)
    - Tone (Professional, Casual, Academic, Technical)
    - Audience level (General, Executive, Technical)
    - Custom LLM prompts with placeholder substitution

- **CLI integration**:
    - `docsummarizer templates` - List available templates
    - `--template` / `-t` - Select template by name
    - `--words` / `-w` - Override target word count

### Improvements

- **Template-aware OutputFormatter**: Applies template settings to control output sections
- **Template aliases**: `exec` → `executive`, `tech` → `technical`, etc.
- **Prompt customization**: Override executive, topic, and chunk prompts per template

### Files Added/Modified

```
Config/
└── SummaryTemplates.cs           # Template system with 9 presets

Services/
├── MapReduceSummarizer.cs        # Template integration
├── RagSummarizer.cs              # Template integration
├── OutputFormatter.cs            # Template-aware formatting
└── DocumentSummarizer.cs         # Template parameter support

Program.cs                         # --template and --words options
```

### Usage Examples

```bash
# Quick one-liner for file preview
docsummarizer -f doc.pdf -t oneliner

# Executive briefing
docsummarizer -f report.pdf --template executive

# Technical documentation summary
docsummarizer -f api-spec.md -t tech --focus "authentication"

# Custom word count
docsummarizer -f doc.pdf --template brief --words 75
```

---

## v2.0.0 - Major Feature Release (2025-12-16)

### 🎉 New Features

#### 1. Configuration System

- **JSON-based configuration** with sensible defaults
- Automatic discovery from multiple locations:
    - Custom path via `--config` option
    - `docsummarizer.json` (current directory)
    - `.docsummarizer.json` (current directory)
    - `~/.docsummarizer.json` (user home)
- Comprehensive configuration sections for all aspects of the tool
- Generate default config with `dotnet run -- config`

#### 2. Batch Processing

- Process entire directories with `--directory` option
- File extension filtering (e.g., `--extensions .pdf .docx .md`)
- Recursive directory scanning with `--recursive`
- Glob pattern support for include/exclude
- Continue on error or fail-fast modes
- Comprehensive batch summary reports

#### 3. Multiple Output Formats

- **Console**: Formatted terminal output (default)
- **Text**: Plain text files for archival
- **Markdown**: Rich formatting with tables
- **JSON**: Machine-readable structured data
- Configurable output directory
- Automatic file naming based on source documents

#### 4. AOT Compilation Support

- Native AOT compilation enabled
- Full trimming support for smaller binaries
- JSON source generation for AOT compatibility
- Faster startup times (<100ms)
- Smaller memory footprint
- Platform-specific optimized builds

#### 5. Enhanced Model Support

- **Default model changed**: `ministral-3:3b` (Mistral-3 3B)
- **128K context window** (vs 8K in llama3.2:3b)
- Model information inspection with `check --verbose`
- Display context window, parameters, quantization
- List all available models

### 🔧 Improvements

#### OllamaService

- Added `GetModelInfoAsync()` for model inspection
- Added `GetAvailableModelsAsync()` for model listing
- Added `Model` and `EmbedModel` properties
- Known context window mappings for common models

#### CLI Enhancements

- Redesigned command-line interface
- More intuitive option names
- Better help text
- Enhanced `check` command with verbose mode

#### Code Quality

- Source-generated JSON serialization
- AOT-compatible throughout
- Better error handling
- Improved nullability annotations

### 📦 New Files

```
Config/
├── DocSummarizerConfig.cs        # Configuration models
├── ConfigurationLoader.cs        # Config loading logic
└── DocSummarizerJsonContext.cs   # Source-generated serialization

Services/
├── BatchProcessor.cs             # Batch processing
└── OutputFormatter.cs            # Output formatting

docsummarizer.example.json        # Example configuration
FEATURES.md                       # Feature documentation
CHANGELOG.md                      # This file
```

### 🔄 Modified Files

```
Services/
├── OllamaService.cs              # Added model inspection
├── DocumentSummarizer.cs         # Config support
└── DoclingClient.cs              # AOT-compatible JSON

Models/
└── DocumentModels.cs             # Added batch models

Program.cs                         # Complete redesign
Mostlylucid.DocSummarizer.csproj  # AOT enabled
```

### 📊 Breaking Changes

#### Default Model Change

- **Old**: `llama3.2:3b` (8K context)
- **New**: `ministral-3:3b` (128K context)

**Migration**: Pull the new model

```bash
ollama pull ministral-3:3b
```

#### Configuration

- Configuration now preferred over CLI options
- CLI options override config file settings
- Sensible defaults provided

### 🚀 Usage Examples

#### Single File (Simple)

```bash
# Uses defaults
dotnet run -- --file document.pdf
```

#### Batch Processing

```bash
# Process directory
dotnet run -- --directory ./docs --extensions .pdf .docx --recursive
```

#### Custom Configuration

```bash
# Use config file
dotnet run -- --config prod.json --file document.pdf
```

#### Output Formats

```bash
# Markdown output
dotnet run -- -f doc.pdf -o Markdown --output-dir ./summaries

# JSON for APIs
dotnet run -- -f doc.pdf -o Json --output-dir ./api-data
```

#### Model Information

```bash
# Check dependencies with model info
dotnet run -- check --verbose
```

Expected output:

```
Checking dependencies...

  Ollama: ✓ (http://localhost:11434)

  Available models:
    - ministral-3:3b
    - nomic-embed-text
    - llama3.2:3b
    ... and 5 more

  Default model info:
    Name: ministral-3:3b
    Family: mistral
    Parameters: 3B
    Quantization: Q4_0
    Context Window: 128,000 tokens
    Format: gguf

  Embed model info:
    Name: nomic-embed-text
    Family: nomic-bert
    Parameters: 137M
```

### 🏗️ AOT Publishing

#### Windows

```bash
dotnet publish -c Release -r win-x64
```

#### Linux

```bash
dotnet publish -c Release -r linux-x64
```

#### macOS (ARM)

```bash
dotnet publish -c Release -r osx-arm64
```

### 📈 Performance Improvements

| Metric         | Before    | After       | Improvement    |
|----------------|-----------|-------------|----------------|
| Startup time   | ~2s       | <100ms      | **20x faster** |
| Binary size    | ~150MB    | ~40MB       | **4x smaller** |
| Memory usage   | ~150MB    | ~50MB       | **3x less**    |
| Context window | 8K tokens | 128K tokens | **16x larger** |

### 🐛 Bug Fixes

- Fixed JSON serialization for AOT compatibility
- Fixed DocumentChunker heading logic for documents without headings
- Fixed test issues with integration test skipping
- Fixed nullable reference warnings

### 🔐 Security

- AOT compilation reduces attack surface
- No runtime code generation
- Trimming removes unused code
- Source-generated serialization (no reflection)

### 📚 Documentation

- New `FEATURES.md` with comprehensive feature documentation
- Updated `README.md` with new features
- Example configuration file with comments
- This changelog for tracking changes

### 🙏 Credits

- **Mistral AI** for the excellent ministral-3 model
- **Ollama team** for local LLM inference
- **.NET team** for Native AOT support

### ⬆️ Upgrade Guide

1. **Pull new model**:
   ```bash
   ollama pull ministral-3:3b
   ```

2. **Generate config** (optional):
   ```bash
   dotnet run -- config
   ```

3. **Update existing scripts** to use new CLI options (backward compatible)

4. **Test with new features**:
   ```bash
   dotnet run -- check --verbose
   ```

### 🔮 Future Roadmap

- [ ] Streaming output for real-time feedback
- [ ] Custom prompt templates
- [ ] Plugin system for custom processors
- [ ] Web API for remote access
- [ ] Docker container with all dependencies
- [ ] Kubernetes Helm chart
- [ ] Resume support for interrupted batch jobs
- [ ] Parallel batch processing

### 📞 Support

For issues, questions, or contributions:

- Check `README.md` for usage documentation
- See `FEATURES.md` for feature details
- Review `docsummarizer.example.json` for configuration options

---

## v1.0.0 - Initial Release

- Basic document summarization
- Three summarization modes (MapReduce, RAG, Iterative)
- Support for PDF, DOCX, and Markdown files
- Citation tracking
- Integration with Ollama, Docling, and Qdrant
