# YAML Manifest System - Complete Implementation

## Overview

LucidRAG now uses a comprehensive YAML-based manifest system following StyloFlow patterns. **ALL configuration is in YAML with NO MAGIC NUMBERS**. The system supports lenses, waves, processors, and pipelines with full signal transparency and fine-grained control.

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                   YAML Manifests                        │
│  (Lenses, Waves, Processors, Pipelines)                │
└────────────────┬────────────────────────────────────────┘
                 │
        ┌────────┴────────┐
        │                 │
    FileSystem      Embedded
     Loader          Loader
        │                 │
        └────────┬────────┘
                 │
         ┌───────▼────────┐
         │   Registries   │
         │  (Singleton)   │
         └───────┬────────┘
                 │
         ┌───────▼────────┐
         │  Orchestrator  │
         │  (Executes)    │
         └────────────────┘
```

## Key Features

### 1. **Full Signal Transparency**
System prompts now receive ALL retrieval signals:
- Dense embedding scores
- BM25 keyword scores
- Salience term matches
- Freshness scores
- WHY each source matched (matched salient terms, entities, signals)

### 2. **Fine-Grained Signal Weighting**
Lenses can weight specific signals or signal classes:

```yaml
scoring:
  # Base RRF weights
  dense_weight: 0.3
  bm25_weight: 0.3
  salience_weight: 0.2
  freshness_weight: 0.2

  # Fine-grained signal weights
  signal_weights:
    "retrieval.title_match":
      weight: 1.0
      boost: 2.0
      min_threshold: 0.7
      decay: exponential

    "entity.technology.*":  # Wildcard matching
      weight: 1.0
      boost: 1.3
      normalize: true

  # Entity type boosts
  entity_boosts:
    tutorial: 1.5
    howto: 1.4
    technology: 1.3

  # Document type weights
  document_type_weights:
    markdown: 1.2
    pdf: 0.8
```

### 3. **Token-Efficient System Prompts**
Prompts present all evidence concisely:

```liquid
Q: {{ query }}
Terms: {{ salient_terms | join: ", " }}
Entities: {{ entities | join: ", " }}

SOURCES ({{ source_count }}):
{% for source in sources %}
[{{ source.number }}] {{ source.title }}
RRF={{ source.rrf_score | round: 2 }} Dense={{ source.dense_score | round: 2 }} BM25={{ source.bm25_score | round: 2 }}
Match: {{ source.matched_salient_terms | join: "," }}
{{ source.text }}
---
{% endfor %}

Answer using sources. Cite as [1],[2].
```

### 4. **Auto Context Size Detection**
Automatically adjusts source count based on detected model context window:

```yaml
context:
  auto_detect_size: true
  fallback_context_size: 4096
  reserve_output_tokens: 2000
  avg_tokens_per_source: 300
  # Formula: max_sources = (context_size - reserve) / avg_per_source
```

### 5. **YAML Inheritance**
Lenses can inherit from other lenses with overrides:

```yaml
name: legal
inherits: blog  # Inherit all blog settings
# Override specific settings
scoring:
  bm25_weight: 0.40  # Higher for exact legal terms
policies:
  require_citations_for_claims: true
