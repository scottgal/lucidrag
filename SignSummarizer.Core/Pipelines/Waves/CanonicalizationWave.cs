using Microsoft.Extensions.Logging;
using SignSummarizer.Models;
using SignSummarizer.Services;

namespace SignSummarizer.Pipelines;

public sealed class CanonicalizationWave : ISignWave
{
    private readonly ILogger<CanonicalizationWave> _logger;
    private readonly ICanonicalizationService _canonicalizationService;
    
    public string Name => "canonicalization";
    public string Description => "Normalizes hand landmarks for scale/rotation/translation";
    public int Priority => 150;
    public bool Enabled { get; set; } = true;
    
    public CanonicalizationWave(
        ILogger<CanonicalizationWave> logger,
        ICanonicalizationService canonicalizationService)
    {
        _logger = logger;
        _canonicalizationService = canonicalizationService;
    }
    
    public Task<SignWaveResult> ExecuteAsync(
        SignWaveContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Executing CanonicalizationWave for {AtomId}", context.SignAtomId);
        
        try
        {
            var canonicalLandmarks = new List<CanonicalLandmarks>();
            
            foreach (var frame in context.FrameLandmarks)
            {
                if (frame.LeftHand != null)
                {
                    var canonical = _canonicalizationService.Canonicalize(
                        frame.LeftHand,
                        mirrorToDominant: true);
                    canonicalLandmarks.Add(canonical);
                }
                
                if (frame.RightHand != null)
                {
                    var canonical = _canonicalizationService.Canonicalize(
                        frame.RightHand,
                        mirrorToDominant: true);
                    canonicalLandmarks.Add(canonical);
                }
            }
            
            var resultData = new Dictionary<string, object>
            {
                ["canonical_landmarks"] = canonicalLandmarks.ToArray(),
                ["canonical_count"] = canonicalLandmarks.Count,
                ["avg_scale"] = canonicalLandmarks.Count > 0 
                    ? canonicalLandmarks.Average(l => l.Scale) 
                    : 1.0f,
                ["avg_rotation"] = canonicalLandmarks.Count > 0 
                    ? canonicalLandmarks.Average(l => l.Rotation) 
                    : 0f
            };
            
            return Task.FromResult(SignWaveResult.Success(data: resultData));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CanonicalizationWave failed");
            return Task.FromResult(SignWaveResult.Failure(ex.Message));
        }
    }
}
