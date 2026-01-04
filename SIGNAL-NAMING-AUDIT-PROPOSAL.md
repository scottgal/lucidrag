# Signal Naming for Auditing - Proposal

**Date**: 2026-01-04
**Status**: Proposal
**Scope**: Refactor signal naming to include emitter identification for auditing

## Problem Statement

Current signal naming does not clearly identify which Wave emitted each signal, making it difficult to:
1. **Audit signal sources**: Can't easily trace signals back to their emitting Wave
2. **Debug signal conflicts**: Multiple waves might emit similar signal keys
3. **Query by emitter**: Can't glob/filter signals by emitting component

### Current Pattern

```csharp
// VisionLlmWave emits:
Key = "vision.llm.caption"  // No emitter identification

// OcrQualityWave emits:
Key = "ocr.quality.spell_check_score"  // No emitter identification

// ColorWave emits:
Key = "color.grid.cell.0_0"  // No emitter identification
```

**Problem**: You can't distinguish between:
- Signals from the same domain but different waves
- Original signals vs derived signals
- Which component to fix if a signal is wrong

## Proposed Solution

### Pattern 1: Prefix with Wave Name

```csharp
// Before:
Key = "vision.llm.caption"

// After:
Key = "VisionLlmWave.vision.llm.caption"
```

**Glob Patterns**:
- `VisionLlmWave.**` - All signals from VisionLlmWave
- `*.vision.llm.*` - All vision LLM signals across any emitter
- `OcrQualityWave.ocr.quality.*` - All quality signals from OcrQualityWave

**Pros**:
- Clear emitter identification
- Backward compatible (just prefix)
- Easy to implement

**Cons**:
- Longer signal keys
- Redundant if emitter name matches domain

### Pattern 2: Metadata-Based Emitter

Keep signal keys unchanged, but add `emitter` metadata:

```csharp
signals.Add(new Signal
{
    Key = "vision.llm.caption",  // Keep existing
    Value = caption,
    Source = Name,  // Already exists ("VisionLlmWave")
    Metadata = new Dictionary<string, object>
    {
        ["emitter"] = "VisionLlmWave",  // Explicit emitter
        ["emitter_version"] = "2.0",    // Optional versioning
        ["emitter_priority"] = Priority  // Priority for ordering
    }
});
```

**Pros**:
- Backward compatible (signal keys unchanged)
- Emitter info in queryable metadata
- Supports versioning and priority

**Cons**:
- Requires metadata queries (not glob-friendly)
- Less explicit in signal key itself

### Pattern 3: Hybrid Approach (RECOMMENDED)

Use domain-first naming with emitter suffix:

```csharp
// Before:
Key = "vision.llm.caption"

// After:
Key = "vision.llm.caption@VisionLlmWave"
```

**Glob Patterns**:
- `vision.llm.*@VisionLlmWave` - All vision LLM signals from VisionLlmWave
- `vision.llm.caption@*` - Caption signals from any emitter
- `*@OcrQualityWave` - All signals from OcrQualityWave

**Pros**:
- Glob-friendly
- Domain-first (easier to find related signals)
- Emitter-last (easy to filter by source)
- More compact than Pattern 1

**Cons**:
- Requires key parsing to extract emitter
- Not standard convention (custom pattern)

## Recommended Implementation: Ephemeral Pattern (Sink.Coordinator.Atom)

### Naming Convention (Aligned with mostlylucid.ephemeral)

Following the ephemeral three-level scoping pattern:

```
<Sink>.<Coordinator>.<Atom>.<Property>
```

Where:
- **Sink**: Top-level system boundary (e.g., "image", "document", "analysis")
- **Coordinator**: Processing unit / Wave name (e.g., "vision_llm", "ocr_quality", "color")
- **Atom**: Specific signal/entity (e.g., "caption", "spell_check", "grid")
- **Property**: Optional property/action (e.g., "score", "completed", "cell.0_0")

