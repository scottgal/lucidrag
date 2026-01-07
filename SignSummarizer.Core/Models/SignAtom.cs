namespace SignSummarizer.Models;

public enum AtomType
{
    Hold,
    Transition,
    Boundary
}

public record SignAtom
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public AtomType Type { get; init; }
    public TimeSpan StartTime { get; init; }
    public TimeSpan EndTime { get; init; }
    public TimeSpan Duration => EndTime - StartTime;
    
    public List<FrameLandmarks> Frames { get; init; } = new();
    public NonManualModifiers? Modifiers { get; private set; }
    public float[]? PoseEmbedding { get; set; }
    
    public List<string>? GlossCandidates { get; set; }
    public float? Confidence { get; set; }
    
    public List<int> KeyFrameIndices { get; } = new();
    
    public SignAtom(AtomType type, TimeSpan startTime, TimeSpan endTime)
    {
        Type = type;
        StartTime = startTime;
        EndTime = endTime;
    }
    
    public void AddModifiers(NonManualModifiers modifiers)
    {
        Modifiers = modifiers;
    }
    
    public void AddKeyFrame(int frameIndex)
    {
        if (!KeyFrameIndices.Contains(frameIndex))
            KeyFrameIndices.Add(frameIndex);
    }
}
