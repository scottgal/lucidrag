# GIF Support & Advanced Features Plan

**Date:** 2026-01-04
**Status:** ✅ Cache Fixed | ⚠️ GIF Support Needed | 🎯 Console Animation (Stretch Goal)

## Current Status - What Works ✅

### 1. **Batch Processing with GIFs**
- ✅ Analyzing 555 GIF files from F:\Gifs
- ✅ Parallel processing (8 workers)
- ✅ Progress tracking and reporting
- ✅ Auto-escalation to vision LLM
- ✅ Cache hits working (e.g., bboy-bear.gif, dumb-dumber-dance.gif)

### 2. **Cache Deserialization - FIXED** ✅
**Bug:** Cached data returned all zeros
**Root Cause:** JsonSerializer.Deserialize<object>() returns JsonElement, not actual types
**Fix Applied:**
```csharp
// DynamicImageProfile.GetValue<T>() now handles:
// 1. Direct type match
// 2. JsonElement deserialization from database
// 3. Type conversion with Convert.ChangeType
```
**Verified:** Cache now returns real data (Dimensions: 100x80, Sharpness: 2856)

### 3. **Features Implemented**
- ✅ Content-based caching (xxhash64 + SHA256)
- ✅ Ordering/sorting by 8 properties (sharpness, resolution, color, brightness, saturation, type, text-score)
- ✅ JSON-LD export with schema.org mappings
- ✅ 555 GIF batch test running successfully

## Issues Discovered ❌

### 1. **Database Concurrency Bug** - P0 CRITICAL
**Error:**
```
System.ArgumentOutOfRangeException: Index was out of range
at Microsoft.Data.Sqlite.SqliteConnection.RemoveCommand(SqliteCommand command)
at LucidRAG.ImageCli.Services.EscalationService.AnalyzeWithEscalationAsync
```

**Cause:** SQLite connection command list corruption under concurrent access

**Fix Needed:**
```csharp
// Option 1: Lock around LoadProfileAsync
private static readonly SemaphoreSlim _dbLock = new(1, 1);

public async Task<DynamicImageProfile?> LoadProfileAsync(string sha256, CancellationToken ct)
{
    await _dbLock.WaitAsync(ct);
    try
    {
        // existing code
    }
    finally
    {
        _dbLock.Release();
    }
}

// Option 2: Connection pooling with WAL mode
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
```

### 2. **GIF Analysis Limitations** - Currently Only First Frame

**What's Missing:**
- Frame count detection
- Animation metadata (duration, FPS, loop count)
- Multi-frame analysis
- Keyframe selection
- Per-frame OCR
- Per-frame captions

**Current Behavior:**
```bash
# GIF analyzed as static image
icon.png: Diagram, 100x80, Sharpness: 2856

# Should be:
dance.gif: Animation, 480x270, 24 frames @ 15fps (1.6s),
           Keyframes analyzed: [0, 12, 23]
           Text detected in frames: 5, 12 ("BOOM", "DANCE")
```

## GIF-Specific Features to Implement

### Feature 1: GIF Metadata Extraction

**Implementation:**
```csharp
public record GifMetadata
{
    public int FrameCount { get; init; }
    public TimeSpan Duration { get; init; }
    public double Fps { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int LoopCount { get; init; } // 0 = infinite
    public bool IsAnimated => FrameCount > 1;
}

// Using SixLabors.ImageSharp
using var gif = await Image.LoadAsync<Rgba32>(gifPath, ct);
var gifMetadata = gif.Metadata.GetGifMetadata();

return new GifMetadata
{
    FrameCount = gif.Frames.Count,
    Duration = CalculateDuration(gif),
    Fps = CalculateFps(gif),
    LoopCount = gifMetadata?.RepeatCount ?? 1,
    Width = gif.Width,
    Height = gif.Height
};
```

**Storage in JSON-LD:**
```json
{
  "@context": {
    "gifMetadata": "lucidrag:gifMetadata",
    "frameCount": "lucidrag:frameCount",
    "duration": "schema:duration",
    "fps": "lucidrag:framesPerSecond"
  },
  "gifMetadata": {
    "isAnimated": true,
    "frameCount": 24,
    "duration": "PT1.6S",
    "fps": 15,
    "loopCount": 0
  }
}
```

### Feature 2: Frame Extraction Strategies

**Strategy 1: All Frames** (for short GIFs, <10 frames)
```csharp
for (int i = 0; i < gif.Frames.Count; i++)
{
    var frame = gif.Frames.CloneFrame(i);
    var framePath = $"{tempDir}/frame_{i:D4}.png";
    await frame.SaveAsPngAsync(framePath, ct);

    // Analyze each frame
    var profile = await _imageAnalyzer.AnalyzeAsync(framePath, ct);
    frameProfiles.Add(new FrameAnalysis(i, profile));
}
```

