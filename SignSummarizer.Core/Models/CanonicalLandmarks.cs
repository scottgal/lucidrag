namespace SignSummarizer.Models;

public sealed class CanonicalLandmarks
{
    public HandSide Side { get; }
    public Point3D[] NormalizedPoints { get; }
    public int FrameIndex { get; }
    public TimeSpan Timestamp { get; }
    public float Scale { get; }
    public float Rotation { get; }
    
    public CanonicalLandmarks(
        HandSide side,
        Point3D[] normalizedPoints,
        int frameIndex,
        TimeSpan timestamp,
        float scale,
        float rotation)
    {
        Side = side;
        NormalizedPoints = normalizedPoints;
        FrameIndex = frameIndex;
        Timestamp = timestamp;
        Scale = scale;
        Rotation = rotation;
    }
    
    public ReadOnlySpan<Point3D> AsSpan() => NormalizedPoints.AsSpan();
    
    public static CanonicalLandmarks FromLandmarks(HandLandmarks landmarks)
    {
        var points = landmarks.AsSpan().ToArray();
        
        var wrist = landmarks.Wrist;
        
        var middleMcp = points[9];
        var palmSize = wrist.DistanceTo(middleMcp);
        var scale = palmSize > 0 ? 1.0f / palmSize : 1.0f;
        
        var indexMcp = points[5];
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
        
        return new CanonicalLandmarks(
            landmarks.Side,
            normalized,
            landmarks.FrameIndex,
            landmarks.Timestamp,
            scale,
            rotation);
    }
}