**Examples**:
```csharp
// VisionLlmWave - Sink: image, Coordinator: vision_llm
"image.vision_llm.caption"
"image.vision_llm.entities"
"image.vision_llm.scene"

// OcrQualityWave - Sink: image, Coordinator: ocr_quality
"image.ocr_quality.spell_check.score"
"image.ocr_quality.spell_check.is_garbled"
"image.ocr_quality.corrected.text"

// ColorWave - Sink: image, Coordinator: color
"image.color.grid"
"image.color.grid.cell.0_0"
"image.color.dominant_colors"

// IdentityWave - Sink: image, Coordinator: identity
"image.identity.aspect_ratio"
"image.identity.width"
"image.identity.height"
```

**Ephemeral Pattern Benefits**:
- ✅ Consistent with mostlylucid.ephemeral reference implementation
- ✅ Clear three-level hierarchy: Sink → Coordinator → Atom
- ✅ Glob-friendly: `image.vision_llm.*` gets all vision LLM signals
- ✅ Auditable: Coordinator name identifies emitter
- ✅ Scalable: Can extend to multi-sink systems (image, document, video)

### Implementation Approach

#### Option A: Base Class Helper Method

Add to `IAnalysisWave` or a base class:

```csharp
public abstract class AnalysisWaveBase : IAnalysisWave
{
    public abstract string Name { get; }
    public abstract int Priority { get; }
    public abstract IReadOnlyList<string> Tags { get; }

    // Helper to create emitter-tagged signal keys
    protected string SignalKey(string key) => $"{key}@{Name}";

    // Usage in derived classes:
    // Key = SignalKey("vision.llm.caption")
    // Results in: "vision.llm.caption@VisionLlmWave"
}
```

#### Option B: Extension Method

```csharp
public static class SignalExtensions
{
    public static string WithEmitter(this string key, string emitterName)
        => $"{key}@{emitterName}";
}

// Usage:
Key = "vision.llm.caption".WithEmitter(Name)
```

#### Option C: Signal Builder

```csharp
public class SignalBuilder
{
    private string _domain;
    private string _key;
    private object _value;
    private double _confidence = 1.0;
    private string _source;
    private List<string> _tags = new();

    public SignalBuilder Domain(string domain)
    {
        _domain = domain;
        return this;
    }

    public SignalBuilder Key(string key)
    {
        _key = key;
        return this;
    }

    public SignalBuilder Value(object value)
    {
        _value = value;
        return this;
    }

    public Signal Build(string emitterName)
    {
        return new Signal
        {
            Key = $"{_domain}.{_key}@{emitterName}",
            Value = _value,
            Confidence = _confidence,
            Source = _source ?? emitterName,
            Tags = _tags
        };
    }
}

// Usage:
signals.Add(new SignalBuilder()
    .Domain("vision.llm")
    .Key("caption")
    .Value(caption)
    .Build(Name));
```

## Migration Strategy

### Phase 1: Add New Keys (Parallel Emission)

Emit both old and new signal keys during transition:

```csharp
// Emit old key (backward compatibility)
signals.Add(new Signal
{
    Key = "vision.llm.caption",
    Value = caption,
    ...
});

// Emit new key (with emitter)
signals.Add(new Signal
{
    Key = "vision.llm.caption@VisionLlmWave",
    Value = caption,
    ...
});
```

### Phase 2: Update Consumers

Update all signal consumers to use new pattern:

```csharp
// Before:
var caption = context.GetValue<string>("vision.llm.caption");

// After:
var caption = context.GetValue<string>("vision.llm.caption@VisionLlmWave");

// Or with fallback:
var caption = SignalResolver.GetFirstValue<string>(profile,
    "vision.llm.caption@VisionLlmWave",  // New pattern
    "vision.llm.caption"                  // Old pattern (fallback)
);
```

### Phase 3: Deprecate Old Keys

After all consumers updated, stop emitting old keys and log warnings for any usage:

```csharp
if (context.HasSignal("vision.llm.caption"))
{
    _logger?.LogWarning("Deprecated signal key 'vision.llm.caption' used. Use 'vision.llm.caption@VisionLlmWave' instead.");
}
```

### Phase 4: Remove Old Keys