**Strategy 2: Keyframe Sampling** (for long GIFs)
```csharp
// Sample every Nth frame or significant changes
var keyframes = SelectKeyframes(gif, maxFrames: 10);
// Keyframes: First, Last, + frames with significant pixel difference

foreach (var frameIndex in keyframes)
{
    var frame = gif.Frames.CloneFrame(frameIndex);
    // Analyze keyframe
}
```

**Strategy 3: Motion-Based Sampling**
```csharp
// Detect frames with significant motion/change
var previousFrame = gif.Frames[0];
for (int i = 1; i < gif.Frames.Count; i++)
{
    var currentFrame = gif.Frames[i];
    var difference = CalculatePixelDifference(previousFrame, currentFrame);

    if (difference > threshold)
    {
        keyframes.Add(i); // Significant change detected
    }
    previousFrame = currentFrame;
}
```

### Feature 3: Per-Frame OCR

**Use Case:** Meme GIFs with text appearing/changing across frames

**Implementation:**
```csharp
public record FrameTextAnalysis
{
    public int FrameNumber { get; init; }
    public List<OcrTextRegion> TextRegions { get; init; }
    public string ExtractedText { get; init; }
    public double Confidence { get; init; }
}

// Analyze text across frames
var frameTexts = new List<FrameTextAnalysis>();
foreach (var frameIndex in keyframes)
{
    var framePath = ExtractFrame(gif, frameIndex);
    var textRegions = await _ocrEngine.ExtractTextWithCoordinates(framePath);

    frameTexts.Add(new FrameTextAnalysis
    {
        FrameNumber = frameIndex,
        TextRegions = textRegions,
        ExtractedText = string.Join(" ", textRegions.Select(r => r.Text)),
        Confidence = textRegions.Average(r => r.Confidence)
    });
}

// Deduplicate: "BOOM" in frames 5-10 → single entry
var uniqueTexts = DeduplicateConsecutiveText(frameTexts);
```

**JSON-LD Output:**
```json
{
  "frameTextSequence": [
    {
      "@type": "FrameText",
      "frames": [0, 1, 2, 3, 4],
      "text": "PREPARING..."
    },
    {
      "@type": "FrameText",
      "frames": [5, 6, 7, 8, 9, 10],
      "text": "BOOM!"
    },
    {
      "@type": "FrameText",
      "frames": [11, 12],
      "text": "EPIC FAIL"
    }
  ]
}
```

### Feature 4: Per-Frame Vision LLM Captions

**Use Case:** Understanding narrative/story across frames

**Implementation:**
```csharp
public record FrameCaptionSequence
{
    public List<FrameCaption> Captions { get; init; }
    public string NarrativeSummary { get; init; }
}

public record FrameCaption
{
    public int FrameNumber { get; init; }
    public string Caption { get; init; }
    public double Confidence { get; init; }
}

// Analyze keyframes with vision LLM
var captions = new List<FrameCaption>();
foreach (var frameIndex in keyframes)
{
    var framePath = ExtractFrame(gif, frameIndex);
    var caption = await _visionLlmService.GenerateDescriptionAsync(framePath, ct);

    captions.Add(new FrameCaption
    {
        FrameNumber = frameIndex,
        Caption = caption,
        Confidence = 0.8
    });
}

// Generate narrative summary
var narrative = await _visionLlmService.SummarizeSequenceAsync(captions, ct);
// "A person waves, then jumps, and lands with arms raised in triumph"

return new FrameCaptionSequence
{
    Captions = captions,
    NarrativeSummary = narrative
};
```

### Feature 5: Animated Console Playback 🎯

**AWESOME IDEA!** Animated GIF playback in terminal using ANSI escape codes.

**Technical Approach:**

