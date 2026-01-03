using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mostlylucid.DocSummarizer.Config;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
///     Lightweight Ollama HTTP client for AOT compatibility.
///     Replaces OllamaSharp to reduce binary size and avoid reflection.
///     Uses Polly for resilience (retry with jitter backoff + circuit breaker).
/// 
///     Observability:
///     - OpenTelemetry tracing with Activity spans for generate and embed operations
///     - Metrics for request counts, durations, token usage, and errors
/// </summary>
public class OllamaService
{
    /// <summary>
    ///     Default timeout for LLM generation (20 minutes for large documents/slow models)
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(20);

    #region OpenTelemetry Instrumentation
    
    /// <summary>ActivitySource for distributed tracing</summary>
    private static readonly ActivitySource ActivitySource = new("Mostlylucid.DocSummarizer.Ollama", "1.0.0");
    
    /// <summary>Meter for metrics</summary>
    private static readonly Meter Meter = new("Mostlylucid.DocSummarizer.Ollama", "1.0.0");
    
    /// <summary>Counter for generate requests</summary>
    private static readonly Counter<long> GenerateCounter = Meter.CreateCounter<long>(
        "docsummarizer.ollama.generate.requests",
        "requests",
        "Total number of LLM generate requests");
    
    /// <summary>Counter for embedding requests</summary>
    private static readonly Counter<long> EmbedCounter = Meter.CreateCounter<long>(
        "docsummarizer.ollama.embed.requests",
        "requests",
        "Total number of embedding requests");
    
    /// <summary>Histogram for generate duration</summary>
    private static readonly Histogram<double> GenerateDurationHistogram = Meter.CreateHistogram<double>(
        "docsummarizer.ollama.generate.duration",
        "ms",
        "Duration of LLM generate operations in milliseconds");
    
    /// <summary>Histogram for embed duration</summary>
    private static readonly Histogram<double> EmbedDurationHistogram = Meter.CreateHistogram<double>(
        "docsummarizer.ollama.embed.duration",
        "ms",
        "Duration of embedding operations in milliseconds");
    
    /// <summary>Histogram for prompt token count</summary>
    private static readonly Histogram<long> PromptTokensHistogram = Meter.CreateHistogram<long>(
        "docsummarizer.ollama.prompt.tokens",
        "tokens",
        "Number of prompt tokens sent");
    
    /// <summary>Histogram for response token count</summary>
    private static readonly Histogram<long> ResponseTokensHistogram = Meter.CreateHistogram<long>(
        "docsummarizer.ollama.response.tokens",
        "tokens",
        "Number of response tokens generated");
    
