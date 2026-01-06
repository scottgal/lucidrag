using Mostlylucid.DocSummarizer.Images.Services.Ocr.Models;

namespace Mostlylucid.DocSummarizer.Images.Services.Ocr.Detection;

/// <summary>
/// Interface for text detection services that identify text regions in images.
/// </summary>
public interface ITextDetectionService
{
    /// <summary>
    /// Detect text regions in an image.
    /// Returns bounding boxes for detected text regions.
    /// </summary>
    Task<TextDetectionResult> DetectTextRegionsAsync(
        string imagePath,
        CancellationToken ct = default);

    /// <summary>
    /// Apply Non-Maximum Suppression to remove overlapping boxes.
    /// </summary>
    List<BoundingBox> ApplyNonMaximumSuppression(
        List<BoundingBox> boxes,
        double iouThreshold);
}
