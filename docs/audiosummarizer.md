# AudioSummarizer.Core - Audio Characterization Library

## Overview

**AudioSummarizer.Core** is a .NET library for forensic audio characterization that follows the "Constrained Fuzziness" design philosophy. It characterizes audio structurally and acoustically **without** identifying cultural content.

### What AudioSummarizer Does

✅ **Characterizes audio** - Extracts deterministic and probabilistic signals
✅ **Transcribes speech** - Converts audio to searchable text
✅ **Identifies speakers** - Anonymous speaker similarity (no naming)
✅ **Detects content type** - Speech vs music vs conversation
✅ **Enables deduplication** - Perceptual fingerprinting

### What AudioSummarizer Does NOT Do

❌ **Auto-identify songs** - No automatic song naming
❌ **Download lyrics** - No external cultural knowledge scraping
❌ **Infer speaker names** - Only anonymous similarity
❌ **Make cultural assertions** - Forensic characterization only

## Design Philosophy: Constrained Fuzziness

AudioSummarizer implements a layered architecture where:

| Layer | Type | Examples |
|-------|------|----------|
| **Deterministic Substrate** | Always correct | SHA-256 hash, duration, RMS loudness, spectral features |
| **Probabilistic Proposers** | ML-based, confidence-scored | Transcription, speaker detection, content classification |
| **Constrainer** | Decides what persists | Evidence repository, signal validation |

**Key Principle:** "AudioSummarizer characterizes audio. It does not identify culture."

## Architecture

### Technology Stack

| Component | Library | Purpose |
|-----------|---------|---------|
| **Audio I/O** | NAudio 2.2.1 | File loading, format conversion |
| **Spectral Analysis** | FftSharp 2.1.0 | FFT, spectral features, MFCC |
| **Transcription** | Whisper.NET 1.9.0 | Local speech-to-text (offline) |
| **Transcription (Alt)** | Ollama HTTP | Remote transcription backend |
| **Voice Embeddings** | ONNX Runtime 1.23.2 | ECAPA-TDNN speaker similarity |
| **ML Inference** | Microsoft.ML.OnnxRuntime | ONNX model execution |

### Wave-Based Pipeline

AudioSummarizer uses a **wave-based architecture** (similar to ImageSummarizer.Core) where each wave extracts specific signals:

```
AudioWaveOrchestrator
├── IdentityWave (Priority 100) - SHA-256, file metadata, duration
├── FingerprintWave (90) - Perceptual fingerprinting for deduplication
├── AcousticProfileWave (80) - RMS, spectral features, SNR, dynamic range
├── ContentClassifierWave (70) - Speech vs music vs mixed
├── TranscriptionWave (60) - Speech-to-text with timestamps
├── SpeakerDiarizationWave (50) - Speaker turn detection
└── VoiceEmbeddingWave (30) - Anonymous speaker similarity
```

**Waves run in priority order** (highest first), each adding signals to the AudioProfile.

## Signal Schema

### Deterministic Signals (Hard Identity)

```csharp
// Cryptographic identity
audio.hash.sha256 = "abc123..."
audio.hash.pcm_sha256 = "def456..."  // Hash of decoded audio

// File metadata
audio.duration_seconds = 182.4
audio.format = "mp3"
audio.bitrate = 320000
audio.sample_rate = 44100
audio.channels = 2

// Acoustic profile
audio.rms_db = -18.2
audio.noise_floor_db = -48
audio.dynamic_range_db = 22
audio.clipping_ratio = 0.003
audio.silence_ratio = 0.12

// Spectral features
audio.spectral_centroid_hz = 2400
audio.spectral_rolloff_hz = 8000
audio.spectral_bandwidth_hz = 3200
```

### Perceptual Fingerprint (Soft Identity)

```csharp
audio.fingerprint.type = "spectral_peaks"  // or "chromaprint"
audio.fingerprint.hash = "AQADtE..."
audio.fingerprint.similarity = 0.94  // vs reference audio
```

### Probabilistic Signals (ML-Based)

