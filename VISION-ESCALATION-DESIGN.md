# Multi-Tier Vision LLM Escalation Design

## Overview

Implement a tiered escalation system that starts with fast/cheap models and escalates to more powerful models only when needed, optimizing for speed and cost while maintaining accuracy.

## Tier Structure

### Tier 1: Fast (Triage)
- **Model**: `bakllava:7b` or `llava:7b`
- **Speed**: ~1-2s per image
- **Use Case**: Initial quick analysis, simple images
- **Accuracy**: 75-85% of baseline

### Tier 2: Balanced (General)
- **Model**: `minicpm-v:8b` (current default)
- **Speed**: ~2-3s per image
- **Use Case**: Most images, moderate complexity
- **Accuracy**: 100% (baseline)

### Tier 3: Powerful (Complex)
- **Model**: `llava:13b` or `llava-llama3:13b`
- **Speed**: ~3-5s per image
- **Use Case**: Complex diagrams, difficult OCR, low confidence
- **Accuracy**: 105-110% of baseline

## Escalation Triggers

### Tier 1 → Tier 2 Escalation
Escalate if ANY of:
- OCR confidence < 0.6
- Image has high text likeliness (>0.5) but poor extraction
- Image type detection confidence < 0.6
- Blurry image (LaplacianVariance < 250)
- Complex image type (Diagram, Chart, Technical)

### Tier 2 → Tier 3 Escalation
Escalate if ANY of:
- OCR confidence < 0.4 after Tier 2 attempt
- Multiple contradictory signals detected
- User explicitly requested highest quality (`--quality high`)
- GIF with high complexity score (>0.7)
- Known difficult image patterns (very small text, complex layouts)

## Configuration

Add to `appsettings.json`:

```json
{
  "VisionLlm": {
    "EnableTieredEscalation": true,
    "Tiers": {
      "Fast": {
        "Model": "bakllava:7b",
        "Enabled": true,
        "MaxRetries": 1
      },
      "Balanced": {
        "Model": "minicpm-v:8b",
        "Enabled": true,
        "MaxRetries": 1
      },
      "Powerful": {
        "Model": "llava:13b",
        "Enabled": false,  // Only if model is available
        "MaxRetries": 1
      }
    },
    "EscalationThresholds": {
      "OcrConfidenceMin": 0.6,
      "TypeConfidenceMin": 0.6,
      "BlurThreshold": 250,
      "ComplexityThreshold": 0.7
    },
    "DefaultTier": "Balanced"  // Start here if triage disabled
  }
}
```

## Implementation Plan

### 1. Update VisionLlmService

Add tiered escalation support:

```csharp
public class VisionLlmService
{
    private readonly VisionTierConfig _tierConfig;

    public async Task<TieredVisionResult> AnalyzeWithTiersAsync(
        string imagePath,
        string? customPrompt = null,
        VisionTier? startTier = null,
        CancellationToken ct = default)
    {
        var currentTier = startTier ?? _tierConfig.DefaultTier;
        var results = new List<VisionAttempt>();

        while (currentTier != null)
        {
            var model = _tierConfig.GetModelForTier(currentTier.Value);
            var result = await AnalyzeImageAsync(imagePath, customPrompt, model, ct);

            var attempt = new VisionAttempt
            {
                Tier = currentTier.Value,
                Model = model,
                Result = result,
                Timestamp = DateTime.UtcNow
            };

            results.Add(attempt);

            // Check if escalation is needed
            if (ShouldEscalate(attempt, currentTier.Value))
            {
                currentTier = GetNextTier(currentTier.Value);
                _logger.LogInformation(
                    "Escalating from {CurrentTier} to {NextTier} for {ImagePath}",
                    attempt.Tier, currentTier, imagePath);
            }
            else
            {
                break; // Success, no escalation needed
            }
        }

        return new TieredVisionResult
        {
            FinalResult = results.Last().Result,
            AllAttempts = results,
            FinalTier = results.Last().Tier,
            EscalationCount = results.Count - 1
        };
    }

    private bool ShouldEscalate(VisionAttempt attempt, VisionTier currentTier)
    {
        // Tier 1 → Tier 2 triggers
        if (currentTier == VisionTier.Fast)
        {
            // Check if result seems uncertain or incomplete
            // (would need to parse LLM output for confidence signals)
            return true; // Placeholder - implement heuristics
        }

        // Tier 2 → Tier 3 triggers
        if (currentTier == VisionTier.Balanced)
        {
            // Only escalate for very difficult cases
            return false; // Conservative escalation
        }

        return false; // Tier 3 is terminal
    }

    private VisionTier? GetNextTier(VisionTier current)
    {
        return current switch
        {
            VisionTier.Fast => VisionTier.Balanced,
            VisionTier.Balanced => _tierConfig.Tiers.Powerful.Enabled ? VisionTier.Powerful : null,
            VisionTier.Powerful => null,
            _ => null
        };
    }
}

public enum VisionTier
{
    Fast,
    Balanced,
    Powerful
}

public record TieredVisionResult
{
    public VisionLlmResult FinalResult { get; init; }
    public List<VisionAttempt> AllAttempts { get; init; }
    public VisionTier FinalTier { get; init; }
    public int EscalationCount { get; init; }
}

public record VisionAttempt
{
    public VisionTier Tier { get; init; }
    public string Model { get; init; }
    public VisionLlmResult Result { get; init; }
    public DateTime Timestamp { get; init; }
}
```

### 2. Update CLI Options

Add quality preset option:

```bash
# Fast mode (Tier 1 only)
lucidrag-image analyze image.gif --quality fast

# Balanced mode (Start Tier 1, escalate to Tier 2 if needed)
lucidrag-image analyze image.gif --quality balanced  # Default

# Best mode (Use all tiers, escalate aggressively)
lucidrag-image analyze image.gif --quality best

# Force specific tier
lucidrag-image analyze image.gif --tier balanced
```

### 3. Update EscalationService

Integrate tiered vision analysis:

```csharp
if (shouldEscalate)
{
    var tieredResult = await _visionLlmService.AnalyzeWithTiersAsync(
        imagePath,
        customPrompt,
        startTier: VisionTier.Fast,  // Or from config
        ct);

    llmCaption = tieredResult.FinalResult.Caption;

    _logger.LogInformation(
        "Vision analysis used {Tier} tier after {Count} escalations",
        tieredResult.FinalTier,
        tieredResult.EscalationCount);
}
```

## Benefits

1. **Cost Optimization**: Use cheap/fast models when sufficient
2. **Speed**: Fast initial response, escalate only when needed
3. **Accuracy**: Best-quality results for difficult images
4. **Transparency**: Users see which tier was used
5. **Configurable**: Easy to adjust thresholds and models

## Testing Strategy

1. **Run baseline tests** with current single-model approach
2. **Implement tiered system** with Fast → Balanced escalation
3. **Measure**:
   - Average processing time (should decrease)
   - Accuracy (should match or exceed baseline)
   - Escalation rate (what % of images escalate?)
4. **Tune thresholds** based on test corpus results
5. **Add Tier 3** once llava:13b is validated

## Migration Path

1. **Phase 1** (Current): Single model with `--model` override ✅
2. **Phase 2**: Two-tier Fast → Balanced with auto-escalation
3. **Phase 3**: Three-tier Fast → Balanced → Powerful
4. **Phase 4**: Self-learning thresholds based on feedback

## Questions for User

1. Should we start with 2-tier (Fast → Balanced) or go straight to 3-tier?
2. What should trigger escalation? (confidence scores, image complexity, both?)
3. Should users see all tier attempts or just the final result?
4. Should we cache results per-tier or only final result?
