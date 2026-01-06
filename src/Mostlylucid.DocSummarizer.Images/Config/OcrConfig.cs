namespace Mostlylucid.DocSummarizer.Images.Config;

/// <summary>
/// Configuration for advanced OCR pipeline with quality modes and phase toggles
/// </summary>
public class OcrConfig
{
    /// <summary>
    /// Enable the advanced multi-phase OCR pipeline (vs simple Tesseract-only)
    /// </summary>
    public bool UseAdvancedPipeline { get; set; } = false;

    /// <summary>
    /// Named pipeline to use from pipelines.json configuration
    /// If specified, takes precedence over QualityMode
    /// Examples: "simpleocr", "advancedocr", "quality"
    /// Leave null to use QualityMode presets instead
    /// </summary>
    public string? PipelineName { get; set; }

    /// <summary>
    /// Quality mode determines which phases are active and their parameters
    /// Fast (default): 2-3s per GIF, +20-30% accuracy
    /// Balanced: 5-7s per GIF, +30-40% accuracy
    /// Quality: 10-15s per GIF, +35-45% accuracy
    /// Ultra: 20-30s per GIF, +40-60% accuracy
    /// Note: Ignored if PipelineName is specified
    /// </summary>
    public OcrQualityMode QualityMode { get; set; } = OcrQualityMode.Fast;

    /// <summary>
    /// Confidence threshold (0-1) for early exit - skip expensive phases if OCR confidence exceeds this
    /// Default: 0.95 (95% confidence = skip remaining phases)
    /// Set to 1.0 to disable early exit
    /// </summary>
    public double ConfidenceThresholdForEarlyExit { get; set; } = 0.95;

    // ============ Phase Toggles (for testing/benchmarking) ============

    /// <summary>
    /// Enable frame stabilization using ORB feature detection and homography
    /// Compensates for camera shake and jitter across frames
    /// </summary>
    public bool EnableStabilization { get; set; } = true;

    /// <summary>
    /// Enable background subtraction to isolate text from static backgrounds
    /// Uses MOG2 Gaussian Mixture Model
    /// </summary>
    public bool EnableBackgroundSubtraction { get; set; } = true;

    /// <summary>
    /// Enable edge consensus masking using Sobel + Canny + LoG voting
    /// Creates high-quality binary mask of text boundaries
    /// </summary>
    public bool EnableEdgeConsensus { get; set; } = true;

    /// <summary>
    /// Enable temporal median filtering to create noise-free composite from multiple frames
    /// Highly effective for GIFs - one of the biggest wins
    /// </summary>
    public bool EnableTemporalMedian { get; set; } = true;

    /// <summary>
    /// Enable multi-frame super-resolution (slow, significant quality boost)
    /// Classical: Bicubic + sharpening (Fast/Balanced modes)
    /// ONNX: Real-ESRGAN deep learning upscaling (Quality/Ultra modes)
    /// </summary>
    public bool EnableSuperResolution { get; set; } = false;

    /// <summary>
    /// Enable EAST/CRAFT deep learning text detection (requires ONNX models)
    /// Falls back to Tesseract PSM if models unavailable
    /// </summary>
    public bool EnableTextDetection { get; set; } = true;

    /// <summary>
    /// Enable temporal voting - OCR multiple frames and vote on character consensus
    /// Dramatically improves accuracy for animated images
    /// </summary>
    public bool EnableTemporalVoting { get; set; } = true;

    /// <summary>
    /// Enable post-correction using dictionaries and OCR error patterns
    /// Fixes common mistakes like O→0, l→1, rn→m
    /// </summary>
    public bool EnablePostCorrection { get; set; } = true;

    // ============ Performance Tuning ============

    /// <summary>
    /// Maximum frames to use for super-resolution (most expensive phase)
    /// Higher = better quality but much slower
    /// </summary>
    public int MaxFramesForSuperResolution { get; set; } = 5;

    /// <summary>
    /// Maximum frames to OCR for temporal voting
    /// Higher = more robust consensus but slower
    /// </summary>
    public int MaxFramesForVoting { get; set; } = 10;

