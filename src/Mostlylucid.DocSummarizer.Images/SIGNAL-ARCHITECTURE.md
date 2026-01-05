# Signal Architecture: Heuristics vs Captions

This document explains the purpose of the image analysis pipeline's two distinct outputs.

## Overview

The image analysis pipeline produces two types of output:

1. **Captions** - Human-readable descriptions from Vision LLM
2. **Signals** - Machine-readable metadata for RAG/AI systems

These serve different purposes and are intentionally decoupled.

## Captions (Vision LLM)

**Purpose**: Generate human-readable alt text and descriptions.

**How it works**:
- Direct prompt to Vision LLM (minicpm-v, llava, etc.)
- Simple, focused prompts without context overload
- **For GIFs**: Automatic filmstrip generation (see below)

**Key insight**: Captions do NOT benefit from heuristic context. A/B testing showed that simple prompts produce equivalent or better captions than context-heavy prompts. The LLM sees the image directly - it doesn't need to be told what colors are dominant.

### GIF/Animation Captions (Filmstrip Approach)

For animated images, we still use Vision LLM but with a **filmstrip** approach:

```
GIF (93 frames) → Sample 16 frames → Dedupe similar frames →
                  Create horizontal strip (max 8 frames) → Send to Vision LLM
```

**Why filmstrip works**:
1. LLM sees temporal sequence (left-to-right = time)
2. Can describe motion ("person walks left to right")
3. Can read subtitles across frames
4. Single image = single API call (efficient)

**Example output**:
```
Input: anchorman-not-even-mad.gif (93 frames)
Filmstrip: 6 unique frames showing expression change
Caption: "A person in a sweater expressing disbelief, with subtitles 'I'm not even mad. That's amazing.'"
```

**Frame selection algorithm**:
```csharp
// 1. Sample frames evenly (max 16)
var step = Math.Max(1, totalFrames / 16);

// 2. Deduplicate similar frames (95% pixel similarity threshold)
for each frame:
    if (!IsSimilarTo(lastUniqueFrame, threshold: 0.95))
        uniqueFrames.Add(frame)

// 3. Limit to 8 frames for optimal strip readability
while (uniqueFrames.Count > 8)
    RemoveMiddleFrame()

// 4. Create horizontal strip (256px per frame max)
var strip = CreateHorizontalStrip(uniqueFrames, frameWidth: 256);
```

**Prompt for filmstrip**:
```
"This is a {N}-frame animated GIF shown left-to-right.
Describe what happens in the animation, the motion,
and transcribe any visible text or subtitles."
```

This approach gives the LLM the visual context it needs to understand:
- **Motion direction** (sees object positions change across frames)
- **Subtitle text** (sees all text frames in sequence)
- **Scene changes** (sees key frames of animation)

```
Simple prompt: "Describe this image concisely"
Result: "Two dogs playing on a wooden deck"

Context-heavy prompt: "Image has warm colors, 2 faces detected, outdoor scene..."
Result: Similar or worse due to prompt confusion
```

## Signals (Heuristics Pipeline)

**Purpose**: Generate machine-readable metadata for AI/ML systems.

**Use cases**:

### 1. RAG (Retrieval-Augmented Generation)
- **CLIP embeddings** → Semantic similarity search ("find images like this")
- **OCR text** → Full-text search on image content
- **Perceptual hash** → Near-duplicate detection

### 2. Auto-Clustering
- **Color palette** → Group by dominant colors
- **Scene classification** → Group by indoor/outdoor/food/etc.
- **Motion type** → Group by animation style (panning, zooming, static)

### 3. Learning & Analytics
- **Face embeddings** → Learn to recognize recurring subjects
- **Object detection** → Track what objects appear together
- **Quality scores** → Learn what makes a "good" image

### 4. Filtering & Faceted Search
- **is_animated** → Filter GIFs vs static
- **has_text** → Filter images with text overlays
- **dominant_color** → Filter by color
- **resolution** → Filter by quality
- **is_grayscale** → Filter B&W vs color

