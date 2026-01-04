using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.DocSummarizer.Images.Services.Storage;

namespace LucidRAG.ImageCli.Services;

/// <summary>
/// Service for managing escalation of image analysis to vision LLMs.
/// Supports auto-escalation, user-triggered escalation, and feedback loops.
/// Includes content-based caching with xxhash64 for fast lookups.
/// </summary>
public class EscalationService
{
    private readonly IImageAnalyzer _imageAnalyzer;
    private readonly VisionLlmService _visionLlmService;
    private readonly UnifiedVisionService? _unifiedVisionService;
    private readonly ISignalDatabase? _signalDatabase;
    private readonly ILogger<EscalationService> _logger;
    private readonly EscalationConfig _config;

    public EscalationService(
        IImageAnalyzer imageAnalyzer,
        VisionLlmService visionLlmService,
        ILogger<EscalationService> logger,
        ISignalDatabase? signalDatabase = null,
        UnifiedVisionService? unifiedVisionService = null)
    {
        _imageAnalyzer = imageAnalyzer;
        _visionLlmService = visionLlmService;
        _unifiedVisionService = unifiedVisionService;
        _signalDatabase = signalDatabase;
        _logger = logger;

        // Default escalation configuration
        _config = new EscalationConfig
        {
            AutoEscalateEnabled = true,
            ConfidenceThreshold = 0.7,
            TextLikelinessThreshold = 0.4,
            BlurThreshold = 300,
            EnableFeedbackLoop = true,
            EnableCaching = true
        };
    }

