# New Features - DocSummarizer v2.7

## Overview

DocSummarizer has been significantly enhanced with new features making it a comprehensive, production-ready document
processing tool with AOT compilation support.

## Universal Tokenizer (New in v2.7)

The ONNX embedding service now supports multiple tokenizer formats via `HuggingFaceTokenizer`, enabling support for a wider range of embedding models.

### Supported Tokenizer Types

| Type | Models | How It Works |
|------|--------|--------------|
| **WordPiece** | BERT, MiniLM, BGE, GTE | Greedy longest-match subword splitting with `##` prefix |
| **BPE** | GPT-2, RoBERTa, MPNet | Byte-pair encoding with learned merge rules |
| **Unigram** | T5, XLNet, SentencePiece | Viterbi-based probabilistic tokenization |

### How It Works

```
tokenizer.json → Parse Config → Detect Type → Create Model
                      |
                      +-- vocab: Dictionary<string, int>
                      +-- model.type: "WordPiece" | "BPE" | "Unigram"
                      +-- merges: (BPE only) merge rules
                      +-- pre_tokenizer: Whitespace, BERT, Metaspace, ByteLevel
                      +-- normalizer: BERT, Lowercase, NFC, NFKC
```

### Automatic Format Detection

The tokenizer automatically:
1. Prefers `tokenizer.json` (universal HuggingFace format)
2. Falls back to `vocab.txt` (legacy WordPiece) if needed
3. Resolves special tokens ([CLS], [SEP], [PAD], [UNK]) from config

### Pre-tokenizers

| Type | Description | Used By |
|------|-------------|---------|
| Whitespace | Split on whitespace | General |
| BERT | Split on whitespace + punctuation | BERT models |
| Metaspace | Replace spaces with ▁ | SentencePiece |
| ByteLevel | GPT-2 style byte-level | GPT/RoBERTa |
| Sequence | Chain multiple pre-tokenizers | Complex pipelines |

### Normalizers

| Type | Description |
|------|-------------|
| BERT | Clean whitespace, handle Chinese chars, optional lowercase |
| Lowercase | Convert to lowercase |
| NFC/NFKC | Unicode normalization |
| Sequence | Chain multiple normalizers |

### Usage

No changes needed - the tokenizer is used automatically:

```bash
# Same commands work with more models
docsummarizer -f document.pdf -m BertRag
docsummarizer -f document.pdf --embedding-model BgeSmallEnV15
```

## Unified UI Service (New in v2.7)

The `UIService` consolidates multiple progress/display implementations into one consistent interface.

### The Problem

Previously, DocSummarizer had 5+ different UI implementations:
- `ProgressService` - Plain Console.WriteLine
- `SpectreProgressService` - Rich Spectre.Console output  
- `SimpleProgressService` - Simple console output
- `ConsoleProgressReporter` / `NullProgressReporter` - IProgressReporter implementations
- Direct `Console.WriteLine` and `AnsiConsole` calls

This led to inconsistent output and the "nested progress bar" error in batch mode.

### The Solution

```csharp
// Single unified interface
public interface IUIService
{
    // Headers & Structure
    void WriteHeader(string title, string? subtitle = null);
    void WriteDocumentInfo(string document, string mode, string model, string? focus = null);
    void WriteDivider(string? title = null);
    
    // Messages
    void Info(string message);
    void Success(string message);
    void Warning(string message);
    void Error(string message, Exception? ex = null);
    
    // Results
    void WriteSummary(string summary, string title = "Summary");
    void WriteEntities(ExtractedEntities? entities);
    void WriteTopics(IEnumerable<(string Topic, string Summary)> topics);
    void WriteCompletion(TimeSpan elapsed, bool success = true);
    
    // Progress
    Task<T> WithSpinnerAsync<T>(string message, Func<Task<T>> operation);
    IDisposable EnterBatchContext();
    void WriteBatchProgress(int current, int total, string fileName, bool success);
}
```

### Features

