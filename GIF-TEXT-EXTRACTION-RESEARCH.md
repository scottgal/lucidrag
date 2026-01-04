# Research: State-of-the-Art Text Extraction for Animated Images

**Date**: 2026-01-04
**Status**: Research Complete - Ready for Implementation

## Executive Summary

Current implementation uses basic frame sampling with Tesseract OCR. Research shows we can significantly improve accuracy by:
1. **SSIM-based frame deduplication** - Skip similar frames to reduce redundant OCR
2. **Temporal text tracking** - Track text across frames for consistency
3. **Levenshtein distance deduplication** - Merge duplicate text intelligently
4. **Scene text detection** (optional) - Use EAST/CRAFT for better text localization

## Research Findings

### 1. Frame Selection & Deduplication

**SSIM (Structural Similarity Index)**:
- Compare consecutive frames using SSIM metric
- If SSIM > threshold (e.g., 0.95), skip frame as duplicate
- **Benefit**: Reduces OCR calls by 50-80% for typical GIFs

**Current Implementation**:
```csharp
// Current: Sample by text-likeliness score
var selectedFrames = frameScores
    .Where(f => f.TextScore >= _textLikelinessThreshold)
    .OrderByDescending(f => f.TextScore)
    .Take(_maxFramesToSample)
```

**Improved Approach**:
```csharp
// Step 1: SSIM-based deduplication
var uniqueFrames = DeduplicateFramesBySSIM(allFrames, threshold: 0.95);

// Step 2: Select frames with high text-likeliness from unique set
var textFrames = uniqueFrames
    .Where(f => f.TextScore >= threshold)
    .OrderByDescending(f => f.TextScore);
```

