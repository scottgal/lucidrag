using Mostlylucid.DataSummarizer.Configuration;
using Mostlylucid.DataSummarizer.Services;
using Mostlylucid.DataSummarizer.Services.Onnx;
using Xunit;

namespace Mostlylucid.DataSummarizer.Tests;

/// <summary>
/// Tests for embedding services (Hash and ONNX)
/// </summary>
public class EmbeddingTests
{
    #region HashEmbeddingService Tests

    [Fact]
    public async Task HashEmbedding_ReturnsCorrectDimension()
    {
        var service = new HashEmbeddingService();
        await service.InitializeAsync();

        Assert.Equal(128, service.EmbeddingDimension);
    }

    [Fact]
    public async Task HashEmbedding_EmbedText_ReturnsNonZeroVector()
    {
        var service = new HashEmbeddingService();
        await service.InitializeAsync();

        var embedding = await service.EmbedAsync("Hello world");

        Assert.Equal(128, embedding.Length);
        Assert.True(embedding.Any(v => v != 0), "Embedding should have non-zero values");
    }

    [Fact]
    public async Task HashEmbedding_EmptyText_ReturnsZeroVector()
    {
        var service = new HashEmbeddingService();
        await service.InitializeAsync();

        var embedding = await service.EmbedAsync("");

        Assert.Equal(128, embedding.Length);
        Assert.True(embedding.All(v => v == 0), "Empty text should produce zero vector");
    }

    [Fact]
    public async Task HashEmbedding_SimilarTexts_HaveSimilarEmbeddings()
    {
        var service = new HashEmbeddingService();
        await service.InitializeAsync();

        var emb1 = await service.EmbedAsync("The quick brown fox jumps over the lazy dog");
        var emb2 = await service.EmbedAsync("The quick brown fox leaps over the lazy dog");
        var emb3 = await service.EmbedAsync("Completely different unrelated text about programming");

        var sim12 = CosineSimilarity(emb1, emb2);
        var sim13 = CosineSimilarity(emb1, emb3);

        Assert.True(sim12 > sim13, "Similar texts should have higher similarity than different texts");
    }

    [Fact]
    public async Task HashEmbedding_BatchEmbedding_Works()
    {
        var service = new HashEmbeddingService();
        await service.InitializeAsync();

        var texts = new[] { "Hello", "World", "Test", "Embedding" };
        var embeddings = await service.EmbedBatchAsync(texts);

        Assert.Equal(4, embeddings.Length);
        Assert.All(embeddings, e => Assert.Equal(128, e.Length));
    }

    [Fact]
    public async Task HashEmbedding_IsNormalized()
    {
        var service = new HashEmbeddingService();
        await service.InitializeAsync();

        var embedding = await service.EmbedAsync("Test normalization");

        var norm = MathF.Sqrt(embedding.Sum(v => v * v));
        Assert.True(Math.Abs(norm - 1.0) < 0.01, $"Vector should be normalized (norm={norm})");
    }

    [Fact]
    public async Task HashEmbedding_DeterministicOutput()
    {
        var service = new HashEmbeddingService();
        await service.InitializeAsync();

        var emb1 = await service.EmbedAsync("Deterministic test");
        var emb2 = await service.EmbedAsync("Deterministic test");

        Assert.Equal(emb1, emb2);
    }

    [Fact]
    public async Task HashEmbedding_CaseInsensitive()
    {
        var service = new HashEmbeddingService();
        await service.InitializeAsync();

        var emb1 = await service.EmbedAsync("HELLO WORLD");
        var emb2 = await service.EmbedAsync("hello world");

        Assert.Equal(emb1, emb2);
    }

    #endregion

    #region EmbeddingServiceFactory Tests

    [Fact]
    public async Task Factory_NullConfig_ReturnsHashService()
    {
        EmbeddingServiceFactory.Reset();

        var service = await EmbeddingServiceFactory.GetOrCreateAsync(config: null);

        Assert.IsType<HashEmbeddingService>(service);
        Assert.Equal(128, service.EmbeddingDimension);

        EmbeddingServiceFactory.Reset();
    }

    [Fact]
    public async Task Factory_DisabledOnnx_ReturnsHashService()
    {
        EmbeddingServiceFactory.Reset();

        var config = new OnnxConfig { Enabled = false };
        var service = await EmbeddingServiceFactory.GetOrCreateAsync(config);

        Assert.IsType<HashEmbeddingService>(service);

        EmbeddingServiceFactory.Reset();
    }

    [Fact]
    public async Task Factory_ReturnsSameInstance()
    {
        EmbeddingServiceFactory.Reset();

        var service1 = await EmbeddingServiceFactory.GetOrCreateAsync(config: null);
        var service2 = await EmbeddingServiceFactory.GetOrCreateAsync(config: null);

        Assert.Same(service1, service2);

        EmbeddingServiceFactory.Reset();
    }

