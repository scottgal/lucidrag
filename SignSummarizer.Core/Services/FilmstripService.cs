using Microsoft.Extensions.Logging;
using SignSummarizer.Models;

namespace SignSummarizer.Services;

public interface IFilmstripService
{
    PoseFilmstrip CreateFilmstrip(SignAtom atom, int maxKeyFrames = 12, float noveltyThreshold = 0.1f);
    float CalculateNovelty(CanonicalLandmarks current, CanonicalLandmarks previous);
}

public sealed class FilmstripService : IFilmstripService
{
    private readonly ILogger<FilmstripService> _logger;
    private readonly ICanonicalizationService _canonicalizationService;
    private readonly int _maxKeyFrames;
    private readonly float _noveltyThreshold;
    
    public FilmstripService(
        ILogger<FilmstripService> logger,
        ICanonicalizationService canonicalizationService,
        int maxKeyFrames = 12,
        float noveltyThreshold = 0.1f)
    {
        _logger = logger;
        _canonicalizationService = canonicalizationService;
        _maxKeyFrames = maxKeyFrames;
        _noveltyThreshold = noveltyThreshold;
    }
    
    public PoseFilmstrip CreateFilmstrip(SignAtom atom, int maxKeyFrames = 12, float noveltyThreshold = 0.1f)
    {
        var filmstrip = new PoseFilmstrip(
            atom.Id,
            atom.StartTime,
            atom.EndTime,
            atom.Frames.Count);
        
        if (atom.Frames.Count == 0)
            return filmstrip;
        
        var canonicalFrames = atom.Frames
            .Where(f => f.HasAnyHand)
            .Select(f => new
            {
                Frame = f,
                Canonical = _canonicalizationService.Canonicalize(
                    f.LeftHand ?? f.RightHand!)
            })
            .ToList();
        
        if (canonicalFrames.Count == 0)
            return filmstrip;
        
        var maxFrames = Math.Min(maxKeyFrames, _maxKeyFrames);
        
        CanonicalLandmarks? lastAdded = null;
        
        for (int i = 0; i < canonicalFrames.Count; i++)
        {
            if (filmstrip.KeyPoses.Count >= maxFrames)
                break;
            
            var current = canonicalFrames[i];
            
            if (lastAdded == null)
            {
                filmstrip.AddKeyPose(new KeyPose(
                    current.Frame.FrameIndex,
                    current.Frame.Timestamp,
                    current.Canonical.AsSpan().ToArray(),
                    1.0f));
                
                lastAdded = current.Canonical;
                continue;
            }
            
            var novelty = CalculateNovelty(current.Canonical, lastAdded);
            
            if (novelty >= noveltyThreshold)
            {
                filmstrip.AddKeyPose(new KeyPose(
                    current.Frame.FrameIndex,
                    current.Frame.Timestamp,
                    current.Canonical.AsSpan().ToArray(),
                    novelty));
                
                lastAdded = current.Canonical;
            }
        }
        
        if (filmstrip.KeyPoses.Count < maxFrames && canonicalFrames.Count > 0)
        {
            var last = canonicalFrames[^1];
            if (filmstrip.KeyPoses.Count == 0 || 
                filmstrip.KeyPoses[^1].FrameIndex != last.Frame.FrameIndex)
            {
                filmstrip.AddKeyPose(new KeyPose(
                    last.Frame.FrameIndex,
                    last.Frame.Timestamp,
                    last.Canonical.AsSpan().ToArray(),
                    CalculateNovelty(last.Canonical, lastAdded!)));
            }
        }
        
        return filmstrip;
    }
    
    public float CalculateNovelty(CanonicalLandmarks current, CanonicalLandmarks previous)
    {
        var currentPoints = current.AsSpan();
        var previousPoints = previous.AsSpan();
        
        var totalDistance = 0f;
        var count = 0;
        
        for (int i = 0; i < Math.Min(currentPoints.Length, previousPoints.Length); i++)
        {
            totalDistance += currentPoints[i].DistanceTo(previousPoints[i]);
            count++;
        }
        
        return count > 0 ? totalDistance / count : 0f;
    }
}