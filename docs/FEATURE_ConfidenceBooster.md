# ConfidenceBooster Pattern - Feature Plan

**Pattern:** Background LLM refinement of low-confidence signals
**Runtime:** Coordinator background learning pipeline (not real-time ingestion)
**Purpose:** Update small aspects of signatures with uncertainty using targeted LLM queries
**Status:** Design phase
**Target Release:** v2.x (post-AudioSummarizer)

---

## Executive Summary

Implement a **universal background confidence boosting pattern** that:
1. Runs in coordinator background jobs (not during user-facing ingestion)
2. Detects low-confidence signals from deterministic/probabilistic waves
3. Extracts bounded artifacts (image crops, audio segments, text windows, data samples)
4. Queries LLMs with targeted questions about uncertain regions
5. Updates signal ledgers with enhanced signals and provenance metadata

**Key insight:** Don't block ingestion with LLM queries. Ingest fast with all available Start signals (some low-confidence), then boost confidence in the background over time.

---

## The Two-Path Architecture

```
FAST PATH (Production Ingestion):
  User uploads file → All waves execute → Signal ledger saved → User can query immediately
  Some signals may have confidence < 0.75 (uncertain transcription, ambiguous OCR, etc.)
  Total time: Same as current (no blocking)

SLOW PATH (Background Coordinator):
  Background job scans for files with low-confidence signals
  → Queue for ConfidenceBoosterWave processing
  → Extract bounded artifacts (only uncertain regions)
  → Query LLM with targeted prompts
  → Update signal ledger with boosted signals
  → Next user query uses improved signals
  Total time: Minutes to hours (user not waiting)
```

**User experience:**
- Upload → Instant results (fast path, some signals uncertain)
- Wait 10 minutes → Re-query → Better results (slow path boosted confidence)
- Quality improves over time without blocking ingestion

---

## Architecture Position

```
LucidRAG.Core
  └── Services/Background/
      ├── DocumentProcessingQueue.cs (existing)
      ├── EntityExtractionQueue.cs (existing)
      └── ConfidenceBoostingService.cs (NEW)
          ├── Scans for documents with signals < threshold
          ├── Queues boost jobs
          └── Updates signal ledgers

Mostlylucid.Summarizer.Core (shared library)
  └── Waves/
      └── ConfidenceBoosterWave.cs (generic, Priority 10)

Domain-specific implementations:
  - AudioSummarizer.Core/Boosters/TranscriptionBooster.cs
  - AudioSummarizer.Core/Boosters/SpeakerDiarizationBooster.cs
  - ImageSummarizer.Core/Boosters/OCRBooster.cs
  - ImageSummarizer.Core/Boosters/SceneComplexityBooster.cs
  - DocSummarizer.Core/Boosters/EntityDisambiguationBooster.cs
  - DataSummarizer.Core/Boosters/TypeInferenceBooster.cs
```

---

## Core Interface

```csharp
namespace Mostlylucid.Summarizer.Core.Interfaces;

/// <summary>
/// Boosts confidence of uncertain signals using targeted LLM queries
/// Runs in background coordinator pipeline, not real-time ingestion
/// </summary>
public interface IConfidenceBooster<TArtifact>
{
    /// <summary>
    /// Can this booster handle this signal type?
    /// </summary>
    bool CanBoost(Signal signal);

    /// <summary>
    /// Extract bounded artifact for LLM analysis (audio clip, image crop, etc.)
    /// </summary>
    Task<TArtifact> ExtractArtifactAsync(
        string sourcePath,
        Signal signal,
        AnalysisContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Query LLM to boost confidence with targeted question
    /// </summary>
    Task<Signal> BoostAsync(
        TArtifact artifact,
        Signal originalSignal,
        AnalysisContext context,
        CancellationToken ct = default);
}
```

---

## Domain-Specific Artifacts