```csharp
// Content classification
audio.type = "conversation"  // speech|music|mixed|silence
audio.music_bed = true
audio.speakers.likely = "multiple"  // single|multiple|uncertain
audio.speech_style = "conversational"  // read|conversational|broadcast

// Transcription
transcription.text = "..."
transcription.confidence = 0.87
transcription.backend = "whisper"  // whisper|ollama
transcription.segments = [{start: 0.0, end: 2.3, text: "Hello world", confidence: 0.92}]

// Speaker diarization
speaker.count = 2
speaker.turns = [
  {speaker_id: "SPEAKER_00", start: 0.0, end: 5.2},
  {speaker_id: "SPEAKER_01", start: 5.2, end: 12.8}
]

// Voice embedding (anonymous)
speaker.voiceprint_id = "vprint:xyz"  // Hash of embedding
speaker.similarity = 0.91  // vs other audio files
```

## ONNX Models

AudioSummarizer uses ONNX Runtime for ML inference:

### 1. Whisper (Transcription)

**Model:** `whisper-base.en.bin` (142 MB)
**Source:** https://huggingface.co/ggerganov/whisper.cpp
**Purpose:** English speech-to-text transcription
**Runtimes:** CPU, CUDA 12/13, CoreML, OpenVINO, Vulkan

**Alternatives:**
- `whisper-tiny.en.bin` (75 MB) - Faster, less accurate
- `whisper-small.en.bin` (466 MB) - More accurate, slower

**Auto-download:** Model downloads automatically on first use

### 2. ECAPA-TDNN (Voice Embeddings)

**Model:** `voxceleb_ECAPA512_LM.onnx` (24.9 MB)
**Source:** https://huggingface.co/Wespeaker/wespeaker-ecapa-tdnn512-LM
**Purpose:** 512-dimensional speaker embeddings
**Performance:** 1.71% EER, 69.43ms inference time

**Features:**
- Generates anonymous speaker IDs (no PII)
- Enables "find similar speakers" queries
- Cosine similarity for speaker matching

### 3. PyAnnote (Speaker Diarization - Optional)

**Challenge:** PyAnnote 3.1+ no longer uses ONNX (pure PyTorch)
**Options:**
1. Export PyAnnote 3.0 models to ONNX (research required)
2. HTTP wrapper to Python PyAnnote service
3. Simple heuristic diarization (spectral clustering)

**Performance (PyAnnote 3.1):**
- ~10% DER (Diarization Error Rate)
- 2.5% real-time factor on GPU

## Implementation Phases

### Phase 1: Core Infrastructure & Deterministic Substrate ✅

**Deliverable:** Fully offline audio characterization with deterministic signals

- Create AudioSummarizer.Core project (.NET 10.0)
- Implement IdentityWave (SHA-256, file metadata)
- Implement AcousticProfileWave (RMS, spectral analysis via FftSharp)
- Implement ContentClassifierWave (speech vs music detection)
- AudioDocumentHandler (converts audio → markdown)
- Service registration
- CLI integration

**Testing:**
```bash
dotnet run --project src/LucidRAG.Cli/LucidRAG.Cli.csproj -- process test.mp3 --type audio --verbose
```

### Phase 2: Fingerprinting (Perceptual Identity)

**Deliverable:** Deduplication and similarity detection

- IFingerprintService with dual providers:
  - PureNetFingerprintService (default, cross-platform)
  - ChromaprintFingerprintService (optional, P/Invoke)
- FingerprintWave
- Similarity comparison (Hamming distance)

**Testing:**
- Upload identical file twice → detect duplicate
- Upload transcoded version → high similarity
- Upload pitch-shifted version → moderate similarity

### Phase 3: Transcription Pipeline (Dual Backends)

**Deliverable:** Transcribed audio searchable via RAG

- ITranscriptionService interface
- WhisperTranscriptionService (Whisper.NET local)
- OllamaTranscriptionService (HTTP remote)
- TranscriptionWave with timestamped segments
- Store as EvidenceTypes.Transcript and TranscriptSegments
- Convert to markdown for RAG ingestion

**Configuration:**
```json
{
  "Audio": {
    "TranscriptionBackend": "Whisper",  // "Whisper" | "Ollama" | "Auto"
    "Whisper": {
      "ModelPath": "./models/whisper-base.en.bin",
      "ModelSize": "base",
      "Language": "en"
    }
  }
}
```

### Phase 4: Voice Embeddings (Speaker Similarity)

**Deliverable:** Anonymous speaker identification

- Download ECAPA-TDNN ONNX model
- VoiceEmbeddingWave extracts 512-dim embeddings
- Generate anonymous voiceprint IDs
- Speaker similarity service (cosine distance)
- Store as EvidenceTypes.EmbeddingVector

