using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Coordination;
using Mostlylucid.DocSummarizer.Images.Services;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;
using Mostlylucid.DocSummarizer.Images.Services.Ocr;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.Detection;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.Models;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.PostProcessing;
using Mostlylucid.DocSummarizer.Images.Services.Vision;
using Mostlylucid.DocSummarizer.Images.Services.Layout;
using Mostlylucid.DocSummarizer.Images.Services.Storage;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Pipeline;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Preprocessing;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace Mostlylucid.DocSummarizer.Images.Extensions;

/// <summary>
/// Extension methods for registering DocSummarizer.Images services
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds image analysis services to the service collection with default configuration.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddDocSummarizerImages(this IServiceCollection services)
    {
        return services.AddDocSummarizerImages(_ => { });
    }

    /// <summary>
    /// Adds image analysis services with custom configuration.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Action to configure image options</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddDocSummarizerImages(
        this IServiceCollection services,
        Action<ImageConfig> configure)
    {
        services.Configure(configure);
        RegisterCoreServices(services);
        return services;
    }

    /// <summary>
    /// Adds image analysis services bound to a configuration section.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configurationSection">Configuration section containing image settings</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddDocSummarizerImages(
        this IServiceCollection services,
        IConfigurationSection configurationSection)
    {
        services.Configure<ImageConfig>(configurationSection);
        RegisterCoreServices(services);
        return services;
    }

    private static void RegisterCoreServices(IServiceCollection services)
    {
        // ONNX Session Factory for GPU-accelerated inference (DirectML/CUDA/CoreML)
        services.TryAddSingleton<OnnxSessionFactory>(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<OnnxSessionFactory>>();
            return new OnnxSessionFactory(imageConfig, logger);
        });

        // Sub-analyzers
        services.TryAddSingleton<ColorAnalyzer>();
        services.TryAddSingleton<EdgeAnalyzer>();
        services.TryAddSingleton<BlurAnalyzer>();
        services.TryAddSingleton<TextLikelinessAnalyzer>();

        // Main analyzer
        services.TryAddSingleton<IImageAnalyzer, ImageAnalyzer>();

        // Model downloader for auto-downloading tessdata and ONNX models
        services.TryAddSingleton<ModelDownloader>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<ImageConfig>>().Value;
            return new ModelDownloader(
                modelsDirectory: config.ModelsDirectory,
                autoDownload: true,
                logger: sp.GetService<Microsoft.Extensions.Logging.ILogger<ModelDownloader>>());
        });

        // OCR services - TesseractOcrEngine with auto-download support
        services.TryAddSingleton<IOcrEngine>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<ImageConfig>>().Value;
            var modelDownloader = sp.GetRequiredService<ModelDownloader>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<TesseractOcrEngine>>();
            return new TesseractOcrEngine(
                modelDownloader: modelDownloader,
                tesseractDataPath: config.TesseractDataPath,
                language: config.TesseractLanguage,
                logger: logger);
        });
        services.TryAddSingleton<GifTextExtractor>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<ImageConfig>>().Value;
            var ocrEngine = sp.GetRequiredService<IOcrEngine>();
            var advancedOcr = sp.GetService<AdvancedGifOcrService>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<GifTextExtractor>>();
            return new GifTextExtractor(ocrEngine, config, advancedOcr, logger);
        });

        services.TryAddSingleton<AdvancedGifOcrService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<ImageConfig>>().Value;
            var ocrEngine = sp.GetRequiredService<IOcrEngine>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<AdvancedGifOcrService>>();

            // Apply quality mode presets on first access
            config.Ocr.ApplyQualityModePresets();

            return new AdvancedGifOcrService(ocrEngine, config.Ocr, logger);
        });

        // Analysis Waves (for wave-based pipeline)
        // Note: Use AddSingleton (not TryAdd) to register multiple implementations of IAnalysisWave

        // IdentityWave - Basic image properties (format, dimensions, hash)
        // Highest priority - provides fundamental properties other waves may depend on
        services.AddSingleton<IAnalysisWave>(sp => new IdentityWave());

        // SceneDetectionService - Histogram-based scene boundary detection
        services.TryAddSingleton<SceneDetectionService>(sp =>
        {
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<SceneDetectionService>>();
            return new SceneDetectionService(logger);
        });

        // SceneDetectionWave - Early scene detection for animated images
        // Priority 15: After Identity, before ColorWave - informs escalation decisions
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var sceneService = sp.GetRequiredService<SceneDetectionService>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<SceneDetectionWave>>();
            return new SceneDetectionWave(sceneService, logger);
        });

        // ColorWave requires ColorAnalyzer and ImageStreamProcessor
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var colorAnalyzer = sp.GetRequiredService<ColorAnalyzer>();
            var streamProcessor = sp.GetService<ImageStreamProcessor>();
            return new ColorWave(colorAnalyzer, streamProcessor);
        });

        // AutoRoutingWave - Uses fast signals to route images through optimal paths
        // Runs after Identity/Color but before expensive waves
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var signalDb = sp.GetService<ISignalDatabase>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<AutoRoutingWave>>();
            return new AutoRoutingWave(signalDb, logger);
        });

        // TextDetectionService - EAST/CRAFT ONNX text region detection
        // Uses OnnxSessionFactory for GPU acceleration (CUDA/DirectML)
        services.TryAddSingleton<TextDetectionService>(sp =>
        {
            var modelDownloader = sp.GetRequiredService<ModelDownloader>();
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var sessionFactory = sp.GetRequiredService<OnnxSessionFactory>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<TextDetectionService>>();
            return new TextDetectionService(modelDownloader, imageConfig.Value.Ocr, sessionFactory, logger);
        });

        // TextDetectionWave - Fast ML-based text region detection (EAST/CRAFT)
        // Priority 82: Runs after routing, before OCR waves
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var detectionService = sp.GetService<TextDetectionService>();
            var imageConfig = sp.GetService<IOptions<ImageConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<TextDetectionWave>>();
            return new TextDetectionWave(detectionService, imageConfig, logger);
        });

        // OcrPreprocessorConfig - Configuration for image preprocessing before OCR
        services.TryAddSingleton(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>().Value;
            // Use quality mode to select preset, or allow custom config via ImageConfig
            var config = imageConfig.Ocr.QualityMode switch
            {
                OcrQualityMode.Fast => OcrPreprocessorConfig.Fast,
                OcrQualityMode.Quality => OcrPreprocessorConfig.Quality,
                _ => OcrPreprocessorConfig.Default
            };

            // Configure super-resolution model path (auto-download if needed)
            if (imageConfig.Ocr.EnableSuperResolution)
            {
                config.EnableSuperResolution = true;
                config.SuperResolutionModelPath = imageConfig.Ocr.SuperResolutionModelPath
                    ?? Path.Combine(imageConfig.ModelsDirectory, "realesrgan-x4.onnx");
            }

            return config;
        });

        // OcrPreprocessingWave - Image preprocessing before OCR (deskew, denoise, contrast)
        // Priority 65: Runs before other OCR waves to enhance image quality
        // Uses ModelDownloader for auto-downloading Real-ESRGAN model for super-resolution
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var config = sp.GetRequiredService<OcrPreprocessorConfig>();
            var modelDownloader = sp.GetService<ModelDownloader>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<OcrPreprocessingWave>>();
            return new OcrPreprocessingWave(config, modelDownloader, logger);
        });

        // MlOcrWave - Fast ML-based OCR using OpenCV + Florence-2 (priority 28)
        // For animated GIFs: Uses filmstrip mode - caches frames for VisionLlmWave
        // For static images: Uses targeted region OCR with Florence-2
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var florence2 = sp.GetService<Florence2CaptionService>();
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<MlOcrWave>>();
            return new MlOcrWave(florence2, imageConfig, logger);
        });

        // DoclingOcrWave - OCR via Docling service (priority 58)
        // When enabled, provides advanced layout-aware OCR for documents/images
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var docConfig = sp.GetRequiredService<IOptions<DocSummarizerConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<DoclingOcrWave>>();
            return new DoclingOcrWave(docConfig, logger);
        });

        // OcrWave - configured with threshold from OcrConfig, uses auto-download
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var modelDownloader = sp.GetRequiredService<ModelDownloader>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<OcrWave>>();
            // Use threshold=0 to always run OCR, or configured threshold
            var threshold = imageConfig.Value.Ocr.TextDetectionConfidenceThreshold;
            return new OcrWave(
                modelDownloader: modelDownloader,
                tesseractDataPath: imageConfig.Value.TesseractDataPath,
                language: imageConfig.Value.TesseractLanguage,
                enabled: imageConfig.Value.EnableOcr,
                textLikelinessThreshold: threshold,
                logger: logger);
        });

        // AdvancedOcrWave requires IOcrEngine and IOptions<ImageConfig>
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var ocrEngine = sp.GetRequiredService<IOcrEngine>();
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<AdvancedOcrWave>>();
            return new AdvancedOcrWave(ocrEngine, imageConfig, logger);
        });

        // OCR Post-Processing services (Tier 2 & 3)
        services.TryAddSingleton<MlContextChecker>(sp =>
        {
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<MlContextChecker>>();
            return new MlContextChecker(logger);
        });

        services.TryAddSingleton<SentinelLlmCorrector>(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<SentinelLlmCorrector>>();
            return new SentinelLlmCorrector(imageConfig, logger);
        });

        // OcrQualityWave - 3-tier OCR correction pipeline
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<OcrQualityWave>>();
            var mlContextChecker = sp.GetService<MlContextChecker>();
            var sentinelLlmCorrector = sp.GetService<SentinelLlmCorrector>();
            return new OcrQualityWave(imageConfig, logger, mlContextChecker, sentinelLlmCorrector);
        });

        // DeepseekOcrWave - OCR via DeepSeek VLM (Ollama)
        // Priority 53: Runs after NanonetsOcrWave (54), before OlmOcr2Wave (51)
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<DeepseekOcrWave>>();
            return new DeepseekOcrWave(imageConfig, logger);
        });

        // LightOnOcrWave - State-of-the-art 1B OCR model from LightOn AI
        // Priority 52 (or 65 when AsPrimary): Fast, accurate OCR for tables, receipts, forms
        // Runs after DeepseekOcr (53), before OlmOcr2 (51)
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<LightOnOcrWave>>();
            return new LightOnOcrWave(imageConfig, logger);
        });

        // OcrBenchmarkWave - Compares multiple OCR systems and generates comparison reports
        // Priority 45: Runs after all OCR waves (50-65) to collect and compare results
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<OcrBenchmarkWave>>();
            // Create SpellChecker with models directory from config
            var dictionaryPath = !string.IsNullOrEmpty(imageConfig.Value.Ocr.DictionaryPath)
                ? imageConfig.Value.Ocr.DictionaryPath
                : imageConfig.Value.ModelsDirectory != null
                    ? Path.Combine(imageConfig.Value.ModelsDirectory, "dictionaries")
                    : null;
            var spellChecker = new SpellChecker(dictionaryPath, sp.GetService<Microsoft.Extensions.Logging.ILogger<SpellChecker>>());
            return new OcrBenchmarkWave(imageConfig, spellChecker, logger);
        });

        // FaceDetectionWave - PII-respecting face detection and embedding
        services.AddSingleton<IAnalysisWave>(sp => new FaceDetectionWave());

        // DigitalFingerprintWave - Perceptual hashing for image similarity
        services.AddSingleton<IAnalysisWave>(sp => new DigitalFingerprintWave());

        // ExifForensicsWave - EXIF metadata extraction
        services.AddSingleton<IAnalysisWave>(sp => new ExifForensicsWave());

        // VisionLlmWave - Captions and entity extraction using vision-language models
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<VisionLlmWave>>();
            return new VisionLlmWave(imageConfig, logger);
        });

        // ClipEmbeddingWave - Semantic image embeddings for similarity search (auto-downloads model)
        // Uses OnnxSessionFactory for GPU acceleration (DirectML/CUDA/CoreML)
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var modelDownloader = sp.GetRequiredService<ModelDownloader>();
            var sessionFactory = sp.GetRequiredService<OnnxSessionFactory>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<ClipEmbeddingWave>>();
            return new ClipEmbeddingWave(imageConfig, modelDownloader, sessionFactory, logger);
        });

        // LayoutDetectionWave - Document layout detection using YOLOv10-DocLayNet (auto-downloads model)
        // Priority 64: Runs early before OCR to guide text extraction with layout regions
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var modelDownloader = sp.GetRequiredService<ModelDownloader>();
            var sessionFactory = sp.GetRequiredService<OnnxSessionFactory>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<LayoutDetectionWave>>();
            return new LayoutDetectionWave(imageConfig, modelDownloader, sessionFactory, logger);
        });

        // LayoutRegionExtractor - Crops detected regions for downstream processing
        services.TryAddSingleton<LayoutRegionExtractor>(sp =>
        {
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<LayoutRegionExtractor>>();
            return new LayoutRegionExtractor(logger);
        });

        // LayoutRoutingWave - Routes extracted regions to DataSummarizer/ImageSummarizer
        // Priority 63: Runs after LayoutDetectionWave (64) to extract and route tables/figures
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var extractor = sp.GetRequiredService<LayoutRegionExtractor>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<LayoutRoutingWave>>();
            return new LayoutRoutingWave(imageConfig, extractor, logger);
        });

        // TableExtractionWave - Converts table images to CSV using Vision LLM
        // Priority 62: Runs after LayoutRoutingWave (63), outputs CSV for DataSummarizer
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var visionService = sp.GetRequiredService<VisionLlmService>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<TableExtractionWave>>();
            return new TableExtractionWave(visionService, logger);
        });

        // TableProfilingWave - Profiles extracted CSVs using DataSummarizer
        // Priority 61: Runs after TableExtractionWave (62), emits data quality signals
        // Note: DataSummarizer services are scoped, so we pass null here and let the wave skip gracefully
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            // DataSummarizer services are scoped - cannot resolve from singleton context
            // Pass null to let the wave skip data profiling (it handles null gracefully)
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<TableProfilingWave>>();
            return new TableProfilingWave(null, null, logger);
        });

        // RecursiveImageWave - Processes extracted picture regions recursively
        // Priority 60: Runs after TableProfilingWave (61), generates captions/embeddings for sub-images
        // Note: Uses deferred wave resolution to avoid circular dependency issues
        // IMPORTANT: Pass null for waves here - they're resolved lazily in AnalyzeAsync
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            // Don't resolve waves here - causes circular dependency!
            // RecursiveImageWave handles null gracefully with fallback mode
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<RecursiveImageWave>>();
            return new RecursiveImageWave(null, logger);
        });

        // OcrLayoutValidationWave - Validates OCR output against layout text regions
        // Priority 55: Runs after OCR waves (60), compares OCR coverage with detected text areas
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<OcrLayoutValidationWave>>();
            return new OcrLayoutValidationWave(logger);
        });

        // ClipZeroShotService - Zero-shot classification using CLIP text embeddings
        // Matches image embeddings against predetermined entity labels
        services.TryAddSingleton<ClipZeroShotService>(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var modelDownloader = sp.GetService<ModelDownloader>();
            var sessionFactory = sp.GetService<OnnxSessionFactory>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<ClipZeroShotService>>();
            return new ClipZeroShotService(imageConfig, modelDownloader, sessionFactory, logger);
        });

        // ClipClassificationWave - Zero-shot entity classification using predetermined labels
        // Priority 46: Runs after ClipEmbeddingWave (45), emits entity type signals for NER
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var clipService = sp.GetRequiredService<ClipZeroShotService>();
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<ClipClassificationWave>>();
            return new ClipClassificationWave(clipService, imageConfig, logger);
        });

        // ChartDetectionWave - Specialized chart/graph detection using CLIP classification
        // Priority 47: Runs after ClipClassificationWave (46), emits chart.detected and chart.type signals
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var clipService = sp.GetRequiredService<ClipZeroShotService>();
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<ChartDetectionWave>>();
            return new ChartDetectionWave(clipService, imageConfig, logger);
        });

        // ChartExtractionWave - Extracts structured data from detected charts using Vision LLM
        // Priority 48: Runs after ChartDetectionWave (47), extracts data when chart.needs_extraction is true
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<ChartExtractionWave>>();
            return new ChartExtractionWave(imageConfig, logger);
        });

        // MotionAnalyzer - Optical flow analysis for animated GIFs
        services.TryAddSingleton<MotionAnalyzer>(sp =>
        {
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<MotionAnalyzer>>();
            return new MotionAnalyzer(logger);
        });

        // MotionWave - Motion detection for animated images using optical flow
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var motionAnalyzer = sp.GetRequiredService<MotionAnalyzer>();
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var httpClient = sp.GetService<HttpClient>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<MotionWave>>();
            return new MotionWave(motionAnalyzer, imageConfig, httpClient, logger);
        });

        // ContradictionDetector - config-driven signal validation
        services.TryAddSingleton<ContradictionDetector>(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>().Value;
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<ContradictionDetector>>();

            // Convert custom rules from config to ContradictionRule objects
            var customRules = imageConfig.Contradiction.CustomRules?
                .Select(r => new ContradictionRule
                {
                    RuleId = r.RuleId,
                    Description = r.Description,
                    SignalKeyA = r.SignalKeyA,
                    SignalKeyB = r.SignalKeyB,
                    Type = Enum.TryParse<ContradictionType>(r.Type, true, out var t)
                        ? t : ContradictionType.ValueConflict,
                    Threshold = r.Threshold,
                    Severity = Enum.TryParse<ContradictionSeverity>(r.Severity, true, out var s)
                        ? s : ContradictionSeverity.Warning,
                    Resolution = Enum.TryParse<ResolutionStrategy>(r.Resolution, true, out var rs)
                        ? rs : ResolutionStrategy.PreferHigherConfidence,
                    Enabled = r.Enabled,
                    MinConfidenceThreshold = imageConfig.Contradiction.MinConfidenceThreshold
                })
                .ToList();

            return new ContradictionDetector(customRules, logger);
        });

        // ContradictionWave - Signal validation and conflict detection (runs last)
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var detector = sp.GetRequiredService<ContradictionDetector>();
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<ContradictionWave>>();
            return new ContradictionWave(detector, imageConfig, logger);
        });

        // Wave orchestrator (legacy)
        services.TryAddSingleton<WaveOrchestrator>();

        // Signal-based coordination infrastructure (new)
        // WaveManifestLoader loads YAML wave declarations for signal contracts
        services.TryAddSingleton<WaveManifestLoader>(sp =>
        {
            var loader = new WaveManifestLoader();
            loader.LoadEmbeddedManifests();
            return loader;
        });

        // ProfiledWaveCoordinator uses coordinator profiles for different execution contexts
        services.TryAddSingleton<ProfiledWaveCoordinator>(sp =>
        {
            var manifestLoader = sp.GetRequiredService<WaveManifestLoader>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<ProfiledWaveCoordinator>>();
            var coordinator = new ProfiledWaveCoordinator(manifestLoader, logger);

            // Register all waves with the coordinator
            var waves = sp.GetServices<IAnalysisWave>();
            foreach (var wave in waves)
            {
                coordinator.RegisterWave(wave);
            }

            return coordinator;
        });

        // WaveSignalCoordinator for reactive signal composition
        services.TryAddSingleton<WaveSignalCoordinator>(sp =>
        {
            var manifestLoader = sp.GetRequiredService<WaveManifestLoader>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<WaveSignalCoordinator>>();
            var coordinator = new WaveSignalCoordinator(manifestLoader, logger);

            // Register all waves
            var waves = sp.GetServices<IAnalysisWave>();
            foreach (var wave in waves)
            {
                coordinator.RegisterWave(wave);
            }

            return coordinator;
        });

        // Image stream processor for memory-efficient large image handling
        services.TryAddSingleton<ImageStreamProcessor>();

        // Unified Vision Service (supports multiple providers: Ollama, Anthropic, OpenAI)
        services.TryAddSingleton<UnifiedVisionService>(sp =>
        {
            var config = sp.GetService<IConfiguration>();
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>().Value;
            var loggerFactory = sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();

            // Create in-memory config if IConfiguration not available
            if (config == null)
            {
                var configData = new Dictionary<string, string?>
                {
                    ["Ollama:BaseUrl"] = imageConfig.OllamaBaseUrl ?? "http://localhost:11434",
                    ["Ollama:VisionModel"] = imageConfig.VisionLlmModel ?? "minicpm-v:8b"
                };
                config = new ConfigurationBuilder()
                    .AddInMemoryCollection(configData)
                    .Build();
            }
            return new UnifiedVisionService(config, loggerFactory);
        });

        // Fast caption service (uses UnifiedVisionService for consistent prompts across all clients)
        services.TryAddSingleton<FastCaptionService>(sp =>
        {
            var unifiedVision = sp.GetRequiredService<UnifiedVisionService>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<FastCaptionService>>();
            return new FastCaptionService(unifiedVision, logger);
        });

        // Florence-2 caption service (fast local ONNX-based captioning with color enhancement)
        services.TryAddSingleton<Florence2CaptionService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<ImageConfig>>();
            var colorAnalyzer = sp.GetRequiredService<ColorAnalyzer>();
            var sceneService = sp.GetRequiredService<SceneDetectionService>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<Florence2CaptionService>>();
            return new Florence2CaptionService(config, colorAnalyzer, sceneService, logger);
        });

        // Florence-2 Wave (fast local captioning/OCR using ONNX, uses OpenCV for complexity assessment)
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var florence2Service = sp.GetRequiredService<Florence2CaptionService>();
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<Florence2Wave>>();
            return new Florence2Wave(florence2Service, imageConfig, logger);
        });

        // Nanonets OCR-s Wave (OpenAI-compatible VLM, Markdown OCR)
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<NanonetsOcrWave>>();
            return new NanonetsOcrWave(imageConfig, logger);
        });

        // OlmOCR-2 Wave (final OCR escalation before Vision LLM)
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<OlmOcr2Wave>>();
            return new OlmOcr2Wave(imageConfig, logger);
        });

        // Vision LLM services for caption/description generation
        services.TryAddSingleton<VisionLlmService>(sp =>
        {
            // VisionLlmService expects IConfiguration, so get it if available or use default config
            var config = sp.GetService<IConfiguration>();
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>().Value;
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<VisionLlmService>>();

            // If IConfiguration is available, use it; otherwise create an in-memory config
            if (config == null)
            {
                var configData = new Dictionary<string, string?>
                {
                    ["Ollama:BaseUrl"] = imageConfig.OllamaBaseUrl ?? "http://localhost:11434",
                    ["Ollama:VisionModel"] = imageConfig.VisionLlmModel ?? "minicpm-v:8b"
                };
                config = new ConfigurationBuilder()
                    .AddInMemoryCollection(configData)
                    .Build();
            }
            return new VisionLlmService(config, logger!);
        });

        // Escalation service for hybrid heuristic + LLM analysis
        services.TryAddSingleton<EscalationService>(sp =>
        {
            var imageAnalyzer = sp.GetRequiredService<IImageAnalyzer>();
            var visionLlmService = sp.GetRequiredService<VisionLlmService>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<EscalationService>>();
            return new EscalationService(imageAnalyzer, visionLlmService, logger);
        });

        // Register image document handler with the handler registry
        services.AddSingleton<IDocumentHandler, ImageDocumentHandler>();

        // Register the pipeline for unified pipeline registry
        services.TryAddSingleton<ImagePipeline>();
        services.AddSingleton<IPipeline>(sp => sp.GetRequiredService<ImagePipeline>());
    }
}
