using Tesseract;

namespace Mostlylucid.DocSummarizer.Images.Services.Ocr;

/// <summary>
/// Tesseract implementation of IOcrEngine.
/// Provides text extraction with word-level bounding boxes.
/// </summary>
public class TesseractOcrEngine : IOcrEngine
{
    private readonly string? _tesseractDataPath;
    private readonly string _language;

    public TesseractOcrEngine(string? tesseractDataPath = null, string language = "eng")
    {
        _tesseractDataPath = tesseractDataPath;
        _language = language;
    }

    /// <inheritdoc/>
    public List<OcrTextRegion> ExtractTextWithCoordinates(string imagePath)
    {
        var regions = new List<OcrTextRegion>();

        using var engine = new TesseractEngine(_tesseractDataPath ?? "./tessdata", _language, EngineMode.Default);

        // Load image from disk
        using var img = Pix.LoadFromFile(imagePath);
        using var page = engine.Process(img);

        // Iterate through layout hierarchy to get word-level bounding boxes
        using var iter = page.GetIterator();
        iter.Begin();

        do
        {
            // Get text and confidence at word level
            var text = iter.GetText(PageIteratorLevel.Word);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            // Get bounding box
            if (iter.TryGetBoundingBox(PageIteratorLevel.Word, out var bbox))
            {
                var confidence = iter.GetConfidence(PageIteratorLevel.Word) / 100.0; // Convert 0-100 to 0-1

                regions.Add(new OcrTextRegion
                {
                    Text = text.Trim(),
                    Confidence = confidence,
                    BoundingBox = new BoundingBox
                    {
                        X1 = bbox.X1,
                        Y1 = bbox.Y1,
                        X2 = bbox.X2,
                        Y2 = bbox.Y2,
                        Width = bbox.Width,
                        Height = bbox.Height
                    }
                });
            }
        } while (iter.Next(PageIteratorLevel.Word));

        return regions;
    }
}
