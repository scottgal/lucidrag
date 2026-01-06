# Signals Reference

## Signal Structure

Every extracted value is a Signal:

```csharp
public record Signal
{
    public string Key { get; init; }           // Unique identifier
    public object Value { get; init; }         // The value
    public double Confidence { get; init; }    // 0.0-1.0
    public string Source { get; init; }        // Producing wave
    public List<string> Tags { get; init; }    // Categories
    public Dictionary<string, object> Metadata { get; init; }
}
```

## Signal Catalog

### Identity Signals

| Key | Type | Source |
|-----|------|--------|
| `identity.width` | int | IdentityWave |
| `identity.height` | int | IdentityWave |
| `identity.format` | string | IdentityWave |
| `identity.is_animated` | bool | IdentityWave |
| `identity.frame_count` | int | IdentityWave |
| `identity.type` | string | TypeClassificationWave |
| `identity.type_confidence` | double | TypeClassificationWave |

### Color Signals

| Key | Type | Source |
|-----|------|--------|
| `color.dominant.hex` | string | ColorWave |
| `color.dominant.name` | string | ColorWave |
| `color.dominant.percentage` | double | ColorWave |
| `color.palette` | List | ColorWave |
| `color.saturation.average` | double | ColorWave |

### Quality Signals

| Key | Type | Source |
|-----|------|--------|
| `quality.sharpness` | double | QualityWave |
| `quality.is_blurry` | bool | QualityWave |
| `quality.noise_level` | double | QualityWave |

### OCR Signals

| Key | Type | Source |
|-----|------|--------|
| `ocr.text` | string | OcrWave |
| `ocr.confidence` | double | OcrWave |
| `ocr.word_count` | int | OcrWave |
| `ocr.ml.text` | string | MlOcrWave |
| `ocr.florence2.text` | string | Florence2Wave |

### Caption Signals

| Key | Type | Source |
|-----|------|--------|
| `caption.text` | string | VisionLlmWave |
| `caption.detailed` | string | VisionLlmWave |
| `caption.confidence` | double | VisionLlmWave |

### Motion Signals

| Key | Type | Source |
|-----|------|--------|
| `motion.is_animated` | bool | MotionWave |
| `motion.frame_count` | int | MotionWave |
| `motion.duration` | double | MotionWave |
| `motion.motion_intensity` | double | MotionWave |
| `motion.motion_type` | string | MotionWave |

### Embedding Signals

| Key | Type | Source |
|-----|------|--------|
| `embedding.clip` | float[] | EmbeddingWave |
| `embedding.dimensions` | int | EmbeddingWave |

## Collections

Pre-defined signal groups using `@` prefix:

```csharp
// Use collection
var result = await analyzer.AnalyzeBySignalsAsync(path, "@alttext");
```

| Collection | Signals |
|------------|---------|
| `@minimal` | `identity.*`, `quality.sharpness` |
| `@alttext` | `caption.text`, `ocr.text`, `color.dominant*` |
| `@motion` | `motion.*`, `identity.frame_count` |
| `@full` | All signals |
| `@tool` | Optimized for automation |

## Glob Patterns

Select signals using glob patterns:

```csharp
// All identity signals
await analyzer.AnalyzeBySignalsAsync(path, "identity.*");

// All dominant color signals
await analyzer.AnalyzeBySignalsAsync(path, "color.dominant*");

// Multiple patterns
await analyzer.AnalyzeBySignalsAsync(path, "ocr.*, caption.text, motion.*");
```

## Accessing Signals

```csharp
var profile = await analyzer.AnalyzeAsync(path);

// Via Ledger (all signals)
foreach (var signal in profile.Ledger.Signals)
{
    Console.WriteLine($"{signal.Key}: {signal.Value} ({signal.Confidence:P0})");
}

// Via typed properties
var text = profile.Ocr.Text;
var caption = profile.Caption.Text;
var dominant = profile.Color.Dominant;
```

## Custom Signal Selection

```yaml
# pipeline.yaml
signals:
  include:
    - identity.*
    - caption.text
    - ocr.text
  exclude:
    - identity.hash  # Skip hash calculation
```
