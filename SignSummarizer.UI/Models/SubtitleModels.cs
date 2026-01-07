namespace SignSummarizer.UI.Models;

public record SubtitleSegment(TimeSpan Start, TimeSpan End, string Text, float Confidence = 1.0f)
{
    public TimeSpan Duration => End - Start;
}

public record DiarizedSegment(TimeSpan Start, TimeSpan End, string Text, string Speaker, float Confidence = 1.0f);