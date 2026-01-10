using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Vision LLM embedding wave using Ollama for direct image embeddings.
/// Uses MiniCPM-V or LLaVA for generating semantic image embeddings.
///
/// Superior to CLIP for:
/// - Semantic understanding (understands context, actions, relationships)
/// - Multilingual support
/// - Direct integration with vision-language models
///
/// References:
/// - MiniCPM-V: https://ollama.com/library/minicpm-v
/// - LLaVA: https://ollama.com/library/llava
/// - Vision LLM design: https://huggingface.co/blog/gigant/vlm-design
/// </summary>
public class VisionLlmEmbeddingWave : IAnalysisWave
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<VisionLlmEmbeddingWave>? _logger;
    private readonly string _model;
    private readonly bool _enabled;

    public string Name => "VisionLlmEmbeddingWave";
    public int Priority => 50; // Medium priority - expensive operation
    public IReadOnlyList<string> Tags => new[] { SignalTags.Content, "embedding" };

    public VisionLlmEmbeddingWave(
        string ollamaBaseUrl = "http://localhost:11434",
        string model = "minicpm-v:8b",
        bool enabled = true,
        ILogger<VisionLlmEmbeddingWave>? logger = null)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(ollamaBaseUrl),
            Timeout = TimeSpan.FromMinutes(2)
        };
        _model = model;
        _enabled = enabled;
        _logger = logger;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        if (!_enabled)
        {
            signals.Add(new Signal
            {
                Key = "embedding.vision_llm_enabled",
                Value = false,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "config" }
            });
            return signals;
        }

        try
        {
            // Check if Ollama is available
            var available = await CheckOllamaAvailableAsync(ct);
            if (!available)
            {
                _logger?.LogWarning("Ollama not available, skipping vision LLM embedding");

                signals.Add(new Signal
                {
                    Key = "embedding.vision_llm_available",
                    Value = false,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "status" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["reason"] = "Ollama service not responding"
                    }
                });

                return signals;
            }

            // Read image and convert to base64
            var imageBytes = await File.ReadAllBytesAsync(imagePath, ct);
            var base64Image = Convert.ToBase64String(imageBytes);

            // Generate embedding via Ollama
            var embedding = await GenerateEmbeddingAsync(base64Image, ct);

            if (embedding != null && embedding.Length > 0)
            {
                signals.Add(new Signal
                {
                    Key = "embedding.vision_llm",
                    Value = embedding,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "embedding" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["model"] = _model,
                        ["dimension"] = embedding.Length,
                        ["embedding_type"] = "vision_llm_direct"
                    }
                });

                _logger?.LogInformation("Generated vision LLM embedding: {Dimension}D vector using {Model}",
                    embedding.Length, _model);
            }

            // Optionally generate description for enhanced search
            var description = await GenerateDescriptionAsync(base64Image, ct);

            if (!string.IsNullOrWhiteSpace(description))
            {
                signals.Add(new Signal
                {
                    Key = "content.vision_llm_description",
                    Value = description,
                    Confidence = 0.9,
                    Source = Name,
                    Tags = new List<string> { SignalTags.Content },
                    Metadata = new Dictionary<string, object>
                    {
                        ["model"] = _model,
                        ["length"] = description.Length
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Vision LLM embedding failed");

            signals.Add(new Signal
            {
                Key = "embedding.vision_llm_error",
                Value = ex.Message,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "error" }
            });
        }

        return signals;
    }

    private async Task<bool> CheckOllamaAvailableAsync(CancellationToken ct)
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

    private async Task<float[]?> GenerateEmbeddingAsync(string base64Image, CancellationToken ct)
    {
        try
        {
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

    private async Task<string?> GenerateDescriptionAsync(string base64Image, CancellationToken ct)
    {
        try
        {
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
