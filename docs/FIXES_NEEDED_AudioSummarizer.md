# AudioSummarizer Code Fixes Needed

**Based on article review vs actual implementation**

---

## P0 - Critical Code Issues

### 1. Diarization Confidence = 1.0 (Undermines Forensic Claim)

**File:** `src/AudioSummarizer.Core/Services/Voice/SpeakerDiarizationService.cs:89`

**Current code:**
```csharp
turns.Add(new SpeakerTurn
{
    SpeakerId = speakerId,
    StartSeconds = segment.StartSeconds,
    EndSeconds = segment.EndSeconds,
    Confidence = 1.0 // TODO: Calculate based on cluster distance
});
```

**Problem:**
- Article claims "forensic characterization" with deterministic signals
- Diarization is inherently uncertain (clustering threshold, embedding similarity)
- Hardcoded confidence = 1.0 contradicts forensic stance
- Article examples show `speaker.diarization_confidence = 1.0` which undermines credibility

**Fix needed:**
```csharp
// Calculate confidence from cluster similarity margin
var clusterSimilarity = CalculateClusterSimilarity(embedding, speakerClusters[speakerId]);
var confidenceMargin = CalculateConfidenceMargin(clusterSimilarity, SimilarityThreshold);

turns.Add(new SpeakerTurn
{
    SpeakerId = speakerId,
    StartSeconds = segment.StartSeconds,
    EndSeconds = segment.EndSeconds,
    Confidence = confidenceMargin // Derived from cluster distance
});

// Where:
// - confidenceMargin = 0.85-0.95 for segments well within cluster
// - confidenceMargin = 0.70-0.85 for segments near cluster boundary
// - confidenceMargin < 0.70 for ambiguous segments (near threshold)
```

**Implementation:**
```csharp
private double CalculateConfidenceMargin(double similarity, double threshold)
{
    // Map similarity to confidence score
    // High similarity (0.95) far from threshold (0.75) → high confidence (0.95)
    // Low similarity (0.76) near threshold (0.75) → low confidence (0.72)

    var margin = similarity - threshold;
    var maxMargin = 1.0 - threshold;

    if (margin <= 0) return 0.50; // Below threshold (shouldn't happen after clustering)

    // Linear mapping from threshold to max similarity
    var normalizedMargin = margin / maxMargin;

    // Confidence range: 0.70-0.95
    return 0.70 + (normalizedMargin * 0.25);
}
```

**Impact:**
- Article examples need updating to show realistic confidence (0.85-0.95)
- Signal ledger examples need updating
- Diarization wave tests need updating

---

### 2. SignalType.Embedding for Base64 WAV (Taxonomy Smell)

**File:** `src/AudioSummarizer.Core/Services/Analysis/Waves/SpeakerDiarizationWave.cs:239`

**Current code:**
```csharp
signals.Add(new Signal
{
    Name = $"speaker.sample.{speakerId.ToLowerInvariant()}",
    Value = base64Wav,
    Type = SignalType.Embedding, // Using Embedding type for binary data
    Source = Name
});
```

**Problem:**
- Binary audio data stored in signal payload (huge signal ledgers)
- `SignalType.Embedding` should be for vector embeddings, not audio clips
- Evidence storage exists for this exact purpose (`EvidenceTypes.SpeakerSample`)
- Scalability issue: 2-second WAV clip = ~30KB Base64 per speaker

**Fix needed:**

**Option A: Store in evidence, signal contains reference**
```csharp
// Store speaker sample as evidence artifact
var evidenceId = Guid.NewGuid();
await _evidenceRepository.SaveAsync(new EvidenceArtifact
{
    Id = evidenceId,
    DocumentId = documentId,
    Type = EvidenceTypes.SpeakerSample,
    Content = base64Wav,
    Metadata = new
    {
        SpeakerId = speakerId,
        StartSeconds = turn.StartSeconds,
        EndSeconds = sampleEnd,
        DurationSeconds = sampleEnd - turn.StartSeconds
    }
});

// Signal contains reference only
signals.Add(new Signal
{
    Name = $"speaker.sample.{speakerId.ToLowerInvariant()}",
    Value = evidenceId.ToString(), // Reference to evidence
    Type = SignalType.Reference,   // New type or use Metadata
    Source = Name,
    Metadata = new Dictionary<string, object>
    {
        ["evidence_type"] = "speaker_sample",
        ["speaker_id"] = speakerId,
        ["size_bytes"] = base64Wav.Length
    }
});
```