**Method 1: Block Characters** (Fast, works everywhere)
```csharp
public async Task PlayGifInConsole(string gifPath, int width = 80, int height = 40)
{
    using var gif = await Image.LoadAsync<Rgba32>(gifPath);

    while (true) // Loop animation
    {
        for (int frameIndex = 0; frameIndex < gif.Frames.Count; frameIndex++)
        {
            var frame = gif.Frames.CloneFrame(frameIndex);
            var resized = frame.Clone(x => x.Resize(width, height));

            // Move cursor to top-left
            Console.SetCursorPosition(0, 0);

            // Render frame with ANSI color codes
            for (int y = 0; y < height; y += 2) // 2 rows = 1 block
            {
                for (int x = 0; x < width; x++)
                {
                    var topPixel = resized[x, y];
                    var bottomPixel = y + 1 < height ? resized[x, y + 1] : topPixel;

                    // Use ▀ (upper half block) with foreground/background colors
                    Console.Write($"\x1b[38;2;{topPixel.R};{topPixel.G};{topPixel.B}m" +
                                  $"\x1b[48;2;{bottomPixel.R};{bottomPixel.G};{bottomPixel.B}m▀");
                }
                Console.WriteLine("\x1b[0m"); // Reset colors
            }

            // Frame delay based on GIF metadata
            var delay = gif.Frames.RootFrame.Metadata.GetGifMetadata()?.FrameDelay ?? 100;
            await Task.Delay(delay * 10, ct); // GIF delay is in 1/100th seconds
        }
    }
}
```

**Method 2: Sixel Graphics** (High quality, limited terminal support)
```csharp
// Requires terminal with Sixel support (iTerm2, mlterm, etc.)
// Convert frame to Sixel format
var sixelData = ConvertToSixel(frame);
Console.Write(sixelData);
```

**Method 3: Kitty Graphics Protocol** (Best quality, Kitty terminal only)
```csharp
// Send frame as base64-encoded PNG
var pngData = Convert.ToBase64String(framePngBytes);
Console.Write($"\x1b_Ga=T,f=100;{pngData}\x1b\\");
```

**Command Example:**
```bash
lucidrag-image preview dance.gif --animate --fps 10 --loop 3

# Output:
# [Animated GIF playing in console with color blocks]
# Press Ctrl+C to stop
```

**Features:**
- Adjustable FPS (slower/faster playback)
- Loop count control
- Pause/resume (Spacebar)
- Frame-by-frame step (Arrow keys)
- Export current frame (S key)

## Implementation Plan

### Phase 1: Fix Critical Bugs (Day 1) - P0
1. ✅ Fix cache deserialization (DONE)
2. ⬜ Fix SignalDatabase concurrency bug
   - Add connection pooling or locking
   - Enable WAL mode for better concurrency
   - Add unit tests for concurrent access

### Phase 2: GIF Metadata (Day 2) - P1
3. ⬜ Add GIF metadata extraction
   - Frame count, duration, FPS, loop count
   - Store in ImageProfile as GifMetadata property
   - Include in JSON-LD export
4. ⬜ Add GIF detection to ImageAnalyzer
   - Detect animated vs static GIFs
   - Add isAnimated flag

### Phase 3: Frame Extraction (Day 3) - P1
5. ⬜ Implement keyframe selection algorithms
   - All frames (for short GIFs)
   - Evenly spaced sampling
   - Motion-based keyframe detection
6. ⬜ Add frame extraction service
   - Extract frames to temp directory
   - Clean up temp files after analysis

### Phase 4: Per-Frame Analysis (Day 4) - P2
7. ⬜ Per-frame OCR with deduplication
   - Extract text from each keyframe
   - Deduplicate consecutive identical text
   - Track which frames contain which text
8. ⬜ Per-frame vision LLM captions
   - Caption each keyframe
   - Generate narrative summary
   - Detect scene changes

### Phase 5: Console Animation (Day 5) - P3 (Stretch Goal)
9. ⬜ Basic console animation with block characters
   - ANSI color codes for true color
   - Frame timing based on GIF metadata
   - Loop control
10. ⬜ Advanced playback controls
    - Pause/resume
    - Frame-by-frame stepping
    - Speed control
    - Export frame

### Phase 6: Testing & Documentation (Day 6)
11. ⬜ Create test project with GIF tests
12. ⬜ Add integration tests for frame extraction
13. ⬜ Add performance tests for large GIFs
14. ⬜ Update documentation with GIF examples

## Test Cases Needed

### Unit Tests
```csharp
// GIF Metadata Tests
- Test_ExtractGifMetadata_AnimatedGif_ReturnsCorrectFrameCount()
- Test_ExtractGifMetadata_StaticGif_FrameCountIsOne()
- Test_ExtractGifMetadata_CalculatesDurationCorrectly()
- Test_ExtractGifMetadata_CalculatesFpsCorrectly()

// Frame Extraction Tests
- Test_SelectKeyframes_ShortGif_ReturnsAllFrames()
- Test_SelectKeyframes_LongGif_SamplesEvenly()
- Test_SelectKeyframes_MotionBased_DetectsSceneChanges()
- Test_ExtractFrame_ValidIndex_SavesPngFile()

// Per-Frame OCR Tests
- Test_AnalyzeFrameText_MemeGif_ExtractsTextFromAllFrames()
- Test_DeduplicateConsecutiveText_IdenticalFrames_MergesEntries()
- Test_FrameTextSequence_ExportsToJsonLd()

// Console Animation Tests
- Test_RenderFrame_BlockCharacters_OutputsAnsiCodes()
- Test_CalculateFrameDelay_UsesGifMetadata()
- Test_PlayAnimation_LoopCount_StopsAfterN()
```