```csharp
namespace Mostlylucid.Summarizer.Core.Models;

/// <summary>
/// Audio segment with padding for transcription correction
/// </summary>
public class AudioSegmentArtifact
{
    public required string TempFilePath { get; init; }
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
    public string? OriginalText { get; init; }
    public double OriginalConfidence { get; init; }
    public string? ContextBefore { get; init; }
    public string? ContextAfter { get; init; }
}

/// <summary>
/// Image crop with bounding box for OCR correction
/// </summary>
public class ImageCropArtifact
{
    public required Image Image { get; init; }
    public BoundingBox BoundingBox { get; init; }
    public string? OriginalText { get; init; }
    public double OriginalConfidence { get; init; }
}

/// <summary>
/// Text context window for entity disambiguation
/// </summary>
public class TextWindowArtifact
{
    public required string Text { get; init; }
    public string EntityText { get; init; }
    public string? EntityType { get; init; }
    public int Position { get; init; }
    public double OriginalConfidence { get; init; }
}

/// <summary>
/// Data sample for type inference clarification
/// </summary>
public class DataSampleArtifact
{
    public required string ColumnName { get; init; }
    public required List<object?> SampleValues { get; init; }
    public string? InferredType { get; init; }
    public double OriginalConfidence { get; init; }
    public List<object>? ConstraintViolations { get; init; }
}
```

---

## Example: Audio Transcription Booster

```csharp
namespace AudioSummarizer.Core.Boosters;

/// <summary>
/// Boosts low-confidence transcription segments using LLM audio analysis
/// Extracts audio clips with surrounding context for targeted correction
/// </summary>
public class TranscriptionBooster : IConfidenceBooster<AudioSegmentArtifact>
{
    private readonly AudioSegmentExtractor _extractor;
    private readonly ILLMService _llm;
    private readonly ILogger _logger;

    public bool CanBoost(Signal signal)
    {
        return signal.Name.StartsWith("transcription.segment");
    }

    public async Task<AudioSegmentArtifact> ExtractArtifactAsync(
        string audioPath,
        Signal signal,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var segment = signal.Metadata["segment"] as TranscriptSegment;

        // Extract with 1 second padding for context
        var tempPath = Path.GetTempFileName() + ".wav";
        await _extractor.ExtractSegmentToFileAsync(
            audioPath,
            Math.Max(0, segment.StartSeconds - 1.0),
            segment.EndSeconds + 1.0,
            tempPath,
            maxDurationSeconds: 10.0,
            ct: ct);

        return new AudioSegmentArtifact
        {
            TempFilePath = tempPath,
            StartSeconds = segment.StartSeconds,
            EndSeconds = segment.EndSeconds,
            OriginalText = signal.Value as string,
            OriginalConfidence = signal.Confidence,
            ContextBefore = GetContext(context, segment.Index - 2, segment.Index - 1),
            ContextAfter = GetContext(context, segment.Index + 1, segment.Index + 2)
        };
    }

    public async Task<Signal> BoostAsync(
        AudioSegmentArtifact artifact,
        Signal originalSignal,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var prompt = $@"Transcription with low confidence ({artifact.OriginalConfidence:F2}):

Context before: ""{artifact.ContextBefore}""
Unclear segment: ""{artifact.OriginalText}""
Context after: ""{artifact.ContextAfter}""

Listen to the audio and provide ONLY the corrected transcription text.
If unclear, respond with the original text.";

        try
        {
            var corrected = await _llm.AnalyzeAudioAsync(artifact.TempFilePath, prompt, ct);

            return new Signal
            {
                Name = originalSignal.Name + "_boosted",
                Value = corrected.Trim(),
                Type = SignalType.Text,
                Confidence = 0.85,  // LLM-boosted confidence
                Source = "TranscriptionBooster",
                Metadata = new Dictionary<string, object>
                {
                    ["original_text"] = artifact.OriginalText ?? "",
                    ["original_confidence"] = artifact.OriginalConfidence,
                    ["boost_method"] = "llm_audio_analysis",
                    ["boosted_at"] = DateTime.UtcNow,
                    ["is_corrected"] = !corrected.Trim().Equals(artifact.OriginalText, StringComparison.OrdinalIgnoreCase)
                }
            };
        }
        finally
        {
            File.Delete(artifact.TempFilePath);
        }
    }

    private string GetContext(AnalysisContext ctx, int start, int end)
    {
        var parts = new List<string>();
        for (int i = start; i <= end; i++)
        {
            var text = ctx.GetValue<string>($"transcription.segment_{i}.text");
            if (!string.IsNullOrEmpty(text)) parts.Add(text);
        }
        return string.Join(" ", parts);
    }
}
```

