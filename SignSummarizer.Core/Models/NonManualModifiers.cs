namespace SignSummarizer.Models;

public enum BrowPosition
{
    Neutral,
    Raised,
    Furrowed
}

public enum HeadMotion
{
    None,
    Nod,
    Shake,
    Tilt
}

public enum MouthShape
{
    Unknown,
    Neutral,
    Open,
    Closed,
    Pursed
}

public sealed class NonManualModifiers
{
    public BrowPosition BrowPosition { get; init; } = BrowPosition.Neutral;
    public HeadMotion HeadMotion { get; init; } = HeadMotion.None;
    public MouthShape MouthShape { get; init; } = MouthShape.Unknown;
    public float TorsoShift { get; init; }
    public float Confidence { get; init; }
    public TimeSpan Timestamp { get; init; }
    
    public bool HasModifiers => 
        BrowPosition != BrowPosition.Neutral ||
        HeadMotion != HeadMotion.None ||
        MathF.Abs(TorsoShift) > 0.01f;
}