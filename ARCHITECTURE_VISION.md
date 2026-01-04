# ImageCli Pipeline Architecture Vision

## Current State vs. Future Vision

### Current Architecture (Monolithic)

```
User Request → BatchCommand → EscalationService (does everything) → SignalDatabase
```

**Problems:**
- Monolithic service doing all work
- No signal-based coordination
- Hard to add new processing pipelines
- Can't run as persistent API service
- No reusable atoms/primitives

### Future Architecture (Signal-Based Pipelines)

Inspired by `mostlylucid.ephemeral` pattern:

```
API Service
├── SignalSink (shared coordination bus)
├── Coordinator (work orchestrator)
├── Atoms (reusable primitives)
│   ├── DeterministicAnalysisAtom
│   ├── VisionLLMAtom
│   ├── OCRAtom
│   ├── ThumbnailAtom
│   ├── CachingAtom
│   ├── GifFrameExtractionAtom
│   └── MotionAnalysisAtom
├── Molecules (pipeline assemblies)
│   ├── StandardImagePipeline
│   ├── ThumbnailPipeline
│   ├── OCRPipeline
│   └── GifAnalysisPipeline
└── Signal-based coordination
```

## Signal-Based Coordination Pattern

### Atoms Emit Signals

```csharp
public class DeterministicAnalysisAtom
{
    private readonly SignalSink _sink;
    private ImageProfile _lastProfile;

    public async Task AnalyzeAsync(string imagePath, CancellationToken ct)
    {
        var profile = await _analyzer.AnalyzeAsync(imagePath, ct);
        _lastProfile = profile;

        // ✅ Pure notification signal - no state
        _sink.Raise("analysis.deterministic.complete");

        // If low confidence, emit escalation signal
        if (profile.TypeConfidence < 0.7)
        {
            _sink.Raise("analysis.needs_escalation");
        }
    }

    // Atom is source of truth
    public ImageProfile GetLastProfile() => _lastProfile;
}
```

### Other Atoms React to Signals

```csharp
public class VisionLLMAtom
{
    private readonly SignalSink _sink;
    private readonly DeterministicAnalysisAtom _analysisAtom;
    private string _lastCaption = "";

    public VisionLLMAtom(SignalSink sink, DeterministicAnalysisAtom analysisAtom)
    {
        _sink = sink;
        _analysisAtom = analysisAtom;

        // React to escalation signals
        _sink.SignalRaised += async signal =>
        {
            if (signal.Signal == "analysis.needs_escalation")
            {
                // Query analysis atom for current state
                var profile = _analysisAtom.GetLastProfile();
                var caption = await GenerateCaptionAsync(profile.ImagePath);

                _lastCaption = caption;
                _sink.Raise("llm.caption.complete");
            }
        };
    }

    public string GetLastCaption() => _lastCaption;
}
```

### Molecules Compose Atoms

```csharp
public class StandardImagePipeline
{
    private readonly SignalSink _sink;
    private readonly DeterministicAnalysisAtom _analysis;
    private readonly VisionLLMAtom _visionLlm;
    private readonly CachingAtom _caching;

    public StandardImagePipeline()
    {
        _sink = new SignalSink();
        _analysis = new DeterministicAnalysisAtom(_sink);
        _visionLlm = new VisionLLMAtom(_sink, _analysis);
        _caching = new CachingAtom(_sink, _analysis, _visionLlm);

        // Wire up the molecule
        WireSignals();
    }

    private void WireSignals()
    {
        // When deterministic completes, trigger caching if no escalation needed
        _sink.SignalRaised += signal =>
        {
            if (signal.Signal == "analysis.deterministic.complete")
            {
                var profile = _analysis.GetLastProfile();
                if (profile.TypeConfidence >= 0.7)
                {
                    _caching.StoreAsync(profile);
                }
            }
        };

        // When LLM caption completes, store with caption
        _sink.SignalRaised += signal =>
        {
            if (signal.Signal == "llm.caption.complete")
            {
                var profile = _analysis.GetLastProfile();
                var caption = _visionLlm.GetLastCaption();
                _caching.StoreWithCaptionAsync(profile, caption);
            }
        };
    }

    public async Task ProcessAsync(string imagePath, CancellationToken ct)
    {
        // Start the pipeline - atoms coordinate via signals
        await _analysis.AnalyzeAsync(imagePath, ct);
    }
}
```

