# Multi-Vector Discriminator Learning with Decay

This document describes the **discriminator discovery loop** implemented in LucidRAG's image analysis system. This is a stable, self-learning framework that uses orthogonal quality vectors, signal effectiveness tracking, and time-based decay to continuously improve analysis quality.

## Philosophy

> "Signals are immutable. Discriminators must continuously justify their existence."

The discriminator system is built on the principle that **facts are append-only** but **interpretations decay**. Every observation about an image (color, sharpness, text likelihood) is stored permanently in an immutable ledger. However, the _usefulness_ of these observations for predicting good vs. bad results must be continuously re-evaluated and can fade over time.

This prevents the system from:
- **Overfitting** to early patterns that later prove spurious
- **Cementing false correlations** as permanent truth
- **Ignoring emergent signals** that become important over time
- **Single-metric optimization collapse** (optimizing one dimension destroys others)

## Architecture

### 1. **Immutable Signal Ledger** (`SignalDatabase`)

All raw observations are stored permanently:
- Edge density, sharpness, text likelihood (visual signals)
- Motion magnitude, frame consistency (temporal signals)
- Color distribution, saturation variance (palette signals)
- SHA256, dimensions, format (metadata signals)

These signals **never change**. They are objective measurements taken at analysis time.

### 2. **Orthogonal Quality Vectors** (`VectorScores`)

Analysis quality is measured across **six independent dimensions**:

| Vector | Measures | Example Signals |
|--------|----------|-----------------|
| **OCR Fidelity** | Text extraction accuracy | TextLikeliness, spell-check pass rate, OCR confidence |
| **Motion Agreement** | Animation analysis reliability | Frame consistency, optical flow confidence, temporal voting |
| **Palette Consistency** | Color analysis stability | Dominant color coverage, saturation variance |
| **Structural Alignment** | Geometric/edge reliability | Edge density, sharpness, aspect ratio sanity |
| **Grounding Completeness** | Evidence-backed claims | Non-synthesis source ratio, evidence diversity |
| **Novelty vs Prior** | Difference from past results | Caption divergence, confidence delta |

Each vector is scored independently (0.0-1.0). The **overall score** is the average of applicable vectors.

### 3. **Signal Contributions** (`SignalContribution`)

For each analysis, we track:
- Which signals contributed to each vector
- How strongly each signal influenced the score (0.0-1.0)
- How much each signal agreed with peer signals in the same vectors

Example:
```json
{
  "LaplacianVariance": {
    "value": 842.3,
    "contributedVectors": ["StructuralAlignment"],
    "strength": 0.842,
    "agreement": 0.91  // 91% agreement with EdgeDensity and LuminanceEntropy
  }
}
```

### 4. **Discriminator Effectiveness** (`DiscriminatorEffectiveness`)

For each signal/type/goal combination, we track:
- **Weight** (0.0-2.0): Current effectiveness, starts at 1.0 (neutral)
- **Evaluation Count**: How many times this discriminator was evaluated
- **Agreement Count**: How many times it agreed with accepted results
- **Disagreement Count**: How many times it disagreed
- **Last Evaluated**: Timestamp for decay calculation
- **Decay Rate**: Default 0.95 (5% decay per day)

### 5. **Decay-Based Learning** (`SignalEffectivenessTracker`)

Weights are updated using **exponential moving average with decay**:

```csharp
// Apply time-based decay first
var decayedWeight = weight * Math.Pow(decayRate, daysSinceLastEval);

// Learning rate decreases as evaluations increase (stabilizes over time)
var learningRate = 1.0 / Math.Max(1, Math.Sqrt(priorEvaluations + 1));

// Update based on agreement
var delta = agreed ? learningRate : -learningRate;
var newWeight = Math.Clamp(decayedWeight + delta, 0.0, 2.0);
```

**Key properties:**
- **Weights can exceed 1.0** for highly effective discriminators (max 2.0)
- **Weights decay to 0.0** for unused discriminators (5% per day default)
- **Learning rate decreases** as evidence accumulates (stability)
- **Discriminators are retired** when weight < 0.1 (automatic pruning)

## Agreement Logic

A signal "agrees" with the outcome when:

| Outcome | Signal Strength | Agreement |
|---------|-----------------|-----------|
| Accepted + High Score | > 0.5 | ✓ Agreed (correctly indicated quality) |
| Rejected + Low Score | ≤ 0.5 | ✓ Agreed (correctly indicated poor quality) |
| Accepted + Low Score | ≤ 0.5 | ✗ Disagreed (user saw value system missed) |
| Rejected + High Score | > 0.5 | ✗ Disagreed (user found issue system missed) |

