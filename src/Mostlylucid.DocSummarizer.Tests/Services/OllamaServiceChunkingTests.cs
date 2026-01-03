using System.Reflection;
using Xunit;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Config;

namespace Mostlylucid.DocSummarizer.Tests.Services;

/// <summary>
/// Unit tests for OllamaService chunking and vector averaging functionality.
/// Tests the text chunking and embedding averaging logic introduced in v3.0.0.
/// </summary>
public class OllamaServiceChunkingTests
{
    #region SplitTextIntoChunks Tests

    [Fact]
    public void SplitTextIntoChunks_ShortText_ReturnsSingleChunk()
    {
        // Arrange
        var text = "Short text";
        var maxChunkSize = 100;
        var overlap = 10;

        // Act
        var chunks = InvokeSplitTextIntoChunks(text, maxChunkSize, overlap);

        // Assert
        Assert.Single(chunks);
        Assert.Equal(text, chunks[0]);
    }

    [Fact]
    public void SplitTextIntoChunks_ExactlyMaxSize_ReturnsSingleChunk()
    {
        // Arrange
        var text = new string('a', 100);
        var maxChunkSize = 100;
        var overlap = 10;

        // Act
        var chunks = InvokeSplitTextIntoChunks(text, maxChunkSize, overlap);

        // Assert
        Assert.Single(chunks);
        Assert.Equal(100, chunks[0].Length);
    }

    [Fact]
    public void SplitTextIntoChunks_LongText_ReturnsMultipleChunks()
    {
        // Arrange
        var text = new string('a', 250);
        var maxChunkSize = 100;
        var overlap = 10;

        // Act
        var chunks = InvokeSplitTextIntoChunks(text, maxChunkSize, overlap);

        // Assert
        Assert.True(chunks.Count >= 3, $"Expected at least 3 chunks, got {chunks.Count}");
        Assert.All(chunks, c => Assert.True(c.Length <= maxChunkSize, $"Chunk length {c.Length} exceeds max {maxChunkSize}"));
    }

    [Fact]
    public void SplitTextIntoChunks_WithOverlap_ChunksOverlap()
    {
        // Arrange
        var text = "AAAAAAAAAA" + "BBBBBBBBBB" + "CCCCCCCCCC"; // 30 chars
        var maxChunkSize = 15;
        var overlap = 5;

        // Act
        var chunks = InvokeSplitTextIntoChunks(text, maxChunkSize, overlap);

        // Assert
        Assert.True(chunks.Count >= 2);
        
        // Verify overlap: end of chunk[0] should match start of chunk[1]
        if (chunks.Count >= 2)
        {
            var endOfFirst = chunks[0][^overlap..];
            var startOfSecond = chunks[1][..overlap];
            Assert.Equal(endOfFirst, startOfSecond);
        }
    }

    [Fact]
    public void SplitTextIntoChunks_CoversEntireText()
    {
        // Arrange
        var text = "The quick brown fox jumps over the lazy dog. This is a longer sentence to ensure coverage.";
        var maxChunkSize = 30;
        var overlap = 5;

        // Act
        var chunks = InvokeSplitTextIntoChunks(text, maxChunkSize, overlap);

        // Assert
        // Reconstruct text from chunks (accounting for overlap)
        var reconstructed = chunks[0];
        for (var i = 1; i < chunks.Count; i++)
        {
            reconstructed += chunks[i][(overlap)..];
        }
        
        // The reconstructed text should cover the original
        Assert.True(text.Length <= reconstructed.Length, "Chunks should cover entire text");
    }

    [Fact]
    public void SplitTextIntoChunks_ZeroOverlap_NoOverlap()
    {
        // Arrange
        var text = new string('x', 200);
        var maxChunkSize = 100;
        var overlap = 0;

        // Act
        var chunks = InvokeSplitTextIntoChunks(text, maxChunkSize, overlap);

        // Assert
        Assert.Equal(2, chunks.Count);
        Assert.Equal(100, chunks[0].Length);
        Assert.Equal(100, chunks[1].Length);
    }

