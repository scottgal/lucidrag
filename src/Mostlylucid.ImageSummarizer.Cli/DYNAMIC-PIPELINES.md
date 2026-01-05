# Dynamic Pipelines

## When to Use What

| Approach | When to Use |
|----------|-------------|
| `--pipeline caption` | **Primary** - Standard workflows, most users |
| `--signals "motion.*"` | Filter output to specific signals |
| `--pipeline-file custom.yaml` | **Advanced** - Custom signal selection, team configs |

**Start with `--pipeline`** - it covers most use cases. Use `--pipeline-file` only for advanced customization.

---

Dynamic pipelines allow you to define custom image analysis workflows using YAML. Instead of using preset pipelines, you can specify exactly which signals you need - and only those analyzers will run.

## The Ephemeral Pattern

The key insight behind dynamic pipelines is the **ephemeral pattern**:

1. **Waves emit signals** - Each analyzer (wave) declares what signals it produces
2. **Waves listen for signals** - Waves can depend on signals from other waves
3. **Signal request drives execution** - When you request specific signals, only the waves that emit (or are needed for) those signals run
4. **Early exit / escalation** - Fast local analyzers run first; if they fail, escalate to more powerful (but slower) LLM analyzers

This means requesting `identity.sha256` runs only the IdentityWave (~5ms), while requesting `vision.llm.caption` runs the full cascade through to VisionLlmWave.

## Quick Start

```bash
# Get sample pipeline template
imagesummarizer sample-pipeline > my-pipeline.yaml

# Edit the pipeline
vim my-pipeline.yaml

# Use it
imagesummarizer image.gif --pipeline-file my-pipeline.yaml

# Or pipe it
cat my-pipeline.yaml | imagesummarizer image.gif --pipeline-file -
```

## Signal-Based Selection

Request only the signals you need:

```yaml
name: fast-metadata
signals:
  - identity.*          # All identity signals
  - color.dominant*     # Dominant color info
```

Use predefined collections with `@`:

```yaml
signals:
  - "@motion"           # motion.*, complexity.*
  - "@alttext"          # vision.llm.caption, content.text*, motion.summary
  - "@tool"             # Signals useful for MCP/automation
```

Run `imagesummarizer list-signals` to see all available signals and collections.

## YAML Schema Reference

```yaml
# Required
name: my-pipeline           # Pipeline name for display/logging
version: 1                  # Schema version (always 1 for now)

# Optional description
description: What this pipeline does

# Signal-based selection (glob patterns)
signals:
  - identity.*              # Wildcard: all identity signals
  - color.dominant*         # Prefix match: dominant_rgb, dominant_name
  - "@motion"               # Collection: predefined signal group
  - vision.llm.caption      # Exact match: specific signal

# Alternative: Wave-based selection (by analyzer name)
waves:
  - IdentityWave
  - ColorWave
  - Florence2Wave

# Output configuration
output:
  format: json              # json, text, alttext, markdown, visual, signals
  include_metadata: true    # Include signal metadata in output
  include_confidence: true  # Include confidence scores

# LLM configuration
llm:
  enabled: true             # Enable/disable Vision LLM
  model: minicpm-v:8b       # Ollama model name
  ollama_url: http://localhost:11434
  fast_mode: false          # Skip heuristics, go straight to LLM
  context: true             # Include analysis signals in LLM prompt

# Escalation behavior
escalation:
  gif_to_llm: true          # Always use LLM for GIFs
  complexity_threshold: 0.4 # Edge density threshold for LLM escalation
  on_no_caption: true       # Escalate if fast path produces no caption
  min_caption_length: 20    # Escalate if caption is too short
```

## Glob Patterns

| Pattern | Matches | Example |
|---------|---------|---------|
| `identity.*` | All signals in identity namespace | identity.sha256, identity.format |
| `color.dominant*` | Prefix match | color.dominant_rgb, color.dominant_name |
| `vision.llm.caption` | Exact match | Only that specific signal |
| `@motion` | Collection expansion | motion.*, complexity.* |
| `*` | Everything | Full pipeline |

## Predefined Collections

| Collection | Signals | Use Case |
|------------|---------|----------|
| `@identity` | identity.* | File metadata, hashing |
| `@motion` | motion.*, complexity.* | Animation analysis |
| `@color` | color.* | Color extraction |
| `@quality` | quality.* | Image quality metrics |
| `@text` | content.text*, ocr.*, vision.llm.text | Text extraction |
| `@vision` | vision.* | Vision LLM outputs |
| `@alttext` | vision.llm.caption, content.text*, motion.summary | Alt text generation |
| `@tool` | identity.*, color.dominant*, motion.*, vision.llm.*, ocr.voting.* | MCP/automation |
| `@all` | * | Full pipeline |

