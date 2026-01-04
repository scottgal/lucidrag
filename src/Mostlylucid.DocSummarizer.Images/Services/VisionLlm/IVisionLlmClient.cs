namespace Mostlylucid.DocSummarizer.Images.Services.VisionLlm;

/// <summary>
/// Abstraction for vision LLM clients that can analyze images.
/// Enables testing and swapping between different vision LLM providers.
/// </summary>
public interface IVisionLlmClient
{
    /// <summary>
    /// Check if the vision LLM service is available.
    /// </summary>
    Task<bool> CheckAvailabilityAsync(CancellationToken ct = default);

    /// <summary>
    /// Extract text from an image using vision LLM.
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Extracted text content</returns>
    Task<string> ExtractTextAsync(string imagePath, CancellationToken ct = default);

    /// <summary>
    /// Generate an embedding vector for an image.
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Embedding vector</returns>
    Task<float[]?> GenerateEmbeddingAsync(string imagePath, CancellationToken ct = default);

    /// <summary>
    /// Generate a description of an image.
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Image description</returns>
    Task<string?> GenerateDescriptionAsync(string imagePath, CancellationToken ct = default);
}