    #endregion

    #region AverageEmbeddings Tests

    [Fact]
    public void AverageEmbeddings_SingleEmbedding_ReturnsSame()
    {
        // Arrange
        var embedding = new float[] { 1.0f, 2.0f, 3.0f };
        var embeddings = new List<float[]> { embedding };

        // Act
        var result = InvokeAverageEmbeddings(embeddings);

        // Assert
        Assert.Equal(embedding, result);
    }

    [Fact]
    public void AverageEmbeddings_TwoIdenticalEmbeddings_ReturnsNormalized()
    {
        // Arrange
        var embedding1 = new float[] { 1.0f, 0.0f, 0.0f };
        var embedding2 = new float[] { 1.0f, 0.0f, 0.0f };
        var embeddings = new List<float[]> { embedding1, embedding2 };

        // Act
        var result = InvokeAverageEmbeddings(embeddings);

        // Assert
        // Average of [1,0,0] and [1,0,0] = [1,0,0], normalized = [1,0,0]
        Assert.Equal(3, result.Length);
        Assert.Equal(1.0f, result[0], 0.001f);
        Assert.Equal(0.0f, result[1], 0.001f);
        Assert.Equal(0.0f, result[2], 0.001f);
    }

    [Fact]
    public void AverageEmbeddings_TwoDifferentEmbeddings_ReturnsAveragedAndNormalized()
    {
        // Arrange
        var embedding1 = new float[] { 1.0f, 0.0f };
        var embedding2 = new float[] { 0.0f, 1.0f };
        var embeddings = new List<float[]> { embedding1, embedding2 };

        // Act
        var result = InvokeAverageEmbeddings(embeddings);

        // Assert
        // Average = [0.5, 0.5], magnitude = sqrt(0.25 + 0.25) = sqrt(0.5) ≈ 0.707
        // Normalized = [0.5/0.707, 0.5/0.707] ≈ [0.707, 0.707]
        Assert.Equal(2, result.Length);
        var expectedValue = 0.5f / (float)Math.Sqrt(0.5);
        Assert.Equal(expectedValue, result[0], 0.001f);
        Assert.Equal(expectedValue, result[1], 0.001f);
    }

    [Fact]
    public void AverageEmbeddings_ResultIsNormalized()
    {
        // Arrange
        var embedding1 = new float[] { 3.0f, 4.0f, 0.0f };
        var embedding2 = new float[] { 0.0f, 3.0f, 4.0f };
        var embedding3 = new float[] { 4.0f, 0.0f, 3.0f };
        var embeddings = new List<float[]> { embedding1, embedding2, embedding3 };

        // Act
        var result = InvokeAverageEmbeddings(embeddings);

        // Assert
        // Check L2 norm is approximately 1
        var magnitude = Math.Sqrt(result.Sum(x => x * x));
        Assert.Equal(1.0, magnitude, 0.001);
    }

    [Fact]
    public void AverageEmbeddings_EmptyList_ThrowsException()
    {
        // Arrange
        var embeddings = new List<float[]>();

        // Act & Assert
        Assert.Throws<TargetInvocationException>(() => InvokeAverageEmbeddings(embeddings));
    }

    [Fact]
    public void AverageEmbeddings_ManyEmbeddings_ReturnsCorrectDimensions()
    {
        // Arrange
        var embeddings = new List<float[]>();
        const int vectorSize = 768; // Typical embedding size
        for (var i = 0; i < 10; i++)
        {
            var embedding = new float[vectorSize];
            for (var j = 0; j < vectorSize; j++)
            {
                embedding[j] = (float)Random.Shared.NextDouble();
            }
            embeddings.Add(embedding);
        }

        // Act
        var result = InvokeAverageEmbeddings(embeddings);

        // Assert
        Assert.Equal(vectorSize, result.Length);
        
        // Should be normalized
        var magnitude = Math.Sqrt(result.Sum(x => x * x));
        Assert.Equal(1.0, magnitude, 0.001);
    }