### 5. Deduplication
- **Perceptual hash (pHash)** → Find visually similar images
- **CLIP embedding distance** → Find semantically similar images
- **Exact hash** → Find byte-identical copies

## Signal Categories

| Category | Signals | Purpose |
|----------|---------|---------|
| **Identity** | format, dimensions, hash, pHash | Deduplication, indexing |
| **Visual** | colors, brightness, contrast, sharpness | Clustering, quality filtering |
| **Content** | OCR text, faces, objects, scene | Search, classification |
| **Motion** | frame_count, motion_type, direction | Animation filtering |
| **Semantic** | CLIP embedding (512-dim vector) | Similarity search |
| **Metadata** | EXIF, GPS, camera, date | Provenance, filtering |

## Pipeline Modes

### Fast Mode (Captions Only)
```
Image → [Quick Hash] → Vision LLM → Caption
         ↓
    [Cache Lookup]
```
- Skips full heuristics pipeline
- Computes minimal identity signals (hash, dimensions) for cache key
- Fastest path for simple captioning
- **Cacheable**: Uses `hash + dimensions + model` as cache key
- Use when you just need alt text

**Desktop App**: Enable with "Fast" checkbox
**CLI**: Use `--pipeline vision` (skips OCR/heuristics)

### Fast Mode with Background Caching
Even in fast mode, we compute basic identity signals for caching. Caching happens in a **background coordinator** to keep the user experience fast:

```csharp
// FAST PATH: Return to user immediately
public async Task<string> GetCaptionAsync(string imagePath)
{
    // Quick identity check (hash + dimensions) - ~10ms
    var identity = await ComputeQuickIdentity(imagePath);
    var cacheKey = $"{identity.Hash}_{identity.Width}x{identity.Height}_{model}";

    // Cache hit = instant return
    if (cache.TryGet(cacheKey, out var cached))
        return cached;

    // Generate caption and return immediately
    var caption = await visionLlm.CaptionAsync(imagePath);

    // BACKGROUND: Cache for future use (don't block user)
    _ = Task.Run(() => cache.SetAsync(cacheKey, caption, expiry: TimeSpan.FromDays(30)));

    return caption;  // User gets result without waiting for cache write
}
```

### Background Coordinator Pattern
For optimal UX, the caching coordinator runs separately:

```
User Request → Quick Identity → Cache Check → [Hit: Return] or [Miss: Generate]
                                                              ↓
                                              Return Caption to User (immediate)
                                                              ↓
                                              Background: Write to Cache
                                              Background: Index for RAG (optional)
                                              Background: Generate embeddings (optional)
```

This means:
- **First request**: ~2s (LLM generation), cache write happens in background
- **Subsequent requests**: ~10ms (cache hit)
- **User never waits** for cache writes or background indexing

### Cache Key Components
```
{hash}_{width}x{height}_{model}
```
- `hash`: MD5 or SHA256 of file content
- `width x height`: Image dimensions (different crops = different cache)
- `model`: LLM model name (different models = different captions)

### Full Pipeline (RAG Signals + Caption)
```
Image → Waves (Identity → Color → OCR → Motion → CLIP → Vision LLM) → Signals + Caption
```
- Runs all analysis waves
- Generates 50+ signals per image
- Use for database ingestion, search indexing

### Index Mode (Signals Only, No LLM)
```
Image → Waves (Identity → Color → OCR → Motion → CLIP) → Signals
```
- Skips Vision LLM entirely
- Fastest for bulk ingestion
- Use for indexing large collections

### Mode Comparison

| Mode | Speed | Output | Cache Key | Use Case |
|------|-------|--------|-----------|----------|
| **Fast** | ~2s | Caption only | hash+dims+model | Quick alt text |
| **Full** | ~5-10s | Signals + Caption | hash+dims | RAG ingestion |
| **Index** | ~3-5s | Signals only | hash+dims | Bulk indexing |

### Desktop App Options

| Checkbox | Effect |
|----------|--------|
| **LLM** | Enable/disable Vision LLM |
| **Fast** | Skip heuristics, direct LLM call |
| **No Ctx** | Use minimal prompt (simpler output) |