## Signal Catalog (Formalized)

### Deterministic Analysis Signals

| Signal | Type | Description |
|--------|------|-------------|
| `analysis.deterministic.started` | Notification | Analysis started |
| `analysis.deterministic.complete` | Notification | Analysis completed successfully |
| `analysis.deterministic.failed` | Notification | Analysis failed |
| `analysis.needs_escalation` | Context | Low confidence, needs LLM |
| `analysis.type.low_confidence` | Context + Hint | `analysis.type.low_confidence:0.42` |

### Vision LLM Signals

| Signal | Type | Description |
|--------|------|-------------|
| `llm.caption.started` | Notification | Caption generation started |
| `llm.caption.complete` | Notification | Caption complete |
| `llm.caption.failed` | Notification | Caption failed |
| `llm.caption.timeout` | Notification | LLM request timed out |

### Caching Signals

| Signal | Type | Description |
|--------|------|-------------|
| `cache.hit` | Context + Hint | `cache.hit:sha256hash` |
| `cache.miss` | Notification | Cache miss |
| `cache.stored` | Notification | Profile stored |
| `cache.error` | Notification | Storage failed |

### GIF Processing Signals

| Signal | Type | Description |
|--------|------|-------------|
| `gif.frame.extracted` | Context + Hint | `gif.frame.extracted:12` (frame count) |
| `gif.motion.detected` | Context + Hint | `gif.motion.detected:right` (direction) |
| `gif.ocr.complete` | Notification | Per-frame OCR complete |

## API Service Design

### Running as Persistent Service

```csharp
public class ImageProcessingService : BackgroundService
{
    private readonly SignalSink _sink;
    private readonly EphemeralWorkCoordinator<ImageRequest> _coordinator;
    private readonly StandardImagePipeline _pipeline;

    public ImageProcessingService()
    {
        _sink = new SignalSink();
        _pipeline = new StandardImagePipeline(_sink);

        _coordinator = new EphemeralWorkCoordinator<ImageRequest>(
            async (req, ct) => await _pipeline.ProcessAsync(req.ImagePath, ct),
            new EphemeralOptions
            {
                MaxConcurrency = 8,
                Signals = _sink
            });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Service runs continuously, accepting image requests
        await _coordinator.DrainAsync();
    }

    public async Task<long> EnqueueImageAsync(string imagePath)
    {
        return await _coordinator.EnqueueWithIdAsync(new ImageRequest(imagePath));
    }
}
```

### Multiple Pipelines Concurrently

```csharp
public class MultiPipelineService
{
    private readonly SignalSink _sink;
    private readonly StandardImagePipeline _standardPipeline;
    private readonly ThumbnailPipeline _thumbnailPipeline;
    private readonly OCRPipeline _ocrPipeline;

    public MultiPipelineService()
    {
        _sink = new SignalSink();

        // All pipelines share the same signal sink
        _standardPipeline = new StandardImagePipeline(_sink);
        _thumbnailPipeline = new ThumbnailPipeline(_sink);
        _ocrPipeline = new OCRPipeline(_sink);

        // Wire cross-pipeline coordination
        WireCrossPipelineSignals();
    }

    private void WireCrossPipelineSignals()
    {
        // When standard analysis detects high text, trigger OCR pipeline
        _sink.SignalRaised += async signal =>
        {
            if (signal.Signal == "analysis.deterministic.complete")
            {
                var profile = _standardPipeline.GetLastProfile();
                if (profile.TextLikeliness > 0.6)
                {
                    await _ocrPipeline.ProcessAsync(profile.ImagePath);
                }
            }
        };

        // When standard analysis completes, always generate thumbnail
        _sink.SignalRaised += async signal =>
        {
            if (signal.Signal == "analysis.deterministic.complete")
            {
                var profile = _standardPipeline.GetLastProfile();
                await _thumbnailPipeline.ProcessAsync(profile.ImagePath);
            }
        };
    }
}
```