    #endregion

    #region NormalizeTextForEmbedding Tests

    [Fact]
    public void NormalizeTextForEmbedding_RemovesExtraWhitespace()
    {
        // Arrange
        var text = "Hello    world\t\ttabs   spaces";

        // Act
        var result = InvokeNormalizeTextForEmbedding(text);

        // Assert
        Assert.DoesNotContain("    ", result);
        Assert.DoesNotContain("\t\t", result);
    }

    [Fact]
    public void NormalizeTextForEmbedding_CollapsesNewlines()
    {
        // Arrange
        var text = "Line1\n\n\n\nLine2";

        // Act
        var result = InvokeNormalizeTextForEmbedding(text);

        // Assert
        Assert.DoesNotContain("\n\n\n", result);
    }

    [Fact]
    public void NormalizeTextForEmbedding_PreservesBasicPunctuation()
    {
        // Arrange
        var text = "Hello, world! How are you?";

        // Act
        var result = InvokeNormalizeTextForEmbedding(text);

        // Assert
        Assert.Contains(",", result);
        Assert.Contains("!", result);
        Assert.Contains("?", result);
    }

    [Fact]
    public void NormalizeTextForEmbedding_TrimsResult()
    {
        // Arrange
        var text = "   trimmed text   ";

        // Act
        var result = InvokeNormalizeTextForEmbedding(text);

        // Assert
        Assert.Equal("trimmed text", result);
    }

    #endregion

    #region GetEmbedContextWindow Tests

    [Fact]
    public void GetEmbedContextWindow_NomicEmbedText_Returns8192()
    {
        // Arrange
        var service = new OllamaService(embedModel: "nomic-embed-text");

        // Act
        var contextWindow = service.GetEmbedContextWindow();

        // Assert
        Assert.Equal(8192, contextWindow);
    }

    [Fact]
    public void GetEmbedContextWindow_SnowflakeArcticEmbed_Returns512()
    {
        // Arrange
        var service = new OllamaService(embedModel: "snowflake-arctic-embed");

        // Act
        var contextWindow = service.GetEmbedContextWindow();

        // Assert
        Assert.Equal(512, contextWindow);
    }

    [Fact]
    public void GetEmbedContextWindow_UnknownModel_ReturnsDefault512()
    {
        // Arrange
        var service = new OllamaService(embedModel: "unknown-model");

        // Act
        var contextWindow = service.GetEmbedContextWindow();

        // Assert
        Assert.Equal(512, contextWindow);
    }

    #endregion

    #region Helper Methods

    private static List<string> InvokeSplitTextIntoChunks(string text, int maxChunkSize, int overlap)
    {
        var method = typeof(OllamaService).GetMethod(
            "SplitTextIntoChunks",
            BindingFlags.NonPublic | BindingFlags.Static);
        
        Assert.NotNull(method);
        
        var result = method.Invoke(null, new object[] { text, maxChunkSize, overlap });
        return (List<string>)result!;
    }

    private static float[] InvokeAverageEmbeddings(List<float[]> embeddings)
    {
        var method = typeof(OllamaService).GetMethod(
            "AverageEmbeddings",
            BindingFlags.NonPublic | BindingFlags.Static);
        
        Assert.NotNull(method);
        
        var result = method.Invoke(null, new object[] { embeddings });
        return (float[])result!;
    }

    private static string InvokeNormalizeTextForEmbedding(string text)
    {
        var method = typeof(OllamaService).GetMethod(
            "NormalizeTextForEmbedding",
            BindingFlags.NonPublic | BindingFlags.Static);
        
        Assert.NotNull(method);
        
        var result = method.Invoke(null, new object[] { text });
        return (string)result!;
    }

    #endregion
}
