using System;
using System.Collections.Generic;

namespace Mostlylucid.DocSummarizer.Images.Coordination;

/// <summary>
/// Coordinator profiles for different processing scenarios.
/// Each profile configures lane concurrency, timeouts, and wave selection.
/// </summary>
public static class CoordinatorProfiles
{
    /// <summary>
    /// Single synchronous request - fast response, limited concurrency.
    /// Use for: API requests, UI interactions
    /// </summary>
    public static CoordinatorProfile SingleRequest => new()
    {
        Name = "single-request",
        Description = "Synchronous single image analysis",
        Lanes = new Dictionary<string, LaneConfig>
        {
            ["metadata"] = new() { Name = "metadata", MaxConcurrency = 4 },
            ["ocr"] = new() { Name = "ocr", MaxConcurrency = 1 },
            ["llm"] = new() { Name = "llm", MaxConcurrency = 1 },
            ["gpu"] = new() { Name = "gpu", MaxConcurrency = 1 }
        },
        DefaultTimeout = TimeSpan.FromSeconds(30),
        EnabledWaves = new HashSet<string>
        {
            "IdentityWave",
            "ColorWave",
            "AutoRoutingWave",
            "MlOcrWave",
            "Florence2Wave",
            "VisionLlmWave",
            "MotionWave"
        },
        Scope = new SignalScope
        {
            Sink = "docsummarizer.images",
            Coordinator = "single-request",
            Atom = "analysis"
        }
    };

    /// <summary>
    /// Background learning - async model improvement, embeddings, indexing.
    /// Use for: CLIP embeddings, knowledge graph updates, model fine-tuning signals
    /// </summary>
    public static CoordinatorProfile BackgroundLearning => new()
    {
        Name = "background-learning",
        Description = "Async background processing for model improvement",
        Lanes = new Dictionary<string, LaneConfig>
        {
            ["embedding"] = new() { Name = "embedding", MaxConcurrency = 2 },
            ["indexing"] = new() { Name = "indexing", MaxConcurrency = 4 },
            ["learning"] = new() { Name = "learning", MaxConcurrency = 1 }
        },
        DefaultTimeout = TimeSpan.FromMinutes(5),
        EnabledWaves = new HashSet<string>
        {
            "ClipEmbeddingWave",
            "EntityExtractionWave",
            "SimilarityIndexWave",
            "QualityLearningWave"
        },
        Scope = new SignalScope
        {
            Sink = "docsummarizer.images",
            Coordinator = "background-learning",
            Atom = "learning"
        },
        RunInBackground = true
    };

    /// <summary>
    /// Batch processing - high throughput, parallel execution.
    /// Use for: Directory processing, bulk imports
    /// </summary>
    public static CoordinatorProfile Batch => new()
    {
        Name = "batch",
        Description = "High-throughput batch processing",
        Lanes = new Dictionary<string, LaneConfig>
        {
            ["metadata"] = new() { Name = "metadata", MaxConcurrency = 8 },
            ["ocr"] = new() { Name = "ocr", MaxConcurrency = 4 },
            ["llm"] = new() { Name = "llm", MaxConcurrency = 2 },
            ["io"] = new() { Name = "io", MaxConcurrency = 8 }
        },
        DefaultTimeout = TimeSpan.FromMinutes(2),
        EnabledWaves = new HashSet<string>
        {
            "IdentityWave",
            "ColorWave",
            "AutoRoutingWave",
            "MlOcrWave",
            "Florence2Wave",
            "VisionLlmWave",
            "MotionWave",
            "ClipEmbeddingWave"
        },
        Scope = new SignalScope
        {
            Sink = "docsummarizer.images",
            Coordinator = "batch",
            Atom = "bulk"
        },
        MaxParallelImages = Environment.ProcessorCount
    };

    /// <summary>
    /// Streaming/real-time - low latency, minimal processing.
    /// Use for: Live preview, video frame analysis
    /// </summary>
    public static CoordinatorProfile Streaming => new()
    {
        Name = "streaming",
        Description = "Low-latency streaming analysis",
        Lanes = new Dictionary<string, LaneConfig>
        {
            ["fast"] = new() { Name = "fast", MaxConcurrency = 4 },
            ["motion"] = new() { Name = "motion", MaxConcurrency = 2 }
        },
        DefaultTimeout = TimeSpan.FromMilliseconds(500),
        EnabledWaves = new HashSet<string>
        {
            "IdentityWave",
            "ColorWave",
            "MotionWave"
        },
        Scope = new SignalScope
        {
            Sink = "docsummarizer.images",
            Coordinator = "streaming",
            Atom = "frame"
        },
        SkipCaching = true,
        LowLatencyMode = true
    };

    /// <summary>
    /// Quality analysis - comprehensive, all waves enabled.
    /// Use for: Final processing, archival, full feature extraction
    /// </summary>
    public static CoordinatorProfile Quality => new()
    {
        Name = "quality",
        Description = "Comprehensive quality analysis",
        Lanes = new Dictionary<string, LaneConfig>
        {
            ["metadata"] = new() { Name = "metadata", MaxConcurrency = 2 },
            ["ocr"] = new() { Name = "ocr", MaxConcurrency = 1 },
            ["llm"] = new() { Name = "llm", MaxConcurrency = 1 },
            ["gpu"] = new() { Name = "gpu", MaxConcurrency = 1 },
            ["verification"] = new() { Name = "verification", MaxConcurrency = 1 }
        },
        DefaultTimeout = TimeSpan.FromMinutes(2),
        EnabledWaves = null, // All waves enabled
        Scope = new SignalScope
        {
            Sink = "docsummarizer.images",
            Coordinator = "quality",
            Atom = "full"
        }
    };

