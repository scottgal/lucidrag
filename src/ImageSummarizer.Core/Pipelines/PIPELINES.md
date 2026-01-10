# Pipeline Reference

## Primary Usage: `--pipeline`

**Use the `--pipeline` flag for standard pipelines** - it's the recommended approach:

```bash
# Default (caption with LLM)
imagesummarizer image.gif

# Fast metadata only
imagesummarizer image.gif --pipeline stats

# Motion analysis for GIFs
imagesummarizer image.gif --pipeline motion

# Fast local captioning (no external API)
imagesummarizer image.gif --pipeline florence2

# Full accessible alt text
imagesummarizer image.gif --pipeline alttext
```

## Custom Pipelines: `--pipeline-file`

**Use `--pipeline-file` only for custom configurations** - when you need to:
- Specify exact signal patterns
- Override LLM settings
- Create specialized workflows
- Share pipeline configs across teams

```bash
# Custom pipeline from YAML file
imagesummarizer image.gif --pipeline-file my-custom-pipeline.yaml

# Pipe via stdin
cat pipeline.yaml | imagesummarizer image.gif --pipeline-file -
```

The YAML files in this directory serve as **templates and documentation** for creating custom pipelines.

---

## Quick Reference

| Pipeline | Use Case | LLM Required | Speed |
|----------|----------|--------------|-------|
| `stats.yaml` | File metadata only | No | ~5ms |
| `fast-dedupe.yaml` | Image deduplication | No | ~50ms |
| `quality-check.yaml` | Quality assessment | No | ~100ms |
| `motion-analysis.yaml` | Animation analysis | No | ~200ms |
| `florence2.yaml` | Fast local captioning | No | ~2s |
| `advancedocr.yaml` | Text extraction | No | ~500ms |
| `vision.yaml` | Vision-only (no OCR) | Yes | ~3s |
| `florence2-llm.yaml` | Hybrid (fast + LLM) | Yes | ~3-5s |
| `caption.yaml` | Full analysis | Yes | ~5s |
| `alttext.yaml` | Accessible alt text | Yes | ~5s |
| `social-alttext.yaml` | Social media ready | Yes | ~5s |

---

## Pipeline Details

### stats.yaml
**Fast metadata extraction** - Identity signals only (hash, dimensions, format)

```yaml
signals:
  - identity.*
llm:
  enabled: false
```

**Best for:** Bulk processing, file organization, quick triage

---

### fast-dedupe.yaml
**Image fingerprinting for deduplication**

```yaml
signals:
  - identity.sha256
  - identity.format
  - clip.embedding      # Perceptual similarity
llm:
  enabled: false
```

**Best for:** Finding duplicates, building image indexes

---

### quality-check.yaml
**Image quality assessment**

```yaml
signals:
  - identity.*
  - quality.*
  - color.mean*
llm:
  enabled: false
```

**Best for:** Quality filtering, blur detection, technical assessment

---

### motion-analysis.yaml
**Animation analysis for GIFs**

```yaml
signals:
  - identity.*
  - motion.*
  - complexity.*
llm:
  enabled: false
```

**Best for:** GIF classification, motion detection, animation understanding

---

### florence2.yaml
**Fast local captioning** - Uses Florence-2 ONNX model (no external API)

```yaml
signals:
  - identity.*
  - color.dominant*
  - florence2.*
llm:
  enabled: false
```

**Best for:** Offline processing, batch operations, quick captions

**Limitations:** Weak at animations (produces generic "animated image" descriptions)

---

### advancedocr.yaml
**Multi-frame OCR with voting**

```yaml
signals:
  - identity.*
  - ocr.*
  - content.extracted_text
llm:
  enabled: false
```

**Best for:** Extracting text from GIFs, subtitles, captions in animations

---

### vision.yaml
**Vision LLM only** - No Tesseract required

