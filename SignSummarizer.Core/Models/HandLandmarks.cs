namespace SignSummarizer.Models;

public enum HandSide
{
    Unknown,
    Left,
    Right
}

public sealed class HandLandmarks
{
    private const int KeyPointCount = 21;
    private readonly Point3D[] _points;
    
    public HandSide Side { get; }
    public float Confidence { get; }
    public int FrameIndex { get; }
    public TimeSpan Timestamp { get; }
    
    public HandLandmarks(Point3D[] points, HandSide side, float confidence, int frameIndex, TimeSpan timestamp)
    {
        if (points.Length != KeyPointCount)
            throw new ArgumentException($"Expected {KeyPointCount} hand landmarks, got {points.Length}");
        
        _points = points;
        Side = side;
        Confidence = confidence;
        FrameIndex = frameIndex;
        Timestamp = timestamp;
    }
    
    public Point3D this[int index] => _points[index];
    
    public ReadOnlySpan<Point3D> AsSpan() => _points.AsSpan();
    
    public Point3D Wrist => _points[0];
    
    public Point3D ThumbTip => _points[4];
    public Point3D IndexTip => _points[8];
    public Point3D MiddleTip => _points[12];
    public Point3D RingTip => _points[16];
    public Point3D PinkyTip => _points[20];
    
    public float PalmSize => Wrist.DistanceTo(MiddleTip);
}