    /// <summary>
    /// Minimum confidence (0-1) for frame stabilization homography
    /// Lower = accept more uncertain alignments (risky)
    /// Higher = only align when very confident (safer)
    /// </summary>
    public double StabilizationConfidenceThreshold { get; set; } = 0.7;

    /// <summary>
    /// SSIM threshold (0-1) for frame deduplication
    /// Higher = more aggressive deduplication (faster, might miss subtle differences)
    /// Lower = keep more unique frames (slower, more data)
    /// NOTE: For subtitle GIFs, text-content deduplication is used instead (compares OCR text)
    /// </summary>
    public double SsimDeduplicationThreshold { get; set; } = 0.92;

    /// <summary>
    /// Text similarity threshold (0-1) for text-content deduplication
    /// Higher = more aggressive (drop frames with similar text)
    /// Lower = keep more frames with text variations
    /// Uses Levenshtein distance - 0.85 means 85% similar text = duplicate
    /// </summary>
    public double TextSimilarityDeduplicationThreshold { get; set; } = 0.85;

    /// <summary>
    /// IoU threshold (0-1) for non-maximum suppression in text detection
    /// Higher = allow more overlapping boxes
    /// Lower = aggressive merge of overlaps
    /// </summary>
    public double NmsIouThreshold { get; set; } = 0.3;

    /// <summary>
    /// Minimum confidence (0-1) for text detection bounding boxes
    /// Lower = more detections (might include noise)
    /// Higher = only high-confidence text regions
    /// </summary>
    public double TextDetectionConfidenceThreshold { get; set; } = 0.5;

    /// <summary>
    /// Padding pixels to add around detected text bounding boxes
    /// Helps capture characters at edges of detection regions
    /// </summary>
    public int TextDetectionPadding { get; set; } = 4;

    // ============ Model Paths (optional - auto-download if missing) ============

    /// <summary>
    /// Path to EAST text detection ONNX model
    /// If null/missing, will attempt auto-download to ModelsDirectory
    /// </summary>
    public string? EastModelPath { get; set; }

    /// <summary>
    /// Path to CRAFT text detection ONNX model
    /// If null/missing, will attempt auto-download to ModelsDirectory
    /// </summary>
    public string? CraftModelPath { get; set; }

    /// <summary>
    /// Path to super-resolution ONNX model (Real-ESRGAN)
    /// If null/missing, will attempt auto-download to ModelsDirectory
    /// Used only in Quality/Ultra modes
    /// </summary>
    public string? SuperResolutionModelPath { get; set; }

    /// <summary>
    /// Path to dictionary file for post-correction (one word per line)
    /// If null/missing, uses embedded English word list
    /// </summary>
    public string? DictionaryPath { get; set; }

    /// <summary>
    /// Path to n-gram language model for context-aware post-correction
    /// Optional - only used if provided
    /// </summary>
    public string? LanguageModelPath { get; set; }

    // ============ Debugging & Diagnostics ============

    /// <summary>
    /// Save intermediate processing results to disk for debugging
    /// Warning: Can produce many files for long GIFs
    /// </summary>
    public bool SaveIntermediateImages { get; set; } = false;

    /// <summary>
    /// Directory for intermediate image output (if SaveIntermediateImages = true)
    /// </summary>
    public string IntermediateOutputDirectory { get; set; } = "./ocr-debug";

    /// <summary>
    /// Emit detailed performance metrics for each wave/phase
    /// Useful for benchmarking and optimization
    /// </summary>
    public bool EmitPerformanceMetrics { get; set; } = true;

    /// <summary>
    /// Enable spell checking to detect garbled OCR output
    /// Emits quality signals and can trigger LLM-based correction
    /// </summary>
    public bool EnableSpellChecking { get; set; } = true;

    /// <summary>
    /// Spell check quality threshold (0-1) - below this triggers correction
    /// Default: 0.5 (less than 50% correct words = garbled)
    /// </summary>
    public double SpellCheckQualityThreshold { get; set; } = 0.5;

