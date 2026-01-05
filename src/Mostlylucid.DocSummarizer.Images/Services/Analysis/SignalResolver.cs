using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Resolves signals from DynamicImageProfile using glob patterns
/// Enables swappable vision models and dynamic signal resolution
/// Supports salience-based ranking and context-aware signal selection
/// </summary>
public class SignalResolver
{
    /// <summary>
    /// Custom weights from configuration (takes priority over defaults)
    /// </summary>
    private static Dictionary<string, double>? _customWeights;
    private static double _defaultWeight = 5.0;

    /// <summary>
    /// Configure custom signal weights from ImageConfig
    /// Call this during application startup to override default weights
    /// </summary>
    public static void Configure(SignalImportanceConfig config)
    {
        _customWeights = config.CustomWeights;
        _defaultWeight = config.DefaultWeight;
    }

    /// <summary>
    /// Default signal importance weights for ranking
    /// Higher weight = more salient/important for LLM understanding
    /// </summary>
    private static readonly Dictionary<string, double> DefaultSignalImportance = new()
    {
        // Vision LLM signals (highest priority - natural language descriptions)
        ["vision.llm.caption"] = 10.0,
        ["vision.llm.detailed_description"] = 9.0,
        ["vision.llm.entities"] = 8.5,
        ["vision.llm.scene"] = 8.0,

        // Motion signals (high priority for animated images - describes dynamic content)
        ["motion.moving_objects"] = 9.0,
        ["motion.summary"] = 8.5,
        ["motion.type"] = 7.5,
        ["motion.direction"] = 7.0,

        // Text content (high priority - explicit content)
        ["ocr.voting.consensus_text"] = 9.5,
        ["ocr.corrected.text"] = 9.0,
        ["text.extracted"] = 8.0,

        // ML model outputs (medium-high priority)
        ["vision.ml.objects"] = 7.5,
        ["faces.embeddings"] = 7.0,
        ["vision.clip.embedding"] = 6.5,

        // Image characteristics (medium priority)
        ["color.dominant_colors"] = 6.0,
        ["identity.format"] = 5.5,
        ["identity.dimensions"] = 5.0,
        ["composition.complexity"] = 5.0,

        // Quality metrics (medium-low priority)
        ["quality.sharpness"] = 4.0,
        ["ocr.quality.spell_check_score"] = 4.0,

        // Technical details (low priority - can be dropped for small context windows)
        ["fingerprint.perceptual_hash"] = 3.0,
        ["performance.duration_ms"] = 2.0,
        ["metadata.exif"] = 2.0,
    };

