namespace Mostlylucid.DocSummarizer.Anthropic.Config;

/// <summary>
/// Configuration for Anthropic Claude API
/// </summary>
public class AnthropicConfig
{
    public const string SectionName = "Anthropic";

    /// <summary>
    /// Anthropic API key. Can use ${ANTHROPIC_API_KEY} for environment variable substitution.
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Default model to use
    /// </summary>
    public string Model { get; set; } = "claude-3-5-haiku-latest";

    /// <summary>
    /// Anthropic API base URL
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    /// <summary>
    /// Maximum tokens to generate
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// Temperature for generation (0.0 - 1.0)
    /// </summary>
    public double Temperature { get; set; } = 0.3;

    /// <summary>
    /// Timeout in seconds for API calls
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// API version header
    /// </summary>
    public string ApiVersion { get; set; } = "2023-06-01";
}