---

## ConfidenceBoosterWave (Generic)

```csharp
namespace Mostlylucid.Summarizer.Core.Waves;

/// <summary>
/// Generic confidence booster wave (Priority 10 - runs last)
/// Used in background coordinator pipeline to refine signatures
/// </summary>
public class ConfidenceBoosterWave : IWave
{
    public string Name => "ConfidenceBoosterWave";
    public int Priority => 10;  // Runs after all signal extraction waves

    private readonly IEnumerable<IConfidenceBooster<object>> _boosters;
    private readonly ConfidenceBoosterConfig _config;
    private readonly ILogger _logger;

    public bool ShouldRun(string sourcePath, AnalysisContext context)
    {
        if (!_config.EnableBackgroundBoosting) return false;

        return context.GetAllSignals()
            .Any(s => s.Confidence < _config.ConfidenceThreshold);
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string sourcePath,
        AnalysisContext context,
        CancellationToken ct)
    {
        var boostedSignals = new List<Signal>();
        var boostCount = 0;

        // Find uncertain signals (worst first)
        var uncertainSignals = context.GetAllSignals()
            .Where(s => s.Confidence < _config.ConfidenceThreshold)
            .OrderBy(s => s.Confidence)
            .Take(_config.MaxBoostsPerFile)
            .ToList();

        _logger.LogInformation(
            "[ConfidenceBooster] Found {Count} signals to boost in {File}",
            uncertainSignals.Count,
            Path.GetFileName(sourcePath));

        foreach (var signal in uncertainSignals)
        {
            var booster = _boosters.FirstOrDefault(b => b.CanBoost(signal));
            if (booster == null) continue;

            try
            {
                _logger.LogDebug(
                    "[ConfidenceBooster] Boosting {Signal} ({Conf:F2})",
                    signal.Name,
                    signal.Confidence);

                var artifact = await booster.ExtractArtifactAsync(sourcePath, signal, context, ct);
                var boosted = await booster.BoostAsync(artifact, signal, context, ct);

                boostedSignals.Add(boosted);
                boostCount++;

                _logger.LogInformation(
                    "[ConfidenceBooster] Boosted {Signal}: {OldConf:F2} → {NewConf:F2}",
                    signal.Name,
                    signal.Confidence,
                    boosted.Confidence);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ConfidenceBooster] Failed to boost {Signal}", signal.Name);
            }
        }

        return boostedSignals;
    }
}
```

---

## Coordinator Background Service

