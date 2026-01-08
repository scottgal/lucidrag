using Mostlylucid.DataSummarizer.Configuration;

namespace Mostlylucid.DataSummarizer.Services.Onnx;

/// <summary>
/// Registry of available ONNX models with download URLs and metadata
/// </summary>
public static class OnnxModelRegistry
{
    /// <summary>
    /// Get embedding model info
    /// </summary>
    public static EmbeddingModelInfo GetEmbeddingModel(OnnxEmbeddingModel model, bool quantized = true)
    {
        return model switch
        {
            OnnxEmbeddingModel.AllMiniLmL6V2 => new EmbeddingModelInfo
            {
                Name = "all-MiniLM-L6-v2",
                HuggingFaceRepo = "Xenova/all-MiniLM-L6-v2",
                ModelFile = quantized ? "onnx/model_quantized.onnx" : "onnx/model.onnx",
                TokenizerFile = "tokenizer.json",
                VocabFile = "vocab.txt",
                EmbeddingDimension = 384,
                MaxSequenceLength = 256,
                SizeBytes = quantized ? 23_000_000 : 90_000_000,
                RequiresInstruction = false
            },
            OnnxEmbeddingModel.BgeSmallEnV15 => new EmbeddingModelInfo
            {
                Name = "bge-small-en-v1.5",
                HuggingFaceRepo = "Xenova/bge-small-en-v1.5",
                ModelFile = quantized ? "onnx/model_quantized.onnx" : "onnx/model.onnx",
                TokenizerFile = "tokenizer.json",
                VocabFile = "vocab.txt",
                EmbeddingDimension = 384,
                MaxSequenceLength = 512,
                SizeBytes = quantized ? 34_000_000 : 133_000_000,
                RequiresInstruction = true,
                QueryInstruction = "Represent this sentence for searching relevant passages: "
            },
            OnnxEmbeddingModel.GteSmall => new EmbeddingModelInfo
            {
                Name = "gte-small",
                HuggingFaceRepo = "Xenova/gte-small",
                ModelFile = quantized ? "onnx/model_quantized.onnx" : "onnx/model.onnx",
                TokenizerFile = "tokenizer.json",
                VocabFile = "vocab.txt",
                EmbeddingDimension = 384,
                MaxSequenceLength = 512,
                SizeBytes = quantized ? 34_000_000 : 133_000_000,
                RequiresInstruction = false
            },
            OnnxEmbeddingModel.MultiQaMiniLm => new EmbeddingModelInfo
            {
                Name = "multi-qa-MiniLM-L6-cos-v1",
                HuggingFaceRepo = "Xenova/multi-qa-MiniLM-L6-cos-v1",
                ModelFile = quantized ? "onnx/model_quantized.onnx" : "onnx/model.onnx",
                TokenizerFile = "tokenizer.json",
                VocabFile = "vocab.txt",
                EmbeddingDimension = 384,
                MaxSequenceLength = 512,
                SizeBytes = quantized ? 23_000_000 : 90_000_000,
                RequiresInstruction = false
            },
            OnnxEmbeddingModel.ParaphraseMiniLmL3 => new EmbeddingModelInfo
            {
                Name = "paraphrase-MiniLM-L3-v2",
                HuggingFaceRepo = "Xenova/paraphrase-MiniLM-L3-v2",
                ModelFile = quantized ? "onnx/model_quantized.onnx" : "onnx/model.onnx",
                TokenizerFile = "tokenizer.json",
                VocabFile = "vocab.txt",
                EmbeddingDimension = 384,
                MaxSequenceLength = 128,
                SizeBytes = quantized ? 17_000_000 : 69_000_000,
                RequiresInstruction = false
            },
            _ => throw new ArgumentOutOfRangeException(nameof(model))
        };
    }

    /// <summary>
    /// Build HuggingFace download URL
    /// </summary>
    public static string GetDownloadUrl(string repo, string file)
    {
        return $"https://huggingface.co/{repo}/resolve/main/{file}";
    }
}

/// <summary>
/// Embedding model metadata
/// </summary>
public class EmbeddingModelInfo
{
    public required string Name { get; init; }
    public required string HuggingFaceRepo { get; init; }
    public required string ModelFile { get; init; }
    public required string TokenizerFile { get; init; }
    public required string VocabFile { get; init; }
    public required int EmbeddingDimension { get; init; }
    public required int MaxSequenceLength { get; init; }
    public required long SizeBytes { get; init; }
    public bool RequiresInstruction { get; init; }
    public string? QueryInstruction { get; init; }
    
    public string GetModelUrl() => OnnxModelRegistry.GetDownloadUrl(HuggingFaceRepo, ModelFile);
    public string GetTokenizerUrl() => OnnxModelRegistry.GetDownloadUrl(HuggingFaceRepo, TokenizerFile);
    public string GetVocabUrl() => OnnxModelRegistry.GetDownloadUrl(HuggingFaceRepo, VocabFile);
}
