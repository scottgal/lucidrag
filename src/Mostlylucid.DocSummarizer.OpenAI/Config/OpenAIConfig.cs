namespace Mostlylucid.DocSummarizer.OpenAI.Config;

/// <summary>
/// Configuration for OpenAI API
/// </summary>
public class OpenAIConfig
{
    public const string SectionName = "OpenAI";

    /// <summary>
    /// OpenAI API key. Can use ${OPENAI_API_KEY} for environment variable substitution.
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Default chat model to use
    /// </summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// Embedding model to use
    /// </summary>
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";

    /// <summary>
    /// OpenAI API base URL (can be changed for Azure OpenAI or compatible APIs)
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>
    /// Maximum tokens to generate
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// Temperature for generation (0.0 - 2.0)
    /// </summary>
    public double Temperature { get; set; } = 0.3;

    /// <summary>
    /// Timeout in seconds for API calls
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Embedding dimension (1536 for text-embedding-3-small)
    /// </summary>
    public int EmbeddingDimension { get; set; } = 1536;
}