```csharp
namespace LucidRAG.Core.Services.Background;

/// <summary>
/// Background service that scans for files with low-confidence signals
/// and queues them for confidence boosting
/// </summary>
public class ConfidenceBoostingService : BackgroundService
{
    private readonly RagDocumentsDbContext _db;
    private readonly DocumentProcessingQueue _queue;
    private readonly ConfidenceBoosterConfig _config;
    private readonly ILogger _logger;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("[ConfidenceBooster] Background service started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Find documents with low-confidence signals
                var candidates = await FindBoostCandidatesAsync(ct);

                _logger.LogInformation(
                    "[ConfidenceBooster] Found {Count} documents with low-confidence signals",
                    candidates.Count);

                foreach (var doc in candidates)
                {
                    // Queue for background processing
                    await _queue.EnqueueAsync(new ProcessingJob
                    {
                        DocumentId = doc.Id,
                        JobType = ProcessingJobType.ConfidenceBoost,
                        Priority = JobPriority.Low,
                        ScheduledAt = DateTime.UtcNow
                    }, ct);
                }

                // Sleep between scans
                await Task.Delay(_config.ScanInterval, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ConfidenceBooster] Error in background scan");
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
            }
        }
    }

    private async Task<List<DocumentEntity>> FindBoostCandidatesAsync(CancellationToken ct)
    {
        var candidates = await _db.Documents
            .Where(d => d.Signals != null)
            .Where(d => d.LastBoostAttempt == null
                     || d.LastBoostAttempt < DateTime.UtcNow.AddDays(-7))
            .Take(_config.MaxDocumentsPerScan)
            .ToListAsync(ct);

        return candidates
            .Where(d => HasLowConfidenceSignals(d, _config.ConfidenceThreshold))
            .ToList();
    }

    private bool HasLowConfidenceSignals(DocumentEntity doc, double threshold)
    {
        var signals = JsonSerializer.Deserialize<List<Signal>>(doc.Signals);
        return signals?.Any(s => s.Confidence < threshold) ?? false;
    }
}
```

---

## Configuration

```json
{
  "ConfidenceBooster": {
    "EnableBackgroundBoosting": false,
    "ConfidenceThreshold": 0.75,
    "MaxBoostsPerFile": 5,
    "MaxDocumentsPerScan": 100,
    "ScanInterval": "01:00:00",
    "Boosters": {
      "Audio": {
        "Transcription": true,
        "SpeakerDiarization": false,
        "ContentClassification": false
      },
      "Image": {
        "OCR": true,
        "SceneComplexity": false,
        "Layout": false
      },
      "Document": {
        "EntityDisambiguation": true,
        "ReferenceResolution": false
      },
      "Data": {
        "TypeInference": true,
        "ConstraintValidation": false
      }
    }
  }
}
```

---

## Database Schema Updates

```sql
-- Add boost tracking to DocumentEntity
ALTER TABLE documents
ADD COLUMN last_boost_attempt TIMESTAMP NULL,
ADD COLUMN boosted_signal_count INT NULL,
ADD COLUMN average_confidence_improvement DOUBLE PRECISION NULL;

-- Index for background job queries
CREATE INDEX idx_documents_boost_candidates
ON documents (last_boost_attempt)
WHERE signals IS NOT NULL;
```

```csharp
// Signal model updates
public class Signal
{
    // Existing fields...

    public bool IsBoosted { get; set; }
    public double? OriginalConfidence { get; set; }
    public DateTime? BoostedAt { get; set; }
    public string? BoostMethod { get; set; }
}
```

---

## Implementation Phases

### Phase 1: Core Infrastructure (1 week)
- [ ] Create `Mostlylucid.Summarizer.Core` shared interfaces
- [ ] Implement `IConfidenceBooster<TArtifact>` interface
- [ ] Implement `ConfidenceBoosterWave` generic orchestrator
- [ ] Add configuration models
- [ ] Service registration extensions
- [ ] Unit tests

### Phase 2: Coordinator Integration (1 week)
- [ ] Implement `ConfidenceBoostingService` background job
- [ ] Add database migration for boost tracking
- [ ] Implement job queue support for boost jobs
- [ ] Add signal ledger update logic
- [ ] Integration tests

### Phase 3: Audio Boosters (1 week)
- [ ] Implement `TranscriptionBooster`
- [ ] Implement `SpeakerDiarizationBooster` (optional)
- [ ] Add LLM audio service integration
- [ ] Test with real podcast samples
- [ ] Document configuration

### Phase 4: Image Boosters (1 week)
- [ ] Implement `OCRBooster`
- [ ] Implement `SceneComplexityBooster` (optional)
- [ ] Integrate with VisionLLM service
- [ ] Test with low-quality scans

