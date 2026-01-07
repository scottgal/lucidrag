using Microsoft.Extensions.Logging;
using SignSummarizer.Models;
using SignSummarizer.Pipelines;
using SignSummarizer.Services;

namespace SignSummarizer.Pipelines;

public sealed class SegmentationWave : ISignWave
{
    private readonly ILogger<SegmentationWave> _logger;
    
    public string Name => "segmentation";
    public string Description => "Segments frames into atomic sign units";
    public int Priority => 200;
    public bool Enabled { get; set; } = true;
    
    public SegmentationWave(ILogger<SegmentationWave> logger)
    {
        _logger = logger;
    }
    
    public Task<SignWaveResult> ExecuteAsync(
        SignWaveContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Executing SegmentationWave for {AtomId}", context.SignAtomId);
        
        try
        {
            var atoms = context.FrameLandmarks
                .Chunk(30) // Group into ~2 second chunks at 15fps
                .Select((chunk, index) => new SignAtom(
                    AtomType.Hold,
                    chunk.First().Timestamp,
                    chunk.Last().Timestamp)
                {
                    Frames = chunk.ToList()
                })
                .ToList();
            
            var resultData = new Dictionary<string, object>
            {
                ["atoms"] = atoms,
                ["atom_count"] = atoms.Count,
                ["avg_duration"] = atoms.Count > 0 
                    ? atoms.Average(a => a.Duration.TotalSeconds) 
                    : 0.0
            };
            
            return Task.FromResult(SignWaveResult.Success(data: resultData));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SegmentationWave failed");
            return Task.FromResult(SignWaveResult.Failure(ex.Message));
        }
    }
}

public sealed class FilmstripWave : ISignWave
{
    private readonly ILogger<FilmstripWave> _logger;
    private readonly IFilmstripService _filmstripService;
    
    public string Name => "filmstrip";
    public string Description => "Extracts keyframe filmstrip from sign atom";
    public int Priority => 250;
    public bool Enabled { get; set; } = true;
    
    public FilmstripWave(
        ILogger<FilmstripWave> logger,
        IFilmstripService filmstripService)
    {
        _logger = logger;
        _filmstripService = filmstripService;
    }
    
    public Task<SignWaveResult> ExecuteAsync(
        SignWaveContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Executing FilmstripWave for {AtomId}", context.SignAtomId);
        
        try
        {
            var atom = context.SignAtom ?? new SignAtom(
                AtomType.Hold,
                TimeSpan.Zero,
                TimeSpan.Zero);
            
            var filmstrip = _filmstripService.CreateFilmstrip(
                atom,
                maxKeyFrames: 12,
                noveltyThreshold: 0.1f);
            
            var resultData = new Dictionary<string, object>
            {
                ["filmstrip"] = filmstrip,
                ["keyframe_count"] = filmstrip.KeyPoses.Count,
                ["avg_novelty"] = filmstrip.KeyPoses.Count > 0
                    ? filmstrip.KeyPoses.Average(k => k.NoveltyScore)
                    : 0.0f,
                ["compression_ratio"] = atom.Frames.Count > 0
                    ? 1.0 - (double)filmstrip.KeyPoses.Count / atom.Frames.Count
                    : 0.0
            };
            
            return Task.FromResult(SignWaveResult.Success(data: resultData));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FilmstripWave failed");
            return Task.FromResult(SignWaveResult.Failure(ex.Message));
        }
    }
}

public sealed class PoseEmbeddingWave : ISignWave
{
    private readonly ILogger<PoseEmbeddingWave> _logger;
    private readonly IPoseEmbeddingService _poseEmbeddingService;
    
    public string Name => "pose_embedding";
    public string Description => "Generates vector embeddings from sign poses";
    public int Priority => 300;
    public bool Enabled { get; set; } = true;
    
    public PoseEmbeddingWave(
        ILogger<PoseEmbeddingWave> logger,
        IPoseEmbeddingService poseEmbeddingService)
    {
        _logger = logger;
        _poseEmbeddingService = poseEmbeddingService;
    }
    
    public Task<SignWaveResult> ExecuteAsync(
        SignWaveContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Executing PoseEmbeddingWave for {AtomId}", context.SignAtomId);
        
        try
        {
            var atom = context.SignAtom ?? new SignAtom(
                AtomType.Hold,
                TimeSpan.Zero,
                TimeSpan.Zero);
            
            var embedding = _poseEmbeddingService.CreateEmbedding(atom);
            
            var resultData = new Dictionary<string, object>
            {
                ["embedding"] = embedding,
                ["embedding_dims"] = embedding.Length,
                ["embedding_norm"] = CalculateNorm(embedding)
            };
            
            return Task.FromResult(SignWaveResult.Success(data: resultData));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PoseEmbeddingWave failed");
            return Task.FromResult(SignWaveResult.Failure(ex.Message));
        }
    }
    
    private static float CalculateNorm(float[] vector)
    {
        var sum = vector.Sum(v => v * v);
        return MathF.Sqrt(sum);
    }
}
