namespace Mostlylucid.DocSummarizer.Services.Utilities;

/// <summary>
/// Shared utilities for cleaning LLM responses.
/// Removes preambles, meta-commentary, and other LLM-isms that leak into outputs.
/// </summary>
public static class ResponseCleaner
{
    /// <summary>
    /// Preamble patterns that appear at the start of LLM responses.
    /// These are typical "acknowledgment" phrases that should be stripped.
    /// </summary>
    private static readonly string[] PreamblePatterns =
    {
        "here is",
        "here are",
        "here's",
        "below is",
        "the following",
        "based on",
        "i'll",
        "let me",
        "sure",
        "certainly",
        "of course",
        "i'd be happy",
        "i would be happy",
        "absolutely",
        "as requested",
        "as you requested",
        "in response to",
        "here you go",
        "please find",
        "i have",
        "i've"
    };

    /// <summary>
    /// Fact-check preamble patterns (more specific to correction tasks).
    /// </summary>
    private static readonly string[] FactCheckPreamblePatterns =
    {
        "after comparing",
        "here is the corrected",
        "here's the corrected",
        "the corrected summary",
        "i found",
        "i corrected",
        "comparing the summary",
        "here is the revised",
        "here's the revised",
        "below is the corrected",
        "the following is the corrected",
        "i have corrected",
        "i've corrected",
        "a few contradictions",
        "few contradictions",
        "some contradictions",
        "no contradictions",
        "corrected version",
        "revised version",
        "updated summary",
        "the summary with corrections"
    };

    /// <summary>
    /// Trailing patterns that indicate meta-commentary at the end.
    /// </summary>
    private static readonly string[] TrailingPatterns =
    {
        "i corrected the following",
        "corrections made:",
        "changes made:",
        "* removed",
        "* changed",
        "* corrected",
        "- removed",
        "- changed",
        "- corrected",
        "note:",
        "notes:",
        "key corrections:",
        "the changes i made",
        "changes include:",
        "let me know if",
        "feel free to",
        "hope this helps",
        "is there anything else"
    };

    /// <summary>
    /// Remove common LLM preamble patterns from synthesis responses.
    /// </summary>
    /// <param name="response">Raw LLM response</param>
    /// <returns>Cleaned response with preamble stripped</returns>
    public static string CleanSynthesisResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return response;

        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var cleaned = new List<string>();
        var foundContent = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var lower = trimmed.ToLowerInvariant();

            // Skip common preamble patterns at the start
            if (!foundContent)
            {
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                var isPreamble = PreamblePatterns.Any(p => lower.StartsWith(p));
                if (isPreamble)
                    continue;

                foundContent = true;
            }

            cleaned.Add(line);
        }

        // If nothing left, return original (might have been all content)
        if (cleaned.Count == 0)
            return response.Trim();

        return string.Join("\n", cleaned).Trim();
    }

    /// <summary>
    /// Clean fact-check response to remove meta-commentary that LLMs tend to add.
    /// More aggressive than CleanSynthesisResponse - removes both preambles and trailing notes.
    /// </summary>
    /// <param name="response">Raw fact-check response</param>
    /// <returns>Cleaned response with meta-commentary stripped</returns>
    public static string CleanFactCheckResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return response;

        var lines = response.Split('\n').ToList();
        var cleaned = new List<string>();
        var inContent = false;
        var skipRest = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var lower = trimmed.ToLowerInvariant();

            // Skip preamble lines - keep scanning until we hit real content
            if (!inContent)
            {
                // Skip blank lines in preamble
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                // Check if this line contains any preamble pattern
                var isPreamble = FactCheckPreamblePatterns.Any(p => lower.Contains(p));
                if (isPreamble)
                    continue;

                // We've hit real content - this line and following are content
                inContent = true;
            }

            // Skip trailing meta-commentary
            if (inContent && !skipRest)
            {
                var isTrailing = TrailingPatterns.Any(p => lower.StartsWith(p));
                if (isTrailing)
                {
                    skipRest = true;
                    continue;
                }
            }

            if (!skipRest)
                cleaned.Add(line);
        }

        return string.Join("\n", cleaned).Trim();
    }

    /// <summary>
    /// Clean a response that should contain a simple list or enumeration.
    /// Removes numbering inconsistencies and normalizes format.
    /// </summary>
    public static string CleanListResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return response;

        // First clean preambles
        var cleaned = CleanSynthesisResponse(response);

        // Split into lines and clean up list formatting
        var lines = cleaned.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Strip markdown headers from response if they were added by the LLM.
    /// Useful when you asked for plain text but got markdown.
    /// </summary>
    public static string StripMarkdownHeaders(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return response;

        var lines = response.Split('\n');
        var cleaned = lines
            .Select(l =>
            {
                var trimmed = l.TrimStart();
                // Remove # headers
                if (trimmed.StartsWith('#'))
                {
                    var afterHash = trimmed.TrimStart('#').Trim();
                    return afterHash;
                }
                return l;
            });

        return string.Join("\n", cleaned);
    }

    /// <summary>
    /// Remove code block markers from response.
    /// </summary>
    public static string StripCodeBlocks(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return response;

        var lines = response.Split('\n')
            .Where(l => !l.Trim().StartsWith("```"));

        return string.Join("\n", lines).Trim();
    }

    /// <summary>
    /// Normalize whitespace in response (collapse multiple newlines, trim).
    /// </summary>
    public static string NormalizeWhitespace(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return response;

        // Collapse multiple newlines to max 2
        var normalized = System.Text.RegularExpressions.Regex.Replace(
            response, @"\n{3,}", "\n\n");

        // Trim each line
        var lines = normalized.Split('\n')
            .Select(l => l.TrimEnd());

        return string.Join("\n", lines).Trim();
    }

    /// <summary>
    /// Full cleaning pipeline: remove preamble, trailing notes, normalize whitespace.
    /// </summary>
    public static string CleanFull(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return response;

        var cleaned = CleanSynthesisResponse(response);
        cleaned = CleanFactCheckResponse(cleaned);
        cleaned = NormalizeWhitespace(cleaned);
        return cleaned;
    }
}
