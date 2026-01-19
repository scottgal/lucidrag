using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Pipeline;
using AudioSummarizer.Core.Services.Analysis;
using AudioSummarizer.Core.Services.Analysis.Waves;
using AudioSummarizer.Core.Services.Audio;
using AudioSummarizer.Core.Services.Fingerprinting;
using AudioSummarizer.Core.Services.SourceSeparation;
using AudioSummarizer.Core.Services.Transcription;
using AudioSummarizer.Core.Services.Voice;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace AudioSummarizer.Core.Extensions;

/// <summary>
/// Service collection extensions for AudioSummarizer
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register AudioSummarizer services
    /// </summary>
    public static IServiceCollection AddAudioSummarizer(
        this IServiceCollection services,
        Action<AudioConfig>? configure = null)
    {
        // Configuration
        if (configure != null)
        {
            services.Configure(configure);
        }

        // Core services
        services.AddSingleton<AudioWaveOrchestrator>();

        // HttpClient for Ollama
        services.AddHttpClient("Ollama");

        // HttpClient for HuggingFace model downloads
        services.AddHttpClient("HuggingFace", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10); // Large model downloads
        });

        // Fingerprinting service (Phase 2)
        services.AddSingleton<IFingerprintService, PureNetFingerprintService>();

        // Transcription services (Phase 3)
        services.AddSingleton<WhisperModelDownloader>();
        services.AddSingleton<ITranscriptionService, WhisperTranscriptionService>();
        services.AddSingleton<ITranscriptionService, OllamaTranscriptionService>();

        // Voice embedding service (Phase 4)
        services.AddSingleton<VoiceEmbeddingModelDownloader>();
        services.AddSingleton<VoiceEmbeddingService>();

        // Speaker diarization service (Phase 5)
        services.AddSingleton<SpeakerDiarizationService>();

        // Audio segment extraction
        services.AddSingleton<AudioSegmentExtractor>();

        // Source separation services (HTDemucs for music - auto-downloads ~210MB model)
        services.AddSingleton<DemucsModelDownloader>();
        services.AddSingleton<AudioStftService>();
        services.AddSingleton<ISourceSeparationService, DemucsSourceSeparationService>();

        // Register waves (in priority order)
        services.AddSingleton<IAudioWave, IdentityWave>();           // Phase 1: Priority 100
        services.AddSingleton<IAudioWave, FingerprintWave>();        // Phase 2: Priority 90
        services.AddSingleton<IAudioWave, ContentClassifierWave>();  // Phase 3.5: Priority 70
        services.AddSingleton<IAudioWave, MusicAnalysisWave>();      // Phase 3.6: Priority 65
        services.AddSingleton<IAudioWave, SourceSeparationWave>();   // Phase 3.7: Priority 55 (Demucs)
        services.AddSingleton<IAudioWave, TranscriptionWave>();      // Phase 3: Priority 60

        // DoclingTranscriptionWave - Transcription via Docling ASR (priority 62)
        // When enabled, provides alternative transcription using Docling service
        services.AddSingleton<IAudioWave>(sp =>
        {
            var docConfig = sp.GetRequiredService<IOptions<DocSummarizerConfig>>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<DoclingTranscriptionWave>>();
            return new DoclingTranscriptionWave(docConfig, logger);
        });

        services.AddSingleton<IAudioWave, SpeakerDiarizationWave>(); // Phase 5: Priority 50
        services.AddSingleton<IAudioWave, VoiceEmbeddingWave>();     // Phase 4: Priority 30

        // Register the pipeline for unified pipeline registry
        services.AddSingleton<AudioPipeline>();
        services.AddSingleton<IPipeline>(sp => sp.GetRequiredService<AudioPipeline>());

        return services;
    }

    /// <summary>
    /// Register AudioSummarizer services with IConfiguration
    /// </summary>
    public static IServiceCollection AddAudioSummarizer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AudioConfig>(configuration.GetSection(AudioConfig.SectionName));
        return services.AddAudioSummarizer();
    }
}
