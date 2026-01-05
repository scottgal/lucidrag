# Mostlylucid.DocSummarizer.Images

**Complete Image Intelligence Pipeline** - Deterministic profiling, motion analysis, OCR, and Vision LLM escalation in a single unified library.

Core profiling is deterministic and offline; LLM/OCR/CLIP are optional stages controlled by thresholds and configuration.

![Cat Motion Demo](demo-images/cat_wag.gif)
*Motion detection extracts keyframes and analyzes movement patterns*

## Mental Model

- Deterministic analyzers emit signals (quality/color/type/text-likeliness)
- Motion analysis extracts movement patterns from animated GIFs
- Escalation rules decide if OCR or Vision LLM is needed
- Results are stored as confidence-scored signals in SQLite
- Subsequent runs reuse cached signals by content hash

## Features

### Core Analysis Pipeline

![Meme Text Extraction](demo-images/anchorman-not-even-mad.gif)
*Subtitle extraction across frames - captures "I'm not even mad. That's amazing."*

- **Deterministic profiling (fast path)** - no ML required
  - Color analysis (dominant colors, grids, saturation)
  - Edge detection (complexity, straight edges, entropy)
  - Blur/sharpness measurement (Laplacian variance)
  - Text-likeliness scoring (heuristic, no OCR)
  - Image type classification (Photo, Screenshot, Diagram, Chart, Icon, Artwork, Meme, Scanned Document)
  - `TypeConfidence` is a heuristic confidence score derived from rule agreement (not a calibrated probability)

- **Escalation to vision LLM (slow path)** - only when low-confidence
  - Rule-based escalation: deterministic and auditable (confidence thresholds + type triggers)
  - Integrates with Ollama for local vision model inference
  - Escalates low-confidence or low-quality cases, diagrams, charts
  - Vision model captions stored as confidence-scored signals

- **Signal storage + caching**
  - Results persisted as confidence-scored signals
  - Cache hit: ~2–10ms (SQLite, local disk)
  - Cache miss: heuristics ~10–50ms + optional LLM time

- **Advanced Features**
  - Perceptual hashing (dHash) for duplicate detection
  - Color grid generation for spatial color signatures
  - Thumbnail generation (WebP format)
  - OCR integration (Tesseract) triggered by text-likeliness
  - CLIP embeddings for similarity search
  - **GIF frame extraction and per-frame analysis**
  - **Motion detection with optical flow analysis**
  - **Frame strip technology for Vision LLM subtitle reading**

### Frame Strip Technology

For animated GIFs with subtitles, the library generates horizontal frame strips that capture unique text frames:

**OCR Mode Strip** (text changes only - 93 frames → 2 frames):
![OCR Strip](demo-images/anchorman-not-even-mad_ocr_strip.png)

**Motion Mode Strip** (keyframes for motion inference):
![Motion Strip](demo-images/cat_wag_motion_strip.png)

This allows Vision LLMs to read all subtitle text in a single API call, dramatically improving accuracy for memes and captioned content.

### Supported Formats

JPEG, PNG, GIF, WebP, BMP, TIFF (via SixLabors.ImageSharp)

## Installation

```bash
dotnet add package Mostlylucid.DocSummarizer.Images
```

## Quick Start

```csharp
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer.Images.Extensions;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;

// Register services
var services = new ServiceCollection();
services.AddDocSummarizerImages();
var serviceProvider = services.BuildServiceProvider();

// Analyze an image
var analyzer = serviceProvider.GetRequiredService<IImageAnalyzer>();
var profile = await analyzer.AnalyzeAsync("photo.jpg");

// Access results
Console.WriteLine($"Type: {profile.DetectedType} ({profile.TypeConfidence:P0} confidence)");
Console.WriteLine($"Dimensions: {profile.Width}x{profile.Height}");
Console.WriteLine($"Sharpness: {profile.LaplacianVariance:F1}");
Console.WriteLine($"Text Likeliness: {profile.TextLikeliness:F3}");

if (profile.DominantColors?.Any() == true)
{
    Console.WriteLine($"Dominant Color: {profile.DominantColors[0].Name} ({profile.DominantColors[0].Hex})");
}

// Generate perceptual hash for deduplication
var hash = await analyzer.GeneratePerceptualHashAsync("photo.jpg");
Console.WriteLine($"Hash: {hash}");
```