**Option B: Skip signal emission, store only in evidence**
```csharp
// Don't emit speaker samples as signals at all
// Store directly in evidence storage
// Query UI retrieves from evidence when displaying diarization results
```

**Recommendation:** Option A (keeps signal ledger complete, but references evidence)

**Impact:**
- Article needs update to clarify signal vs evidence storage
- Documentation needs to explain evidence retrieval pattern
- Tests need updating

---

## P1 - Code Improvements

### 3. Segment Extraction Pattern (Potentially Fragile)

**File:** `src/AudioSummarizer.Core/Services/Audio/AudioSegmentExtractor.cs:141`

**Current code:**
```csharp
using var reader = new AudioFileReader(audioPath);
// ... byte position calculations ...
reader.Position = startPosition;
using var trimmedReader = new WaveFileReader(new TrimmedStream(reader, segmentLength));
```

**Potential issues:**
- `AudioFileReader` outputs decoded PCM samples
- Wrapping in `WaveFileReader(TrimmedStream(...))` works but is conceptually odd
- Byte position calculations assume linear mapping (works for WAV, fragile for MP3/AAC)

**Current status:**
- Code DOES work for MP3/WAV/FLAC (tested)
- NAudio handles format conversion transparently
- TrimmedStream wrapper is valid pattern

**Recommendation:**
- Keep current implementation (it works)
- Add comment explaining the pattern
- OR refactor to use `OffsetSampleProvider` (more explicit, same result)

**Article fix:**
```csharp
private async Task ExtractSegmentToStreamAsync(...)
{
    using var reader = new AudioFileReader(audioPath);

    // Calculate byte positions in decoded PCM stream
    // AudioFileReader handles format conversion (MP3/FLAC → PCM)
    var startPosition = (long)(startSeconds * reader.WaveFormat.AverageBytesPerSecond);
    // ...

    // TrimmedStream creates a windowed view of the decoded PCM
    // WaveFileReader wraps it for format conversion pipeline
    using var trimmedReader = new WaveFileReader(new TrimmedStream(reader, segmentLength));
    // ...
}
```

**Impact:** Article annotation, no code change needed

---

### 4. VoiceprintId Cross-Platform Stability

**File:** `src/AudioSummarizer.Core/Services/Voice/VoiceEmbeddingService.cs:1045`

**Current code:**
```csharp
private string GenerateVoiceprintId(float[] embedding)
{
    using var sha256 = System.Security.Cryptography.SHA256.Create();
    var bytes = new byte[embedding.Length * sizeof(float)];
    Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
    var hash = sha256.ComputeHash(bytes);

    return "vprint:" + Convert.ToHexString(hash).Substring(0, 16).ToLower();
}
```

**Potential issues:**
- `Buffer.BlockCopy` float array to bytes depends on endianness
- Cross-platform determinism fragile if preprocessing changes
- Model version changes break ID stability

**Recommendation:**
Add comment and version tracking:
```csharp
private string GenerateVoiceprintId(float[] embedding)
{
    // Voiceprint IDs are stable for a given model + preprocessing version.
    // Cross-platform determinism depends on float serialization consistency.
    // Treat as versioned identifiers - regenerate if model changes.

    using var sha256 = System.Security.Cryptography.SHA256.Create();
    var bytes = new byte[embedding.Length * sizeof(float)];
    Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
    var hash = sha256.ComputeHash(bytes);

    // Include model version in ID generation for future-proofing
    return $"vprint:v1:{Convert.ToHexString(hash).Substring(0, 16).ToLower()}";
}
```

