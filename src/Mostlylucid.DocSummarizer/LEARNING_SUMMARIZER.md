# Learning Summarizer - Intelligent Caching for BertRag Pipeline

> **Re-summarizing the same document? Skip the LLM. Same evidence set? Cache hit.**

The Learning Summarizer extends BertRag mode with persistent vector storage and intelligent caching. Documents are only re-embedded when content changes. Summaries are cached by their evidence set - if the same segments are retrieved, you get instant results.

---

## Quick Start

```bash
# Enable persistent caching with Qdrant
docsummarizer -f document.pdf -m BertRag

# Configuration (docsummarizer.json)
{
  "bertRag": {
    "vectorStore": "Qdrant",
    "collectionName": "docsummarizer",
    "persistVectors": true,
    "reuseExistingEmbeddings": true
  }
}
```

First run: ~15s (embed + synthesize). Second run: ~0.5s (cache hit).

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                        Learning Summarizer Pipeline                          │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  Document → [Content Hash] → Cache Lookup                                    │
│                    │                                                         │
│                    ▼                                                         │
│            ┌──────────────┐                                                  │
│            │ Has Segments?│──Yes──→ [Load Existing] → Skip Embedding         │
│            └──────────────┘                                                  │
│                    │ No                                                      │
│                    ▼                                                         │
│  ┌─────────────────────────────────────────────────────────────────┐        │
│  │                    EXTRACT (CPU-bound)                          │        │
│  │  Parse → Segment → Embed → Store in Vector DB                   │        │
│  │  • Segments keyed by content hash (drift detection)             │        │
│  │  • Salience scores computed from position + centrality          │        │
│  └─────────────────────────────────────────────────────────────────┘        │
│                    │                                                         │
│                    ▼                                                         │
│  ┌─────────────────────────────────────────────────────────────────┐        │
│  │                   RETRIEVE (Vector Search)                       │        │
│  │  Query → Dual-Score Ranking → Top-K Segments                     │        │
│  │  • Semantic similarity (query ↔ segment)                         │        │
│  │  • Content salience (importance score)                           │        │
│  └─────────────────────────────────────────────────────────────────┘        │
│                    │                                                         │
│                    ▼                                                         │
│            ┌──────────────────────┐                                          │
│            │ Evidence Hash Match? │──Yes──→ [Return Cached Summary]          │
│            └──────────────────────┘                                          │
│                    │ No                                                      │
│                    ▼                                                         │
│  ┌─────────────────────────────────────────────────────────────────┐        │
│  │                   SYNTHESIZE (LLM-bound)                         │        │
│  │  Segments → Prompt → LLM → Summary                               │        │
│  │  • Cache result with evidence hash                               │        │
│  │  • Perfect citations from segment IDs                            │        │
│  └─────────────────────────────────────────────────────────────────┘        │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Key Concepts

### Content Hashing

Every segment is identified by a hash of its canonicalized content (normalized whitespace). This enables:

- **Drift Detection**: Changed paragraphs get new hashes → re-embedded
- **Unchanged Segments**: Same hash → reuse existing embedding
- **Granular Invalidation**: Only update what changed

```csharp
// Content is canonicalized before hashing
"Hello   world\n\n" → "Hello world" → xxHash64 → "a1b2c3d4..."
```

### Evidence-Based Cache Key

Summaries are cached by their evidence set, not just the document:

```
CacheKey = Hash(
    pipelineVersion,      // "bertrag-v1"
    documentContentHash,  // Hash of full document
    queryHash,            // Focus query (if any)
    templateHash,         // Summary template settings
    modelHash,            // LLM model identifier
    evidenceContentHashes // Hashes of retrieved segments
)
```

This means:
- Same document + same query + same evidence → cache hit
- Same document + different query → cache miss (different segments retrieved)
- Document updated → cache miss (content hash changed)

### Granular Invalidation

When a document changes, only affected segments are re-processed:

```
Original Document:              Updated Document:
┌─────────────────────┐        ┌─────────────────────┐
│ Paragraph A [hash1] │───────→│ Paragraph A [hash1] │ ← Reused
│ Paragraph B [hash2] │───────→│ Paragraph B' [hash5]│ ← Re-embedded
│ Paragraph C [hash3] │───────→│ Paragraph C [hash3] │ ← Reused
│ Paragraph D [hash4] │        │ Paragraph E [hash6] │ ← New
└─────────────────────┘        └─────────────────────┘
                                        │
                                        ▼
                               Stale segment D removed
                               Only B' and E embedded
```

---

## Configuration

### BertRagConfig Options

```json
{
  "bertRag": {
    "vectorStore": "InMemory",
    "collectionName": "docsummarizer",
    "persistVectors": true,
    "reuseExistingEmbeddings": true
  }
}
```

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `vectorStore` | `InMemory` \| `Qdrant` | `InMemory` | Storage backend |
| `collectionName` | string | `docsummarizer` | Collection/index name |
| `persistVectors` | bool | `true` | Keep vectors between runs (Qdrant only) |
| `reuseExistingEmbeddings` | bool | `true` | Reuse unchanged segment embeddings |