## Analysis Process

The library uses a **Wave-based analysis architecture** where specialized analyzers (Waves) each contribute signals to a unified profile:

![Alan Shrug](demo-images/alanshrug_opt.gif)
*Each wave analyzes different aspects: color, motion, text, quality*

### Wave Architecture

| Wave | Priority | Purpose | Signals Emitted |
|------|----------|---------|-----------------|
| **IdentityWave** | 10 | Format, dimensions, hash | `identity.*` |
| **ColorWave** | 20 | Dominant colors, palette, saturation | `color.*` |
| **ForensicsWave** | 30 | Edges, sharpness, blur detection | `visual.*`, `quality.*` |
| **MotionWave** | 40 | GIF frame analysis, optical flow | `motion.*` |
| **AdvancedOcrWave** | 50 | Multi-frame OCR with voting | `ocr.*` |
| **VisionLlmWave** | 80 | Vision LLM captions, scene classification | `vision.llm.*` |
| **ClipEmbeddingWave** | 90 | CLIP vector embeddings | `vision.clip.*` |
| **ContradictionWave** | 100 | Cross-wave validation | `validation.*` |

```mermaid
graph TD
    A[Image Input] --> B[Heuristic Analysis]
    B --> C[ColorAnalyzer]
    B --> D[EdgeAnalyzer]
    B --> E[BlurAnalyzer]
    B --> F[TextLikelinessAnalyzer]

    C --> G[ImageProfile]
    D --> G
    E --> G
    F --> G

    G --> H{Should Escalate?}
    H -->|Low Confidence| I[Vision LLM]
    H -->|Blurry| I
    H -->|High Text| I
    H -->|Diagram/Chart| I
    H -->|Confident| J[Cache Results]

    I --> K[Generate Caption]
    K --> L[Store with Signals]
    L --> J

    J --> M[Return Results]

    style B stroke:#90EE90
    style I stroke:#FFB6C1
    style L stroke:#87CEEB
```

### Stage 1: Heuristic Analysis (Fast Path)

All images undergo fast heuristic analysis (~50ms per image):

1. **Load & Downsample**: Load image and create 512x512 working copy
2. **Color Analysis**: Extract dominant colors, generate 3x3 color grid, measure saturation
3. **Edge Detection**: Apply Sobel operators, calculate edge density and entropy
4. **Blur Measurement**: Compute Laplacian variance for sharpness assessment
5. **Text Detection**: Heuristic scoring for text presence (no OCR yet)
6. **Type Classification**: Rule-based decision tree to classify image type

**Output**: `ImageProfile` with ~20 visual metrics and type classification

**Performance**: 50ms typical, scales to 100+ images/second with parallelization

### Stage 2: Escalation Decision (Conditional)

The system decides whether to escalate to vision LLM based on:

| Condition | Threshold | Reason |
|-----------|-----------|--------|
| Type confidence | < 0.7 | Uncertain classification |
| Sharpness | < 300 | Too blurry for heuristics |
| Text likeliness | > 0.4 | High text content needs understanding |
| Detected type | Diagram, Chart | Complex visualizations benefit from captions |

**Escalation rate**: Typically 10-20% of images in mixed collections

### Stage 3: Vision LLM Analysis (Slow Path, Cached)

Images meeting escalation criteria are sent to Ollama:

1. **Cache Check**: Look up SHA256 hash in SignalDatabase
2. **If Cached**: Return stored caption (~2-10ms)
3. **If Not Cached**:
   - Send image to Ollama vision model (minicpm-v:8b default)
   - Generate natural language caption (typically 2-5s per image)
   - Store caption as signal with confidence score
   - Cache for future requests

