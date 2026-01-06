# Motion Analysis

## Overview

The library provides comprehensive motion analysis for animated GIFs:

- Frame extraction and sampling
- Optical flow analysis
- Motion intensity classification
- Keyframe extraction

## Motion Signals

| Signal | Type | Description |
|--------|------|-------------|
| `motion.is_animated` | bool | Whether image has multiple frames |
| `motion.frame_count` | int | Total number of frames |
| `motion.duration` | double | Total duration in seconds |
| `motion.motion_intensity` | double | 0.0-1.0 intensity score |
| `motion.motion_type` | string | "static", "subtle", "moderate", "dynamic" |
| `motion.loop_detected` | bool | Whether animation loops |

## Motion Types

| Type | Intensity | Example |
|------|-----------|---------|
| `static` | < 0.1 | Still image with minor variations |
| `subtle` | 0.1-0.3 | Blinking, breathing |
| `moderate` | 0.3-0.6 | Walking, gestures |
| `dynamic` | > 0.6 | Action, fast movement |

## Keyframe Extraction

Extract representative frames for motion understanding:

```bash
# Extract 6 keyframes showing motion progression
imagesummarizer export-strip animation.gif --mode motion --max-frames 6
```

### Keyframe Selection Algorithm

1. Calculate optical flow between consecutive frames
2. Identify frames with highest motion delta
3. Ensure temporal distribution (don't cluster)
4. Include first and last frames

## Text-Change Detection

For GIFs with subtitles, detect frames where text changes:

```bash
# Extract only frames where text changed
imagesummarizer export-strip subtitle.gif --mode ocr
```

### Detection Method

1. Focus on subtitle region (bottom 30%)
2. Threshold for bright pixels (text)
3. Compare Jaccard similarity of text pixels
4. New segment when similarity < 85%

## CLI Strip Modes

| Mode | Purpose | Output |
|------|---------|--------|
| `auto` | Smart selection | Best for image type |
| `ocr` | Text changes | Deduplicated text frames |
| `motion` | Movement | Keyframes showing motion |
| `text-only` | Bounding boxes | Just text regions, compact |

## Example: Motion Strip

**Input**: Cat wagging tail (93 frames)
**Output**: 6 keyframes showing motion progression

```bash
imagesummarizer export-strip cat.gif --mode motion --max-frames 6
# Output: 6 keyframes, dimensions 3000×280
```

## Example: Text-Only Strip

**Input**: Meme GIF with two captions (93 frames)
**Output**: Compact strip with just text regions

```bash
imagesummarizer export-strip meme.gif --mode text-only
# Output: 2 segments → 2 regions, dimensions 253×105
```

## API Usage

```csharp
// Get motion analysis
var profile = await analyzer.AnalyzeAsync("animation.gif");

Console.WriteLine($"Frames: {profile.Motion.FrameCount}");
Console.WriteLine($"Duration: {profile.Motion.Duration}s");
Console.WriteLine($"Motion: {profile.Motion.MotionType}");
Console.WriteLine($"Intensity: {profile.Motion.MotionIntensity:P0}");
```

## Configuration

```json
{
  "DocSummarizer": {
    "Motion": {
      "MaxFramesToAnalyze": 30,
      "OpticalFlowEnabled": true,
      "KeyframeExtractionEnabled": true
    }
  }
}
```
