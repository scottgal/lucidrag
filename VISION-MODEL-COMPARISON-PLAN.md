# Vision LLM Model Comparison Test Plan

**Date**: 2026-01-04
**Purpose**: Compare 4 vision LLM models for OCR accuracy, speed, and suitability for GIF text extraction

## Models to Test

1. **minicpm-v:8b** (recommended baseline)
   - Claimed size: 4.5GB
   - Actual size: 5.5 GB
   - Expected: Good balance of speed and accuracy

2. **llava:7b**
   - Claimed size: 3.8GB
   - Expected: Faster but less accurate

3. **llava:13b**
   - Claimed size: 7.3GB
   - Expected: Higher quality, slower

4. **bakllava:7b**
   - Claimed size: 4.1GB
   - Expected: Optimized for speed

## Test Corpus

Select 5 GIFs with varying OCR difficulty:

1. **BackOfTheNet.gif** - Known OCR error ("Back Bf the net")
2. **anchorman-not-even-mad.gif** - Clean text ("I'm not even mad.")
3. **aed.gif** - Multi-word text ("You keep using that word...")
4. **arse_biscuits.gif** - Uppercase text ("ARSE BISCUITS")
5. **animatedbullshit.gif** - Garbled text ("ph ima "|")

## Metrics to Measure

### 1. Accuracy
- **OCR Correction Quality**: Did it fix known errors? (e.g., "Bf" → "of")
- **Text Extraction Completeness**: Did it extract all visible text?
- **False Positives**: Did it hallucinate text that doesn't exist?

### 2. Speed
- **Total Time**: Time from start to finish for Tier 3
- **Time per GIF**: Average processing time

### 3. Model Size
- **Disk Size**: Actual size from `ollama list`
- **Memory Usage**: If observable

### 4. GIF-Specific Performance
- **Frame Consistency**: Can it track text across multiple frames?
- **Animation Handling**: Does it handle animated GIFs differently than static images?

## Test Procedure

For each model:

1. **Configure ImageCli** to use the model:
   ```csharp
   opt.VisionLlmModel = "{model_name}";
   ```

2. **Test on corpus** with timing:
   ```bash
   $models = @('minicpm-v:8b', 'llava:7b', 'llava:13b', 'bakllava:7b')
   $gifs = @('BackOfTheNet.gif', 'anchorman-not-even-mad.gif', 'aed.gif',
             'arse_biscuits.gif', 'animatedbullshit.gif')

   foreach ($model in $models) {
       Write-Host "`n=== Testing model: $model ==="
       foreach ($gif in $gifs) {
           $start = Get-Date
           & ImageCli.exe "F:\Gifs\$gif" --model $model --output json |
               Select-String "ocr.corrected.text|vision.llm"
           $duration = (Get-Date) - $start
           Write-Host "$gif: $($duration.TotalSeconds)s"
       }
   }
   ```

3. **Record results** in comparison table

## Expected Results Matrix

| Metric | minicpm-v:8b | llava:7b | llava:13b | bakllava:7b |
|--------|--------------|----------|-----------|-------------|
| **Size** | 5.5 GB | ~4 GB | ~7.5 GB | ~4 GB |
| **Speed** | Medium (baseline) | Fast (1.5x baseline) | Slow (0.7x baseline) | Fastest (1.8x baseline) |
| **Accuracy** | High (baseline) | Medium (80% baseline) | Highest (110% baseline) | Medium (85% baseline) |
| **Best For** | General use | High throughput | Maximum quality | Speed-critical |

## Success Criteria

A model is **recommended** if:
- ✅ Accuracy >= 90% of minicpm-v:8b baseline
- ✅ Can correct known OCR errors ("Bf" → "of")
- ✅ Low false positive rate (< 5%)
- ✅ Reasonable speed (< 5s per GIF on average)

A model is **acceptable for speed-critical** use if:
- ✅ Speed >= 1.5x minicpm-v:8b baseline
- ✅ Accuracy >= 75% of baseline
- ⚠️ May sacrifice some accuracy for speed

A model is **recommended for quality-critical** use if:
- ✅ Accuracy >= 105% of baseline
- ⚠️ May sacrifice speed for accuracy
- ✅ Can handle complex multi-word text

## Output Format

### Per-Model Results

```markdown
## Model: {model_name}

**Size**: {actual_size} GB
**Average Speed**: {avg_seconds}s per GIF

### Test Results

| GIF | Original OCR | Corrected Text | Time (s) | Accuracy |
|-----|--------------|----------------|----------|----------|
| BackOfTheNet.gif | "Back Bf the net" | "Back of the net" | 2.3s | ✅ Correct |
| anchorman.gif | "I'm not even mad." | "I'm not even mad." | 1.8s | ✅ Correct |
| ... | ... | ... | ... | ... |

**Strengths**: {list}
**Weaknesses**: {list}
**Recommended For**: {use case}
```

### Final Comparison Table

| Model | Size (GB) | Avg Speed (s) | Accuracy (%) | Corrections | False Positives | Recommendation |
|-------|-----------|---------------|--------------|-------------|-----------------|----------------|
| minicpm-v:8b | 5.5 | 2.1 | 100% (baseline) | 4/5 | 0 | ✅ General use |
| llava:7b | 4.0 | 1.4 | 85% | 3/5 | 1 | ⚠️ High throughput |
| llava:13b | 7.5 | 3.2 | 110% | 5/5 | 0 | ✅ Maximum quality |
| bakllava:7b | 4.1 | 1.2 | 80% | 3/5 | 2 | ⚠️ Speed-critical |

## Next Steps

1. ✅ Download all models (in progress)
2. ⏳ Implement model-switching in ImageCli Program.cs
3. ⏳ Run systematic tests on all 5 GIFs × 4 models = 20 test cases
4. ⏳ Record timing and accuracy data
5. ⏳ Create final comparison report
6. ⏳ Update documentation with recommended model per use case

---

**Status**: Test Plan Ready, Awaiting Model Downloads
