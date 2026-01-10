# ImageSummarizer.Core

**Signal-based image intelligence with 22-wave ML pipeline and unified IPipeline integration.**

Deterministic image profiling with optional OCR, motion analysis, embeddings, and constrained vision captions. Part of the LucidRAG unified pipeline architecture.

## Key Features

- **Signal-Based Architecture**: 22+ waves emit typed, confidence-scored signals
- **Unified Pipeline**: Integrates with `IPipeline` interface for seamless multi-modal processing
- **Intelligent Escalation**: Deterministic gates decide OCR → Florence-2 → Vision LLM routing
- **Filmstrip Optimization**: 30× token reduction for GIF/WebP subtitle extraction
- **Content Hashing**: XxHash64-based caching with SQLite signal persistence
- **Execution Profiles**: Fast (~100ms), Balanced, Quality modes

## Install

```bash
# NuGet package
dotnet add package ImageSummarizer.Core

# Or use via LucidRAG unified CLI
dotnet tool install -g LucidRAG.Cli
```

## Quick Start

### Via Unified Pipeline (Recommended)

```csharp
using Mostlylucid.Summarizer.Core.Pipeline;

// Register all pipelines
services.AddDocSummarizerImages();  // Adds ImagePipeline
services.AddPipelineRegistry();

// Get registry and process
var registry = serviceProvider.GetRequiredService<IPipelineRegistry>();
var pipeline = registry.FindForFile("image.gif");

var result = await pipeline.ProcessAsync("image.gif");
foreach (var chunk in result.Chunks)
{
    Console.WriteLine($"{chunk.ContentType}: {chunk.Text}");
    Console.WriteLine($"Confidence: {chunk.Confidence:P1}");
}
```

### Direct WaveOrchestrator Usage

```csharp
using Mostlylucid.DocSummarizer.Images.Services.Analysis;

var orchestrator = serviceProvider.GetRequiredService<WaveOrchestrator>();
var profile = await orchestrator.AnalyzeAsync("image.png");

// Access signals
var ocrText = profile.GetValue<string>(ImageSignalKeys.OcrText);
var caption = profile.GetValue<string>(ImageSignalKeys.Caption);
var motion = profile.GetValue<string>(ImageSignalKeys.Motion);
```

## Unified Pipeline Integration

ImagePipeline implements `IPipeline` interface:

```csharp
public class ImagePipeline : PipelineBase
{
    public override string PipelineId => "image";
    public override string Name => "Image Pipeline";
    public override IReadOnlySet<string> SupportedExtensions =>
        new[] { ".gif", ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tiff", ".tif" };

    protected override async Task<IReadOnlyList<ContentChunk>> ProcessCoreAsync(...)
    {
        // Orchestrates 22-wave analysis
        // Returns standardized ContentChunk objects
    }
}
```

**Benefits:**
- Auto-routing by file extension via `IPipelineRegistry`
- Standardized `ContentChunk` output across all pipelines (Document, Image, Data)
- Unified progress reporting and cancellation
- XxHash64 content hashing for deduplication

## Execution Profiles

```csharp
services.AddDocSummarizerImages(opt =>
{
    opt.ExecutionProfile = ExecutionProfile.Fast;      // ~100ms - basic signals only
    opt.ExecutionProfile = ExecutionProfile.Balanced;  // ~500ms - Florence-2 + OCR
    opt.ExecutionProfile = ExecutionProfile.Quality;   // ~2-5s  - Full pipeline + LLM
});
```

| Profile | Waves Executed | Speed | Use Case |
|---------|---------------|-------|----------|
| Fast | 5-8 waves | ~100ms | Triage, type detection |
| Balanced | 12-15 waves | ~500ms | General analysis, screenshots |
| Quality | 20+ waves | ~2-5s | Complex images, GIFs with motion |

## 22-Wave Signal Catalog

