# GIF/WebP Motion Analysis with OpenCV

Automated motion detection for animated images using dense optical flow analysis.

## Overview

The GIF Motion Analyzer uses OpenCV's Farneback dense optical flow algorithm to detect and characterize motion in animated GIF and WebP images. Unlike sparse optical flow methods (like Lucas-Kanade) that track specific feature points, Farneback computes motion vectors for **every pixel** between consecutive frames, providing comprehensive motion field coverage.

### Key Features

- **Dense Optical Flow**: Analyzes motion across the entire frame, not just feature points
- **8-Directional Classification**: Identifies motion as right, left, up, down, or diagonal (up-right, up-left, down-right, down-left)
- **Motion Magnitude**: Measures average pixel displacement per frame
- **Confidence Scoring**: Calculates consistency of motion direction across frames
- **Automatic Integration**: Seamlessly integrated into image analysis pipeline with signal-based caching
- **Multi-Format Support**: Works with both GIF and WebP animated images

## How It Works

### 1. Frame Extraction

The analyzer extracts frames from the animated image using ImageSharp:

```csharp
using var gifImage = await Image.LoadAsync<Rgba32>(gifPath, ct);
var frameCount = gifImage.Frames.Count;
var frameDelay = gifMetadata.RepeatCount > 0 ?
    (gifImage.Frames.RootFrame.Metadata.GetGifMetadata()?.FrameDelay ?? 10) * 10 : 100;
```

- Limits to 50 frames maximum to prevent memory issues with large GIFs
- Samples frames uniformly if total count exceeds limit
- Converts each frame to grayscale for efficient processing

### 2. Grayscale Conversion

Frames are converted from RGBA to grayscale using standard luminance formula:

```csharp
var gray = (byte)(0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B);
```

This reduces computational complexity while preserving motion information, as optical flow algorithms work on intensity gradients rather than color.

### 3. OpenCV Mat Creation

ImageSharp frames are converted to OpenCV Mat objects:

```csharp
var mat = Mat.FromPixelData(height, width, MatType.CV_8UC1, grayscale);
```

This allows us to leverage OpenCV's optimized C++ implementation of optical flow algorithms.

### 4. Farneback Dense Optical Flow

For each consecutive frame pair, we compute dense optical flow:

```csharp
Cv2.CalcOpticalFlowFarneback(
    prevFrame,
    nextFrame,
    flow,
    pyrScale: 0.5,      // Pyramid scale for multi-resolution analysis
    levels: 3,          // Number of pyramid levels (coarse-to-fine)
    winsize: 15,        // Averaging window size
    iterations: 3,      // Iterations at each pyramid level
    polyN: 5,           // Polynomial expansion degree
    polySigma: 1.2,     // Gaussian sigma for polynomial expansion
    flags: 0
);
```

**What this produces:**
- A 2-channel flow field (same dimensions as input frames)
- Each pixel contains (dx, dy) - horizontal and vertical motion vectors
- Vectors represent pixel displacement between frames

**Farneback Algorithm Details:**
- Uses polynomial expansion to approximate neighborhood of each pixel
- Operates on multi-scale pyramid for robustness to large motions
- Iteratively refines flow estimates at each pyramid level
- More accurate than Lucas-Kanade for dense motion fields
- Computationally intensive but provides complete motion coverage

### 5. Flow Field Analysis

For each flow field, we extract motion statistics:

```csharp
for (int y = 0; y < flow.Rows; y++)
{
    for (int x = 0; x < flow.Cols; x++)
    {
        var flowVec = flow.At<Vec2f>(y, x);
        var dx = flowVec.Item0;  // Horizontal motion
        var dy = flowVec.Item1;  // Vertical motion

        var magnitude = Math.Sqrt(dx * dx + dy * dy);

        if (magnitude > motionThreshold)  // Default: 2.0 pixels
        {
            totalHorizontal += dx;
            totalVertical += dy;
            totalMagnitude += magnitude;
            significantMotionPixels++;
        }
    }
}
```

**Motion Threshold:**
- Default: 2.0 pixels per frame
- Filters out camera shake and noise
- Only pixels with motion > threshold are counted as "significant motion"

### 6. Direction Determination

Motion direction is classified using vector angle:

```csharp
var angle = Math.Atan2(vertical, horizontal) * 180 / Math.PI;

// 8-directional classification
if (angle >= -22.5 && angle < 22.5) return "right";
else if (angle >= 22.5 && angle < 67.5) return "down-right";
else if (angle >= 67.5 && angle < 112.5) return "down";
// ... and so on
```