This allows the system to learn from **both positive and negative examples**.

## Usage Example

### 1. **Analyze and Score**

```bash
# Analyze an image and compute discriminator scores
dotnet run --project src/LucidRAG.ImageCli -- score test.jpg \
  --model anthropic:claude-3-opus-20240229 \
  --goal caption
```

Output:
```
┌─ Multi-Vector Discriminator Scores ───────────────────────┐
│ Vector                  │ Score   │ Description            │
├─────────────────────────┼─────────┼────────────────────────┤
│ Overall Quality         │ 0.847 ████████████████████   │ Weighted average    │
│ OCR Fidelity            │ 0.912 ██████████████████     │ Text detection      │
│ Motion Agreement        │ 0.000                         │ N/A (static)        │
│ Palette Consistency     │ 0.823 ████████████████       │ Color reliability   │
│ Structural Alignment    │ 0.891 █████████████████      │ Edge/sharpness      │
│ Grounding Completeness  │ 0.950 ███████████████████    │ Evidence coverage   │
│ Novelty vs Prior        │ 1.000 ████████████████████   │ First analysis      │
└─────────────────────────┴─────────┴────────────────────────┘
```

### 2. **Provide Feedback**

```bash
# Accept the result
dotnet run --project src/LucidRAG.ImageCli -- score test.jpg \
  --model anthropic:claude-3-opus-20240229 \
  --goal caption \
  --accept true \
  --feedback "Caption accurately describes the book page"
```

This updates effectiveness weights for all contributing signals:
- Signals with high strength in high-scoring vectors → weight increases
- Signals with low strength that correctly indicated quality → weight increases
- Signals that disagreed with outcome → weight decreases

### 3. **View Top Discriminators**

```bash
# See which signals are most effective for book pages
dotnet run --project src/LucidRAG.ImageCli -- score test.jpg \
  --goal ocr \
  --show-top
```

Output:
```
┌─ Top Discriminators for Document/ocr ─────────────────────┐
│ Signal              │ Weight  │ Agreement Rate │ Evals    │
├─────────────────────┼─────────┼────────────────┼──────────┤
│ TextLikeliness      │ 1.847   │ 94%            │ 47/50    │
│ LaplacianVariance   │ 1.623   │ 89%            │ 42/47    │
│ EdgeDensity         │ 1.412   │ 85%            │ 38/45    │
│ LuminanceEntropy    │ 1.289   │ 82%            │ 35/43    │
│ MeanSaturation      │ 0.923   │ 71%            │ 25/35    │
└─────────────────────┴─────────┴────────────────┴──────────┘
```

### 4. **Prune Ineffective Discriminators**

```bash
# Remove discriminators with weight < 0.1
dotnet run --project src/LucidRAG.ImageCli -- score test.jpg --prune
```

## Multi-Vector Optimization

The discriminator system prevents **single-metric optimization collapse** by:

1. **Orthogonal Vectors**: Each vector measures a different quality dimension
2. **Vector Weighting**: Overall score requires balance across all applicable vectors
3. **Cross-Vector Agreement**: Signals are rewarded for agreeing with peers in same vectors
4. **Decay Pruning**: Discriminators that stop helping are automatically retired

This means optimizing for OCR quality **cannot** degrade motion analysis quality, because they're measured independently and both contribute to the overall score.

## Immutable Ledger + Adaptive Control

The system maintains **two separate databases**:

### `discriminator_scores` (Immutable Ledger)
- Append-only record of every analysis
- Contains: timestamp, vectors, signals, vision model, accepted/rejected
- **Never modified or deleted**
- Enables audit trail and replay

### `discriminator_effectiveness` (Adaptive Weights)
- Mutable record of signal effectiveness
- Contains: weight, evaluation count, agreement rate, last evaluated
- **Updated continuously** with decay
- **Pruned automatically** when weight < threshold

This separation ensures:
- **Facts are permanent** (you can always see what was observed)
- **Interpretations evolve** (what observations mean changes over time)
- **Bad feedback can only influence policy, never facts**

## Continuous Learning Loop

