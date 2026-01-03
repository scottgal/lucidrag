using Xunit;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Tests.Config;

/// <summary>
/// Tests for DocSummarizerConfig defaults ensuring safe configuration
/// </summary>
public class DocSummarizerConfigTests
{
    [Fact]
    public void DocSummarizerConfig_AllSectionsInitialized()
    {
        // Arrange & Act
        var config = new DocSummarizerConfig();

        // Assert - all config sections should be non-null
        Assert.NotNull(config.Onnx);
        Assert.NotNull(config.Ollama);
        Assert.NotNull(config.Bert);
        Assert.NotNull(config.Docling);
        Assert.NotNull(config.Qdrant);
        Assert.NotNull(config.Processing);
        Assert.NotNull(config.Output);
        Assert.NotNull(config.WebFetch);
        Assert.NotNull(config.Batch);
        Assert.NotNull(config.Embedding);
    }

    [Fact]
    public void DocSummarizerConfig_EmbeddingBackend_DefaultsToOnnx()
    {
        // Arrange & Act
        var config = new DocSummarizerConfig();

        // Assert - ONNX is the safe default (no external dependencies)
        Assert.Equal(EmbeddingBackend.Onnx, config.EmbeddingBackend);
    }

    [Fact]
    public void OllamaConfig_DefaultModel_IsLlama32_3b()
    {
        // Arrange & Act
        var config = new OllamaConfig();

        // Assert - good balance of speed/quality
        Assert.Equal("llama3.2:3b", config.Model);
    }

    [Fact]
    public void OllamaConfig_DefaultTemperature_Is0Point3()
    {
        // Arrange & Act
        var config = new OllamaConfig();

        // Assert - low temperature for deterministic summaries
        Assert.Equal(0.3, config.Temperature);
    }

    [Fact]
    public void OllamaConfig_DefaultTimeout_Is1200Seconds()
    {
        // Arrange & Act
        var config = new OllamaConfig();

        // Assert - 20 minutes for large documents
        Assert.Equal(1200, config.TimeoutSeconds);
    }

    [Fact]
    public void OllamaConfig_ClassifierModel_DefaultsToTinyllama()
    {
        // Arrange & Act
        var config = new OllamaConfig();

        // Assert - tiny model for fast classification
        Assert.Equal("tinyllama", config.ClassifierModel);
    }

    [Fact]
    public void WebFetchConfig_DefaultEnabled_IsFalse()
    {
        // Arrange & Act
        var config = new WebFetchConfig();

        // Assert - web fetching disabled by default for security
        Assert.False(config.Enabled);
    }

    [Fact]
    public void WebFetchConfig_DefaultMode_IsSimple()
    {
        // Arrange & Act
        var config = new WebFetchConfig();

        // Assert - simple HTTP (faster, no browser required)
        Assert.Equal(WebFetchMode.Simple, config.Mode);
    }

    [Fact]
    public void WebFetchConfig_DefaultTimeout_Is30Seconds()
    {
        // Arrange & Act
        var config = new WebFetchConfig();

        // Assert - reasonable timeout for web requests
        Assert.Equal(30, config.TimeoutSeconds);
    }

    [Fact]
    public void ProcessingConfig_DefaultMaxLlmParallelism_Is2()
    {
        // Arrange & Act
        var config = new ProcessingConfig();

        // Assert - conservative parallelism to avoid overwhelming Ollama
        Assert.Equal(2, config.MaxLlmParallelism);
    }

    [Fact]
    public void ProcessingConfig_DefaultTargetChunkTokens_Is1500()
    {
        // Arrange & Act
        var config = new ProcessingConfig();

        // Assert - safe chunk size that fits most models
        Assert.Equal(1500, config.TargetChunkTokens);
    }

    [Fact]
    public void ProcessingConfig_DefaultMinChunkTokens_Is200()
    {
        // Arrange & Act
        var config = new ProcessingConfig();

        // Assert - minimum meaningful chunk size
        Assert.Equal(200, config.MinChunkTokens);
    }

    [Fact]
    public void EmbeddingConfig_DefaultMaxRetries_Is5()
    {
        // Arrange & Act
        var config = new EmbeddingConfig();

        // Assert - reasonable retry count
        Assert.Equal(5, config.MaxRetries);
    }

    [Fact]
    public void EmbeddingConfig_DefaultCircuitBreakerEnabled_IsTrue()
    {
        // Arrange & Act
        var config = new EmbeddingConfig();

        // Assert - circuit breaker enabled for resilience
        Assert.True(config.EnableCircuitBreaker);
    }

    [Fact]
    public void BatchConfig_DefaultContinueOnError_IsTrue()
    {
        // Arrange & Act
        var config = new BatchConfig();

        // Assert - don't fail entire batch on single error
        Assert.True(config.ContinueOnError);
    }

    [Fact]
    public void BatchConfig_DefaultRecursive_IsFalse()
    {
        // Arrange & Act
        var config = new BatchConfig();

        // Assert - explicit opt-in for recursive
        Assert.False(config.Recursive);
    }

    [Fact]
    public void OutputConfig_DefaultVerbose_IsFalse()
    {
        // Arrange & Act
        var config = new OutputConfig();

        // Assert - quiet by default
        Assert.False(config.Verbose);
    }

    [Fact]
    public void OutputConfig_DefaultFormat_IsConsole()
    {
        // Arrange & Act
        var config = new OutputConfig();

        // Assert - console output for interactive use
        Assert.Equal(OutputFormat.Console, config.Format);
    }

    [Fact]
    public void QdrantConfig_DefaultDeleteAfterSummarization_IsTrue()
    {
        // Arrange & Act
        var config = new QdrantConfig();

        // Assert - clean up temporary collections
        Assert.True(config.DeleteCollectionAfterSummarization);
    }

    [Fact]
    public void MemoryConfig_DefaultEnableDiskStorage_IsTrue()
    {
        // Arrange & Act
        var config = new MemoryConfig();

        // Assert - memory safety for large docs
        Assert.True(config.EnableDiskStorage);
    }

    [Fact]
    public void SummaryLengthConfig_DefaultMinWordsForSummary_Is150()
    {
        // Arrange & Act
        var config = new SummaryLengthConfig();

        // Assert - don't summarize very short docs
        Assert.Equal(150, config.MinWordsForSummary);
    }
}
