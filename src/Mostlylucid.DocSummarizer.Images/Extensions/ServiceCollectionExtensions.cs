using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Services;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;
using Mostlylucid.DocSummarizer.Images.Services.Ocr;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.Models;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.PostProcessing;
using Mostlylucid.DocSummarizer.Images.Services.Vision;
using Mostlylucid.DocSummarizer.Services;

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
        services.TryAddSingleton<GifTextExtractor>();

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

        // ColorWave requires ColorAnalyzer and ImageStreamProcessor
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var colorAnalyzer = sp.GetRequiredService<ColorAnalyzer>();
            var streamProcessor = sp.GetService<ImageStreamProcessor>();
            return new ColorWave(colorAnalyzer, streamProcessor);
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
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var modelDownloader = sp.GetRequiredService<ModelDownloader>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<ClipEmbeddingWave>>();
            return new ClipEmbeddingWave(imageConfig, modelDownloader, logger);
        });

        // Wave orchestrator
        services.TryAddSingleton<WaveOrchestrator>();

        // Image stream processor for memory-efficient large image handling
        services.TryAddSingleton<ImageStreamProcessor>();

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
    }
}
