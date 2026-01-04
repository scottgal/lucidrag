# CLI Signal-Based Design - Background Processes & Status Hooks

**Date**: 2026-01-04
**Status**: Design Proposal
**Ephemeral Pattern**: Aligned with mostlylucid.ephemeral

## Overview

Enable ImageCli to run long-running background processes that emit signals for status updates, with console hooks to display progress in real-time.

## Coordinator Types

### 1. Per-Interaction Coordinators (Stateless)

**Scope**: Single image analysis
**Lifetime**: Request → Response
**Example**: `ImageCli.exe image.jpg --output json`

**Signal Pattern**:
```
image.<coordinator>.<atom>.<action>

Example:
image.ocr.analysis.started
image.ocr.spell_check.completed
image.color.grid.processing
image.vision_llm.caption.completed
```

**Use Case**: Quick one-off analysis, no persistence needed

### 2. Cross-Conversation Coordinators (Stateful)

**Scope**: Batch processing across multiple images
**Lifetime**: Session start → Session end (with persistence)
**Example**: `ImageCli.exe batch F:\Images --session batch-001`

**Signal Pattern**:
```
batch.<session_id>.<coordinator>.<atom>.<action>

Example:
batch.batch-001.ocr.analysis.started
batch.batch-001.ocr.progress.25_percent
batch.batch-001.ocr.completed
batch.batch-001.results.persisted
```

**Use Case**: Process 100+ images with resumable progress

## Architecture

### Signal Flow

```
┌─────────────────┐
│   Background    │
│   Process       │  Emits signals
│  (Coordinator)  │──────────────┐
└─────────────────┘              │
                                 ▼
                         ┌───────────────┐
                         │  Signal Sink  │
                         └───────────────┘
                                 │
                    ┌────────────┴──────────────┐
                    ▼                           ▼
            ┌───────────────┐         ┌────────────────┐
            │ Console Hook  │         │ Persistence    │
            │ (Live Status) │         │ (State Store)  │
            └───────────────┘         └────────────────┘
```

### Components

#### 1. ScopedSignalEmitter

Emit signals with full context (Sink.Coordinator.Atom):

```csharp
public class ImageAnalysisCoordinator
{
    private readonly ScopedSignalEmitter _emitter;
    private readonly string _sessionId;

    public ImageAnalysisCoordinator(string sessionId, ISignalSink sink)
    {
        var context = new SignalContext(
            Sink: "image",              // Top-level boundary
            Coordinator: sessionId,      // Session/batch ID
            Atom: "analysis"             // Operation name
        );
        _emitter = new ScopedSignalEmitter(context, Guid.NewGuid().ToString(), sink);
    }

    public async Task AnalyzeAsync(string imagePath, CancellationToken ct)
    {
        _emitter.Emit("started");  // → "image.{sessionId}.analysis.started"

        // Run OCR
        _emitter.Emit("ocr.processing");
        var ocrResult = await RunOcrAsync(imagePath, ct);
        _emitter.Emit("ocr.completed", new { confidence = ocrResult.Confidence });

        // Run color analysis
        _emitter.Emit("color.processing");
        var colorResult = await RunColorAnalysisAsync(imagePath, ct);
        _emitter.Emit("color.completed");

        _emitter.Emit("completed");  // → "image.{sessionId}.analysis.completed"
    }
}
```

#### 2. ConsoleStatusHook

Hook signals to display real-time progress:

```csharp
public class ConsoleStatusHook
{
    private readonly ISignalSink _sink;
    private readonly Dictionary<string, ProgressState> _progress = new();

    public ConsoleStatusHook(ISignalSink sink)
    {
        _sink = sink;

        // Subscribe to status signals
        _sink.Subscribe("image.*.analysis.started", OnAnalysisStarted);
        _sink.Subscribe("image.*.ocr.processing", OnOcrProcessing);
        _sink.Subscribe("image.*.ocr.completed", OnOcrCompleted);
        _sink.Subscribe("image.*.color.processing", OnColorProcessing);
        _sink.Subscribe("image.*.color.completed", OnColorCompleted);
        _sink.Subscribe("image.*.analysis.completed", OnAnalysisCompleted);
    }

    private void OnAnalysisStarted(string signal)
    {
        Console.WriteLine($"⏳ Starting image analysis...");
    }

    private void OnOcrProcessing(string signal)
    {
        Console.Write("🔍 OCR processing... ");
    }

    private void OnOcrCompleted(string signal)
    {
        Console.WriteLine("✅");
    }

    private void OnColorProcessing(string signal)
    {
        Console.Write("🎨 Color analysis... ");
    }

    private void OnColorCompleted(string signal)
    {
        Console.WriteLine("✅");
    }

    private void OnAnalysisCompleted(string signal)
    {
        Console.WriteLine("✨ Analysis complete!");
    }
}
```

