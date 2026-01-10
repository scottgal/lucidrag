# Architecture

## Wave-Signal Model

The library uses a pipeline of **waves** that emit **signals**. Each wave is a specialized analyzer that runs based on priority and dependencies.

```
Image → IdentityWave → ColorWave → QualityWave → OcrWave → MotionWave → VisionLlmWave → Signals
          (P:10)        (P:20)      (P:30)       (P:50)      (P:60)        (P:80)
```

### Signals

Every piece of extracted information is a **Signal**:

```csharp
public record Signal
{
    public string Key { get; init; }           // e.g., "ocr.text", "color.dominant.hex"
    public object Value { get; init; }         // The extracted value
    public double Confidence { get; init; }    // 0.0 to 1.0
    public string Source { get; init; }        // Wave that produced it
    public List<string> Tags { get; init; }    // Categories
    public Dictionary<string, object> Metadata { get; init; }
}
```

### Wave Priorities

| Wave | Priority | Purpose |
|------|----------|---------|
| IdentityWave | 10 | Format, dimensions, animated detection |
| ColorWave | 20 | Dominant colors, color grid, saturation |
| QualityWave | 30 | Sharpness, blur detection, noise |
| TextLikelinessWave | 40 | Heuristic text detection (no OCR) |
| OcrWave | 50 | Tesseract OCR (if text-likely) |
| MlOcrWave | 51 | Florence-2 ML OCR |
| StructureWave | 52 | Document structure detection |
| Florence2Wave | 55 | Local ONNX captions |
| MotionWave | 60 | GIF motion analysis |
| EmbeddingWave | 70 | CLIP embeddings |
| VisionLlmWave | 80 | Vision LLM escalation |
| ValidationWave | 90 | Cross-validation, confidence adjustment |

## Escalation Rules

Escalation to more expensive processing is **deterministic** and **auditable**:

```csharp
// Escalation triggers (configurable)
if (typeConfidence < 0.7) → escalate
if (type is Diagram or Chart) → escalate
if (ocrConfidence < 0.5 && hasText) → escalate
if (quality.sharpness < 30) → escalate with "low quality" hint
```

### Escalation Chain

```
Fast Path (deterministic)
    │
    ▼
Tesseract OCR (if text-likely)
    │
    ▼ (if low confidence or chart/diagram)
Florence-2 ONNX (local, fast)
    │
    ▼ (if still low confidence)
Vision LLM (Ollama/Claude/GPT-4V)
```

## Caching

All signals are cached by content hash in SQLite:

```
Image → SHA256 hash → Check cache
                         │
                    ┌────┴────┐
                    │         │
                Cache hit   Cache miss
                 (~2ms)      (run waves)
                    │         │
                    └────┬────┘
                         │
                      Signals
```

### Cache Performance

- Cache hit: ~2-10ms
- Cache miss (heuristics only): ~10-50ms
- Cache miss (with OCR): ~100-500ms
- Cache miss (with Vision LLM): ~1-5s

## Analysis Context

Waves share data through `AnalysisContext`:

```csharp
public class AnalysisContext
{
    // Computed signals from previous waves
    public Dictionary<string, Signal> Signals { get; }

    // Cached intermediate results (OCR regions, embeddings, etc.)
    public T GetCached<T>(string key);
    public void SetCached<T>(string key, T value);

    // Get signal value with type conversion
    public T GetValue<T>(string key, T defaultValue = default);
}
```

## Dynamic Pipelines

Request only the signals you need:

```yaml
# pipeline.yaml
name: alttext
description: Generate alt text for accessibility
signals:
  - identity.*
  - color.dominant*
  - caption.text
  - ocr.text
llm:
  enabled: true
  model: minicpm-v:8b
```

```csharp
var result = await analyzer.AnalyzeBySignalsAsync(
    "image.png",
    "@alttext");  // Uses the pipeline above
```

## Thread Safety

- Waves are stateless
- AnalysisContext is not thread-safe (one per analysis)
- Cache access is thread-safe
- Florence-2 model loading is lazy and thread-safe