1. **Automatic Fallback**: Rich Spectre.Console output when interactive, simple console in batch mode
2. **Batch Context**: `EnterBatchContext()` prevents nested progress bar errors
3. **Consistent Styling**: Same look across all operations
4. **Legacy Bridge**: `UIServiceProgressAdapter` connects to `IProgressReporter`

### Usage

```csharp
var ui = new UIService(verbose: true);

// Headers
ui.WriteHeader("DocSummarizer", "Batch Mode");
ui.WriteDocumentInfo("report.pdf", "BertRag", "llama3.2:3b");

// Progress (auto-fallback in batch mode)
var result = await ui.WithSpinnerAsync("Summarizing...", async () => 
    await summarizer.SummarizeAsync(filePath, mode));

// Results
ui.WriteSummary(result.ExecutiveSummary);
ui.WriteEntities(result.Entities);
ui.WriteTopics(result.TopicSummaries.Select(t => (t.Topic, t.Summary)));
ui.WriteCompletion(elapsed);

// Batch mode - prevents nested progress bars
using (ui.EnterBatchContext())
{
    foreach (var file in files)
    {
        var success = await ProcessFile(file);
        ui.WriteBatchProgress(current, total, file.Name, success);
    }
}
```

## Summary Templates (New in v2.1)

Templates provide fine-grained control over how summaries are generated. Instead of one-size-fits-all output, you can
choose the style that fits your use case.

### Template Architecture

Each template controls three aspects of summarization:

1. **LLM Prompts**: How the model is instructed to summarize
2. **Output Structure**: What sections to include (topics, questions, trace)
3. **Formatting**: Word count, bullet style, tone

```
┌─────────────────────────────────────────────────────────────────┐
│                     SummaryTemplate                             │
├─────────────────────────────────────────────────────────────────┤
│  Prompt Control          │  Output Control    │  Style Control  │
│  ──────────────          │  ──────────────    │  ─────────────  │
│  • ExecutivePrompt       │  • IncludeTopics   │  • TargetWords  │
│  • TopicPrompt           │  • IncludeCitations│  • MaxBullets   │
│  • ChunkPrompt           │  • IncludeQuestions│  • OutputStyle  │
│                          │  • IncludeTrace    │  • Tone         │
│                          │                    │  • Audience     │
└─────────────────────────────────────────────────────────────────┘
```

### Built-in Templates

| Template    | Use Case             | Output                                   |
|-------------|----------------------|------------------------------------------|
| `default`   | General purpose      | 2-paragraph prose with topic breakdowns  |
| `brief`     | Quick scanning       | 2-3 sentence overview                    |
| `oneliner`  | Titles/previews      | Single sentence (25 words max)           |
| `bullets`   | Quick reference      | 5-7 key takeaways as bullet points       |
| `executive` | Leadership briefings | Overview + 3 takeaways + recommendation  |
| `detailed`  | Deep analysis        | 500+ word comprehensive analysis         |
| `technical` | Tech documentation   | Implementation-focused with requirements |
| `academic`  | Research papers      | Formal abstract structure                |
| `citations` | Evidence gathering   | Key quotes with source references        |

### Template Properties

```csharp
public class SummaryTemplate
{
    // Identity
    string Name { get; set; }
    string Description { get; set; }
    
    // Length control
    int TargetWords { get; set; }      // 0 = no limit
    int MaxBullets { get; set; }       // 0 = no limit
    int Paragraphs { get; set; }       // 0 = auto
    
    // Output style
    OutputStyle OutputStyle { get; set; }  // Prose, Bullets, Mixed, CitationsOnly
    
    // Section control
    bool IncludeTopics { get; set; }
    bool IncludeCitations { get; set; }
    bool IncludeQuestions { get; set; }
    bool IncludeTrace { get; set; }
    
    // Tone and audience
    SummaryTone Tone { get; set; }         // Professional, Casual, Academic, Technical
    AudienceLevel Audience { get; set; }   // General, Executive, Technical
    
    // Custom prompts (optional)
    string? ExecutivePrompt { get; set; }
    string? TopicPrompt { get; set; }
    string? ChunkPrompt { get; set; }
}
```