    /// <summary>
    /// Stats only - ultra-fast, identity and color analysis only.
    /// Use for: Quick metadata extraction, thumbnailing, sorting
    /// </summary>
    public static CoordinatorProfile Stats => new()
    {
        Name = "stats",
        Description = "Ultra-fast stats extraction (identity + color only)",
        Lanes = new Dictionary<string, LaneConfig>
        {
            ["metadata"] = new() { Name = "metadata", MaxConcurrency = 8 }
        },
        DefaultTimeout = TimeSpan.FromSeconds(2),
        EnabledWaves = new HashSet<string>
        {
            "IdentityWave",
            "ColorWave"
        },
        Scope = new SignalScope
        {
            Sink = "docsummarizer.images",
            Coordinator = "stats",
            Atom = "quick"
        },
        SkipCaching = true,
        LowLatencyMode = true
    };

    /// <summary>
    /// Motion only - fast GIF/animation analysis.
    /// Use for: Quick motion detection, animation characterization
    /// </summary>
    public static CoordinatorProfile Motion => new()
    {
        Name = "motion",
        Description = "Fast motion/animation analysis",
        Lanes = new Dictionary<string, LaneConfig>
        {
            ["metadata"] = new() { Name = "metadata", MaxConcurrency = 4 },
            ["motion"] = new() { Name = "motion", MaxConcurrency = 2 }
        },
        DefaultTimeout = TimeSpan.FromSeconds(5),
        EnabledWaves = new HashSet<string>
        {
            "IdentityWave",
            "ColorWave",
            "MotionWave"
        },
        Scope = new SignalScope
        {
            Sink = "docsummarizer.images",
            Coordinator = "motion",
            Atom = "animation"
        },
        LowLatencyMode = true
    };

    /// <summary>
    /// Florence2 only - fast local ONNX captioning/OCR.
    /// Use for: Quick captions without external API calls
    /// </summary>
    public static CoordinatorProfile Florence2 => new()
    {
        Name = "florence2",
        Description = "Fast local Florence-2 captioning/OCR",
        Lanes = new Dictionary<string, LaneConfig>
        {
            ["metadata"] = new() { Name = "metadata", MaxConcurrency = 4 },
            ["gpu"] = new() { Name = "gpu", MaxConcurrency = 1 }
        },
        DefaultTimeout = TimeSpan.FromSeconds(10),
        EnabledWaves = new HashSet<string>
        {
            "IdentityWave",
            "ColorWave",
            "Florence2Wave"
        },
        Scope = new SignalScope
        {
            Sink = "docsummarizer.images",
            Coordinator = "florence2",
            Atom = "local"
        },
        LowLatencyMode = true
    };

    /// <summary>
    /// Auto pipeline - fast and good, balanced approach.
    /// Default pipeline that gives good results without being slow.
    /// Use for: Most common use cases, quick analysis with quality
    /// </summary>
    /// <summary>
    /// Fast analysis - quick metadata, color, motion without slow ML.
    /// Use for: Quick sorting, thumbnailing, basic categorization
    /// </summary>
    public static CoordinatorProfile Fast => new()
    {
        Name = "fast",
        Description = "Ultra-fast analysis (identity + color + motion + routing)",
        Lanes = new Dictionary<string, LaneConfig>
        {
            ["metadata"] = new() { Name = "metadata", MaxConcurrency = 8 }
        },
        DefaultTimeout = TimeSpan.FromSeconds(5),
        EnabledWaves = new HashSet<string>
        {
            "IdentityWave",
            "ColorWave",
            "AutoRoutingWave",
            "MotionWave"
        },
        Scope = new SignalScope
        {
            Sink = "docsummarizer.images",
            Coordinator = "fast",
            Atom = "quick"
        },
        LowLatencyMode = true
    };

    public static CoordinatorProfile Auto => new()
    {
        Name = "auto",
        Description = "Default - fast first, escalate to ML if needed",
        Lanes = new Dictionary<string, LaneConfig>
        {
            ["metadata"] = new() { Name = "metadata", MaxConcurrency = 4 },
            ["gpu"] = new() { Name = "gpu", MaxConcurrency = 1 }
        },
        DefaultTimeout = TimeSpan.FromSeconds(30),
        EnabledWaves = new HashSet<string>
        {
            "IdentityWave",
            "ColorWave",
            "AutoRoutingWave",
            "MotionWave"
            // Note: Florence2Wave NOT included by default
            // Use 'florence2' or 'caption' pipeline for ML analysis
        },
        Scope = new SignalScope
        {
            Sink = "docsummarizer.images",
            Coordinator = "auto",
            Atom = "balanced"
        },
        LowLatencyMode = true
    };

    /// <summary>
    /// Get profile by name (pipeline name mapping).
    /// </summary>
    public static CoordinatorProfile GetByName(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "stats" => Stats,
            "motion" => Motion,
            "fast" => Fast,
            "florence2" => Florence2,
            "streaming" => Streaming,
            "batch" => Batch,
            "quality" => Quality,
            "background-learning" or "learning" => BackgroundLearning,
            "auto" => Auto,  // Fast without ML by default
            "caption" or "alttext" or "socialmediaalt" or "vision" => SingleRequest, // Full analysis for caption tasks
            _ => Auto // Default to Auto for best experience
        };
    }
}

/// <summary>
/// Configuration profile for a coordinator.
/// </summary>
public sealed class CoordinatorProfile
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public Dictionary<string, LaneConfig> Lanes { get; init; } = new();
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public HashSet<string>? EnabledWaves { get; init; }
    public SignalScope Scope { get; init; } = new();
    public bool RunInBackground { get; init; }
    public int MaxParallelImages { get; init; } = 1;
    public bool SkipCaching { get; init; }
    public bool LowLatencyMode { get; init; }
}
