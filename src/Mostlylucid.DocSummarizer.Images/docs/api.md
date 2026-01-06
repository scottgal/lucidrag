# API Reference

## Core Interfaces

### IImageAnalyzer

Main entry point for image analysis:

```csharp
public interface IImageAnalyzer
{
    Task<ImageProfile> AnalyzeAsync(string imagePath, CancellationToken ct = default);

    Task<SignalResult> AnalyzeBySignalsAsync(
        string imagePath,
        string signalPattern,
        CancellationToken ct = default);
}
```

### IAnalysisWave

Interface for analysis waves:

```csharp
public interface IAnalysisWave
{
    string Name { get; }
    int Priority { get; }
    IReadOnlyList<string> Tags { get; }

    bool ShouldRun(string imagePath, AnalysisContext context);

    Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default);
}
```

## ImageProfile

Result of full analysis:

```csharp
public class ImageProfile
{
    public IdentityInfo Identity { get; }
    public ColorInfo Color { get; }
    public QualityInfo Quality { get; }
    public OcrInfo Ocr { get; }
    public CaptionInfo Caption { get; }
    public MotionInfo Motion { get; }
    public SignalLedger Ledger { get; }
}
```

### IdentityInfo

```csharp
public record IdentityInfo
{
    public int Width { get; init; }
    public int Height { get; init; }
    public string Format { get; init; }
    public bool IsAnimated { get; init; }
    public int FrameCount { get; init; }
    public ImageType Type { get; init; }
    public double TypeConfidence { get; init; }
}
```

### ColorInfo

```csharp
public record ColorInfo
{
    public DominantColor Dominant { get; init; }
    public List<ColorEntry> Palette { get; init; }
    public double AverageSaturation { get; init; }
}

public record DominantColor
{
    public string Hex { get; init; }
    public string Name { get; init; }
    public double Percentage { get; init; }
}
```

### MotionInfo

```csharp
public record MotionInfo
{
    public bool IsAnimated { get; init; }
    public int FrameCount { get; init; }
    public double Duration { get; init; }
    public double MotionIntensity { get; init; }
    public string MotionType { get; init; }
}
```

## Configuration

### ImageConfig

```csharp
public class ImageConfig
{
    public bool EnableOcr { get; set; } = true;
    public bool EnableVisionLlm { get; set; } = true;
    public bool EnableFlorence2 { get; set; } = true;
    public bool EnableClip { get; set; } = false;

    public OcrConfig Ocr { get; set; }
    public VisionLlmConfig VisionLlm { get; set; }
    public Florence2Config Florence2 { get; set; }
    public CacheConfig Cache { get; set; }
}
```

### OcrConfig

```csharp
public class OcrConfig
{
    public string TesseractDataPath { get; set; }
    public List<string> Languages { get; set; } = ["eng"];
    public double MinConfidence { get; set; } = 0.5;
}
```

### VisionLlmConfig

```csharp
public class VisionLlmConfig
{
    public bool Enabled { get; set; } = true;
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "minicpm-v:8b";
    public int TimeoutSeconds { get; set; } = 60;
}
```

## Dependency Injection

```csharp
services.AddImageAnalyzer(config =>
{
    config.EnableOcr = true;
    config.EnableFlorence2 = true;
    config.VisionLlm.OllamaUrl = "http://localhost:11434";
    config.VisionLlm.Model = "minicpm-v:8b";
});
```

## Error Handling

```csharp
try
{
    var profile = await analyzer.AnalyzeAsync(path);
}
catch (ImageAnalysisException ex)
{
    // Analysis failed
    Console.WriteLine($"Analysis failed: {ex.Message}");
    Console.WriteLine($"Wave: {ex.WaveName}");
}
catch (ModelLoadException ex)
{
    // Florence-2 or CLIP model failed to load
    Console.WriteLine($"Model load failed: {ex.Message}");
}
```

## Performance Tips

1. **Use signal patterns** - Only request needed signals
2. **Enable caching** - Reuse results for same images
3. **Disable unused waves** - Set `EnableClip = false` if not needed
4. **Use ProfileOnly pipeline** - For metadata-only analysis
5. **Batch processing** - Process multiple images concurrently
