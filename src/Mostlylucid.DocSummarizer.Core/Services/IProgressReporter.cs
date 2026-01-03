namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
///     Interface for reporting progress during document processing
/// </summary>
public interface IProgressReporter
{
    /// <summary>
    ///     Report a stage change during processing
    /// </summary>
    void ReportStage(string stage, float progress = 0);

    /// <summary>
    ///     Report LLM activity (e.g., "Summarizing chunk 3/10")
    /// </summary>
    void ReportLlmActivity(string activity);

    /// <summary>
    ///     Report a log message
    /// </summary>
    void ReportLog(string message, LogLevel level = LogLevel.Info);

    /// <summary>
    ///     Report chunk processing progress
    /// </summary>
    void ReportChunkProgress(int completed, int total);
}

public enum LogLevel
{
    Info,
    Warning,
    Error,
    Success
}

/// <summary>
///     Null progress reporter that does nothing (for non-TUI mode)
/// </summary>
public class NullProgressReporter : IProgressReporter
{
    public static readonly NullProgressReporter Instance = new();

    public void ReportStage(string stage, float progress = 0)
    {
    }

    public void ReportLlmActivity(string activity)
    {
    }

    public void ReportLog(string message, LogLevel level = LogLevel.Info)
    {
    }

    public void ReportChunkProgress(int completed, int total)
    {
    }
}

/// <summary>
///     Console-based progress reporter for non-TUI mode
/// </summary>
public class ConsoleProgressReporter : IProgressReporter
{
    public void ReportStage(string stage, float progress = 0)
    {
        Console.WriteLine($"[Stage] {stage} ({progress:P0})");
    }

    public void ReportLlmActivity(string activity)
    {
        Console.WriteLine($"[LLM] {activity}");
    }

    public void ReportLog(string message, LogLevel level = LogLevel.Info)
    {
        var prefix = level switch
        {
            LogLevel.Error => "[ERROR]",
            LogLevel.Warning => "[WARN]",
            LogLevel.Success => "[OK]",
            _ => "[INFO]"
        };
        Console.WriteLine($"{prefix} {message}");
    }

    public void ReportChunkProgress(int completed, int total)
    {
        Console.WriteLine($"[Progress] {completed}/{total} chunks");
    }
}