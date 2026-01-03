namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Simple console-based progress service that works reliably in all terminals.
/// No ANSI escape codes, no fancy rendering - just clear text output.
/// </summary>
public class SimpleProgressService : IProgressReporter
{
    private readonly bool _verbose;
    private string _currentStage = "";
    private int _lastPercentReported = -1;

    public SimpleProgressService(bool verbose = true)
    {
        _verbose = verbose;
    }

    public void ReportStage(string stage, float progress = 0)
    {
        if (!_verbose) return;
        
        _currentStage = stage;
        var percent = (int)(progress * 100);
        
        // Only report if percentage changed significantly (every 10%)
        if (percent / 10 != _lastPercentReported / 10 || percent == 0 || percent == 100)
        {
            _lastPercentReported = percent;
            if (percent > 0)
                Console.WriteLine($"  [{percent,3}%] {stage}");
            else
                Console.WriteLine($"  {stage}");
        }
    }

    public void ReportLlmActivity(string activity)
    {
        if (!_verbose) return;
        Console.WriteLine($"  [LLM] {activity}");
    }

    public void ReportChunkProgress(int completed, int total)
    {
        if (!_verbose) return;
        
        var percent = total > 0 ? (completed * 100) / total : 0;
        
        // Only report on significant changes
        if (percent / 10 != _lastPercentReported / 10 || completed == total)
        {
            _lastPercentReported = percent;
            Console.WriteLine($"  [Chunks] {completed}/{total} ({percent}%)");
        }
    }

    public void ReportLog(string message, LogLevel level = LogLevel.Info)
    {
        if (!_verbose && level == LogLevel.Info) return;
        
        var prefix = level switch
        {
            LogLevel.Success => "[OK]",
            LogLevel.Warning => "[WARN]",
            LogLevel.Error => "[ERROR]",
            _ => "[INFO]"
        };
        
        Console.WriteLine($"  {prefix} {message}");
    }

    /// <summary>
    /// Display a header for a new operation
    /// </summary>
    public static void WriteHeader(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"  {title}");
        Console.WriteLine(new string('=', 60));
    }

    /// <summary>
    /// Display info about the current operation
    /// </summary>
    public static void WriteInfo(string label, string value)
    {
        Console.WriteLine($"  {label}: {value}");
    }

    /// <summary>
    /// Display a section divider
    /// </summary>
    public static void WriteDivider(string? title = null)
    {
        Console.WriteLine();
        if (title != null)
            Console.WriteLine($"--- {title} ---");
        else
            Console.WriteLine(new string('-', 40));
    }

    /// <summary>
    /// Display success message
    /// </summary>
    public static void WriteSuccess(string message)
    {
        Console.WriteLine($"[OK] {message}");
    }

    /// <summary>
    /// Display error message
    /// </summary>
    public static void WriteError(string message)
    {
        Console.WriteLine($"[ERROR] {message}");
    }

    /// <summary>
    /// Display warning message
    /// </summary>
    public static void WriteWarning(string message)
    {
        Console.WriteLine($"[WARN] {message}");
    }

    /// <summary>
    /// Simple progress bar using ASCII characters
    /// </summary>
    public static void WriteProgressBar(int current, int total, string? label = null)
    {
        var percent = total > 0 ? (current * 100) / total : 0;
        var filled = percent / 5; // 20 char bar
        var bar = new string('#', filled) + new string('-', 20 - filled);
        
        var text = label != null 
            ? $"  [{bar}] {percent,3}% ({current}/{total}) {label}"
            : $"  [{bar}] {percent,3}% ({current}/{total})";
            
        // Use carriage return to update in place (works in most terminals)
        Console.Write($"\r{text}");
        
        if (current >= total)
            Console.WriteLine(); // New line when complete
    }
}