## Benefits of Signal-Based Architecture

### 1. Reusable Atoms

```csharp
// Same atoms used in different molecules
var fastPipeline = new FastImagePipeline(
    new DeterministicAnalysisAtom(sink),
    new CachingAtom(sink));

var fullPipeline = new FullImagePipeline(
    new DeterministicAnalysisAtom(sink),
    new VisionLLMAtom(sink),
    new OCRAtom(sink),
    new ThumbnailAtom(sink),
    new CachingAtom(sink));
```

### 2. Easy to Add New Pipelines

```csharp
// New GIF pipeline reuses existing atoms + new GIF-specific atoms
var gifPipeline = new GifAnalysisPipeline(
    new DeterministicAnalysisAtom(sink),  // Reused
    new GifFrameExtractionAtom(sink),     // New
    new MotionAnalysisAtom(sink),         // New
    new VisionLLMAtom(sink),              // Reused
    new CachingAtom(sink));               // Reused
```

### 3. Observable Pipelines

```csharp
// Monitor all signals across all pipelines
_sink.SignalRaised += signal =>
{
    _logger.LogInformation("Signal: {Signal} from operation {OpId}",
        signal.Signal, signal.OperationId);
};

// Metrics
var deterministic = _sink.CountSignals("analysis.deterministic.complete");
var escalated = _sink.CountSignals("llm.caption.complete");
var cached = _sink.CountSignals("cache.stored");
```

### 4. Non-Blocking Coordination

```csharp
// Fast, lock-free signal emission
_sink.Raise("analysis.deterministic.complete");  // No locks!

// Listeners coordinate asynchronously
_sink.SignalRaised += async signal =>
{
    // Process when ready, query atoms for current state
    var profile = _analysisAtom.GetLastProfile();
    await StoreAsync(profile);
};
```

## Migration Path

### Phase 1: Extract Atoms (No Breaking Changes)

Keep EscalationService but extract internal logic to atoms:

```csharp
public class EscalationService
{
    private readonly DeterministicAnalysisAtom _analysis;
    private readonly VisionLLMAtom _visionLlm;
    private readonly CachingAtom _caching;

    // Same public API, atoms internally
    public async Task<EscalationResult> AnalyzeAsync(string imagePath, CancellationToken ct)
    {
        await _analysis.AnalyzeAsync(imagePath, ct);
        // ... rest of logic
    }
}
```

### Phase 2: Add Signal Coordination

Wire atoms with signals:

```csharp
private readonly SignalSink _sink = new();

private void WireAtoms()
{
    _sink.SignalRaised += HandleSignals;
}
```

### Phase 3: Create Molecules

Extract pipelines:

```csharp
var pipeline = new StandardImagePipeline(_sink);
```

### Phase 4: API Service

Expose as service:

```csharp
var service = new ImageProcessingService(pipeline);
await service.StartAsync();
```

## Reference Implementation

See `D:\Source\mostlylucid.atoms\mostlylucid.ephemeral` for:
- Signal-based coordination patterns
- Atom/molecule examples
- Coordinator usage
- Shared SignalSink patterns
- Responsibility signals (pinning until queried)

## Next Steps

1. ✅ Document vision (this file)
2. ⏳ Create test project (Priority #2)
3. ⏳ Extract atoms from EscalationService (Phase 1)
4. ⏳ Add signal coordination (Phase 2)
5. ⏳ Create molecule pipelines (Phase 3)
6. ⏳ API service implementation (Phase 4)
