# Learning Pipeline - Background Document Reprocessing

**Pattern:** Like BotDetection's learning pipeline - singleton coordinator runs WHOLE stack (no early exit) to find better results, updates only if improved.

**Runtime:** Hosted mode only (DocSummarizer, LucidRAG web) - NOT in CLI mode

---

## Architecture Differences from ConfidenceBooster

### ConfidenceBooster (Targeted LLM Refinement)
```
Problem: Low-confidence SIGNALS need LLM refinement
Approach: Extract BOUNDED ARTIFACTS → Query LLM → Update specific signals
Example: "person" (0.68 confidence) → LLM clarifies → "person with backpack" (0.92)
Cost: Moderate (one LLM call per artifact)
```

### Learning Pipeline (Full Stack Reprocessing)
```
Problem: Entire DOCUMENT may have poor results
Approach: Rerun FULL STACK → Compare results → Update if better
Example: Document has 5 entities → Reprocess → finds 12 entities → UPDATE
Cost: High (full reprocessing) but finds systematic improvements
```

**Key difference:** ConfidenceBooster fixes individual low-confidence signals. Learning Pipeline reruns everything to find better overall results.

---

## How It Works

### The Two-Path Architecture

```
FAST PATH (User Upload):
  Upload → Process with best-effort → Return results immediately
  User can query right away (some results may be suboptimal)

SLOW PATH (Background Learning):
  Periodic scan → Find documents needing learning
  → Queue for reprocessing
  → Run FULL stack (no early exit, slower algorithms)
  → Compare with current results
  → Update if better
```

### Keyed Sequential Processing (Multi-Tenant)

Like BotDetection's keyed learning, with multi-tenant support:
- **One queue per (tenant, document) pair** (composite key: "tenantId:documentId")
- **Sequential per document** (no conflicts, ensures consistency)
- **Parallel across documents and tenants** (global throughput)
- **Priority queue** (0 = highest priority, 100 = lowest)
- **Low-priority background execution** (BelowNormal thread priority)

Example:
```
Tenant A, Doc 1: Task 1 (priority 10) → Task 2 (priority 50) → Task 3 (priority 80)
Tenant A, Doc 2: Task 1 (priority 20) → Task 2 (priority 50) (parallel to Doc 1)
Tenant B, Doc 1: Task 1 (priority 30) (parallel to Tenant A)
```

**Priority Guidelines:**
- `0-20`: Critical (user feedback, high-value documents)
- `40-60`: Normal (low confidence, low entity count)
- `70-100`: Low (periodic refresh, background optimization)

---

## Deduplication and Efficiency

### Document Hash Tracking

To prevent wasteful reprocessing of unchanged documents:

1. **Hash Computation**: Before processing, compute SHA-256 hash of document content
2. **Comparison**: Compare with `LearningStats.LastProcessedHash`
3. **Skip if Unchanged**: If hashes match, skip reprocessing and increment `SkippedUnchanged` counter
4. **Update on Success**: After successful learning, update hash to track version processed

**Benefits:**
- No redundant full-stack processing
- Efficient resource usage
- Fast path for unchanged documents (hash check takes milliseconds)

```csharp
// Example flow
var currentHash = await handler.GetDocumentHashAsync(task);
if (stats.LastProcessedHash == currentHash)
{
    stats.SkippedUnchanged++;
    _logger.LogDebug("Skipping unchanged document");
    return; // Early exit, no processing needed
}

// ... full stack processing ...

stats.LastProcessedHash = currentHash; // Track for next time
```

---

## When Does Learning Trigger?

### Automatic Triggers

1. **Low Confidence** (`confidence < 0.75`)
   ```
   Document processed, average entity confidence is 0.62
   → Queue for learning after 1 hour
   ```

2. **Low Entity Count** (`entities < 3`)
   ```
   Document has only 2 entities (likely poor extraction)
   → Queue for learning
   ```

3. **User Feedback** (negative ratings)
   ```
   User marks results as "poor" or "missing entities"
   → Queue immediately for learning
   ```

4. **Periodic Refresh** (documents > 30 days old)
   ```
   Document last processed 35 days ago
   → Queue for refresh (may use better algorithms now)
   ```

---

## What "Full Stack" Means

### Normal Processing (Fast Path)
```csharp
ProcessingOptions {
    ExtractEntities = true,
    RunAllExtractors = false,  // Early exit on first good match
    LearningMode = false        // Use fast algorithms
}
```

