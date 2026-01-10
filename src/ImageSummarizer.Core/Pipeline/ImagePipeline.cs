using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Orchestration;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace Mostlylucid.DocSummarizer.Images.Pipeline;

/// <summary>
/// Pipeline implementation for image files (GIF, PNG, JPG, WebP, etc.).
/// Wraps the WaveOrchestrator for OCR and vision model captioning.
/// </summary>
public class ImagePipeline : PipelineBase
{
    private readonly WaveOrchestrator _orchestrator;
    private readonly ILogger<ImagePipeline> _logger;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gif", ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tiff", ".tif"
    };

    public ImagePipeline(
        WaveOrchestrator orchestrator,
        ILogger<ImagePipeline> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <inheritdoc />
    public override string PipelineId => "image";

    /// <inheritdoc />
    public override string Name => "Image Pipeline";

    /// <inheritdoc />
    public override IReadOnlySet<string> SupportedExtensions => ImageExtensions;

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<ContentChunk>> ProcessCoreAsync(
        string filePath,
        PipelineOptions options,
        IProgress<PipelineProgress>? progress,
        CancellationToken ct)
    {
        _logger.LogInformation("Processing image: {FilePath}", filePath);

        progress?.Report(new PipelineProgress("Analyzing", "Running analysis waves", 10));

        // Run the wave orchestrator to analyze the image
        var profile = await _orchestrator.AnalyzeAsync(filePath, ct);

        progress?.Report(new PipelineProgress("Extracting", "Building content chunks", 80));

        var chunks = new List<ContentChunk>();
        var chunkIndex = 0;

        // Extract OCR text if available
        var ocrText = profile.GetValue<string>(ImageSignalKeys.OcrText);
        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            var ocrConfidence = profile.GetValue<double?>(ImageSignalKeys.OcrConfidence);
            chunks.Add(new ContentChunk
            {
                Id = GenerateChunkId(filePath, chunkIndex++),
                Text = ocrText,
                ContentType = ContentType.ImageOcr,
                SourcePath = filePath,
                Index = chunkIndex - 1,
                ContentHash = ComputeHash(ocrText),
                Confidence = ocrConfidence,
                Metadata = new Dictionary<string, object?>
                {
                    ["source"] = "ocr",
                    ["wordCount"] = profile.GetValue<int?>(ImageSignalKeys.OcrWordCount),
                    ["language"] = profile.GetValue<string>(ImageSignalKeys.OcrLanguage)
                }
            });
        }

        // Extract caption if available
        var caption = profile.GetValue<string>(ImageSignalKeys.Caption);
        if (!string.IsNullOrWhiteSpace(caption))
        {
            var visionConfidence = profile.GetValue<double?>(ImageSignalKeys.VisionConfidence);
            chunks.Add(new ContentChunk
            {
                Id = GenerateChunkId(filePath, chunkIndex++),
                Text = caption,
                ContentType = ContentType.ImageCaption,
                SourcePath = filePath,
                Index = chunkIndex - 1,
                ContentHash = ComputeHash(caption),
                Confidence = visionConfidence,
                Metadata = new Dictionary<string, object?>
                {
                    ["source"] = "vision",
                    ["objects"] = profile.GetValue<object>(ImageSignalKeys.Objects)
                }
            });
        }

        // Extract entities if available
        var entities = profile.GetValue<IReadOnlyList<string>>(ImageSignalKeys.Entities);
        if (entities != null && entities.Count > 0)
        {
            var entityText = string.Join(", ", entities);
            chunks.Add(new ContentChunk
            {
                Id = GenerateChunkId(filePath, chunkIndex++),
                Text = $"Entities: {entityText}",
                ContentType = ContentType.Entity,
                SourcePath = filePath,
                Index = chunkIndex - 1,
                ContentHash = ComputeHash(entityText),
                Metadata = new Dictionary<string, object?>
                {
                    ["source"] = "entities",
                    ["entityCount"] = entities.Count
                }
            });
        }

        // Add image metadata as a chunk if no text was extracted
        if (chunks.Count == 0)
        {
            var metadataText = BuildMetadataText(filePath, profile);
            chunks.Add(new ContentChunk
            {
                Id = GenerateChunkId(filePath, chunkIndex++),
                Text = metadataText,
                ContentType = ContentType.Summary,
                SourcePath = filePath,
                Index = chunkIndex - 1,
                ContentHash = ComputeHash(metadataText),
                Metadata = new Dictionary<string, object?>
                {
                    ["source"] = "metadata",
                    ["width"] = profile.GetValue<int?>(ImageSignalKeys.ImageWidth),
                    ["height"] = profile.GetValue<int?>(ImageSignalKeys.ImageHeight),
                    ["format"] = profile.GetValue<string>(ImageSignalKeys.ImageFormat)
                }
            });
        }

        _logger.LogInformation("Processed {ChunkCount} chunks from image", chunks.Count);

        return chunks;
    }

    private static string BuildMetadataText(string filePath, Models.Dynamic.DynamicImageProfile profile)
    {
        var width = profile.GetValue<int?>(ImageSignalKeys.ImageWidth);
        var height = profile.GetValue<int?>(ImageSignalKeys.ImageHeight);
        var format = profile.GetValue<string>(ImageSignalKeys.ImageFormat) ?? Path.GetExtension(filePath);

        return $"Image: {Path.GetFileName(filePath)}, Format: {format}, Dimensions: {width}x{height}";
    }
}