**Angle Ranges:**
- Right: -22.5° to 22.5°
- Down-right: 22.5° to 67.5°
- Down: 67.5° to 112.5°
- Down-left: 112.5° to 157.5°
- Left: 157.5° to -157.5° (wraps around)
- Up-left: -157.5° to -112.5°
- Up: -112.5° to -67.5°
- Up-right: -67.5° to -22.5°

### 7. Aggregation Across Frames

Motion data from all frame pairs is aggregated:

```csharp
var avgMagnitude = frameData.Average(f => f.Magnitude);
var maxMagnitude = frameData.Max(f => f.Magnitude);
var framesWithMotion = frameData.Count(f => f.Magnitude > motionThreshold);
var motionPercentage = (double)framesWithMotion / frameData.Count * 100;

// Determine dominant direction by voting
var dominantDirection = frameData
    .Where(f => f.Magnitude > motionThreshold)
    .GroupBy(f => f.Direction)
    .OrderByDescending(g => g.Count())
    .FirstOrDefault()?.Key ?? "static";

// Calculate confidence based on consistency
var confidence = framesWithMotion > 0
    ? directionCounts.First().Count / (double)framesWithMotion
    : 1.0;
```

**Confidence Calculation:**
- Measures how consistently frames agree on motion direction
- High confidence (>0.8): Most frames show same direction
- Low confidence (<0.5): Mixed motion directions
- 1.0 for static images (perfect agreement on no motion)

## Output Data Model

### GifMotionProfile

```csharp
public record GifMotionProfile
{
    // Frame metadata
    public int FrameCount { get; init; }
    public int FrameDelayMs { get; init; }
    public double Fps => FrameDelayMs > 0 ? 1000.0 / FrameDelayMs : 0;
    public int TotalDurationMs { get; init; }
    public bool Loops { get; init; }

    // Motion analysis
    public string MotionDirection { get; init; } = "static";
    public double MotionMagnitude { get; init; }
    public double MaxMotionMagnitude { get; init; }
    public double MotionPercentage { get; init; }
    public double Confidence { get; init; }

    // Optional detailed data
    public List<FrameMotionData>? FrameMotionData { get; init; }
    public List<MotionRegion>? MotionRegions { get; init; }
}
```

**Field Descriptions:**
- **FrameCount**: Total number of frames in the animation
- **FrameDelayMs**: Average delay between frames in milliseconds
- **Fps**: Calculated frames per second (1000 / FrameDelayMs)
- **TotalDurationMs**: Total animation duration (FrameCount × FrameDelayMs)
- **Loops**: Whether the animation loops infinitely
- **MotionDirection**: Dominant motion direction (8 directions + "static")
- **MotionMagnitude**: Average pixel displacement per frame
- **MaxMotionMagnitude**: Maximum observed motion magnitude
- **MotionPercentage**: Percentage of frames with significant motion (>threshold)
- **Confidence**: Directional consistency score (0.0-1.0)

### FrameMotionData

Per-frame motion details (optional, for detailed analysis):

```csharp
public record FrameMotionData
{
    public int FrameIndex { get; init; }
    public double Magnitude { get; init; }
    public string Direction { get; init; } = "static";
    public double HorizontalMotion { get; init; }
    public double VerticalMotion { get; init; }
}
```

### MotionRegion

Spatial regions with significant motion (future enhancement):

```csharp
public record MotionRegion
{
    public double X { get; init; }              // Normalized 0.0-1.0
    public double Y { get; init; }              // Normalized 0.0-1.0
    public double Width { get; init; }          // Normalized 0.0-1.0
    public double Height { get; init; }         // Normalized 0.0-1.0
    public double Magnitude { get; init; }
    public string Direction { get; init; } = "static";
}
```

## Usage

### Basic Analysis

```csharp
using var analyzer = new GifMotionAnalyzer(logger);
var profile = await analyzer.AnalyzeAsync("animated.gif");

Console.WriteLine($"Direction: {profile.MotionDirection}");
Console.WriteLine($"Magnitude: {profile.MotionMagnitude:F2} px/frame");
Console.WriteLine($"FPS: {profile.Fps:F1}");
Console.WriteLine($"Confidence: {profile.Confidence:P0}");
```

### Custom Configuration