    /// <summary>Counter for errors by type</summary>
    private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
        "docsummarizer.ollama.errors",
        "errors",
        "Total number of Ollama errors");
    
    /// <summary>Counter for circuit breaker events</summary>
    private static readonly Counter<long> CircuitBreakerCounter = Meter.CreateCounter<long>(
        "docsummarizer.ollama.circuit_breaker",
        "events",
        "Circuit breaker state changes");
    
    #endregion

    private readonly string _baseUrl;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;
    private readonly EmbeddingConfig _embeddingConfig;
    private readonly ResiliencePipeline<float[]> _embeddingResiliencePipeline;

    public OllamaService(
        string model = "llama3.2:3b",
        string embedModel = "nomic-embed-text",
        string baseUrl = "http://localhost:11434",
        TimeSpan? timeout = null,
        EmbeddingConfig? embeddingConfig = null,
        string? classifierModel = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        _baseUrl = baseUrl.TrimEnd('/');
        Model = model;
        EmbedModel = embedModel;
        // Classifier model defaults to main model if not specified or empty
        ClassifierModel = string.IsNullOrWhiteSpace(classifierModel) ? model : classifierModel;
        _embeddingConfig = embeddingConfig ?? new EmbeddingConfig();

        // Configure HttpClient with proper connection handling for Windows
        var handler = new SocketsHttpHandler
        {
            // Don't pool connections - create fresh ones to avoid Windows wsarecv issues
            PooledConnectionLifetime = TimeSpan.Zero,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 1, // Ollama processes one at a time anyway
            ConnectTimeout = TimeSpan.FromSeconds(30),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
        };
        
        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = _timeout + TimeSpan.FromMinutes(1)
        };
        
        // Build Polly resilience pipeline for embeddings
        _embeddingResiliencePipeline = BuildEmbeddingResiliencePipeline();
    }
    
    /// <summary>
    /// Build resilience pipeline with retry (decorrelated jitter backoff) and circuit breaker
    /// </summary>
    private ResiliencePipeline<float[]> BuildEmbeddingResiliencePipeline()
    {
        return new ResiliencePipelineBuilder<float[]>()
            // Retry with decorrelated jitter backoff (Polly's recommended approach)
            .AddRetry(new RetryStrategyOptions<float[]>
            {
                MaxRetryAttempts = _embeddingConfig.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true, // Decorrelated jitter
                Delay = TimeSpan.FromMilliseconds(_embeddingConfig.InitialRetryDelayMs),
                MaxDelay = TimeSpan.FromMilliseconds(_embeddingConfig.MaxRetryDelayMs),
                ShouldHandle = new PredicateBuilder<float[]>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .Handle<InvalidOperationException>(ex => ex.Message.Contains("embedding")),
                OnRetry = args =>
                {
                    var isConnectionError = args.Outcome.Exception?.Message.Contains("wsarecv") == true ||
                                           args.Outcome.Exception?.Message.Contains("forcibly closed") == true ||
                                           args.Outcome.Exception?.Message.Contains("connection") == true;
                    
                    Console.WriteLine($"[Ollama] Retry {args.AttemptNumber}/{_embeddingConfig.MaxRetries} after {args.RetryDelay.TotalSeconds:F1}s" +
                                    (isConnectionError ? " (connection error)" : ""));
                    return ValueTask.CompletedTask;
                }
            })
            // Circuit breaker to fail fast when Ollama is consistently failing
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<float[]>
            {
                FailureRatio = 0.8, // Open circuit if 80% of requests fail
                MinimumThroughput = _embeddingConfig.CircuitBreakerThreshold,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(_embeddingConfig.CircuitBreakerDurationSeconds),
                ShouldHandle = new PredicateBuilder<float[]>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .Handle<InvalidOperationException>(),
                OnOpened = args =>
                {
                    Console.WriteLine($"[Ollama] Circuit breaker OPENED - Ollama appears unavailable. Will retry after {args.BreakDuration.TotalSeconds}s");
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    Console.WriteLine("[Ollama] Circuit breaker CLOSED - Ollama is available again");
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = _ =>
                {
                    Console.WriteLine("[Ollama] Circuit breaker HALF-OPEN - Testing Ollama availability...");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    public string Model { get; }

    public string EmbedModel { get; }
    
    /// <summary>
    /// Small/fast model for document classification (sentinel). 
    /// Defaults to main model if not specified.
    /// </summary>
    public string ClassifierModel { get; }

    public TimeSpan Timeout => _timeout;

    public async Task<string> GenerateAsync(string prompt, double temperature = 0.3,
        CancellationToken cancellationToken = default)
    {
        return await GenerateWithModelAsync(Model, prompt, temperature, cancellationToken);
    }
    
    /// <summary>
    /// Generate text using a specific model (useful for sentinel/classifier models)
    /// </summary>
    public async Task<string> GenerateWithModelAsync(string modelName, string prompt, double temperature = 0.3,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("OllamaGenerate", ActivityKind.Client);
        activity?.SetTag("llm.model", modelName);
        activity?.SetTag("llm.temperature", temperature);
        activity?.SetTag("llm.prompt_length", prompt.Length);
        
        var sw = Stopwatch.StartNew();
        
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        var request = new OllamaGenerateRequest
        {
            Model = modelName,
            Prompt = prompt,
            Options = new OllamaOptions { Temperature = temperature }
        };

        var json = JsonSerializer.Serialize(request, DocSummarizerJsonContext.Default.OllamaGenerateRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        
        // Estimate prompt tokens (rough: ~4 chars per token for English)
        var estimatedPromptTokens = prompt.Length / 4;
        PromptTokensHistogram.Record(estimatedPromptTokens, 
            new KeyValuePair<string, object?>("model", modelName));

        var sb = new StringBuilder();
        try
        {
            using var response = await _httpClient.PostAsync("/api/generate", content, cts.Token);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);

            // Read streaming NDJSON response
            string? line;
            while ((line = await reader.ReadLineAsync(cts.Token)) != null)
            {
                if (string.IsNullOrEmpty(line)) continue;

                var chunk = JsonSerializer.Deserialize(line, DocSummarizerJsonContext.Default.OllamaGenerateResponse);
                if (chunk?.Response != null) sb.Append(chunk.Response);

                if (chunk?.Done == true) break;
            }
            
            var result = sb.ToString().Trim();
            
            // Estimate response tokens
            var estimatedResponseTokens = result.Length / 4;
            ResponseTokensHistogram.Record(estimatedResponseTokens,
                new KeyValuePair<string, object?>("model", modelName));
            
            activity?.SetTag("llm.response_length", result.Length);
            activity?.SetTag("llm.estimated_tokens", estimatedResponseTokens);
            activity?.SetStatus(ActivityStatusCode.Ok);
            
            GenerateCounter.Add(1, new KeyValuePair<string, object?>("model", modelName));
            
            return result;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Timeout");
            ErrorCounter.Add(1,
                new KeyValuePair<string, object?>("model", modelName),
                new KeyValuePair<string, object?>("error.type", "timeout"));
            
            throw new TimeoutException(
                $"LLM generation timed out after {_timeout.TotalMinutes:F0} minutes. Consider using a faster model or increasing the timeout.");
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            ErrorCounter.Add(1,
                new KeyValuePair<string, object?>("model", modelName),
                new KeyValuePair<string, object?>("error.type", ex.GetType().Name));
            throw;
        }
        finally
        {
            sw.Stop();
            GenerateDurationHistogram.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("model", modelName));
        }
    }

    /// <summary>
    /// Generate embeddings for text with robust retry logic, circuit breaker, and connection recovery.
    /// For long texts, splits into chunks and averages the resulting vectors to preserve semantic content.
    /// Addresses Ollama Windows wsarecv connection issues (GitHub issue #13340).
    /// </summary>
    public async Task<float[]> EmbedAsync(string text, int maxRetries = 5, CancellationToken cancellationToken = default)
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
        
        var embeddings = new List<float[]>();
        for (var i = 0; i < chunks.Count; i++)
        {
            // Add significant jittered delay between chunk embeddings to let Ollama recover
            // This is critical to prevent connection pool exhaustion and wsarecv errors on Windows
            // Ollama processes embeddings sequentially and needs time between requests
            if (i > 0)
            {
                var baseDelay = Math.Max(_embeddingConfig.DelayBetweenRequestsMs * 5, 500); // At least 500ms
                var jitter = Random.Shared.Next(0, baseDelay); // 0-100% jitter for decorrelation
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
    /// Embed a single chunk of text using Polly resilience pipeline (retry + circuit breaker)
    /// </summary>
    private async Task<float[]> EmbedSingleChunkAsync(string cleanText, int maxRetries, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("OllamaEmbed", ActivityKind.Client);
        activity?.SetTag("llm.model", EmbedModel);
        activity?.SetTag("llm.text_length", cleanText.Length);
        
        var sw = Stopwatch.StartNew();
        
        try
        {
            var result = await _embeddingResiliencePipeline.ExecuteAsync(
                async ct => await ExecuteEmbeddingRequestAsync(cleanText, ct),
                cancellationToken);
            
            activity?.SetTag("llm.embedding_dimension", result.Length);
            activity?.SetStatus(ActivityStatusCode.Ok);
            
            EmbedCounter.Add(1, new KeyValuePair<string, object?>("model", EmbedModel));
            
            return result;
        }
        catch (BrokenCircuitException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "CircuitBreakerOpen");
            CircuitBreakerCounter.Add(1, 
                new KeyValuePair<string, object?>("state", "rejected"),
                new KeyValuePair<string, object?>("model", EmbedModel));
            ErrorCounter.Add(1,
                new KeyValuePair<string, object?>("model", EmbedModel),
                new KeyValuePair<string, object?>("error.type", "circuit_breaker"));
            
            throw new InvalidOperationException(
                $"Circuit breaker is open - Ollama appears unavailable. " +
                $"Will retry after circuit breaker timeout. " +
                "This usually indicates Ollama is overloaded or crashed.", ex);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            ErrorCounter.Add(1,
                new KeyValuePair<string, object?>("model", EmbedModel),
                new KeyValuePair<string, object?>("error.type", ex.GetType().Name));
            throw;
        }
        finally
        {
            sw.Stop();
            EmbedDurationHistogram.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("model", EmbedModel));
        }
    }
    
    /// <summary>
    /// Execute a single embedding request with fresh connection handling
    /// </summary>
    private async Task<float[]> ExecuteEmbeddingRequestAsync(string cleanText, CancellationToken cancellationToken)
    {
        var request = new OllamaEmbedRequest { Model = EmbedModel, Prompt = cleanText };
        var json = JsonSerializer.Serialize(request, DocSummarizerJsonContext.Default.OllamaEmbedRequest);
        
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60)); // 60 second timeout per attempt (increased from 30)
        
        // Create fresh request to avoid connection pooling issues
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/embeddings")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        
        // Add Connection: close header to ensure fresh connection each time
        // This is the KEY FIX for Windows wsarecv issues
        httpRequest.Headers.ConnectionClose = true;
        
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cts.Token);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("forcibly closed") || 
                                               ex.Message.Contains("wsarecv") ||
                                               ex.Message.Contains("connection"))
        {
            // Wrap with more context for debugging
            throw new HttpRequestException(
                $"Ollama connection failed (likely Ollama crashed or is overloaded). " +
                $"Text length: {cleanText.Length} chars. Original error: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cts.Token);
            
            // Check for specific Ollama errors
            if (errorBody.Contains("caching disabled") || errorBody.Contains("unable to fit"))
            {
                throw new InvalidOperationException(
                    $"Ollama batch size error - text too long ({cleanText.Length} chars). " +
                    $"Try reducing chunk size. Error: {errorBody}");
            }
            
            throw new HttpRequestException(
                $"Ollama embedding request failed with status {response.StatusCode}: {errorBody}. " +
                $"Text length: {cleanText.Length} chars.");
        }

        var responseJson = await response.Content.ReadAsStringAsync(cts.Token);
        var embedResponse = JsonSerializer.Deserialize(responseJson, DocSummarizerJsonContext.Default.OllamaEmbedResponse);

        if (embedResponse?.Embedding == null || embedResponse.Embedding.Length == 0)
        {
            throw new InvalidOperationException(
                $"No embedding returned from Ollama. Response: {responseJson[..Math.Min(200, responseJson.Length)]}");
        }

        return embedResponse.Embedding;
    }

    /// <summary>
    /// Get context window for the embed model (in tokens)
    /// </summary>
    public int GetEmbedContextWindow()
    {
        return GetEmbedContextWindowForModel(EmbedModel);
    }
    
    private static int GetEmbedContextWindowForModel(string model)
    {
        var embedContextWindows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "snowflake-arctic-embed", 512 },
            { "snowflake-arctic-embed:latest", 512 },
            { "snowflake-arctic-embed:xs", 512 },
            { "snowflake-arctic-embed:s", 512 },
            { "snowflake-arctic-embed:m", 512 },
            { "snowflake-arctic-embed:l", 512 },
            { "nomic-embed-text", 8192 },
            { "nomic-embed-text:latest", 8192 },
            { "mxbai-embed-large", 512 },
            { "mxbai-embed-large:latest", 512 },
            { "all-minilm", 256 },
            { "all-minilm:latest", 256 },
            { "bge-m3", 8192 },
            { "bge-m3:latest", 8192 },
        };
        
        if (embedContextWindows.TryGetValue(model, out var window))
            return window;
        
        // Default conservative context for unknown embed models
        return 512;
    }

    private static string NormalizeTextForEmbedding(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");

        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            // Keep basic whitespace
            if (c == '\n' || c == '\t' || c == ' ')
            {
                sb.Append(c);
            }
            // Keep printable ASCII (0x20-0x7E)
            else if (c >= 0x20 && c <= 0x7E)
            {
                sb.Append(c);
            }
            // Keep Latin-1 Supplement (0x80-0xFF) - Western European accents
            else if (c >= 0x80 && c <= 0xFF)
            {
                sb.Append(c);
            }
            // SKIP Latin Extended-A/B (0x0100-0x024F) - often garbage from bad PDF fonts
            else if (c >= 0x0100 && c <= 0x024F)
            {
                continue;
            }
            // Keep Greek (0x0370-0x03FF)
            else if (c >= 0x0370 && c <= 0x03FF)
            {
                sb.Append(c);
            }
            // Keep Cyrillic (0x0400-0x04FF)
            else if (c >= 0x0400 && c <= 0x04FF)
            {
                sb.Append(c);
            }
            // Keep Arabic (0x0600-0x06FF)
            else if (c >= 0x0600 && c <= 0x06FF)
            {
                sb.Append(c);
            }
            // Keep Hebrew (0x0590-0x05FF)
            else if (c >= 0x0590 && c <= 0x05FF)
            {
                sb.Append(c);
            }
            // Keep CJK Unified Ideographs (0x4E00-0x9FFF)
            else if (c >= 0x4E00 && c <= 0x9FFF)
            {
                sb.Append(c);
            }
            // Keep Hiragana (0x3040-0x309F)
            else if (c >= 0x3040 && c <= 0x309F)
            {
                sb.Append(c);
            }
            // Keep Katakana (0x30A0-0x30FF)
            else if (c >= 0x30A0 && c <= 0x30FF)
            {
                sb.Append(c);
            }
            // Keep Hangul Syllables (0xAC00-0xD7AF)
            else if (c >= 0xAC00 && c <= 0xD7AF)
            {
                sb.Append(c);
            }
            // Keep Thai (0x0E00-0x0E7F)
            else if (c >= 0x0E00 && c <= 0x0E7F)
            {
                sb.Append(c);
            }
            // Keep Devanagari (0x0900-0x097F)
            else if (c >= 0x0900 && c <= 0x097F)
            {
                sb.Append(c);
            }
            // Keep common punctuation and symbols
            else if (char.IsPunctuation(c) || char.IsSymbol(c))
            {
                sb.Append(c);
            }
        }

        // Collapse multiple spaces/newlines
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
            var tagsResponse = JsonSerializer.Deserialize(json, DocSummarizerJsonContext.Default.OllamaTagsResponse);
            return tagsResponse?.Models?.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ModelInfo?> GetModelInfoAsync(string? modelName = null)
    {
        try
        {
            var model = modelName ?? Model;
            var request = new OllamaShowRequest { Name = model };
            var json = JsonSerializer.Serialize(request, DocSummarizerJsonContext.Default.OllamaShowRequest);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/api/show", content);
            if (!response.IsSuccessStatusCode) return null;

            var responseJson = await response.Content.ReadAsStringAsync();
            var showResponse = JsonSerializer.Deserialize(responseJson, DocSummarizerJsonContext.Default.OllamaShowResponse);

            var modelInfo = new ModelInfo
            {
                Name = model,
                ParameterCount = showResponse?.Details?.ParameterSize ?? "unknown",
                QuantizationLevel = showResponse?.Details?.QuantizationLevel ?? "unknown",
                Family = showResponse?.Details?.Family ?? "unknown",
                Format = showResponse?.Details?.Format ?? "unknown"
            };

            modelInfo.ContextWindow = GetContextWindowForModel(model, modelInfo.Family);
            return modelInfo;
        }
        catch
        {
            return null;
        }
    }

    public async Task<int> GetContextWindowAsync()
    {
        var info = await GetModelInfoAsync();
        return info?.ContextWindow ?? 8192;
    }

    private static int GetContextWindowForModel(string model, string family)
    {
        var contextWindows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "ministral-3:3b", 128000 },
            { "ministral-3:latest", 128000 },
            { "llama3.2:3b", 128000 },
            { "llama3.2:latest", 128000 },
            { "llama3.1:8b", 128000 },
            { "llama3.1:latest", 128000 },
            { "gemma3:1b", 32000 },
            { "gemma3:4b", 128000 },
            { "gemma3:12b", 128000 },
            { "gemma2:2b", 8192 },
            { "gemma2:9b", 8192 },
            { "qwen2.5:1.5b", 32000 },
            { "qwen2.5:3b", 32000 },
            { "qwen2.5:7b", 32000 },
            { "phi3:mini", 128000 },
            { "phi3:medium", 128000 },
            { "mistral:7b", 32000 },
            { "mistral:latest", 32000 },
            { "tinyllama:latest", 2048 },
            { "nomic-embed-text", 8192 },
            { "nomic-embed-text:latest", 8192 },
            { "snowflake-arctic-embed", 512 },
            { "snowflake-arctic-embed:latest", 512 }
        };

        if (contextWindows.TryGetValue(model, out var knownWindow)) return knownWindow;

        var familyLower = family.ToLowerInvariant();
        if (familyLower.Contains("llama3") || familyLower.Contains("ministral"))
            return 128000;
        if (familyLower.Contains("gemma3"))
            return 32000;
        if (familyLower.Contains("qwen"))
            return 32000;
        if (familyLower.Contains("phi"))
            return 128000;
        if (familyLower.Contains("mistral"))
            return 32000;

        var modelLower = model.ToLowerInvariant();
        if (modelLower.Contains("llama3") || modelLower.Contains("ministral"))
            return 128000;
        if (modelLower.Contains("gemma3"))
            return 32000;
        if (modelLower.Contains("qwen"))
            return 32000;

        return 8192;
    }

    public async Task<List<string>> GetAvailableModelsAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/tags");
            if (!response.IsSuccessStatusCode) return new List<string>();

            var json = await response.Content.ReadAsStringAsync();
            var tagsResponse = JsonSerializer.Deserialize(json, DocSummarizerJsonContext.Default.OllamaTagsResponse);

            return tagsResponse?.Models?.Select(m => m.Name ?? "").Where(n => !string.IsNullOrEmpty(n)).ToList()
                   ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}

// DTOs for Ollama API - used by source generator
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

    [JsonPropertyName("quantization_level")]
    public string? QuantizationLevel { get; set; }

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