### Custom Prompts

Templates can override the default LLM prompts. Placeholders are replaced at runtime:

**Executive Prompt** (`{topics}`, `{focus}`):

```
{topics}

Write an executive briefing with:
1. One paragraph overview (50 words max)
2. Three key takeaways as bullets
3. One recommended action

Be direct and actionable. No jargon.
```

**Topic Prompt** (`{topic}`, `{context}`, `{focus}`):

```
Topic: {topic}
Focus: {focus}
Sources: {context}

Write 2-3 bullet points summarizing this topic.
End each bullet with a citation [chunk-X].
```

**Chunk Prompt** (`{heading}`, `{content}`):

```
Section: {heading}

Content: {content}

Summarize this section in 2-4 bullet points.
Be specific and preserve key facts.
```

### Using Templates via CLI

```bash
# List available templates
docsummarizer templates

# Use a template
docsummarizer -f doc.pdf --template bullets
docsummarizer -f doc.pdf -t exec

# Override word count
docsummarizer -f doc.pdf --template executive --words 100

# Combine with focus query
docsummarizer -f doc.pdf -t technical --mode Rag --focus "API design"
```

### Programmatic Usage

```csharp
// Get a built-in template
var template = SummaryTemplate.Presets.Executive;

// Create custom template
var custom = new SummaryTemplate
{
    Name = "legal-brief",
    TargetWords = 400,
    OutputStyle = OutputStyle.Mixed,
    IncludeCitations = true,
    Tone = SummaryTone.Professional,
    Audience = AudienceLevel.Executive,
    ExecutivePrompt = """
        {topics}
        
        Write a legal brief summary:
        1. Key findings
        2. Risk assessment
        3. Recommended actions
        
        Cite all sources.
        """
};

// Use with DocumentSummarizer
var summarizer = new DocumentSummarizer(..., template: custom);
```

## 🎯 Other Features

### 1. Configuration System with Sensible Defaults

**File**: `Config/DocSummarizerConfig.cs`, `Config/ConfigurationLoader.cs`

- **JSON-based configuration** with automatic discovery
- **Default locations checked** (in order):
    1. Custom path via `--config` option
    2. `docsummarizer.json` (current directory)
    3. `.docsummarizer.json` (current directory)
    4. `~/.docsummarizer.json` (user home)

- **Configuration sections**:
    - `ollama`: Model selection, temperature, timeouts
    - `docling`: Service URL, timeouts
    - `qdrant`: Host, port, collection settings
    - `processing`: Chunking, parallelization settings
    - `output`: Format, directories, verbosity
    - `batch`: File filters, recursion, error handling

**Generate default config**:

```bash
dotnet run -- config --output myconfig.json
```

**Example config**: See `docsummarizer.example.json`

### 2. Batch Processing

**File**: `Services/BatchProcessor.cs`

Process entire directories with powerful filtering:

```bash
# Process all PDFs in a directory
dotnet run -- --directory ./docs --extensions .pdf

# Recursive processing with multiple file types
dotnet run -- -d ./documents -e .pdf .docx .md --recursive

# With specific patterns
dotnet run -- -d ./reports --recursive --extensions .pdf
```

**Features**:

- **File extension filtering**: `.pdf`, `.docx`, `.md`
- **Glob pattern support**: Include/exclude patterns
- **Recursive directory scanning**
- **Error handling**: Continue on error or fail-fast
- **Progress tracking**: Real-time status updates
- **Batch summary reports**: Success rates, timing, errors

### 3. Multiple Output Formats

**File**: `Services/OutputFormatter.cs`

Choose how you want your summaries:

```bash
# Console output (default)
dotnet run -- -f document.pdf

# Plain text file
dotnet run -- -f document.pdf --output-format Text --output-dir ./summaries

# Markdown file
dotnet run -- -f document.pdf -o Markdown --output-dir ./summaries

# JSON (machine-readable)
dotnet run -- -f document.pdf -o Json --output-dir ./api-results
```