**Example Ollama models:**
- `minicpm-v:8b` (recommended)
- `llava:7b` / `llava:13b`
- `bakllava:7b`

Any Ollama vision model can be configured.

### Stage 4: OCR & Text Extraction (Optional)

If text likeliness > threshold (default 0.4):

1. **Tesseract OCR**: Extract text content
2. **Store as Signal**: `content.extracted_text` with confidence 0.9
3. **Cache**: Store in SignalDatabase for reuse

### Stage 5: GIF Motion Analysis

![Shrug GIF](demo-images/alanshrug_opt.gif)
*Motion analysis detects shoulder movement and gesture patterns*

For animated GIFs using the MotionWave analyzer:

1. **Frame Extraction**: Extract all frames or keyframes using subtitle-aware deduplication
2. **Optical Flow**: Compute motion vectors between consecutive frames
3. **Motion Direction**: Analyze dominant motion (left, right, up, down, radial)
4. **Motion Magnitude**: Calculate average pixel displacement per frame
5. **Store Signals**:
   - `motion.direction`: Dominant motion direction
   - `motion.magnitude`: Average displacement (pixels/frame)
   - `motion.regions`: Regions with significant motion
   - `motion.type`: Classification (camera_pan, camera_zoom, object_motion, general)
   - `motion.is_looping`: Whether animation loops seamlessly

**Subtitle-Aware Frame Deduplication**:
- Weights bottom 25% of frame at 40% (where subtitles appear)
- Weights bright pixels (white/yellow text) at 30% with 3x multiplier
- Main content only 30%
- This ensures text changes are captured even when main image is static

![Arse Biscuits](demo-images/arse_biscuits.gif)
*Multi-line subtitle extraction with gesture recognition*

**Example motion signals** (from cat_wag.gif):
```csharp
// For a GIF with motion:
profile.GetValue<bool>("motion.has_motion");       // true
profile.GetValue<string>("motion.type");           // "object_motion"
profile.GetValue<string>("motion.direction");      // dominant direction
profile.GetValue<double>("motion.magnitude");      // intensity 0-1
profile.GetValue<double>("motion.activity");       // coverage percentage
profile.GetValue<double>("motion.temporal_consistency"); // consistency across frames
```

**Text output** (from CLI):
```
Caption: A cat is laying down with its body stretched out across a white bench...
Scene: indoor
Motion: MODERATE object_motion motion (partial coverage)
```

### Stage 6: Caching & Storage

All results are cached in SQLite SignalDatabase:

**Dual-hash strategy**:
- **xxhash64**: Fast initial lookup (10x+ faster than SHA256)
- **SHA256**: Cryptographically secure primary key

**Signal-based storage**:
- Each analysis result stored as discrete signal
- Multiple sources can contribute signals
- Retrieve by confidence, source, or timestamp
- Thread-safe with SemaphoreSlim + WAL mode

**Cache performance**:
- **Hit**: ~3.2ms (load from database)
- **Miss**: ~740ms (heuristics) + 2.9ms (hashing) + optional LLM time
- **Speedup**: 231x faster on cache hit vs. full analysis

## Detailed Component Documentation

For in-depth technical documentation on each analyzer:
- **[ANALYZERS.md](ANALYZERS.md)** - Detailed analyzer algorithms, metrics, and usage
  - ColorAnalyzer (quantization, color grids, Lanczos3 resampling)
  - EdgeAnalyzer (Sobel operators, entropy, straight edge detection)
  - BlurAnalyzer (Laplacian variance, sharpness categories)
  - TextLikelinessAnalyzer (multi-factor heuristic scoring)
  - TypeDetector (decision tree rules and confidence factors)

## API Reference

### IImageAnalyzer

Main interface for image analysis.

```csharp
public interface IImageAnalyzer
{
    // Analyze image from file path
    Task<ImageProfile> AnalyzeAsync(string imagePath, CancellationToken ct = default);

    // Analyze image from bytes
    Task<ImageProfile> AnalyzeAsync(byte[] imageBytes, string fileName, CancellationToken ct = default);

    // Generate perceptual hash for deduplication
    Task<string> GeneratePerceptualHashAsync(string imagePath, CancellationToken ct = default);

    // Generate WebP thumbnail
    Task<byte[]> GenerateThumbnailAsync(string imagePath, int maxSize = 256, CancellationToken ct = default);
}
```

