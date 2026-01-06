namespace Mostlylucid.DocSummarizer.Images.Services.Ocr;

/// <summary>
/// Abstraction for OCR engines that extract text with bounding box coordinates.
/// Enables testing and swapping between different OCR implementations.
/// </summary>
public interface IOcrEngine
{
    /// <summary>
    /// Extract text regions with coordinates from an image.
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <returns>List of text regions with bounding boxes and confidence scores</returns>
    List<OcrTextRegion> ExtractTextWithCoordinates(string imagePath);
}

/// <summary>
/// Text region with coordinates and confidence (EasyOCR-compatible format).
/// </summary>
public record OcrTextRegion
{
    /// <summary>
    /// Detected text content.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// OCR confidence score (0.0 - 1.0).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Bounding box coordinates for this text region.
    /// </summary>
    public required BoundingBox BoundingBox { get; init; }
}

/// <summary>
/// Bounding box coordinates for a text region.
/// Compatible with EasyOCR format.
/// </summary>
public record BoundingBox
{
    /// <summary>
    /// Left X coordinate.
    /// </summary>
    public int X1 { get; init; }

    /// <summary>
    /// Top Y coordinate.
    /// </summary>
    public int Y1 { get; init; }

    /// <summary>
    /// Right X coordinate.
    /// </summary>
    public int X2 { get; init; }

    /// <summary>
    /// Bottom Y coordinate.
    /// </summary>
    public int Y2 { get; init; }

    /// <summary>
    /// Width of bounding box.
    /// </summary>
    public int Width { get; init; }

    /// <summary>
    /// Height of bounding box.
    /// </summary>
    public int Height { get; init; }

    /// <summary>
    /// Detection confidence score (0.0 - 1.0).
    /// </summary>
    public float Confidence { get; init; }

    /// <summary>
    /// Center point X coordinate.
    /// </summary>
    public int CenterX => (X1 + X2) / 2;

    /// <summary>
    /// Center point Y coordinate.
    /// </summary>
    public int CenterY => (Y1 + Y2) / 2;
}
