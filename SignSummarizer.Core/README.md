# SignSummarizer

Sign language recognition system with ONNX-based hand detection and temporal analysis for Avalonia applications.

## Overview

SignSummarizer implements a multi-stage pipeline for real-time sign language recognition using computer vision and machine learning techniques:

- **Stage A**: Hand detection and 21-point landmark extraction (per frame)
- **Stage B**: Landmark canonicalization (scale, rotation, translation normalization)
- **Stage C**: Streaming segmentation into atomic units (holds, transitions, boundaries)
- **Stage D**: Pose embedding and classification
- **Stage E**: Non-manual modifiers detection (brows, head, mouth, torso)

## Architecture

### Core Pipeline

```
Video → Hand Detection → Landmarks → Canonicalization → Segmentation → Atoms → Embeddings → Vector Store
                              ↓
                         Non-manual Modifiers
```

### Key Components

1. **Models** (`Models/`)
   - `Point3D` - 3D point with distance calculations
   - `HandLandmarks` - 21-point hand landmarks with confidence
   - `FrameLandmarks` - All landmarks for a single frame
   - `SignAtom` - Atomic sign unit (hold/transition/boundary)
   - `CanonicalLandmarks` - Normalized hand pose
   - `PoseFilmstrip` - Keyframe extraction (filmstrip technique)
   - `NonManualModifiers` - Face/pose modifiers

2. **Services** (`Services/`)
   - `OnnxRunner` - Reusable ONNX inference with session pooling
   - `ModelLoader` - ONNX model management
   - `HandDetectionService` - Hand detection using ONNX models
   - `CanonicalizationService` - Landmark normalization
   - `SegmentationService` - Stream-based atom segmentation
   - `PoseEmbeddingService` - Vector embedding generation
   - `FilmstripService` - Keyframe deduplication
   - `ModifierDetectionService` - Face/pose analysis
   - `SignVectorStore` - RAG-style vector retrieval
   - `SignProcessingPipeline` - Main pipeline orchestrator

## Features

### Hand Filmstrip Technique

Instead of processing every frame, SignSummarizer extracts only N key poses (6-12) per sign atom, dramatically reducing computational cost while preserving essential motion information.

### Vector Store Retrieval

Each sign atom generates an embedding vector stored in a vector store, enabling:
- Similar sign retrieval across all signers
- Signer-specific variant adaptation
- RAG-style querying for downstream applications

### Non-Manual Modifiers

Parallel detection of facial expressions and body movements:
- Brow position (raised/furrowed) - questions, conditionals
- Head motion (nod/shake) - affirmation, negation
- Mouth shape - intensifiers, adverbs
- Torso shift - role shift, emphasis

## Usage

### Basic Setup

```csharp
// Register services
var services = new ServiceCollection();
services.AddSignSummarizerCore(
    modelsDirectory: @"C:\Models\SignSummarizer",
    dominantHand: HandSide.Right);

services.AddSignSummarizerServices();

var provider = services.BuildServiceProvider();

// Process video
var pipeline = provider.GetRequiredService<ISignProcessingPipeline>();
await foreach (var atom in pipeline.ProcessVideoAsync("sign_video.mp4", "signer123"))
{
    Console.WriteLine($"Atom: {atom.Type} ({atom.StartTime.TotalSeconds:F2}s - {atom.EndTime.TotalSeconds:F2}s)");
    Console.WriteLine($"  Keyframes: {atom.KeyFrameIndices.Count}");
    Console.WriteLine($"  Modifiers: {atom.Modifiers?.HasModifiers ?? false}");
}
```

### Search Similar Signs

```csharp
var pipeline = provider.GetRequiredService<ISignProcessingPipeline>();
var matches = await pipeline.SearchSimilarSignsAsync(queryAtom, topK: 5, signerId: "signer123");

foreach (var match in matches)
{
    Console.WriteLine($"Match: {match.Similarity:F2} - {match.Atom.Type}");
}
```

