using Microsoft.Extensions.Logging;
using SignSummarizer.Models;

namespace SignSummarizer.Services;

public interface ICanonicalizationService
{
    CanonicalLandmarks Canonicalize(HandLandmarks landmarks, bool mirrorToDominant = false);
    CanonicalLandmarks[] CanonicalizeSequence(HandLandmarks[] landmarks, bool mirrorToDominant = false);
}

public sealed class CanonicalizationService : ICanonicalizationService
{
    private readonly ILogger<CanonicalizationService> _logger;
    private readonly HandSide _dominantHand;
    
    public CanonicalizationService(
        ILogger<CanonicalizationService> logger,
        HandSide dominantHand = HandSide.Right)
    {
        _logger = logger;
        _dominantHand = dominantHand;
    }
    
    public CanonicalLandmarks Canonicalize(HandLandmarks landmarks, bool mirrorToDominant = false)
    {
        var points = landmarks.AsSpan().ToArray();
        var wrist = landmarks.Wrist;
        
        var middleMcp = points[9];
        var indexMcp = points[5];
        var palmSize = wrist.DistanceTo(middleMcp);
        
        var scale = palmSize > 0 ? 1.0f / palmSize : 1.0f;
        
        var dx = indexMcp.X - wrist.X;
        var dy = indexMcp.Y - wrist.Y;
        var rotation = MathF.Atan2(dy, dx);
        
        var normalized = new Point3D[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            var p = points[i];
            
            var translatedX = p.X - wrist.X;
            var translatedY = p.Y - wrist.Y;
            var translatedZ = p.Z - wrist.Z;
            
            var rotatedX = translatedX * MathF.Cos(-rotation) - translatedY * MathF.Sin(-rotation);
            var rotatedY = translatedX * MathF.Sin(-rotation) + translatedY * MathF.Cos(-rotation);
            
            normalized[i] = new Point3D(
                rotatedX * scale,
                rotatedY * scale,
                translatedZ * scale
            );
        }
        
        if (mirrorToDominant && landmarks.Side != _dominantHand && landmarks.Side != HandSide.Unknown)
        {
            for (int i = 0; i < normalized.Length; i++)
            {
                normalized[i] = normalized[i] with { X = -normalized[i].X };
            }
        }
        
        return new CanonicalLandmarks(
            landmarks.Side,
            normalized,
            landmarks.FrameIndex,
            landmarks.Timestamp,
            scale,
            rotation);
    }
    
    public CanonicalLandmarks[] CanonicalizeSequence(HandLandmarks[] landmarks, bool mirrorToDominant = false)
    {
        var result = new CanonicalLandmarks[landmarks.Length];
        
        for (int i = 0; i < landmarks.Length; i++)
        {
            result[i] = Canonicalize(landmarks[i], mirrorToDominant);
        }
        
        return result;
    }
}