namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Declares what signals a wave emits and what signals it depends on.
/// Used for dynamic pipeline construction and dependency resolution.
/// </summary>
public class WaveManifest
{
    /// <summary>
    /// Wave name (e.g., "IdentityWave", "ColorWave")
    /// </summary>
    public required string WaveName { get; init; }

    /// <summary>
    /// Wave priority (lower runs first)
    /// </summary>
    public int Priority { get; init; }

    /// <summary>
    /// Tags for this wave (used for tag-based filtering)
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Signals this wave emits. Supports glob patterns.
    /// E.g., ["identity.*", "identity.sha256"]
    /// </summary>
    public IReadOnlyList<string> Emits { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Required signals this wave depends on.
    /// If these signals are not available, the wave will fail or skip.
    /// E.g., ["identity.is_animated"]
    /// </summary>
    public IReadOnlyList<string> Requires { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Optional signals this wave can use if available.
    /// The wave will still run without these, but with reduced capability.
    /// E.g., ["color.dominant_*"]
    /// </summary>
    public IReadOnlyList<string> Optional { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Human-readable description of what this wave does
    /// </summary>
    public string? Description { get; init; }
}

/// <summary>
/// Static registry of all wave manifests.
/// This provides the metadata for dynamic pipeline construction.
/// </summary>
public static class WaveRegistry
{
    /// <summary>
    /// All registered wave manifests
    /// </summary>
    public static IReadOnlyList<WaveManifest> Manifests => _manifests;

    private static readonly List<WaveManifest> _manifests = new()
    {
        new WaveManifest
        {
            WaveName = "AutoRoutingWave",
            Priority = 98,
            Tags = ["routing", "auto", "optimization"],
            Emits = [
                "route.selected",       // "fast", "balanced", or "quality"
                "route.reason",         // Why this route was selected
                "route.skip_waves",     // List of waves to skip
                "route.quality_tier"    // 1=fast, 2=balanced, 3=quality
            ],
            Requires = ["identity.*", "content.text_likeliness", "quality.edge_density"],
            Optional = ["color.*"],
            Description = "Auto-routing based on fast signals (identity, color, complexity)"
        },

        new WaveManifest
        {
            WaveName = "IdentityWave",
            Priority = 10,
            Tags = ["identity"],
            Emits = [
                "identity.sha256",
                "identity.format",
                "identity.width",
                "identity.height",
                "identity.aspect_ratio",
                "identity.frame_count",
                "identity.is_animated",
                "identity.pixel_count",
                "identity.file_size"
            ],
            Requires = [],
            Optional = [],
            Description = "Basic image identity: format, dimensions, hash, animation detection"
        },

        new WaveManifest
        {
            WaveName = "ColorWave",
            Priority = 20,
            Tags = ["color", "quality"],
            Emits = [
                "color.dominant_rgb",
                "color.dominant_name",
                "color.dominant_confidence",
                "color.palette",
                "color.mean_luminance",
                "color.mean_saturation",
                "color.is_mostly_grayscale",
                "quality.edge_density",
                "quality.laplacian_variance",
                "content.type",
                "content.text_likeliness"
            ],
            Requires = [],
            Optional = [],
            Description = "Color analysis, quality metrics, content type detection"
        },

        new WaveManifest
        {
            WaveName = "MotionWave",
            Priority = 25,
            Tags = ["motion"],
            Emits = [
                "motion.has_motion",
                "motion.type",
                "motion.direction",
                "motion.magnitude",
                "motion.activity",
                "motion.temporal_consistency",
                "motion.regions",
                "complexity.scene_transitions",
                "complexity.unique_frames"
            ],
            Requires = ["identity.is_animated"],
            Optional = [],
            Description = "Motion analysis for animated images (GIFs)"
        },

        new WaveManifest
        {
            WaveName = "MlOcrWave",
            Priority = 28,
            Tags = ["ocr", "ml", "opencv", "content"],
            Emits = [
                // OpenCV fast detection signals
                "ocr.opencv.has_text",
                "ocr.opencv.text_regions",
                "ocr.opencv.has_subtitles",
                // ML OCR results
                "ocr.ml.text",
                "ocr.ml.has_text",
                "ocr.ml.frames_with_text",
                "ocr.ml.skipped",
                // Escalation signals for downstream OCR
                "ocr.escalation.run_tesseract",
                "ocr.escalation.skip_tesseract"
            ],
            Requires = [],
            Optional = ["identity.is_animated", "identity.frame_count", "content.text_likeliness"],
            Description = "Fast OpenCV MSER + Florence-2 text detection before Tesseract (caches text regions for targeted OCR)"
        },

        new WaveManifest
        {
            WaveName = "OcrWave",
            Priority = 30,
            Tags = ["ocr", "content"],
            Emits = [
                "ocr.text",
                "ocr.confidence",
                "content.extracted_text"
            ],
            Requires = [],
            Optional = [
                "content.text_likeliness",
                "ocr.escalation.run_tesseract",
                "ocr.escalation.skip_tesseract",
                "ocr.opencv.text_regions",  // Use cached OpenCV regions for targeted OCR
                "ocr.opencv.has_text"
            ],
            Description = "Basic OCR text extraction using Tesseract (uses OpenCV regions if available)"
        },

        new WaveManifest
        {
            WaveName = "AdvancedOcrWave",
            Priority = 35,
            Tags = ["ocr", "content"],
            Emits = [
                "ocr.voting.text",
                "ocr.voting.confidence",
                "ocr.voting.agreement",
                "ocr.full_text",
                "ocr.frame_count"
            ],
            Requires = [],
            Optional = [
                "identity.is_animated",
                "content.text_likeliness",
                "ocr.ml.text_changed_indices",       // Use ML-detected text change frames
                "ocr.opencv.per_frame_regions"       // Use per-frame text regions for targeted OCR
            ],
            Description = "Multi-frame OCR with voting for animated images (uses OpenCV regions if available)"
        },

        new WaveManifest
        {
            WaveName = "Florence2Wave",
            Priority = 55,
            Tags = ["florence2", "vision", "content"],
            Emits = [
                "florence2.caption",
                "florence2.ocr_text",
                "florence2.should_escalate",
                "florence2.duration_ms",
                "florence2.available"
                // Note: Does NOT emit vision.llm.caption - each wave uses its own namespace
            ],
            Requires = [],
            Optional = ["color.dominant_rgb", "color.dominant_name"],
            Description = "Fast local captioning using Florence-2 ONNX model"
        },

        new WaveManifest
        {
            WaveName = "VisionLlmWave",
            Priority = 60,
            Tags = ["vision", "llm", "content"],
            Emits = [
                "vision.llm.caption",
                "vision.llm.scene",
                "vision.llm.entities",
                "vision.llm.text",
                "vision.llm.model",
                "vision.llm.duration_ms"
            ],
            Requires = [],
            Optional = [
                "color.dominant_*",
                "motion.*",
                "ocr.*",
                "florence2.caption"
            ],
            Description = "Vision LLM captioning (Ollama/OpenAI/Anthropic)"
        },

        new WaveManifest
        {
            WaveName = "FaceDetectionWave",
            Priority = 40,
            Tags = ["face"],
            Emits = [
                "face.count",
                "face.regions",
                "face.embeddings"
            ],
            Requires = [],
            Optional = [],
            Description = "Face detection and embedding extraction"
        },

        new WaveManifest
        {
            WaveName = "ClipEmbeddingWave",
            Priority = 50,
            Tags = ["clip", "embedding"],
            Emits = [
                "clip.embedding",
                "clip.model"
            ],
            Requires = [],
            Optional = [],
            Description = "CLIP embedding for semantic similarity"
        },

        new WaveManifest
        {
            WaveName = "ContradictionWave",
            Priority = 100,
            Tags = ["validation"],
            Emits = [
                "validation.contradiction.count",
                "validation.contradiction.status",
                "validation.contradiction.details"
            ],
            Requires = [],
            Optional = ["*"], // Uses any available signals
            Description = "Cross-validates signals for contradictions"
        }
    };

    /// <summary>
    /// Find waves that emit a given signal pattern
    /// </summary>
    public static IEnumerable<WaveManifest> FindWavesEmitting(string signalPattern)
    {
        return _manifests.Where(m =>
            m.Emits.Any(e => SignalPatternMatches(e, signalPattern) || SignalPatternMatches(signalPattern, e)));
    }

    /// <summary>
    /// Find waves required to produce a set of signals (transitive closure)
    /// </summary>
    public static IEnumerable<WaveManifest> GetRequiredWaves(IEnumerable<string> signalPatterns)
    {
        var needed = new HashSet<string>();
        var waves = new List<WaveManifest>();

        // Find waves that emit the requested signals
        foreach (var pattern in signalPatterns)
        {
            var emitters = FindWavesEmitting(pattern);
            foreach (var wave in emitters)
            {
                if (!waves.Contains(wave))
                {
                    waves.Add(wave);
                    // Add required dependencies
                    foreach (var req in wave.Requires)
                        needed.Add(req);
                }
            }
        }

        // Recursively find waves for required signals
        while (needed.Count > 0)
        {
            var current = needed.ToList();
            needed.Clear();

            foreach (var req in current)
            {
                var emitters = FindWavesEmitting(req);
                foreach (var wave in emitters)
                {
                    if (!waves.Contains(wave))
                    {
                        waves.Add(wave);
                        foreach (var r in wave.Requires)
                            needed.Add(r);
                    }
                }
            }
        }

        return waves.OrderBy(w => w.Priority);
    }

    /// <summary>
    /// Get all signals that can be emitted by any wave
    /// </summary>
    public static IEnumerable<string> GetAllEmittedSignals()
    {
        return _manifests.SelectMany(m => m.Emits).Distinct();
    }

    /// <summary>
    /// Find orphan signals (required but never emitted)
    /// </summary>
    public static IEnumerable<string> FindOrphanSignals()
    {
        var emitted = GetAllEmittedSignals().ToHashSet();
        var required = _manifests.SelectMany(m => m.Requires).Distinct();

        return required.Where(r => !emitted.Any(e => SignalPatternMatches(e, r)));
    }

    /// <summary>
    /// Check if a signal pattern matches an actual signal key
    /// </summary>
    private static bool SignalPatternMatches(string pattern, string signal)
    {
        if (pattern == signal) return true;
        if (pattern == "*") return true;
        if (pattern.EndsWith("*"))
        {
            var prefix = pattern[..^1];
            return signal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
}