    /// <summary>
    /// Default language for spell checking
    /// </summary>
    public string SpellCheckLanguage { get; set; } = "en_US";

    /// <summary>
    /// Apply quality mode presets to phase toggles
    /// Called automatically when QualityMode is set
    /// </summary>
    public void ApplyQualityModePresets()
    {
        switch (QualityMode)
        {
            case OcrQualityMode.Fast:
                // Fast mode: Basic temporal processing only
                EnableStabilization = true;
                EnableBackgroundSubtraction = false;
                EnableEdgeConsensus = false;
                EnableTemporalMedian = true;
                EnableSuperResolution = false;
                EnableTextDetection = false; // Skip EAST/CRAFT, use Tesseract PSM
                EnableTemporalVoting = true;
                EnablePostCorrection = false;
                MaxFramesForVoting = 5;
                ConfidenceThresholdForEarlyExit = 0.90; // More aggressive early exit
                break;

            case OcrQualityMode.Balanced:
                // Balanced mode: Add text detection and post-correction
                EnableStabilization = true;
                EnableBackgroundSubtraction = true;
                EnableEdgeConsensus = true;
                EnableTemporalMedian = true;
                EnableSuperResolution = false;
                EnableTextDetection = true; // EAST/CRAFT enabled
                EnableTemporalVoting = true;
                EnablePostCorrection = true;
                MaxFramesForVoting = 8;
                ConfidenceThresholdForEarlyExit = 0.95;
                break;

            case OcrQualityMode.Quality:
                // Quality mode: Add classical super-resolution
                EnableStabilization = true;
                EnableBackgroundSubtraction = true;
                EnableEdgeConsensus = true;
                EnableTemporalMedian = true;
                EnableSuperResolution = true; // Classical SR
                EnableTextDetection = true;
                EnableTemporalVoting = true;
                EnablePostCorrection = true;
                MaxFramesForSuperResolution = 5;
                MaxFramesForVoting = 10;
                ConfidenceThresholdForEarlyExit = 0.98; // Less aggressive - want quality
                break;

            case OcrQualityMode.Ultra:
                // Ultra mode: All phases enabled, max quality
                EnableStabilization = true;
                EnableBackgroundSubtraction = true;
                EnableEdgeConsensus = true;
                EnableTemporalMedian = true;
                EnableSuperResolution = true; // ONNX deep learning SR
                EnableTextDetection = true;
                EnableTemporalVoting = true;
                EnablePostCorrection = true;
                MaxFramesForSuperResolution = 8;
                MaxFramesForVoting = 15;
                ConfidenceThresholdForEarlyExit = 1.0; // Disable early exit - always run all phases
                break;
        }
    }
}

/// <summary>
/// OCR quality mode presets
/// </summary>
public enum OcrQualityMode
{
    /// <summary>
    /// Fast mode: SSIM + Temporal median + Voting (2-3s per GIF, +20-30% accuracy)
    /// Best for: Real-time applications, batch processing
    /// Phases: Stabilization, Temporal Median, Voting
    /// </summary>
    Fast,

    /// <summary>
    /// Balanced mode: + EAST detection + Background subtraction + Post-correction (5-7s per GIF, +30-40% accuracy)
    /// Best for: General use, interactive applications
    /// Phases: Fast + Text Detection + Background Subtraction + Edge Consensus + Post-Correction
    /// </summary>
    Balanced,

    /// <summary>
    /// Quality mode: + Classical super-resolution (10-15s per GIF, +35-45% accuracy)
    /// Best for: Archival, important documents
    /// Phases: Balanced + Classical Super-Resolution (bicubic + sharpening)
    /// </summary>
    Quality,

    /// <summary>
    /// Ultra mode: All techniques + ONNX deep learning (20-30s per GIF, +40-60% accuracy)
    /// Best for: Maximum accuracy, research
    /// Phases: Quality + ONNX Super-Resolution (Real-ESRGAN), no early exit
    /// </summary>
    Ultra
}
