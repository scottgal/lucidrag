# ImageSummarizer CLI Improvements

## Issues Identified

### 1. **No Capability Check on Startup** ❌
**Problem:** CLI doesn't verify Tesseract/models are available before processing
**Impact:** Silent failures, poor UX
**Solution:** Add `CheckCapabilitiesAsync()` method that verifies:
- Tesseract OCR engine available
- CLIP models (optional, warn if missing)
- Ollama connection (optional, warn if unavailable)
- Disk space for caching

### 2. **No Progress Display** ❌
**Problem:** Uses basic spinner, no visibility into which wave is executing
**Impact:** 6+ seconds with just "Analyzing image ⠋"
**Solution:** Use Spectre.Console progress bars showing:
```
⏳ Analyzing image...
  [▓▓▓▓▓▓▓▓▓░░░░░░░░] IdentityWave (110ms)
  [▓▓▓▓▓▓▓▓▓▓▓▓░░░░] ColorWave (121ms)
  [▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓] ExifForensicsWave (28ms)
  ...
```

### 3. **OCR Fails on Screenshots** ❌
**Problem:** Screenshots with clear text show "No text detected"
**Root Cause:** `text_likeliness` threshold too conservative for screenshots
**Solution:**
- Detect screenshot format (high edge density + rectangular regions)
- Bypass threshold for screenshots: `opt.Ocr.TextDetectionConfidenceThreshold = 0`
- OR: Lower default threshold from 0.4 to 0.2

### 4. **Minimal Wave Output** ❌
**Problem:** GIF analysis shows minimal info (no motion analysis displayed)
**Impact:** User sees "46 signals" but can't see what they are
**Solution:** Show signal summary in interactive mode:
```
📊 Analysis Results (46 signals):
  Identity: 9 signals (format=GIF, 200×198, 1.01 aspect ratio)
  Color: 22 signals (7 dominant colors, not grayscale)
  Motion: 3 signals (direction=static, 0 magnitude)
  Quality: 2 signals (sharpness=soft/blurry)
  Text: 0 signals (no OCR text extracted)
```

### 5. **No Glob/Directory Support** ❌
**Problem:** Can only analyze one image at a time
**Solution:** Support patterns:
```bash
imagesummarizer "screenshots/*.png"
imagesummarizer "path/to/folder"
imagesummarizer "**/*.gif" --recursive
```

### 6. **No Conversational UI** ❌
**Problem:** One-shot analysis, no follow-up questions
**Solution:** After analysis, enter conversational mode:
```
📸 Image analyzed! Ask me about it:
> What colors are in this image?
> Can you extract the text?
> Summarize the visual content
> quit
```

## Implementation Priority

### Phase 1: Immediate Fixes (This Session)
1. ✅ Add Spectre.Console dependency
2. ⏳ Add capability check on startup
3. ⏳ Add progress bars for wave execution
4. ⏳ Fix OCR detection for screenshots

### Phase 2: Enhanced Output
5. Better signal summary display
6. GIF motion analysis output
7. Face detection display
8. Quality metrics breakdown

### Phase 3: Batch & Interactive
9. Glob/directory pattern support
10. Conversational UI after analysis
11. Export summaries for batches

## Code Changes Needed

### 1. Capability Check Method
```csharp
static async Task<CapabilityStatus> CheckCapabilitiesAsync()
{
    var status = new CapabilityStatus();

    // Check Tesseract
    try {
        var engine = new TesseractOcrEngine();
        status.TesseractAvailable = true;
    } catch {
        status.TesseractAvailable = false;
        status.Warnings.Add("Tesseract OCR not found - install from https://github.com/tesseract-ocr/tesseract");
    }

    // Check CLIP models
    if (!File.Exists("./models/clip/clip-vit-b-32-visual.onnx")) {
        status.Warnings.Add("CLIP model not found - semantic embeddings disabled");
    }

    // Check Ollama (optional)
    try {
        var client = new HttpClient();
        var response = await client.GetAsync("http://localhost:11434/api/tags");
        status.OllamaAvailable = response.IsSuccessStatusCode;
    } catch {
        status.OllamaAvailable = false;
    }

    return status;
}
```

### 2. Spectre.Console Progress
```csharp
await AnsiConsole.Progress()
    .Columns(new ProgressColumn[] {
        new TaskDescriptionColumn(),
        new ProgressBarColumn(),
        new ElapsedTimeColumn()
    })
    .StartAsync(async ctx => {
        var task = ctx.AddTask("[cyan]Analyzing image[/]");

        orchestrator.OnWaveStarted += (name, priority) => {
            AnsiConsole.MarkupLine($"  [dim]→ {name}[/]");
        };

        orchestrator.OnWaveCompleted += (name, ms, signalCount) => {
            AnsiConsole.MarkupLine($"  [green]✓[/] {name} ({ms}ms, {signalCount} signals)");
        };

        var profile = await orchestrator.AnalyzeAsync(imagePath);
        task.Value = 100;
    });
```

### 3. Screenshot Detection Fix
```csharp
// In OcrWave.cs or WaveOrchestrator.cs
var isScreenshot = DetectScreenshot(context);
if (isScreenshot) {
    // Force OCR regardless of text_likeliness
    context.SetValue("ocr.force_extraction", true);
}

bool DetectScreenshot(AnalysisContext context) {
    var edgeDensity = context.GetValue<double>("quality.edge_density");
    var format = context.GetValue<string>("identity.format");
    var aspectRatio = context.GetValue<double>("identity.aspect_ratio");

    // Screenshots: PNG format + high edge density + widescreen aspect
    return format == "PNG"
        && edgeDensity > 0.15
        && aspectRatio > 1.3;
}
```

## Testing Plan

1. **Capability Check:**
   - Run without Tesseract → should show warning
   - Run without CLIP → should warn but continue
   - Run without Ollama → should work fine

2. **Progress Display:**
   - Analyze image → should show wave names and timing
   - Should update in real-time, not at end

3. **Screenshot OCR:**
   - Analyze PNG screenshot with text → should extract text
   - Compare before/after fix

4. **Signal Display:**
   - Analyze GIF → should show motion analysis
   - Analyze photo → should show face detection count
   - Show all 46 signals in structured format

## Success Criteria

✅ Capability warnings appear on first run
✅ Real-time progress shows which wave is executing
✅ Screenshots with text successfully extract OCR
✅ Signal summary shows meaningful breakdowns
✅ User can see what the 46 signals represent