    /// <summary>
    /// Get first signal value matching a glob pattern
    /// Supports wildcards: "vision.*.caption", "*.caption", "vision.llm.*"
    /// </summary>
    public static T? GetValueByPattern<T>(DynamicImageProfile profile, string pattern)
    {
        var matchingSignals = GetSignalsByPattern(profile, pattern);
        var signal = matchingSignals.FirstOrDefault();

        if (signal?.Value == null)
            return default;

        try
        {
            return (T)signal.Value;
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Get all signal values matching a glob pattern
    /// </summary>
    public static List<T> GetValuesByPattern<T>(DynamicImageProfile profile, string pattern)
    {
        var matchingSignals = GetSignalsByPattern(profile, pattern);
        var values = new List<T>();

        foreach (var signal in matchingSignals)
        {
            if (signal.Value is T typedValue)
            {
                values.Add(typedValue);
            }
        }

        return values;
    }

    /// <summary>
    /// Get all signals matching a glob pattern
    /// </summary>
    public static List<Signal> GetSignalsByPattern(DynamicImageProfile profile, string pattern)
    {
        var allSignals = profile.GetAllSignals().ToList();

        // Convert glob pattern to regex
        var regexPattern = GlobToRegex(pattern);

        return allSignals
            .Where(s => regexPattern.IsMatch(s.Key))
            .OrderByDescending(s => s.Confidence) // Highest confidence first
            .ToList();
    }

    /// <summary>
    /// Check if any signal matches the pattern
    /// </summary>
    public static bool HasSignalMatching(DynamicImageProfile profile, string pattern)
    {
        var allSignals = profile.GetAllSignals();
        var regexPattern = GlobToRegex(pattern);
        return allSignals.Any(s => regexPattern.IsMatch(s.Key));
    }

    /// <summary>
    /// Get signal value with fallback patterns
    /// Tries each pattern in order until a match is found
    /// Example: GetWithFallback&lt;string&gt;(profile, "vision.llm.caption", "vision.ml.caption", "visual.description")
    /// </summary>
    public static T? GetWithFallback<T>(DynamicImageProfile profile, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var value = GetValueByPattern<T>(profile, pattern);
            if (value != null)
            {
                return value;
            }
        }

        return default;
    }

    /// <summary>
    /// Get signals ranked by salience/importance
    /// Combines pattern matching with importance weighting
    /// </summary>
    public static List<Signal> GetSignalsBySalience(DynamicImageProfile profile, params string[] patterns)
    {
        var matchingSignals = new List<Signal>();

        foreach (var pattern in patterns)
        {
            matchingSignals.AddRange(GetSignalsByPattern(profile, pattern));
        }

        // Remove duplicates
        matchingSignals = matchingSignals
            .GroupBy(s => s.Key)
            .Select(g => g.First())
            .ToList();

        // Calculate salience score for each signal
        return matchingSignals
            .Select(s => new
            {
                Signal = s,
                Salience = CalculateSalience(s)
            })
            .OrderByDescending(x => x.Salience)
            .Select(x => x.Signal)
            .ToList();
    }

    /// <summary>
    /// Get signals that fit within a token budget
    /// Auto-truncates to most salient signals that fit the context window
    /// </summary>
    /// <param name="profile">Image profile to extract signals from</param>
    /// <param name="maxTokens">Maximum token budget</param>
    /// <param name="requiredPatterns">Patterns that MUST be included (bypass token limit)</param>
    /// <param name="optionalPatterns">Patterns that can be truncated to fit budget</param>
    public static List<Signal> GetSignalsForContextWindow(
        DynamicImageProfile profile,
        int maxTokens,
        string[] requiredPatterns,
        params string[] optionalPatterns)
    {
        var selectedSignals = new List<Signal>();
        int currentTokens = 0;

        // First, add ALL required signals (these must be included)
        var requiredSignals = GetSignalsBySalience(profile, requiredPatterns);
        foreach (var signal in requiredSignals)
        {
            selectedSignals.Add(signal);
            currentTokens += EstimateTokens(signal);
        }

        // Then, add optional signals that fit within remaining budget
        var optionalSignals = GetSignalsBySalience(profile, optionalPatterns);
        foreach (var signal in optionalSignals)
        {
            // Skip if already included as required
            if (requiredSignals.Any(s => s.Key == signal.Key))
                continue;

            var signalTokens = EstimateTokens(signal);

            if (currentTokens + signalTokens <= maxTokens)
            {
                selectedSignals.Add(signal);
                currentTokens += signalTokens;
            }
            else
            {
                // Context window full, stop adding optional signals
                break;
            }
        }

        return selectedSignals;
    }

    /// <summary>
    /// Get ALL signals matching patterns, regardless of token budget
    /// Used for tasks that need complete information (summarization, comparison, etc.)
    /// </summary>
    public static List<Signal> GetAllRequiredSignals(DynamicImageProfile profile, params string[] patterns)
    {
        return GetSignalsBySalience(profile, patterns);
    }

    /// <summary>
    /// Calculate salience score for a signal
    /// Combines importance weight, confidence, and value size
    /// </summary>
    private static double CalculateSalience(Signal signal)
    {
        // Get base importance from dictionary
        var importance = GetImportance(signal.Key);

        // Factor in signal confidence (0.0-1.0)
        var confidenceFactor = signal.Confidence;

        // Combine: importance * confidence
        // This ensures high-importance, high-confidence signals rank highest
        return importance * confidenceFactor;
    }

    /// <summary>
    /// Get importance weight for a signal key
    /// Checks custom weights first (from config), then falls back to built-in defaults.
    /// Supports glob patterns in both dictionaries.
    /// </summary>
    private static double GetImportance(string signalKey)
    {
        // 1. Check custom weights first (exact match)
        if (_customWeights != null && _customWeights.TryGetValue(signalKey, out var customImportance))
        {
            return customImportance;
        }

        // 2. Check custom weights (pattern match)
        if (_customWeights != null)
        {
            foreach (var (pattern, weight) in _customWeights)
            {
                if (pattern.Contains('*'))
                {
                    var regex = GlobToRegex(pattern);
                    if (regex.IsMatch(signalKey))
                    {
                        return weight;
                    }
                }
            }
        }

        // 3. Check default weights (exact match)
        if (DefaultSignalImportance.TryGetValue(signalKey, out var defaultImportance))
        {
            return defaultImportance;
        }

        // 4. Check default weights (pattern match)
        foreach (var (pattern, weight) in DefaultSignalImportance)
        {
            if (pattern.Contains('*'))
            {
                var regex = GlobToRegex(pattern);
                if (regex.IsMatch(signalKey))
                {
                    return weight;
                }
            }
        }

        // 5. Use configured or built-in default
        return _defaultWeight;
    }

    /// <summary>
    /// Estimate token count for a signal
    /// Rough approximation: 1 token ≈ 4 characters
    /// </summary>
    private static int EstimateTokens(Signal signal)
    {
        var valueString = signal.Value?.ToString() ?? "";

        // Base tokens for key + value
        var baseTokens = (signal.Key.Length + valueString.Length) / 4;

        // Add tokens for metadata if present
        if (signal.Metadata != null && signal.Metadata.Any())
        {
            var metadataString = string.Join(",", signal.Metadata.Select(kv => $"{kv.Key}:{kv.Value}"));
            baseTokens += metadataString.Length / 4;
        }

        return Math.Max(1, baseTokens); // Minimum 1 token
    }

    /// <summary>
    /// Convert glob pattern to regex for signal matching
    /// Supports:
    /// - "*" matches any segment (e.g., "vision.*.caption" matches "vision.llm.caption" and "vision.ml.caption")
    /// - "**" matches any number of segments (e.g., "vision.**" matches all vision signals)
    /// - Exact matches (e.g., "vision.llm.caption")
    /// </summary>
    private static System.Text.RegularExpressions.Regex GlobToRegex(string pattern)
    {
        // Escape special regex characters except * and .
        var escaped = System.Text.RegularExpressions.Regex.Escape(pattern);

        // Replace escaped wildcards with regex equivalents
        // ** = match zero or more segments (including dots)
        escaped = escaped.Replace("\\*\\*", ".*");

        // * = match single segment (no dots)
        escaped = escaped.Replace("\\*", "[^.]+");

        // Ensure exact match (start and end anchors)
        var regexPattern = $"^{escaped}$";

        return new System.Text.RegularExpressions.Regex(
            regexPattern,
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Resolve multiple patterns to their values and merge into a single object
    /// Useful for combining different signal sources
    /// </summary>
    public static Dictionary<string, object> ResolveMany(DynamicImageProfile profile, params string[] patterns)
    {
        var result = new Dictionary<string, object>();

        foreach (var pattern in patterns)
        {
            var signals = GetSignalsByPattern(profile, pattern);
            foreach (var signal in signals)
            {
                // Use signal key as dictionary key, avoiding duplicates and null values
                if (signal.Value != null && !result.ContainsKey(signal.Key))
                {
                    result[signal.Key] = signal.Value;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Get best caption from any source
    /// Tries vision LLM first, then ML models, then fallback to alt text
    /// </summary>
    public static string? GetBestCaption(DynamicImageProfile profile)
    {
        return GetWithFallback<string>(
            profile,
            "vision.llm.caption",        // Vision LLM (highest priority)
            "vision.*.caption",           // Any vision model caption
            "caption.*",                  // Any caption source
            "alttext.primary"             // Fallback to generated alt text
        );
    }

    /// <summary>
    /// Get all entity detections from any source
    /// Combines vision LLM entities and ML object detections
    /// </summary>
    public static List<EntityDetection> GetAllEntities(DynamicImageProfile profile)
    {
        var entities = new List<EntityDetection>();

        // Get from vision LLM
        var visionEntities = GetValueByPattern<List<EntityDetection>>(profile, "vision.*.entities");
        if (visionEntities != null)
        {
            entities.AddRange(visionEntities);
        }

        // Get from ML models
        var mlEntities = GetValueByPattern<List<EntityDetection>>(profile, "ml.*.entities");
        if (mlEntities != null)
        {
            entities.AddRange(mlEntities);
        }

        // Deduplicate by type + label
        return entities
            .GroupBy(e => $"{e.Type}:{e.Label}")
            .Select(g => g.OrderByDescending(e => e.Confidence).First())
            .ToList();
    }

    /// <summary>
    /// Get best scene classification from any source
    /// </summary>
    public static (string? Scene, double Confidence) GetBestScene(DynamicImageProfile profile)
    {
        // Try different scene classification sources
        var sceneSignals = GetSignalsByPattern(profile, "*.scene");

        if (!sceneSignals.Any())
        {
            return (null, 0);
        }

        // Get highest confidence scene
        var bestScene = sceneSignals
            .OrderByDescending(s => s.Confidence)
            .First();

        var sceneValue = bestScene.Value as string;
        return (sceneValue, bestScene.Confidence);
    }
}

/// <summary>
/// Extension methods for DynamicImageProfile to make signal resolution more convenient
/// </summary>
public static class DynamicImageProfileExtensions
{
    /// <summary>
    /// Get signal value by glob pattern
    /// </summary>
    public static T? GetByPattern<T>(this DynamicImageProfile profile, string pattern)
    {
        return SignalResolver.GetValueByPattern<T>(profile, pattern);
    }

    /// <summary>
    /// Get all signal values matching pattern
    /// </summary>
    public static List<T> GetManyByPattern<T>(this DynamicImageProfile profile, string pattern)
    {
        return SignalResolver.GetValuesByPattern<T>(profile, pattern);
    }

    /// <summary>
    /// Get value with fallback patterns
    /// </summary>
    public static T? GetWithFallback<T>(this DynamicImageProfile profile, params string[] patterns)
    {
        return SignalResolver.GetWithFallback<T>(profile, patterns);
    }

    /// <summary>
    /// Check if signal exists matching pattern
    /// </summary>
    public static bool HasSignal(this DynamicImageProfile profile, string pattern)
    {
        return SignalResolver.HasSignalMatching(profile, pattern);
    }
}