    /// <summary>
    /// Analyze an image with optional auto-escalation to vision LLM.
    /// Uses content-based caching (xxhash64) to avoid reprocessing identical files.
    /// </summary>
    public async Task<EscalationResult> AnalyzeWithEscalationAsync(
        string imagePath,
        bool forceEscalate = false,
        bool enableOcr = true,
        bool bypassCache = false,
        string? visionModel = null,
        CancellationToken ct = default)
    {
        // Step 0: Check cache if enabled
        string? sha256Hash = null;
        if (_config.EnableCaching && _signalDatabase != null && !bypassCache)
        {
            // Compute xxhash64 for fast cache lookup
            var xxhash = await ComputeXxHash64Async(imagePath, ct);

            // Try to load from cache (using SHA256 as primary key for security)
            // We compute SHA256 only if cache lookup requires it
            sha256Hash = await ComputeSha256Async(imagePath, ct);
            var cachedProfile = await _signalDatabase.LoadProfileAsync(sha256Hash, ct);

            if (cachedProfile != null)
            {
                _logger.LogInformation("Cache hit for {ImagePath} (hash: {Hash})", imagePath, xxhash);

                // Convert DynamicImageProfile to ImageProfile for compatibility
                var profile = ConvertToImageProfile(cachedProfile);

                // Retrieve LLM caption and extracted text from signals if they were cached
                var llmCaptionFromCache = cachedProfile.GetValue<string>("content.llm_caption");
                var extractedTextFromCache = cachedProfile.GetValue<string>("content.extracted_text");

                if (!string.IsNullOrWhiteSpace(llmCaptionFromCache))
                {
                    _logger.LogDebug("Loaded cached LLM caption for {ImagePath}", imagePath);
                }

                // Try to load GIF motion data from cache
                GifMotionProfile? cachedGifMotion = null;
                var motionDirection = cachedProfile.GetValue<string>("motion.direction");
                if (!string.IsNullOrWhiteSpace(motionDirection))
                {
                    var motionSignal = cachedProfile.GetBestSignal("motion.direction");
                    var frameCount = 0;
                    var fps = 0.0;

                    if (motionSignal?.Metadata != null)
                    {
                        if (motionSignal.Metadata.TryGetValue("frame_count", out var fcObj))
                        {
                            frameCount = fcObj is System.Text.Json.JsonElement fcJson
                                ? fcJson.GetInt32()
                                : Convert.ToInt32(fcObj);
                        }
                        if (motionSignal.Metadata.TryGetValue("fps", out var fpsObj))
                        {
                            fps = fpsObj is System.Text.Json.JsonElement fpsJson
                                ? fpsJson.GetDouble()
                                : Convert.ToDouble(fpsObj);
                        }
                    }

                    // Try to load complexity data if available
                    GifComplexityProfile? cachedComplexity = null;
                    var animationType = cachedProfile.GetValue<string>("complexity.animation_type");
                    if (!string.IsNullOrWhiteSpace(animationType))
                    {
                        var complexitySignal = cachedProfile.GetBestSignal("complexity.animation_type");
                        var sceneChangeCount = 0;
                        var avgFrameDiff = 0.0;
                        var maxFrameDiff = 0.0;

                        if (complexitySignal?.Metadata != null)
                        {
                            if (complexitySignal.Metadata.TryGetValue("scene_changes", out var scObj))
                            {
                                sceneChangeCount = scObj is System.Text.Json.JsonElement scJson
                                    ? scJson.GetInt32()
                                    : Convert.ToInt32(scObj);
                            }
                            if (complexitySignal.Metadata.TryGetValue("avg_frame_diff", out var avgObj))
                            {
                                avgFrameDiff = avgObj is System.Text.Json.JsonElement avgJson
                                    ? avgJson.GetDouble()
                                    : Convert.ToDouble(avgObj);
                            }
                            if (complexitySignal.Metadata.TryGetValue("max_frame_diff", out var maxObj))
                            {
                                maxFrameDiff = maxObj is System.Text.Json.JsonElement maxJson
                                    ? maxJson.GetDouble()
                                    : Convert.ToDouble(maxObj);
                            }
                        }

                        cachedComplexity = new GifComplexityProfile
                        {
                            FrameCount = frameCount,
                            VisualStability = cachedProfile.GetValue<double>("complexity.visual_stability"),
                            ColorVariation = cachedProfile.GetValue<double>("complexity.color_variation"),
                            EntropyVariation = cachedProfile.GetValue<double>("complexity.entropy_variation"),
                            SceneChangeCount = sceneChangeCount,
                            AnimationType = animationType,
                            OverallComplexity = cachedProfile.GetValue<double>("complexity.overall"),
                            AverageFrameDifference = avgFrameDiff,
                            MaxFrameDifference = maxFrameDiff
                        };
                    }

                    cachedGifMotion = new GifMotionProfile
                    {
                        MotionDirection = motionDirection,
                        MotionMagnitude = cachedProfile.GetValue<double>("motion.magnitude"),
                        MotionPercentage = cachedProfile.GetValue<double>("motion.percentage"),
                        Confidence = motionSignal?.Confidence ?? 0.5,
                        FrameCount = frameCount,
                        FrameDelayMs = fps > 0 ? (int)(1000.0 / fps) : 100,
                        TotalDurationMs = frameCount * (fps > 0 ? (int)(1000.0 / fps) : 100),
                        Loops = true,
                        Complexity = cachedComplexity
                    };
                }

                // Note: Evidence claims are not cached currently, only caption
                // Future: Store claims as signals and reconstruct here
                return new EscalationResult(
                    FilePath: imagePath,
                    Profile: profile,
                    LlmCaption: llmCaptionFromCache,
                    ExtractedText: extractedTextFromCache,
                    WasEscalated: !string.IsNullOrWhiteSpace(llmCaptionFromCache), // If we have caption, it was escalated
                    EscalationReason: null,
                    FromCache: true,
                    GifMotion: cachedGifMotion,
                    EvidenceClaims: null); // Not cached yet
            }

            _logger.LogDebug("Cache miss for {ImagePath} (hash: {Hash})", imagePath, xxhash);
        }

        // Step 1: Deterministic analysis
        var analyzedProfile = await _imageAnalyzer.AnalyzeAsync(imagePath, ct);

        // Step 1.5: Animated image motion analysis if applicable (GIF, WebP)
        GifMotionProfile? gifMotionProfile = null;
        if (analyzedProfile.Format?.Equals("GIF", StringComparison.OrdinalIgnoreCase) == true ||
            analyzedProfile.Format?.Equals("WEBP", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                using var gifAnalyzer = new GifMotionAnalyzer(_logger as ILogger<GifMotionAnalyzer>);
                gifMotionProfile = await gifAnalyzer.AnalyzeAsync(imagePath, ct);
                _logger.LogInformation("{Format} motion analysis: {Direction} ({Magnitude:F2} px/frame, {Confidence:P0} confidence)",
                    analyzedProfile.Format,
                    gifMotionProfile.MotionDirection,
                    gifMotionProfile.MotionMagnitude,
                    gifMotionProfile.Confidence);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to analyze {Format} motion for {ImagePath}",
                    analyzedProfile.Format, imagePath);
            }
        }

        // Step 2: Decide if escalation is needed
        var shouldEscalate = forceEscalate || ShouldAutoEscalate(analyzedProfile);

        string? llmCaption = null;
        string? extractedText = null;
        List<VisionClients.EvidenceClaim>? evidenceClaims = null;
        var escalationReason = forceEscalate ? "User requested" : null;

        if (shouldEscalate)
        {
            if (!forceEscalate)
            {
                escalationReason = DetermineEscalationReason(analyzedProfile);
                _logger.LogInformation("Auto-escalating {ImagePath}: {Reason}", imagePath, escalationReason);
            }

            // Step 3: Escalate to vision LLM
            // Build custom prompt with color information to improve accuracy
            var customPrompt = BuildVisionPrompt(analyzedProfile);

            // Check if model spec includes provider (e.g., "anthropic:claude-3-5-sonnet")
            if (!string.IsNullOrEmpty(visionModel) && visionModel.Contains(':') && _unifiedVisionService != null)
            {
                // Use UnifiedVisionService for provider:model format
                var (provider, model) = _unifiedVisionService.ParseModelSpec(visionModel);
                var visionResult = await _unifiedVisionService.AnalyzeImageAsync(imagePath, customPrompt, provider, model, temperature: null, ct);

                if (visionResult.Success)
                {
                    llmCaption = visionResult.Caption;
                    evidenceClaims = visionResult.Claims;
                    _logger.LogInformation("Vision analysis completed with {Provider}:{Model}", provider ?? "default", model);
                }
                else
                {
                    _logger.LogWarning("Vision LLM failed for {ImagePath}: {Error}", imagePath, visionResult.Error);
                }
            }
            else
            {
                // Use V1 VisionLlmService for Ollama-only models (no evidence claims support yet)
                var llmResult = await _visionLlmService.AnalyzeImageAsync(imagePath, customPrompt, visionModel, ct);

                if (llmResult.Success)
                {
                    llmCaption = llmResult.Caption;
                    // V1 service doesn't return evidence claims yet
                    evidenceClaims = null;
                }
                else
                {
                    _logger.LogWarning("Vision LLM failed for {ImagePath}: {Error}", imagePath, llmResult.Error);
                }
            }
        }