### Vector Store Backends

#### InMemory (Default)

- No external dependencies
- Fast for single session
- Vectors lost on exit
- Best for: one-off summarization, testing

```json
{
  "bertRag": {
    "vectorStore": "InMemory"
  }
}
```

#### Qdrant (Recommended for Production)

- Persistent storage
- Requires Qdrant server
- Enables cross-session caching
- Best for: repeated summarization, batch processing

```bash
# Start Qdrant
docker run -d -p 6333:6333 -p 6334:6334 qdrant/qdrant
```

```json
{
  "bertRag": {
    "vectorStore": "Qdrant",
    "collectionName": "docsummarizer",
    "persistVectors": true
  },
  "qdrant": {
    "host": "localhost",
    "port": 6333,
    "vectorSize": 384
  }
}
```

---

## IVectorStore Interface

The `IVectorStore` abstraction enables pluggable backends:

```csharp
public interface IVectorStore : IAsyncDisposable
{
    // Initialization
    Task InitializeAsync(string collectionName, int vectorSize, CancellationToken ct = default);
    
    // Document operations
    Task<bool> HasDocumentAsync(string collectionName, string docId, CancellationToken ct = default);
    Task DeleteDocumentAsync(string collectionName, string docId, CancellationToken ct = default);
    
    // Segment operations
    Task UpsertSegmentsAsync(string collectionName, IEnumerable<Segment> segments, CancellationToken ct = default);
    Task<List<Segment>> SearchAsync(string collectionName, float[] queryEmbedding, int topK, string? docId = null, CancellationToken ct = default);
    Task<List<Segment>> GetDocumentSegmentsAsync(string collectionName, string docId, CancellationToken ct = default);
    
    // Granular invalidation
    Task<Dictionary<string, Segment>> GetSegmentsByHashAsync(string collectionName, IEnumerable<string> contentHashes, CancellationToken ct = default);
    Task RemoveStaleSegmentsAsync(string collectionName, string docId, IEnumerable<string> validContentHashes, CancellationToken ct = default);
    
    // Summary caching
    Task<DocumentSummary?> GetCachedSummaryAsync(string collectionName, string evidenceHash, CancellationToken ct = default);
    Task CacheSummaryAsync(string collectionName, string evidenceHash, DocumentSummary summary, CancellationToken ct = default);
    
    bool IsPersistent { get; }
}
```

### Implementing Custom Backends

To add a new backend (e.g., Pinecone, Weaviate):

1. Implement `IVectorStore`
2. Add to `VectorStoreBackend` enum
3. Wire up in `DocumentSummarizer` constructor

```csharp
public class PineconeVectorStore : IVectorStore
{
    public bool IsPersistent => true;
    
    public async Task InitializeAsync(string collectionName, int vectorSize, CancellationToken ct = default)
    {
        // Create Pinecone index if not exists
    }
    
    public async Task UpsertSegmentsAsync(string collectionName, IEnumerable<Segment> segments, CancellationToken ct = default)
    {
        // Upsert vectors to Pinecone
    }
    
    // ... implement remaining methods
}
```

---

## Performance Characteristics

### First Run (Cold Cache)

| Phase | Time | Description |
|-------|------|-------------|
| Parse | ~0.5s | Document to segments |
| Embed | ~5-10s | ONNX embedding (384-dim) |
| Store | ~0.1s | Upsert to vector store |
| Retrieve | ~0.05s | Vector search |
| Synthesize | ~5-15s | LLM generation |
| **Total** | **~10-25s** | Depends on document size |

### Second Run (Warm Cache - Same Query)

| Phase | Time | Description |
|-------|------|-------------|
| Hash Check | ~0.01s | Document content hash |
| Segment Lookup | ~0.05s | Load from vector store |
| Retrieve | ~0.05s | Vector search |
| Cache Hit | ~0.01s | Evidence hash match |
| **Total** | **~0.1s** | 100x faster |

### Second Run (Warm Cache - Different Query)

| Phase | Time | Description |
|-------|------|-------------|
| Hash Check | ~0.01s | Document content hash |
| Segment Lookup | ~0.05s | Load from vector store |
| Retrieve | ~0.05s | Different segments retrieved |
| Synthesize | ~5-15s | New LLM call (different evidence) |
| **Total** | **~5-15s** | Skip embedding, new synthesis |

### Document Update (Partial Change)

| Phase | Time | Description |
|-------|------|-------------|
| Parse | ~0.5s | Document to segments |
| Diff | ~0.1s | Compare content hashes |
| Re-embed | ~1-3s | Only changed segments |
| Cleanup | ~0.05s | Remove stale segments |
| Retrieve | ~0.05s | Vector search |
| Synthesize | ~5-15s | LLM generation |
| **Total** | **~7-20s** | Faster than full re-process |