#### 3. BatchProcessingCoordinator

Handle multi-image processing with progress tracking:

```csharp
public class BatchProcessingCoordinator
{
    private readonly ScopedSignalEmitter _emitter;
    private readonly ISignalSink _sink;
    private readonly string _batchId;

    public BatchProcessingCoordinator(string batchId, ISignalSink sink)
    {
        _batchId = batchId;
        _sink = sink;

        var context = new SignalContext(
            Sink: "batch",
            Coordinator: batchId,
            Atom: "processing"
        );
        _emitter = new ScopedSignalEmitter(context, Guid.NewGuid().ToString(), sink);
    }

    public async Task ProcessImagesAsync(string[] imagePaths, CancellationToken ct)
    {
        _emitter.Emit("started", new { total = imagePaths.Length });

        for (int i = 0; i < imagePaths.Length; i++)
        {
            var imagePath = imagePaths[i];

            _emitter.Emit("image.processing", new {
                index = i,
                total = imagePaths.Length,
                path = imagePath
            });

            try
            {
                var analyzer = new ImageAnalysisCoordinator($"{_batchId}.image_{i}", _sink);
                await analyzer.AnalyzeAsync(imagePath, ct);

                _emitter.Emit("image.completed", new { index = i });
            }
            catch (Exception ex)
            {
                _emitter.Emit("image.failed", new {
                    index = i,
                    path = imagePath,
                    error = ex.Message
                });
            }

            // Emit progress
            var progressPercent = (int)((i + 1) * 100.0 / imagePaths.Length);
            _emitter.Emit("progress", new { percent = progressPercent });
        }

        _emitter.Emit("completed", new { processed = imagePaths.Length });
    }
}
```

#### 4. BatchProgressHook

Display batch progress with fancy UI:

```csharp
public class BatchProgressHook
{
    private readonly ISignalSink _sink;
    private int _total = 0;
    private int _completed = 0;
    private int _failed = 0;

    public BatchProgressHook(ISignalSink sink)
    {
        _sink = sink;

        _sink.Subscribe("batch.*.processing.started", OnBatchStarted);
        _sink.Subscribe("batch.*.processing.image.completed", OnImageCompleted);
        _sink.Subscribe("batch.*.processing.image.failed", OnImageFailed);
        _sink.Subscribe("batch.*.processing.progress", OnProgress);
        _sink.Subscribe("batch.*.processing.completed", OnBatchCompleted);
    }

    private void OnBatchStarted(string signal, dynamic? data)
    {
        _total = data?.total ?? 0;
        Console.WriteLine($"📦 Starting batch processing: {_total} images");
        DrawProgressBar(0);
    }

    private void OnImageCompleted(string signal, dynamic? data)
    {
        _completed++;
    }

    private void OnImageFailed(string signal, dynamic? data)
    {
        _failed++;
        Console.WriteLine($"❌ Failed: {data?.path} - {data?.error}");
    }

    private void OnProgress(string signal, dynamic? data)
    {
        var percent = data?.percent ?? 0;
        DrawProgressBar(percent);
    }

    private void OnBatchCompleted(string signal, dynamic? data)
    {
        Console.WriteLine();
        Console.WriteLine($"✨ Batch complete! {_completed} succeeded, {_failed} failed");
    }

    private void DrawProgressBar(int percent)
    {
        var width = 50;
        var filled = (int)(width * percent / 100.0);
        var bar = new string('█', filled) + new string('░', width - filled);
        Console.Write($"\r[{bar}] {percent}% ({_completed}/{_total})");
    }
}
```

## CLI Commands

### Single Image (Per-Interaction)

```bash
# Simple analysis with live status
ImageCli.exe image.jpg

# Output:
# ⏳ Starting image analysis...
# 🔍 OCR processing... ✅
# 🎨 Color analysis... ✅
# ✨ Analysis complete!
```

### Batch Processing (Cross-Conversation)

```bash
# Process multiple images with progress bar
ImageCli.exe batch F:\Images --session batch-001

# Output:
# 📦 Starting batch processing: 100 images
# [████████████████████████░░░░░░░░░░░░░░░░░░░░░░░░░░] 45% (45/100)
```

### Resumable Batch (With Persistence)

```bash
# Start batch (gets interrupted)
ImageCli.exe batch F:\Images --session batch-001 --persist

# Resume batch later
ImageCli.exe resume batch-001

# Output:
# 📦 Resuming batch: 55 images remaining
# [█████████████████████████████████████████░░░░░░░░░] 82% (82/100)
```