```
┌─────────────────────────────────────────────────────────┐
│ 1. Analyze Image                                        │
│    ├─ Extract signals (immutable observations)          │
│    ├─ Compute vector scores (orthogonal quality dims)   │
│    └─ Record to ledger (append-only)                    │
├─────────────────────────────────────────────────────────┤
│ 2. User Feedback                                        │
│    ├─ Accept or reject result                           │
│    └─ Optional notes explaining decision                │
├─────────────────────────────────────────────────────────┤
│ 3. Update Effectiveness                                 │
│    ├─ Apply time-based decay to all weights             │
│    ├─ Increase weight for signals that agreed           │
│    ├─ Decrease weight for signals that disagreed        │
│    └─ Retire discriminators with weight < 0.1           │
├─────────────────────────────────────────────────────────┤
│ 4. Next Analysis                                        │
│    ├─ Use updated weights to prioritize signals         │
│    ├─ Compute novelty vs prior (learning history)       │
│    └─ Continue loop                                     │
└─────────────────────────────────────────────────────────┘
```

## Technical Implementation

### Database Schema

```sql
-- Immutable ledger
CREATE TABLE discriminator_scores (
    id TEXT PRIMARY KEY,
    image_hash TEXT NOT NULL,
    timestamp TEXT NOT NULL,
    image_type INTEGER NOT NULL,
    goal TEXT NOT NULL,
    overall_score REAL NOT NULL,
    ocr_fidelity REAL,
    motion_agreement REAL,
    palette_consistency REAL,
    structural_alignment REAL,
    grounding_completeness REAL,
    novelty_vs_prior REAL,
    vision_model TEXT,
    strategy TEXT,
    accepted INTEGER,        -- NULL = pending, 0 = rejected, 1 = accepted
    feedback TEXT,
    signal_contributions_json TEXT
);

-- Adaptive weights
CREATE TABLE discriminator_effectiveness (
    signal_name TEXT NOT NULL,
    image_type INTEGER NOT NULL,
    goal TEXT NOT NULL,
    weight REAL NOT NULL,
    evaluation_count INTEGER NOT NULL,
    agreement_count INTEGER NOT NULL,
    disagreement_count INTEGER NOT NULL,
    last_evaluated TEXT NOT NULL,
    decay_rate REAL NOT NULL,
    is_retired INTEGER DEFAULT 0,
    PRIMARY KEY (signal_name, image_type, goal)
);
```

### API Surface

```csharp
// Score an analysis result
var score = await discriminatorService.ScoreAnalysisAsync(
    imageHash, profile, gifMotion, visionResult, extractedText, goal);

// Record user feedback
await discriminatorService.RecordFeedbackAsync(
    score, accepted: true, feedback: "Accurate caption");

// Get effectiveness for specific signal
var weight = await tracker.GetEffectivenessWeightAsync(
    "TextLikeliness", ImageType.Document, "ocr");

// Get top discriminators for type/goal
var topDiscriminators = await tracker.GetTopDiscriminatorsAsync(
    ImageType.Document, "ocr", limit: 10);

// Prune ineffective discriminators
await tracker.PruneIneffectiveDiscriminatorsAsync(threshold: 0.1);

// Get learning statistics
var stats = await tracker.GetStatsAsync();
```

## Benefits

1. **Self-Improving**: System gets better with use, learning from feedback
2. **Robust to Noise**: Decay prevents spurious correlations from becoming permanent
3. **Multi-Objective**: Orthogonal vectors prevent optimization collapse
4. **Transparent**: Immutable ledger enables full audit trail
5. **Adaptive**: Weights adjust to changing data distributions
6. **Automatic Pruning**: Ineffective discriminators retire naturally
7. **Stable**: Learning rate decreases as evidence accumulates

## Future Extensions

- **Per-User Learning**: Track effectiveness separately for each user
- **Strategy Integration**: Learn which preprocessing strategies work best
- **Active Learning**: Suggest images that would most improve the model
- **Ensemble Scoring**: Combine multiple discriminator approaches
- **Confidence Calibration**: Adjust confidence thresholds based on agreement rates
- **Anomaly Detection**: Flag images where discriminators strongly disagree

## References

- `src/Mostlylucid.DocSummarizer.Images/Models/DiscriminatorScore.cs` - Data models
- `src/Mostlylucid.DocSummarizer.Images/Services/Analysis/DiscriminatorService.cs` - Scoring logic
- `src/Mostlylucid.DocSummarizer.Images/Services/Analysis/SignalEffectivenessTracker.cs` - Learning loop
- `src/Mostlylucid.DocSummarizer.Images/Services/Storage/SignalDatabase.cs` - Persistence
- `src/LucidRAG.ImageCli/Commands/ScoreCommand.cs` - CLI interface
