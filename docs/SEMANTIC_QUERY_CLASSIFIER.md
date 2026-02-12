# Semantic Query Classifier v2.5

Deterministic, embedding-based query classification for DoomSummarizer.
Classifies user queries into topic, type, vibe, and escalation signals using cosine
similarity against pre-embedded exemplar questions. No LLM round-trip needed for most queries.

## Complete Flow: Query to Sources

This section traces the full path a user query takes from input to source selection.

```
User types: "latest AI news"
    |
    v
PromptInterpreter.InterpretAsync(prompt)
    |
    |--- 1. GetRouterAsync() --- initializes classifier + source router on first call
    |
    |--- 2. QueryClassifier.ClassifyAsync(prompt)    [< 2ms, deterministic]
    |         |
    |         |-- Extract structural features         [< 0.02ms, regex]
    |         |-- Embed query via ONNX                [~1ms]
    |         |-- Score all 444 exemplars              [< 0.5ms, SIMD]
    |         |-- IDF-weighted voting (topic + type)
    |         |-- Detect vibe, composite, complex
    |         |
    |         v
    |    QueryClassification {
    |      Categories: { ai: 1.51, technology: 0.92 }
    |      QueryType: "roundup" (confidence: 1.12)
    |      Vibe: null
    |      IsComposite: false, IsComplex: false
    |      BestMatchScore: 0.75
    |    }
    |
    |--- 3. Decision gate
    |         |
    |         |-- BestMatchScore >= 0.55
    |         |   AND NOT composite
    |         |   AND NOT complex
    |         |     |
    |         |     v  STRONG MATCH PATH
    |         |     BuildIntentFromClassification() -> SentinelIntent
    |         |     SentinelSourceMapper.ToInterpretedPrompt()
    |         |     -> Sources: [gnews:AI, hn, reddit:machinelearning]
    |         |     -> Sentinel LLM SKIPPED (saved 200-800ms)
    |         |
    |         |-- Otherwise: SENTINEL PATH
    |               |
    |               v
    |         Sentinel LLM (Ollama JSON mode, ~200-800ms)
    |         -> search_queries, subqueries, filter_keywords
    |         -> Categories STILL from embedding (sentinel provides decomposition only)
    |         -> SentinelSourceMapper.ToInterpretedPrompt()
    |
    v
InterpretedPrompt {
  Sources: ["gnews:AI", "hn", "reddit:machinelearning"]
  Topics: ["AI"]
  Vibe: "neutral"
  SearchQueries: ["AI"]
}
```

### The Three Paths

| Path | When | Latency | What happens |
|------|------|---------|-------------|
| **Strong match** | BestMatch >= 0.55, not composite, not complex | < 2ms | Embedding classification used directly. Sentinel skipped. |
| **Sentinel** | Weak match OR composite OR complex | 200-800ms | Sentinel LLM called for decomposition. Categories still from embedding. |
| **Fallback** | Sentinel unavailable or fails | < 5ms | Keyword-based vibe + embedding categories for routing. |

### Composite Flow (Multi-Part Queries)

```
User: "AI news and also what's happening in politics"
    |
    v
Classifier detects:
  IsComposite: true  (top-2 composite exemplars above threshold)
  HasCompositeConjunction: true  ("and also" regex match)
    |
    v
PromptInterpreter: needsSentinel = true (composite)
    |
    v
Sentinel LLM decomposes:
  subqueries: ["What's the latest AI news?", "What's happening in politics?"]
  search_queries: ["AI news 2026", "politics news today"]
  categories: { ai: 1.51, politics: 0.87 }  <-- from EMBEDDING, not sentinel
    |
    v
Both subquery topics get routed to their sources independently
```

### Vibe Enrichment Flow

Vibe detection has two layers that merge:

```
1. Embedding classifier (with ratio guard):
   Query matches vibe-tagged exemplar with sim > 0.60
   AND vibe sim >= 85% of best overall match
   -> Vibe: "doom" (confidence: 0.72)

   Why ratio guard? Without it, "tech news" gets vibe=snarky because
   "Roast the latest tech announcements" (snarky) shares the tech semantic
   space at 0.71. But "What's the latest tech news?" (non-vibe) matches at
   1.0 — the ratio 0.71/1.0 = 0.71 < 0.85, so the false vibe is blocked.

2. Keyword fallback (in FallbackInterpretAsync):
   prompt.Contains("doom") -> Vibe: "doom"

3. Merge rule:
   - If keyword detected vibe, use it (explicit intent)
   - If keyword returned "neutral" but embedding detected vibe with
     confidence > 0.50, use embedding vibe (catches phrasing-based intent)
```

