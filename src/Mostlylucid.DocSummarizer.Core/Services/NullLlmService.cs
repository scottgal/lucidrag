namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// No-op LLM service for when LlmBackend is set to None or when using deterministic-only mode.
/// All methods return empty/default values without making any API calls.
/// </summary>
public class NullLlmService : ILlmService
{
    /// <inheritdoc />
    public string ProviderName => "None";

    /// <inheritdoc />
    public Task<string> GenerateAsync(string prompt, LlmOptions? options = null, CancellationToken ct = default)
    {
        // Return empty string - deterministic mode only
        return Task.FromResult(string.Empty);
    }

    /// <inheritdoc />
    public Task<T?> GenerateJsonAsync<T>(string prompt, LlmOptions? options = null, CancellationToken ct = default) where T : class
    {
        // Return null - no LLM available
        return Task.FromResult<T?>(null);
    }

    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // No LLM configured
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task<int> GetContextWindowAsync(CancellationToken ct = default)
    {
        // Return 0 - no context window available
        return Task.FromResult(0);
    }
}
