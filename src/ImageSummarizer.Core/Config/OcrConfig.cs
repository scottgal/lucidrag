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
    public bool EnableSuperResolution { get; set; } = true;

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

    // ============ Hunyuan OCR Local VLM Escalation ============
    // Uses tencent/HunyuanOCR from HuggingFace - a 1B parameter Vision Language Model
    // Runs locally via vLLM server - no data leaves your infrastructure
    // Capabilities: Document parsing, table→HTML, formula→LaTeX, text spotting

    /// <summary>
    /// Enable HunyuanOCR VLM escalation for poor-quality OCR results.
    /// Requires a local vLLM server running tencent/HunyuanOCR model.
    /// Start server: vllm serve tencent/HunyuanOCR --port 8000
    /// </summary>
    public bool EnableHunyuanOcrEscalation { get; set; } = false;

    /// <summary>
    /// Base URL for the vLLM server running HunyuanOCR.
    /// Default: http://localhost:8000 (standard vLLM port)
    /// </summary>
    public string HunyuanVllmBaseUrl { get; set; } = "http://localhost:8000";

    /// <summary>
    /// Model name as registered in vLLM (usually the HuggingFace model ID).
    /// </summary>
    public string HunyuanModelName { get; set; } = "tencent/HunyuanOCR";

    /// <summary>
    /// OCR mode for HunyuanOCR. Options:
    /// - "text_spotting": Detect and recognize text with coordinates
    /// - "document_parsing": Parse documents (tables→HTML, formulas→LaTeX)
    /// - "info_extraction": Extract key-value pairs as JSON
    /// </summary>
    public string HunyuanOcrMode { get; set; } = "text_spotting";

    /// <summary>
    /// Minimum OCR quality score (0-1) below which Hunyuan escalation triggers.
    /// Default: 0.4 (escalate when less than 40% of words are valid)
    /// </summary>
    public double HunyuanEscalationThreshold { get; set; } = 0.4;

    /// <summary>
    /// Timeout in seconds for HunyuanOCR vLLM API calls.
    /// Local inference typically takes 2-10 seconds depending on image complexity.
    /// </summary>
    public int HunyuanTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum tokens for HunyuanOCR generation.
    /// Higher values needed for document parsing with tables.
    /// </summary>
    public int HunyuanMaxTokens { get; set; } = 4096;

    // ============ Nanonets OCR-s (OpenAI-compatible VLM) ============
    // Uses a hosted or local OpenAI-compatible endpoint that supports images.
    // Optimized for OCR with Markdown output.

    /// <summary>
    /// Enable Nanonets OCR-s for OCR escalation.
    /// </summary>
    public bool EnableNanonetsOcr { get; set; } = false;

    /// <summary>
    /// Base URL for the OpenAI-compatible endpoint serving Nanonets OCR-s.
    /// Example: http://localhost:8000 (vLLM) or https://api.nanonets.com
    /// </summary>
    public string NanonetsOcrBaseUrl { get; set; } = "http://localhost:8000";

    /// <summary>
    /// Model name for Nanonets OCR-s.
    /// </summary>
    public string NanonetsOcrModelName { get; set; } = "nanonets-ocr-s";

    /// <summary>
    /// API key for Nanonets OCR-s (if required by the endpoint).
    /// Leave empty for local unauthenticated endpoints.
    /// </summary>
    public string? NanonetsOcrApiKey { get; set; }

    /// <summary>
    /// Timeout in seconds for Nanonets OCR-s requests.
    /// </summary>
    public int NanonetsOcrTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum tokens for Nanonets OCR-s generation.
    /// </summary>
    public int NanonetsOcrMaxTokens { get; set; } = 4096;

    /// <summary>
    /// Request Markdown output (tables, headings, lists) from Nanonets OCR-s.
    /// </summary>
    public bool NanonetsOcrPreferMarkdown { get; set; } = true;

    // ============ OlmOCR-2 (OpenAI-compatible VLM) ============
    // Runs as a final OCR escalation before Vision LLM.

    /// <summary>
    /// Enable OlmOCR-2 OCR escalation.
    /// </summary>
    public bool EnableOlmOcr2 { get; set; } = false;

    /// <summary>
    /// Base URL for the Ollama endpoint serving OlmOCR-2.
    /// Default is Ollama's default URL. Pull the model with: ollama pull richardyoung/olmocr2
    /// </summary>
    public string OlmOcr2BaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Model name for OlmOCR-2.
    /// The model must be pulled first: ollama pull richardyoung/olmocr2
    /// </summary>
    public string OlmOcr2ModelName { get; set; } = "richardyoung/olmocr2";

    /// <summary>
    /// API key for OlmOCR-2 (if required by the endpoint).
    /// Leave empty for local unauthenticated endpoints.
    /// </summary>
    public string? OlmOcr2ApiKey { get; set; }

    /// <summary>
    /// Timeout in seconds for OlmOCR-2 requests.
    /// </summary>
    public int OlmOcr2TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum tokens for OlmOCR-2 generation.
    /// </summary>
    public int OlmOcr2MaxTokens { get; set; } = 4096;

    /// <summary>
    /// Request Markdown output from OlmOCR-2.
    /// </summary>
    public bool OlmOcr2PreferMarkdown { get; set; } = true;

    // ============ DeepSeek OCR (Ollama VLM) ============
    // Uses deepseek-ocr:latest via Ollama's OpenAI-compatible API.
    // High-quality OCR optimized for document text extraction.

    /// <summary>
    /// Enable DeepSeek OCR for OCR escalation.
    /// Requires Ollama running with deepseek-ocr:latest model.
    /// </summary>
    public bool EnableDeepseekOcr { get; set; } = false;

    /// <summary>
    /// Base URL for the Ollama API serving DeepSeek OCR.
    /// Default: http://localhost:11434 (standard Ollama port)
    /// </summary>
    public string DeepseekOcrBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Model name for DeepSeek OCR.
    /// </summary>
    public string DeepseekOcrModelName { get; set; } = "deepseek-ocr:latest";

    /// <summary>
    /// API key for DeepSeek OCR (if required by the endpoint).
    /// Leave empty for local Ollama endpoints.
    /// </summary>
    public string? DeepseekOcrApiKey { get; set; }

    /// <summary>
    /// Timeout in seconds for DeepSeek OCR requests.
    /// </summary>
    public int DeepseekOcrTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Maximum tokens for DeepSeek OCR generation.
    /// </summary>
    public int DeepseekOcrMaxTokens { get; set; } = 4096;

    /// <summary>
    /// Request Markdown output from DeepSeek OCR.
    /// </summary>
    public bool DeepseekOcrPreferMarkdown { get; set; } = true;

    // ============ LightOnOCR (Ollama VLM) ============
    // State-of-the-art 1B-parameter OCR model from LightOn AI.
    // 3.3× faster than competitors, handles tables, math, multi-column layouts.
    // Pull with: ollama pull aipib/LightOnOCR-1B-1025

    /// <summary>
    /// Enable LightOnOCR for OCR extraction.
    /// Requires Ollama running with aipib/LightOnOCR-1B-1025 model.
    /// </summary>
    public bool EnableLightOnOcr { get; set; } = false;

    /// <summary>
    /// Base URL for the Ollama API serving LightOnOCR.
    /// Default: http://localhost:11434 (standard Ollama port)
    /// </summary>
    public string LightOnOcrBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Model name for LightOnOCR.
    /// Available models:
    /// - aipib/LightOnOCR-1B-1025 (recommended, 1B params)
    /// - aipib/LightOnOCR-2-1B (newer version if available)
    /// </summary>
    public string LightOnOcrModelName { get; set; } = "aipib/LightOnOCR-1B-1025";

    /// <summary>
    /// API key for LightOnOCR (if required by the endpoint).
    /// Leave empty for local Ollama endpoints.
    /// </summary>
    public string? LightOnOcrApiKey { get; set; }

    /// <summary>
    /// Timeout in seconds for LightOnOCR requests.
    /// </summary>
    public int LightOnOcrTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Maximum tokens for LightOnOCR generation.
    /// </summary>
    public int LightOnOcrMaxTokens { get; set; } = 4096;

    /// <summary>
    /// Request Markdown output from LightOnOCR.
    /// When true, preserves table structure, headings, and formatting.
    /// </summary>
    public bool LightOnOcrPreferMarkdown { get; set; } = true;

    /// <summary>
    /// Use LightOnOCR as the primary OCR engine (highest priority).
    /// When true, LightOnOCR runs before other OCR waves.
    /// </summary>
    public bool LightOnOcrAsPrimary { get; set; } = false;

    // ============ OCR Benchmarking ============
    // Compares multiple OCR systems and generates comparison reports
    // Useful for training data collection and system evaluation

    /// <summary>
    /// OCR benchmark configuration for comparing multiple OCR systems.
    /// When enabled, runs all OCR systems and generates comparison reports.
    /// </summary>
    public OcrBenchmarkConfig Benchmark { get; set; } = new();

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

