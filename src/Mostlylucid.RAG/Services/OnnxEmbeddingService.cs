using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Mostlylucid.RAG.Config;
using System.Text.RegularExpressions;

namespace Mostlylucid.RAG.Services;

/// <summary>
/// ONNX-based embedding service using CPU-friendly sentence transformers
/// Uses all-MiniLM-L6-v2 model (384 dimensions) for efficient semantic search
/// </summary>
public class OnnxEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly ILogger<OnnxEmbeddingService> _logger;
    private readonly SemanticSearchConfig _config;
    private InferenceSession? _session;
    private readonly Dictionary<string, int> _vocabulary;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly SemaphoreSlim _initSemaphore = new(1, 1);
    private bool _disposed;
    private bool _initialized;

    private const int MaxSequenceLength = 256;
    private const string PadToken = "[PAD]";
    private const string UnkToken = "[UNK]";
    private const string ClsToken = "[CLS]";
    private const string SepToken = "[SEP]";

    // Hugging Face model URLs
    private const string ModelUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx";
    private const string VocabUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt";

    public OnnxEmbeddingService(
        ILogger<OnnxEmbeddingService> logger,
        SemanticSearchConfig config)
    {
        _logger = logger;
        _config = config;
        _vocabulary = new Dictionary<string, int>();
    }

    /// <summary>
    /// Ensures the model is initialized, downloading if necessary
    /// </summary>
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized || !_config.Enabled) return;

        await _initSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            // Ensure directory exists
            var modelDir = Path.GetDirectoryName(_config.EmbeddingModelPath);
            if (!string.IsNullOrEmpty(modelDir) && !Directory.Exists(modelDir))
            {
                Directory.CreateDirectory(modelDir);
                _logger.LogInformation("Created model directory: {Path}", modelDir);
            }

            // Download model if not exists
            if (!File.Exists(_config.EmbeddingModelPath))
            {
                _logger.LogInformation("Downloading ONNX embedding model to {Path}...", _config.EmbeddingModelPath);
                await DownloadFileAsync(ModelUrl, _config.EmbeddingModelPath, cancellationToken);
                _logger.LogInformation("ONNX model downloaded successfully");
            }

            // Download vocab if not exists
            if (!File.Exists(_config.VocabPath))
            {
                _logger.LogInformation("Downloading vocabulary file to {Path}...", _config.VocabPath);
                await DownloadFileAsync(VocabUrl, _config.VocabPath, cancellationToken);
                _logger.LogInformation("Vocabulary file downloaded successfully");
            }

            // Load vocabulary
            if (File.Exists(_config.VocabPath))
            {
                LoadVocabulary(_config.VocabPath);
            }

            // Create ONNX session
            var sessionOptions = new SessionOptions
            {
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };

            _session = new InferenceSession(_config.EmbeddingModelPath, sessionOptions);
            _logger.LogInformation("ONNX embedding model loaded successfully from {Path}", _config.EmbeddingModelPath);
            _initialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize ONNX embedding service");
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    private static async Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromMinutes(10); // Large file timeout

        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
        await contentStream.CopyToAsync(fileStream, cancellationToken);
    }

    private void LoadVocabulary(string vocabPath)
    {
        var lines = File.ReadAllLines(vocabPath);
        for (int i = 0; i < lines.Length; i++)
        {
            var token = lines[i].Trim();
            if (!string.IsNullOrEmpty(token))
            {
                _vocabulary[token] = i;
            }
        }
        _logger.LogInformation("Loaded vocabulary with {Count} tokens", _vocabulary.Count);
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
        {
            return new float[_config.VectorSize];
        }

        // Ensure model is downloaded and initialized
        await EnsureInitializedAsync(cancellationToken);

        if (_session == null)
        {
            // Return zero vector if initialization failed
            return new float[_config.VectorSize];
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new float[_config.VectorSize];
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => GenerateEmbedding(text), cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<List<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        var results = new List<float[]>();
        foreach (var text in texts)
        {
            var embedding = await GenerateEmbeddingAsync(text, cancellationToken);
            results.Add(embedding);
        }
        return results;
    }

    private float[] GenerateEmbedding(string text)
    {
        try
        {
            // Tokenize the input text
            var tokens = Tokenize(text);
            var actualLength = Math.Min(tokens.Count, MaxSequenceLength);

            // Create input tensors
            var inputIds = CreateInputTensor(tokens, "input_ids");
            var attentionMask = CreateAttentionMaskTensor(tokens.Count);
            var tokenTypeIds = CreateTokenTypeIdsTensor(tokens.Count);

            // Run inference
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
                NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds)
            };

            using var results = _session!.Run(inputs);

            // Extract the output tensor - shape is [1, sequence_length, hidden_size]
            // For all-MiniLM-L6-v2: [1, 256, 384]
            var output = results.First().AsTensor<float>();
            var dimensions = output.Dimensions.ToArray();

            // Apply mean pooling over the sequence dimension (considering attention mask)
            var embedding = MeanPooling(output, actualLength, dimensions);
            return NormalizeVector(embedding);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating embedding for text: {Text}", text[..Math.Min(100, text.Length)]);
            return new float[_config.VectorSize];
        }
    }

    /// <summary>
    /// Mean pooling: average the token embeddings, only considering non-padded tokens
    /// </summary>
    private float[] MeanPooling(Tensor<float> output, int actualLength, int[] dimensions)
    {
        // Handle different output shapes
        // Shape could be [1, seq_len, hidden_size] or [1, hidden_size]
        if (dimensions.Length == 2)
        {
            // Already pooled - just return as is
            var result = new float[dimensions[1]];
            for (int i = 0; i < dimensions[1]; i++)
            {
                result[i] = output[0, i];
            }
            return result;
        }

        // Shape is [batch, seq_len, hidden_size]
        var seqLen = dimensions[1];
        var hiddenSize = dimensions[2];
        var pooled = new float[hiddenSize];

        // Sum all token embeddings (only for actual tokens, not padding)
        var tokensToPool = Math.Min(actualLength, seqLen);
        for (int t = 0; t < tokensToPool; t++)
        {
            for (int h = 0; h < hiddenSize; h++)
            {
                pooled[h] += output[0, t, h];
            }
        }

        // Average by dividing by number of actual tokens
        if (tokensToPool > 0)
        {
            for (int h = 0; h < hiddenSize; h++)
            {
                pooled[h] /= tokensToPool;
            }
        }

        return pooled;
    }

    private List<int> Tokenize(string text)
    {
        // Simple whitespace + punctuation tokenization
        // In production, use a proper WordPiece tokenizer
        var tokens = new List<int>();

        // Add [CLS] token at the start
        if (_vocabulary.TryGetValue(ClsToken, out var clsId))
            tokens.Add(clsId);

        // Tokenize the text
        var words = Regex.Split(text.ToLowerInvariant(), @"(\W+)")
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Take(MaxSequenceLength - 2); // Leave room for [CLS] and [SEP]

        foreach (var word in words)
        {
            if (_vocabulary.Count > 0)
            {
                // Use vocabulary if available
                if (_vocabulary.TryGetValue(word, out var tokenId))
                    tokens.Add(tokenId);
                else if (_vocabulary.TryGetValue(UnkToken, out var unkId))
                    tokens.Add(unkId);
            }
            else
            {
                // Fallback: use hash code as token ID
                tokens.Add(Math.Abs(word.GetHashCode()) % 30000);
            }
        }

        // Add [SEP] token at the end
        if (_vocabulary.TryGetValue(SepToken, out var sepId))
            tokens.Add(sepId);

        return tokens;
    }

    private Tensor<long> CreateInputTensor(List<int> tokens, string name)
    {
        var length = Math.Min(tokens.Count, MaxSequenceLength);
        var paddedLength = MaxSequenceLength;

        var tensorData = new long[1, paddedLength];

        for (int i = 0; i < length; i++)
        {
            tensorData[0, i] = tokens[i];
        }

        // Pad the rest with pad token ID
        var padId = _vocabulary.TryGetValue(PadToken, out var id) ? id : 0;
        for (int i = length; i < paddedLength; i++)
        {
            tensorData[0, i] = padId;
        }

        // Flatten to 1D array and create tensor
        var flatData = new long[paddedLength];
        for (int i = 0; i < paddedLength; i++)
        {
            flatData[i] = tensorData[0, i];
        }
        return new DenseTensor<long>(flatData.AsMemory(), new[] { 1, paddedLength });
    }

    private Tensor<long> CreateAttentionMaskTensor(int actualLength)
    {
        var length = Math.Min(actualLength, MaxSequenceLength);
        var paddedLength = MaxSequenceLength;

        var flatData = new long[paddedLength];

        for (int i = 0; i < length; i++)
        {
            flatData[i] = 1;
        }
        // Rest are already 0

        return new DenseTensor<long>(flatData.AsMemory(), new[] { 1, paddedLength });
    }

    private Tensor<long> CreateTokenTypeIdsTensor(int actualLength)
    {
        var paddedLength = MaxSequenceLength;
        var flatData = new long[paddedLength];

        // All zeros for single sentence (already initialized to 0)
        return new DenseTensor<long>(flatData.AsMemory(), new[] { 1, paddedLength });
    }

    private float[] NormalizeVector(float[] vector)
    {
        // L2 normalization
        var sumOfSquares = vector.Sum(v => v * v);
        var magnitude = MathF.Sqrt(sumOfSquares);

        if (magnitude > 0)
        {
            for (int i = 0; i < vector.Length; i++)
            {
                vector[i] /= magnitude;
            }
        }

        return vector;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _session?.Dispose();
        _semaphore?.Dispose();
        _disposed = true;

        GC.SuppressFinalize(this);
    }
}
