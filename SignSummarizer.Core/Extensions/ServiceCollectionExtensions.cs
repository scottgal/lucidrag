using Microsoft.Extensions.DependencyInjection;
using SignSummarizer.Models;
using SignSummarizer.Pipelines;
using SignSummarizer.Services;

namespace SignSummarizer.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSignSummarizerCore(
        this IServiceCollection services,
        string? modelsDirectory = null,
        Models.HandSide dominantHand = Models.HandSide.Right)
    {
        services.AddSingleton<IModelLoader>(sp =>
            new ModelLoader(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ModelLoader>>(),
                modelsDirectory));
        
        services.AddSingleton<ICanonicalizationService>(sp =>
            new CanonicalizationService(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CanonicalizationService>>(),
                dominantHand));
        
        services.AddSingleton<IPoseEmbeddingService, PoseEmbeddingService>();
        services.AddSingleton<IFilmstripService, FilmstripService>();
        services.AddSingleton<ISignVectorStore, SignVectorStore>();
        services.AddSingleton<IVisionLlmService, VisionLlmService>();
        
        services.AddSingleton<ISegmentationService, SegmentationService>();
        services.AddSingleton<IModifierDetectionService, ModifierDetectionService>();
        
        services.AddSingleton<IHandDetectionService>(sp =>
            new HandDetectionService(
                sp.GetRequiredService<IModelLoader>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<HandDetectionService>>()));
        
        services.AddSingleton<ISignPipelineRegistry, SignPipelineRegistry>();
        
        services.AddSingleton<ISignWaveCoordinator, SignWaveCoordinator>();
        
        services.AddSingleton<ISignProcessingPipeline, SignProcessingPipeline>();
        
        services.AddSingleton<HandPoseWave>();
        services.AddSingleton<CanonicalizationWave>();
        services.AddSingleton<SegmentationWave>();
        services.AddSingleton<FilmstripWave>();
        services.AddSingleton<PoseEmbeddingWave>();
        services.AddSingleton<ModifierDetectionWave>();
        // services.AddSingleton<SignClassificationWave>(); // Missing
        // services.AddSingleton<VisionLlmWave>(); // Missing
        services.AddSingleton<RagRetrievalWave>();
        
        return services;
    }
    
    public static IServiceCollection AddSignSummarizerServices(
        this IServiceCollection services)
    {
        services.AddSingleton<VideoCaptureService>();
        
        return services;
    }
    
    public static ISignWaveCoordinator UseWaveManifest(
        this ISignWaveCoordinator coordinator,
        string manifestPath)
    {
        var loader = new SignWaveManifestLoader(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SignWaveManifestLoader>.Instance);
        
        var manifest = loader.LoadFromYaml(manifestPath);
        
        return coordinator;
    }
    
    public static ISignWaveCoordinator UseWaveManifestsFromDirectory(
        this ISignWaveCoordinator coordinator,
        string directory)
    {
        var loader = new SignWaveManifestLoader(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SignWaveManifestLoader>.Instance);
        
        var manifests = loader.LoadFromDirectory(directory);
        
        return coordinator;
    }
}
