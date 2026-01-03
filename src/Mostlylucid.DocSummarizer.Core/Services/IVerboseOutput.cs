namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Interface for verbose output during processing.
/// Allows CLI to use Spectre.Console while library uses simple console output.
/// </summary>
public interface IVerboseOutput
{
    /// <summary>
    /// Write a verbose message. Only called when verbose mode is enabled.
    /// </summary>
    /// <param name="message">The message to write. May contain markup syntax that the implementation can interpret.</param>
    void Write(string message);
    
    /// <summary>
    /// Write a verbose message with a specific style.
    /// </summary>
    /// <param name="message">The message to write.</param>
    /// <param name="style">The style hint (e.g., "cyan", "dim", "green", "bold").</param>
    void Write(string message, string style);
}

/// <summary>
/// Simple console implementation of verbose output.
/// Strips markup syntax and writes to Console.
/// </summary>
public class ConsoleVerboseOutput : IVerboseOutput
{
    /// <summary>
    /// Singleton instance for convenience.
    /// </summary>
    public static readonly ConsoleVerboseOutput Instance = new();
    
    /// <inheritdoc />
    public void Write(string message)
    {
        // Strip basic Spectre markup like [bold cyan]...[/]
        var clean = StripMarkup(message);
        Console.WriteLine(clean);
    }
    
    /// <inheritdoc />
    public void Write(string message, string style)
    {
        // Ignore style, just write the message
        Console.WriteLine(StripMarkup(message));
    }
    
    private static string StripMarkup(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        
        // Remove Spectre markup tags like [bold], [cyan], [/], [dim], etc.
        var result = text;
        
        // Remove opening tags like [bold cyan], [dim], [green], etc.
        while (true)
        {
            var start = result.IndexOf('[');
            if (start < 0) break;
            
            var end = result.IndexOf(']', start);
            if (end < 0) break;
            
            // Check if this is a closing tag [/] or an opening tag [something]
            result = result.Remove(start, end - start + 1);
        }
        
        return result;
    }
}

/// <summary>
/// Null implementation that discards all output.
/// </summary>
public class NullVerboseOutput : IVerboseOutput
{
    /// <summary>
    /// Singleton instance.
    /// </summary>
    public static readonly NullVerboseOutput Instance = new();
    
    /// <inheritdoc />
    public void Write(string message) { }
    
    /// <inheritdoc />
    public void Write(string message, string style) { }
}