## Persistence

### State Store

```csharp
public class BatchStateStore
{
    private readonly string _stateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LucidRAG", "BatchState"
    );

    public void SaveState(string batchId, BatchState state)
    {
        var statePath = Path.Combine(_stateDir, $"{batchId}.json");
        Directory.CreateDirectory(_stateDir);
        File.WriteAllText(statePath, JsonSerializer.Serialize(state));
    }

    public BatchState? LoadState(string batchId)
    {
        var statePath = Path.Combine(_stateDir, $"{batchId}.json");
        if (!File.Exists(statePath)) return null;

        var json = File.ReadAllText(statePath);
        return JsonSerializer.Deserialize<BatchState>(json);
    }
}

public class BatchState
{
    public string BatchId { get; set; }
    public string[] AllImages { get; set; }
    public HashSet<string> ProcessedImages { get; set; } = new();
    public Dictionary<string, string> Errors { get; set; } = new();
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
```

## Signal Patterns

### Progress Signals

```csharp
// Analysis lifecycle
"image.{sessionId}.analysis.started"
"image.{sessionId}.analysis.processing"
"image.{sessionId}.analysis.completed"
"image.{sessionId}.analysis.failed"

// Phase progress
"image.{sessionId}.ocr.started"
"image.{sessionId}.ocr.processing"
"image.{sessionId}.ocr.completed"
"image.{sessionId}.color.started"
"image.{sessionId}.color.processing"
"image.{sessionId}.color.completed"

// Batch progress
"batch.{batchId}.processing.started"
"batch.{batchId}.processing.progress"  // { percent: 45 }
"batch.{batchId}.processing.image.completed"  // { index: 12 }
"batch.{batchId}.processing.image.failed"  // { index: 23, error: "..." }
"batch.{batchId}.processing.completed"
```

### Error Signals

```csharp
// Wave-level errors
"image.{sessionId}.ocr.error"  // { message: "Tesseract failed", exception: "..." }
"image.{sessionId}.vision_llm.error"  // { message: "Ollama unavailable" }

// Batch-level errors
"batch.{batchId}.processing.error"  // { message: "Disk full" }
```

### Telemetry Signals

```csharp
// Performance metrics
"image.{sessionId}.performance.wave_completed"  // { wave: "OcrWave", duration_ms: 1234 }
"image.{sessionId}.performance.total_duration"  // { duration_ms: 3456 }

// Resource usage
"image.{sessionId}.resources.memory_peak"  // { bytes: 123456789 }
"image.{sessionId}.resources.disk_io"  // { read_bytes: 456, write_bytes: 123 }
```

## Implementation Plan

### Phase 1: Signal Infrastructure
1. Create `ScopedSignalEmitter` wrapper for ephemeral pattern
2. Create `ISignalSink` interface for signal subscriptions
3. Implement `InMemorySignalSink` for local processing

### Phase 2: Console Hooks
4. Create `ConsoleStatusHook` for live progress display
5. Create `BatchProgressHook` for batch processing UI
6. Add signal subscription system to ImageCli Program.cs

### Phase 3: Batch Processing
7. Create `BatchProcessingCoordinator`
8. Create `BatchStateStore` for persistence
9. Add `batch` and `resume` commands to ImageCli

### Phase 4: Testing
10. Test single image analysis with status hooks
11. Test batch processing with progress bar
12. Test resume functionality with persistence

## Benefits

1. **Real-Time Feedback**: Users see progress as it happens
2. **Resumable**: Can restart interrupted batches
3. **Auditable**: All operations emit signals for logging/monitoring
4. **Decoupled**: Console hooks are separate from processing logic
5. **Scalable**: Can process 1000+ images with persistent state

## Example Session

```bash
$ ImageCli.exe batch F:\Gifs --session gif-analysis --persist

📦 Starting batch processing: 8 images
⏳ [1/8] BackOfTheNet.gif
  🔍 OCR processing... ✅
  🎨 Color analysis... ✅
⏳ [2/8] anchorman-not-even-mad.gif
  🔍 OCR processing... ✅
  🎨 Color analysis... ✅
[████████████░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░] 25% (2/8)
...
[██████████████████████████████████████████████████] 100% (8/8)
✨ Batch complete! 8 succeeded, 0 failed

Results saved to: C:\Users\scott\AppData\Roaming\LucidRAG\BatchState\gif-analysis\results.json
```

---

**Status**: Design Proposal
**Next Step**: Implement Phase 1 (Signal Infrastructure)
**Estimated Effort**: 6-8 hours (full implementation)