## Architecture

### Components

| File | Role |
|------|------|
| `QueryClassifier.cs` | Core classifier: init, scoring, weighted voting |
| `QueryFeatures.cs` | Structural feature extraction (7 regexes, sub-0.02ms) |
| `QueryExemplar.cs` | Data models: exemplar, classification result |
| `DoomConfig.cs` | `ClassifierConfig` record: all thresholds |
| `PromptInterpreter.cs` | Integration: classifier -> sentinel -> routing |
| `CommandBootstrap.cs` | Wiring: loads config, creates shared classifier |
| `ExemplarsCommand.cs` | CLI: list, init, rebuild, validate, test, benchmark |
| `default-exemplars.yaml` | 444 embedded exemplar questions |

### Initialization

At startup, `CommandBootstrap.CreateAsync` does:

1. Loads `ClassifierConfig` from YAML config
2. Calls `PromptInterpreter.ConfigureClassifier(config.Classifier)` with custom thresholds
3. On first query, `GetRouterAsync()` calls `QueryClassifier.InitializeAsync(embedding)`:
   - Loads all exemplars (embedded defaults + user YAML overrides)
   - Batch-embeds all 444 questions in a single ONNX call (~150ms on GPU)
   - Computes IDF weights per topic and type label
4. Subsequent queries score against the pre-embedded exemplars (< 2ms each)

### Per-Query Classification (ClassifyAsync)

```
1. Extract structural features     <- QueryFeatures.Extract()  (< 0.02ms)
   7 pre-compiled regexes: question words, howto, comparison,
   search-only, QA, composite conjunction, imperative verbs

2. Embed query                     <- IEmbeddingService.EmbedAsync()  (~1ms ONNX)

3. Score ALL exemplars              <- single pass, SIMD cosine similarity
   Simultaneously tracks in one loop:
   - Candidate set (sim >= 0.35)
   - Best overall match
   - Best vibe match (raw similarity, only vibe-tagged exemplars)
   - Composite top-2 scores (consensus check)
   - Complex flag (any complex exemplar above threshold)

4. IDF-weighted voting              <- per topic, per type
   score(label) = max_sim + CountBoost * log2(count) * idf(label)

5. Feature-based adjustments        <- short queries only (<= 4 words)
   Howto/comparison/QA markers boost respective type scores
   Search-only pattern -> force search_only type
   No markers -> boost roundup (short queries default to news browse)

6. Vibe detection                   <- raw similarity + ratio guard
   Best vibe exemplar match > 0.60 AND >= 85% of best overall -> detected vibe
   (prevents false vibes when neutral query matches non-vibe exemplars much better)

7. Complex detection                <- raw similarity + ratio guard
   Best complex exemplar match > 0.50 AND >= 85% of best overall -> flag IsComplex
   (prevents false sentinel escalation on simple queries that share topic space)

8. Composite detection              <- consensus + multi-topic heuristic
   Both top-2 composite matches above 0.82 -> flag IsComposite
   Feature-based conjunction ("and also", "and compare", ";", "/", "+") -> relax by 15%
   Multi-topic fallback: 2+ strong topics (>0.70) + punctuation separator -> composite

9. Short-query confidence scaling   <- type confidence * 0.85 for short queries

10. Return QueryClassification
```

## The Scoring Algorithm

### IDF-Weighted Multi-Match Voting

Combines three statistical principles:

**1. Max anchoring** -- the best individual exemplar match dominates the score.
A single 0.92 match outweighs many mediocre matches.

**2. Logarithmic count boost** -- additional matches contribute diminishing returns.
`log2(count)` means 2 matches add 1.0, 4 add 2.0, 8 add 3.0.

**3. IDF weighting** -- rare labels get stronger count boosts.
"howto" (15 exemplars) gets a higher IDF weight than "roundup" (80 exemplars),
so 5 howto matches can out-score 40 roundup matches when quality is similar.

```
score(label) = max_sim(label) + CountBoost * log2(count(label)) * idf(label)

where:
  max_sim(label)  = highest cosine similarity among candidates with this label
  count(label)    = number of candidates with this label
  CountBoost      = 0.05 (configurable)
  idf(label)      = log2(1 + total_exemplars / exemplars_with_label)
```