---

## Use Cases

### 1. Iterative Document Review

Summarizing a document multiple times as you refine your focus:

```bash
# First summary (full processing)
docsummarizer -f report.pdf -m BertRag

# Different focus (reuses embeddings, new synthesis)
docsummarizer -f report.pdf -m BertRag --focus "security requirements"

# Same focus again (cache hit!)
docsummarizer -f report.pdf -m BertRag --focus "security requirements"
```

### 2. Batch Processing with Updates

Processing a document library, then updating changed files:

```bash
# Initial batch (all documents embedded)
docsummarizer -d ./documents -m BertRag -o Markdown --output-dir ./summaries

# After editing some documents (only changed files re-embedded)
docsummarizer -d ./documents -m BertRag -o Markdown --output-dir ./summaries
```

### 3. CI/CD Documentation Pipeline

Generate summaries for documentation on every commit:

```yaml
# .github/workflows/docs.yml
jobs:
  summarize:
    runs-on: ubuntu-latest
    services:
      qdrant:
        image: qdrant/qdrant
        ports: ["6333:6333"]
    steps:
      - uses: actions/checkout@v4
      
      # Cache Qdrant data between runs
      - uses: actions/cache@v4
        with:
          path: ~/.qdrant
          key: qdrant-${{ hashFiles('docs/**') }}
      
      - run: docsummarizer -d ./docs -m BertRag -o Markdown --output-dir ./summaries
```

### 4. Interactive Q&A Sessions

Answer multiple questions about the same document:

```bash
# First question (embed + retrieve + synthesize)
docsummarizer -f manual.pdf -m BertRag -q "How do I install?"

# Second question (reuse embeddings, different retrieval)
docsummarizer -f manual.pdf -m BertRag -q "What are the requirements?"

# Same question again (full cache hit!)
docsummarizer -f manual.pdf -m BertRag -q "How do I install?"
```

---

## Debugging

### Verbose Output

```bash
docsummarizer -f document.pdf -m BertRag -v
```

Shows:
- Segment parsing stats
- Cache lookup results
- Embedding reuse counts
- Retrieved segment IDs
- Evidence hash computation

### Qdrant Dashboard

Access the Qdrant web UI at `http://localhost:6333/dashboard`:
- View collections
- Inspect stored points
- Search vectors manually

### Cache Inspection

```bash
# List collections
curl http://localhost:6333/collections

# Count segments in collection
curl http://localhost:6333/collections/docsummarizer

# Search for a document's segments
curl -X POST http://localhost:6333/collections/docsummarizer/points/scroll \
  -H "Content-Type: application/json" \
  -d '{"filter": {"must": [{"key": "docId", "match": {"value": "mydoc"}}]}, "limit": 100}'
```

---

## Troubleshooting

| Issue | Cause | Solution |
|-------|-------|----------|
| Cache never hits | Evidence set changes | Check if query/template differs |
| Slow first run | Large document | Expected - embedding is CPU-intensive |
| Qdrant connection failed | Server not running | `docker run -d -p 6333:6333 qdrant/qdrant` |
| Stale summaries | Old cached version | Set `persistVectors: false` or delete collection |
| High memory usage | Many segments in memory | Use Qdrant backend for large batches |

---

## Roadmap

- [ ] Redis backend for distributed caching
- [ ] TTL-based cache expiration
- [ ] Compression for stored summaries
- [ ] Metrics/telemetry for cache hit rates
- [ ] CLI command to inspect/clear cache

---

## API Reference

### DocumentSummarizer Constructor

```csharp
public DocumentSummarizer(
    string ollamaModel = "llama3.2:3b",
    string doclingUrl = "http://localhost:5001",
    string qdrantHost = "localhost",
    bool verbose = false,
    DoclingConfig? doclingConfig = null,
    ProcessingConfig? processingConfig = null,
    QdrantConfig? qdrantConfig = null,
    SummaryTemplate? template = null,
    OllamaConfig? ollamaConfig = null,
    OnnxConfig? onnxConfig = null,
    EmbeddingBackend embeddingBackend = EmbeddingBackend.Onnx,
    BertConfig? bertConfig = null,
    BertRagConfig? bertRagConfig = null  // NEW: Caching config
)
```

### BertRagConfig

```csharp
public class BertRagConfig
{
    public VectorStoreBackend VectorStore { get; set; } = VectorStoreBackend.InMemory;
    public string CollectionName { get; set; } = "docsummarizer";
    public bool PersistVectors { get; set; } = true;
    public bool ReuseExistingEmbeddings { get; set; } = true;
}

public enum VectorStoreBackend
{
    InMemory,
    Qdrant
}
```
