using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models;

namespace Mostlylucid.DocSummarizer.Images.Services;

/// <summary>
/// Service for building vision LLM prompts from configurable templates.
/// Uses weighted signal selection to include only relevant context.
/// </summary>
public class PromptTemplateService
{
    private readonly ILogger<PromptTemplateService>? _logger;
    private readonly PromptTemplates _templates;

    public PromptTemplateService(ILogger<PromptTemplateService>? logger = null)
    {
        _logger = logger;
        _templates = LoadTemplates();
    }

    /// <summary>
    /// Build a prompt for the vision LLM based on image profile and output format.
    /// Uses weighted signal selection to include only relevant context per image type.
    /// Applies TOON-like compression principles for token efficiency.
    /// </summary>
    public string BuildPrompt(
        ImageProfile profile,
        string outputFormat = "alttext",
        GifMotionProfile? motion = null,
        string? extractedText = null,
        bool useCompactFormat = true)
    {
        // Use compact TOON-style format for efficiency
        if (useCompactFormat)
            return BuildCompactPrompt(profile, outputFormat, motion, extractedText);

        return BuildVerbosePrompt(profile, outputFormat, motion, extractedText);
    }

    /// <summary>
    /// TOON-style compact prompt: ~50% fewer tokens than verbose format.
    /// Principles: one-time headers, no verbose syntax, bullet efficiency.
    /// PURPOSE-DRIVEN: Different formats answer different questions.
    /// </summary>
    private string BuildCompactPrompt(
        ImageProfile profile,
        string outputFormat,
        GifMotionProfile? motion,
        string? extractedText)
    {
        var typeKey = GetImageTypeKey(profile);
        var typeTemplate = _templates.ImageTypes.GetValueOrDefault(typeKey)
                           ?? _templates.ImageTypes.GetValueOrDefault("unknown");
        var formatTemplate = _templates.OutputFormats.GetValueOrDefault(outputFormat)
                             ?? _templates.OutputFormats.GetValueOrDefault("alttext");

        // Determine if actually animated (not just detected as photo)
        var isActuallyAnimated = profile.Format?.Equals("GIF", StringComparison.OrdinalIgnoreCase) == true ||
                                  profile.Format?.Equals("WEBP", StringComparison.OrdinalIgnoreCase) == true;

        var sb = new StringBuilder();

        // Task header with output format
        var formatLabel = isActuallyAnimated ? "ANIMATED" : "STATIC";
        sb.AppendLine($"TASK:{formatLabel}|OUT:JSON {{\"caption\":\"...\"}}");

        // PURPOSE - The key differentiator between formats
        if (!string.IsNullOrWhiteSpace(formatTemplate?.Purpose))
            sb.AppendLine($"PURPOSE:{formatTemplate.Purpose}");

        // Focus directive based on image type
        if (typeTemplate != null)
            sb.AppendLine($"FOCUS:{typeTemplate.Focus}");

        // Signals (compact, only high-weight)
        var signals = BuildCompactSignals(profile, typeTemplate, motion, extractedText);
        if (!string.IsNullOrWhiteSpace(signals))
            sb.AppendLine($"SIGNALS:{signals}");

        // Format-specific rules with salience enforcement
        var rules = BuildPurposeDrivenRules(formatTemplate, outputFormat);
        sb.AppendLine($"RULES:{rules}");

        // Example (compact)
        if (typeTemplate?.Example != null)
            sb.AppendLine($"EX:{typeTemplate.Example}");

        var prompt = sb.ToString();
        _logger?.LogDebug("Compact prompt for {ImageType}/{Format}: {Chars} chars (est ~{Tokens} tokens)",
            typeKey, outputFormat, prompt.Length, prompt.Length / 4);

        return prompt;
    }

