# Background Learning Loop & Vector Store Abstraction

## Overview

Two features that reduce DoomSummarizer's runtime cost and package size:

1. **Background Learning** — sentinel LLM corrections train the embedding classifier over time, progressively reducing expensive LLM calls
2. **sqlite-vec for CLI** — replace 96MB DuckDB with 369KB sqlite-vec for vector storage in the CLI, keeping DuckDB for the LucidRAG server where HNSW matters

---

## 1. Background Learning Loop

### The Problem

The `QueryClassifier` (v2.5) classifies queries using ~250 exemplar embeddings in ~250us. When confidence is low (BestMatchScore < 0.55), it escalates to Ollama's sentinel LLM — correct but slow (~2s). Currently ~30% of queries escalate.

### The Solution

Capture every sentinel correction. Periodically analyze where the embedding classifier disagrees with the sentinel. Propose new exemplars to fill gaps. Validate against the existing 88-test diagnostic suite before merging.

### Architecture

```
Live query flow:
  Query → Classifier → strong match (>0.55) → skip sentinel ✓
                     → weak match           → sentinel LLM → log disagreement
                                                           ↓
                                              learning_log table (SQLite)

Offline learning flow (exemplars --learn):
  learning_log → cluster disagreements by topic/type
               → pick representative query per cluster
               → propose exemplar YAML
               → (optional) LLM enhances proposals
               → validate: run 88+ test matrix, reject if regressions
               → merge: write to ~/.doomsummarizer/exemplars/
```

### Data Flow

The `learning_log` SQLite table captures both sides of every disagreement:

| Column | Source |
|--------|--------|
| `emb_topic`, `emb_type`, `emb_vibe` | Embedding classifier output |
| `sentinel_topic`, `sentinel_type`, `sentinel_vibe` | Sentinel LLM correction |
| `topic_disagreement`, `type_disagreement` | Computed flags |
| `query_embedding` | 384d BLOB for clustering |

### CLI Commands

```bash
# Show disagreement statistics
doomsummarizer exemplars --learn

# Propose exemplars, display as table
doomsummarizer exemplars --learn --verbose

# Use Ollama to generate richer pattern exemplars
doomsummarizer exemplars --learn --learn-llm

# Validate and apply proposals (runs test suite as gate)
doomsummarizer exemplars --learn-apply

# Custom cluster threshold
doomsummarizer exemplars --learn --learn-min-cluster 5
```

### Validation Gate

No auto-learned exemplar ships without passing:
1. Load existing 250+ exemplars + proposed additions
2. Re-initialize classifier with combined set
3. Run full `BuildTestMatrix()` (88+ diagnostic tests)
4. **Reject** if any previously-passing test now fails
5. **Accept** only if all pass (bonus: check if weak tests now pass)

### Integration Points

- **`PromptInterpreter.cs`** — captures disagreements via `ILearningLogger` interface (static property, set at startup)
- **`StorageService.Learning.cs`** — new partial class for learning log CRUD
- **`LearningAnalyzer.cs`** — clustering + proposal generation
- **`ExemplarsCommand.cs`** — CLI surface (`--learn`, `--learn-apply`)

### Expected Impact

With 100+ sentinel calls logged, the analyzer should identify 5-10 gap clusters. Each merged cluster adds 1-3 exemplars. Over time:
- Strong-match rate: 70% → 90%+
- Sentinel call rate: 30% → <10%
- Classification latency: stays at ~250us for most queries

---

## 2. Vector Store Abstraction (DuckDB → sqlite-vec)

### The Problem

DoomSummarizer bundles both SQLite (~2MB) and DuckDB.NET (~96MB). For a CLI tool processing <10K items, DuckDB's HNSW indexing is unnecessary. The brute-force approach works fine at this scale.

### The Solution

Abstract vector operations behind `IItemVectorStore`. Two implementations:

| Backend | Package Size | Search Method | Scale Sweet Spot |
|---------|-------------|---------------|------------------|
| `DuckDbVectorStore` | ~96MB | HNSW (ANN) | >10K vectors |
| `SqliteVecItemVectorStore` | ~369KB | Brute-force | <10K vectors |

### Interface

```csharp
public interface IItemVectorStore : IAsyncDisposable
{
    Task InitializeAsync();
    bool SupportsHnsw { get; }
    Task UpsertItemEmbeddingAsync(string itemId, string title, string? source,
                                   string? url, float[]? embedding);
    Task<List<(string itemId, string title, string? url, float similarity)>>
        FindSimilarItemsAsync(float[] queryEmbedding, int topK = 10,
                               float minSimilarity = 0.5f);
    Task<int> GetItemCountAsync();
    Task CleanupAsync(int retentionDays);
}
```

### Configuration

```yaml
storage:
  vector_backend: "duckdb"      # default, HNSW for scale
  # vector_backend: "sqlite-vec"  # lightweight CLI, brute-force
```

### Migration

On first run with `vector_backend: sqlite-vec`, if a DuckDB file exists:
1. Open DuckDB read-only
2. Export all item embeddings
3. Bulk insert into sqlite-vec virtual table
4. Log migration stats

### Where Each Backend Is Used

| Application | Recommended Backend | Why |
|-------------|-------------------|-----|
| DoomSummarizer CLI | sqlite-vec | Package size matters, <10K items |
| LucidRAG server | DuckDB | Needs HNSW at scale, multi-tenant |
| LucidRESEARCH | DuckDB | Large corpora, persistent indexes |

### Performance Expectations

At 384 dimensions:

| Item Count | sqlite-vec (brute) | DuckDB (HNSW) |
|-----------|-------------------|---------------|
| 100 | <1ms | <1ms |
| 1,000 | ~2ms | <1ms |
| 5,000 | ~10ms | <1ms |
| 10,000 | ~20ms | <1ms |
| 100,000 | ~200ms | <2ms |

---

## Implementation Phases

| Phase | Scope | Dependencies |
|-------|-------|-------------|
| 1 | Sentinel disagreement capture (logging) | None |
| 2 | Gap analysis + `exemplars --learn` | Phase 1 data |
| 3 | `IItemVectorStore` + sqlite-vec | Independent |
| 4 | LucidRAG server background loop | Phase 1 + 2 |

Phases 1 and 3 can run in parallel.

---

## Future Extensions

- **Per-tenant drift tracking** — LucidRAG server tracks classification accuracy per tenant, adjusts thresholds
- **Exemplar CI pipeline** — auto-proposed exemplars go through PR review with test results
- **Model calibration** — when switching embedding models, auto-recalibrate thresholds from exemplar scores
- **Active learning** — intentionally route borderline queries to sentinel to maximize learning signal