**Concrete example with IDF:**

```
Given 444 total exemplars:
  roundup:  80 exemplars -> idf = log2(1 + 444/80)  = 2.76
  howto:    15 exemplars -> idf = log2(1 + 444/15)  = 4.93
  composite: 12 exemplars -> idf = log2(1 + 444/12) = 5.25

Query: "Docker help"
  roundup: max=0.52, count=30 -> 0.52 + 0.05 * 4.9 * 2.76 = 1.20
  howto:   max=0.58, count=4  -> 0.58 + 0.05 * 2.0 * 4.93 = 1.07
  -> roundup wins (more exemplars matched, higher total)

Query: "How do I configure Docker networking?"
  roundup: max=0.45, count=25 -> 0.45 + 0.05 * 4.6 * 2.76 = 1.09
  howto:   max=0.82, count=6  -> 0.82 + 0.05 * 2.6 * 4.93 = 1.46
  -> howto wins (strong individual match + IDF boost for rare type)
```

### Why Not Just Max Similarity?

Max-per-group (Phase 1 approach) was fragile: a single high-scoring exemplar in "roundup"
could beat consistent "howto" matches. Multi-match voting uses the collective signal.
When 6 different howto exemplars all match at 0.6-0.8, that's more reliable than 1 roundup
exemplar matching at 0.85 (which might be vocabulary overlap rather than true semantic match).

### Vibe and Composite: Raw Similarity, Not Voting

Vibe and composite use **raw max similarity**, not the IDF-weighted vote:

- **Vibe**: vocabulary overlap causes false positives with voting. "latest tech news" partially
  matches "Roast the latest tech announcements" (snarky) via shared "latest tech". Raw
  similarity requires a genuine semantic match (> 0.60).

- **Composite**: requires **consensus** -- top 2 composite exemplar matches must both be strong
  (> 0.82). Prevents "latest tech news" from matching "Summarize tech news and also politics"
  just because they share "tech news". Threshold is relaxed 15% when structural conjunction
  patterns are present ("and also", "and compare", etc.).

## Short-Query Feature Decomposition

Embeddings produce noisy similarity scores on short queries (1-4 words) because there's
insufficient context for semantic differentiation. "Docker help" matches both howto and roundup
exemplars at similar scores. Structural features provide discriminative signals that embeddings miss.

### Feature Extraction

`QueryFeatures.Extract(query)` runs 7 pre-compiled source-generated regexes in under 0.02ms:

| Feature | Pattern | What it detects |
|---------|---------|-----------------|
| `HasQuestionWord` | `^(how\|what\|why\|when\|who\|where\|which)` | Question intent |
| `HasHowtoMarker` | `how (do\|can\|to)\|set up\|configure\|tutorial` | Howto/tutorial intent |
| `HasComparisonMarker` | `compare\|vs\|versus\|difference between` | Comparison intent |
| `HasSearchOnlyMarker` | `convert\|define\|population of\|what time` | Factual lookup intent |
| `HasQaMarker` | `^(what is\|who is\|where is)` | Direct Q&A intent |
| `HasCompositeConjunction` | `and also\|and compare\|as well as\|along with` | Multi-part query |
| `HasImperativeVerb` | `^(show\|get\|find\|tell\|list\|summarize)` | Action/fetch intent |

### Feature-Based Type Adjustments

Applied **only when the query has 4 or fewer words** (configurable via `short_query_max_words`):

- Howto marker detected -> boost "howto" type score by +0.12
- Comparison marker detected -> boost "comparison" type score by +0.12
- QA marker detected (and not search-only) -> boost "qa" type score by +0.10
- No intent markers at all -> boost "roundup" by +0.08 (short queries without intent are news browsing)

### Search-Only Fast Path

`HasSearchOnlyMarker` applies to **all query lengths** (not just short queries). Patterns like
"convert X to Y", "define Z", "what time in Tokyo" are unambiguously factual lookups regardless
of how the embedding scores them. When detected, the type is forced to `search_only` unless
the embedding strongly disagrees (best match > 0.85 for a non-search-only type).

## Exemplar System

### Structure

Each exemplar is a representative question with classification metadata:

```yaml
exemplars:
  - question: "How do I set up Docker?"
    topic: technology
    type: howto

  - question: "Give me the most depressing news you can find"
    topic: default
    type: roundup
    vibe: doom

  - question: "What are the second-order effects of rising interest rates?"
    topic: business
    type: deep_dive
    complexity: complex

  - question: "Tell me about AI news and also what's going on in politics"
    topic: ai
    type: composite
```