        // Step 4: OCR if text detected and enabled
        if (enableOcr && analyzedProfile.TextLikeliness >= _config.TextLikelinessThreshold)
        {
            _logger.LogInformation("Image {ImagePath} has high text likeliness ({Score:F3}), performing OCR",
                imagePath, analyzedProfile.TextLikeliness);

            try
            {
                // Check if this is an animated image (GIF/WebP)
                var isAnimated = analyzedProfile.Format?.Equals("GIF", StringComparison.OrdinalIgnoreCase) == true ||
                                 analyzedProfile.Format?.Equals("WEBP", StringComparison.OrdinalIgnoreCase) == true;

                if (isAnimated)
                {
                    // Use multi-frame text extraction for animated images
                    _logger.LogInformation("Using multi-frame text extraction for {Format}", analyzedProfile.Format);

                    var ocrEngine = new Mostlylucid.DocSummarizer.Images.Services.Ocr.TesseractOcrEngine();
                    var imageConfig = new Mostlylucid.DocSummarizer.Images.Config.ImageConfig(); // Use default config
                    var gifExtractor = new Mostlylucid.DocSummarizer.Images.Services.Ocr.GifTextExtractor(
                        ocrEngine,
                        imageConfig,
                        advancedOcrService: null, // No advanced pipeline in CLI
                        logger: _logger as ILogger<Mostlylucid.DocSummarizer.Images.Services.Ocr.GifTextExtractor>);

                    var result = await gifExtractor.ExtractTextAsync(imagePath, ct);

                    extractedText = result.CombinedText;

                    _logger.LogInformation("Extracted text from {Frames} frames (out of {Total}): {Preview}",
                        result.FramesWithText,
                        result.TotalFrames,
                        extractedText.Length > 100 ? extractedText.Substring(0, 100) + "..." : extractedText);
                }
                else
                {
                    // Use standard single-frame OCR for static images
                    var ocrEngine = new Mostlylucid.DocSummarizer.Images.Services.Ocr.TesseractOcrEngine();
                    var regions = ocrEngine.ExtractTextWithCoordinates(imagePath);

                    extractedText = string.Join(" ", regions.Select(r => r.Text));

                    _logger.LogInformation("Extracted {Count} text regions from static image: {Preview}",
                        regions.Count,
                        extractedText.Length > 100 ? extractedText.Substring(0, 100) + "..." : extractedText);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OCR failed for {ImagePath}", imagePath);
            }
        }

        // Step 5: Store in cache if enabled
        if (_config.EnableCaching && _signalDatabase != null)
        {
            // Use SHA256 from earlier or compute it now
            sha256Hash ??= await ComputeSha256Async(imagePath, ct);

            // Convert ImageProfile to DynamicImageProfile for storage
            var dynamicProfile = ConvertToDynamicProfile(analyzedProfile);

            // Add LLM caption as signal if available
            if (!string.IsNullOrWhiteSpace(llmCaption))
            {
                dynamicProfile.AddSignal(new Signal
                {
                    Key = "content.llm_caption",
                    Value = llmCaption,
                    Confidence = 0.85, // Vision LLMs are generally reliable for captions
                    Source = "VisionLLM",
                    Tags = new List<string> { "caption", "description", "llm" },
                    Metadata = new Dictionary<string, object>
                    {
                        { "escalated", shouldEscalate },
                        { "escalation_reason", escalationReason ?? "none" }
                    }
                });
                _logger.LogDebug("Added LLM caption signal for {ImagePath}", imagePath);
            }

            // Add extracted text as signal if available
            if (!string.IsNullOrWhiteSpace(extractedText))
            {
                dynamicProfile.AddSignal(new Signal
                {
                    Key = "content.extracted_text",
                    Value = extractedText,
                    Confidence = 0.9, // OCR is highly accurate for clear text
                    Source = "OCR",
                    Tags = new List<string> { "text", "ocr", "content" },
                    Metadata = new Dictionary<string, object>
                    {
                        { "text_likeliness", analyzedProfile.TextLikeliness }
                    }
                });
                _logger.LogDebug("Added extracted text signal for {ImagePath}", imagePath);
            }

            // Add GIF motion signals if available
            if (gifMotionProfile != null)
            {
                dynamicProfile.AddSignal(new Signal
                {
                    Key = "motion.direction",
                    Value = gifMotionProfile.MotionDirection,
                    Confidence = gifMotionProfile.Confidence,
                    Source = "GifMotionAnalyzer",
                    Tags = new List<string> { "motion", "gif", "direction" },
                    Metadata = new Dictionary<string, object>
                    {
                        { "frame_count", gifMotionProfile.FrameCount },
                        { "fps", gifMotionProfile.Fps }
                    }
                });

                dynamicProfile.AddSignal(new Signal
                {
                    Key = "motion.magnitude",
                    Value = gifMotionProfile.MotionMagnitude,
                    Confidence = gifMotionProfile.Confidence,
                    Source = "GifMotionAnalyzer",
                    Tags = new List<string> { "motion", "gif", "magnitude" }
                });

                dynamicProfile.AddSignal(new Signal
                {
                    Key = "motion.percentage",
                    Value = gifMotionProfile.MotionPercentage,
                    Confidence = 1.0,
                    Source = "GifMotionAnalyzer",
                    Tags = new List<string> { "motion", "gif", "statistics" }
                });

                _logger.LogDebug("Added GIF motion signals for {ImagePath}: {Direction} ({Magnitude:F2} px/frame)",
                    imagePath, gifMotionProfile.MotionDirection, gifMotionProfile.MotionMagnitude);

                // Add complexity signals if available
                if (gifMotionProfile.Complexity != null)
                {
                    var complexity = gifMotionProfile.Complexity;

                    dynamicProfile.AddSignal(new Signal
                    {
                        Key = "complexity.animation_type",
                        Value = complexity.AnimationType,
                        Confidence = 1.0,
                        Source = "GifComplexityAnalyzer",
                        Tags = new List<string> { "complexity", "gif", "animation" },
                        Metadata = new Dictionary<string, object>
                        {
                            { "scene_changes", complexity.SceneChangeCount },
                            { "avg_frame_diff", complexity.AverageFrameDifference },
                            { "max_frame_diff", complexity.MaxFrameDifference }
                        }
                    });

                    dynamicProfile.AddSignal(new Signal
                    {
                        Key = "complexity.visual_stability",
                        Value = complexity.VisualStability,
                        Confidence = 1.0,
                        Source = "GifComplexityAnalyzer",
                        Tags = new List<string> { "complexity", "gif", "stability" }
                    });

                    dynamicProfile.AddSignal(new Signal
                    {
                        Key = "complexity.color_variation",
                        Value = complexity.ColorVariation,
                        Confidence = 1.0,
                        Source = "GifComplexityAnalyzer",
                        Tags = new List<string> { "complexity", "gif", "color" }
                    });

                    dynamicProfile.AddSignal(new Signal
                    {
                        Key = "complexity.entropy_variation",
                        Value = complexity.EntropyVariation,
                        Confidence = 1.0,
                        Source = "GifComplexityAnalyzer",
                        Tags = new List<string> { "complexity", "gif", "entropy" }
                    });

                    dynamicProfile.AddSignal(new Signal
                    {
                        Key = "complexity.overall",
                        Value = complexity.OverallComplexity,
                        Confidence = 1.0,
                        Source = "GifComplexityAnalyzer",
                        Tags = new List<string> { "complexity", "gif", "score" }
                    });

                    _logger.LogDebug("Added GIF complexity signals for {ImagePath}: {Type} (stability={Stability:F2}, overall={Overall:F2})",
                        imagePath, complexity.AnimationType, complexity.VisualStability, complexity.OverallComplexity);
                }
            }

            // Store with filename metadata (allows tracking renames)
            using var imageStream = File.OpenRead(imagePath);
            using var image = await SixLabors.ImageSharp.Image.LoadAsync(imagePath, ct);

            await _signalDatabase.StoreProfileAsync(
                dynamicProfile,
                sha256Hash,
                imagePath, // Current filename (metadata only)
                image.Width,
                image.Height,
                image.Metadata.DecodedImageFormat?.Name,
                ct);

            _logger.LogDebug("Stored analysis for {ImagePath} in cache (hash: {Hash})", imagePath, sha256Hash[..16]);
        }

        return new EscalationResult(
            FilePath: imagePath,
            Profile: analyzedProfile,
            LlmCaption: llmCaption,
            ExtractedText: extractedText,
            WasEscalated: shouldEscalate,
            EscalationReason: escalationReason,
            FromCache: false,
            GifMotion: gifMotionProfile,
            EvidenceClaims: evidenceClaims);
    }

