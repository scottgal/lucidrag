# OCR Pipeline Configuration System

## Overview

The OCR pipeline system provides a flexible, JSON-based configuration for orchestrating multi-phase image analysis and OCR workflows. Pipelines are composed of modular **phases** (implemented as analysis waves) that can be enabled, disabled, and parameterized independently.

## Key Features

- **JSON Configuration**: Define pipelines in `pipelines.json` with human-readable format
- **Source Generation**: Zero-allocation JSON serialization using .NET source generators
- **Named Pipelines**: Pre-configured pipelines for common use cases (simple, advanced, quality, alt text)
- **Phase Composition**: Mix and match phases with dependency management
- **Performance Control**: Set timeouts, early exit thresholds, and parallelization
- **Runtime Flexibility**: Load custom pipeline configurations at runtime

## Architecture

```
PipelinesConfig (root)
├── Pipelines[] (list of named pipelines)
│   ├── PipelineConfig
│   │   ├── Name: "advancedocr"
│   │   ├── Phases[]
│   │   │   ├── PipelinePhase (ColorWave)
│   │   │   ├── PipelinePhase (OcrWave)
│   │   │   ├── PipelinePhase (AdvancedOcrWave)
│   │   │   └── PipelinePhase (OcrQualityWave)
│   │   └── GlobalSettings
│   └── ...
└── Defaults (global settings)
```

## Configuration File Structure

### Root Configuration

```json
{
  "schemaVersion": "1.0",
  "defaultPipeline": "advancedocr",
  "defaults": {
    "maxTotalDurationMs": 60000,
    "enableParallelization": true,
    "emitPerformanceSignals": true
  },
  "pipelines": [ ... ]
}
```

### Pipeline Definition

```json
{
  "name": "advancedocr",
  "displayName": "Advanced OCR (Default)",
  "description": "Multi-frame temporal OCR with stabilization and voting",
  "category": "balanced",
  "estimatedDurationSeconds": 2.5,
  "accuracyImprovement": 25,
  "isDefault": true,
  "tags": ["balanced", "recommended"],
  "globalSettings": {
    "maxTotalDurationMs": 5000,
    "globalEarlyExitThreshold": 0.95
  },
  "phases": [ ... ]
}
```

### Phase Definition

```json
{
  "id": "advanced-ocr",
  "name": "Advanced Multi-Frame OCR",
  "description": "Temporal processing with frame stabilization and voting",
  "enabled": true,
  "priority": 59,
  "waveType": "AdvancedOcrWave",
  "dependsOn": ["simple-ocr"],
  "parameters": {
    "maxFrames": 30,
    "ssimThreshold": 0.95,
    "enableStabilization": true,
    "enableTemporalMedian": true,
    "enableVoting": true,
    "votingConfidenceThreshold": 0.3
  },
  "earlyExitThreshold": 0.9,
  "maxDurationMs": 3000,
  "tags": ["ocr", "temporal", "advanced"]
}
```

## Configuration Fields

### Pipeline Fields

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | Unique pipeline identifier (e.g., "advancedocr") |
| `displayName` | string | Human-readable name for UI/CLI |
| `description` | string | What this pipeline optimizes for |
| `category` | string | Category: "speed", "quality", "balanced", "accessibility" |
| `estimatedDurationSeconds` | number | Typical processing time for user guidance |
| `accuracyImprovement` | number | Expected accuracy gain over baseline (%) |
| `isDefault` | boolean | Whether this is the default pipeline |
| `tags` | string[] | Categorization tags |
| `globalSettings` | object | Pipeline-wide configuration |
| `phases` | array | Ordered list of phases |

### Phase Fields

| Field | Type | Description |
|-------|------|-------------|
| `id` | string | Unique phase identifier within pipeline |
| `name` | string | Human-readable phase name |
| `description` | string | What this phase does |
| `enabled` | boolean | Whether phase is active |
| `priority` | number | Execution order (higher = earlier) |
| `waveType` | string | Wave class implementing this phase |
| `dependsOn` | string[] | IDs of phases that must run first |
| `parameters` | object | Phase-specific configuration |
| `earlyExitThreshold` | number | Skip if previous confidence exceeds this |
| `maxDurationMs` | number | Maximum phase execution time |
| `tags` | string[] | Phase categorization |

### Global Settings

| Field | Type | Description |
|-------|------|-------------|
| `maxTotalDurationMs` | number | Maximum total pipeline duration (0 = unlimited) |
| `globalEarlyExitThreshold` | number | Skip remaining phases if any achieves this confidence |
| `enableParallelization` | boolean | Whether to parallelize independent phases |
| `maxParallelism` | number | Max degree of parallelism (-1 = CPU count) |
| `emitPerformanceSignals` | boolean | Emit detailed performance metrics |
| `enableCaching` | boolean | Cache intermediate results |

## Built-in Pipelines

### Simple OCR
- **Speed**: ~0.5s per image
- **Accuracy**: Baseline Tesseract
- **Use Case**: Quick extraction, batch processing
- **Phases**: ColorWave → OcrWave → OcrQualityWave