### ImageProfile

Result of image analysis containing all measured properties.

```csharp
public record ImageProfile
{
    // Identity
    public string Sha256 { get; init; }
    public string Format { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double AspectRatio { get; init; }
    public bool HasExif { get; init; }

    // Type Detection
    public ImageType DetectedType { get; init; }
    public double? TypeConfidence { get; init; }

    // Visual Complexity
    public double EdgeDensity { get; init; }
    public double LuminanceEntropy { get; init; }
    public double CompressionArtifacts { get; init; }

    // Brightness/Contrast
    public double MeanLuminance { get; init; }
    public double LuminanceStdDev { get; init; }
    public double ClippedBlacksPercent { get; init; }
    public double ClippedWhitesPercent { get; init; }

    // Sharpness
    public double LaplacianVariance { get; init; }

    // Text Detection
    public double TextLikeliness { get; init; }
    public List<SaliencyRegion>? SalientRegions { get; init; }

    // Color Analysis
    public List<DominantColor>? DominantColors { get; init; }
    public ColorGrid? ColorGrid { get; init; }
    public double MeanSaturation { get; init; }
    public bool IsMostlyGrayscale { get; init; }

    // Hashing
    public string? PerceptualHash { get; init; }
}
```

### ImageType Enum

```csharp
public enum ImageType
{
    Unknown,
    Photo,
    Screenshot,
    Diagram,
    ScannedDocument,
    Icon,
    Chart,
    Artwork,
    Meme
}
```

## Configuration

### ImageConfig

Configure image processing behavior.

```csharp
services.AddDocSummarizerImages(config =>
{
    config.Mode = ImageSummaryMode.ProfileOnly; // No model inference
    config.EnableOcr = true; // Enable Tesseract OCR
    config.EnableClipEmbedding = true; // Enable CLIP embeddings
    config.TextLikelinessThreshold = 0.4; // OCR trigger threshold
    config.MaxImageSize = 2048; // Max dimension for processing
    config.ThumbnailSize = 256; // Thumbnail max dimension
    config.TesseractLanguage = "eng"; // OCR language
    config.ColorGrid.Rows = 3; // Color grid rows
    config.ColorGrid.Cols = 3; // Color grid columns
});
```

### Configuration from appsettings.json

```json
{
  "Images": {
    "Mode": "ProfileOnly",
    "EnableOcr": true,
    "EnableClipEmbedding": true,
    "TextLikelinessThreshold": 0.4,
    "MaxImageSize": 2048,
    "ThumbnailSize": 256,
    "TesseractLanguage": "eng",
    "ColorGrid": {
      "Rows": 3,
      "Cols": 3,
      "TargetWidth": 384,
      "SampleStep": 2,
      "BucketBits": 4
    }
  }
}
```

```csharp
services.AddDocSummarizerImages(configuration.GetSection("Images"));
```

## Integration with Document Handlers

The library includes `ImageDocumentHandler` that implements `IDocumentHandler` for integration with document processing pipelines.

```csharp
// Automatically registered when using AddDocSummarizerImages()
var handler = serviceProvider.GetRequiredService<IDocumentHandler>();

if (handler.CanHandle("photo.jpg"))
{
    var options = new DocumentHandlerOptions
    {
        CollectionName = "photos",
        OllamaUrl = "http://localhost:11434"
    };

    var result = await handler.ProcessAsync("photo.jpg", options);

    Console.WriteLine(result.Summary); // Markdown format
    Console.WriteLine($"Embeddings: {result.Embeddings.Count}");
}
```

## Perceptual Hashing for Deduplication

Find duplicate or similar images using dHash (difference hash).

