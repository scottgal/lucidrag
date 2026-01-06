using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.Ephemeral;

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
        ["text"] = new[] { "content.text*", "content.extracted*", "ocr.*", "vision.llm.text", "florence2.ocr_text" },
        ["quality"] = new[] { "quality.*" },
        ["identity"] = new[] { "identity.*" },
        ["vision"] = new[] { "vision.*", "florence2.*" },
        ["faces"] = new[] { "face.*", "vision.llm.entity.person" },
        ["all"] = new[] { "*" },
        // Use case specific collections
        ["alttext"] = new[] { "vision.llm.caption", "florence2.caption", "vision.llm.text", "ocr.*", "content.extracted_text", "motion.summary", "identity.format", "identity.is_animated" },
        ["tool"] = new[] { "identity.*", "color.dominant*", "motion.*", "vision.llm.*", "florence2.*", "ocr.voting.*" },
        ["caption"] = new[] { "vision.llm.caption", "florence2.caption" }
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
    /// Uses Ephemeral's StringPatternMatcher for consistent glob matching.
    /// </summary>
    public static bool Matches(string signalKey, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (StringPatternMatcher.Matches(signalKey, pattern))
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
    /// Uses WaveRegistry to trace dependencies (e.g., motion.* needs identity.is_animated).
    /// </summary>
    public static HashSet<string> GetRequiredWaveTags(string? globString)
    {
        var patterns = ParseGlobs(globString);

        // Check for match-all
        if (patterns.Contains("*"))
            return new HashSet<string> { "*" };

        // Use WaveRegistry to find required waves with dependency tracing
        var requiredWaves = Analysis.WaveRegistry.GetRequiredWaves(patterns);
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var wave in requiredWaves)
        {
            foreach (var tag in wave.Tags)
            {
                tags.Add(tag);
            }
        }

        // Fallback: if WaveRegistry didn't find anything, use prefix mapping
        if (tags.Count == 0)
        {
            foreach (var pattern in patterns)
            {
                var prefix = pattern.Split('.')[0].TrimEnd('*');
                switch (prefix.ToLowerInvariant())
                {
                    case "motion":
                    case "complexity":
                        tags.Add("motion");
                        tags.Add("identity"); // MotionWave requires identity.is_animated
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
                }
            }
        }

        return tags;
    }

    /// <summary>
    /// Get available collection names for help display.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> GetCollections() => SignalCollections;
}
