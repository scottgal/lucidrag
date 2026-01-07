using Microsoft.Extensions.Logging;
using SignSummarizer.Models;

namespace SignSummarizer.Services;

public interface IPoseEmbeddingService
{
    float[] CreateEmbedding(SignAtom atom);
    float[] CreateEmbedding(CanonicalLandmarks[] landmarks);
    float[] CreateEmbedding(FrameLandmarks[] frames);
}

public sealed class PoseEmbeddingService : IPoseEmbeddingService
{
    private readonly ILogger<PoseEmbeddingService> _logger;
    private readonly int _embeddingDimensions;
    
    public PoseEmbeddingService(
        ILogger<PoseEmbeddingService> logger,
        int embeddingDimensions = 128)
    {
        _logger = logger;
        _embeddingDimensions = embeddingDimensions;
    }
    
    public float[] CreateEmbedding(SignAtom atom)
    {
        if (atom.Frames.Count == 0)
            return new float[_embeddingDimensions];
        
        var landmarks = atom.Frames
            .Where(f => f.HasAnyHand)
            .Select(f => f.LeftHand ?? f.RightHand)
            .Where(h => h != null)
            .Select(h => CanonicalLandmarks.FromLandmarks(h!))
            .ToArray();
        
        return CreateEmbedding(landmarks);
    }
    
    public float[] CreateEmbedding(CanonicalLandmarks[] landmarks)
    {
        if (landmarks.Length == 0)
            return new float[_embeddingDimensions];
        
        var embedding = new float[_embeddingDimensions];
        
        var landmarkCount = Math.Min(landmarks.Length, 10);
        var dimensionsPerLandmark = _embeddingDimensions / landmarkCount;
        
        for (int i = 0; i < landmarkCount; i++)
        {
            var landmarksSpan = landmarks[i].AsSpan();
            var offset = i * dimensionsPerLandmark;
            
            for (int j = 0; j < Math.Min(landmarksSpan.Length, dimensionsPerLandmark / 3); j++)
            {
                var pt = landmarksSpan[j];
                embedding[offset + j * 3] = pt.X;
                embedding[offset + j * 3 + 1] = pt.Y;
                embedding[offset + j * 3 + 2] = pt.Z;
            }
        }
        
        return embedding;
    }
    
    public float[] CreateEmbedding(FrameLandmarks[] frames)
    {
        var canonical = frames
            .Where(f => f.HasAnyHand)
            .Select(f => f.LeftHand ?? f.RightHand)
            .Where(h => h != null)
            .Select(h => CanonicalLandmarks.FromLandmarks(h!))
            .ToArray();
        
        return CreateEmbedding(canonical);
    }
}