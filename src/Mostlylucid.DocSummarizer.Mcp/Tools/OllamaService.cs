using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mostlylucid.DocSummarizer.Mcp.Tools;

/// <summary>
/// Lightweight Ollama HTTP client for MCP server.
/// </summary>
public class OllamaService
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(20);

    private readonly string _baseUrl;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    public OllamaService(
        string model = "llama3.2:3b",
        string embedModel = "nomic-embed-text",
        string baseUrl = "http://localhost:11434",
        TimeSpan? timeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        _baseUrl = baseUrl.TrimEnd('/');
        Model = model;
        EmbedModel = embedModel;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = _timeout + TimeSpan.FromMinutes(1)
        };
    }

    public string Model { get; }
    public string EmbedModel { get; }

    public async Task<string> GenerateAsync(string prompt, double temperature = 0.3,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        var request = new OllamaGenerateRequest
        {
            Model = Model,
            Prompt = prompt,
            Options = new OllamaOptions { Temperature = temperature }
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var sb = new StringBuilder();
        try
        {
            using var response = await _httpClient.PostAsync("/api/generate", content, cts.Token);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(cts.Token)) != null)
            {
                if (string.IsNullOrEmpty(line)) continue;

                var chunk = JsonSerializer.Deserialize<OllamaGenerateResponse>(line);
                if (chunk?.Response != null) sb.Append(chunk.Response);
                if (chunk?.Done == true) break;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"LLM generation timed out after {_timeout.TotalMinutes:F0} minutes.");
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Generate embeddings for text. For long texts, splits into chunks and averages vectors.
    /// </summary>
    public async Task<float[]> EmbedAsync(string text, int maxRetries = 3, CancellationToken cancellationToken = default)
    {
        var cleanText = NormalizeTextForEmbedding(text);
        
        // CRITICAL: Ollama on Windows crashes with large embedding requests (wsarecv errors)
        // Testing shows nomic-embed-text fails at ~1700+ chars despite supporting 8192 tokens.
        // This appears to be a batch size limitation in Ollama's embedding implementation.
        // Use very conservative 1000 char limit to ensure reliability and avoid splitting.
        const int maxCharsPerChunk = 1000;
        
        // If text fits in one chunk, embed directly
        if (cleanText.Length <= maxCharsPerChunk)
        {
            return await EmbedSingleChunkAsync(cleanText, maxRetries, cancellationToken);
        }
        
        // Split into overlapping chunks and average embeddings
        var chunks = SplitTextIntoChunks(cleanText, maxCharsPerChunk, overlap: maxCharsPerChunk / 10);
        Console.WriteLine($"[Ollama] Text too long ({cleanText.Length} chars), splitting into {chunks.Count} chunks for embedding");
        
        var embeddings = new List<float[]>();
        for (var i = 0; i < chunks.Count; i++)
        {
            // Add significant jittered delay between chunk embeddings to let Ollama recover
            // This is critical to prevent connection pool exhaustion and wsarecv errors on Windows
            if (i > 0)
            {
                var baseDelay = 500; // At least 500ms
                var jitter = Random.Shared.Next(0, 500); // 0-500ms jitter for decorrelation
                await Task.Delay(baseDelay + jitter, cancellationToken);
            }
            
            var embedding = await EmbedSingleChunkAsync(chunks[i], maxRetries, cancellationToken);
            embeddings.Add(embedding);
        }
        
        // Average all chunk embeddings to get final vector
        return AverageEmbeddings(embeddings);
    }
    
    /// <summary>
    /// Split text into overlapping chunks for embedding
    /// </summary>
    private static List<string> SplitTextIntoChunks(string text, int maxChunkSize, int overlap)
    {
        var chunks = new List<string>();
        var stride = maxChunkSize - overlap;
        
        for (var i = 0; i < text.Length; i += stride)
        {
            var length = Math.Min(maxChunkSize, text.Length - i);
            chunks.Add(text.Substring(i, length));
            
            // Stop if we've covered the entire text
            if (i + length >= text.Length) break;
        }
        
        return chunks;
    }
    
    /// <summary>
    /// Average multiple embedding vectors into a single normalized vector
    /// </summary>
    private static float[] AverageEmbeddings(List<float[]> embeddings)
    {
        if (embeddings.Count == 0)
            throw new InvalidOperationException("No embeddings to average");
        
        if (embeddings.Count == 1)
            return embeddings[0];
        
        var vectorSize = embeddings[0].Length;
        var result = new float[vectorSize];
        
        // Sum all vectors
        foreach (var embedding in embeddings)
        {
            for (var i = 0; i < vectorSize; i++)
            {
                result[i] += embedding[i];
            }
        }
        
        // Average and normalize (L2 normalization for cosine similarity)
        var count = embeddings.Count;
        var magnitude = 0.0;
        for (var i = 0; i < vectorSize; i++)
        {
            result[i] /= count;
            magnitude += result[i] * result[i];
        }
        
        magnitude = Math.Sqrt(magnitude);
        if (magnitude > 0)
        {
            for (var i = 0; i < vectorSize; i++)
            {
                result[i] = (float)(result[i] / magnitude);
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Embed a single chunk of text with retry logic
    /// </summary>
    private async Task<float[]> EmbedSingleChunkAsync(string cleanText, int maxRetries, CancellationToken cancellationToken)
    {
        var request = new OllamaEmbedRequest { Model = EmbedModel, Prompt = cleanText };
        var json = JsonSerializer.Serialize(request);

        Exception? lastException = null;
        
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                
                var response = await _httpClient.PostAsync("/api/embeddings", content, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(cts.Token);
                    throw new HttpRequestException(
                        $"Ollama embedding request failed with status {response.StatusCode}: {errorBody}");
                }

                var responseJson = await response.Content.ReadAsStringAsync(cts.Token);
                var embedResponse = JsonSerializer.Deserialize<OllamaEmbedResponse>(responseJson);

                if (embedResponse?.Embedding == null || embedResponse.Embedding.Length == 0)
                {
                    throw new InvalidOperationException("No embedding returned from Ollama");
                }

                return embedResponse.Embedding;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                lastException = ex;
                var baseDelay = 1000 * Math.Pow(2, attempt - 1);
                var jitter = Random.Shared.Next(0, (int)(baseDelay * 0.1));
                await Task.Delay(TimeSpan.FromMilliseconds(baseDelay + jitter), cancellationToken);
            }
        }

        throw lastException ?? new InvalidOperationException("Embedding failed after retries");
    }

    public int GetEmbedContextWindow()
    {
        var embedContextWindows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "snowflake-arctic-embed", 512 },
            { "nomic-embed-text", 8192 },
            { "mxbai-embed-large", 512 },
            { "all-minilm", 256 },
            { "bge-m3", 8192 },
        };
        
        return embedContextWindows.TryGetValue(EmbedModel, out var window) ? window : 512;
    }

    private static string NormalizeTextForEmbedding(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var sb = new StringBuilder(normalized.Length);
        
        foreach (var c in normalized)
        {
            if (c == '\n' || c == '\t' || c == ' ' || (c >= 0x20 && c <= 0x7E) || 
                (c >= 0x80 && c <= 0xFF) || char.IsPunctuation(c) || char.IsSymbol(c))
            {
                sb.Append(c);
            }
        }

        var result = sb.ToString();
        result = Regex.Replace(result, @"[ \t]+", " ");
        result = Regex.Replace(result, @"\n{3,}", "\n\n");
        
        return result.Trim();
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/tags");
            if (!response.IsSuccessStatusCode) return false;

            var json = await response.Content.ReadAsStringAsync();
            var tagsResponse = JsonSerializer.Deserialize<OllamaTagsResponse>(json);
            return tagsResponse?.Models?.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<string>> GetAvailableModelsAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/tags");
            if (!response.IsSuccessStatusCode) return new List<string>();

            var json = await response.Content.ReadAsStringAsync();
            var tagsResponse = JsonSerializer.Deserialize<OllamaTagsResponse>(json);

            return tagsResponse?.Models?.Select(m => m.Name ?? "").Where(n => !string.IsNullOrEmpty(n)).ToList()
                   ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    public async Task<ModelInfo?> GetModelInfoAsync(string? modelName = null)
    {
        try
        {
            var model = modelName ?? Model;
            var request = new OllamaShowRequest { Name = model };
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/api/show", content);
            if (!response.IsSuccessStatusCode) return null;

            var responseJson = await response.Content.ReadAsStringAsync();
            var showResponse = JsonSerializer.Deserialize<OllamaShowResponse>(responseJson);

            return new ModelInfo
            {
                Name = model,
                ParameterCount = showResponse?.Details?.ParameterSize ?? "unknown",
                QuantizationLevel = showResponse?.Details?.QuantizationLevel ?? "unknown",
                Family = showResponse?.Details?.Family ?? "unknown",
                Format = showResponse?.Details?.Format ?? "unknown",
                ContextWindow = GetContextWindowForModel(model, showResponse?.Details?.Family ?? "")
            };
        }
        catch
        {
            return null;
        }
    }

    private static int GetContextWindowForModel(string model, string family)
    {
        var contextWindows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "llama3.2:3b", 128000 },
            { "llama3.1:8b", 128000 },
            { "gemma2:2b", 8192 },
            { "qwen2.5:3b", 32000 },
            { "mistral:7b", 32000 },
            { "tinyllama:latest", 2048 },
        };

        if (contextWindows.TryGetValue(model, out var knownWindow)) return knownWindow;
        return 8192;
    }
}