### Advanced OCR (Default)
- **Speed**: ~2.5s per GIF
- **Accuracy**: +25% over baseline
- **Use Case**: Best balance of speed and accuracy
- **Phases**: ColorWave → OcrWave → AdvancedOcrWave → OcrQualityWave
- **Features**: Frame stabilization, temporal median, voting, spell-check

### Quality OCR
- **Speed**: ~12s per GIF
- **Accuracy**: +45% over baseline
- **Use Case**: High-accuracy needs, archival
- **Phases**: Same as Advanced but with higher frame counts and stricter thresholds

### Alt Text Generation
- **Speed**: ~3.5s per image
- **Accuracy**: Optimized for descriptive quality
- **Use Case**: Accessibility, image description
- **Phases**: ColorWave → OcrWave → AdvancedOcrWave → OcrQualityWave
- **Future**: Will add LLM synthesis wave for coherent alt text generation

## Using Pipelines

### Command Line

```bash
# List available pipelines
imagecli list-pipelines

# Use a specific pipeline
imagecli image.gif --pipeline advancedocr --output json

# Use simple pipeline for speed
imagecli image.png --pipeline simpleocr

# Use quality pipeline for best accuracy
imagecli document.gif --pipeline quality
```

### Programmatic Usage

```csharp
using Mostlylucid.DocSummarizer.Images.Services.Pipelines;

// Load pipelines
var pipelineService = new PipelineService();
var config = await pipelineService.LoadPipelinesAsync();

// Get a specific pipeline
var pipeline = await pipelineService.GetPipelineAsync("advancedocr");

// List all pipelines
var names = await pipelineService.ListPipelineNamesAsync();

// Get default pipeline
var defaultPipeline = await pipelineService.GetDefaultPipelineAsync();
```

### Configuration in appsettings.json

```json
{
  "Images": {
    "EnableOcr": true,
    "Ocr": {
      "PipelineName": "advancedocr",  // Use named pipeline
      "UseAdvancedPipeline": true,
      "SpellCheckLanguage": "en_US"
    }
  }
}
```

### Legacy Mode (Quality Presets)

If `PipelineName` is not specified, falls back to quality mode presets:

```csharp
opt.Ocr.QualityMode = OcrQualityMode.Fast;     // ~2-3s, +20-30% accuracy
opt.Ocr.QualityMode = OcrQualityMode.Balanced; // ~5-7s, +30-40% accuracy
opt.Ocr.QualityMode = OcrQualityMode.Quality;  // ~10-15s, +35-45% accuracy
opt.Ocr.QualityMode = OcrQualityMode.Ultra;    // ~20-30s, +40-60% accuracy
```

## Creating Custom Pipelines

### 1. Edit pipelines.json

Add a new pipeline definition:

```json
{
  "name": "my-custom-pipeline",
  "displayName": "My Custom Pipeline",
  "description": "Optimized for my specific use case",
  "category": "custom",
  "estimatedDurationSeconds": 5.0,
  "isDefault": false,
  "phases": [
    {
      "id": "color",
      "name": "Color Analysis",
      "enabled": true,
      "priority": 100,
      "waveType": "ColorWave"
    },
    {
      "id": "ocr",
      "name": "Basic OCR",
      "enabled": true,
      "priority": 60,
      "waveType": "OcrWave",
      "parameters": {
        "customParam": "value"
      }
    }
  ]
}
```

### 2. Use the Custom Pipeline

```bash
imagecli image.gif --pipeline my-custom-pipeline
```

### 3. Programmatic Pipeline Creation

```csharp
var customPipeline = new PipelineConfig
{
    Name = "runtime-custom",
    DisplayName = "Runtime Custom Pipeline",
    Phases = new List<PipelinePhase>
    {
        new PipelinePhase
        {
            Id = "color",
            Name = "Color Analysis",
            Priority = 100,
            WaveType = "ColorWave",
            Enabled = true
        },
        // Add more phases...
    }
};

var config = new PipelinesConfig
{
    Pipelines = new List<PipelineConfig> { customPipeline }
};

var pipelineService = new PipelineService();
await pipelineService.SavePipelinesAsync(config, "custom-pipelines.json");
```

## Available Wave Types

| Wave Type | Description | Priority | Signals Emitted |
|-----------|-------------|----------|-----------------|
| `ColorWave` | Color analysis, dominant colors, text likelihood | 100 | `color.*`, `content.text_likeliness` |
| `OcrWave` | Simple Tesseract OCR (baseline) | 60 | `ocr.text`, `ocr.confidence` |
| `AdvancedOcrWave` | Multi-frame temporal OCR | 59 | `ocr.voting.*`, `ocr.stabilization.*` |
| `OcrQualityWave` | Spell-check quality assessment | 58 | `ocr.quality.*` |

## Performance Tuning

### Early Exit Optimization

Set thresholds to skip expensive phases when confidence is high:

```json
{
  "globalSettings": {
    "globalEarlyExitThreshold": 0.95  // Skip all if any phase achieves 95% confidence
  },
  "phases": [
    {
      "id": "simple-ocr",
      "earlyExitThreshold": 0.98,  // Skip this phase's successors if 98% confidence
      ...
    }
  ]
}
```

### Timeout Control

Prevent phases from running too long:

```json
{
  "globalSettings": {
    "maxTotalDurationMs": 10000  // 10 second total limit
  },
  "phases": [
    {
      "id": "expensive-phase",
      "maxDurationMs": 3000,  // 3 second phase limit
      ...
    }
  ]
}
```

### Parallelization

Enable parallel execution of independent phases:

```json
{
  "globalSettings": {
    "enableParallelization": true,
    "maxParallelism": 4  // Use 4 threads max
  }
}
```

## Phase Dependencies

Use `dependsOn` to ensure correct execution order:

```json
{
  "phases": [
    {
      "id": "phase1",
      "priority": 60,
      ...
    },
    {
      "id": "phase2",
      "priority": 59,
      "dependsOn": ["phase1"],  // Must run after phase1
      ...
    },
    {
      "id": "phase3",
      "priority": 58,
      "dependsOn": ["phase1", "phase2"],  // Must run after both
      ...
    }
  ]
}
```

## Source Generation

The pipeline configuration system uses .NET source generation for efficient JSON serialization:

```csharp
// Zero-allocation deserialization
var config = JsonSerializer.Deserialize(
    json,
    PipelineJsonContext.Default.PipelinesConfig
);

// Zero-allocation serialization
var json = JsonSerializer.Serialize(
    config,
    PipelineJsonContext.Default.PipelinesConfig
);
```

This provides:
- **No reflection cost**: Types are generated at compile time
- **AOT compatibility**: Works with native AOT compilation
- **Smaller binaries**: No metadata required at runtime
- **Better performance**: ~2-3x faster than reflection-based serialization

## File Locations

Pipelines are loaded from (in order of preference):

1. **Custom path**: Specified when creating `PipelineService`
2. **Config directory**: `./Config/pipelines.json`
3. **Application directory**: `./pipelines.json`
4. **User directory**: `%APPDATA%/LucidRAG/pipelines.json`
5. **Embedded resource**: Built-in default pipelines

## Best Practices

### 1. Start with Built-in Pipelines

Use the pre-configured pipelines as templates:
- Copy and modify existing pipelines
- Understand phase interactions before creating custom ones

### 2. Use Descriptive Names

```json
{
  "name": "fast-ocr-no-spellcheck",  // Good: describes what it does
  "name": "pipeline2"                 // Bad: meaningless
}
```

### 3. Set Realistic Estimates

Help users choose appropriate pipelines:

```json
{
  "estimatedDurationSeconds": 2.5,  // Measured average
  "accuracyImprovement": 25          // Tested improvement over baseline
}
```

### 4. Use Tags for Categorization

```json
{
  "tags": ["fast", "production", "realtime"]
}
```

### 5. Document Parameters

Add descriptions for custom parameters:

```json
{
  "parameters": {
    "maxFrames": 30,          // Maximum frames to process
    "ssimThreshold": 0.95,    // Similarity threshold for deduplication
    "enableVoting": true      // Enable temporal voting
  }
}
```

## Troubleshooting

### Pipeline Not Found

```bash
Error: Pipeline 'myocr' not found
```

**Solution**: Check pipeline name matches exactly (case-sensitive). Use `imagecli list-pipelines` to see available options.

### Phase Execution Order Wrong

If phases run in unexpected order, check:
- Priority values (higher = earlier)
- `dependsOn` dependencies
- Wave implementation priority

### Performance Issues

If pipeline is too slow:
- Enable early exit thresholds
- Reduce frame counts in phase parameters
- Use parallelization
- Switch to faster pipeline (e.g., simpleocr)

### JSON Validation Errors

If pipelines.json fails to load:
- Validate JSON syntax (use a JSON validator)
- Check all required fields are present
- Verify phase IDs in `dependsOn` exist
- Ensure `waveType` names match registered waves

## Future Enhancements

Planned features for the pipeline system:

1. **LLM Synthesis Wave**: Combine signals into coherent alt text
2. **Vision Model Integration**: Add Florence-2, CLIP analysis phases
3. **Dynamic Phase Selection**: Conditional phase execution based on signals
4. **Pipeline Inheritance**: Extend base pipelines with modifications
5. **Performance Profiling**: Built-in benchmarking and optimization suggestions
6. **Phase Marketplace**: Share custom phases and pipelines

## Contributing

To add a new phase:

1. Implement `IAnalysisWave` interface
2. Register in `ServiceCollectionExtensions.cs`
3. Add to `pipelines.json` configuration
4. Document signals emitted
5. Update this documentation

## References

- [IAnalysisWave Interface](./Services/Analysis/IAnalysisWave.cs)
- [WaveOrchestrator](./Services/Analysis/WaveOrchestrator.cs)
- [Pipeline Configuration Models](./Config/PipelineConfig.cs)
- [JSON Source Generation Context](./Config/PipelineJsonContext.cs)