### Integration Tests
```csharp
// End-to-End GIF Processing
- Test_E2E_AnalyzeAnimatedGif_ExtractsAllMetadata()
- Test_E2E_BatchProcessGifs_HandlesAllFormats()
- Test_E2E_GifWithText_ExtractsPerFrameOcr()
- Test_E2E_GifAnimation_PlaysInConsole()
```

## Example Output

### Current (Static Analysis):
```
dance.gif: Diagram, 480x270, Sharpness: 1234, Text Score: 0.45
```

### Future (Full GIF Analysis):
```
dance.gif: Animation
  Metadata:  24 frames @ 15fps (1.6s), loops: infinite
  Keyframes: Analyzed frames 0, 8, 16, 23

  Frame Analysis:
    Frame 0:  "Person standing still"
    Frame 8:  "Person jumping up"
    Frame 16: "Person spinning mid-air"
    Frame 23: "Person landing with arms raised"

  Text Detected:
    Frames 0-5:   "GET READY..."
    Frames 10-15: "BOOM!"
    Frames 20-23: "EPIC!"

  Narrative: "A person prepares, jumps dramatically with an explosion effect, and celebrates landing"
```

### Console Animation:
```
$ lucidrag-image preview dance.gif --animate --width 60 --height 30

[Animated GIF playing with colored blocks]
█████████████████████████████████████
████░░░░░░░░░░░██████░░░░░░░░░░░████
███░░░▄███▄░░░░░░░░░░░░░▄███▄░░░███
[...animation continues...]

Frame: 12/24 | FPS: 15 | Loop: 1/∞
[Space] Pause | [←→] Step | [S] Save | [Q] Quit
```

## Priority Matrix

**P0 - Critical (Must Fix):**
- Fix SignalDatabase concurrency bug
- Ensure batch processing stability

**P1 - High (Core Features):**
- GIF metadata extraction
- Frame extraction with keyframe selection
- Per-frame OCR

**P2 - Medium (Enhanced Features):**
- Per-frame vision LLM captions
- Narrative summary generation
- Motion-based keyframe selection

**P3 - Low (Nice to Have):**
- Animated console playback
- Advanced playback controls
- Sixel/Kitty graphics protocol support

## Performance Considerations

### GIF with 100 frames:
- **All frames:** 100 OCR calls + 100 vision LLM calls = ~8 minutes
- **Keyframes (10):** 10 OCR calls + 10 vision LLM calls = ~48 seconds
- **Smart sampling (5):** 5 OCR calls + 5 vision LLM calls = ~24 seconds

**Recommendation:** Default to 10 keyframes max, with user option to analyze all frames

### Memory Usage:
- Loading full GIF into memory: ~10-50MB for typical GIF
- Frame extraction: 1-5MB per extracted PNG frame
- **Solution:** Extract frames on-demand, process, delete immediately

## Next Steps

1. ✅ Complete test review document (DONE - see IMAGECLI_TEST_REVIEW.md)
2. ⬜ Fix SignalDatabase concurrency bug (IN PROGRESS)
3. ⬜ Wait for user confirmation on which GIF features to prioritize
4. ⬜ Implement Phase 1: GIF metadata extraction
5. ⬜ Implement Phase 2: Frame extraction and keyframe selection
6. ⬜ Create comprehensive test suite

## Questions for User

1. **GIF Analysis Depth:** Analyze all frames or just keyframes by default?
2. **Console Animation Priority:** Is this a must-have or nice-to-have?
3. **Vision LLM Costs:** Per-frame captioning could be expensive - limit to N frames?
4. **Storage:** Store all frame analyses or just summary?

## Resources

- SixLabors.ImageSharp docs: https://docs.sixlabors.com/articles/imagesharp/
- GIF metadata spec: https://www.w3.org/Graphics/GIF/spec-gif89a.txt
- ANSI color codes: https://gist.github.com/fnky/458719343aabd01cfb17a3a4f7296797
- Kitty graphics protocol: https://sw.kovidgoyal.net/kitty/graphics-protocol/