```csharp
using var analyzer = new GifMotionAnalyzer(
    logger: logger,
    motionThreshold: 3.0,           // Higher threshold = less sensitive
    maxFramesToAnalyze: 100,        // Analyze more frames
    enableDetailedFrameData: true   // Include per-frame data
);

var profile = await analyzer.AnalyzeAsync("complex-animation.gif");

// Access per-frame data
foreach (var frame in profile.FrameMotionData!)
{
    Console.WriteLine($"Frame {frame.FrameIndex}: " +
        $"{frame.Direction} at {frame.Magnitude:F2} px/frame");
}
```

### Integration with EscalationService

The analyzer is automatically invoked for GIF and WebP formats:

```csharp
var result = await escalationService.AnalyzeWithEscalationAsync(
    imagePath: "animation.gif",
    useOcr: false,
    ct: cancellationToken
);

if (result.GifMotion != null)
{
    Console.WriteLine($"Detected {result.GifMotion.MotionDirection} motion");
    Console.WriteLine($"Speed: {result.GifMotion.MotionMagnitude:F2} px/frame");
}
```

### Signal-Based Caching

Motion data is automatically cached as signals:

```csharp
// Signals stored in SignalDatabase:
// - motion.direction (value: "right", "up-left", etc.)
// - motion.magnitude (value: 12.5)
// - motion.percentage (value: 87.3)

// Retrieve from cache
var cachedProfile = await signalDb.GetProfileAsync(sha256Hash);
var direction = cachedProfile.GetValue<string>("motion.direction");
var magnitude = cachedProfile.GetValue<double>("motion.magnitude");
```

## CLI Usage

### Batch Processing with Motion Data

```bash
# Analyze GIFs with table output (shows motion column)
lucidrag-image batch ./animations --pattern "**/*.gif" --format table

# JSON output with complete motion data
lucidrag-image batch ./animations --format json --output results.json

# Markdown report with motion statistics
lucidrag-image batch ./animations --format markdown --output report.md

# Filter by motion characteristics (future feature)
lucidrag-image batch ./animations --min-motion 10.0 --motion-direction "right"
```

### CLI Output Examples

**Table Format:**
```
┌─────────────────────────────────────────────────────────────────┐
│ Batch Analysis Summary                                          │
├─────────────────────────────┬───────────────────────────────────┤
│ Total Images                │ 25                                │
│ Successful                  │ 25                                │
│ Failed                      │ 0                                 │
│                                                                  │
│ Animated Images (GIF/WebP)                                      │
│   Count                     │ 15                                │
│   Avg Motion Magnitude      │ 8.42 px/frame                     │
│   Avg Frame Count           │ 24                                │
│   Avg FPS                   │ 12.5                              │
│     right                   │ 8                                 │
│     left                    │ 4                                 │
│     down                    │ 3                                 │
└─────────────────────────────┴───────────────────────────────────┘

┌──────────────────────┬─────────┬────────────┬──────────┬────────┬─────────┬────────┐
│ File                 │ Type    │ Dimensions │ Sharpness│ Text   │ Motion  │ Status │
├──────────────────────┼─────────┼────────────┼──────────┼────────┼─────────┼────────┤
│ loading-spinner.gif  │ Graphic │ 64x64      │ 450      │ 0.12   │ → 12.5  │ ✓      │
│ scroll-arrow.gif     │ Icon    │ 32x32      │ 320      │ 0.05   │ ↓ 8.2   │ ✓      │
│ slide-transition.gif │ Photo   │ 800x600    │ 1200     │ 0.03   │ ← 15.3  │ ✓      │
└──────────────────────┴─────────┴────────────┴──────────┴────────┴─────────┴────────┘
```

**JSON Format:**
```json
{
  "summary": {
    "totalImages": 25,
    "successful": 25,
    "failed": 0,
    "escalated": 0
  },
  "results": [
    {
      "filePath": "loading-spinner.gif",
      "success": true,
      "profile": {
        "detectedType": "Graphic",
        "width": 64,
        "height": 64,
        "laplacianVariance": 450.2
      },
      "gifMotion": {
        "frameCount": 12,
        "fps": 15.0,
        "motionDirection": "right",
        "motionMagnitude": 12.5,
        "motionPercentage": 91.7,
        "confidence": 0.92
      }
    }
  ]
}
```

## Performance Considerations

### Memory Usage

