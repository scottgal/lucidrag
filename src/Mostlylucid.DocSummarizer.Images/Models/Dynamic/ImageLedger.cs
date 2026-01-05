using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer;
using Mostlylucid.DocSummarizer.Models;

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
    /// Summarize long OCR text using DocSummarizer.
    /// Populates Text.ExtractedTextSummary if text exceeds threshold.
    /// </summary>
    /// <param name="summarizer">DocSummarizer instance (configured via DI)</param>
    /// <param name="minLengthForSummary">Minimum text length to trigger summarization (default: 200)</param>
    /// <param name="ct">Cancellation token</param>
    public async Task SummarizeOcrTextAsync(
        IDocumentSummarizer summarizer,
        int minLengthForSummary = 200,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Text.ExtractedText) ||
            Text.ExtractedText.Length < minLengthForSummary)
            return;

        try
        {
            // Let DocSummarizer use its configured mode (BERT, MapReduce, etc.)
            var summary = await summarizer.SummarizeMarkdownAsync(
                Text.ExtractedText,
                documentId: "ocr-text",
                focusQuery: "key information",
                cancellationToken: ct);

            if (!string.IsNullOrWhiteSpace(summary.ExecutiveSummary))
            {
                Text.ExtractedTextSummary = summary.ExecutiveSummary;
            }
        }
        catch
        {
            // If summarization fails, we'll use the original text (truncated)
        }
    }

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

        // Motion (for animations) - check multiple signal keys
        var hasMotionSignals = profile.HasSignal("motion.has_motion") ||
                               profile.HasSignal("motion.type") ||
                               profile.HasSignal("identity.is_animated");

        if (hasMotionSignals)
        {
            var movingObjectsValue = profile.GetValue<object>("motion.moving_objects");
            var movingObjectsList = movingObjectsValue switch
            {
                List<string> list => list,
                IEnumerable<string> enumerable => enumerable.ToList(),
                _ => new List<string>()
            };

            ledger.Motion = new MotionLedger
            {
                FrameCount = profile.GetValue<int?>("identity.frame_count")
                    ?? profile.GetValue<int?>("motion.frame_count")
                    ?? profile.GetValue<int?>("ocr.frames.extracted")
                    ?? 0,
                Duration = profile.GetValue<double>("motion.duration"),
                FrameRate = profile.GetValue<double>("motion.frame_rate"),
                OpticalFlowMagnitude = profile.GetValue<double>("motion.magnitude"),
                MotionIntensity = profile.GetValue<double>("motion.activity"),
                StabilizationQuality = profile.GetValue<double>("ocr.stabilization.confidence"),
                IsLooping = profile.GetValue<bool>("motion.is_looping"),

                // New MotionWave signals
                HasMotion = profile.GetValue<bool>("motion.has_motion"),
                MotionType = profile.GetValue<string>("motion.type"),
                Direction = profile.GetValue<string>("motion.direction"),
                Magnitude = profile.GetValue<double>("motion.magnitude"),
                Activity = profile.GetValue<double>("motion.activity"),
                Summary = profile.GetValue<string>("motion.summary"),
                MovingObjects = movingObjectsList
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
    /// Generate a concise, WCAG-compliant alt text for accessibility
    /// Focuses on what the image shows rather than technical details
    /// </summary>
    /// <param name="maxLength">Maximum character length (WCAG recommends 125)</param>
    public string ToAltTextContext(int maxLength = 0)
    {
        var parts = new List<string>();
        var hasOcrText = !string.IsNullOrWhiteSpace(Text.ExtractedText);

        // Start with what/who is in the image (most important for accessibility)
        var subjects = new List<string>();

        // People first
        if (Objects.Faces.Count > 0)
        {
            var faceCount = Objects.Faces.Count;
            var clusterCount = Objects.FaceClusters.Count;

            if (clusterCount > 0 && clusterCount < faceCount)
            {
                subjects.Add(clusterCount == 1 ? "A person" : $"{clusterCount} people");
            }
            else
            {
                subjects.Add(faceCount == 1 ? "A person" : $"{faceCount} people");
            }
        }

        // Objects/entities
        if (Objects.ObjectCount > 0)
        {
            // Use the detected objects, not technical labels
            var objectList = Objects.ObjectTypes.Take(3).ToList();
            if (objectList.Count > 0 && subjects.Count == 0)
            {
                subjects.Add(FormatObjectList(objectList));
            }
            else if (objectList.Count > 0)
            {
                subjects.Add($"with {FormatObjectList(objectList)}");
            }
        }

        // What's happening (motion for animated images)
        if (Identity.IsAnimated && Motion != null)
        {
            if (subjects.Count > 0)
            {
                parts.Add(string.Join(" ", subjects));

                // Describe the action if we know what's moving
                if (Motion.MovingObjects.Count > 0)
                {
                    var movingThings = string.Join(" and ", Motion.MovingObjects);
                    parts.Add($"{movingThings} moving");
                }
                else if (Motion.HasMotion)
                {
                    // Simple motion description
                    var motionDesc = GetSimpleMotionDescription();
                    if (!string.IsNullOrEmpty(motionDesc))
                    {
                        parts.Add(motionDesc);
                    }
                }

                parts.Add("(animated)");
            }
            else
            {
                // No detected subjects, describe motion directly
                if (Motion.MovingObjects.Count > 0)
                {
                    parts.Add($"Animation showing {string.Join(" and ", Motion.MovingObjects)} moving");
                }
                else if (Motion.HasMotion)
                {
                    parts.Add($"Animated image with {GetSimpleMotionDescription()}");
                }
                else
                {
                    parts.Add("Animated image");
                }
            }
        }
        else if (subjects.Count > 0)
        {
            parts.Add(string.Join(" ", subjects));
        }
        else
        {
            // Fallback based on detected type
            parts.Add(Colors.IsGrayscale ? "A grayscale image" : "An image");
        }

        // Text content (CRITICAL for accessibility - OCR text must always be included)
        if (hasOcrText)
        {
            // Use DocSummarizer summary if available (for long OCR text)
            var ocrToUse = !string.IsNullOrEmpty(Text.ExtractedTextSummary)
                ? Text.ExtractedTextSummary
                : Text.ExtractedText;

            // Calculate space available for OCR text
            var baseParts = string.Join(". ", parts);
            var textPrefix = "Text: \"";
            var textSuffix = "\"";
            var overhead = baseParts.Length + textPrefix.Length + textSuffix.Length + 2; // +2 for ". "

            // If we have a max length, calculate how much space is left for OCR
            var ocrMaxLength = maxLength > 0
                ? Math.Max(20, maxLength - overhead) // At least 20 chars for OCR summary
                : 80; // Default to 80 chars when no limit

            var textPreview = ocrToUse.Length > ocrMaxLength
                ? ocrToUse[..(ocrMaxLength - 3)] + "..."
                : ocrToUse;
            parts.Add($"Text: \"{textPreview}\"");
        }

        var result = string.Join(". ", parts).TrimEnd('.');

        // If result still exceeds maxLength, prioritize OCR text
        if (maxLength > 0 && result.Length > maxLength && hasOcrText)
        {
            // OCR-first approach: Minimal context + OCR summary
            var ocrText = Text.ExtractedText;
            var contextBudget = Math.Max(30, maxLength - ocrText.Length - 15); // "Image with text: "

            var briefContext = GetBriefContext(contextBudget);
            var ocrBudget = maxLength - briefContext.Length - 8; // " Text: \"\""
            var ocrSummary = ocrText.Length > ocrBudget
                ? ocrText[..(ocrBudget - 3)] + "..."
                : ocrText;

            result = $"{briefContext} Text: \"{ocrSummary}\"";
        }

        return result;
    }

    /// <summary>
    /// Get a brief context string for OCR-priority alt text
    /// </summary>
    private string GetBriefContext(int maxLength)
    {
        // Priority: Person > Animated > Object > Generic
        if (Objects.Faces.Count > 0)
        {
            var desc = Objects.Faces.Count == 1 ? "Person" : $"{Objects.Faces.Count} people";
            if (Identity.IsAnimated) desc += " (animated)";
            return desc.Length <= maxLength ? desc : desc[..(maxLength - 3)] + "...";
        }

        if (Identity.IsAnimated)
        {
            return "Animated image".Length <= maxLength ? "Animated image" : "GIF";
        }

        if (Objects.ObjectCount > 0 && Objects.ObjectTypes.Count > 0)
        {
            var obj = Objects.ObjectTypes[0];
            return obj.Length <= maxLength ? obj : obj[..(maxLength - 3)] + "...";
        }

        return "Image";
    }

    /// <summary>
    /// Get a simple, natural motion description
    /// </summary>
    private string GetSimpleMotionDescription()
    {
        if (Motion == null) return "";

        // Simplify technical motion descriptions
        var type = Motion.MotionType?.ToLowerInvariant() ?? "";

        return type switch
        {
            "stationary" => "subtle movement",
            "subtle" => "subtle movement",
            "localized" => "localized movement",
            "general" => "general movement",
            "rapid" => "rapid movement",
            "camera_shake" => "camera movement",
            "pan" => "panning motion",
            "zoom" => "zooming",
            _ when Motion.Magnitude < 2 => "subtle movement",
            _ when Motion.Magnitude < 5 => "moderate movement",
            _ => "active movement"
        };
    }

    /// <summary>
    /// Format a list of objects naturally
    /// </summary>
    private static string FormatObjectList(List<string> objects)
    {
        if (objects.Count == 0) return "";
        if (objects.Count == 1) return objects[0];
        if (objects.Count == 2) return $"{objects[0]} and {objects[1]}";
        return $"{string.Join(", ", objects.Take(objects.Count - 1))}, and {objects.Last()}";
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

    /// <summary>
    /// Generate a synthesized summary using confidence-weighted signal fusion.
    /// Uses RRF-like ranking to preserve high-confidence signals while reducing noise.
    /// </summary>
    /// <param name="purpose">Output purpose: alttext, caption, verbose, technical, tool</param>
    /// <param name="maxSignals">Maximum number of signal categories to include</param>
    public string ToSalienceSummary(string purpose = "caption", int maxSignals = 6)
    {
        // Build ranked signal list with confidence scores
        var rankedSignals = BuildRankedSignals(purpose);

        // Apply RRF-like fusion: rank by (salience_weight * confidence)
        var fused = rankedSignals
            .OrderByDescending(s => s.Score)
            .Take(maxSignals)
            .ToList();

        // Format output
        return string.Join(" | ", fused.Select(s => s.Display));
    }

    /// <summary>
    /// Build ranked signal list with salience weights per purpose.
    /// Weights define how important each signal category is for the purpose.
    /// </summary>
    private List<RankedSignal> BuildRankedSignals(string purpose)
    {
        var signals = new List<RankedSignal>();
        var purposeLower = purpose.ToLowerInvariant();

        // Define salience weights per purpose (0-1, higher = more important)
        var weights = GetSalienceWeights(purposeLower);

        // Subjects/People (almost always high priority)
        if (Objects.Faces.Count > 0)
        {
            var peopleCount = Objects.FaceClusters.Count > 0 ? Objects.FaceClusters.Count : Objects.Faces.Count;
            var conf = 0.95; // Face detection is high confidence
            signals.Add(new RankedSignal(
                "subjects",
                weights.GetValueOrDefault("subjects", 0.9) * conf,
                $"[Who] {(peopleCount == 1 ? "1 person" : $"{peopleCount} people")}"
            ));
        }

        // Vision entities (combine and dedupe)
        if (Vision.Entities.Count > 0)
        {
            var entities = Vision.Entities
                .Where(e => e.Type != "text")
                .GroupBy(e => e.Label.ToLowerInvariant())
                .Select(g => new { Label = g.First().Label, AvgConf = g.Average(e => e.Confidence) })
                .OrderByDescending(e => e.AvgConf)
                .Take(5);

            if (entities.Any())
            {
                var avgConf = entities.Average(e => e.AvgConf);
                signals.Add(new RankedSignal(
                    "entities",
                    weights.GetValueOrDefault("entities", 0.8) * avgConf,
                    $"[Visible] {string.Join(", ", entities.Select(e => e.Label))}"
                ));
            }
        }

        // Scene classification
        if (!string.IsNullOrWhiteSpace(Vision.Scene))
        {
            var sceneConf = Vision.SceneConfidence ?? 0.8;
            signals.Add(new RankedSignal(
                "scene",
                weights.GetValueOrDefault("scene", 0.7) * sceneConf,
                $"[Scene] {Vision.Scene}"
            ));
        }

        // Motion/Animation (high priority for animated images)
        if (Identity.IsAnimated && Motion != null)
        {
            var motionConf = Motion.HasMotion ? 0.9 : 0.7;
            var motionDesc = Motion.MovingObjects.Count > 0
                ? $"{string.Join(", ", Motion.MovingObjects)} moving"
                : Motion.Summary ?? $"{Motion.FrameCount} frames";

            signals.Add(new RankedSignal(
                "motion",
                weights.GetValueOrDefault("motion", 0.85) * motionConf,
                $"[Animation] {motionDesc}"
            ));
        }

        // Text content
        if (!string.IsNullOrWhiteSpace(Text.ExtractedText))
        {
            var textConf = Text.Confidence / 100.0;
            var textPreview = Text.ExtractedText.Length > 60
                ? Text.ExtractedText.Substring(0, 57) + "..."
                : Text.ExtractedText;

            signals.Add(new RankedSignal(
                "text",
                weights.GetValueOrDefault("text", 0.8) * textConf,
                $"[Text] \"{textPreview}\""
            ));
        }

        // Colors (combine dominant colors)
        if (Colors.DominantColors.Count > 0)
        {
            // Group by color name and sum percentages for deduplication
            var grouped = Colors.DominantColors
                .GroupBy(c => c.Name ?? c.Hex)
                .Select(g => new { Name = g.Key, TotalPct = g.Sum(c => c.Percentage) })
                .OrderByDescending(c => c.TotalPct)
                .Take(3);

            var colorConf = 0.9; // Color detection is reliable
            signals.Add(new RankedSignal(
                "colors",
                weights.GetValueOrDefault("colors", 0.4) * colorConf,
                $"[Colors] {string.Join(", ", grouped.Select(c => c.Name))}"
            ));
        }

        // Quality (synthesize from multiple metrics)
        if (Quality.Sharpness.HasValue || Quality.Blur.HasValue)
        {
            var qualityScore = (Quality.OverallQuality ?? 0.5);
            var qualityDesc = Quality.Sharpness switch
            {
                > 1000 => "sharp",
                > 500 => "clear",
                > 100 => "moderate",
                _ => "soft"
            };
            signals.Add(new RankedSignal(
                "quality",
                weights.GetValueOrDefault("quality", 0.3) * qualityScore,
                $"[Quality] {qualityDesc}"
            ));
        }

        // Identity/Format
        signals.Add(new RankedSignal(
            "identity",
            weights.GetValueOrDefault("identity", 0.2) * 1.0,
            $"[Format] {Identity.Format}, {Identity.Width}×{Identity.Height}"
        ));

        // Vision caption (if different from what we'd synthesize)
        if (!string.IsNullOrWhiteSpace(Vision.Caption))
        {
            signals.Add(new RankedSignal(
                "caption",
                weights.GetValueOrDefault("caption", 0.85) * 0.9,
                $"[VisionCaption] {Vision.Caption}"
            ));
        }

        return signals;
    }

    /// <summary>
    /// Get salience weights for each signal category based on output purpose.
    /// Higher weight = more important for this purpose.
    /// </summary>
    private static Dictionary<string, double> GetSalienceWeights(string purpose)
    {
        return purpose switch
        {
            "alttext" => new Dictionary<string, double>
            {
                ["subjects"] = 1.0,   // Who is in it - most important
                ["entities"] = 0.9,   // What's visible
                ["motion"] = 0.85,    // Action (critical for animations)
                ["text"] = 0.7,       // Important if present
                ["scene"] = 0.5,      // Context
                ["colors"] = 0.1,     // Usually not needed
                ["quality"] = 0.0,    // Never include
                ["identity"] = 0.0,   // Never include
                ["caption"] = 0.95,   // Use if available
            },
            "caption" or "socialmedia" => new Dictionary<string, double>
            {
                ["subjects"] = 1.0,
                ["entities"] = 0.85,
                ["motion"] = 0.8,
                ["text"] = 0.6,
                ["scene"] = 0.7,
                ["colors"] = 0.3,
                ["quality"] = 0.1,
                ["identity"] = 0.1,
                ["caption"] = 0.9,
            },
            "verbose" or "markdown" => new Dictionary<string, double>
            {
                ["subjects"] = 1.0,
                ["entities"] = 0.9,
                ["motion"] = 0.85,
                ["text"] = 0.8,
                ["scene"] = 0.75,
                ["colors"] = 0.6,
                ["quality"] = 0.5,
                ["identity"] = 0.7,
                ["caption"] = 0.85,
            },
            "technical" or "tool" => new Dictionary<string, double>
            {
                ["subjects"] = 0.5,
                ["entities"] = 0.6,
                ["motion"] = 0.7,
                ["text"] = 0.8,
                ["scene"] = 0.5,
                ["colors"] = 0.9,
                ["quality"] = 1.0,
                ["identity"] = 1.0,
                ["caption"] = 0.3,
            },
            _ => new Dictionary<string, double>
            {
                ["subjects"] = 0.9,
                ["entities"] = 0.8,
                ["motion"] = 0.8,
                ["text"] = 0.7,
                ["scene"] = 0.6,
                ["colors"] = 0.4,
                ["quality"] = 0.3,
                ["identity"] = 0.3,
                ["caption"] = 0.85,
            }
        };
    }

    /// <summary>
    /// Internal record for ranked signal fusion
    /// </summary>
    private record RankedSignal(string Category, double Score, string Display);

    /// <summary>
    /// Get salient signals for a specific purpose as key-value pairs.
    /// Useful for structured output like JSON or prompt engineering.
    /// </summary>
    public Dictionary<string, object?> GetSalientSignals(string purpose = "caption")
    {
        var signals = new Dictionary<string, object?>();
        var purposeLower = purpose.ToLowerInvariant();

        // Always include these core signals
        signals["subjects.people_count"] = Objects.Faces.Count > 0 ? Objects.FaceClusters.Count : 0;
        signals["subjects.is_animated"] = Identity.IsAnimated;

        if (!string.IsNullOrWhiteSpace(Vision.Scene))
            signals["scene"] = Vision.Scene;

        // Purpose-specific signals
        switch (purposeLower)
        {
            case "alttext":
                // Minimal: subjects, action, text only
                if (Vision.Entities.Count > 0)
                    signals["subjects.entities"] = Vision.Entities.Take(3).Select(e => e.Label).ToList();
                if (Identity.IsAnimated && Motion != null)
                {
                    signals["motion.summary"] = Motion.Summary ?? $"{Motion.FrameCount} frames";
                    if (Motion.MovingObjects.Count > 0)
                        signals["motion.moving_objects"] = Motion.MovingObjects;
                }
                if (!string.IsNullOrWhiteSpace(Text.ExtractedText))
                    signals["text"] = Text.ExtractedText.Length > 50 ? Text.ExtractedText[..50] + "..." : Text.ExtractedText;
                break;

            case "caption":
            case "socialmedia":
                // Moderate: subjects, scene, key details
                if (Vision.Entities.Count > 0)
                    signals["subjects.entities"] = Vision.Entities.Take(5).Select(e => e.Label).ToList();
                if (Identity.IsAnimated && Motion != null)
                    signals["motion.summary"] = Motion.Summary;
                if (!string.IsNullOrWhiteSpace(Text.ExtractedText))
                    signals["text"] = Text.ExtractedText.Length > 100 ? Text.ExtractedText[..100] + "..." : Text.ExtractedText;
                break;

            case "verbose":
            case "markdown":
                // Everything
                signals["identity.format"] = Identity.Format;
                signals["identity.dimensions"] = $"{Identity.Width}×{Identity.Height}";
                if (Vision.Entities.Count > 0)
                    signals["subjects.entities"] = Vision.Entities.Select(e => e.Label).ToList();
                if (Identity.IsAnimated && Motion != null)
                {
                    signals["motion.frame_count"] = Motion.FrameCount;
                    signals["motion.duration"] = Motion.Duration;
                    signals["motion.summary"] = Motion.Summary;
                    signals["motion.moving_objects"] = Motion.MovingObjects;
                }
                if (!string.IsNullOrWhiteSpace(Text.ExtractedText))
                    signals["text.full"] = Text.ExtractedText;
                if (Colors.DominantColors.Count > 0)
                    signals["colors.dominant"] = Colors.DominantColors.Take(5).Select(c => c.Name ?? c.Hex).ToList();
                if (Quality.Sharpness.HasValue)
                    signals["quality.sharpness"] = Quality.Sharpness;
                break;

            case "technical":
            case "tool":
                // All metrics, structured for API consumption
                signals["identity"] = new { Identity.Format, Identity.Width, Identity.Height, Identity.AspectRatio, Identity.FileSize, Identity.IsAnimated };
                signals["colors"] = new { Colors.DominantColors, Colors.IsGrayscale, Colors.MeanSaturation };
                signals["objects"] = new { Objects.ObjectCount, Objects.ObjectTypes, FaceCount = Objects.Faces.Count };
                if (Identity.IsAnimated && Motion != null)
                    signals["motion"] = new { Motion.FrameCount, Motion.Duration, Motion.Magnitude, Motion.MovingObjects, Motion.Summary };
                if (!string.IsNullOrWhiteSpace(Text.ExtractedText))
                    signals["text"] = new { Text.ExtractedText, Text.Confidence, Text.SpellCheckScore };
                signals["quality"] = new { Quality.Sharpness, Quality.Blur, Quality.OverallQuality };
                break;
        }

        return signals;
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

    /// <summary>
    /// DocSummarizer-generated summary of long OCR text.
    /// Populated when ExtractedText exceeds threshold (e.g., 200 chars).
    /// Uses configured summarization mode (BERT, MapReduce, etc.)
    /// </summary>
    public string? ExtractedTextSummary { get; set; }

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

    // New motion detection fields from MotionWave
    public bool HasMotion { get; set; }
    public string? MotionType { get; set; }
    public string? Direction { get; set; }
    public double Magnitude { get; set; }
    public double Activity { get; set; }
    public string? Summary { get; set; }
    public List<string> MovingObjects { get; set; } = new();
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
