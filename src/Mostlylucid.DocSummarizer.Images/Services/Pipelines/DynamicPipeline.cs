using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mostlylucid.DocSummarizer.Images.Services.Pipelines;

/// <summary>
/// YAML-defined dynamic pipeline configuration.
/// Allows users to define custom analysis pipelines via file or string.
/// Shared across CLI and Desktop applications.
/// </summary>
public class DynamicPipeline
{
    /// <summary>
    /// Pipeline name (for display and logging)
    /// </summary>
    public string Name { get; set; } = "custom";

    /// <summary>
    /// Optional description of what this pipeline does
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Schema version (for future compatibility)
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Signal patterns to include in analysis.
    /// Supports glob patterns and @collections.
    /// Examples: "motion.*", "color.dominant*", "@alttext"
    /// </summary>
    public List<string> Signals { get; set; } = new();

    /// <summary>
    /// Specific waves to run (by name).
    /// If specified, only these waves run regardless of signals.
    /// Examples: ["ColorWave", "MotionWave", "Florence2Wave"]
    /// </summary>
    public List<string> Waves { get; set; } = new();

    /// <summary>
    /// Output configuration
    /// </summary>
    public OutputConfig Output { get; set; } = new();

    /// <summary>
    /// LLM configuration overrides
    /// </summary>
    public LlmConfig Llm { get; set; } = new();

    /// <summary>
    /// Escalation behavior configuration
    /// </summary>
    public EscalationConfig Escalation { get; set; } = new();

    /// <summary>
    /// Get combined signal pattern string for SignalGlobMatcher
    /// </summary>
    public string GetSignalPattern()
    {
        if (Signals.Count == 0)
            return "*"; // All signals if none specified
        return string.Join(",", Signals);
    }

    /// <summary>
    /// Check if this pipeline uses wave-based selection
    /// </summary>
    public bool UsesWaveSelection => Waves.Count > 0;

    /// <summary>
    /// Check if this pipeline uses signal-based selection
    /// </summary>
    public bool UsesSignalSelection => Signals.Count > 0;
}

/// <summary>
/// Output format configuration
/// </summary>
public class OutputConfig
{
    /// <summary>
    /// Output format: json, text, alttext, markdown, visual, signals, metrics, caption
    /// </summary>
    public string Format { get; set; } = "json";

    /// <summary>
    /// Include full signal metadata in output
    /// </summary>
    public bool IncludeMetadata { get; set; } = true;

    /// <summary>
    /// Include confidence scores in output
    /// </summary>
    public bool IncludeConfidence { get; set; } = true;
}

/// <summary>
/// LLM configuration for the pipeline
/// </summary>
public class LlmConfig
{
    /// <summary>
    /// Enable Vision LLM processing
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Ollama model to use (e.g., "minicpm-v:8b", "llava:7b")
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Ollama base URL
    /// </summary>
    public string? OllamaUrl { get; set; }

    /// <summary>
    /// Fast mode: direct LLM call without heuristics
    /// </summary>
    public bool FastMode { get; set; } = false;

    /// <summary>
    /// Include analysis context when calling LLM
    /// </summary>
    public bool Context { get; set; } = true;
}

/// <summary>
/// Escalation behavior configuration
/// </summary>
public class EscalationConfig
{
    /// <summary>
    /// Always escalate GIFs to LLM (Florence-2 is weak at animations)
    /// </summary>
    public bool GifToLlm { get; set; } = true;

    /// <summary>
    /// Edge density threshold for complexity-based escalation
    /// </summary>
    public double ComplexityThreshold { get; set; } = 0.4;

    /// <summary>
    /// Escalate if no caption is generated
    /// </summary>
    public bool OnNoCaption { get; set; } = true;

    /// <summary>
    /// Escalate if caption is shorter than this length
    /// </summary>
    public int MinCaptionLength { get; set; } = 20;
}

/// <summary>
/// Loader for YAML pipeline definitions.
/// Shared across CLI and Desktop applications.
/// </summary>
public static class DynamicPipelineLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Load pipeline from a YAML file
    /// </summary>
    public static DynamicPipeline LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Pipeline file not found: {filePath}", filePath);

        var yaml = File.ReadAllText(filePath);
        return LoadFromString(yaml);
    }

    /// <summary>
    /// Load pipeline from YAML string
    /// </summary>
    public static DynamicPipeline LoadFromString(string yaml)
    {
        try
        {
            var pipeline = Deserializer.Deserialize<DynamicPipeline>(yaml);
            return pipeline ?? new DynamicPipeline();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse pipeline YAML: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Load pipeline from a stream (for stdin or embedded resources)
    /// </summary>
    public static DynamicPipeline LoadFromStream(Stream stream)
    {
        using var reader = new StreamReader(stream);
        var yaml = reader.ReadToEnd();
        return LoadFromString(yaml);
    }

    /// <summary>
    /// Try to load pipeline, returns null on failure
    /// </summary>
    public static DynamicPipeline? TryLoadFromFile(string filePath)
    {
        try
        {
            return LoadFromFile(filePath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Validate a pipeline definition
    /// </summary>
    public static (bool IsValid, List<string> Errors) Validate(DynamicPipeline pipeline)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(pipeline.Name))
            errors.Add("Pipeline name is required");

        if (pipeline.Signals.Count == 0 && pipeline.Waves.Count == 0)
            errors.Add("Either signals or waves must be specified");

        var validOutputFormats = new[] { "json", "text", "alttext", "markdown", "visual", "signals", "metrics", "caption", "auto" };
        if (!validOutputFormats.Contains(pipeline.Output.Format.ToLowerInvariant()))
            errors.Add($"Invalid output format: {pipeline.Output.Format}. Valid: {string.Join(", ", validOutputFormats)}");

        return (errors.Count == 0, errors);
    }

    /// <summary>
    /// Get sample YAML for documentation
    /// </summary>
    public static string GetSampleYaml()
    {
        return """
            # Dynamic Pipeline Definition
            # Use with: imagesummarizer image.gif --pipeline-file pipeline.yaml
            # Or pipe:  cat pipeline.yaml | imagesummarizer image.gif --pipeline-file -

            name: social-media-alttext
            description: Generate accessible alt text for social media images
            version: 1

            # Signal-based selection (glob patterns or @collections)
            signals:
              - "@alttext"           # Predefined collection for alt text
              - motion.*             # All motion signals
              - color.dominant*      # Dominant color info

            # Alternative: Wave-based selection (runs specific waves)
            # waves:
            #   - IdentityWave
            #   - ColorWave
            #   - Florence2Wave
            #   - VisionLlmWave

            # Output configuration
            output:
              format: alttext        # json, text, alttext, markdown, visual
              include_metadata: true
              include_confidence: true

            # LLM configuration
            llm:
              enabled: true
              model: minicpm-v:8b    # Ollama model
              fast_mode: false       # Skip heuristics and go straight to LLM
              context: true          # Include analysis signals in LLM prompt

            # Escalation behavior
            escalation:
              gif_to_llm: true       # Always use LLM for GIFs (Florence-2 is weak)
              complexity_threshold: 0.4
              on_no_caption: true
              min_caption_length: 20
            """;
    }
}
