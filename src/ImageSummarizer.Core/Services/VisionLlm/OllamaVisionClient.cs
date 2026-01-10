using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.DocSummarizer.Images.Services.VisionLlm;

/// <summary>
/// Ollama implementation of IVisionLlmClient.
/// Communicates with Ollama API for vision model inference.
/// </summary>
public class OllamaVisionClient : IVisionLlmClient
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly ILogger<OllamaVisionClient>? _logger;

    public OllamaVisionClient(
        HttpClient httpClient,
        string model = "minicpm-v:8b",
        ILogger<OllamaVisionClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _model = model;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<bool> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/tags", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<string> ExtractTextAsync(string imagePath, CancellationToken ct = default)
    {
        try
        {
            var imageBytes = await File.ReadAllBytesAsync(imagePath, ct);
            var base64Image = Convert.ToBase64String(imageBytes);

            var request = new
            {
                model = _model,
                prompt = "Extract all visible text from this image. Return only the text content, preserving layout and structure. Be comprehensive and accurate.",
                images = new[] { base64Image },
                stream = false
            };

            var response = await _httpClient.PostAsJsonAsync("/api/generate", request, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Vision LLM text extraction failed: {Status}", response.StatusCode);
                return string.Empty;
            }

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(ct);
            return result?.Response?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to extract text with vision LLM");
            return string.Empty;
        }
    }

    /// <inheritdoc/>
    public async Task<float[]?> GenerateEmbeddingAsync(string imagePath, CancellationToken ct = default)
    {
        try
        {
            var imageBytes = await File.ReadAllBytesAsync(imagePath, ct);
            var base64Image = Convert.ToBase64String(imageBytes);

            var request = new
            {
                model = _model,
                prompt = "Generate embedding for this image",
                images = new[] { base64Image }
            };

            var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Embedding generation failed: {Status}", response.StatusCode);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(ct);
            return result?.Embedding;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to generate embedding");
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<string?> GenerateDescriptionAsync(string imagePath, CancellationToken ct = default)
    {
        try
        {
            var imageBytes = await File.ReadAllBytesAsync(imagePath, ct);
            var base64Image = Convert.ToBase64String(imageBytes);

            var request = new
            {
                model = _model,
                prompt = "Describe this image in detail for search indexing. Include: objects, people, setting, actions, mood, colors, text visible.",
                images = new[] { base64Image },
                stream = false
            };

            var response = await _httpClient.PostAsJsonAsync("/api/generate", request, ct);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(ct);
            return result?.Response;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to generate description");
            return null;
        }
    }

    private record OllamaEmbeddingResponse(
        [property: JsonPropertyName("embedding")] float[] Embedding);

    private record OllamaGenerateResponse(
        [property: JsonPropertyName("response")] string Response,
        [property: JsonPropertyName("done")] bool Done);
}