```csharp
var hash1 = await analyzer.GeneratePerceptualHashAsync("photo1.jpg");
var hash2 = await analyzer.GeneratePerceptualHashAsync("photo2.jpg");

// Calculate Hamming distance
int HammingDistance(ulong a, ulong b) =>
    System.Numerics.BitOperations.PopCount(a ^ b);

var distance = HammingDistance(
    Convert.ToUInt64(hash1[..16], 16),
    Convert.ToUInt64(hash2[..16], 16));

if (distance <= 5)
{
    Console.WriteLine("Images are very similar or identical");
}
else if (distance <= 10)
{
    Console.WriteLine("Images are somewhat similar");
}
else
{
    Console.WriteLine("Images are different");
}
```

## Performance

**Typical performance on modern hardware:**

- **Analysis**: 10-50ms per image (depends on resolution)
- **Perceptual Hash**: 5-10ms per image
- **Thumbnail**: 15-30ms per image
- **Memory**: Works on downscaled versions (256-512px) for efficiency

**Optimization tips:**
- Process images in parallel for batch operations
- Use `IProgress<T>` for progress reporting
- Consider caching `ImageProfile` results for repeated access
- Thumbnails are cached if using the same parameters

## Advanced Features

### Signal-Based Architecture

The library uses a **signal-based storage pattern** where analysis results are stored as discrete observations (signals) with metadata:

```csharp
public class Signal
{
    public string Key { get; set; }           // "content.llm_caption"
    public object? Value { get; set; }        // Actual value
    public double Confidence { get; set; }    // 0.0-1.0 confidence score
    public string Source { get; set; }        // "ImageAnalyzer", "VisionLLM", "OCR"
    public DateTime Timestamp { get; set; }   // When signal was emitted
    public List<string>? Tags { get; set; }   // Categorization tags
    public Dictionary<string, object>? Metadata { get; set; }  // Additional context
}
```

**Benefits:**
- **Flexibility**: Add new analysis types without schema changes
- **Versioning**: Multiple sources can provide competing signals
- **Aggregation**: Combine signals using strategies (highest confidence, average)
- **Queryable**: Filter by tags, source, confidence

### DynamicImageProfile

Flexible profile that aggregates signals from multiple sources:

```csharp
var dynamicProfile = new DynamicImageProfile
{
    ImagePath = "photo.jpg"
};

// Add signals from deterministic analysis
dynamicProfile.AddSignal(new Signal
{
    Key = "quality.sharpness",
    Value = 2856.97,
    Confidence = 0.8,
    Source = "ImageAnalyzer",
    Tags = new List<string> { "quality", "sharpness" }
});

// Add signals from vision LLM
dynamicProfile.AddSignal(new Signal
{
    Key = "content.llm_caption",
    Value = "A serene landscape with mountains...",
    Confidence = 0.85,
    Source = "VisionLLM",
    Tags = new List<string> { "caption", "description", "llm" }
});

// Query signals
var sharpness = dynamicProfile.GetValue<double>("quality.sharpness");
var caption = dynamicProfile.GetValue<string>("content.llm_caption");
var bestSignal = dynamicProfile.GetBestSignal("quality.sharpness");
```

### Signal Catalog

#### Identity Signals (confidence: 1.0)
- `identity.sha256` - SHA256 content hash
- `identity.format` - Image format (PNG, JPEG, GIF, WEBP)
- `identity.width`, `identity.height` - Dimensions
- `identity.aspect_ratio` - Width/height ratio

#### Content Signals
- `content.type` - Detected type (Photo, Diagram, Chart, etc.)
- `content.type_confidence` - Type detection confidence (0.0-1.0)
- `content.text_likeliness` - Probability of containing text (0.0-1.0)
- `content.llm_caption` - Vision LLM description (confidence: 0.85, source: VisionLLM)
- `content.extracted_text` - OCR results (confidence: 0.9, source: OCR)

#### Quality Signals
- `quality.sharpness` - Laplacian variance (confidence: 0.8)
- `quality.edge_density` - Percentage of edge pixels (confidence: 0.9)
- `quality.luminance_entropy` - Information content (confidence: 0.9)

