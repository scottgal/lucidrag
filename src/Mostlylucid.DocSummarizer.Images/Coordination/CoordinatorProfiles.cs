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