    /// <summary>
    /// Batch analyze images with parallel processing and escalation.
    /// </summary>
    public async Task<List<EscalationResult>> AnalyzeBatchAsync(
        IEnumerable<string> imagePaths,
        bool enableAutoEscalation = true,
        bool enableOcr = true,
        int maxParallel = 4,
        IProgress<BatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        var results = new System.Collections.Concurrent.ConcurrentBag<EscalationResult>();
        var imagePathsList = imagePaths.ToList();
        var totalCount = imagePathsList.Count;
        var processedCount = 0;

        var semaphore = new SemaphoreSlim(maxParallel);
        var tasks = imagePathsList.Select(async (imagePath, index) =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var result = await AnalyzeWithEscalationAsync(
                    imagePath,
                    forceEscalate: false,
                    enableOcr: enableOcr,
                    ct: ct);

                results.Add(result);

                var processed = Interlocked.Increment(ref processedCount);
                progress?.Report(new BatchProgress(
                    WorkerId: index % maxParallel,
                    FilePath: imagePath,
                    Success: true,
                    Processed: processed,
                    Total: totalCount));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze {ImagePath}", imagePath);
                progress?.Report(new BatchProgress(
                    WorkerId: index % maxParallel,
                    FilePath: imagePath,
                    Success: false,
                    Error: ex.Message,
                    Processed: Interlocked.Increment(ref processedCount),
                    Total: totalCount));
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        return results.ToList();
    }

    /// <summary>
    /// Determine if an image should be auto-escalated to vision LLM.
    /// </summary>
    private bool ShouldAutoEscalate(ImageProfile profile)
    {
        if (!_config.AutoEscalateEnabled)
            return false;

        // Escalate if type detection confidence is low
        if (profile.TypeConfidence < _config.ConfidenceThreshold)
        {
            return true;
        }

        // Escalate if image is blurry (might need better description)
        if (profile.LaplacianVariance < _config.BlurThreshold)
        {
            return true;
        }

        // Escalate if image has high text content (for better OCR/description)
        if (profile.TextLikeliness >= _config.TextLikelinessThreshold)
        {
            return true;
        }

        // Escalate for complex diagrams or charts
        if (profile.DetectedType is ImageType.Diagram or ImageType.Chart)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Determine the reason for escalation.
    /// </summary>
    private string DetermineEscalationReason(ImageProfile profile)
    {
        var reasons = new List<string>();

        if (profile.TypeConfidence < _config.ConfidenceThreshold)
        {
            reasons.Add($"Low type confidence ({profile.TypeConfidence:P0})");
        }

        if (profile.LaplacianVariance < _config.BlurThreshold)
        {
            reasons.Add($"Blurry image (sharpness: {profile.LaplacianVariance:F0})");
        }

        if (profile.TextLikeliness >= _config.TextLikelinessThreshold)
        {
            reasons.Add($"High text content (score: {profile.TextLikeliness:F2})");
        }

        if (profile.DetectedType is ImageType.Diagram or ImageType.Chart)
        {
            reasons.Add($"Complex {profile.DetectedType.ToString().ToLower()}");
        }

        return string.Join(", ", reasons);
    }

    /// <summary>
    /// Build a custom vision LLM prompt that includes deterministic analysis signals,
    /// anti-hallucination constraints, and evidence citation requirements.
    /// </summary>
    private static string BuildVisionPrompt(ImageProfile profile)
    {
        var prompt = new StringBuilder();

        // Main instruction with hallucination prevention
        prompt.AppendLine("Analyze this image and provide a JSON response with clean caption and evidence-backed claims.");
        prompt.AppendLine();
        prompt.AppendLine("CRITICAL CONSTRAINTS:");
        prompt.AppendLine("- Only describe what is visually present in the image");
        prompt.AppendLine("- Only reference metadata values provided below");
        prompt.AppendLine("- Do NOT infer, assume, or guess information not visible or provided");
        prompt.AppendLine("- Do NOT identify specific people, brands, or copyrighted content unless the text is clearly visible");
        prompt.AppendLine("- If uncertain about something, say 'appears to be' or 'seems like' rather than stating as fact");
        prompt.AppendLine();
        prompt.AppendLine("REQUIRED JSON OUTPUT FORMAT:");
        prompt.AppendLine("You MUST respond with ONLY a valid JSON object (no markdown code blocks, no explanation):");
        prompt.AppendLine("{");
        prompt.AppendLine("  \"caption\": \"<clean English description>\",");
        prompt.AppendLine("  \"claims\": [<array of claim objects>],");
        prompt.AppendLine("  \"metadata\": {");
        prompt.AppendLine("    \"tone\": \"<professional|casual|humorous|formal|technical>\",");
        prompt.AppendLine("    \"sentiment\": <-1.0 to 1.0>,");
        prompt.AppendLine("    \"complexity\": <0.0 to 1.0>,");
        prompt.AppendLine("    \"aesthetic_score\": <0.0 to 1.0>,");
        prompt.AppendLine("    \"primary_subject\": \"<main focus>\",");
        prompt.AppendLine("    \"purpose\": \"<educational|entertainment|commercial|documentation>\",");
        prompt.AppendLine("    \"target_audience\": \"<general|technical|children|professionals>\"");
        prompt.AppendLine("  }");
        prompt.AppendLine("}");
        prompt.AppendLine();
        prompt.AppendLine("EVIDENCE SOURCES:");
        prompt.AppendLine("- V = direct visual observation from the image");
        prompt.AppendLine("- M = motion/animation analysis (for GIFs)");
        prompt.AppendLine("- O = OCR-extracted text");
        prompt.AppendLine("- S = deterministic signal from metadata (color, sharpness, type, etc.)");
        prompt.AppendLine("- G = multiple signals in agreement (consensus)");
        prompt.AppendLine("- L = synthesis/inference (composition only - NEVER use alone)");
        prompt.AppendLine();
        prompt.AppendLine("RULES:");
        prompt.AppendLine("- Every claim MUST have at least one non-L source (V/M/O/S/G)");
        prompt.AppendLine("- If only synthesis, hedge with 'appears to' or omit the claim");
        prompt.AppendLine("- Caption should be natural, readable English");
        prompt.AppendLine();
        prompt.AppendLine("Example JSON:");
        prompt.AppendLine("{");
        prompt.AppendLine("  \"caption\": \"A sharp photograph shows a person wearing dark clothing in an indoor setting with warm lighting.\",");
        prompt.AppendLine("  \"claims\": [");
        prompt.AppendLine("    { \"text\": \"sharp photograph\", \"sources\": [\"V\", \"S\"], \"evidence\": [\"sharpness=1591\"] },");
        prompt.AppendLine("    { \"text\": \"person wearing dark clothing\", \"sources\": [\"V\"] },");
        prompt.AppendLine("    { \"text\": \"indoor setting with warm lighting\", \"sources\": [\"V\", \"S\"], \"evidence\": [\"color.dominant=warm_tones\"] }");
        prompt.AppendLine("  ],");
        prompt.AppendLine("  \"metadata\": {");
        prompt.AppendLine("    \"tone\": \"professional\",");
        prompt.AppendLine("    \"sentiment\": 0.1,");
        prompt.AppendLine("    \"complexity\": 0.4,");
        prompt.AppendLine("    \"aesthetic_score\": 0.7,");
        prompt.AppendLine("    \"primary_subject\": \"portrait\",");
        prompt.AppendLine("    \"purpose\": \"documentation\",");
        prompt.AppendLine("    \"target_audience\": \"general\"");
        prompt.AppendLine("  }");
        prompt.AppendLine("}");
        prompt.AppendLine();
        prompt.AppendLine("DESCRIBE:");
        prompt.AppendLine("- Objects, people, animals, and their positions");
        prompt.AppendLine("- Setting, environment, and background");
        prompt.AppendLine("- Any text that is visible (transcribe it exactly)");
        prompt.AppendLine("- Actions, expressions, and mood based on visible cues");
        prompt.AppendLine("- Visual qualities: lighting, composition, style");
        prompt.AppendLine();

        // Add metadata context
        prompt.AppendLine("METADATA SIGNALS (computed from image analysis):");
        prompt.AppendLine();

        // Color information
        if (profile.DominantColors?.Any() == true)
        {
            prompt.Append("• Dominant Colors: ");
            var colorDescriptions = profile.DominantColors
                .Take(3)
                .Select(c => $"{c.Name} ({c.Percentage:F0}%)");
            prompt.AppendLine(string.Join(", ", colorDescriptions));

            if (profile.IsMostlyGrayscale)
            {
                prompt.AppendLine("  → Image is mostly grayscale (low saturation)");
            }
            else if (profile.MeanSaturation > 0.5)
            {
                prompt.AppendLine("  → Image has vibrant, saturated colors");
            }
        }

        // Image quality signals
        prompt.AppendLine($"• Sharpness: {profile.LaplacianVariance:F0} (Laplacian variance)");
        if (profile.LaplacianVariance < 100)
        {
            prompt.AppendLine("  → Image is blurry or soft-focused");
        }
        else if (profile.LaplacianVariance > 500)
        {
            prompt.AppendLine("  → Image is sharp with clear details");
        }

        // Image type detection
        prompt.AppendLine($"• Detected Type: {profile.DetectedType} (confidence: {profile.TypeConfidence:P0})");
        prompt.AppendLine($"  → Photo={profile.TypeConfidence > 0.3}, Screenshot={profile.TypeConfidence < 0.3 && profile.EdgeDensity > 0.15}, Diagram/Chart={profile.DetectedType is ImageType.Diagram or ImageType.Chart}");

        // Text likelihood
        prompt.AppendLine($"• Text Likelihood: {profile.TextLikeliness:F2}");
        if (profile.TextLikeliness > 0.4)
        {
            prompt.AppendLine("  → High probability of containing readable text - look carefully for text elements");
        }

        // Luminance/exposure
        prompt.AppendLine($"• Luminance: Mean={profile.MeanLuminance:F1}, StdDev={profile.LuminanceStdDev:F1}");
        if (profile.MeanLuminance < 50)
        {
            prompt.AppendLine("  → Image is dark or underexposed");
        }
        else if (profile.MeanLuminance > 200)
        {
            prompt.AppendLine("  → Image is bright or overexposed");
        }

        // Edge density (complexity)
        prompt.AppendLine($"• Edge Density: {profile.EdgeDensity:F3}");
        if (profile.EdgeDensity > 0.2)
        {
            prompt.AppendLine("  → Image has high visual complexity with many edges/details");
        }

        prompt.AppendLine();
        prompt.AppendLine("Use these metadata signals to guide your description and validate what you observe.");
        prompt.AppendLine("Your description should be grounded in observable facts only.");

        return prompt.ToString();
    }

    /// <summary>
    /// Store feedback for learning/improvement.
    /// This would integrate with SQLite storage for the feedback loop.
    /// </summary>
    public async Task StoreFeedbackAsync(
        string imagePath,
        ImageProfile profile,
        string? llmCaption,
        bool wasCorrect,
        string? userCorrection = null,
        CancellationToken ct = default)
    {
        // TODO: Implement SQLite storage for feedback loop
        // This data can be used to:
        // 1. Improve auto-escalation thresholds
        // 2. Train/fine-tune models
        // 3. Build a knowledge base

        _logger.LogInformation(
            "Feedback recorded for {ImagePath}: Correct={Correct}, Correction={Correction}",
            imagePath, wasCorrect, userCorrection);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Compute xxhash64 for fast content-based cache lookups.
    /// Fast (10x+ faster than SHA256) but not cryptographically secure.
    /// </summary>
    private static async Task<string> ComputeXxHash64Async(string filePath, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        var hash = XxHash64.Hash(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Compute SHA256 for secure content identification and deduplication.
    /// Slower than xxhash64 but collision-resistant.
    /// </summary>
    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Convert DynamicImageProfile to ImageProfile for compatibility.
    /// </summary>
    private static ImageProfile ConvertToImageProfile(DynamicImageProfile dynamicProfile)
    {
        // Extract dominant colors (stored as JSON or structured data)
        var dominantColorsList = new List<DominantColor>();
        var colorNames = dynamicProfile.GetValue<List<string>>("color.dominant_color_names") ?? new List<string>();
        var colorHexes = dynamicProfile.GetValue<List<string>>("color.dominant_color_hexes") ?? new List<string>();
        var colorPercentages = dynamicProfile.GetValue<List<double>>("color.dominant_color_percentages") ?? new List<double>();

        for (int i = 0; i < Math.Min(colorHexes.Count, Math.Min(colorNames.Count, colorPercentages.Count)); i++)
        {
            dominantColorsList.Add(new DominantColor(colorHexes[i], colorPercentages[i], colorNames[i]));
        }

        return new ImageProfile
        {
            Sha256 = dynamicProfile.GetValue<string>("identity.sha256") ?? "",
            Format = dynamicProfile.GetValue<string>("identity.format") ?? "Unknown",
            Width = dynamicProfile.GetValue<int>("identity.width"),
            Height = dynamicProfile.GetValue<int>("identity.height"),
            AspectRatio = dynamicProfile.GetValue<double>("identity.aspect_ratio"),
            DetectedType = dynamicProfile.GetValue<ImageType>("content.type"),
            TypeConfidence = dynamicProfile.GetValue<double>("content.type_confidence"),
            EdgeDensity = dynamicProfile.GetValue<double>("quality.edge_density"),
            LuminanceEntropy = dynamicProfile.GetValue<double>("quality.luminance_entropy"),
            MeanLuminance = dynamicProfile.GetValue<double>("color.mean_luminance"),
            LuminanceStdDev = dynamicProfile.GetValue<double>("color.luminance_stddev"),
            ClippedBlacksPercent = dynamicProfile.GetValue<double>("color.clipped_blacks_percent"),
            ClippedWhitesPercent = dynamicProfile.GetValue<double>("color.clipped_whites_percent"),
            LaplacianVariance = dynamicProfile.GetValue<double>("quality.sharpness"),
            TextLikeliness = dynamicProfile.GetValue<double>("content.text_likeliness"),
            DominantColors = dominantColorsList,
            MeanSaturation = dynamicProfile.GetValue<double>("color.mean_saturation"),
            IsMostlyGrayscale = dynamicProfile.GetValue<bool>("color.is_mostly_grayscale")
        };
    }

    /// <summary>
    /// Convert ImageProfile to DynamicImageProfile for storage.
    /// </summary>
    private static DynamicImageProfile ConvertToDynamicProfile(ImageProfile profile)
    {
        var dynamicProfile = new DynamicImageProfile();

        // Identity signals
        dynamicProfile.AddSignal(new Signal { Key = "identity.sha256", Value = profile.Sha256, Confidence = 1.0, Source = "ImageAnalyzer" });
        dynamicProfile.AddSignal(new Signal { Key = "identity.format", Value = profile.Format, Confidence = 1.0, Source = "ImageAnalyzer" });
        dynamicProfile.AddSignal(new Signal { Key = "identity.width", Value = profile.Width, Confidence = 1.0, Source = "ImageAnalyzer" });
        dynamicProfile.AddSignal(new Signal { Key = "identity.height", Value = profile.Height, Confidence = 1.0, Source = "ImageAnalyzer" });
        dynamicProfile.AddSignal(new Signal { Key = "identity.aspect_ratio", Value = profile.AspectRatio, Confidence = 1.0, Source = "ImageAnalyzer" });

        // Content signals
        dynamicProfile.AddSignal(new Signal { Key = "content.type", Value = profile.DetectedType, Confidence = profile.TypeConfidence, Source = "ImageAnalyzer" });
        dynamicProfile.AddSignal(new Signal { Key = "content.type_confidence", Value = profile.TypeConfidence, Confidence = 1.0, Source = "ImageAnalyzer" });
        dynamicProfile.AddSignal(new Signal { Key = "content.text_likeliness", Value = profile.TextLikeliness, Confidence = 0.7, Source = "ImageAnalyzer" });

        // Quality signals
        dynamicProfile.AddSignal(new Signal { Key = "quality.edge_density", Value = profile.EdgeDensity, Confidence = 0.9, Source = "ImageAnalyzer" });
        dynamicProfile.AddSignal(new Signal { Key = "quality.luminance_entropy", Value = profile.LuminanceEntropy, Confidence = 0.9, Source = "ImageAnalyzer" });
        dynamicProfile.AddSignal(new Signal { Key = "quality.sharpness", Value = profile.LaplacianVariance, Confidence = 0.8, Source = "ImageAnalyzer" });

        // Color signals
        dynamicProfile.AddSignal(new Signal { Key = "color.mean_luminance", Value = profile.MeanLuminance, Confidence = 1.0, Source = "ImageAnalyzer" });
        dynamicProfile.AddSignal(new Signal { Key = "color.luminance_stddev", Value = profile.LuminanceStdDev, Confidence = 1.0, Source = "ImageAnalyzer" });
        dynamicProfile.AddSignal(new Signal { Key = "color.clipped_blacks_percent", Value = profile.ClippedBlacksPercent, Confidence = 1.0, Source = "ImageAnalyzer" });
        dynamicProfile.AddSignal(new Signal { Key = "color.clipped_whites_percent", Value = profile.ClippedWhitesPercent, Confidence = 1.0, Source = "ImageAnalyzer" });
        dynamicProfile.AddSignal(new Signal { Key = "color.mean_saturation", Value = profile.MeanSaturation, Confidence = 1.0, Source = "ImageAnalyzer" });
        dynamicProfile.AddSignal(new Signal { Key = "color.is_mostly_grayscale", Value = profile.IsMostlyGrayscale, Confidence = 1.0, Source = "ImageAnalyzer" });

        // Store dominant colors as separate signals for easier querying
        var colorNames = profile.DominantColors.Select(c => c.Name).ToList();
        var colorHexes = profile.DominantColors.Select(c => c.Hex).ToList();
        var colorPercentages = profile.DominantColors.Select(c => c.Percentage).ToList();

        dynamicProfile.AddSignal(new Signal { Key = "color.dominant_color_names", Value = colorNames, Confidence = 0.9, Source = "ImageAnalyzer" });
        dynamicProfile.AddSignal(new Signal { Key = "color.dominant_color_hexes", Value = colorHexes, Confidence = 0.9, Source = "ImageAnalyzer" });
        dynamicProfile.AddSignal(new Signal { Key = "color.dominant_color_percentages", Value = colorPercentages, Confidence = 0.9, Source = "ImageAnalyzer" });

        return dynamicProfile;
    }
}

/// <summary>
/// Configuration for escalation behavior.
/// </summary>
public class EscalationConfig
{
    /// <summary>
    /// Enable automatic escalation based on confidence/quality thresholds.
    /// </summary>
    public bool AutoEscalateEnabled { get; set; } = true;

    /// <summary>
    /// Type detection confidence threshold (0.0-1.0). Below this triggers escalation.
    /// </summary>
    public double ConfidenceThreshold { get; set; } = 0.7;

    /// <summary>
    /// Text likeliness threshold. Above this triggers escalation for better OCR.
    /// </summary>
    public double TextLikelinessThreshold { get; set; } = 0.4;

    /// <summary>
    /// Blur threshold (Laplacian variance). Below this triggers escalation.
    /// </summary>
    public double BlurThreshold { get; set; } = 300;

    /// <summary>
    /// Enable feedback loop to learn from escalations.
    /// </summary>
    public bool EnableFeedbackLoop { get; set; } = true;

    /// <summary>
    /// Enable content-based caching with xxhash64/SHA256.
    /// Caches analysis results to avoid reprocessing identical files (even if renamed).
    /// </summary>
    public bool EnableCaching { get; set; } = true;
}

/// <summary>
/// Result of an analysis with potential escalation.
/// </summary>
public record EscalationResult(
    string FilePath,
    ImageProfile Profile,
    string? LlmCaption,
    string? ExtractedText,
    bool WasEscalated,
    string? EscalationReason,
    bool FromCache = false,
    GifMotionProfile? GifMotion = null,
    List<VisionClients.EvidenceClaim>? EvidenceClaims = null);

/// <summary>
/// Progress report for batch processing.
/// </summary>
public record BatchProgress(
    int WorkerId,
    string FilePath,
    bool Success,
    string? Error = null,
    int Processed = 0,
    int Total = 0);
