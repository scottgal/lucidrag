using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Orchestration;
using StyloFlow.Retrieval.Entities;
// Use explicit namespace for Signal to avoid conflict with Orchestration.Signal
using StyloSignal = StyloFlow.Retrieval.Analysis.Signal;
using StyloSignalTags = StyloFlow.Retrieval.Analysis.SignalTags;

namespace Mostlylucid.DocSummarizer.Images.Entities;

/// <summary>
/// Extracts RetrievalEntity from image analysis results.
/// Bridges DocSummarizer.Images to the unified cross-modal entity model.
/// </summary>
public class ImageEntityExtractor : IEntityExtractor
{
    private readonly ImageAnalysisOrchestrator _orchestrator;

    public ContentType SupportedContentType => ContentType.Image;

    public ImageEntityExtractor(ImageAnalysisOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    /// <summary>
    /// Extract a RetrievalEntity from an image.
    /// </summary>
    public async Task<RetrievalEntity> ExtractAsync(
        string contentPath,
        StyloFlow.Retrieval.Analysis.AnalysisContext? context = null,
        string? collection = null,
        CancellationToken ct = default)
    {
        // Run full image analysis pipeline
        var analysisResult = await _orchestrator.AnalyzeAsync(contentPath, ct);

        // Convert to unified entity
        return BuildEntity(contentPath, analysisResult, collection);
    }

    /// <summary>
    /// Build entity from ImageAnalysisResult (full signal-based analysis).
    /// </summary>
    public RetrievalEntity BuildEntity(
        string imagePath,
        ImageAnalysisResult analysisResult,
        string? collection = null)
    {
        var builder = new EntityBuilder()
            .WithContentType(ContentType.Image)
            .WithSource(imagePath)
            .WithTitle(Path.GetFileName(imagePath));

        if (collection != null)
            builder.WithCollection(collection);

        // Add content hash
        if (analysisResult.Signals.TryGetValue("identity.sha256", out var hash))
            builder.WithContentHash(hash?.ToString() ?? "");

        // Build text content from captions and OCR
        var textParts = new List<string>();

        if (analysisResult.Signals.TryGetValue("vision.caption", out var caption) && caption != null)
            textParts.Add(caption.ToString()!);

        if (analysisResult.Signals.TryGetValue("vision.detailed_caption", out var detailed) && detailed != null)
            textParts.Add(detailed.ToString()!);

        if (analysisResult.Signals.TryGetValue("ocr.text", out var ocrText) && ocrText != null)
            textParts.Add(ocrText.ToString()!);

        if (textParts.Count > 0)
            builder.WithTextContent(string.Join("\n\n", textParts));

        // Set summary
        if (caption != null)
            builder.WithSummary(caption.ToString()!);

        // Add CLIP embedding if available
        if (analysisResult.Signals.TryGetValue("vision.clip.embedding", out var clipEmbedding) && clipEmbedding is float[] embedding)
            builder.WithAdditionalEmbedding("clip_visual", embedding);

        // Convert signals to StyloFlow.Retrieval format
        foreach (var (key, value) in analysisResult.Signals)
        {
            var signal = new StyloSignal
            {
                Key = key,
                Value = value,
                Confidence = 1.0,
                Source = "ImageAnalyzer",
                Tags = GetTagsForSignal(key)
            };
            builder.WithSignal(signal);
        }

        // Extract entities from signals
        ExtractEntities(builder, analysisResult);

        // Build metadata
        var metadata = BuildMetadata(analysisResult);
        builder.WithMetadata(metadata);

        // Set quality score
        if (analysisResult.Signals.TryGetValue("quality.overall", out var quality) && quality is double q)
            builder.WithQualityScore(q);

        // Check if needs review (low quality, failed OCR, etc.)
        if (analysisResult.Signals.TryGetValue("route.needs_escalation", out var needsEsc) && needsEsc is true)
        {
            var reason = analysisResult.Signals.TryGetValue("route.escalation_reason", out var r) ? r?.ToString() : "Auto-routing suggested review";
            builder.NeedsReview(reason ?? "Unknown reason");
        }

        // Add tags
        if (analysisResult.Signals.TryGetValue("identity.is_animated", out var isAnimated) && isAnimated is true)
            builder.WithTag("animated");
        if (analysisResult.Signals.TryGetValue("content.type", out var contentType))
            builder.WithTag(contentType?.ToString() ?? "unknown");

        return builder.Build();
    }

    /// <summary>
    /// Build entity from ImageProfile (basic deterministic analysis only).
    /// </summary>
    public RetrievalEntity BuildEntityFromProfile(
        string imagePath,
        ImageProfile profile,
        string? collection = null)
    {
        var builder = new EntityBuilder()
            .WithContentType(ContentType.Image)
            .WithSource(imagePath)
            .WithContentHash(profile.Sha256)
            .WithTitle(Path.GetFileName(imagePath));

        if (collection != null)
            builder.WithCollection(collection);

        // Build metadata from profile
        var metadata = new ContentMetadata
        {
            Width = profile.Width,
            Height = profile.Height,
            Format = profile.Format
        };
        builder.WithMetadata(metadata);

        // Add basic signals from profile
        builder.WithSignal(new StyloSignal { Key = "identity.sha256", Value = profile.Sha256, Source = "ImageAnalyzer", Tags = [StyloSignalTags.Identity] });
        builder.WithSignal(new StyloSignal { Key = "identity.width", Value = profile.Width, Source = "ImageAnalyzer", Tags = [StyloSignalTags.Identity] });
        builder.WithSignal(new StyloSignal { Key = "identity.height", Value = profile.Height, Source = "ImageAnalyzer", Tags = [StyloSignalTags.Identity] });
        builder.WithSignal(new StyloSignal { Key = "identity.format", Value = profile.Format, Source = "ImageAnalyzer", Tags = [StyloSignalTags.Identity] });
        builder.WithSignal(new StyloSignal { Key = "quality.edge_density", Value = profile.EdgeDensity, Source = "ImageAnalyzer", Tags = [StyloSignalTags.Quality] });
        builder.WithSignal(new StyloSignal { Key = "quality.sharpness", Value = profile.LaplacianVariance, Source = "ImageAnalyzer", Tags = [StyloSignalTags.Quality] });
        builder.WithSignal(new StyloSignal { Key = "color.saturation", Value = profile.MeanSaturation, Source = "ImageAnalyzer", Tags = [StyloSignalTags.Color] });
        builder.WithSignal(new StyloSignal { Key = "color.is_grayscale", Value = profile.IsMostlyGrayscale, Source = "ImageAnalyzer", Tags = [StyloSignalTags.Color] });

        if (profile.DominantColors.Count > 0)
        {
            builder.WithSignal(new StyloSignal { Key = "color.dominant_name", Value = profile.DominantColors[0].Name, Source = "ImageAnalyzer", Tags = [StyloSignalTags.Color] });
        }

        // Add detected type as tag
        builder.WithTag(profile.DetectedType.ToString().ToLowerInvariant());

        return builder.Build();
    }

    private static ContentMetadata BuildMetadata(ImageAnalysisResult analysisResult)
    {
        int? width = null, height = null, frameCount = null;
        string? format = null, dominantColor = null;
        bool? isAnimated = null;
        long? fileSize = null;

        if (analysisResult.Signals.TryGetValue("identity.width", out var w) && w is int iw)
            width = iw;
        if (analysisResult.Signals.TryGetValue("identity.height", out var h) && h is int ih)
            height = ih;
        if (analysisResult.Signals.TryGetValue("identity.format", out var f))
            format = f?.ToString();
        if (analysisResult.Signals.TryGetValue("identity.is_animated", out var a) && a is bool ba)
            isAnimated = ba;
        if (analysisResult.Signals.TryGetValue("identity.frame_count", out var fc) && fc is int ifc)
            frameCount = ifc;
        if (analysisResult.Signals.TryGetValue("color.dominant_name", out var c))
            dominantColor = c?.ToString();
        if (analysisResult.Signals.TryGetValue("identity.file_size", out var s) && s is long ls)
            fileSize = ls;

        return new ContentMetadata
        {
            Width = width,
            Height = height,
            Format = format,
            IsAnimated = isAnimated,
            FrameCount = frameCount,
            DominantColor = dominantColor,
            FileSizeBytes = fileSize
        };
    }

    private void ExtractEntities(EntityBuilder builder, ImageAnalysisResult result)
    {
        // Extract objects from Florence2 detection
        if (result.Signals.TryGetValue("vision.objects", out var objects) && objects is IEnumerable<object> objList)
        {
            foreach (var obj in objList)
            {
                builder.WithEntity(new ExtractedEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = obj.ToString() ?? "unknown",
                    Type = EntityTypes.Object,
                    Confidence = 0.8,
                    Source = "Florence2"
                });
            }
        }

        // Extract text regions from OCR
        if (result.Signals.TryGetValue("ocr.text", out var ocrText) && ocrText != null)
        {
            builder.WithEntity(new ExtractedEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Detected Text",
                Type = EntityTypes.Text,
                Description = ocrText.ToString(),
                Confidence = result.Signals.TryGetValue("ocr.confidence", out var conf) && conf is double c ? c : 0.7,
                Source = "OCR"
            });
        }

