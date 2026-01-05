using Mostlylucid.DocSummarizer.Images.Models;

namespace Mostlylucid.DocSummarizer.Images.Models.Dynamic;

/// <summary>
/// Structured ledger of salient image features accumulated from analysis waves.
/// Designed for LLM consumption - provides constrained, factual information for synthesis tasks.
/// </summary>
public class ImageLedger
{
    /// <summary>
    /// Image identity and format information
    /// </summary>
    public ImageIdentity Identity { get; set; } = new();

    /// <summary>
    /// Color palette and distribution
    /// </summary>
    public ColorLedger Colors { get; set; } = new();

    /// <summary>
    /// Detected objects, their types and locations
    /// </summary>
    public ObjectLedger Objects { get; set; } = new();

    /// <summary>
    /// Text extraction and OCR results
    /// </summary>
    public TextLedger Text { get; set; } = new();

    /// <summary>
    /// Motion and animation analysis
    /// </summary>
    public MotionLedger? Motion { get; set; }

    /// <summary>
    /// Image quality metrics
    /// </summary>
    public QualityLedger Quality { get; set; } = new();

    /// <summary>
    /// Visual composition and aesthetics
    /// </summary>
    public CompositionLedger Composition { get; set; } = new();

    /// <summary>
    /// Vision LLM and ML-based analysis results
    /// </summary>
    public VisionLedger Vision { get; set; } = new();