**Formats**:

- **Console**: Formatted terminal output with colors
- **Text**: Plain text files for archival
- **Markdown**: Rich formatting with tables
- **Json**: Structured data for programmatic use

**Batch output**:

- Individual summaries per file
- Comprehensive batch summary with statistics
- Automatic file naming based on source documents

### 4. AOT Compilation & Trimming

**File**: `Mostlylucid.DocSummarizer.csproj`

Optimized for performance and deployment:

```xml
<PublishAot>true</PublishAot>
<PublishTrimmed>true</PublishTrimmed>
<TrimMode>full</TrimMode>
<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
```

**Benefits**:

- ⚡ **Faster startup**: Near-instant launch
- 📦 **Smaller binaries**: Trimmed to only what's needed
- 🔒 **Better security**: Less attack surface
- 💰 **Lower memory**: Reduced runtime overhead

**Publish AOT binary**:

```bash
dotnet publish -c Release -r win-x64
dotnet publish -c Release -r linux-x64
dotnet publish -c Release -r osx-arm64
```

### 5. JSON Source Generation

**File**: `Config/DocSummarizerJsonContext.cs`

All JSON serialization uses source generators for AOT compatibility:

```csharp
[JsonSourceGenerationOptions(...)]
[JsonSerializable(typeof(DocSummarizerConfig))]
[JsonSerializable(typeof(DocumentSummary))]
[JsonSerializable(typeof(BatchSummary))]
public partial class DocSummarizerJsonContext : JsonSerializerContext
{
}
```

**Benefits**:

- ✅ **AOT compatible**: No reflection needed
- ⚡ **Faster serialization**: Pre-generated code
- 🔍 **Compile-time safety**: Catches errors early
- 📦 **Smaller binaries**: No runtime codegen

## 📝 Usage Examples

### Single File with Custom Config

```bash
dotnet run -- --config production.json --file contract.pdf --mode Rag --focus "payment terms"
```

### Batch Processing Financial Reports

```bash
dotnet run -- \
  --directory ./quarterly-reports \
  --extensions .pdf .docx \
  --recursive \
  --output-format Markdown \
  --output-dir ./summaries \
  --mode MapReduce \
  --verbose
```

### Generate JSON for API Integration

```bash
dotnet run -- \
  --file policy.pdf \
  --output-format Json \
  --output-dir ./api-data \
  --mode Rag \
  --focus "coverage limits"
```

### Custom Configuration for Production

```json
{
  "ollama": {
    "model": "llama3.1:latest",
    "temperature": 0.2
  },
  "output": {
    "format": "Markdown",
    "outputDirectory": "/var/summaries",
    "includeTrace": false
  },
  "batch": {
    "fileExtensions": [".pdf"],
    "recursive": true,
    "continueOnError": true
  }
}
```

## 🔧 Command-Line Reference

### New Options

| Option            | Short | Description                    | Example              |
|-------------------|-------|--------------------------------|----------------------|
| `--config`        | `-c`  | Path to config file            | `-c prod.json`       |
| `--directory`     | `-d`  | Directory for batch processing | `-d ./docs`          |
| `--output-format` | `-o`  | Output format                  | `-o Markdown`        |
| `--output-dir`    |       | Output directory               | `--output-dir ./out` |
| `--extensions`    | `-e`  | File extensions                | `-e .pdf .docx`      |
| `--recursive`     | `-r`  | Recursive scanning             | `-r`                 |

### New Commands

```bash
# Generate default config file
dotnet run -- config --output docsummarizer.json

# Check dependencies (unchanged)
dotnet run -- check
```

## 🏗️ Architecture Updates

### New Components

1. **ConfigurationLoader**: Loads config from multiple sources
2. **BatchProcessor**: Handles multi-file processing
3. **OutputFormatter**: Formats output in multiple formats
4. **DocSummarizerJsonContext**: Source-generated JSON serialization

### Updated Components