    [Fact]
    public async Task Factory_CreateAsync_ReturnsNewInstance()
    {
        var service1 = await EmbeddingServiceFactory.CreateAsync(useOnnx: false);
        var service2 = await EmbeddingServiceFactory.CreateAsync(useOnnx: false);

        Assert.NotSame(service1, service2);
    }

    [Fact]
    public void Factory_Reset_ClearsInstance()
    {
        // This should not throw
        EmbeddingServiceFactory.Reset();
        EmbeddingServiceFactory.Reset(); // Double reset should be safe
    }

    #endregion

    #region OnnxModelRegistry Tests

    [Fact]
    public void Registry_AllMiniLm_ReturnsCorrectInfo()
    {
        var info = OnnxModelRegistry.GetEmbeddingModel(OnnxEmbeddingModel.AllMiniLmL6V2, quantized: true);

        Assert.Equal("all-MiniLM-L6-v2", info.Name);
        Assert.Equal(384, info.EmbeddingDimension);
        Assert.Equal(256, info.MaxSequenceLength);
        Assert.False(info.RequiresInstruction);
        Assert.Contains("Xenova", info.HuggingFaceRepo);
    }

    [Fact]
    public void Registry_BgeSmall_RequiresInstruction()
    {
        var info = OnnxModelRegistry.GetEmbeddingModel(OnnxEmbeddingModel.BgeSmallEnV15);

        Assert.True(info.RequiresInstruction);
        Assert.NotNull(info.QueryInstruction);
        Assert.NotEmpty(info.QueryInstruction);
    }

    [Fact]
    public void Registry_AllModels_HaveValidInfo()
    {
        foreach (OnnxEmbeddingModel model in Enum.GetValues<OnnxEmbeddingModel>())
        {
            var info = OnnxModelRegistry.GetEmbeddingModel(model);

            Assert.NotEmpty(info.Name);
            Assert.NotEmpty(info.HuggingFaceRepo);
            Assert.NotEmpty(info.ModelFile);
            Assert.NotEmpty(info.TokenizerFile);
            Assert.True(info.EmbeddingDimension > 0);
            Assert.True(info.MaxSequenceLength > 0);
            Assert.True(info.SizeBytes > 0);
        }
    }

    [Fact]
    public void Registry_QuantizedVsNonQuantized_DifferentSizes()
    {
        var quantized = OnnxModelRegistry.GetEmbeddingModel(OnnxEmbeddingModel.AllMiniLmL6V2, quantized: true);
        var nonQuantized = OnnxModelRegistry.GetEmbeddingModel(OnnxEmbeddingModel.AllMiniLmL6V2, quantized: false);

        Assert.True(quantized.SizeBytes < nonQuantized.SizeBytes, "Quantized should be smaller");
    }

    [Fact]
    public void Registry_GetDownloadUrl_FormatsCorrectly()
    {
        var url = OnnxModelRegistry.GetDownloadUrl("Xenova/all-MiniLM-L6-v2", "onnx/model.onnx");

        Assert.Equal("https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx", url);
    }

    [Fact]
    public void Registry_ModelInfo_GeneratesCorrectUrls()
    {
        var info = OnnxModelRegistry.GetEmbeddingModel(OnnxEmbeddingModel.AllMiniLmL6V2);

        var modelUrl = info.GetModelUrl();
        var tokenizerUrl = info.GetTokenizerUrl();
        var vocabUrl = info.GetVocabUrl();

        Assert.Contains("huggingface.co", modelUrl);
        Assert.Contains("huggingface.co", tokenizerUrl);
        Assert.Contains("huggingface.co", vocabUrl);
        Assert.Contains("model", modelUrl);
        Assert.Contains("tokenizer.json", tokenizerUrl);
        Assert.Contains("vocab.txt", vocabUrl);
    }

    #endregion

    #region OnnxConfig Tests

    [Fact]
    public void OnnxConfig_DefaultValues_AreCorrect()
    {
        var config = new OnnxConfig();

        Assert.True(config.Enabled);
        Assert.Equal(OnnxEmbeddingModel.AllMiniLmL6V2, config.EmbeddingModel);
        Assert.True(config.UseQuantized);
        Assert.Equal(256, config.MaxEmbeddingSequenceLength);
        Assert.Equal(0, config.InferenceThreads); // Auto
        Assert.True(config.UseParallelExecution);
        Assert.Equal(OnnxExecutionProvider.Auto, config.ExecutionProvider);
        Assert.Equal(16, config.EmbeddingBatchSize);
    }

    [Fact]
    public void OnnxConfig_ModelDirectory_HasDefaultValue()
    {
        var config = new OnnxConfig();

        Assert.NotEmpty(config.ModelDirectory);
        Assert.Contains("models", config.ModelDirectory);
    }

    #endregion

    #region Helper Methods

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        if (na == 0 || nb == 0) return 0;
        return dot / (MathF.Sqrt(na) * MathF.Sqrt(nb));
    }

    #endregion
}