### Fields

| Field | Required | Values | Purpose |
|-------|----------|--------|---------|
| `question` | Yes | Free text | The representative query (gets embedded) |
| `topic` | Yes | technology, ai, programming, science, health, business, finance, politics, world, entertainment, sports, space, security, gaming, environment, crime, flooding, pharma, satire, food, transport, uk, education, default | Routing category |
| `type` | Yes | roundup, qa, howto, deep_dive, comparison, composite, search_only, trend, news | Query intent type |
| `sources` | No | List of source hints: hn, reddit, bbc, reuters, etc. | Preferred sources for this exemplar |
| `vibe` | No | doom, hopeful, snarky, funny, upbeat, friendly, toon, neutral, concise | Detected tone from query phrasing |
| `complexity` | No | simple, complex | Whether the query needs nuanced sentinel analysis |

### Coverage (v2.5)

- **444 exemplars** across 24 topics and 9 types
- ~20 topics with 10+ exemplars each (enough for multi-match voting)
- 12+ composite exemplars for multi-part query detection
- 5+ complex exemplars for sentinel escalation
- 7 vibes covered: doom, hopeful, snarky, funny, upbeat, friendly, toon
- 10+ search_only exemplars for factual lookup detection

### Exemplar Design Principles

1. **Pattern-based, not entity-specific** -- "Latest news from [region]" not "What happened to Boris Johnson?"
   NER already handles entity extraction; exemplars capture query *patterns*.

2. **Sufficient per-topic density** -- at least 5-10 exemplars per topic so multi-match voting
   gets meaningful count signals, not just single-match luck.

3. **Type diversity within topics** -- each topic should have roundup + at least one other type
   (howto, qa, deep_dive, comparison) to allow type disambiguation.

4. **Vibe exemplars use distinctive phrasing** -- "doom-scroll the worst headlines" not just
   "bad news". The embedding needs to distinguish phrasing, not just topic words.

### User Exemplars

Users can extend or override defaults by adding YAML files to `~/.doomsummarizer/exemplars/`:

```bash
# Create the directory with a template
doomsummarizer exemplars --init

# Edit ~/.doomsummarizer/exemplars/my-exemplars.yaml
# Then rebuild embeddings:
doomsummarizer exemplars --rebuild
```

User exemplars with the same question text as a default override the default's metadata.
New questions are added to the exemplar set.

## Configuration

All thresholds are in the `classifier:` section of config YAML:

```yaml
classifier:
  # -- Core Thresholds --
  min_candidate_threshold: 0.35    # Cosine sim floor to enter candidate set
  min_topic_threshold: 0.35        # Weighted vote floor for topic inclusion
  min_type_threshold: 0.30         # Weighted vote floor for type detection
  count_boost: 0.05                # IDF count boost multiplier

  # -- Detection Thresholds --
  complex_threshold: 0.50          # Raw sim for complex exemplar flagging
  complex_min_ratio: 0.85          # Complex must be >= 85% of best overall
  vibe_threshold: 0.60             # Raw sim for vibe detection
  vibe_min_ratio: 0.85             # Vibe must be >= 85% of best overall
  composite_raw_threshold: 0.82    # Raw sim consensus for composite detection

  # -- Short-Query Features --
  short_query_max_words: 4         # Word count threshold for "short" query
  howto_feature_boost: 0.12        # Type boost for howto intent markers
  comparison_feature_boost: 0.12   # Type boost for comparison intent markers
  default_roundup_boost: 0.08      # Type boost for unmarked short queries
  qa_feature_boost: 0.10           # Type boost for QA intent markers
  search_only_feature_threshold: 0.60  # Min confidence for search_only override
  synonym_expansion_enabled: false # Expand abbreviations before embedding
  short_query_confidence_scale: 0.85   # Scale confidence for short queries
```

### Tuning Checklist

When something goes wrong, start here:

| Symptom | Knob | Direction |
|---------|------|-----------|
| Neutral queries get a vibe (e.g. "tech news" → snarky) | `vibe_min_ratio` | Raise toward 0.90 |
| Intentional vibes not detected ("doom-scroll the worst news" → null) | `vibe_threshold` | Lower toward 0.55 |
| Simple queries trigger sentinel via complex flag | `complex_min_ratio` | Raise toward 0.90 |
| Too many queries skip sentinel (poor decomposition) | `StrongMatchThreshold` in PromptInterpreter | Raise toward 0.65 |
| Too few queries skip sentinel (unnecessary latency) | `StrongMatchThreshold` | Lower toward 0.45 |
| Non-composite queries flagged composite | `composite_raw_threshold` | Raise toward 0.85 |
| Composite queries missed | Add composite exemplars or punctuation patterns to `CompositeConjunctionPattern` |
| Wrong topic on short queries (2-3 words) | Add more exemplars for that topic (need 5+ per topic for voting to work) |
| Type detection returns "roundup" for everything | `default_roundup_boost` | Lower to 0.04 |
| Short queries over-confident | `short_query_confidence_scale` | Lower to 0.70 |

**Model calibration warning:** All thresholds are calibrated for `all-MiniLM-L6-v2` (384-dim).
Cosine similarity distributions vary between embedding models and quantization settings.
If you swap the ONNX model, run `exemplars --test` and `exemplars --benchmark` to recalibrate.
A quick sanity check: run `exemplars --rebuild` and verify that sample queries get reasonable scores.
The strong-match threshold (0.55) should sit around P90-P95 of non-self exemplar-to-exemplar similarities.

**Device profiles:**
- Desktop with GPU: defaults are fine
- Raspberry Pi: consider raising `min_candidate_threshold` to 0.40 (fewer candidates to vote on)

### Exemplar Design Rules

- **Minimum coverage:** Don't add a topic unless you can supply at least 5 exemplars across 2+ types.
  Thin topics with 1-2 exemplars win IDF lottery (rare = high weight) and can beat broader topics unexpectedly.
- **Pattern over entity:** "Latest news from [region]" not "What happened to Boris Johnson?"
  NER handles entities; exemplars capture query *structure*.
- **Tag vibes on vibe-adjacent exemplars.** If an exemplar's question clearly carries a tone
  ("Funniest news stories"), tag it with `vibe: funny`. Untagged exemplars in the same semantic
  space will block vibe detection via the ratio guard.

### Score Naming

Two different score types appear in output — don't confuse them:

- **BestMatchScore** (0.0–1.0): Raw cosine similarity to the single closest exemplar. This is what the strong-match gate (0.55) checks.
- **Category/Type scores** (can exceed 1.0): IDF-weighted voted scores: `max_sim + CountBoost * log2(count) * idf`. Multiple matching exemplars boost the score above the raw cosine sim.

Example: `ai=1.51` means the best AI exemplar scored 0.75 cosine sim, but 30+ AI exemplars voted with IDF weight, pushing the aggregate above 1.0.

### Example: Misclassification and Fix

**Before ratio guard** — "What's the latest tech news?" returned `vibe=snarky (0.71)`:

```
Best overall: "What's the latest tech news?" at 1.00 (technology/roundup, NO vibe)
Best vibe:    "Roast the latest tech announcements" at 0.71 (technology/roundup, vibe=snarky)

Old check: 0.71 > vibe_threshold(0.60) → vibe=snarky  ← WRONG
Ratio:     0.71 / 1.00 = 0.71 < vibe_min_ratio(0.85) → vibe=null  ← FIXED
```

The snarky exemplar shares the tech-news semantic neighborhood but isn't actually
closer to the query than the neutral exemplar. The ratio guard catches this.

## CLI Commands

```bash
# Show exemplar summary (topic/type counts, user dir status)
doomsummarizer exemplars

# List all exemplars in a table
doomsummarizer exemplars --list

# Create user exemplar directory with template
doomsummarizer exemplars --init

# Re-embed all exemplars and show sample classifications
doomsummarizer exemplars --rebuild

# Validate YAML files for errors
doomsummarizer exemplars --validate

# Quiet rebuild (no sample output)
doomsummarizer exemplars --rebuild --quiet

# Use specific GPU for rebuild
doomsummarizer exemplars --rebuild --gpu 1
```

### Diagnostic Test

Run the full 88-query test matrix to verify classification accuracy across all dimensions:

```bash
# Compact mode: shows only failures
doomsummarizer exemplars --test

# Verbose mode: full per-query breakdown (top matches, features, scores)
doomsummarizer exemplars --test -v
```