### Phase 5: Doc + Data Boosters (1 week)
- [ ] Implement `EntityDisambiguationBooster`
- [ ] Implement `TypeInferenceBooster`
- [ ] Integration tests
- [ ] Documentation

### Phase 6: Monitoring & Metrics (3 days)
- [ ] Add Prometheus metrics
- [ ] Dashboard for boost activity
- [ ] Cost tracking
- [ ] Performance monitoring

---

## Example Scenarios

### Scenario 1: Podcast with Accented Speech

**Fast Path (Ingestion):**
```
User uploads: podcast.mp3 (30 minutes)
→ All waves execute: Identity, Acoustic, Transcription, Diarization, Voice
→ Whisper transcription: 95% segments high confidence, 5% segments 0.55-0.68 confidence
→ Signal ledger saved with 8 low-confidence segments
→ User can query immediately
→ Total time: 45 seconds
```

**Slow Path (Background, 10 minutes later):**
```
ConfidenceBoostingService scans database
→ Finds podcast.mp3 with 8 signals < 0.75
→ Queues boost job
→ TranscriptionBooster extracts 8 audio clips (3-5 seconds each)
→ Queries LLM with surrounding context
→ Updates signal ledger:
   - Segment 12: "quantum mumble" → "quantum computing" (0.62 → 0.85)
   - Segment 45: "unclear blockchain" → "blockchain infrastructure" (0.58 → 0.83)
   - ... (6 more corrections)
→ Next user query uses improved signals
→ Total cost: $0.04 (8 clips × $0.005)
```

### Scenario 2: Scanned Document with Handwriting

**Fast Path (Ingestion):**
```
User uploads: scanned_receipt.png
→ OCR waves execute: Tesseract, PaddleOCR
→ Tesseract: 12 text regions, 3 with confidence < 0.70 (handwritten notes)
→ Signal ledger saved with low-confidence OCR results
→ Total time: 2 seconds
```

**Slow Path (Background):**
```
OCRBooster extracts 3 bounding box crops
→ Queries VisionLLM:
   - Region [120,450,300,80]: "handwritt3n n0te" → "handwritten note" (0.62 → 0.90)
   - Region [450,200,200,60]: "unclear dat3" → "March 3rd" (0.58 → 0.88)
   - Region [200,600,150,40]: "t0tal: $45.2O" → "total: $45.20" (0.65 → 0.92)
→ Total cost: $0.03 (3 crops)
```

---

## Cost Analysis

### Per-File Costs (Background Mode)

| Domain | Typical File | Uncertain Signals | Cost per Signal | Total Cost |
|--------|--------------|-------------------|-----------------|------------|
| Audio | 15-min podcast | 3 transcription gaps | $0.005 | $0.015 |
| Image | 10-page scan | 5 OCR regions | $0.01 | $0.05 |
| Document | 10k words | 2 entity ambiguities | $0.001 | $0.002 |
| Data | 100k rows | 3 type inferences | $0.001 | $0.003 |

### Monthly Costs (1000 files/day)

**Conservative estimate (30% of files have uncertain signals):**
- 1000 files/day × 30% = 300 files needing boost
- 300 files × $0.02 average = $6/day
- **Monthly total: ~$180**

**Cost control:**
- Max 5 boosts per file (prevents runaway costs)
- Confidence threshold 0.75 (only worst cases)
- Disabled by default (opt-in per domain)
- Re-boost weekly max (not every ingestion)

---

## Monitoring Metrics

```csharp
public class ConfidenceBoosterMetrics
{
    public int FilesScanned { get; set; }
    public int FilesQueued { get; set; }
    public int SignalsBoosted { get; set; }
    public int SignalsCorrected { get; set; }  // Changed vs confirmed
    public double TotalCostUSD { get; set; }
    public double AverageConfidenceImprovement { get; set; }
    public TimeSpan AverageBoostTime { get; set; }
    public Dictionary<string, int> BoostsByDomain { get; set; }
}
```