- **Frame Limiting**: Maximum 50 frames analyzed to prevent memory exhaustion
- **Grayscale Conversion**: Reduces memory by 75% (1 channel vs 4 channels RGBA)
- **Mat Disposal**: All OpenCV Mat objects are properly disposed after use

### Processing Speed

Approximate performance on typical hardware (Intel i7, 16GB RAM):

| Image Size | Frame Count | Processing Time |
|------------|-------------|-----------------|
| 64x64      | 12 frames   | ~50ms           |
| 256x256    | 24 frames   | ~200ms          |
| 512x512    | 30 frames   | ~800ms          |
| 1024x1024  | 50 frames   | ~3000ms         |

**Optimization Tips:**
- Lower `maxFramesToAnalyze` for faster processing
- Increase `motionThreshold` to reduce computation on low-motion pixels
- Process in parallel batches for multiple files

### Accuracy vs Speed Tradeoffs

**Farneback Parameters:**

```csharp
// Fast but less accurate
pyrScale: 0.5,    // Fewer pyramid levels
levels: 2,        // Coarser resolution
winsize: 10,      // Smaller averaging window
iterations: 2     // Fewer refinements

// Slow but more accurate
pyrScale: 0.5,
levels: 5,        // More pyramid levels
winsize: 21,      // Larger averaging window
iterations: 5     // More refinements
```

**Frame Sampling:**
- Analyze every frame: Most accurate, slowest
- Analyze every 2nd frame: ~50% faster, still accurate for most cases
- Analyze every 5th frame: Fast, may miss subtle motion

## Technical Details

### Optical Flow Background

**What is Optical Flow?**
Optical flow is the pattern of apparent motion of objects in a visual scene based on their brightness patterns. It makes two key assumptions:

1. **Brightness Constancy**: Pixel intensity remains constant between frames
2. **Spatial Coherence**: Neighboring pixels move similarly

**Farneback Method:**
- Approximates neighborhood of each pixel with polynomial expansion
- Computes coefficients for displaced neighborhoods
- Uses multi-scale pyramid for large displacements
- More robust than differential methods (Lucas-Kanade) for dense fields

**Mathematical Foundation:**
For each pixel, the algorithm solves:
```
I(x, y, t) = I(x + dx, y + dy, t + dt)
```

Where:
- I(x, y, t) = pixel intensity at position (x,y) and time t
- (dx, dy) = displacement vector (what we want to find)

### Why Dense Optical Flow?

**Dense vs Sparse:**

| Feature | Dense (Farneback) | Sparse (Lucas-Kanade) |
|---------|-------------------|----------------------|
| Coverage | Every pixel | Selected features only |
| Computation | Slower | Faster |
| Motion Field | Complete | Partial |
| Best For | Analyzing overall motion patterns | Tracking specific objects |

**For GIF analysis, dense flow is better because:**
- We want to characterize overall animation direction
- No need to track specific objects
- Complete coverage ensures we don't miss motion
- Can calculate aggregate statistics across entire frame

### OpenCV Dependencies

**Required Packages:**
```xml
<PackageReference Include="OpenCvSharp4" Version="4.11.0.20250506" />
<PackageReference Include="OpenCvSharp4.runtime.win" Version="4.11.0.20250506" />
```

**Platform Support:**
- Windows: `OpenCvSharp4.runtime.win`
- Linux: `OpenCvSharp4.runtime.linux`
- macOS: `OpenCvSharp4.runtime.osx`

**Native Dependencies:**
OpenCvSharp4 bundles native OpenCV libraries (opencv_world411.dll on Windows). No separate OpenCV installation required.

## Example Use Cases

### 1. Loading Spinner Detection

Identify circular/rotating animations:

```csharp
var profile = await analyzer.AnalyzeAsync("spinner.gif");

// Spinners typically have:
// - High motion percentage (most frames moving)
// - No dominant direction (rotational motion averages out)
// - Consistent magnitude

if (profile.MotionPercentage > 80 && profile.Confidence < 0.4)
{
    Console.WriteLine("Likely a loading spinner or rotation animation");
}
```

### 2. Scroll Arrow Classification

Detect directional indicators:

```csharp
var profile = await analyzer.AnalyzeAsync("arrow.gif");

// Scroll arrows typically have:
// - High directional confidence
// - Consistent motion in one direction

if (profile.Confidence > 0.8 && new[] {"up", "down", "left", "right"}.Contains(profile.MotionDirection))
{
    Console.WriteLine($"Directional indicator pointing {profile.MotionDirection}");
}
```

