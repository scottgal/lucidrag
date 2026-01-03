namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Options for LLM generation requests
/// </summary>
public class LlmOptions
{
    /// <summary>
    /// Override the default model for this request
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Temperature for generation (0.0-1.0). Lower = more deterministic.
    /// </summary>
    public double? Temperature { get; set; }

    /// <summary>
    /// Maximum tokens to generate
    /// </summary>
    public int? MaxTokens { get; set; }

    /// <summary>
    /// System prompt/instructions
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Default options with temperature 0.3
    /// </summary>
    public static LlmOptions Default => new() { Temperature = 0.3 };
}

/// <summary>
/// Abstraction for LLM text generation services.
/// Implementations: OllamaLlmService, AnthropicLlmService, OpenAILlmService
/// </summary>
public interface ILlmService
{
    /// <summary>
    /// Provider name for logging/diagnostics
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Generate text from a prompt
    /// </summary>
    /// <param name="prompt">The prompt to send to the LLM</param>
    /// <param name="options">Generation options (temperature, max tokens, etc.)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Generated text response</returns>
    Task<string> GenerateAsync(string prompt, LlmOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Generate structured JSON output from a prompt
    /// </summary>
    /// <typeparam name="T">Type to deserialize the response into</typeparam>
    /// <param name="prompt">The prompt to send to the LLM</param>
    /// <param name="options">Generation options</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Deserialized response object</returns>
    Task<T?> GenerateJsonAsync<T>(string prompt, LlmOptions? options = null, CancellationToken ct = default) where T : class;

    /// <summary>
    /// Check if the LLM service is available and responding
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Get the context window size in tokens for the configured model
    /// </summary>
    Task<int> GetContextWindowAsync(CancellationToken ct = default);
}