    /// <summary>
    /// Build ledger from dynamic image profile by extracting salient signals
    /// </summary>
    public static ImageLedger FromProfile(DynamicImageProfile profile)
    {
        var ledger = new ImageLedger();

        // Identity
        ledger.Identity = new ImageIdentity
        {
            Format = profile.GetValue<string>("identity.format") ?? "Unknown",
            Width = profile.GetValue<int>("identity.width"),
            Height = profile.GetValue<int>("identity.height"),
            AspectRatio = profile.GetValue<double>("identity.aspect_ratio"),
            FileSize = profile.GetValue<long>("identity.file_size"),
            IsAnimated = profile.GetValue<bool>("identity.is_animated") || profile.HasSignal("motion.frame_count")
        };

        // Colors
        ledger.Colors = new ColorLedger
        {
            DominantColors = profile.GetValue<List<DominantColor>>("color.dominant_colors") ?? new(),
            ColorCount = profile.GetValue<int>("color.unique_count"),
            MeanSaturation = profile.GetValue<double>("color.mean_saturation"),
            IsGrayscale = profile.GetValue<bool>("color.is_grayscale"),
            ColorPalette = profile.GetValue<List<string>>("color.palette") ?? new(),
            VibrantColor = profile.GetValue<string>("color.vibrant"),
            MutedColor = profile.GetValue<string>("color.muted")
        };

        // Objects (from ML detection if available)
        ledger.Objects = new ObjectLedger
        {
            DetectedObjects = profile.GetValue<List<DetectedObject>>("objects.detected") ?? new(),
            ObjectCount = profile.GetValue<int>("objects.count"),
            ObjectTypes = profile.GetValue<List<string>>("objects.types") ?? new(),
            Faces = profile.GetValue<List<FaceDetection>>("objects.faces") ?? new(),
            Landmarks = profile.GetValue<List<LandmarkDetection>>("objects.landmarks") ?? new(),
            // Face embeddings for PII-respecting search
            FaceEmbeddings = profile.GetValue<List<object>>("faces.embeddings") ?? new(),
            FaceEmbeddingHashes = profile.GetValue<List<string>>("faces.embedding_hashes") ?? new(),
            FaceClusters = profile.GetValue<List<object>>("faces.clusters") ?? new()
        };

        // Text
        ledger.Text = new TextLedger
        {
            // Priority: Corrected text from 3-tier pipeline > voting consensus > temporal median > raw OCR
            ExtractedText = profile.GetValue<string>("ocr.final.corrected_text") // Tier 2/3 corrections
                ?? profile.GetValue<string>("ocr.corrected.text") // Legacy Tier 3 signal
                ?? profile.GetValue<string>("ocr.voting.consensus_text") // Temporal voting
                ?? profile.GetValue<string>("ocr.temporal_median.full_text") // Temporal median
                ?? profile.GetValue<string>("ocr.text") // Raw OCR
                ?? string.Empty,
            Confidence = profile.GetValue<double?>("ocr.voting.confidence")
                ?? profile.GetValue<double?>("ocr.confidence")
                ?? 0.0,
            TextRegions = profile.GetValue<List<TextRegion>>("ocr.regions") ?? new(),
            SpellCheckScore = profile.GetValue<double>("ocr.quality.spell_check_score"),
            IsGarbled = profile.GetValue<bool>("ocr.quality.is_garbled"),
            TextLikeliness = profile.GetValue<double>("content.text_likeliness")
        };

        // Calculate WordCount from the same text used in ExtractedText
        ledger.Text.WordCount = CountWords(ledger.Text.ExtractedText);

        // Motion (for animations)
        if (profile.HasSignal("motion.frame_count"))
        {
            ledger.Motion = new MotionLedger
            {
                FrameCount = profile.GetValue<int?>("motion.frame_count")
                    ?? profile.GetValue<int?>("ocr.frames.extracted")
                    ?? 0,
                Duration = profile.GetValue<double>("motion.duration"),
                FrameRate = profile.GetValue<double>("motion.frame_rate"),
                OpticalFlowMagnitude = profile.GetValue<double>("motion.optical_flow_magnitude"),
                MotionIntensity = profile.GetValue<double>("motion.intensity"),
                StabilizationQuality = profile.GetValue<double>("ocr.stabilization.confidence"),
                IsLooping = profile.GetValue<bool>("motion.is_looping")
            };
        }

        // Quality
        ledger.Quality = new QualityLedger
        {
            Sharpness = profile.GetValue<double>("quality.sharpness"),
            CompressionArtifacts = profile.GetValue<double>("quality.compression_artifacts"),
            Noise = profile.GetValue<double>("quality.noise"),
            Blur = profile.GetValue<double>("quality.blur"),
            Exposure = GetExposureQuality(profile),
            OverallQuality = profile.GetValue<double>("quality.overall")
        };

        // Composition
        ledger.Composition = new CompositionLedger
        {
            EdgeDensity = profile.GetValue<double>("visual.edge_density"),
            Complexity = profile.GetValue<double>("visual.complexity"),
            Symmetry = profile.GetValue<double>("composition.symmetry"),
            RuleOfThirds = profile.GetValue<double>("composition.rule_of_thirds"),
            SalientRegions = profile.GetValue<List<SaliencyRegion>>("content.salient_regions") ?? new(),
            Brightness = profile.GetValue<double>("visual.mean_luminance"),
            Contrast = profile.GetValue<double>("visual.luminance_stddev")
        };

        // Vision LLM and ML features
        ledger.Vision = new VisionLedger
        {
            Caption = profile.GetValue<string>("vision.llm.caption"),
            DetailedDescription = profile.GetValue<string>("vision.llm.detailed_description"),
            Scene = profile.GetValue<string>("vision.llm.scene"),
            Entities = profile.GetValue<List<EntityDetection>>("vision.llm.entities") ?? new(),
            ClipEmbedding = profile.GetValue<float[]>("vision.clip.embedding"),
            ClipEmbeddingHash = profile.GetValue<string>("vision.clip.embedding_hash"),
            MlObjectDetections = profile.GetValue<List<MlObjectDetection>>("vision.ml.objects") ?? new(),
            SceneConfidence = profile.GetValue<double?>("vision.ml.scene_confidence")
        };

        return ledger;
    }