The test matrix covers:
- **33 topic detection** tests (all major + niche topics)
- **27 type detection** tests (roundup, howto, comparison, deep_dive, search_only, qa, news)
- **17 vibe detection** tests (doom, snarky, hopeful, funny + 4 negative "should be neutral" assertions)
- **8 composite detection** tests (4 positive, 3 negative)
- **5 complex detection** tests (2 positive, 2 negative)

Current results: **82/82 (100%)** across all dimensions.

### Debug Output

With `scroll "query" --debug`, the classifier output appears as:

```
Embedding: technology=0.93, ai=0.54 | type=roundup (0.93) | vibe=doom (0.72) | composite | complex
  Top matches: "What's the latest tech news?" (0.93), "Show me programming articles" (0.71), ...
Final: technology=0.93 | intent=roundup | vibe=doom
```

The `--rebuild` command shows a classification table with flags:
- **C** = composite detected
- **X** = complex detected
- **F** = structural features contributed (short query)

## Classification Result

`QueryClassification` contains all extracted signals:

| Property | Type | Description |
|----------|------|-------------|
| `Categories` | `Dictionary<string, double>` | Topic -> weighted vote score |
| `QueryType` | `string` | Best detected type (roundup, howto, qa, etc.) |
| `QueryTypeConfidence` | `double` | Confidence score for the detected type |
| `Vibe` | `string?` | Detected vibe (doom, snarky, etc.) or null |
| `VibeConfidence` | `double` | Confidence of vibe detection |
| `IsComposite` | `bool` | Multi-part query needing decomposition |
| `IsComplex` | `bool` | Complex query benefiting from sentinel nuance |
| `SourceHints` | `List<string>?` | Preferred sources from best-matching exemplar |
| `BestMatch` | `string?` | Best-matching exemplar question (debug) |
| `BestMatchScore` | `double` | Similarity of the best match |
| `TopMatches` | `List<ScoredExemplar>` | Top 5 matches (debug) |
| `Features` | `object?` | Structural features for short queries |

## Performance

| Operation | Time | Notes |
|-----------|------|-------|
| Initialization (batch embed 444 exemplars) | ~150ms | Single ONNX call, once at startup |
| Feature extraction | < 0.02ms | Pre-compiled source-generated regexes |
| Query embedding | ~1ms | Single ONNX inference |
| Score + vote (single pass, 443 exemplars) | < 0.3ms | SIMD cosine sim + inline voting |
| **Total per-query** | **< 2ms** | vs 200-800ms for sentinel LLM |

**Benchmark** (`exemplars --benchmark --iterations 100`, 1000 samples):

| Metric | Value |
|--------|-------|
| p50 | 241 us |
| p95 | 263 us |
| p99 | 275 us |
| Min | 209 us |
| Max | 585 us |

Memory: ~443 x 384 x 4 bytes = ~680KB for exemplar embeddings (all-MiniLM-L6-v2, 384 dimensions).
Single-pass architecture: no intermediate list allocations — score, vote, and track top-5 in one loop.

## Test Coverage

875 tests pass, including:

- **Unit tests**: Exemplar loading, count, types, vibes, complexity, no duplicates, topic coverage
- **Integration tests** (ONNX): Topic detection (15 queries), type detection (5 types),
  vibe detection (4 vibes), composite detection (3 queries), complex detection (2 queries),
  search-only detection (4 queries), strong match rate > 70%, determinism (3 runs identical),
  niche topic coverage (flooding, crime, pharma, satire, gaming, food, transport)
- **Diagnostic test matrix**: 87 prompts (including negative vibe/complex assertions), 100% pass rate

```bash
# Unit tests (no ONNX needed)
dotnet test src/DoomSummarizer.Tests/ --filter "Category!=Browser&Category!=Integration"

# Integration tests (requires ONNX model files)
dotnet test src/DoomSummarizer.Tests/ --filter "Category=Integration"

# Diagnostic test (full 87-query matrix)
doomsummarizer exemplars --test
```

## Evolution History

| Phase | What Changed |
|-------|-------------|
| 1.0 | Max-per-group scoring, 76 exemplars, topic + type only |
| 2.0 | Multi-match IDF-weighted voting, 444 exemplars, vibe/composite/complex detection |
| 2.5 | Short-query feature decomposition, single-pass inline voting, bounded top-5 tracking, centroid filter removed, all thresholds YAML-configurable, diagnostic test matrix (87 queries, 100% pass), tuned thresholds (vibe 0.60, composite 0.82), expanded composite conjunction detection, ratio guards for vibe/complex (prevents false positives on neutral queries), p99 275us |