    /// <summary>
    /// Build purpose-driven rules based on output format.
    /// WCAG Alt text = subjects-first, under 125 chars, observable context OK
    /// Caption = subjects-then-setting, factual
    /// Social = engaging, context-aggregation-ok
    /// </summary>
    private string BuildPurposeDrivenRules(OutputFormatTemplate? format, string outputKey)
    {
        // Default rules for unknown formats
        if (format == null)
            return "factual-only|no-names|1-2 sentences|ONLY what you see";

        // If format has rules, use them directly
        if (format.Rules?.Length > 0)
        {
            return string.Join("|", format.Rules);
        }

        // Purpose-specific default rules (WCAG-compliant for alttext)
        return outputKey.ToLowerInvariant() switch
        {
            // WCAG 2.1 compliant: subjects first, observable context, no inventory
            "alttext" => "subjects-first|NO-image-of-prefix|observable-context-ok|no-inventory|no-names|under-125-chars",
            "caption" => "subjects-first|then-setting|factual|no-names|2-3 sentences",
            "socialmedia" => "engaging|brief|context-aggregation-ok|no-hashtags|1-2 sentences",
            _ => "factual-only|no-names|1-2 sentences|ONLY what you see"
        };
    }

    /// <summary>
    /// Build compact signal string using glob patterns: "motion:left-rapid|colors:blue,gray|text:high"
    /// </summary>
    private string BuildCompactSignals(
        ImageProfile profile,
        ImageTypeTemplate? typeTemplate,
        GifMotionProfile? motion,
        string? extractedText)
    {
        // Prefer SignalGlobs over SignalWeights (backwards compatible)
        var signalConfig = typeTemplate?.SignalGlobs ?? typeTemplate?.SignalWeights;
        if (signalConfig == null || signalConfig.Count == 0)
            return "";

        var threshold = _templates.SignalThreshold;
        var parts = new List<string>();

        // Group globs by category and get highest weight per category
        var categoryValues = new Dictionary<string, (double weight, string? value)>();

        foreach (var (globPattern, weight) in signalConfig.OrderByDescending(kv => kv.Value))
        {
            if (weight < threshold) continue;

            // Extract category from glob (e.g., "motion.*" -> "motion", "color.dominant*" -> "color")
            var category = GetCategoryFromGlob(globPattern);
            if (string.IsNullOrEmpty(category)) continue;

            // Only process if we haven't captured this category yet (highest weight wins)
            if (categoryValues.ContainsKey(category)) continue;

            var value = GetCompactSignalValueForGlob(globPattern, category, profile, motion, extractedText);
            if (!string.IsNullOrWhiteSpace(value))
            {
                categoryValues[category] = (weight, value);
            }
        }

        // Build output sorted by weight
        foreach (var (category, (weight, value)) in categoryValues.OrderByDescending(kv => kv.Value.weight))
        {
            if (!string.IsNullOrWhiteSpace(value))
                parts.Add($"{category}:{value}");
        }

        return string.Join("|", parts);
    }

    /// <summary>
    /// Extract category name from glob pattern.
    /// "motion.*" -> "motion", "color.dominant*" -> "color", "quality.edge*" -> "edges"
    /// </summary>
    private static string GetCategoryFromGlob(string glob)
    {
        if (string.IsNullOrEmpty(glob)) return "";

        // Get first segment before dot
        var dotIndex = glob.IndexOf('.');
        if (dotIndex > 0)
        {
            var category = glob[..dotIndex];
            // Map some categories to shorter names
            return category switch
            {
                "content" => "text",
                "ocr" => "text",
                "complexity" => "motion",
                _ => category
            };
        }

        return glob.TrimEnd('*', '.');
    }

    /// <summary>
    /// Get compact signal value for a glob pattern.
    /// </summary>
    private string? GetCompactSignalValueForGlob(
        string globPattern,
        string category,
        ImageProfile profile,
        GifMotionProfile? motion,
        string? extractedText)
    {
        return category.ToLowerInvariant() switch
        {
            "motion" when motion != null =>
                $"{motion.MotionDirection}-{(motion.MotionMagnitude > 3 ? "rapid" : "slow")}-{motion.FrameCount}f",
            "color" or "colors" when profile.DominantColors?.Any() == true =>
                string.Join(",", profile.DominantColors.Take(2).Select(c => c.Name.Split(' ')[0].ToLower())),
            "text" when profile.TextLikeliness > 0.3 =>
                profile.TextLikeliness > 0.7 ? "high" : "med",
            "quality" when profile.LaplacianVariance < 100 => "blur",
            "quality" when profile.LaplacianVariance > 500 => "sharp",
            "edges" when profile.EdgeDensity > 0.15 => "complex",
            "identity" when profile.Width > 0 => $"{profile.Width}x{profile.Height}",
            _ => null
        };
    }