### Learning Reprocessing (Slow Path)
```csharp
ProcessingOptions {
    ExtractEntities = true,
    GenerateEmbeddings = true,
    ExtractRelationships = true,
    RunAllExtractors = true,    // NO EARLY EXIT - run all extractors
    LearningMode = true          // Use slower but better algorithms
}
```

**Example:**
- Fast path might use simple regex for dates
- Learning mode uses LLM-based date normalization (slower, more accurate)

---

## Result Comparison and Selective Updates

### Comparison Criteria

```csharp
// PHASE 3: Compare results
var shouldUpdate = false;

// More entities found?
if (newEntityCount > currentEntityCount) {
    shouldUpdate = true;
    improvements.Add($"+{delta} entities");
}

// Higher confidence?
if (newConfidence > currentConfidence + 0.05) {  // 5% threshold
    shouldUpdate = true;
    improvements.Add($"confidence improved");
}

// More relationships?
if (newRelationships > currentRelationships) {
    shouldUpdate = true;
    improvements.Add($"+{delta} relationships");
}

// Faster processing?
if (newProcessingTime < currentProcessingTime * 0.8) {  // 20% faster
    shouldUpdate = true;
    improvements.Add("processing optimized");
}

// PHASE 4: Update ONLY if improvements found
if (shouldUpdate) {
    await UpdateEntitySignature(documentId, newResults);
    await UpdateEvidence(documentId, newResults);
}
```

---

## Example Learning Flow

### Scenario: PDF with poor OCR extraction

```
1. USER UPLOAD (Fast Path)
   - Upload document.pdf
   - Run fast OCR (tesseract baseline)
   - Extract 3 entities (low count!)
   - Average confidence: 0.58 (low!)
   - User can query immediately (results available)

2. BACKGROUND SCAN (30 minutes later)
   - Scanner finds: low entity count (3) + low confidence (0.58)
   - Reason: "low_confidence (0.58), low_entity_count (3)"
   - Queue document for learning

3. LEARNING REPROCESSING (Slow Path)
   BASELINE:
   - Current: 3 entities, 0.58 confidence

   REPROCESS (full stack, no early exit):
   - Run ALL OCR extractors (tesseract + doctr + LLM-enhanced)
   - Run ALL entity extractors (spacy + bert + LLM)
   - Extract relationships

   RESULTS:
   - New: 12 entities, 0.84 confidence
   - Improvements: +9 entities, +0.26 confidence

4. SELECTIVE UPDATE
   - Replace entities (3 → 12)
   - Update evidence with new extraction
   - Log: "Learning found improvements: +9 entities, confidence improved"

5. USER QUERIES AGAIN
   - Sees improved results (12 entities instead of 3)
   - Higher quality answers from better extraction
```

---

## Integration

### DocSummarizer (Hosted Mode)
```csharp
// In Program.cs (hosted web service)
builder.Services.AddDocSummarizerLearning(config =>
{
    config.Enabled = true;  // Enable learning
    config.ScanInterval = TimeSpan.FromMinutes(30);
    config.ConfidenceThreshold = 0.75;
});
```

### LucidRAG Web (Hosted Mode)
```csharp
// In Program.cs (web app)
builder.Services.AddLucidRagLearning(config =>
{
    config.Enabled = true;
    config.ScanInterval = TimeSpan.FromMinutes(60);  // Less frequent
    config.ConfidenceThreshold = 0.70;
    config.MinDocumentAge = TimeSpan.FromHours(2);  // Wait before learning
});
```

### CLI (Disabled)
```csharp
// In CLI Program.cs
services.DisableLearning();  // No background service in CLI
```

---

## Configuration

### appsettings.json

```json
{
  "Learning": {
    "Enabled": true,
    "HostedModeOnly": true,
    "ScanInterval": "00:30:00",
    "MinDocumentAge": "01:00:00",
    "ConfidenceThreshold": 0.75
  }
}
```

### Configuration Options

- **`Enabled`** - Enable learning (default: false)
- **`HostedModeOnly`** - Only run in hosted mode, not CLI (default: true)
- **`ScanInterval`** - How often to scan for learning candidates (default: 30 minutes)
- **`MinDocumentAge`** - Minimum age before document eligible (default: 1 hour)
- **`ConfidenceThreshold`** - Threshold for low-confidence trigger (default: 0.75)
- **`CancellationToken`** - Optional cancellation token for graceful shutdown

### Performance Tuning

