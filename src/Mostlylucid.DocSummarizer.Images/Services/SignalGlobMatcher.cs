using System.Text.RegularExpressions;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;

namespace Mostlylucid.DocSummarizer.Images.Services;

/// <summary>
/// Matches signals using glob patterns. Supports:
/// - Wildcards: motion.* matches motion.direction, motion.magnitude
/// - Partial: color.dominant* matches color.dominant_rgb, color.dominant_name
/// - Collections: @motion expands to predefined signal groups
/// </summary>
public static class SignalGlobMatcher
{
    /// <summary>
    /// Predefined signal collections for common use cases.
    /// Use @collection_name to expand.
    /// </summary>
    private static readonly Dictionary<string, string[]> SignalCollections = new()
    {
        ["motion"] = new[] { "motion.*", "complexity.*" },
        ["color"] = new[] { "color.*" },
        ["text"] = new[] { "content.text*", "content.extracted*", "ocr.*", "vision.llm.text" },
        ["quality"] = new[] { "quality.*" },
        ["identity"] = new[] { "identity.*" },
        ["vision"] = new[] { "vision.*" },
        ["faces"] = new[] { "face.*", "vision.llm.entity.person" },
        ["all"] = new[] { "*" },
        // Use case specific collections
        ["alttext"] = new[] { "vision.llm.caption", "content.text*", "motion.summary", "identity.format" },
        ["tool"] = new[] { "identity.*", "color.dominant*", "motion.*", "vision.llm.*", "ocr.voting.*" }
    };

    /// <summary>
    /// Parse a comma-separated glob pattern string into expanded patterns.
    /// Handles @collection expansion.
    /// </summary>
    public static List<string> ParseGlobs(string? globString)
    {
        if (string.IsNullOrWhiteSpace(globString))
            return new List<string> { "*" }; // Default: match all

        var patterns = new List<string>();
        var parts = globString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (part.StartsWith('@'))
            {
                // Collection reference
                var collectionName = part[1..].ToLowerInvariant();
                if (SignalCollections.TryGetValue(collectionName, out var collection))
                {
                    patterns.AddRange(collection);
                }
            }
            else
            {
                patterns.Add(part);
            }
        }

        return patterns.Distinct().ToList();
    }

    /// <summary>
    /// Check if a signal key matches any of the glob patterns.
    /// </summary>
    public static bool Matches(string signalKey, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (GlobMatch(signalKey, pattern))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Filter signals from a profile based on glob patterns.
    /// </summary>
    public static IEnumerable<Signal> FilterSignals(DynamicImageProfile profile, string? globString)
    {
        var patterns = ParseGlobs(globString);
        return profile.GetAllSignals().Where(s => Matches(s.Key, patterns));
    }

    /// <summary>
    /// Get the wave tags that would produce signals matching the globs.
    /// Used to determine which waves to skip.
    /// </summary>
    public static HashSet<string> GetRequiredWaveTags(string? globString)
    {
        var patterns = ParseGlobs(globString);
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in patterns)
        {
            // Map signal prefixes to wave tags
            var prefix = pattern.Split('.')[0].TrimEnd('*');

            switch (prefix.ToLowerInvariant())
            {
                case "motion":
                case "complexity":
                    tags.Add("motion");
                    break;
                case "color":
                    tags.Add("color");
                    break;
                case "ocr":
                case "content":
                    tags.Add("ocr");
                    tags.Add("content");
                    break;
                case "quality":
                    tags.Add("quality");
                    break;
                case "identity":
                    tags.Add("identity");
                    break;
                case "vision":
                    tags.Add("vision");
                    tags.Add("llm");
                    break;
                case "face":
                    tags.Add("face");
                    break;
                case "clip":
                    tags.Add("clip");
                    tags.Add("embedding");
                    break;
                case "*":
                    // Match all - need all waves
                    return new HashSet<string> { "*" };
            }
        }

        return tags;
    }

    /// <summary>
    /// Simple glob pattern matching.
    /// Supports * (match any) at end or as segment wildcard.
    /// </summary>
    private static bool GlobMatch(string input, string pattern)
    {
        // Exact match
        if (pattern == input)
            return true;

        // Match all
        if (pattern == "*")
            return true;

        // Ends with * (prefix match)
        if (pattern.EndsWith('*'))
        {
            var prefix = pattern[..^1];
            return input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // Contains .* (segment wildcard)
        if (pattern.Contains(".*"))
        {
            var regexPattern = "^" + Regex.Escape(pattern).Replace(@"\.\*", @"\.[^.]+") + "$";
            return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Get available collection names for help display.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> GetCollections() => SignalCollections;
}