| Wave | Signals Emitted | Purpose |
|------|----------------|---------|
| StructureWave | dimensions, aspect_ratio, file_size | Metadata |
| ColorWave | dominant_colors, brightness, contrast | Visual properties |
| OcrWave | ocr.text, ocr.confidence, ocr.word_count | Text extraction |
| MlOcrWave | ml_ocr.text, text_regions | ML-based text detection |
| Florence2Wave | florence2.caption, florence2.ocr | Vision foundation model |
| VisionLlmWave | llm.caption, llm.description | LLM escalation |
| MotionWave | motion.intensity, motion.type, motion.direction | Optical flow |
| FaceDetectionWave | faces.count, faces.locations | Face detection |
| SceneDetectionWave | scene.type, scene.confidence | Indoor/outdoor/meme |
| ClipEmbeddingWave | clip.embedding | Semantic search vectors |
| TextDetectionWave | text_regions.bbox, text_regions.confidence | EAST detection |
| OcrQualityWave | ocr.quality_score, needs_escalation | Validation |
| ContradictionWave | contradictions.found, contradictions.details | Consistency |
| AutoRoutingWave | routing.decision, routing.reason | Pipeline selection |
| *...and 8 more* | | |

## Filmstrip Optimization

For animated GIFs/WebP with subtitles:

```csharp
// Automatically enabled when text-only content detected
var profile = await orchestrator.AnalyzeAsync("animation.gif");

// Text-only strip is 30× smaller in tokens
var stripPath = profile.GetValue<string>("filmstrip.text_only_path");
```

**Performance:**
- **Before**: 10 frames × ~150 tokens each = 1500 tokens, ~27 seconds
- **After**: Single strip × ~50 tokens = 50 tokens, ~2-3 seconds

## CLI Usage

```bash
# Analyze single image
imagesummarizer image.gif

# Process directory
imagesummarizer ./screenshots --output json

# List available pipelines
imagesummarizer --list-pipelines

# MCP server mode (Claude Desktop integration)
imagesummarizer --mcp
```

## Signal-Based Request Pattern

Request only the signals you need:

```csharp
var orchestrator = new WaveOrchestrator(services);

// Request specific signals
var profile = await orchestrator.AnalyzeAsync(
    "image.png",
    requestedSignals: new[] { "ocr.text", "color.dominant", "motion.*" }
);

// Only relevant waves execute
if (profile.HasSignal("ocr.text"))
{
    var text = profile.GetValue<string>("ocr.text");
}
```

## Content Hashing

All pipelines use XxHash64 for fast, consistent hashing:

```csharp
// Automatically computed for each chunk
var hash = ContentHasher.ComputeHash(imageContent);

// Used for:
// - Deduplication
// - Cache keys
// - Change detection
```

## Configuration

```json
{
  "ImageAnalysis": {
    "ModelsDirectory": "./models",
    "EnableOcr": true,
    "Ocr": {
      "UseAdvancedPipeline": true,
      "QualityMode": "Fast",
      "ConfidenceThresholdForEarlyExit": 0.95,
      "EnableSpellChecking": true
    },
    "ExecutionProfile": "Balanced"
  }
}
```

## Service Registration

```csharp
using Mostlylucid.DocSummarizer.Images.Extensions;

services.AddDocSummarizerImages(opt =>
{
    opt.ModelsDirectory = Path.Combine(dataDir, "models");
    opt.EnableOcr = true;
    opt.Ocr.UseAdvancedPipeline = true;
    opt.Ocr.QualityMode = OcrQualityMode.Fast;
    opt.ExecutionProfile = ExecutionProfile.Balanced;
});

// Registers:
// - ImagePipeline (implements IPipeline)
// - WaveOrchestrator
// - All 22 waves
// - Signal database
// - ONNX session factory
```

## Documentation

- **SIGNALS.md** - Complete signal catalog with descriptions
- **GIF-MOTION.md** - Motion analysis and filmstrip optimization
- **WAVE-COORDINATION.md** - Signal-based coordination architecture

## Architecture

```
ImagePipeline (IPipeline)
  └─ WaveOrchestrator
       ├─ ProfiledWaveCoordinator (signal-based execution)
       │    ├─ StructureWave
       │    ├─ ColorWave
       │    ├─ OcrWave
       │    ├─ MlOcrWave
       │    ├─ Florence2Wave
       │    ├─ VisionLlmWave
       │    ├─ MotionWave
       │    ├─ FaceDetectionWave
       │    └─ ...18 more waves
       │
       └─ SignalDatabase (SQLite persistence)
```

## License

MIT - Part of the LucidRAG project