## Model Requirements

Place ONNX models in the models directory:

```
~/.local/share/SignSummarizer/Models/
  ├── hand_landmarks.onnx     # Hand landmark detection (21 keypoints)
  ├── face_landmarks.onnx     # Facial landmarks for modifiers
  └── pose_landmarks.onnx     # Body pose landmarks
```

### Model Input/Output

#### Hand Landmark Model
- Input: `[1, 3, 256, 256]` - RGB image normalized to [0,1]
- Output: `[63]` - 21 keypoints × (x, y, z) coordinates

#### Face/Pose Models
- Input: `[1, 3, 256, 256]` - RGB image
- Output: Varies by model (landmarks, confidence scores)

## Configuration

### Segmentation Parameters

```csharp
var segmentationService = new SegmentationService(
    logger,
    holdMotionThreshold: 0.02f,      // Low motion = hold
    transitionMotionThreshold: 0.1f, // High motion = transition
    minAtomDuration: TimeSpan.FromMilliseconds(100),
    minHoldDuration: TimeSpan.FromMilliseconds(200));
```

### Filmstrip Parameters

```csharp
var filmstripService = new FilmstripService(
    logger,
    canonicalizationService,
    maxKeyFrames: 12,
    noveltyThreshold: 0.1f);
```

### Embedding Dimensions

```csharp
var embeddingService = new PoseEmbeddingService(
    logger,
    embeddingDimensions: 128); // Default 128-dimensional vectors
```

## Building

```bash
# Build solution
dotnet build SignSummarizer.sln

# Run Avalonia UI
dotnet run --project SignSummarizer.UI/SignSummarizer.UI.csproj

# Build for release (single-file)
dotnet publish SignSummarizer.UI/SignSummarizer.UI.csproj -c Release -r win-x64
```

## Dependencies

- **.NET 10.0** - Target framework
- **Avalonia 11.3** - Cross-platform UI
- **ONNX Runtime 1.23** - Model inference
- **OpenCV 4.11** - Video processing and computer vision
- **Microsoft.ML** - Machine learning utilities
- **OpenCvSharp4** - C# OpenCV bindings

## Architecture Philosophy

### Signals First, LLM Second

Following the approach from DocSummarizer.Images, SignSummarizer prioritizes:
1. **Deterministic signals** (landmarks, embeddings, modifiers) - always reliable
2. **RAG retrieval** - vector similarity for sign matching
3. **Optional LLM usage** - only for novel sign labeling and complex queries

### Streaming Architecture

All services support async streaming for real-time processing:
- Frame-by-frame hand detection
- Incremental atom segmentation
- Online novelty detection for filmstrip extraction

### Inspectable and Correctable

Every step produces structured, queryable data:
- Frame-level timestamps for replay
- Confidence scores on all detections
- Keyframe evidence pointers
- Per-atom modifier attachments

## Performance Optimizations

1. **Filmstrip Deduplication** - Process only 6-12 key poses per sign
2. **Preallocated Tensors** - Reuse ONNX input buffers
3. **Async Channels** - Parallel detector consumption
4. **Lazy Evaluation** - Compute embeddings only when needed
5. **Vector Similarity** - Fast cosine similarity with precomputed embeddings

## Future Enhancements

- [ ] Temporal model (BiLSTM/Transformer) for sequence classification
- [ ] Sign-specific learned embeddings (MLP training)
- [ ] Per-signer adaptive normalization
- [ ] Real-time webcam input support
- [ ] Sign gloss classification using CTC
- [ ] Integration with pgvector for scalable storage

## References

Based on architecture described in:
- [SIGNAL-ARCHITECTURE.md](../Mostlylucid.DocSummarizer.Images/SIGNAL-ARCHITECTURE.md)
- [DocSummarizer.Images README](../Mostlylucid.DocSummarizer.Images/README.md)

## License

MIT License - See LICENSE file for details