#### Color Signals
- `color.dominant_color_names` - List of color names (confidence: 0.9)
- `color.dominant_color_hexes` - List of hex codes (confidence: 0.9)
- `color.dominant_color_percentages` - List of percentages (confidence: 0.9)
- `color.mean_luminance` - Average brightness 0-255 (confidence: 1.0)
- `color.mean_saturation` - Average color intensity 0-1 (confidence: 1.0)
- `color.is_mostly_grayscale` - Boolean flag (confidence: 1.0)

#### Validation Signals
- `validation.contradiction.count` - Number of contradictions detected (confidence: 1.0)
- `validation.contradiction.status` - Overall status: "clean", "info", "warning", "error", "critical"
- `validation.contradiction.<rule_id>` - Details of specific contradiction (when detected)

### Contradiction Detection

The library includes a **config-driven contradiction detection system** that validates signals from different analysis waves for consistency. This catches cases where different analyzers produce conflicting results.

```mermaid
graph TD
    A[All Waves Complete] --> B[ContradictionWave]
    B --> C{Check Rules}
    C --> D[OCR vs Vision Text]
    C --> E[Grayscale vs Colors]
    C --> F[Type Classifications]
    C --> G[Confidence Conflicts]
    D --> H{Contradiction?}
    E --> H
    F --> H
    G --> H
    H -->|Yes| I[Emit Contradiction Signal]
    H -->|No| J[Status: Clean]
    I --> K[Apply Resolution Strategy]

    style B stroke:#FFD700
    style I stroke:#FF6B6B
    style J stroke:#90EE90
```

**Built-in Contradiction Rules:**

| Rule ID | Description | Severity |
|---------|-------------|----------|
| `ocr_vs_vision_text` | OCR found text but Vision LLM says no text | Warning |
| `text_likeliness_vs_ocr` | High text score but OCR found nothing | Warning |
| `grayscale_vs_colors` | Marked grayscale but has colorful dominants | Info |
| `screenshot_vs_photo_noise` | Screenshot type but photo-like noise | Warning |
| `llm_vs_heuristic_type` | Vision LLM type differs from heuristics | Info |
| `face_vs_icon` | Faces detected in Icon/Diagram | Warning |
| `exif_format_mismatch` | EXIF in format that doesn't support it | Warning |
| `blur_vs_edges` | Low sharpness but high edge density | Info |

**Resolution Strategies:**

- `PreferHigherConfidence` - Keep signal with higher confidence
- `PreferMostRecent` - Keep most recent signal
- `MarkConflicting` - Keep both, flag for review
- `RemoveBoth` - Neither signal trusted
- `EscalateToLlm` - Escalate to Vision LLM for resolution
- `ManualReview` - Flag for human review

**Configuration:**

```json
{
  "Images": {
    "Contradiction": {
      "Enabled": true,
      "RejectOnCritical": false,
      "MinConfidenceThreshold": 0.5,
      "EnableLlmEscalation": true,
      "CustomRules": [
        {
          "RuleId": "my_custom_rule",
          "Description": "Custom validation rule",
          "SignalKeyA": "content.type",
          "SignalKeyB": "vision.detected_type",
          "Type": "ValueConflict",
          "Severity": "Warning",
          "Resolution": "PreferHigherConfidence"
        }
      ]
    }
  }
}
```

**Programmatic Usage:**

```csharp
// Get contradiction results from profile
var contradictions = profile.GetSignals("validation.contradiction")
    .Where(s => !s.Key.EndsWith(".count") && !s.Key.EndsWith(".status"));

foreach (var signal in contradictions)
{
    var metadata = signal.Metadata;
    Console.WriteLine($"Rule: {metadata["rule_id"]}");
    Console.WriteLine($"Severity: {metadata["severity"]}");
    Console.WriteLine($"Explanation: {signal.Value}");
    Console.WriteLine($"Resolution: {metadata["resolution"]}");
}

// Check if image should be rejected
var status = profile.GetValue<string>("validation.contradiction.status");
if (status == "critical" && config.Contradiction.RejectOnCritical)
{
    // Handle rejected image
}
```