Clean up all old signal emissions after 1-2 releases.

## Impact Analysis

### Files to Modify

| Wave | File | Signal Count | Priority |
|------|------|--------------|----------|
| **VisionLlmWave** | VisionLlmWave.cs | 6 signals | High (core feature) |
| **OcrQualityWave** | OcrQualityWave.cs | 11 signals | High (recently added) |
| **OcrWave** | OcrWave.cs | 8 signals | High (core feature) |
| **OcrVerificationWave** | OcrVerificationWave.cs | 6 signals | Medium |
| **ColorWave** | ColorWave.cs | 17 signals (+ 9 per-cell) | High (recently modified) |
| **IdentityWave** | IdentityWave.cs | 7 signals | High (foundational) |
| **AdvancedOcrWave** | AdvancedOcrWave.cs | ~15 signals | Medium |
| **VisionLlmEmbeddingWave** | VisionLlmEmbeddingWave.cs | 5 signals | Low |
| **ClipEmbeddingWave** | ClipEmbeddingWave.cs | 3 signals | Low |
| **FaceDetectionWave** | FaceDetectionWave.cs | 4 signals | Low |
| **ErrorLevelAnalysisWave** | ErrorLevelAnalysisWave.cs | 3 signals | Low |
| **DigitalFingerprintWave** | DigitalFingerprintWave.cs | 2 signals | Low |
| **ExifForensicsWave** | ExifForensicsWave.cs | 5 signals | Low |

**Total**: ~90+ signal emissions to update

### Testing Required

1. **Unit tests**: Update expected signal keys in all wave tests
2. **Integration tests**: Verify SignalResolver glob patterns work with new format
3. **Backward compatibility**: Ensure old consumers still work during Phase 1-2
4. **Performance**: Measure overhead of longer signal keys

## Benefits

1. **Auditing**: Clear trace of which Wave emitted each signal
2. **Debugging**: Easy to identify source of incorrect signals
3. **Querying**: Glob patterns enable powerful signal filtering
4. **Versioning**: Can version signal emitters independently
5. **Conflict Resolution**: Disambiguate signals from multiple waves

## Example Queries (Post-Refactor)

### Query all signals from OcrQualityWave
```csharp
var ocrQualitySignals = profile.Signals
    .Where(s => s.Key.EndsWith("@OcrQualityWave"))
    .ToList();
```

### Query all vision LLM signals from any emitter
```csharp
var visionLlmSignals = profile.Signals
    .Where(s => s.Key.StartsWith("vision.llm.") && s.Key.Contains("@"))
    .ToList();
```

### Glob pattern matching
```csharp
// Get all OCR correction signals from any emitter
var corrections = SignalResolver.ResolveSignals(profile, "ocr.corrected.*@*");

// Get all signals from VisionLlmWave
var visionSignals = SignalResolver.ResolveSignals(profile, "*@VisionLlmWave");

// Get caption from VisionLlmWave specifically
var caption = SignalResolver.GetFirstValue<string>(profile, "vision.llm.caption@VisionLlmWave");
```

## Recommendations

1. **Adopt Pattern 3** (Hybrid with `@` separator)
2. **Implement in phases** (parallel emission → update consumers → deprecate old)
3. **Start with high-priority waves** (VisionLlmWave, OcrQualityWave, ColorWave, IdentityWave)
4. **Add SignalBuilder helper** for cleaner code
5. **Update SignalResolver** to support new glob patterns
6. **Document pattern** in ANALYZERS.md and developer guide

## Next Steps

1. **Review and approve** this proposal
2. **Implement SignalBuilder** helper class
3. **Update IdentityWave** as pilot (smallest, foundational)
4. **Test SignalResolver** glob patterns with new format
5. **Roll out to remaining waves** (VisionLlmWave, OcrQualityWave, ColorWave, etc.)
6. **Update documentation** and examples
7. **Add migration guide** for downstream consumers

---

**Generated**: 2026-01-04
**Status**: Proposal - Awaiting Approval
**Estimated Effort**: 8-12 hours (full migration across all waves)
