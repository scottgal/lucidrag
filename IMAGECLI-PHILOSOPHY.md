# ImageCli: A Perceptual Primitive for Agentic Systems

## What This Is

ImageCli is a **standalone perceptual substrate** for LLM-based systems.

It's not "AI vision". It's deterministic signal extraction that LLMs can query.

## The Division of Labor

```
┌─────────────────────────────────┐
│   Claude (probabilistic)        │  ← proposes, narrates, composes
│   - natural language reasoning  │
│   - task decomposition          │
│   - narrative synthesis         │
└─────────────────────────────────┘
           ↕ queries
┌─────────────────────────────────┐
│   ImageCli (deterministic)      │  ← measures, decides, persists
│   - perceptual extraction       │
│   - quality discrimination      │
│   - signal emission             │
└─────────────────────────────────┘
```

Claude doesn't "see" images.
Claude **queries a perceptual substrate**.

## Why This Matters

### 1. Determinism at the Edge

```bash
# Same input → same output
imagecli logo.png --pipeline advanced
# Always produces identical signals for identical input
```

This enables:
- **Reproducible forensics**
- **Compliance auditing**
- **Evidence chains**
- **Offline operation**

LLMs are probabilistic narrators. ImageCli is the deterministic witness.

### 2. Signals, Not Stories

Traditional vision APIs return prose:
> "The image shows a screenshot of a terminal window with white text on a black background..."

ImageCli returns **perceptual facts**:
```json
{
  "content.text_likeliness": 0.91,
  "quality.sharpness": 2450.3,
  "motion.temporal_stability": 1.0,
  "structure.edge_density": 0.18
}
```

These signals can:
- Drive downstream decisions
- Feed into discriminators
- Persist in databases
- Compose into higher-order reasoning

### 3. Graceful Degradation

```bash
# Works offline (ONNX-based OCR)
imagecli screenshot.png

# Works without LLMs
imagecli screenshot.png --format json

# Works with LLMs (MCP mode)
# Claude → imagecli → structured signals → Claude
```

The tool remains **useful in isolation**.

It's infrastructure, not a SaaS trap.

## The MCP Integration Pattern

### Before: LLM Vision

```
User: "What does this screenshot say?"
Claude: [analyzes pixel data internally]
Claude: "I see text that says..."
```

**Problems**:
- Non-deterministic interpretation
- No signal persistence
- No quality metrics
- Probabilistic all the way down

### After: Perceptual Substrate

```
User: "What does this screenshot say?"
Claude: [calls imagecli via MCP]
ImageCli: {
  "text": "Error: connection timeout",
  "signals": {
    "text_likeliness": 0.88,
    "voting_confidence": 0.92,
    "frames_processed": 1
  }
}
Claude: "The screenshot shows 'Error: connection timeout'.
        High confidence (92%) based on single-frame OCR."
```

**Benefits**:
- Deterministic text extraction
- Quality signals available
- Auditable perception
- Claude narrates *after* measurement

## Pipeline Philosophy

### Simple Pipeline
```
Input → OCR → Output
```
**Use case**: Clear text, speed critical
**Trade-off**: Minimal processing

### Advanced Pipeline
```
Input → Frame Dedup → Temporal Voting → Post-Correction → Output
```
**Use case**: GIFs, noisy images, accuracy matters
**Trade-off**: 2-3x slower, +30% accuracy

### Quality Pipeline
```
Input → Full Stabilization → Edge Detection → Multi-pass OCR → Dictionary Correction → Output
```
**Use case**: Forensics, compliance, critical documents
**Trade-off**: 5-10x slower, maximum accuracy

The key: **you choose the mode**, not the model.

## Zero-Dependency Story

### What "Zero Dependencies" Really Means

```bash
# Download binary
imagecli.exe

# Run immediately
./imagecli screenshot.png

# No:
# - API keys
# - Ollama
# - Cloud services
# - Internet connection
# - Python runtime
# - npm install hell
```

This unlocks:
- **Regulated environments** (healthcare, finance, government)
- **Forensic use cases** (chain of custody, reproducibility)
- **Offline deployment** (field operations, air-gapped networks)
- **Privacy compliance** (GDPR, data sovereignty)

## Positioning: Understated Power

We don't say:
- ❌ "AI vision platform"
- ❌ "Next-gen OCR solution"
- ❌ "Powered by advanced machine learning"

We say:
- ✅ "Standalone OCR and image analysis tool"
- ✅ "Zero dependencies. No API keys required."
- ✅ "Runs entirely offline with ONNX models."

Let the **capabilities emerge through usage**, not marketing.

## Signals as Composition Primitive

Traditional OCR tools return text. That's it.

ImageCli returns:
```json
{
  "text": "Error: connection timeout",
  "signals": {
    "content.text_likeliness": 0.88,
    "quality.sharpness": 2140.5,
    "motion.temporal_stability": 1.0,
    "pipeline.frames_processed": 1,
    "pipeline.voting_confidence": 0.92,
    "structure.edge_density": 0.18,
    "diagnostics.processing_time_ms": 147
  }
}
```

Now you can:
- **Filter low-confidence results**: `voting_confidence < 0.7`
- **Route to quality pipeline**: `text_likeliness > 0.9 && sharpness < 1000`
- **Track performance over time**: Store signals in time-series DB
- **Build discriminators**: Learn which signals predict success

Signals are **composition primitives**.

## MCP as Infrastructure Layer

Claude Desktop users won't think of this as "OCR".

They'll think of it as a **sensor**.

```
Claude: [user shares screenshot]
Claude: [queries lucidrag-ocr sensor]
Claude: [receives structured perception]
Claude: [composes narrative from signals]
```

This is the right abstraction:
- Probabilistic model (Claude) proposes and narrates
- Deterministic tool (ImageCli) measures and decides
- Signals persist and compose
- Cognition happens after perception

## Worked Example of Philosophy

This is not a "clever utility".

This is a **worked example** of the deterministic/probabilistic division:

1. **Deterministic pipelines decide**
   - Frame deduplication (SSIM > 0.95 → skip)
   - Temporal voting (3+ frames agree → accept)
   - Quality thresholds (sharpness < 500 → escalate)

2. **Probabilistic models propose**
   - Claude interprets context
   - Claude composes narrative
   - Claude decides next action

3. **Signals persist**
   - Stored in SQLite database
   - Available for discriminator learning
   - Compose into higher-order features

4. **Cognition narrates after**
   - Perception happens first (ImageCli)
   - Narration happens second (Claude)
   - Understanding emerges from composition

## The Correct Shape of Things

A good perceptual primitive:
- ✅ Works offline
- ✅ Degrades gracefully
- ✅ Integrates cleanly with LLMs
- ✅ Remains useful without them

ImageCli is all four.

## Final Note

This is infrastructure for agentic systems.

It's not meant to be flashy.
It's meant to be **reliably boring**.

Deterministic perception.
Structured signals.
Offline operation.
No surprises.

That's the product.

---

**Status**: Ready to ship.

Let it sit. Let people discover it.

The hard part is done.
