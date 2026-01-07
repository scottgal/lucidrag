using Microsoft.Extensions.Logging;
using SignSummarizer.Models;

namespace SignSummarizer.Services;

public interface ISegmentationService
{
    IAsyncEnumerable<SignAtom> SegmentStreamAsync(
        IAsyncEnumerable<FrameLandmarks> frames,
        CancellationToken cancellationToken = default);
}

public sealed class SegmentationService : ISegmentationService
{
    private readonly ILogger<SegmentationService> _logger;
    private readonly float _holdMotionThreshold;
    private readonly float _transitionMotionThreshold;
    private readonly TimeSpan _minAtomDuration;
    private readonly TimeSpan _minHoldDuration;
    
    public SegmentationService(
        ILogger<SegmentationService> logger,
        float holdMotionThreshold = 0.02f,
        float transitionMotionThreshold = 0.1f,
        TimeSpan? minAtomDuration = null,
        TimeSpan? minHoldDuration = null)
    {
        _logger = logger;
        _holdMotionThreshold = holdMotionThreshold;
        _transitionMotionThreshold = transitionMotionThreshold;
        _minAtomDuration = minAtomDuration ?? TimeSpan.FromMilliseconds(100);
        _minHoldDuration = minHoldDuration ?? TimeSpan.FromMilliseconds(200);
    }
    
    public async IAsyncEnumerable<SignAtom> SegmentStreamAsync(
        IAsyncEnumerable<FrameLandmarks> frames,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var frameBuffer = new List<FrameLandmarks>();
        var motionBuffer = new List<float>();
        
        await foreach (var frame in frames.WithCancellation(cancellationToken))
        {
            if (!frame.HasAnyHand)
                continue;
            
            frameBuffer.Add(frame);
            
            if (frameBuffer.Count < 2)
                continue;
            
            var motion = CalculateMotion(frameBuffer[^2], frameBuffer[^1]);
            motionBuffer.Add(motion);
            
            if (ShouldSegment(frameBuffer, motionBuffer))
            {
                var atom = CreateAtomFromBuffer(frameBuffer);
                if (atom.Duration >= _minAtomDuration)
                {
                    yield return atom;
                }
                
                frameBuffer.Clear();
                motionBuffer.Clear();
            }
        }
        
        if (frameBuffer.Count > 0)
        {
            var duration = frameBuffer[^1].Timestamp - frameBuffer[0].Timestamp;
            if (duration >= _minAtomDuration)
            {
                yield return CreateAtomFromBuffer(frameBuffer);
            }
        }
    }
    
    private float CalculateMotion(FrameLandmarks prev, FrameLandmarks current)
    {
        if (prev.LeftHand == null || current.LeftHand == null)
            return 0f;
        
        var totalMotion = 0f;
        var count = 0;
        
        var prevPoints = prev.LeftHand.AsSpan();
        var currPoints = current.LeftHand.AsSpan();
        
        for (int i = 0; i < prevPoints.Length; i++)
        {
            totalMotion += prevPoints[i].DistanceTo(currPoints[i]);
            count++;
        }
        
        return count > 0 ? totalMotion / count : 0f;
    }
    
    private bool ShouldSegment(List<FrameLandmarks> frameBuffer, List<float> motionBuffer)
    {
        if (frameBuffer.Count < 5)
            return false;
        
        var avgMotion = motionBuffer.Average();
        var recentMotion = motionBuffer.TakeLast(3).Average();
        
        if (avgMotion < _holdMotionThreshold)
        {
            return frameBuffer[^1].Timestamp - frameBuffer[0].Timestamp >= _minHoldDuration;
        }
        
        if (recentMotion > _transitionMotionThreshold && avgMotion < _holdMotionThreshold)
        {
            return true;
        }
        
        return false;
    }
    
    private SignAtom CreateAtomFromBuffer(List<FrameLandmarks> frameBuffer)
    {
        if (frameBuffer.Count == 0)
            throw new InvalidOperationException("Cannot create atom from empty buffer");
        
        var avgMotion = 0f;
        if (frameBuffer.Count > 1)
        {
            for (int i = 1; i < frameBuffer.Count; i++)
            {
                avgMotion += CalculateMotion(frameBuffer[i - 1], frameBuffer[i]);
            }
            avgMotion /= (frameBuffer.Count - 1);
        }
        
        var atomType = avgMotion < _holdMotionThreshold ? AtomType.Hold : AtomType.Transition;
        
        var atom = new SignAtom(
            atomType,
            frameBuffer[0].Timestamp,
            frameBuffer[^1].Timestamp);
        
        foreach (var frame in frameBuffer)
        {
            atom.Frames.Add(frame);
        }
        
        return atom;
    }
}