**Impact:** Breaking change if implemented (existing voiceprints invalidated). Defer to v2.

---

## P2 - Documentation Clarifications

### 5. VAD Threshold Fragility

**File:** `src/AudioSummarizer.Core/Services/Voice/SpeakerDiarizationService.cs`

**Current implementation:**
```csharp
bool isSpeech = rms > 0.02;  // Fixed threshold
```

**Article already has disclaimer:**
```csharp
// Speech detection threshold (simple baseline - fragile across gain levels)
// Production: use relative threshold (noise floor / percentile) or per-file calibration
```

**Recommendation:**
Implement adaptive threshold:
```csharp
// Calculate noise floor from first 2 seconds of audio
var noiseFloor = CalculateNoiseFloor(sampleProvider);
var threshold = noiseFloor * 2.5; // 2.5x noise floor

bool isSpeech = rms > threshold;
```

**Impact:** Code improvement, article already disclaims this

---

### 6. Content Classification Calibration

**File:** `src/AudioSummarizer.Core/Services/Analysis/Waves/ContentClassifierWave.cs`

**Current thresholds:**
```csharp
if (zcr > 0.15 && spectralFlux < 0.3)  // Speech
if (zcr < 0.10 && spectralFlux > 0.5)  // Music
```

**Article disclaimer:** "rough heuristic classification... Used only for wave routing decisions"

**Recommendation:**
Add calibration note in code:
```csharp
// Heuristic thresholds - calibrate on your corpus for best routing accuracy
// ZCR and spectral flux are genre/context dependent
// These defaults work for typical podcast/interview content
const double SpeechZcrThreshold = 0.15;
const double SpeechFluxThreshold = 0.3;
```

**Impact:** Documentation improvement, article already disclaims

---

### 7. Two-Stage Reduction Temp File

**File:** Article example (not in actual code yet - CLI integration pending)

**Article code:**
```csharp
var tempTranscript = Path.GetTempFileName() + ".md";
await File.WriteAllTextAsync(tempTranscript, transcriptText, ct);
// ... process ...
File.Delete(tempTranscript);
```

**Privacy issue:** Temp files linger, backups, AV scans

**Recommendation:**
Add note in article:
```csharp
// NOTE: Temp file approach is demo code for CLI integration example.
// Production should use in-memory pipeline API to avoid privacy leaks.
var tempTranscript = Path.GetTempFileName() + ".md";
```

**Impact:** Article annotation only

---

## Summary

**Must fix before article publish:**
1. ✅ Article edits for consistency (signature vs signal ledger, SPEAKER_00 query, etc.)
2. ❌ Diarization confidence = 1.0 → realistic values (CODE FIX REQUIRED)
3. ❌ SignalType.Embedding for Base64 WAV → evidence storage pattern (CODE FIX REQUIRED)

**Should fix for v2.0:**
4. VoiceprintId versioning
5. VAD adaptive threshold
6. Content classification calibration notes

**Article-only fixes (no code change):**
7. Segment extraction code comments
8. Temp file privacy note
9. Embedding invertibility phrasing consistency
10. CF + Reduced RAG positioning clarity

---

## Implementation Priority

### Phase 1: Critical for Article Accuracy (this week)
- [ ] Fix diarization confidence calculation (SpeakerDiarizationService.cs)
- [ ] Update article examples with realistic confidence values
- [ ] Fix SignalType.Embedding → evidence storage pattern
- [ ] Update article to reflect evidence storage pattern
- [ ] Apply all article editorial fixes

### Phase 2: Code Quality (next sprint)
- [ ] VoiceprintId versioning scheme
- [ ] VAD adaptive threshold implementation
- [ ] Content classification calibration documentation

### Phase 3: Future Enhancements
- [ ] ConfidenceBooster integration
- [ ] Cross-platform voiceprint stability testing
- [ ] Model version tracking in signal metadata