**Privacy:** NEVER infer speaker names, only similarity

### Phase 5: Speaker Diarization (Speaker Turn Detection)

**Deliverable:** Transcripts with speaker turn boundaries

- Research PyAnnote 3.0 ONNX availability
- SpeakerDiarizationWave
- Integrate with TranscriptionWave
- Store as EvidenceTypes.SpeakerDiarization

**Output Example:**
```
[SPEAKER_00 00:00-00:05]: Hello, how are you?
[SPEAKER_01 00:05-00:12]: I'm doing great, thanks for asking!
[SPEAKER_00 00:12-00:18]: That's wonderful to hear.
```

## Integration with LucidRAG

### CLI Integration

```bash
# Process single audio file
dotnet run --project src/LucidRAG.Cli/LucidRAG.Cli.csproj -- process podcast.mp3 --type audio

# Process directory with glob pattern
dotnet run --project src/LucidRAG.Cli/LucidRAG.Cli.csproj -- process ./interviews/*.mp3 --type auto

# Extract entities from transcript
dotnet run --project src/LucidRAG.Cli/LucidRAG.Cli.csproj -- process meeting.wav --extract-entities
```

### Web UI Integration

1. Upload audio file (.mp3, .wav, .m4a, .flac, .ogg)
2. Background processing via `DocumentQueueProcessor`
3. Signals stored as `EvidenceTypes.SignalDump` (JSON)
4. Transcript stored as `EvidenceTypes.Transcript`
5. Transcript indexed in vector store for RAG search
6. Search results include timestamp citations

**Example Search:**
```
User: "What did they say about the budget?"
RAG: Found in meeting.mp3 at 05:23-05:45:
     "We need to allocate $50,000 for the new project..."
```

### Evidence Artifacts

AudioSummarizer stores multiple evidence types:

| Artifact Type | Content | MIME Type | Purpose |
|---------------|---------|-----------|---------|
| `Transcript` | Full transcript text | text/plain | RAG ingestion |
| `TranscriptSegments` | JSON with timestamps | application/json | Time-based citation |
| `SpeakerDiarization` | Speaker turns JSON | application/json | Multi-speaker transcripts |
| `SignalDump` | All audio signals | application/json | Forensic analysis |
| `EmbeddingVector` | Speaker embeddings | application/octet-stream | Similarity queries |

## Configuration Reference

### appsettings.json (Web)

```json
{
  "Audio": {
    "TranscriptionBackend": "Whisper",
    "Whisper": {
      "ModelPath": "./models/whisper-base.en.bin",
      "ModelSize": "base",
      "Language": "en",
      "UseGpu": false
    },
    "Ollama": {
      "BaseUrl": "http://localhost:11434",
      "Model": "whisper"
    },
    "SupportedFormats": [".mp3", ".wav", ".m4a", ".flac", ".ogg", ".wma", ".aac"],
    "MaxFileSizeMB": 500,
    "EnableSpeakerDiarization": false,
    "EnableVoiceEmbeddings": true,
    "Pipeline": {
      "EnableFingerprinting": true,
      "EnableAcousticProfiling": true,
      "EnableContentClassification": true
    },
    "FingerprintProvider": "PureNet"  // "PureNet" | "Chromaprint"
  }
}
```

### CLI Config (Programmatic)

```csharp
services.AddAudioSummarizer(opt =>
{
    opt.TranscriptionBackend = TranscriptionBackend.Whisper;
    opt.Whisper.ModelPath = Path.Combine(dataDir, "models", "whisper-base.en.bin");
    opt.EnableVoiceEmbeddings = true;
    opt.EnableSpeakerDiarization = false;
    opt.FingerprintProvider = FingerprintProvider.PureNet;
    opt.SupportedFormats = new[] { ".mp3", ".wav", ".m4a", ".flac" };
});
```

## Performance Considerations

### Transcription Speed

| Model | Size | Speed (RTF) | Accuracy |
|-------|------|-------------|----------|
| whisper-tiny.en | 75 MB | ~0.1x | Good |
| whisper-base.en | 142 MB | ~0.3x | Better |
| whisper-small.en | 466 MB | ~0.6x | Best |

**RTF (Real-Time Factor):** 0.3x means a 1-minute audio file takes ~18 seconds to transcribe

### Voice Embedding Speed