1. **Program.cs**: Redesigned CLI with new options
2. **DocumentSummarizer**: Enhanced with config support
3. **DoclingClient**: Uses source-generated JSON
4. **Models**: Added BatchResult and BatchSummary

## 📦 File Structure

```
Mostlylucid.DocSummarizer/
├── Config/
│   ├── DocSummarizerConfig.cs        # Configuration models
│   ├── ConfigurationLoader.cs        # Config loading logic
│   └── DocSummarizerJsonContext.cs   # Source-generated serialization
├── Services/
│   ├── BatchProcessor.cs             # Batch processing
│   ├── OutputFormatter.cs            # Output formatting
│   ├── DocumentSummarizer.cs         # (Updated)
│   └── DoclingClient.cs              # (AOT-compatible)
├── Models/
│   └── DocumentModels.cs             # (Added batch models)
├── Program.cs                         # (Redesigned CLI)
├── docsummarizer.example.json        # Example configuration
└── Mostlylucid.DocSummarizer.csproj  # (AOT enabled)
```

## 🚀 Performance

### Benchmarks (Approximate)

- **Startup time**: <100ms (AOT) vs ~2s (JIT)
- **Binary size**: ~40MB (trimmed) vs ~150MB (full)
- **Memory**: ~50MB vs ~150MB
- **Batch processing**: 10-50 files/minute (depending on size/mode)

### Parallel Processing

- MapReduce mode processes chunks in parallel
- Configurable via `processing.maxDegreeOfParallelism`
- Default: Use all available cores

## 🔐 Security & Deployment

### AOT Benefits for Production

1. **No JIT compilation**: Attack surface reduced
2. **No runtime codegen**: More predictable behavior
3. **Deterministic execution**: Same binary, same behavior
4. **Faster cold starts**: Perfect for serverless/containers

### Deployment Scenarios

**Docker**:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
COPY publish/ /app
WORKDIR /app
ENTRYPOINT ["./Mostlylucid.DocSummarizer"]
```

**Kubernetes Job**:

```yaml
apiVersion: batch/v1
kind: Job
metadata:
  name: doc-summarizer
spec:
  template:
    spec:
      containers:
      - name: summarizer
        image: docsummarizer:latest
        args: ["--directory", "/data", "--output-dir", "/output"]
        volumeMounts:
        - name: docs
          mountPath: /data
        - name: summaries
          mountPath: /output
```

## 🐛 Known Limitations

1. **Trimming warnings**: Some dependencies (OllamaSharp, Qdrant.Client) aren't fully trim-compatible
    - Workaround: Added to `TrimmerRootAssembly` to preserve

2. **AOT restrictions**: No dynamic code generation
    - Solution: All JSON uses source generation

3. **Platform-specific builds**: Must target specific runtime
    - Use `-r` flag when publishing

## 🎓 Migration Guide

### From v1.0 to v2.0

**Old way**:

```bash
dotnet run -- --file doc.pdf --mode MapReduce --verbose
```

**New way (same result)**:

```bash
dotnet run -- --file doc.pdf --mode MapReduce --verbose
```

**New features**:

```bash
# Batch processing
dotnet run -- --directory ./docs --extensions .pdf

# Custom output
dotnet run -- -f doc.pdf -o Markdown --output-dir ./summaries

# Configuration file
dotnet run -- --config myconfig.json -f doc.pdf
```

### Configuration Migration

Create a config file with your common settings:

```bash
dotnet run -- config
# Edit docsummarizer.json with your preferences
```

Now you can omit CLI options:

```bash
# Uses config file automatically
dotnet run -- -f document.pdf
```

## 📚 Additional Resources

- See `README.md` for complete documentation
- See `docsummarizer.example.json` for all configuration options
- Run `dotnet run -- --help` for command-line reference

## 🙏 Acknowledgments

Built with:

- **.NET 9.0** Native AOT
- **System.CommandLine** for CLI
- **Microsoft.Extensions.FileSystemGlobbing** for pattern matching
- **Source generators** for JSON serialization
