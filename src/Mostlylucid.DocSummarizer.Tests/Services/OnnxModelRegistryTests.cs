using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services.Onnx;
using Xunit;

namespace Mostlylucid.DocSummarizer.Tests.Services;

/// <summary>
/// Tests for OnnxModelRegistry - validates model metadata
/// </summary>
public class OnnxModelRegistryTests
{
    [Theory]
    [InlineData(OnnxEmbeddingModel.AllMiniLmL6V2, 384)]
    [InlineData(OnnxEmbeddingModel.BgeSmallEnV15, 384)]
    [InlineData(OnnxEmbeddingModel.GteSmall, 384)]
    [InlineData(OnnxEmbeddingModel.MultiQaMiniLm, 384)]
    [InlineData(OnnxEmbeddingModel.ParaphraseMiniLmL3, 384)]
    public void GetEmbeddingModel_ReturnsCorrectDimension(OnnxEmbeddingModel model, int expectedDimension)
    {
        // Act
        var modelInfo = OnnxModelRegistry.GetEmbeddingModel(model, quantized: true);

        // Assert
        Assert.Equal(expectedDimension, modelInfo.EmbeddingDimension);
    }

    [Theory]
    [InlineData(OnnxEmbeddingModel.AllMiniLmL6V2)]
    [InlineData(OnnxEmbeddingModel.BgeSmallEnV15)]
    [InlineData(OnnxEmbeddingModel.GteSmall)]
    [InlineData(OnnxEmbeddingModel.MultiQaMiniLm)]
    [InlineData(OnnxEmbeddingModel.ParaphraseMiniLmL3)]
    public void GetEmbeddingModel_HasValidModelUrl(OnnxEmbeddingModel model)
    {
        // Act
        var modelInfo = OnnxModelRegistry.GetEmbeddingModel(model, quantized: true);
        var modelUrl = modelInfo.GetModelUrl();

        // Assert
        Assert.NotNull(modelUrl);
        Assert.StartsWith("https://huggingface.co/", modelUrl);
        Assert.Contains("/resolve/main/", modelUrl);
    }

    [Theory]
    [InlineData(OnnxEmbeddingModel.AllMiniLmL6V2)]
    [InlineData(OnnxEmbeddingModel.BgeSmallEnV15)]
    [InlineData(OnnxEmbeddingModel.GteSmall)]
    [InlineData(OnnxEmbeddingModel.MultiQaMiniLm)]
    [InlineData(OnnxEmbeddingModel.ParaphraseMiniLmL3)]
    public void GetEmbeddingModel_HasValidVocabUrl(OnnxEmbeddingModel model)
    {
        // Act
        var modelInfo = OnnxModelRegistry.GetEmbeddingModel(model, quantized: true);
        var vocabUrl = modelInfo.GetVocabUrl();

        // Assert
        Assert.NotNull(vocabUrl);
        Assert.StartsWith("https://huggingface.co/", vocabUrl);
        Assert.Contains("vocab.txt", vocabUrl);
    }

    [Theory]
    [InlineData(OnnxEmbeddingModel.AllMiniLmL6V2)]
    [InlineData(OnnxEmbeddingModel.BgeSmallEnV15)]
    [InlineData(OnnxEmbeddingModel.GteSmall)]
    [InlineData(OnnxEmbeddingModel.MultiQaMiniLm)]
    [InlineData(OnnxEmbeddingModel.ParaphraseMiniLmL3)]
    public void GetEmbeddingModel_HasPositiveMaxSequenceLength(OnnxEmbeddingModel model)
    {
        // Act
        var modelInfo = OnnxModelRegistry.GetEmbeddingModel(model, quantized: true);

        // Assert
        Assert.True(modelInfo.MaxSequenceLength > 0);
        Assert.True(modelInfo.MaxSequenceLength <= 512); // All current models support up to 512
    }

    [Theory]
    [InlineData(OnnxEmbeddingModel.AllMiniLmL6V2)]
    [InlineData(OnnxEmbeddingModel.BgeSmallEnV15)]
    [InlineData(OnnxEmbeddingModel.GteSmall)]
    [InlineData(OnnxEmbeddingModel.MultiQaMiniLm)]
    [InlineData(OnnxEmbeddingModel.ParaphraseMiniLmL3)]
    public void GetEmbeddingModel_HasNonEmptyName(OnnxEmbeddingModel model)
    {
        // Act
        var modelInfo = OnnxModelRegistry.GetEmbeddingModel(model, quantized: true);

        // Assert
        Assert.NotNull(modelInfo.Name);
        Assert.NotEmpty(modelInfo.Name);
    }

