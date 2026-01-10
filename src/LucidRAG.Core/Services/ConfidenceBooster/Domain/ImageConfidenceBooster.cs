using System.Text.Json;
using Microsoft.Extensions.Logging;
using LucidRAG.Core.Services.ConfidenceBooster.Artifacts;

namespace LucidRAG.Core.Services.ConfidenceBooster.Domain;

/// <summary>
/// ConfidenceBooster for ImageSummarizer - refines object recognition and OCR using LLM.
/// </summary>
public class ImageConfidenceBooster : BaseConfidenceBooster<ImageCropArtifact>
{
    private readonly IImageSignalRepository _signalRepository;

    public ImageConfidenceBooster(
        ILogger<ImageConfidenceBooster> logger,
        ILlmService llmService,
        IEvidenceRepository evidenceRepository,
        IImageSignalRepository signalRepository,
        ConfidenceBoosterConfig config)
        : base(logger, llmService, evidenceRepository, config)
    {
        _signalRepository = signalRepository;
    }

    /// <summary>
    /// Extract low-confidence image crops for boosting.
    /// </summary>
    public override async Task<List<ImageCropArtifact>> ExtractArtifactsAsync(
        Guid documentId,
        double confidenceThreshold = 0.75,
        int maxArtifacts = 5,
        CancellationToken ct = default)
    {
        Logger.LogInformation(
            "Extracting image artifacts for document {DocumentId} (threshold: {Threshold})",
            documentId,
            confidenceThreshold);

        var artifacts = new List<ImageCropArtifact>();

        // 1. Get all signals for this document
        var signals = await _signalRepository.GetSignalsAsync(documentId, ct);

        // 2. Find low-confidence signals that benefit from LLM boost
        var candidates = signals
            .Where(s => s.Confidence < confidenceThreshold)
            .Where(s => s.Type == "object_detection" || s.Type == "ocr" || s.Type == "classification")
            .OrderBy(s => s.Confidence)  // Lowest confidence first
            .Take(maxArtifacts)
            .ToList();

        if (!candidates.Any())
        {
            Logger.LogDebug("No low-confidence image signals found for document {DocumentId}", documentId);
            return artifacts;
        }

        Logger.LogDebug("Found {Count} candidate signals for boosting", candidates.Count);

        // 3. Extract bounded crops for each candidate
        foreach (var signal in candidates)
        {
            try
            {
                var artifact = await ExtractCropAsync(documentId, signal, ct);
                if (artifact != null)
                {
                    artifacts.Add(artifact);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to extract crop for signal {SignalName}", signal.Name);
            }
        }

        Logger.LogInformation("Extracted {Count} image artifacts for boosting", artifacts.Count);
        return artifacts;
    }

    /// <summary>
    /// Extract a bounded crop from the original image using signal metadata.
    /// </summary>
    private async Task<ImageCropArtifact?> ExtractCropAsync(
        Guid documentId,
        ImageSignal signal,
        CancellationToken ct)
    {
        // Get bounding box from signal metadata
        if (!signal.Metadata.TryGetValue("bounding_box", out var bboxObj) || bboxObj is not int[] bbox)
        {
            Logger.LogWarning("Signal {SignalName} missing bounding_box metadata", signal.Name);
            return null;
        }

        // Get frame number if available
        signal.Metadata.TryGetValue("frame_number", out var frameNumberObj);
        var frameNumber = frameNumberObj as int?;

        // Retrieve original image from evidence storage
        var evidence = await EvidenceRepository.GetAsync(
            documentId,
            frameNumber.HasValue ? $"frame_{frameNumber}" : "original_image",
            ct);

        if (evidence == null)
        {
            Logger.LogWarning("Original image not found for document {DocumentId}", documentId);
            return null;
        }

        // Extract crop (simplified - actual implementation would use image processing library)
        var base64Crop = ExtractImageCrop(evidence.Content, bbox);

        // Determine task type based on signal type
        var taskType = signal.Type switch
        {
            "object_detection" => "object_recognition",
            "ocr" => "text_extraction",
            "classification" => "image_classification",
            _ => "object_recognition"
        };

        return new ImageCropArtifact
        {
            ArtifactId = $"img_{documentId:N}_{signal.Name}_{Guid.NewGuid():N}",
            DocumentId = documentId,
            SignalName = signal.Name,
            OriginalConfidence = signal.Confidence,
            Base64Image = base64Crop,
            BoundingBox = bbox,
            FrameNumber = frameNumber,
            OriginalClassification = signal.Value as string,
            TaskType = taskType,
            Metadata = new Dictionary<string, object>
            {
                ["signal_type"] = signal.Type,
                ["original_value"] = signal.Value?.ToString() ?? "",
                ["frame_number"] = frameNumber ?? 0
            }
        };
    }

    /// <summary>
    /// Generate system prompt for image understanding.
    /// </summary>
    protected override string GetSystemPrompt()
    {
        return """
        You are an expert computer vision analyst helping to refine low-confidence image recognition results.

        Your task is to analyze image crops and provide:
        1. A refined classification or description
        2. Confidence level (0.0-1.0) for your answer
        3. Reasoning for your classification

        Always respond in JSON format:
        {
            "value": "refined classification or extracted text",
            "confidence": 0.85,
            "reasoning": "explanation of what you see and why",
            "metadata": {
                "alternative_labels": ["other possible classifications"],
                "notable_features": ["key visual features"]
            }
        }

        Be accurate and conservative. If uncertain, reflect that in the confidence score.
        """;
    }

    /// <summary>
    /// Generate domain-specific prompt for image crop.
    /// </summary>
    protected override string GeneratePrompt(ImageCropArtifact artifact)
    {
        return artifact.TaskType switch
        {
            "object_recognition" => GenerateObjectRecognitionPrompt(artifact),
            "text_extraction" => GenerateOcrPrompt(artifact),
            "image_classification" => GenerateClassificationPrompt(artifact),
            _ => GenerateObjectRecognitionPrompt(artifact)
        };
    }

    private string GenerateObjectRecognitionPrompt(ImageCropArtifact artifact)
    {
        return $"""
        Task: Object Recognition

        Original low-confidence classification: "{artifact.OriginalClassification}" (confidence: {artifact.OriginalConfidence:F2})

        Please analyze this image crop and provide:
        - What object(s) you see in the image
        - Key distinguishing features
        - Confidence in your classification

        The image is provided as Base64:
        {artifact.Base64Image}

        Context:
        - Frame number: {artifact.FrameNumber ?? 0}
        - Bounding box: [{string.Join(", ", artifact.BoundingBox)}]

        Respond in JSON format as specified in the system prompt.
        """;
    }

    private string GenerateOcrPrompt(ImageCropArtifact artifact)
    {
        return $"""
        Task: Text Extraction (OCR Refinement)

        Original low-confidence OCR result: "{artifact.OriginalClassification}" (confidence: {artifact.OriginalConfidence:F2})

        Please extract and refine the text from this image crop:
        - Correct any obvious OCR errors
        - Preserve formatting if visible
        - Indicate confidence in the extraction

        The image is provided as Base64:
        {artifact.Base64Image}

        Context:
        - Frame number: {artifact.FrameNumber ?? 0}
        - Bounding box: [{string.Join(", ", artifact.BoundingBox)}]

        Respond in JSON format as specified in the system prompt.
        """;
    }

    private string GenerateClassificationPrompt(ImageCropArtifact artifact)
    {
        return $"""
        Task: Image Classification

        Original low-confidence classification: "{artifact.OriginalClassification}" (confidence: {artifact.OriginalConfidence:F2})

        Please classify this image crop:
        - What category or type does it belong to?
        - What are the distinguishing visual features?
        - How confident are you in this classification?

        The image is provided as Base64:
        {artifact.Base64Image}

        Context:
        - Frame number: {artifact.FrameNumber ?? 0}
        - Bounding box: [{string.Join(", ", artifact.BoundingBox)}]

        Respond in JSON format as specified in the system prompt.
        """;
    }

    /// <summary>
    /// Parse LLM JSON response.
    /// </summary>
    protected override (string? Value, double? Confidence, string? Reasoning, Dictionary<string, object>? Metadata)
        ParseLlmResponse(string llmResponse, ImageCropArtifact artifact)
    {
        try
        {
            var json = JsonDocument.Parse(llmResponse);
            var root = json.RootElement;

            var value = root.TryGetProperty("value", out var valueProp)
                ? valueProp.GetString()
                : null;

            var confidence = root.TryGetProperty("confidence", out var confProp)
                ? confProp.GetDouble()
                : (double?)null;

            var reasoning = root.TryGetProperty("reasoning", out var reasonProp)
                ? reasonProp.GetString()
                : null;

            Dictionary<string, object>? metadata = null;
            if (root.TryGetProperty("metadata", out var metaProp))
            {
                metadata = new Dictionary<string, object>();
                foreach (var prop in metaProp.EnumerateObject())
                {
                    metadata[prop.Name] = prop.Value.ToString();
                }
            }

            return (value, confidence, reasoning, metadata);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse LLM response as JSON: {Response}", llmResponse);
            return (null, null, llmResponse, null);
        }
    }

    /// <summary>
    /// Persist boost result back to signal ledger.
    /// </summary>
    protected override async Task PersistBoostResult(
        Guid documentId,
        BoostResult result,
        CancellationToken ct)
    {
        var artifact = (ImageCropArtifact)result.Artifact;

        // Update the original signal with boosted value and confidence
        await _signalRepository.UpdateSignalAsync(new ImageSignal
        {
            DocumentId = documentId,
            Name = artifact.SignalName + ".boosted",  // Mark as boosted
            Value = result.BoostedValue,
            Type = artifact.TaskType,
            Confidence = result.BoostedConfidence ?? artifact.OriginalConfidence,
            Source = "ConfidenceBooster.Image",
            Metadata = new Dictionary<string, object>
            {
                ["original_value"] = artifact.OriginalClassification ?? "",
                ["original_confidence"] = artifact.OriginalConfidence,
                ["boost_reasoning"] = result.Reasoning ?? "",
                ["boost_metadata"] = result.AdditionalMetadata ?? new Dictionary<string, object>(),
                ["tokens_used"] = result.TokensUsed,
                ["inference_time_ms"] = result.InferenceTimeMs
            }
        }, ct);

        Logger.LogDebug(
            "Persisted boosted signal {SignalName} for document {DocumentId}",
            artifact.SignalName,
            documentId);
    }

    /// <summary>
    /// Extract image crop from Base64 image using bounding box.
    /// Simplified implementation - would use actual image processing library (SkiaSharp, ImageSharp, etc.).
    /// </summary>
    private string ExtractImageCrop(string base64Image, int[] bbox)
    {
        // TODO: Implement actual crop extraction using image processing library
        // For now, return the original image (LLM can still analyze the bounding box context)
        return base64Image;
    }
}

/// <summary>
/// Repository interface for image signals.
/// </summary>
public interface IImageSignalRepository
{
    Task<List<ImageSignal>> GetSignalsAsync(Guid documentId, CancellationToken ct = default);
    Task UpdateSignalAsync(ImageSignal signal, CancellationToken ct = default);
}

/// <summary>
/// Image signal model.
/// </summary>
public class ImageSignal
{
    public Guid DocumentId { get; set; }
    public required string Name { get; set; }
    public object? Value { get; set; }
    public required string Type { get; set; }
    public double Confidence { get; set; }
    public required string Source { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}
