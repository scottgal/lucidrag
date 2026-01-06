# Pipelines

## Built-in Pipelines

| Pipeline | Description | Speed | Use Case |
|----------|-------------|-------|----------|
| `ProfileOnly` | Deterministic analysis only | ~10ms | Fast metadata |
| `auto` | Smart routing based on image | ~100-500ms | General purpose |
| `florence2` | Florence-2 captions | ~200ms | Photos, screenshots |
| `motion` | GIF motion analysis | ~300ms | Animations |
| `florence2+llm` | Florence-2 + Vision LLM | ~1-5s | Best quality |
| `advancedocr` | Multi-stage OCR | ~500ms | Text extraction |

## Auto Pipeline

The `auto` pipeline selects the best route based on image characteristics:

```
Image Analysis
    │
    ├── Is animated? → Motion pipeline
    │
    ├── Has text regions? → OCR pipeline
    │   └── High confidence → Skip Vision LLM
    │   └── Low confidence → Escalate
    │
    ├── Is chart/diagram? → Vision LLM
    │
    └── Default → Florence-2 caption
```

## Custom Pipelines

Define custom pipelines in YAML:

```yaml
name: accessibility-alttext
description: Generate accessible alt text

signals:
  - identity.type
  - identity.dimensions
  - color.dominant*
  - caption.text
  - ocr.text

llm:
  enabled: true
  model: minicpm-v:8b
  prompt: |
    Generate concise alt text for screen readers.
    Focus on what's visually important.

output:
  format: text
  template: "{caption.text}"
```

## Signal Collections

Pre-defined signal groups:

| Collection | Signals |
|------------|---------|
| `@minimal` | identity.*, quality.sharpness |
| `@alttext` | caption.text, ocr.text, color.dominant* |
| `@motion` | motion.*, identity.frame_count |
| `@full` | All signals |
| `@tool` | Optimized for MCP/automation |

## Pipeline Selection Guide

```
Q: What are you analyzing?
│
├── Static images
│   ├── Need fast metadata? → ProfileOnly
│   ├── Need caption? → florence2
│   └── Need best quality? → florence2+llm
│
├── Animated GIFs
│   ├── Has subtitles? → auto (includes OCR)
│   └── Need motion description? → motion
│
└── Documents/Screenshots
    └── Need text extraction? → advancedocr
```

## CLI Pipeline Usage

```bash
# Use specific pipeline
imagesummarizer image.png --pipeline florence2

# Use signal collection
imagesummarizer image.png --signals "@alttext"

# Use custom pipeline file
imagesummarizer image.png --pipeline-file my-pipeline.yaml
```