    /// <summary>
    /// Get compact signal value (short form).
    /// </summary>
    private string? GetCompactSignalValue(string key, ImageProfile profile, GifMotionProfile? motion, string? text)
    {
        return key.ToLowerInvariant() switch
        {
            "motion" when motion != null => $"{motion.MotionDirection}-{(motion.MotionMagnitude > 3 ? "rapid" : "slow")}-{motion.FrameCount}f",
            "colors" when profile.DominantColors?.Any() == true =>
                string.Join(",", profile.DominantColors.Take(2).Select(c => c.Name.Split(' ')[0].ToLower())),
            "text" when profile.TextLikeliness > 0.3 =>
                profile.TextLikeliness > 0.7 ? "high" : "med",
            "quality" when profile.LaplacianVariance < 100 => "blur",
            "quality" when profile.LaplacianVariance > 500 => "sharp",
            "edges" when profile.EdgeDensity > 0.15 => "complex",
            _ => null
        };
    }

    /// <summary>
    /// Verbose prompt format (original, for comparison/debugging).
    /// </summary>
    private string BuildVerbosePrompt(
        ImageProfile profile,
        string outputFormat,
        GifMotionProfile? motion,
        string? extractedText)
    {
        var sb = new StringBuilder();
        var typeKey = GetImageTypeKey(profile);
        var typeTemplate = _templates.ImageTypes.GetValueOrDefault(typeKey)
                           ?? _templates.ImageTypes.GetValueOrDefault("unknown");
        var formatTemplate = _templates.OutputFormats.GetValueOrDefault(outputFormat)
                             ?? _templates.OutputFormats.GetValueOrDefault("alttext");

        sb.AppendLine("Describe this image factually for accessibility.");
        sb.AppendLine();

        foreach (var rule in _templates.GlobalRules)
            sb.AppendLine(rule);
        sb.AppendLine();

        if (typeTemplate != null)
        {
            sb.AppendLine($"IMAGE TYPE: {typeTemplate.Name.ToUpperInvariant()}");
            sb.AppendLine($"Focus on: {typeTemplate.Focus}");
            if (!string.IsNullOrWhiteSpace(typeTemplate.Context))
                sb.AppendLine(typeTemplate.Context);
            sb.AppendLine();
        }

        var signalContext = BuildSignalContext(profile, typeTemplate, motion, extractedText);
        if (!string.IsNullOrWhiteSpace(signalContext))
        {
            sb.AppendLine("DETECTED SIGNALS:");
            sb.AppendLine(signalContext);
            sb.AppendLine();
        }

        if (formatTemplate != null)
        {
            sb.AppendLine($"OUTPUT: {formatTemplate.Instruction}");
            foreach (var rule in formatTemplate.Rules)
                sb.AppendLine($"- {rule}");
        }
        sb.AppendLine();

        if (typeTemplate?.Example != null)
            sb.AppendLine($"Example: {typeTemplate.Example}");

        var prompt = sb.ToString();
        _logger?.LogDebug("Verbose prompt for {ImageType}: {PromptLength} chars",
            typeKey, prompt.Length);

        return prompt;
    }

    /// <summary>
    /// Build signal context based on weighted relevance for the image type.
    /// Only includes signals with weight >= threshold.
    /// </summary>
    private string BuildSignalContext(
        ImageProfile profile,
        ImageTypeTemplate? typeTemplate,
        GifMotionProfile? motion,
        string? extractedText)
    {
        if (typeTemplate?.SignalWeights == null || typeTemplate.SignalWeights.Count == 0)
            return "";

        var threshold = _templates.SignalThreshold;
        var signalParts = new List<(double weight, string context)>();

        foreach (var (signalKey, weight) in typeTemplate.SignalWeights)
        {
            if (weight < threshold)
                continue;

            var context = FormatSignal(signalKey, weight, profile, motion, extractedText);
            if (!string.IsNullOrWhiteSpace(context))
            {
                signalParts.Add((weight, context));
            }
        }

        // Sort by weight descending (most relevant first)
        signalParts.Sort((a, b) => b.weight.CompareTo(a.weight));

        return string.Join("\n", signalParts.Select(s => $"• {s.context}"));
    }

