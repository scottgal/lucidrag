namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Helper class for verbose output that replaces Spectre.Console dependency.
/// </summary>
internal static class VerboseHelper
{
    /// <summary>
    /// Write a verbose log message if verbose mode is enabled.
    /// Messages go to stderr to avoid polluting stdout (which is reserved for JSON output).
    /// </summary>
    /// <param name="verbose">Whether verbose mode is enabled.</param>
    /// <param name="message">The message to write.</param>
    public static void Log(bool verbose, string message)
    {
        if (verbose)
        {
            Console.Error.WriteLine(StripMarkup(message));
        }
    }

    /// <summary>
    /// Write a verbose log message unconditionally.
    /// Messages go to stderr to avoid polluting stdout (which is reserved for JSON output).
    /// </summary>
    /// <param name="message">The message to write.</param>
    public static void Log(string message)
    {
        Console.Error.WriteLine(StripMarkup(message));
    }

    /// <summary>
    /// Escape text for safe display (no-op in console mode, would escape markup in Spectre).
    /// </summary>
    /// <param name="text">The text to escape.</param>
    /// <returns>The text unchanged.</returns>
    public static string Escape(string? text) => text ?? "";

    /// <summary>
    /// Strip Spectre markup tags from a string.
    /// Converts "[bold cyan]text[/]" to "text".
    /// </summary>
    private static string StripMarkup(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var result = text;
        var pos = 0;

        while (pos < result.Length)
        {
            var start = result.IndexOf('[', pos);
            if (start < 0) break;

            var end = result.IndexOf(']', start);
            if (end < 0) break;

            // Remove the tag
            result = result.Remove(start, end - start + 1);
            // Don't advance pos since we removed characters
        }

        return result;
    }
}