        // Extract scene from caption
        if (result.Signals.TryGetValue("vision.caption", out var caption) && caption != null)
        {
            builder.WithEntity(new ExtractedEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Scene",
                Type = EntityTypes.Scene,
                Description = caption.ToString(),
                Confidence = 0.9,
                Source = "VisionLLM"
            });
        }

        // Extract faces if detected
        if (result.Signals.TryGetValue("vision.face_count", out var faceCount) && faceCount is int fc && fc > 0)
        {
            for (int i = 0; i < fc; i++)
            {
                builder.WithEntity(new ExtractedEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = $"Face {i + 1}",
                    Type = EntityTypes.Face,
                    Confidence = 0.85,
                    Source = "Florence2"
                });
            }
        }
    }

    private static List<string> GetTagsForSignal(string key)
    {
        var tags = new List<string>();

        if (key.StartsWith("identity.")) tags.Add(StyloSignalTags.Identity);
        else if (key.StartsWith("color.")) tags.Add(StyloSignalTags.Color);
        else if (key.StartsWith("quality.")) tags.Add(StyloSignalTags.Quality);
        else if (key.StartsWith("vision.")) tags.Add(StyloSignalTags.Visual);
        else if (key.StartsWith("ocr.")) tags.Add(StyloSignalTags.Content);
        else if (key.StartsWith("motion.")) tags.Add(StyloSignalTags.Motion);
        else if (key.StartsWith("scene.")) tags.Add(StyloSignalTags.Scene);

        return tags;
    }
}

/// <summary>
/// Extensions for converting between signal formats.
/// </summary>
public static class SignalConversionExtensions
{
    /// <summary>
    /// Convert DocSummarizer signals to StyloFlow signals.
    /// </summary>
    public static IEnumerable<StyloSignal> ToStyloFlowSignals(
        this IReadOnlyDictionary<string, object?> signals,
        string source = "DocSummarizer")
    {
        foreach (var (key, value) in signals)
        {
            yield return new StyloSignal
            {
                Key = key,
                Value = value,
                Confidence = 1.0,
                Source = source,
                Tags = GetTagsForKey(key)
            };
        }
    }

    private static List<string> GetTagsForKey(string key)
    {
        var tags = new List<string>();
        var prefix = key.Split('.').FirstOrDefault();

        tags.Add(prefix switch
        {
            "identity" => StyloSignalTags.Identity,
            "color" => StyloSignalTags.Color,
            "quality" => StyloSignalTags.Quality,
            "vision" => StyloSignalTags.Visual,
            "ocr" => StyloSignalTags.Content,
            "motion" => StyloSignalTags.Motion,
            "scene" => StyloSignalTags.Scene,
            _ => StyloSignalTags.Metadata
        });

        return tags;
    }
}
