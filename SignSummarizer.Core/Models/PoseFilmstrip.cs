namespace SignSummarizer.Models;

public sealed class PoseFilmstrip
{
    public Guid SignAtomId { get; }
    public List<KeyPose> KeyPoses { get; } = new();
    public TimeSpan StartTime { get; }
    public TimeSpan EndTime { get; }
    public int TotalFrames { get; }
    
    public PoseFilmstrip(Guid signAtomId, TimeSpan startTime, TimeSpan endTime, int totalFrames)
    {
        SignAtomId = signAtomId;
        StartTime = startTime;
        EndTime = endTime;
        TotalFrames = totalFrames;
    }
    
    public void AddKeyPose(KeyPose keyPose)
    {
        KeyPoses.Add(keyPose);
    }
    
    public float[] FlattenToVector()
    {
        if (KeyPoses.Count == 0) return Array.Empty<float>();
        
        var dimensions = 21 * 3;
        var vector = new float[KeyPoses.Count * dimensions];
        
        for (int i = 0; i < KeyPoses.Count; i++)
        {
            var pose = KeyPoses[i];
            var offset = i * dimensions;
            
            for (int j = 0; j < pose.Points.Length; j++)
            {
                vector[offset + j * 3] = pose.Points[j].X;
                vector[offset + j * 3 + 1] = pose.Points[j].Y;
                vector[offset + j * 3 + 2] = pose.Points[j].Z;
            }
        }
        
        return vector;
    }
}

public sealed record KeyPose(
    int FrameIndex,
    TimeSpan Timestamp,
    Point3D[] Points,
    float NoveltyScore
);