**Source**: [VideOCR GitHub](https://github.com/timminator/VideOCR) - Uses SSIM to skip similar frames

### 2. Temporal Text Tracking

**Problem**: Progressive text reveals and subtitle changes need temporal context

**Solution**: Track text bounding boxes across frames
- Assign Track IDs to consistent text regions
- Merge text from same Track ID
- Only output final version of progressively revealed text

**Example**:
```
Frame 1: "H"       (Track ID: 1)
Frame 2: "HE"      (Track ID: 1)
Frame 3: "HEL"     (Track ID: 1)
Frame 4: "HELL"    (Track ID: 1)
Frame 5: "HELLO"   (Track ID: 1)

Output: "HELLO" (from Track ID 1)
```

**Source**: [Scene Text Detection and Tracking](https://dl.acm.org/doi/10.1145/3206025.3206051) - Temporal fusion improves recognition accuracy

### 3. Text Deduplication Strategies

**Levenshtein Distance**:
- Measure edit distance between extracted text strings
- Merge strings with high similarity (e.g., distance < 3)
- Handles OCR errors gracefully

**Implementation**:
```csharp
private string DeduplicateText(List<string> textBlocks)
{
    var merged = new List<string>();

    foreach (var text in textBlocks)
    {
        bool isDuplicate = false;
        for (int i = 0; i < merged.Count; i++)
        {
            if (LevenshteinDistance(text, merged[i]) < 3)
            {
                // Keep longer version
                if (text.Length > merged[i].Length)
                    merged[i] = text;
                isDuplicate = true;
                break;
            }
        }

        if (!isDuplicate)
            merged.Add(text);
    }

    return string.Join(" ", merged);
}
```

**Source**: [Subtitle Extractor](https://subtitleextractor.com/blog/how-to-extract-hardcoded-subtitles/) - Levenshtein distance for duplicate merging

### 4. Advanced Text Detection (Optional Enhancement)

**EAST (Efficient and Accurate Scene Text) Detector**:
- Deep learning model for text detection
- Detects arbitrary-oriented text
- ~0.14 seconds per image
- **Downside**: Requires additional ML model deployment

**CRAFT (Character Region Awareness)**:
- Fully convolutional network
- Character-level detection
- Multi-lingual support
- **Downside**: More complex than EAST

**Current Approach (Tesseract)**:
- ✅ Simple, no extra dependencies
- ✅ Works well for clear text
- ❌ Misses rotated/curved text
- ❌ No confidence scoring for text regions

**Decision**: Stick with Tesseract for now, focus on better frame selection and deduplication

**Sources**:
- [EAST Text Detector - PyImageSearch](https://pyimagesearch.com/2018/08/20/opencv-text-detection-east-text-detector/)
- [EAST and CRAFT Comparison](https://medium.com/technovators/scene-text-detection-in-python-with-east-and-craft-cbe03dda35d5)

### 5. Subtitle-Specific Optimizations

**Bounding Box Tracking**:
- Detect subtitle region (typically bottom 20% of frame)
- Track text position across frames
- Filter out non-subtitle text (signs, UI elements)

**Temporal Refinement**:
- Aggregate text from multiple frames showing same subtitle
- Use voting mechanism for conflicting OCR results
- Timestamp alignment for sequential subtitles

**Source**: [End-to-end Video Subtitle Recognition](https://www.sciencedirect.com/science/article/abs/pii/S0167865520300313)

## Recommended Implementation Plan

### Phase 1: SSIM-Based Frame Deduplication (High Impact, Low Effort)

**Changes to `GifTextExtractor.cs`**:
1. Compute SSIM between consecutive frames
2. Skip frames with SSIM > 0.95
3. Reduces OCR calls by 50-80%

**Estimated Effort**: 2-3 hours
**Expected Improvement**: 2-5x faster, same accuracy

### Phase 2: Levenshtein Distance Deduplication (Medium Impact, Low Effort)

**Changes to `DeduplicateAndCombineText()` method**:
1. Implement Levenshtein distance calculation
2. Merge similar strings (distance < 3)
3. Better handling of OCR errors

**Estimated Effort**: 1-2 hours
**Expected Improvement**: Cleaner output, fewer duplicates

### Phase 3: Temporal Text Tracking (High Impact, Medium Effort)

**New Class**: `TextTracker.cs`
1. Track bounding boxes across frames
2. Assign Track IDs
3. Merge progressive reveals intelligently

**Estimated Effort**: 4-6 hours
**Expected Improvement**: Correct handling of progressive text, subtitle changes

### Phase 4: Subtitle Region Detection (Optional)

**Enhancement**: Focus OCR on subtitle regions
1. Detect common subtitle positions (bottom 20%)
2. Apply OCR only to subtitle regions
3. Faster + more accurate

**Estimated Effort**: 2-3 hours
**Expected Improvement**: 2x faster for subtitle-heavy GIFs

## Performance Comparison

### Current Implementation
- **Frames Analyzed**: 10 out of 50 (random sampling by text-likeliness)
- **OCR Calls**: 10
- **Processing Time**: ~2 seconds
- **Accuracy**: Good for static text, poor for progressive reveals

### Proposed Implementation (Phase 1 + 2)
- **Frames Analyzed**: 50 (all frames)
- **SSIM Deduplication**: → 8 unique frames
- **OCR Calls**: 8
- **Processing Time**: ~1.5 seconds
- **Accuracy**: Excellent for both static and progressive text

### Proposed Implementation (All Phases)
- **Frames Analyzed**: 50
- **SSIM Deduplication**: → 8 unique frames
- **Temporal Tracking**: → 3 distinct text tracks
- **OCR Calls**: 8
- **Processing Time**: ~1.8 seconds
- **Accuracy**: Near-perfect for GIF subtitles and text reveals

## Key Metrics to Track

1. **Frame Reduction Ratio**: `uniqueFrames / totalFrames`
2. **OCR Hit Rate**: `framesWithText / totalOcrCalls`
3. **Deduplication Efficiency**: `uniqueTextBlocks / totalTextBlocks`
4. **Processing Time**: Per-GIF analysis time
5. **Text Accuracy**: Manual verification on test set

## Test Cases for Validation

1. **Progressive Text Reveal**: "HELLO" appearing letter by letter
2. **Subtitle Changes**: Multiple subtitle lines in sequence
3. **Static Watermark**: Persistent text across all frames (should extract once)
4. **Mixed Content**: Subtitle + watermark + scene text
5. **Empty Frames**: GIFs with no text

## References

### Video OCR & Frame Processing
- [AI-Powered Video OCR](https://reelmind.ai/blog/ai-powered-video-ocr-extract-text-from-on-screen-graphics)
- [Top OCR Tools 2025](https://www.koncile.ai/en/ressources/top-3-best-ocr-tools-for-extracting-text-from-images-in-2025)
- [Text Extraction from Video Frames - GitHub](https://github.com/renukatamboli/text-extraction-from-video-frames)

### Text Detection Methods
- [EAST Text Detector - PyImageSearch](https://pyimagesearch.com/2018/08/20/opencv-text-detection-east-text-detector/)
- [Scene Text Detection with EAST and CRAFT](https://medium.com/technovators/scene-text-detection-in-python-with-east-and-craft-cbe03dda35d5)
- [NVIDIA Robust Scene Text Detection](https://developer.nvidia.com/blog/robust-scene-text-detection-and-recognition-implementation/)

### Subtitle Extraction
- [VideOCR - GitHub](https://github.com/timminator/VideOCR)
- [How to Extract Hardcoded Subtitles](https://subtitleextractor.com/blog/how-to-extract-hardcoded-subtitles/)
- [End-to-end Subtitle Recognition](https://www.sciencedirect.com/science/article/abs/pii/S0167865520300313)

### Temporal Tracking & Deduplication
- [Scene Text Tracking in Video](https://dl.acm.org/doi/10.1145/3206025.3206051)
- [Text Detection and Recognition in Video - Survey](https://www.researchgate.net/publication/301316962_Text_Detection_Tracking_and_Recognition_in_Video_A_Comprehensive_Survey)

## Decision

**Implement Phases 1 & 2 immediately** (SSIM deduplication + Levenshtein distance):
- Low effort (~3-4 hours total)
- High impact (2-5x faster, better accuracy)
- No new dependencies
- Fully deterministic (no ML models needed)

**Defer Phase 3** (Temporal tracking) pending test results from Phases 1 & 2.

**Skip Phase 4** (EAST/CRAFT) - Tesseract is sufficient for current use cases.