    /// <summary>
    /// Format a signal category with actual values from the profile.
    /// </summary>
    private string? FormatSignal(
        string signalKey,
        double weight,
        ImageProfile profile,
        GifMotionProfile? motion,
        string? extractedText)
    {
        return signalKey.ToLowerInvariant() switch
        {
            "motion" => FormatMotionSignal(motion),
            "colors" => FormatColorSignal(profile),
            "text" => FormatTextSignal(profile, extractedText),
            "quality" => FormatQualitySignal(profile),
            "identity" => FormatIdentitySignal(profile),
            "edges" => FormatEdgeSignal(profile),
            _ => null
        };
    }

    private string? FormatMotionSignal(GifMotionProfile? motion)
    {
        if (motion == null || string.IsNullOrWhiteSpace(motion.MotionDirection))
            return null;

        var intensity = motion.MotionMagnitude switch
        {
            < 1 => "subtle",
            < 3 => "moderate",
            < 5 => "significant",
            _ => "rapid"
        };

        return $"Motion: {motion.MotionDirection} movement ({intensity}, {motion.FrameCount} frames)";
    }

    private string? FormatColorSignal(ImageProfile profile)
    {
        if (profile.DominantColors == null || !profile.DominantColors.Any())
            return null;

        var colors = string.Join(", ", profile.DominantColors.Take(3).Select(c => c.Name));
        var saturation = profile.IsMostlyGrayscale ? "grayscale" :
                         profile.MeanSaturation > 0.5 ? "vibrant" : "muted";

        return $"Colors: {colors} ({saturation})";
    }

    private string? FormatTextSignal(ImageProfile profile, string? extractedText)
    {
        if (profile.TextLikeliness < 0.2)
            return null;

        var confidence = profile.TextLikeliness switch
        {
            > 0.7 => "high",
            > 0.4 => "moderate",
            _ => "low"
        };

        if (!string.IsNullOrWhiteSpace(extractedText) && extractedText.Length > 10)
        {
            var preview = extractedText.Length > 50
                ? extractedText[..47] + "..."
                : extractedText;
            return $"Text detected ({confidence} confidence): \"{preview}\"";
        }

        return $"Text likely present ({confidence} confidence)";
    }

    private string? FormatQualitySignal(ImageProfile profile)
    {
        var sharpness = profile.LaplacianVariance switch
        {
            < 100 => "blurry/soft",
            < 300 => "moderate sharpness",
            < 500 => "sharp",
            _ => "very sharp"
        };

        // Only mention if notable
        if (profile.LaplacianVariance < 100 || profile.LaplacianVariance > 500)
        {
            return $"Quality: {sharpness}";
        }

        return null;
    }

    private string? FormatIdentitySignal(ImageProfile profile)
    {
        if (profile.Width == 0 || profile.Height == 0)
            return null;

        // Only include for unusual sizes
        var isSmall = profile.Width < 100 || profile.Height < 100;
        var isLarge = profile.Width > 2000 || profile.Height > 2000;

        if (isSmall || isLarge)
        {
            var sizeDesc = isSmall ? "small/icon" : "high-res";
            return $"Size: {profile.Width}x{profile.Height} ({sizeDesc})";
        }

        return null;
    }

    private string? FormatEdgeSignal(ImageProfile profile)
    {
        var complexity = profile.EdgeDensity switch
        {
            > 0.2 => "high detail/complexity",
            > 0.1 => "moderate detail",
            _ => null
        };

        return complexity != null ? $"Visual complexity: {complexity}" : null;
    }

