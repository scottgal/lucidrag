# ConfidenceBooster - Background LLM Refinement System

**Pattern:** Uncertainty-Driven Bounded Escalation
**Runtime:** Background coordinator (slow path, not real-time ingestion)
**Purpose:** Refine low-confidence signals using targeted LLM queries

---

## Overview

ConfidenceBooster is a general-purpose background learning system that identifies low-confidence signals in processed documents and uses targeted LLM queries to improve them.

**The Two-Path Architecture:**

```
FAST PATH (Real-Time Ingestion):
  User uploads file → All waves execute → Signal ledger saved → User can query immediately
  Some signals may have confidence < 0.75 (uncertain OCR, unclear transcription, etc.)
  Total time: Same as current (no blocking)

SLOW PATH (Background Coordinator):
  Background job scans for files with low-confidence signals
  → Queue for ConfidenceBooster processing
  → Extract bounded artifacts (only uncertain regions)
  → Query LLM with targeted prompts
  → Update signal ledger with boosted signals
```

---

## Architecture

### Core Components

1. **`IArtifact`** - Base interface for bounded artifacts
   - `ImageCropArtifact` - Cropped images for object recognition/OCR
   - `AudioSegmentArtifact` - Audio clips for transcription refinement
   - `TextWindowArtifact` - Text excerpts for OCR/entity extraction
   - `DataSampleArtifact` - Sample records for schema inference

2. **`IConfidenceBooster<TArtifact>`** - Generic booster interface
   - `ExtractArtifactsAsync` - Find low-confidence signals, extract artifacts
   - `BoostBatchAsync` - Query LLM to refine artifacts
   - `UpdateSignalLedgerAsync` - Persist boosted signals

3. **`BaseConfidenceBooster<TArtifact>`** - Common LLM orchestration
   - Handles LLM invocation, JSON parsing, error handling
   - Domain-specific implementations override prompts and extraction

4. **`ConfidenceBoosterCoordinator`** - Background orchestration
   - Scans for documents needing boost
   - Routes to appropriate domain-specific booster
   - Manages cost control (max artifacts, rate limiting)

5. **`ConfidenceBoosterBackgroundService`** - Hosted service
   - Runs boost cycles every 5 minutes
   - Processes queued documents
   - Logs metrics (tokens, success rate)

---

## Domain-Specific Implementations

### ImageConfidenceBooster

**Use case:** Refine object detection, OCR, or classification with low confidence

**Example:**
```csharp
// Original signal from ImageSummarizer
{
  "signal": "object.classification.person",
  "value": "person",
  "confidence": 0.68,  // Low confidence!
  "bounding_box": [120, 45, 80, 200]
}

// Extract bounded crop → Query LLM → Boosted signal
{
  "signal": "object.classification.person.boosted",
  "value": "person with backpack",
  "confidence": 0.92,
  "reasoning": "Clear view of person wearing a backpack, standing upright"
}
```

**Prompt:**
```
Task: Object Recognition

Original low-confidence classification: "person" (confidence: 0.68)

Please analyze this image crop and provide:
- What object(s) you see in the image
- Key distinguishing features
- Confidence in your classification

[Image provided as Base64]
```

---

### AudioConfidenceBooster

**Use case:** Refine unclear Whisper transcriptions

**Example:**
```csharp
// Original segment from Whisper
{
  "segment": 42,
  "text": "quantum comp... [inaudible] algorithm",
  "confidence": 0.62,  // Low confidence!
  "time_range": [125.3, 128.7]
}

// Extract audio clip → Query LLM → Boosted segment
{
  "segment": 42,
  "text": "quantum computing Shor's algorithm",
  "confidence": 0.88,
  "reasoning": "Technical term corrected based on audio clarity and context"
}
```

**Prompt:**
```
Task: Transcription Refinement

Original transcription: "quantum comp... [inaudible] algorithm" (confidence: 0.62)

Context:
Before: "Today we're discussing cryptography and..."
After: "...which can factor large numbers efficiently."

[Audio segment provided as Base64 WAV]
```

---

### TextWindowArtifact (DocSummarizer)

**Use case:** Refine OCR errors, extract entities from garbled text

**Example:**
```csharp
// Original OCR from PDF
{
  "signal": "text.line_42",
  "value": "Effechve date: 2O24-Ol-l5",  // OCR errors!
  "confidence": 0.58
}

// Send text window to LLM → Boosted signal
{
  "signal": "text.line_42.boosted",
  "value": "Effective date: 2024-01-15",
  "confidence": 0.95,
  "reasoning": "Corrected OCR errors: 'Effechve' → 'Effective', '2O24' → '2024', 'Ol' → '01'"
}
```

---

### DataSampleArtifact (DataSummarizer)

**Use case:** Refine type inference, detect semantic meaning

**Example:**
```csharp
// Original type inference
{
  "column": "transaction_date",
  "type": "string",  // Detected as string!
  "confidence": 0.65,
  "sample": ["2024-01-15", "2024/01/16", "Jan 17, 2024"]
}

// Send samples to LLM → Boosted signal
{
  "column": "transaction_date",
  "type": "date",  // Corrected to date
  "format": "mixed (ISO 8601, slash-separated, written)",
  "confidence": 0.90
}
```

---

## Configuration

### appsettings.json

```json
{
  "ConfidenceBooster": {
    "Enabled": true,  // Enable background boosting
    "ConfidenceThreshold": 0.75,  // Boost signals below this
    "MaxArtifactsPerDocument": 5,  // Cost control
    "Temperature": 0.1,  // Low temperature for consistency
    "MaxTokensPerRequest": 500,
    "DelayBetweenRequestsMs": 1000  // Rate limiting
  }
}
```