- **ECAPA-TDNN:** ~69ms per audio file (CPU)
- **Batch processing:** Can process 10-15 files per second

### Storage Requirements

**Per Audio File:**
- Original audio: ~1-5 MB (MP3, 3-minute file)
- Transcript: ~1-10 KB
- Signals JSON: ~2-5 KB
- Fingerprint: ~500 bytes
- Voice embedding: 2 KB (512 floats)

**Total overhead:** ~5-20 KB per audio file (excluding original)

## Testing Strategy

### Unit Tests

- `IdentityWave` determinism (same file = same hash)
- `AcousticProfileWave` accuracy (RMS, spectral features)
- `FingerprintWave` similarity (identical vs transcoded files)
- `TranscriptionWave` confidence scores
- `VoiceEmbeddingWave` consistency

### Integration Tests

1. **End-to-end pipeline:**
   ```bash
   Upload test.mp3 → Process → Verify signals → Search transcript → Find results
   ```

2. **Fingerprint deduplication:**
   ```bash
   Upload file1.mp3 → Upload file1.mp3 (duplicate) → Verify dedup
   ```

3. **Speaker similarity:**
   ```bash
   Upload speaker1_sample1.wav → Upload speaker1_sample2.wav → Verify high similarity
   ```

4. **Multi-speaker diarization:**
   ```bash
   Upload interview.mp3 → Verify speaker turns detected → Check transcript labels
   ```

### Test Data

- **Speech samples:** Single speaker, clear audio
- **Conversation samples:** 2-3 speakers, overlapping speech
- **Music samples:** Songs, instrumental, mixed
- **Low-quality samples:** Noisy, clipped, low bitrate

## Roadmap

### MVP (Phases 1-3)

- ✅ Deterministic audio characterization
- ✅ Perceptual fingerprinting
- ✅ Dual transcription backends (Whisper + Ollama)
- ✅ CLI and Web UI integration
- ✅ RAG search with timestamp citations

### Extended Features (Phases 4-5)

- ✅ Voice embeddings (ECAPA-TDNN ONNX)
- ✅ Speaker diarization (PyAnnote ONNX or HTTP)
- ⏳ Sentiment/emotion detection
- ⏳ Music genre/instrument detection
- ⏳ Real-time streaming analysis
- ⏳ Video audio extraction

## References

### Research Papers & Models

- [Whisper.NET GitHub](https://github.com/sandrohanea/whisper.net) - C# wrapper for Whisper.cpp
- [ECAPA-TDNN Model](https://huggingface.co/Wespeaker/wespeaker-ecapa-tdnn512-LM) - Speaker embeddings
- [PyAnnote Audio](https://github.com/pyannote/pyannote-audio) - Speaker diarization toolkit
- [FftSharp](https://github.com/swharden/FftSharp) - .NET FFT library
- [NAudio](https://github.com/naudio/NAudio) - Audio I/O for .NET

### Related Documentation

- [ImageSummarizer.Core](./ADVANCED-OCR-PIPELINE.md) - Similar wave-based architecture
- [LucidRAG Architecture](../README.md) - Main system overview

### External Links

- [ONNX Runtime C# API](https://onnxruntime.ai/docs/get-started/with-csharp.html)
- [HuggingFace Audio Models](https://huggingface.co/models?pipeline_tag=audio-classification)
- [Chromaprint Fingerprinting](https://acoustid.org/chromaprint)

---

## Quick Start

### 1. Install AudioSummarizer

```bash
# In LucidRAG web app
dotnet add src/LucidRAG/LucidRAG.csproj reference src/AudioSummarizer.Core/AudioSummarizer.Core.csproj

# Or via NuGet (when published)
dotnet add package AudioSummarizer.Core
```

### 2. Register Services

```csharp
// Program.cs
builder.Services.AddAudioSummarizer(
    builder.Configuration.GetSection("Audio"));
```

### 3. Process Audio Files

```bash
# CLI
dotnet run --project src/LucidRAG.Cli/LucidRAG.Cli.csproj -- process podcast.mp3

# Or via Web UI: Upload → Process → Search
```

### 4. Query Transcripts

```
Search: "what did they say about the project timeline?"
Result: Found at 12:34 in meeting.mp3: "The project should be completed by Q3..."
```

---

**AudioSummarizer.Core** - Forensic audio characterization without cultural identification.