### Auto-Escalation to Vision LLM

Automatically escalates low-confidence images to vision LLM for semantic understanding:

```mermaid
graph TD
    A[Deterministic Analysis] --> B{Should Escalate?}
    B -->|Type confidence < 0.7| E[Vision LLM]
    B -->|Sharpness < 300| E
    B -->|Text likeliness > 0.4| E
    B -->|Type = Diagram/Chart| E
    B -->|No| F[Store Results]
    E --> G[Generate Caption]
    G --> H[Store Caption Signal]
    H --> F

    style E stroke:#FFB6C1
    style H stroke:#90EE90
```

**Escalation conditions:**

| Condition | Threshold | Reason |
|-----------|-----------|--------|
| Type confidence | < 0.7 | Uncertain classification |
| Sharpness (Laplacian) | < 300 | Blurry, needs context |
| Text likeliness | > 0.4 | High text content |
| Detected type | Diagram, Chart | Complex visualizations |

**Usage with Ollama:**

```csharp
var escalationService = new EscalationService(
    analyzer,
    visionLlmClient,
    signalDatabase,
    logger,
    new EscalationConfig
    {
        AutoEscalateEnabled = true,
        ConfidenceThreshold = 0.7,
        BlurThreshold = 300,
        TextLikelinessThreshold = 0.4,
        EnableCaching = true
    });

var result = await escalationService.AnalyzeWithEscalationAsync("image.jpg");

Console.WriteLine($"Type: {result.Profile.DetectedType}");
Console.WriteLine($"LLM Caption: {result.LlmCaption}");
Console.WriteLine($"Was Escalated: {result.WasEscalated}");
Console.WriteLine($"From Cache: {result.FromCache}");
```

### SignalDatabase Caching

SQLite-based persistent storage with content-based caching:

```csharp
var signalDatabase = new SignalDatabase("image-cache.db", logger);

// Store profile with signals
var dynamicProfile = ConvertToDynamicProfile(imageProfile);
dynamicProfile.AddSignal(new Signal
{
    Key = "content.llm_caption",
    Value = "A serene landscape...",
    Confidence = 0.85,
    Source = "VisionLLM"
});

await signalDatabase.StoreProfileAsync(
    dynamicProfile,
    sha256Hash,
    filePath: "photo.jpg",
    width: 1920,
    height: 1080,
    format: "JPEG");

// Load from cache
var cachedProfile = await signalDatabase.LoadProfileAsync(sha256Hash);
if (cachedProfile != null)
{
    var caption = cachedProfile.GetValue<string>("content.llm_caption");
    Console.WriteLine($"Cached caption: {caption}");
}

// Get statistics
var stats = await signalDatabase.GetStatisticsAsync();
Console.WriteLine($"Images: {stats.ImageCount}");
Console.WriteLine($"Signals: {stats.SignalCount}");
Console.WriteLine($"Unique sources: {stats.UniqueSourceCount}");
```

**Dual-hash strategy:**
- **xxhash64**: Fast (10x+ faster than SHA256) for cache lookups
- **SHA256**: Cryptographically secure primary key in database

**Performance:**
- **Cache hit**: ~3.2ms (231x faster than full analysis)
- **Cache miss**: ~740ms (analysis) + 2.9ms (hashing)
- **Thread-safe**: SemaphoreSlim + SQLite WAL mode

### Ollama Vision LLM Integration