### Service Registration

```csharp
// In Program.cs or Startup.cs
builder.Services.AddConfidenceBooster(config =>
{
    config.Enabled = true;
    config.ConfidenceThreshold = 0.75;
    config.MaxArtifactsPerDocument = 5;
    config.Temperature = 0.1;
});
```

---

## Cost Control Strategies

1. **Confidence Threshold** (0.75): Only boost signals below this
2. **Max Artifacts** (5 per document): Limit LLM calls
3. **Rate Limiting** (delay between requests): Prevent API throttling
4. **Batch Processing** (10 docs per cycle): Spread load over time
5. **Cycle Frequency** (every 5 minutes): Control background overhead

**Example cost calculation:**
- 100 documents processed
- 5 artifacts per document = 500 LLM calls
- 500 tokens per call = 250,000 tokens total
- Cost: ~$0.25 @ $0.001/1K tokens (varies by provider)

---

## LLM Response Format

All boosters expect JSON responses:

```json
{
  "value": "refined classification or text",
  "confidence": 0.85,
  "reasoning": "explanation of refinement",
  "metadata": {
    "corrections": ["list of changes"],
    "technical_terms": ["identified terms"],
    "uncertain_words": ["words still unclear"]
  }
}
```

---

## Integration Example

### Scenario: Refine unclear podcast transcription

```csharp
// 1. User uploads podcast → AudioSummarizer processes (FAST PATH)
var audioDoc = await audioProcessor.ProcessAsync("podcast.mp3");
// Signal ledger includes:
//   - segment_42: "quantum comp... [inaudible]" (confidence: 0.62)

// 2. Background coordinator scans (SLOW PATH)
var candidates = await coordinator.ScanAndQueueAsync();
// Finds: 1 document with low-confidence signals

// 3. Coordinator processes
var summary = await coordinator.ProcessDocumentAsync(audioDoc.Id, "audio");
// - Extracted: 3 artifacts (segments 42, 58, 91)
// - Boosted: 3/3 successful
// - Tokens: 1,247

// 4. Signal ledger updated
// segment_42.boosted: "quantum computing Shor's algorithm" (confidence: 0.88)

// 5. User queries again (sees improved transcription)
```

---

## Extending ConfidenceBooster

### Add a new domain-specific booster

```csharp
// 1. Create artifact type
public class VideoFrameArtifact : IArtifact
{
    public required string Base64Frame { get; init; }
    public int FrameNumber { get; init; }
    // ...
}

// 2. Implement booster
public class VideoConfidenceBooster : BaseConfidenceBooster<VideoFrameArtifact>
{
    protected override string GetSystemPrompt() => "You are a video analyst...";

    protected override string GeneratePrompt(VideoFrameArtifact artifact)
    {
        return $"Analyze this video frame: {artifact.Base64Frame}";
    }

    // ... implement other abstract methods
}

// 3. Register in DI
services.AddScoped<VideoConfidenceBooster>();
```

---

## Monitoring and Metrics

Background service logs:
- Documents scanned
- Artifacts extracted
- LLM success rate
- Token usage
- Processing time

**Example log output:**
```
[12:05:23] Boost cycle complete: processed 8 documents
[12:05:23] Summary:
  - Documents: 8
  - Artifacts extracted: 34
  - Artifacts boosted: 31/34 (91.2%)
  - Total tokens: 12,456
  - Avg processing time: 2.3s
```

---

## Design Principles

1. **General-purpose LLM stage** - Same infrastructure, different prompts
2. **Bounded artifacts** - Extract only uncertain regions (cost control)
3. **Background learning** - No blocking on user ingestion
4. **Auditability** - Store reasoning and metadata
5. **Cost-aware** - Configurable limits, rate limiting
6. **Domain-agnostic** - Works for Image, Audio, Doc, Data

---

## Files Created

```
LucidRAG.Core/Services/ConfidenceBooster/
├── IArtifact.cs                        # Base artifact interface
├── IConfidenceBooster.cs               # Generic booster interface
├── BoostResult.cs                      # Boost result model
├── BaseConfidenceBooster.cs            # Common LLM orchestration
├── ConfidenceBoosterCoordinator.cs     # Background orchestration
├── ConfidenceBoosterBackgroundService.cs  # Hosted service
├── Artifacts/
│   ├── ImageCropArtifact.cs
│   ├── AudioSegmentArtifact.cs
│   ├── TextWindowArtifact.cs
│   └── DataSampleArtifact.cs
├── Domain/
│   ├── ImageConfidenceBooster.cs       # Image domain implementation
│   └── AudioConfidenceBooster.cs       # Audio domain implementation
└── README.md (this file)
```

---

## Next Steps

1. **Implement remaining boosters:**
   - DocumentConfidenceBooster (OCR refinement, entity extraction)
   - DataConfidenceBooster (type inference, format detection)

2. **Add actual implementations:**
   - Image crop extraction (using SkiaSharp/ImageSharp)
   - Audio segment extraction (using AudioSegmentExtractor)
   - Signal repository persistence (EF Core)

3. **LLM service integration:**
   - Implement ILlmService using Ollama or Claude API
   - Add vision support for ImageConfidenceBooster
   - Add audio support for AudioConfidenceBooster (if LLM supports)

4. **Testing:**
   - Unit tests for artifact extraction
   - Integration tests for LLM prompts
   - End-to-end tests for coordinator

---

## References

- Feature plan: `docs/FEATURE_ConfidenceBooster.md`
- Related pattern: [Constrained Fuzziness](https://www.mostlylucid.net/blog/constrained-fuzziness-pattern)
- Related pattern: [Reduced RAG](https://www.mostlylucid.net/blog/reduced-rag)
