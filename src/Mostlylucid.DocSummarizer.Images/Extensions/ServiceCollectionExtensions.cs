using Microsoft.Extensions.Configuration;
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

        // OCR services
        services.TryAddSingleton<IOcrEngine, TesseractOcrEngine>();
        services.TryAddSingleton<GifTextExtractor>();

        // Advanced OCR pipeline services (optional, registered when config enables it)
        services.TryAddSingleton<ModelDownloader>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<ImageConfig>>().Value;
            return new ModelDownloader(
                modelsDirectory: config.ModelsDirectory,
                autoDownload: true,
                logger: sp.GetService<Microsoft.Extensions.Logging.ILogger<ModelDownloader>>());
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

        // ColorWave requires ColorAnalyzer and ImageStreamProcessor
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var colorAnalyzer = sp.GetRequiredService<ColorAnalyzer>();
            var streamProcessor = sp.GetService<ImageStreamProcessor>();
            return new ColorWave(colorAnalyzer, streamProcessor);
        });

        // OcrWave - configured with threshold from OcrConfig
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<OcrWave>>();
            // Use threshold=0 to always run OCR, or configured threshold
            var threshold = imageConfig.Value.Ocr.TextDetectionConfidenceThreshold;
            return new OcrWave(
                tesseractDataPath: null, // Uses default ./tessdata
                language: "eng",
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

        // ClipEmbeddingWave - Semantic image embeddings for similarity search
        services.AddSingleton<IAnalysisWave>(sp =>
        {
            var imageConfig = sp.GetRequiredService<IOptions<ImageConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<ClipEmbeddingWave>>();
            return new ClipEmbeddingWave(imageConfig, logger);
        });

        // Wave orchestrator
        services.TryAddSingleton<WaveOrchestrator>();

        // Image stream processor for memory-efficient large image handling
        services.TryAddSingleton<ImageStreamProcessor>();

        // Register image document handler with the handler registry
        services.AddSingleton<IDocumentHandler, ImageDocumentHandler>();
    }
}