## Wave Dependency Chain

Signals flow through waves in priority order:

```
Priority  Wave              Emits                    Depends On
───────── ───────────────── ──────────────────────── ──────────────────
10        IdentityWave      identity.*               (none)
20        ColorWave         color.*, quality.*       (none)
25        MotionWave        motion.*, complexity.*   identity.is_animated
30        OcrWave           ocr.text, content.*      content.text_likeliness
55        Florence2Wave     florence2.*, vision.*    color.dominant_*
60        VisionLlmWave     vision.llm.*             color.*, motion.*, ocr.*
```

When you request `vision.llm.caption`, the orchestrator traces the dependency chain:
1. VisionLlmWave emits it → needs color, motion, ocr
2. ColorWave, MotionWave, OcrWave run first
3. MotionWave needs identity.is_animated → IdentityWave runs first

## Example Pipelines

### Fast Deduplication

```yaml
name: fast-dedupe
signals:
  - identity.sha256
  - identity.format
  - clip.embedding      # Perceptual similarity
output:
  format: json
llm:
  enabled: false
```

### Motion Analysis

```yaml
name: motion-analysis
signals:
  - identity.*
  - motion.*
  - complexity.*
output:
  format: json
llm:
  enabled: false        # Fully local
```

### Social Media Alt Text

```yaml
name: social-alttext
signals:
  - "@alttext"
  - color.dominant*
  - motion.*
output:
  format: alttext
llm:
  enabled: true
  model: minicpm-v:8b
escalation:
  gif_to_llm: true      # GIFs need LLM
```

### Quality Check

```yaml
name: quality-check
signals:
  - identity.*
  - quality.*
  - color.mean*
output:
  format: json
llm:
  enabled: false
```

## CLI Usage

```bash
# Use pipeline file
imagesummarizer image.gif --pipeline-file pipeline.yaml

# Combine with CLI options (CLI takes precedence)
imagesummarizer image.gif --pipeline-file base.yaml --output markdown

# Pipe pipeline via stdin
cat pipeline.yaml | imagesummarizer image.gif --pipeline-file -

# Direct signal selection (no file needed)
imagesummarizer image.gif --signals "identity.*,motion.*" --output json

# List available signals
imagesummarizer list-signals
```

## MCP Integration

When building MCP tools, dynamic pipelines allow efficient signal retrieval:

```json
{
  "name": "analyze_image",
  "arguments": {
    "path": "/path/to/image.gif",
    "signals": "identity.*,motion.type,color.dominant_name"
  }
}
```

The tool can request exactly the signals needed for the current task, avoiding unnecessary computation.

## Performance Tips

1. **Request minimal signals** - Each signal pattern determines which waves run
2. **Use `@identity` for fast metadata** - IdentityWave is ~5ms
3. **Disable LLM when not needed** - `llm.enabled: false` saves significant time
4. **Use `@tool` for automation** - Curated set of useful signals
5. **Check with `--signals`** - Test signal patterns before creating pipeline files

## Debugging

```bash
# Verbose mode shows wave execution
imagesummarizer image.gif --pipeline-file pipeline.yaml --verbose

# See what waves would run for a signal pattern
imagesummarizer list-signals
```

## Future Optimizations

The signal-driven architecture enables future enhancements:

### Runtime Optimizations
- **Wave timeouts** - Kill slow waves and use fallback signals
- **Early exit** - Return as soon as requested signals are available
- **Parallel execution** - Independent waves can run concurrently
- **Streaming signals** - Emit signals as they're computed

### Build-Time Optimizations (Planned)
- **Source Generation** - Compile YAML pipelines to optimized C# at build time
  - Zero runtime YAML parsing overhead
  - Static dependency resolution
  - AOT-friendly code generation
  - Pipeline validation at compile time

```csharp
// Future: Generated from caption.yaml
[GeneratedPipeline("caption")]
public static class CaptionPipeline
{
    public static readonly string[] RequiredTags = ["identity", "color", "motion", "ocr", "vision", "llm"];
    public static readonly string[] Signals = ["identity.*", "color.*", "motion.*", "ocr.*", "vision.llm.*"];
}
```

This keeps YAML as the source of truth (easy to edit, share, version) while delivering optimized runtime performance.

---

These patterns are fundamental to the ephemeral/reactive architecture where analyzers listen for signals they need and emit signals for downstream consumers.