Combinations:
- `LLM + Fast` = Quick caption with detailed prompt
- `LLM + Fast + No Ctx` = Quickest caption, minimal prompt
- `LLM` (no Fast) = Full pipeline with all signals
- No `LLM` = Heuristics only (stats, OCR, colors)

## Why Signals Don't Improve Captions

We tested whether feeding heuristic signals to the Vision LLM improves caption quality:

| Test | With Context | Without Context | Winner |
|------|--------------|-----------------|--------|
| Dogs photo | "Two dogs playing on deck" | "Two dogs on wooden deck" | Tie |
| GIF with text | Extracts subtitles | Extracts subtitles | Tie |
| Motion GIF | Describes motion | Describes motion | Tie |

**Conclusion**: The Vision LLM sees the actual image - it doesn't need to be told what it's looking at. Context can actually confuse the model or cause prompt leakage.

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                        Image Input                          │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                    Wave Orchestrator                         │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐       │
│  │ Identity │→│  Color   │→│   OCR    │→│  Motion  │→ ...  │
│  │   Wave   │ │   Wave   │ │   Wave   │ │   Wave   │       │
│  └──────────┘ └──────────┘ └──────────┘ └──────────┘       │
└─────────────────────────────────────────────────────────────┘
                              │
              ┌───────────────┴───────────────┐
              ▼                               ▼
┌──────────────────────┐         ┌──────────────────────┐
│   Signal Database    │         │    Vision LLM        │
│   (RAG/Search/ML)    │         │    (Captions)        │
│                      │         │                      │
│ • CLIP embeddings    │         │ • Alt text           │
│ • OCR text index     │         │ • Descriptions       │
│ • Color clusters     │         │ • Entity extraction  │
│ • Motion metadata    │         │                      │
│ • Perceptual hashes  │         │                      │
└──────────────────────┘         └──────────────────────┘
         │                                │
         ▼                                ▼
┌──────────────────────┐         ┌──────────────────────┐
│   AI/ML Systems      │         │   Human Interfaces   │
│                      │         │                      │
│ • Auto-clustering    │         │ • Screen readers     │
│ • Similarity search  │         │ • Image galleries    │
│ • Anomaly detection  │         │ • Social media       │
│ • Learning pipelines │         │ • Documentation      │
└──────────────────────┘         └──────────────────────┘
```

## Best Practices

1. **For alt text generation**: Use Fast Mode - skip heuristics
2. **For database ingestion**: Use Full Pipeline - capture all signals
3. **For bulk indexing**: Use Index Mode - skip LLM, just signals
4. **For deduplication**: Use perceptual hash signals
5. **For semantic search**: Use CLIP embeddings
6. **For text search**: Use OCR signals

## Signal Schema (Abbreviated)

```json
{
  "identity": {
    "format": "gif",
    "width": 480,
    "height": 270,
    "hash_md5": "abc123...",
    "hash_perceptual": "f0e1d2c3..."
  },
  "color": {
    "dominant": ["#2a4858", "#8b9a6b"],
    "palette": [...],
    "is_grayscale": false
  },
  "content": {
    "ocr_text": "I'm not even mad",
    "text_likeliness": 0.85,
    "faces_detected": 1
  },
  "motion": {
    "is_animated": true,
    "frame_count": 93,
    "motion_type": "general",
    "dominant_direction": "stationary"
  },
  "semantic": {
    "clip_embedding": [0.023, -0.041, ...],  // 512 dimensions
    "scene": "indoor"
  },
  "caption": {
    "alttext": "A person expressing disbelief with the caption 'I'm not even mad'",
    "source": "minicpm-v:8b"
  }
}
```

## Summary

| Output | Purpose | Consumers | Context Needed |
|--------|---------|-----------|----------------|
| **Caption** | Human description | Screen readers, UI | No |
| **Signals** | Machine metadata | RAG, ML, Search | N/A (self-contained) |

The heuristics pipeline exists for AI/ML systems, not for improving captions. Keep them separate.