// DTOs
public class OllamaGenerateRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
    [JsonPropertyName("options")] public OllamaOptions? Options { get; set; }
}

public class OllamaOptions
{
    [JsonPropertyName("temperature")] public double Temperature { get; set; }
}

public class OllamaGenerateResponse
{
    [JsonPropertyName("response")] public string? Response { get; set; }
    [JsonPropertyName("done")] public bool Done { get; set; }
}

public class OllamaEmbedRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
}

public class OllamaEmbedResponse
{
    [JsonPropertyName("embedding")] public float[]? Embedding { get; set; }
}

public class OllamaShowRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public class OllamaShowResponse
{
    [JsonPropertyName("details")] public OllamaModelDetails? Details { get; set; }
}

public class OllamaModelDetails
{
    [JsonPropertyName("parameter_size")] public string? ParameterSize { get; set; }
    [JsonPropertyName("quantization_level")] public string? QuantizationLevel { get; set; }
    [JsonPropertyName("family")] public string? Family { get; set; }
    [JsonPropertyName("format")] public string? Format { get; set; }
}

public class OllamaTagsResponse
{
    [JsonPropertyName("models")] public List<OllamaModelInfo>? Models { get; set; }
}

public class OllamaModelInfo
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public record ModelInfo
{
    public string Name { get; set; } = "";
    public string ParameterCount { get; set; } = "unknown";
    public string QuantizationLevel { get; set; } = "unknown";
    public string Family { get; set; } = "unknown";
    public string Format { get; set; } = "unknown";
    public int ContextWindow { get; set; } = 2048;
}