[Ollama](https://ollama.ai) provides local vision model inference for image captioning:

**Installation:**
```bash
# Install Ollama
winget install Ollama.Ollama  # Windows
brew install ollama            # macOS
curl -fsSL https://ollama.ai/install.sh | sh  # Linux

# Pull vision model
ollama pull minicpm-v:8b  # 4.5 GB, 8B parameters
```

**Configuration:**
```csharp
var visionClient = new VisionLlmClient(new VisionLlmConfig
{
    BaseUrl = "http://localhost:11434",
    Model = "minicpm-v:8b",
    Temperature = 0.3,
    Timeout = 120
});

var caption = await visionClient.AnalyzeImageAsync("photo.jpg");
Console.WriteLine(caption);  // "A serene landscape with mountains..."
```

**Alternative models:**
- `llava:7b` - General vision model (3.8 GB)
- `llava:13b` - Higher quality (7.3 GB, slower)
- `bakllava:7b` - Fast vision model (4.1 GB)

### Feedback Loop

Store user corrections to improve future analysis:

```csharp
await signalDatabase.StoreFeedbackAsync(
    sha256: imageHash,
    feedbackType: "type_correction",
    originalValue: "Diagram",
    correctedValue: "Chart",
    confidenceAdjustment: -0.2,
    notes: "This is clearly a bar chart, not a generic diagram");
```

## Use Cases

![Cat Motion](demo-images/cat_wag.gif)

1. **Document Processing** - Classify images in document pipelines
2. **Photo Library Organization** - Detect and organize photos vs screenshots
3. **Duplicate Detection** - Find duplicate images using perceptual hashing
4. **Quality Control** - Filter blurry or low-quality images
5. **Content Moderation** - Pre-screen images before expensive vision model inference
6. **OCR Optimization** - Only run OCR on images with high text likeliness
7. **Diagram Extraction** - Identify and extract diagrams from mixed content
8. **Color Palette Generation** - Extract color schemes from images
9. **Semantic Search** - Cache vision LLM captions for natural language search
10. **Multi-Pipeline Processing** - Signal-based coordination for complex workflows
11. **Meme Text Extraction** - Extract subtitle text from animated memes
12. **GIF Captioning** - Generate natural language descriptions of animations
13. **Motion Classification** - Detect and classify movement patterns

## Command Line Tool

For interactive use, batch processing, and MCP server integration, see:

**[ImageSummarizer CLI](../Mostlylucid.ImageSummarizer.Cli/README.md)** - Full-featured CLI with Vision LLM integration

![Anchorman Meme](demo-images/anchorman-not-even-mad.gif)

```bash
# Download from releases
# https://github.com/scottgal/lucidrag/releases

# Analyze single image with visual output
imagesummarizer demo-images/anchorman-not-even-mad.gif --pipeline caption --output visual

# Extract text from animated meme
imagesummarizer demo-images/anchorman-not-even-mad.gif --output text
# Output:
# "I'm not even i mmad."
# "That's amazing."
# Caption: A man with a mustache is wearing grey high neck sweater...
# Scene: meme
# Motion: SUBTLE general motion (localized coverage)

# Generate OCR frame strip (text changes only)
imagesummarizer export-strip demo-images/anchorman-not-even-mad.gif --mode ocr
# Deduplicating 93 frames (OCR mode - text changes only)...
#   Reduced to 2 unique text frames
# ✓ Saved ocr strip to: anchorman-not-even-mad_ocr_strip.png
#   Dimensions: 600x185 (2 frames)

# Generate motion keyframe strip
imagesummarizer export-strip demo-images/cat_wag.gif --mode motion --max-frames 6
# Extracting 6 keyframes from 9 frames (motion mode)...
#   Extracted 6 keyframes for motion inference
# ✓ Saved motion strip to: cat_wag_motion_strip.png
#   Dimensions: 3000x280 (6 frames)

# Batch process directory
imagesummarizer ./photos --output json
```

## Dependencies

- **SixLabors.ImageSharp** - Core image processing
- **Microsoft.ML.OnnxRuntime** (optional) - For CLIP models
- **Tesseract** (optional) - For OCR capabilities

## License

Unlicense - See LICENSE file for details

## Contributing

Contributions welcome! Please see the main repository for guidelines.

## Support

- **GitHub Issues**: https://github.com/scottgal/LucidRAG/issues
- **Documentation**: https://github.com/scottgal/LucidRAG/wiki

---

*Part of the LucidRAG project - Multi-document Agentic RAG with GraphRAG capabilities*