### Prometheus Metrics
```
confidence_booster_files_scanned_total
confidence_booster_files_queued_total
confidence_booster_signals_boosted_total
confidence_booster_signals_corrected_total
confidence_booster_cost_usd_total
confidence_booster_confidence_improvement_avg
confidence_booster_latency_seconds
```

---

## Success Criteria

- [ ] Background service runs every hour scanning for boost candidates
- [ ] ConfidenceBoosterWave runs as Priority 10 in background pipeline
- [ ] Signal ledger updates persist boosted signals with metadata
- [ ] Fast path ingestion unaffected (no latency added)
- [ ] At least one booster per domain (Audio, Image, Doc, Data)
- [ ] Cost per file stays under $0.10 for typical content
- [ ] Confidence improvements average 0.15+ on boosted signals
- [ ] Dashboard shows boost metrics (files, signals, cost, improvement)
- [ ] Re-boost weekly for files with new low-confidence signals
- [ ] Integration tests pass for all boosters

---

## Open Questions

1. **Should we support manual boost triggers?**
   - Allow users to request immediate boost via UI
   - Pro: User control, faster feedback
   - Con: Costs harder to predict, queue management

2. **Should we cache LLM responses?**
   - Hash artifact → cached response
   - Pro: Avoid duplicate work, cost savings
   - Con: Storage overhead, cache invalidation complexity

3. **Should we support batch boosting?**
   - Combine multiple uncertain regions in one LLM call
   - Pro: Cost-efficient
   - Con: Complex prompt engineering, harder attribution

4. **How to handle cascading boosts?**
   - DocSummarizer processes AudioSummarizer transcript
   - If transcript was boosted, mark downstream entities?
   - Provenance tracking across pipelines

5. **Should we learn optimal thresholds?**
   - Calibrate confidence thresholds per domain/booster
   - Pro: Reduce false boosts, optimize cost
   - Con: Requires training data, adds complexity

---

## Related Patterns

- **Reduced RAG:** Signals + Evidence + Query-time synthesis (boosting enhances signals)
- **Constrained Fuzziness:** Deterministic substrate + probabilistic enhancement (boosting is meta-enhancement)
- **Three-Tier OCR:** Local → ONNX → LLM escalation (ImageSummarizer precedent)
- **Filmstrip Pattern:** Multi-frame sampling for complex scenes (ImageSummarizer bounded artifact extraction)
- **Cascading Reduction:** AudioSummarizer → DocSummarizer (provenance tracking for boosted signals)

---

## Timeline

- **Phase 1 (Core):** 1 week
- **Phase 2 (Coordinator):** 1 week
- **Phase 3 (Audio):** 1 week
- **Phase 4 (Image):** 1 week
- **Phase 5 (Doc + Data):** 1 week
- **Phase 6 (Monitoring):** 3 days
- **Testing & Documentation:** 1 week

**Total:** ~7-8 weeks for complete implementation across all domains

---

## References

- **Reduced RAG Article:** [mostlylucid.net/blog/reduced-rag](https://www.mostlylucid.net/blog/reduced-rag)
- **Constrained Fuzziness Pattern:** [mostlylucid.net/blog/constrained-fuzziness-pattern](https://www.mostlylucid.net/blog/constrained-fuzziness-pattern)
- **AudioSummarizer Design:** `docs/audiosummarizer.md`
- **ImageSummarizer Three-Tier OCR:** Existing implementation in `ImageSummarizer.Core`

---

## Summary

ConfidenceBooster is the **background learning complement to Reduced RAG**:

1. **Reduced RAG:** Compute signals once, query against pre-computed facts
2. **ConfidenceBooster:** Improve signal quality over time using targeted LLM queries
3. **User experience:** Fast ingestion + progressive quality improvement
4. **Cost model:** Zero-cost ingestion + opt-in background refinement
5. **Universal pattern:** Works across Audio, Image, Doc, Data domains

This enables LucidRAG to have its cake and eat it too: instant results (fast path) AND high-quality signals (slow path), without forcing users to wait for LLM processing during ingestion.