```

## Component Types

### Lenses
**Purpose:** Presentation/formatting layer for search results

**Key Sections:**
- `taxonomy`: domain, style, audience
- `scoring`: RRF weights + fine-grained signal weights
- `templates`: inline Liquid templates (system_prompt, citation, response)
- `styles`: inline CSS
- `policies`: guardrails (no_pii, require_citations, etc.)
- `defaults`: all configuration parameters

**Examples:**
- `blog.lens.yaml` - Conversational blog presentation
- `legal.lens.yaml` - Formal legal citations (inherits from blog)
- `technical.lens.yaml` - Technical docs with code (inherits from blog)
- `research.lens.yaml` - Academic papers with advanced signals (inherits from technical)

### Waves
**Purpose:** RAG pipeline stages (retrieval, ranking, synthesis)

**Key Sections:**
- `taxonomy`: kind (sensor/analyzer/retriever/ranker/synthesizer)
- `input`/`output`: signal contracts
- `triggers`: execution conditions
- `emits`: signals produced
- `defaults`: all wave parameters

**Examples:**
- `dense-retrieval.wave.yaml` - Embedding search
- `bm25-retrieval.wave.yaml` - Keyword search
- `salience-scoring.wave.yaml` - Salient term matching
- `rrf-ranker.wave.yaml` - Reciprocal Rank Fusion
- `llm-synthesis.wave.yaml` - Answer generation with context management

### Processors
**Purpose:** Document processing (parsing, chunking, extraction)

**Key Sections:**
- `taxonomy`: kind (parser/chunker/embedder/extractor)
- `input`: MIME types, file extensions, max file size
- `output`: produces, metadata, signals
- `capabilities`: OCR, tables, streaming, etc.
- `defaults`: all processor parameters

**Examples:**
- `pdf-parser.processor.yaml` - PDF extraction with OCR
- `markdown-chunker.processor.yaml` - Semantic markdown chunking
- `entity-extractor.processor.yaml` - GraphRAG entity extraction

### Pipelines
**Purpose:** Orchestrate multiple waves into execution flow

**Key Sections:**
- `stages`: execution stages with dependencies
- `lanes`: concurrency pools (fast, normal, ml, llm)
- `config`: timeouts, early exit conditions, failure handling
- `defaults`: all pipeline parameters

**Examples:**
- `rag-search.pipeline.yaml` - Main RAG retrieval pipeline

## SourceCitation - Full Signal Exposure

The `SourceCitation` record now exposes ALL signals:

```csharp
public record SourceCitation(
    int Number,
    Guid DocumentId,
    string DocumentName,
    string SegmentId,
    string Text,
    string? PageOrSection = null,
    // Signal scores
    double RrfScore = 0.0,
    double DenseScore = 0.0,
    double Bm25Score = 0.0,
    double SalienceScore = 0.0,
    double FreshnessScore = 0.0,
    // Matching information
    List<string>? MatchedSalientTerms = null,
    List<string>? MatchedEntities = null,
    List<string>? SignalExplanations = null,
    // Metadata
    string? Author = null,
    string? PublishDate = null,
    string? DocumentType = null,
    Dictionary<string, object>? Metadata = null
);
```

## Signal Weight Configuration

The `SignalWeight` class provides fine-grained control:

```csharp
public sealed class SignalWeight
{
    public double Weight { get; set; } = 1.0;
    public double? Boost { get; set; }
    public double? MinThreshold { get; set; }
    public double? MaxCap { get; set; }
    public bool Normalize { get; set; } = true;
    public string? Decay { get; set; }  // none|linear|exponential|logarithmic
}
```

## Workflow Builder Integration

All components can be visualized in StyloFlow Workflow Builder:

- **Waves** → Nodes with input/output contracts
- **Pipelines** → Directed graphs with stage dependencies
- **Signals** → Edges showing data flow
- **Lanes** → Concurrency visualization

## Configuration Hierarchy

**Three-tier configuration system:**

1. **appsettings.json** - Override YAML defaults
   ```json
   {
     "Lenses": {
       "Blog": {
         "Retrieval": { "top_k": 15 }
       }
     }
   }
   ```

2. **YAML manifests** - Primary configuration
   ```yaml
   defaults:
     retrieval:
       top_k: 10
   ```

3. **Code defaults** - Fallback if not in YAML

## Service Registration

```csharp
// Register YAML manifest system
builder.Services.AddYamlLenses(builder.Configuration, useEmbedded: false);
builder.Services.AddYamlWaves(builder.Configuration, useEmbedded: false);
builder.Services.AddYamlProcessors(builder.Configuration, useEmbedded: false);

// Or register all at once
builder.Services.AddYamlManifestSystem(builder.Configuration);
```

## Manifest Inheritance Resolution

```csharp
var resolver = new ManifestInheritanceResolver<LensManifest>(loader, logger);
var resolved = await resolver.ResolveInheritanceAsync(manifest, ct);
```

Supports:
- Recursive inheritance (A inherits B inherits C)
- Deep merging of nested objects
- Dictionary merging (child overrides parent)
- Circular dependency detection

## Key Files

### Models
- `src/LucidRAG.Core/Manifests/LensManifestModels.cs`
- `src/LucidRAG.Core/Manifests/WaveManifestModels.cs`
- `src/LucidRAG.Core/Manifests/ProcessorManifestModels.cs`
- `src/LucidRAG.Core/Manifests/PipelineManifestModels.cs`

### Loaders
- `src/LucidRAG.Core/Manifests/FileSystemManifestLoader.cs`
- `src/LucidRAG.Core/Manifests/EmbeddedManifestLoader.cs`
- `src/LucidRAG.Core/Manifests/ManifestInheritanceResolver.cs`

### Services
- `src/LucidRAG.Core/Services/Lenses/YamlLensLoader.cs`
- `src/LucidRAG.Core/Services/Lenses/LensRegistry.cs`
- `src/LucidRAG.Core/Services/Waves/WaveRegistry.cs`
- `src/LucidRAG.Core/Extensions/ManifestServiceExtensions.cs`

### Manifests
- `src/LucidRAG/manifests/lenses/*.lens.yaml`
- `src/LucidRAG/manifests/waves/*.wave.yaml`
- `src/LucidRAG/manifests/processors/*.processor.yaml`
- `src/LucidRAG/manifests/pipelines/*.pipeline.yaml`

## Future Enhancements

1. **Wave Orchestrator** - Execute pipelines using wave manifests
2. **Policy Enforcement** - Implement lens policies (no_pii, require_citations)
3. **Signal Matcher** - Pattern matching for signal weights (wildcards, regex)
4. **Cost Tracking** - Track LLM costs per signal/wave
5. **Manifest Validator** - Schema validation for all manifest types
6. **Hot Reload** - Runtime manifest updates without restart
7. **Workflow UI** - Visual pipeline editor using workflow builder

## Summary

The YAML manifest system provides:
- ✅ **Zero hardcoded values** - Everything configurable
- ✅ **Full signal transparency** - LLM sees all retrieval signals
- ✅ **Fine-grained control** - Weight specific signals/classes
- ✅ **Token efficiency** - Concise prompts with all evidence
- ✅ **Auto context management** - Detects model limits
- ✅ **Inheritance** - Lenses extend other lenses
- ✅ **Workflow visualization** - Ready for workflow builder
- ✅ **StyloFlow compatible** - Follows established patterns