    /// <summary>
    /// Generate a structured summary suitable for LLM prompts
    /// </summary>
    public string ToLlmSummary()
    {
        var parts = new List<string>();

        // Format and dimensions
        parts.Add($"Format: {Identity.Format}, {Identity.Width}×{Identity.Height} ({Identity.AspectRatio:F2} aspect ratio)");

        // Animation
        if (Identity.IsAnimated && Motion != null)
        {
            parts.Add($"Animation: {Motion.FrameCount} frames{(Motion.Duration.HasValue ? $", {Motion.Duration:F1}s duration" : "")}");
        }

        // Colors (group by name and sum percentages)
        if (Colors.DominantColors.Count > 0)
        {
            var groupedColors = Colors.DominantColors
                .GroupBy(c => c.Name ?? $"#{c.Hex}")
                .Select(g => new { Name = g.Key, Percentage = g.Sum(c => c.Percentage) })
                .OrderByDescending(c => c.Percentage)
                .Take(5);

            var colorList = string.Join(", ", groupedColors.Select(c => $"{c.Name}({c.Percentage:F0}%)"));
            parts.Add($"Colors: {colorList}");
        }

        if (Colors.IsGrayscale)
        {
            parts.Add("Image is mostly grayscale");
        }

        // Text content
        if (!string.IsNullOrWhiteSpace(Text.ExtractedText))
        {
            var preview = Text.ExtractedText.Length > 100
                ? Text.ExtractedText.Substring(0, 100) + "..."
                : Text.ExtractedText;
            parts.Add($"Text (OCR, {Text.Confidence:F0}% confident): \"{preview}\"");

            if (Text.SpellCheckScore.HasValue)
            {
                parts.Add($"Text quality: {Text.SpellCheckScore:F0}% correctly spelled");
            }
        }
        else if (Text.TextLikeliness > 0.5)
        {
            parts.Add($"Likely contains text ({Text.TextLikeliness:F0}% confidence) but OCR failed");
        }

        // Vision LLM caption (primary description)
        if (!string.IsNullOrWhiteSpace(Vision.Caption))
        {
            parts.Add($"Caption: {Vision.Caption}");
        }

        // Scene classification
        if (!string.IsNullOrWhiteSpace(Vision.Scene))
        {
            parts.Add($"Scene: {Vision.Scene}");
        }

        // Entities from vision LLM
        if (Vision.Entities.Count > 0)
        {
            var entitySummary = string.Join(", ", Vision.Entities
                .GroupBy(e => e.Type)
                .Select(g => $"{g.Count()} {g.Key}(s)"));
            parts.Add($"Entities: {entitySummary}");

            // Detailed entity list
            var entityDetails = string.Join(", ", Vision.Entities.Take(5).Select(e => e.Label));
            if (Vision.Entities.Count > 5)
            {
                entityDetails += $" (and {Vision.Entities.Count - 5} more)";
            }
            parts.Add($"  Detected: {entityDetails}");
        }

        // Objects
        if (Objects.ObjectCount > 0)
        {
            parts.Add($"Objects detected: {Objects.ObjectCount} ({string.Join(", ", Objects.ObjectTypes)})");
        }

        // Faces (with privacy-preserving details)
        if (Objects.Faces.Count > 0)
        {
            var faceCount = Objects.Faces.Count;
            var clusterCount = Objects.FaceClusters.Count;

            if (clusterCount > 0 && clusterCount < faceCount)
            {
                parts.Add($"Faces: {faceCount} detected ({clusterCount} unique person/people)");
            }
            else
            {
                parts.Add($"Faces: {faceCount} detected");
            }

            // Add embedding info for RAG searchability
            if (Objects.FaceEmbeddingHashes.Count > 0)
            {
                parts.Add($"Face signatures available for similarity search (PII-respecting)");
            }
        }

        // Quality
        if (Quality.Sharpness.HasValue)
        {
            var sharpnessDesc = Quality.Sharpness switch
            {
                > 1000 => "very sharp",
                > 500 => "sharp",
                > 100 => "moderate sharpness",
                _ => "soft/blurry"
            };
            parts.Add($"Image quality: {sharpnessDesc}");
        }

        // Composition
        if (Composition.Complexity.HasValue)
        {
            var complexityDesc = Composition.Complexity switch
            {
                > 0.7 => "complex composition",
                > 0.4 => "moderate complexity",
                _ => "simple composition"
            };
            parts.Add($"Visual: {complexityDesc}");
        }

        if (Composition.SalientRegions.Count > 0)
        {
            parts.Add($"Focus regions: {Composition.SalientRegions.Count} areas of interest");
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Generate a concise summary for alt text generation
    /// </summary>
    public string ToAltTextContext()
    {
        var parts = new List<string>();

        // Type of image
        if (Identity.IsAnimated)
        {
            parts.Add("Animated GIF");
        }
        else
        {
            parts.Add($"{Identity.Format} image");
        }

        // Main content
        if (!string.IsNullOrWhiteSpace(Text.ExtractedText))
        {
            parts.Add($"containing text: \"{Text.ExtractedText}\"");
        }

        // Objects
        if (Objects.ObjectCount > 0)
        {
            parts.Add($"showing {string.Join(", ", Objects.ObjectTypes)}");
        }

        // Faces
        if (Objects.Faces.Count > 0)
        {
            var faceCount = Objects.Faces.Count;
            var clusterCount = Objects.FaceClusters.Count;

            if (clusterCount > 0 && clusterCount < faceCount)
            {
                parts.Add($"with {clusterCount} person/people (multiple shots)");
            }
            else
            {
                parts.Add($"with {faceCount} {(faceCount == 1 ? "person" : "people")}");
            }
        }

        // Color theme
        if (Colors.IsGrayscale)
        {
            parts.Add("in grayscale");
        }
        else if (Colors.DominantColors.Count > 0)
        {
            var topColor = Colors.DominantColors.First().Name ?? Colors.DominantColors.First().Hex;
            parts.Add($"predominantly {topColor}");
        }

        // Quality/condition
        if (Quality.Blur.HasValue && Quality.Blur > 0.7)
        {
            parts.Add("(blurry)");
        }

        return string.Join(" ", parts);
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static ExposureQuality GetExposureQuality(DynamicImageProfile profile)
    {
        var clippedBlacks = profile.GetValue<double>("visual.clipped_blacks_percent");
        var clippedWhites = profile.GetValue<double>("visual.clipped_whites_percent");
        var meanLuminance = profile.GetValue<double>("visual.mean_luminance");

        if (clippedWhites > 10)
            return ExposureQuality.Overexposed;
        if (clippedBlacks > 10)
            return ExposureQuality.Underexposed;
        if (meanLuminance < 0.3)
            return ExposureQuality.Dark;
        if (meanLuminance > 0.7)
            return ExposureQuality.Bright;

        return ExposureQuality.Good;
    }
}

/// <summary>
/// Image identity and format
/// </summary>
public class ImageIdentity
{
    public string Format { get; set; } = "Unknown";
    public int Width { get; set; }
    public int Height { get; set; }
    public double AspectRatio { get; set; }
    public long FileSize { get; set; }
    public bool IsAnimated { get; set; }
}

/// <summary>
/// Color information ledger
/// </summary>
public class ColorLedger
{
    public List<DominantColor> DominantColors { get; set; } = new();
    public int ColorCount { get; set; }
    public double MeanSaturation { get; set; }
    public bool IsGrayscale { get; set; }
    public List<string> ColorPalette { get; set; } = new();
    public string? VibrantColor { get; set; }
    public string? MutedColor { get; set; }
}

/// <summary>
/// Detected objects ledger
/// </summary>
public class ObjectLedger
{
    public List<DetectedObject> DetectedObjects { get; set; } = new();
    public int ObjectCount { get; set; }
    public List<string> ObjectTypes { get; set; } = new();
    public List<FaceDetection> Faces { get; set; } = new();
    public List<LandmarkDetection> Landmarks { get; set; } = new();

    /// <summary>
    /// PII-respecting face embeddings for "same person" clustering.
    /// Never stores actual face images, only anonymized 512-dim vectors.
    /// </summary>
    public List<object> FaceEmbeddings { get; set; } = new();

    /// <summary>
    /// Quick-lookup hashes of face embeddings.
    /// </summary>
    public List<string> FaceEmbeddingHashes { get; set; } = new();

    /// <summary>
    /// Face clusters (groups of faces that appear to be the same person).
    /// </summary>
    public List<object> FaceClusters { get; set; } = new();
}

/// <summary>
/// Text and OCR ledger
/// </summary>
public class TextLedger
{
    public string ExtractedText { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public List<TextRegion> TextRegions { get; set; } = new();
    public double? SpellCheckScore { get; set; }
    public bool IsGarbled { get; set; }
    public double TextLikeliness { get; set; }
    public int WordCount { get; set; }
}

/// <summary>
/// Motion and animation ledger
/// </summary>
public class MotionLedger
{
    public int FrameCount { get; set; }
    public double? Duration { get; set; }
    public double? FrameRate { get; set; }
    public double OpticalFlowMagnitude { get; set; }
    public double MotionIntensity { get; set; }
    public double StabilizationQuality { get; set; }
    public bool IsLooping { get; set; }
}

/// <summary>
/// Quality metrics ledger
/// </summary>
public class QualityLedger
{
    public double? Sharpness { get; set; }
    public double? CompressionArtifacts { get; set; }
    public double? Noise { get; set; }
    public double? Blur { get; set; }
    public ExposureQuality Exposure { get; set; }
    public double? OverallQuality { get; set; }
}

/// <summary>
/// Visual composition ledger
/// </summary>
public class CompositionLedger
{
    public double EdgeDensity { get; set; }
    public double? Complexity { get; set; }
    public double? Symmetry { get; set; }
    public double? RuleOfThirds { get; set; }
    public List<SaliencyRegion> SalientRegions { get; set; } = new();
    public double Brightness { get; set; }
    public double Contrast { get; set; }
}

/// <summary>
/// Detected object with type, confidence, and location
/// </summary>
public class DetectedObject
{
    public string Type { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public BoundingBox Location { get; set; } = new();
    public Dictionary<string, object>? Attributes { get; set; }
}

/// <summary>
/// Face detection result
/// </summary>
public class FaceDetection
{
    public BoundingBox Location { get; set; } = new();
    public double Confidence { get; set; }
    public Dictionary<string, double>? Landmarks { get; set; }
    public Dictionary<string, double>? Attributes { get; set; } // age, gender, emotion, etc.
}

/// <summary>
/// Landmark detection (buildings, monuments, etc.)
/// </summary>
public class LandmarkDetection
{
    public string Name { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public BoundingBox Location { get; set; } = new();
}

/// <summary>
/// Text region with location
/// </summary>
public class TextRegion
{
    public string Text { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public BoundingBox Location { get; set; } = new();
}

/// <summary>
/// Bounding box for object/text locations
/// </summary>
public class BoundingBox
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>
/// Exposure quality classification
/// </summary>
public enum ExposureQuality
{
    Good,
    Overexposed,
    Underexposed,
    Dark,
    Bright
}

/// <summary>
/// Vision LLM and ML-based analysis results
/// </summary>
public class VisionLedger
{
    /// <summary>
    /// Primary caption from vision LLM (10-15 words, concise)
    /// </summary>
    public string? Caption { get; set; }

    /// <summary>
    /// Detailed description from vision LLM (100+ words, comprehensive)
    /// </summary>
    public string? DetailedDescription { get; set; }

    /// <summary>
    /// Scene classification (indoor, outdoor, food, nature, etc.)
    /// </summary>
    public string? Scene { get; set; }

    /// <summary>
    /// Confidence score for scene classification (0-1)
    /// </summary>
    public double? SceneConfidence { get; set; }

    /// <summary>
    /// Entities extracted by vision LLM (people, animals, objects, text)
    /// </summary>
    public List<EntityDetection> Entities { get; set; } = new();

    /// <summary>
    /// CLIP embedding for image similarity search (512-dim vector)
    /// </summary>
    public float[]? ClipEmbedding { get; set; }

    /// <summary>
    /// Hash of CLIP embedding for deduplication
    /// </summary>
    public string? ClipEmbeddingHash { get; set; }

    /// <summary>
    /// ML-based object detections (YOLO, DETR, etc.)
    /// </summary>
    public List<MlObjectDetection> MlObjectDetections { get; set; } = new();
}

/// <summary>
/// Entity detected by vision LLM
/// </summary>
public class EntityDetection
{
    public string Type { get; set; } = "object"; // person, animal, object, text
    public string Label { get; set; } = string.Empty;
    public double Confidence { get; set; } = 0.8;
    public Dictionary<string, string>? Attributes { get; set; }
}

/// <summary>
/// ML-based object detection result
/// </summary>
public class MlObjectDetection
{
    public string Label { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public BoundingBox Location { get; set; } = new();
    public Dictionary<string, double>? Attributes { get; set; }
}