    [Fact]
    public void GetEmbeddingModel_Quantized_HasQuantizedInModelFile()
    {
        // Act
        var modelInfo = OnnxModelRegistry.GetEmbeddingModel(OnnxEmbeddingModel.AllMiniLmL6V2, quantized: true);

        // Assert - quantized models have "quantized" in file path
        Assert.Contains("quantized", modelInfo.ModelFile);
    }

    [Fact]
    public void GetEmbeddingModel_NotQuantized_DoesNotHaveQuantizedInModelFile()
    {
        // Act
        var modelInfo = OnnxModelRegistry.GetEmbeddingModel(OnnxEmbeddingModel.AllMiniLmL6V2, quantized: false);

        // Assert
        Assert.DoesNotContain("quantized", modelInfo.ModelFile);
    }

    [Theory]
    [InlineData(OnnxEmbeddingModel.AllMiniLmL6V2, 256)]
    [InlineData(OnnxEmbeddingModel.BgeSmallEnV15, 512)]
    [InlineData(OnnxEmbeddingModel.GteSmall, 512)]
    [InlineData(OnnxEmbeddingModel.MultiQaMiniLm, 512)]
    [InlineData(OnnxEmbeddingModel.ParaphraseMiniLmL3, 128)]
    public void GetEmbeddingModel_HasExpectedSequenceLength(OnnxEmbeddingModel model, int expectedMaxSeq)
    {
        // Act
        var modelInfo = OnnxModelRegistry.GetEmbeddingModel(model, quantized: true);

        // Assert
        Assert.Equal(expectedMaxSeq, modelInfo.MaxSequenceLength);
    }

    [Theory]
    [InlineData(OnnxEmbeddingModel.AllMiniLmL6V2)]
    [InlineData(OnnxEmbeddingModel.BgeSmallEnV15)]
    [InlineData(OnnxEmbeddingModel.GteSmall)]
    [InlineData(OnnxEmbeddingModel.MultiQaMiniLm)]
    [InlineData(OnnxEmbeddingModel.ParaphraseMiniLmL3)]
    public void GetEmbeddingModel_HasValidHuggingFaceRepo(OnnxEmbeddingModel model)
    {
        // Act
        var modelInfo = OnnxModelRegistry.GetEmbeddingModel(model, quantized: true);

        // Assert
        Assert.NotNull(modelInfo.HuggingFaceRepo);
        Assert.Contains("/", modelInfo.HuggingFaceRepo); // format: owner/repo
        Assert.StartsWith("Xenova/", modelInfo.HuggingFaceRepo);
    }

    [Fact]
    public void GetEmbeddingModel_BgeSmall_RequiresInstruction()
    {
        // Act
        var modelInfo = OnnxModelRegistry.GetEmbeddingModel(OnnxEmbeddingModel.BgeSmallEnV15, quantized: true);

        // Assert
        Assert.True(modelInfo.RequiresInstruction);
        Assert.NotNull(modelInfo.QueryInstruction);
        Assert.NotEmpty(modelInfo.QueryInstruction);
    }

    [Theory]
    [InlineData(OnnxEmbeddingModel.AllMiniLmL6V2)]
    [InlineData(OnnxEmbeddingModel.GteSmall)]
    [InlineData(OnnxEmbeddingModel.MultiQaMiniLm)]
    [InlineData(OnnxEmbeddingModel.ParaphraseMiniLmL3)]
    public void GetEmbeddingModel_NonBgeModels_DoNotRequireInstruction(OnnxEmbeddingModel model)
    {
        // Act
        var modelInfo = OnnxModelRegistry.GetEmbeddingModel(model, quantized: true);

        // Assert
        Assert.False(modelInfo.RequiresInstruction);
    }

    [Fact]
    public void GetDownloadUrl_FormatsCorrectly()
    {
        // Act
        var url = OnnxModelRegistry.GetDownloadUrl("Xenova/all-MiniLM-L6-v2", "onnx/model.onnx");

        // Assert
        Assert.Equal("https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx", url);
    }

    [Theory]
    [InlineData(OnnxEmbeddingModel.AllMiniLmL6V2)]
    [InlineData(OnnxEmbeddingModel.BgeSmallEnV15)]
    [InlineData(OnnxEmbeddingModel.GteSmall)]
    [InlineData(OnnxEmbeddingModel.MultiQaMiniLm)]
    [InlineData(OnnxEmbeddingModel.ParaphraseMiniLmL3)]
    public void GetEmbeddingModel_Quantized_HasSmallerSize(OnnxEmbeddingModel model)
    {
        // Act
        var quantized = OnnxModelRegistry.GetEmbeddingModel(model, quantized: true);
        var notQuantized = OnnxModelRegistry.GetEmbeddingModel(model, quantized: false);

        // Assert
        Assert.True(quantized.SizeBytes < notQuantized.SizeBytes,
            "Quantized model should be smaller than non-quantized");
    }
}
