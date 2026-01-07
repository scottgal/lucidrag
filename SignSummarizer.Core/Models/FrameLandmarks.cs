namespace SignSummarizer.Models;

public sealed class FrameLandmarks
{
    public int FrameIndex { get; }
    public TimeSpan Timestamp { get; }
    public HandLandmarks? LeftHand { get; }
    public HandLandmarks? RightHand { get; }
    public NonManualModifiers? Modifiers { get; }
    
    public FrameLandmarks(
        int frameIndex,
        TimeSpan timestamp,
        HandLandmarks? leftHand = null,
        HandLandmarks? rightHand = null,
        NonManualModifiers? modifiers = null)
    {
        FrameIndex = frameIndex;
        Timestamp = timestamp;
        LeftHand = leftHand;
        RightHand = rightHand;
        Modifiers = modifiers;
    }
    
    public bool HasLeftHand => LeftHand != null && LeftHand.Confidence > 0.5f;
    public bool HasRightHand => RightHand != null && RightHand.Confidence > 0.5f;
    public bool HasAnyHand => HasLeftHand || HasRightHand;
}