- **Max Queue Size**: 100 tasks per (tenant, document) queue (configurable in constructor)
- **Thread Priority**: BelowNormal (background tasks don't interfere with user requests)
- **Shutdown Timeout**: 30 seconds to drain queues gracefully
- **Deduplication**: Hash-based skip for unchanged documents

---

## Monitoring

### Log Output

```
[12:30:00] Learning scan starting
[12:30:01] Found 8 documents needing learning
[12:30:01]   - low_confidence: 3 documents
[12:30:01]   - low_entity_count: 2 documents
[12:30:01]   - user_feedback: 1 document
[12:30:01]   - periodic_refresh: 2 documents
[12:30:01] Submitted 8/8 documents to coordinator

[12:32:15] Processing learning task for document abc123 (reason: low_confidence (0.62))
[12:32:15] Baseline: 3 entities, 0.62 confidence
[12:32:45] Reprocessing complete: 12 entities, 0.84 confidence
[12:32:45] Updating document abc123 with improvements: +9 entities, confidence improved
[12:32:45] Learning complete: 12/12 entities, processing time: 30s

[12:34:20] Processing learning task for document def456 (reason: low_entity_count (2))
[12:34:45] Reprocessing complete: 2 entities, 0.75 confidence
[12:34:45] No improvements found for document def456
```

### Statistics

```csharp
var stats = await coordinator.GetStatsAsync(tenantId, documentId);

Console.WriteLine($"Document {documentId} (Tenant: {tenantId}):");
Console.WriteLine($"  Total learning runs: {stats.TotalLearningRuns}");
Console.WriteLine($"  Improvements found: {stats.ImprovementsFound}");
Console.WriteLine($"  No improvement runs: {stats.NoImprovementRuns}");
Console.WriteLine($"  Skipped (unchanged): {stats.SkippedUnchanged}");
Console.WriteLine($"  Last improvement: {stats.LastImprovement}");
Console.WriteLine($"  Last processed hash: {stats.LastProcessedHash}");
Console.WriteLine($"  Average processing time: {stats.AverageProcessingTime}");
```

**Efficiency Metrics:**
- High `SkippedUnchanged` count = good (documents stable, no waste)
- High `NoImprovementRuns` with low `SkippedUnchanged` = potential issue (reprocessing but not finding improvements)
- `LastProcessedHash` useful for debugging (compare with current document hash)

---

## Files Created

```
LucidRAG.Core/Services/Learning/
├── LearningCoordinator.cs           # Singleton coordinator (keyed sequential)
├── LearningBackgroundService.cs     # Background scan service
├── Handlers/
│   └── DocumentLearningHandler.cs   # Document reprocessing handler
└── README.md (this file)
```

---

## Next Steps

1. **Implement remaining handlers:**
   - `ImageLearningHandler` - Reprocess images with slower but better OCR/object detection
   - `AudioLearningHandler` - Reprocess audio with better transcription models
   - `DataLearningHandler` - Reprocess data with more sophisticated schema inference

2. **Add repository implementations:**
   - `DocumentRepository.FindByConfidenceAsync`
   - `DocumentRepository.FindByEntityCountAsync`
   - `DocumentRepository.FindWithNegativeFeedbackAsync`
   - `DocumentRepository.FindProcessedBeforeAsync`

3. **Add processing service extensions:**
   - `IDocumentProcessingService.ReprocessFullStackAsync`
   - Support for `RunAllExtractors` and `LearningMode` options

4. **Testing:**
   - Unit tests for comparison logic
   - Integration tests for learning flow
   - Verify no updates when results are worse

---

## Pattern Summary

**Like BotDetection:**
- ✅ Singleton coordinator
- ✅ Keyed sequential processing (one doc at a time per key)
- ✅ Multi-tenant support (composite key: "tenantId:documentId")
- ✅ Background service with periodic scans
- ✅ Event-driven triggers (confidence, feedback, etc.)
- ✅ Hosted mode only (not CLI)
- ✅ Low-priority thread execution (BelowNormal)

**Unlike ConfidenceBooster:**
- ❌ NOT about LLM refinement of individual signals
- ✅ About rerunning FULL processing stack
- ✅ Compares entire result sets
- ✅ Updates only if objectively better

**New Features (Beyond BotDetection):**
- ✅ Priority queue (0-100 scale, user feedback = highest priority)
- ✅ Document hash tracking (skip if unchanged)
- ✅ Multi-tenant isolation (parallel across tenants, sequential per document)
- ✅ Efficiency metrics (SkippedUnchanged counter)

**Cost Model:**
- High per-document (full reprocessing)
- But infrequent (periodic scans, selective triggering)
- Mitigated by deduplication (hash-based skip for unchanged docs)
- Net benefit: Better results over time without user waiting