```yaml
signals:
  - identity.*
  - color.dominant*
  - motion.*
  - vision.llm.*
llm:
  enabled: true
```

**Best for:** Systems without Tesseract, visual-only understanding

---

### florence2-llm.yaml
**Hybrid pipeline** - Florence-2 first, LLM escalation for complex images

```yaml
signals:
  - identity.*
  - color.dominant*
  - motion.*
  - florence2.*
  - vision.llm.*
llm:
  enabled: true
escalation:
  gif_to_llm: true
  complexity_threshold: 0.4
```

**Best for:** Balanced speed/quality, animated GIFs that need rich descriptions

---

### caption.yaml (Default)
**Full analysis with Vision LLM**

```yaml
signals:
  - identity.*
  - color.*
  - motion.*
  - ocr.*
  - vision.llm.*
llm:
  enabled: true
  context: true
```

**Best for:** General image understanding, rich descriptions

---

### alttext.yaml
**WCAG-compliant accessible alt text**

```yaml
signals:
  - identity.format
  - identity.is_animated
  - color.dominant*
  - motion.type
  - motion.summary
  - content.text*
  - vision.llm.caption
output:
  format: alttext
llm:
  enabled: true
```

**Best for:** Web accessibility, screen readers, social media

---

### social-alttext.yaml
**Social media optimized alt text**

```yaml
signals:
  - "@alttext"          # Predefined collection
  - motion.*
  - color.dominant*
output:
  format: alttext
llm:
  enabled: true
escalation:
  gif_to_llm: true
```

**Best for:** Twitter, Instagram, LinkedIn accessibility

---

## YAML Schema

All pipeline files follow this schema:

```yaml
# Required
name: pipeline-name
version: 1

# Optional description
description: What this pipeline does

# Signal selection (glob patterns or @collections)
signals:
  - identity.*              # Wildcard: all identity signals
  - color.dominant*         # Prefix match
  - "@motion"               # Predefined collection
  - vision.llm.caption      # Exact match

# Output configuration
output:
  format: json              # json, text, alttext, markdown, visual, caption
  include_metadata: true
  include_confidence: true

# LLM configuration
llm:
  enabled: true
  model: minicpm-v:8b       # Ollama model
  ollama_url: http://localhost:11434
  fast_mode: false          # Skip heuristics
  context: true             # Include signals in LLM prompt

# Escalation behavior
escalation:
  gif_to_llm: true          # Always use LLM for GIFs
  complexity_threshold: 0.4 # Edge density trigger
  on_no_caption: true       # Escalate if fast path fails
  min_caption_length: 20    # Escalate if caption too short
```

## Signal Collections

Use `@collection` syntax for predefined signal groups:

| Collection | Signals | Use Case |
|------------|---------|----------|
| `@identity` | identity.* | File metadata |
| `@motion` | motion.*, complexity.* | Animation analysis |
| `@color` | color.* | Color extraction |
| `@quality` | quality.* | Quality metrics |
| `@text` | content.text*, ocr.* | Text extraction |
| `@vision` | vision.* | Vision LLM outputs |
| `@alttext` | vision.llm.caption, content.text*, motion.summary | Alt text |
| `@tool` | Curated set for MCP/automation | Tool integration |
| `@all` | * | Full pipeline |

## Creating Custom Pipelines

```bash
# Get sample template
imagesummarizer sample-pipeline > my-pipeline.yaml

# Edit to your needs
vim my-pipeline.yaml

# Use it
imagesummarizer image.gif --pipeline-file my-pipeline.yaml

# List available signals
imagesummarizer list-signals
```

## Tips

1. **Start with `stats.yaml`** for fast triage
2. **Use `florence2.yaml`** for offline/batch processing
3. **Use `florence2-llm.yaml`** for GIFs (Florence-2 alone is weak at animations)
4. **Use `--all-computed`** to see all signals that were calculated (not just filtered)
5. **Combine with `--signals`** to further filter output