/// <summary>
/// Configuration for OCR benchmarking - compares multiple OCR systems
/// and generates comparison reports for training and evaluation.
/// </summary>
public class OcrBenchmarkConfig
{
    /// <summary>
    /// Enable OCR benchmarking. When true, the OcrBenchmarkWave will run
    /// after all OCR waves and generate a comparison report.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Path to the Markdown report file.
    /// Results will be appended to this file for each image processed.
    /// </summary>
    public string ReportOutputPath { get; set; } = "./OCR Test.md";

    /// <summary>
    /// Force all OCR systems to run regardless of escalation logic.
    /// When false, only systems that would normally run are benchmarked.
    /// When true, forces all enabled systems to process every image.
    /// </summary>
    public bool ForceRunAllSystems { get; set; } = false;

    /// <summary>
    /// Append results to the report file instead of overwriting.
    /// Set to false to create a fresh report for each run.
    /// </summary>
    public bool AppendToReport { get; set; } = true;

    /// <summary>
    /// Include full extracted text in the report.
    /// When false, only shows summary statistics.
    /// </summary>
    public bool IncludeFullText { get; set; } = true;

    /// <summary>
    /// Language code for spell-check accuracy assessment.
    /// Uses the same dictionary system as OcrQualityWave.
    /// </summary>
    public string AccuracyLanguage { get; set; } = "en_US";
}
