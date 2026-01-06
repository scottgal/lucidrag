# Vision OCR Integration

## Overview

The library intelligently combines multiple OCR technologies for optimal text extraction:

- **Tesseract** - Traditional OCR, good for clean text
- **Florence-2** - ML-based, handles stylized fonts, runs locally via ONNX
- **Vision LLM** - Last resort for complex cases

## Auto-Routing

OpenCV text detection (~5-20ms) determines the processing path:

```
Image → Text Detection → Route Decision
                            │
            ┌───────────────┼───────────────┐
            │               │               │
         FAST           BALANCED         QUALITY
      Florence-2     Florence-2 +      Multi-frame +
         only         Tesseract        Vision LLM
```

### Route Selection

| Route | When | Cost |
|-------|------|------|
| FAST | Simple text, high contrast | ~100ms |
| BALANCED | Normal text, moderate confidence | ~300ms |
| QUALITY | Charts, diagrams, stylized fonts | ~1-5s |

## Multi-Frame GIF OCR

For animated GIFs with subtitles, Florence-2 processes **all unique frames in parallel**:

```
Input: 93-frame GIF
    │
    ▼
Sample frames (up to 10)
    │
    ▼ (parallel)
┌───┬───┬───┬───┐
│ F1│ F2│ F3│...│  Florence-2 OCR
└───┴───┴───┴───┘
    │
    ▼
Deduplicate (Levenshtein 85%)
    │
    ▼
Combined text output
```

**Example** (anchorman-not-even-mad.gif):
- 93 frames → 10 sampled → 2 unique results
- Output: "I'm not even mad." + "That's amazing."

## Text-Only Strip Extraction

For maximum token efficiency, extract **only the text bounding boxes**:

### Full Frame vs Text-Only

```
Full frame:     300×185 = ~150 tokens
Text region:    252×49  = ~25 tokens
                         ↓
                   83% token reduction
```

### How It Works

1. Detect subtitle region (bottom 30% of frame)
2. Threshold bright pixels (text is typically white/yellow)
3. Find tight bounding box around text
4. Compare frames to detect text changes (Jaccard similarity of bright pixels)
5. Extract unique text regions only

### CLI Usage

```bash
# Extract text-only strip
imagesummarizer export-strip animation.gif --mode text-only

# Output:
# 93 frames → 2 segments → 2 text regions
# Strip dimensions: 253×105
```

### Visual Example

**Input**: 93-frame GIF with two different captions
**Output**: Compact strip with just the text

![Text-Only Strip](../demo-images/anchorman-not-even-mad_textonly_strip.png)

## Token Economics

### Before: Full Frame Filmstrip

```
10 frames × 300×185 = 10 × 150 tokens = 1,500 tokens
```

### After: Text-Only Strip

```
2 text regions × 250×50 = 2 × 25 tokens = 50 tokens
```

**30× token reduction** while preserving all subtitle text.

## Escalation Service

When Florence-2 fails or confidence is low:

```csharp
// EscalationService decision tree
if (florence2Result.Confidence < 0.5)
{
    // Try Tesseract voting across frames
    var tesseractResult = await TesseractVoting(frames);

    if (tesseractResult.Confidence < 0.5)
    {
        // Escalate to Vision LLM with filmstrip
        return await VisionLlmWithFilmstrip(frames);
    }
}
```

## Configuration

```json
{
  "DocSummarizer": {
    "Florence2": {
      "Enabled": true,
      "ModelPath": "models/florence2"
    },
    "Ocr": {
      "TesseractDataPath": "/usr/share/tesseract-ocr/4.00/tessdata",
      "Languages": ["eng"]
    },
    "VisionLlm": {
      "Enabled": true,
      "OllamaUrl": "http://localhost:11434",
      "Model": "minicpm-v:8b"
    }
  }
}
```