    /// <summary>
    /// Get the template key for the detected image type.
    /// </summary>
    private string GetImageTypeKey(ImageProfile profile)
    {
        // Check for animated first (takes priority)
        if (profile.Format?.Equals("GIF", StringComparison.OrdinalIgnoreCase) == true ||
            profile.Format?.Equals("WEBP", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "animated";
        }

        return profile.DetectedType switch
        {
            ImageType.Photo => "photo",
            ImageType.Screenshot => "screenshot",
            ImageType.Diagram => "diagram",
            ImageType.Chart => "chart",
            ImageType.Artwork => "artwork",
            ImageType.Meme => "meme",
            ImageType.ScannedDocument => "scanned_document",
            ImageType.Icon => "icon",
            _ => "unknown"
        };
    }

    /// <summary>
    /// Load templates from embedded JSON or external file.
    /// </summary>
    private PromptTemplates LoadTemplates()
    {
        try
        {
            // Try loading from embedded resource first
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "Mostlylucid.DocSummarizer.Images.Config.prompt-templates.json";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var templates = JsonSerializer.Deserialize<PromptTemplates>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (templates != null)
                {
                    _logger?.LogInformation("Loaded prompt templates from embedded resource");
                    return templates;
                }
            }

            // Try loading from file in Config directory
            var configPath = Path.Combine(AppContext.BaseDirectory, "Config", "prompt-templates.json");
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var templates = JsonSerializer.Deserialize<PromptTemplates>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (templates != null)
                {
                    _logger?.LogInformation("Loaded prompt templates from {Path}", configPath);
                    return templates;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load prompt templates, using defaults");
        }

        // Return default templates
        return GetDefaultTemplates();
    }

    private static PromptTemplates GetDefaultTemplates()
    {
        return new PromptTemplates
        {
            Version = "1.0",
            ImageTypes = new Dictionary<string, ImageTypeTemplate>
            {
                ["photo"] = new() { Name = "Photograph", Focus = "main subject, setting, action/pose", SignalWeights = new() { ["colors"] = 0.6, ["quality"] = 0.4 } },
                ["animated"] = new() { Name = "Animated Image", Focus = "the action/movement, what is happening, and ANY VISIBLE TEXT/SUBTITLES shown (quote the text exactly)", SignalWeights = new() { ["motion"] = 1.0, ["text"] = 1.0, ["colors"] = 0.5 } },
                ["screenshot"] = new() { Name = "Screenshot", Focus = "UI elements, visible text, application", SignalWeights = new() { ["text"] = 1.0, ["colors"] = 0.4 } },
                ["diagram"] = new() { Name = "Diagram", Focus = "diagram type, what it represents", SignalWeights = new() { ["text"] = 0.9, ["edges"] = 0.8 } },
                ["chart"] = new() { Name = "Chart", Focus = "chart type, data shown, key takeaway", SignalWeights = new() { ["text"] = 1.0, ["colors"] = 0.7 } },
                ["unknown"] = new() { Name = "Image", Focus = "main subject and visible details", SignalWeights = new() { ["colors"] = 0.5, ["text"] = 0.5, ["quality"] = 0.4 } }
            },
            OutputFormats = new Dictionary<string, OutputFormatTemplate>
            {
                ["alttext"] = new()
                {
                    Name = "Alt Text",
                    Instruction = "Write 1-2 clear sentences for accessibility.",
                    Rules = new[] { "Describe what is visible", "Be factual" }
                }
            },
            GlobalRules = new[]
            {
                "Reply ONLY with JSON: {\"caption\": \"<description>\"}",
                "ONLY describe what you can actually see",
                "Do NOT identify specific people by name"
            },
            SignalThreshold = 0.5
        };
    }
}

#region Template Models

public class PromptTemplates
{
    public string Version { get; set; } = "1.0";
    public string? Description { get; set; }
    public Dictionary<string, ImageTypeTemplate> ImageTypes { get; set; } = new();
    public Dictionary<string, OutputFormatTemplate> OutputFormats { get; set; } = new();
    public Dictionary<string, SignalDefinition>? SignalDefinitions { get; set; }
    public string[] GlobalRules { get; set; } = Array.Empty<string>();
    public double SignalThreshold { get; set; } = 0.5;
}

public class ImageTypeTemplate
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Focus { get; set; } = "";
    public string? Context { get; set; }
    public string? Example { get; set; }
    public Dictionary<string, double>? SignalWeights { get; set; }
    public Dictionary<string, double>? SignalGlobs { get; set; }
}

public class OutputFormatTemplate
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>
    /// The core PURPOSE of this output format - tells the LLM what question to answer.
    /// Alt text: "What would someone miss if they couldn't see this?"
    /// Caption: "Describe the scene for understanding"
    /// Social: "Capture the moment engagingly"
    /// </summary>
    public string? Purpose { get; set; }
    public string Instruction { get; set; } = "";
    public int MaxLength { get; set; } = 200;
    public string[] Rules { get; set; } = Array.Empty<string>();
    /// <summary>
    /// Priority order for elements (what to include first)
    /// </summary>
    public string[]? PriorityOrder { get; set; }
    /// <summary>
    /// Drop order for compression (what to remove first when too long)
    /// </summary>
    public string[]? DropOrder { get; set; }
}

public class SignalDefinition
{
    public string[] Keys { get; set; } = Array.Empty<string>();
    public string? Description { get; set; }
    public string? FormatTemplate { get; set; }
}

#endregion