### 3. Animation Speed Analysis

Categorize animations by speed:

```csharp
var profile = await analyzer.AnalyzeAsync("animation.gif");

var speedCategory = profile.MotionMagnitude switch
{
    < 3.0 => "Subtle",
    < 8.0 => "Moderate",
    < 15.0 => "Fast",
    _ => "Very Fast"
};

Console.WriteLine($"Animation speed: {speedCategory} ({profile.MotionMagnitude:F1} px/frame)");
```

### 4. Static vs Animated Detection

Quickly determine if GIF is actually animated:

```csharp
var profile = await analyzer.AnalyzeAsync("maybe-static.gif");

if (profile.FrameCount <= 1 || profile.MotionMagnitude < 1.0)
{
    Console.WriteLine("Static image (no significant motion)");
}
else
{
    Console.WriteLine($"Animated: {profile.FrameCount} frames, {profile.Fps:F1} fps");
}
```

## Limitations and Known Issues

### Current Limitations

1. **No Region-Based Analysis**: Currently analyzes global motion only. Future versions will support `MotionRegion` for localized motion detection.

2. **Rotational Motion**: Pure rotational motion (like spinners) may show low confidence as vectors cancel out when averaged.

3. **Scene Changes**: Sudden scene changes (cuts) register as very high motion magnitude and may skew results.

4. **Transparency**: Alpha channel is ignored during grayscale conversion. May affect results for GIFs with significant transparency.

5. **Compression Artifacts**: Heavily compressed GIFs may have noise that affects low-magnitude motion detection.

### Future Enhancements

- **Motion Region Detection**: Identify which parts of the frame are moving
- **Rotational Motion Detection**: Specific algorithm for circular motion patterns
- **Scene Cut Detection**: Filter out frame transitions vs actual motion
- **Multi-Object Tracking**: Track multiple independent motion patterns
- **Video Format Support**: Extend to MP4, WebM, AVI (requires different frame extraction)

## Testing

### Unit Tests

```csharp
[Fact]
public void GifMotionProfile_FpsCalculation_IsCorrect()
{
    var profile = new GifMotionProfile
    {
        FrameDelayMs = 100  // 100ms delay
    };

    profile.Fps.Should().BeApproximately(10.0, 0.01); // 1000/100 = 10 FPS
}

[Fact]
public void MotionRegion_Properties_SetCorrectly()
{
    var region = new MotionRegion
    {
        X = 0.25,
        Y = 0.30,
        Width = 0.50,
        Height = 0.40,
        Magnitude = 12.5,
        Direction = "down-right"
    };

    region.Magnitude.Should().BeApproximately(12.5, 0.01);
    region.Direction.Should().Be("down-right");
}
```

### Integration Tests

See `LucidRAG.ImageCli.Tests/GifMotionAnalyzerTests.cs` for full test suite.

### Manual Testing

Create test GIFs with known motion patterns:

```bash
# Test with various GIF types
lucidrag-image analyze ./test-gifs/spinner.gif
lucidrag-image analyze ./test-gifs/scroll-down.gif
lucidrag-image analyze ./test-gifs/slide-left.gif
lucidrag-image analyze ./test-gifs/static.gif
```

## References

### OpenCV Documentation

- [Optical Flow Tutorial](https://docs.opencv.org/4.x/d4/dee/tutorial_optical_flow.html)
- [calcOpticalFlowFarneback API](https://docs.opencv.org/4.x/dc/d6b/group__video__track.html#ga5d10ebbd59fe09c5f650289ec0ece5af)
- [Farneback Algorithm Paper](https://www.diva-portal.org/smash/get/diva2:273847/FULLTEXT01.pdf) (Gunnar Farneback, 2003)

### ImageSharp Documentation

- [Image.Load API](https://docs.sixlabors.com/api/ImageSharp/SixLabors.ImageSharp.Image.html)
- [GIF Metadata](https://docs.sixlabors.com/api/ImageSharp/SixLabors.ImageSharp.Metadata.Formats.Gif.GifMetadata.html)

### Related Projects

- [OpenCvSharp GitHub](https://github.com/shimat/opencvsharp)
- [ImageSharp GitHub](https://github.com/SixLabors/ImageSharp)

## License

This implementation is part of the LucidRAG project and follows the same MIT license.

---

**Last Updated**: 2026-01-04
**OpenCV Version**: 4.11.0
**Author**: LucidRAG Team
