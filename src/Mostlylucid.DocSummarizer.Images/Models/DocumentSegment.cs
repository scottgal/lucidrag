using SixLabors.ImageSharp;

namespace Mostlylucid.DocSummarizer.Images.Models;

/// <summary>
/// Represents a detected segment in a document
/// </summary>
public class DocumentSegment
{
    /// <summary>
    /// Unique identifier for this segment
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Type of segment
    /// </summary>
    public SegmentType Type { get; set; }

    /// <summary>
    /// Bounding box coordinates
    /// </summary>
    public Rectangle BoundingBox { get; set; }

    /// <summary>
    /// Reading order (z-order) - lower numbers read first
    /// </summary>
    public int ZOrder { get; set; }

    /// <summary>
    /// ID of related segment (e.g., caption relates to image)
    /// </summary>
    public string? RelatedTo { get; set; }

    /// <summary>
    /// Confidence score for segment detection (0-1)
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// Whether this segment is color or grayscale
    /// </summary>
    public bool IsColor { get; set; }

    /// <summary>
    /// Dominant color (if color segment)
    /// </summary>
    public string? DominantColor { get; set; }

    /// <summary>
    /// Color saturation (0-1)
    /// </summary>
    public double Saturation { get; set; }

    /// <summary>
    /// Additional metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Types of document segments
/// </summary>
public enum SegmentType
{
    Unknown,
    TextBlock,      // Paragraph, column
    Image,          // Photo, illustration
    Chart,          // Bar chart, line chart, pie chart
    Diagram,        // Flowchart, network diagram
    Table,          // Grid with rows/columns
    Caption,        // Small text describing nearby image
    Header,         // Page header
    Footer,         // Page footer
    Equation,       // Mathematical formula
    Code,           // Source code block
    Logo,           // Company logo, icon
    Barcode         // QR code, barcode
}

/// <summary>
/// Result of processing a segment
/// </summary>
public class SegmentResult
{
    /// <summary>
    /// Segment ID
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Segment type
    /// </summary>
    public SegmentType Type { get; set; }

    /// <summary>
    /// Extracted content (text, caption, markdown table, etc.)
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Confidence score (0-1)
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// Z-order for reconstruction
    /// </summary>
    public int ZOrder { get; set; }

    /// <summary>
    /// Related segment ID (for captions)
    /// </summary>
    public string? RelatedTo { get; set; }

    /// <summary>
    /// Path to cropped segment image
    /// </summary>
    public string? ImagePath { get; set; }

    /// <summary>
    /// Structured data (for tables, charts)
    /// </summary>
    public object? StructuredData { get; set; }

    /// <summary>
    /// Additional metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// Processing duration in milliseconds
    /// </summary>
    public long DurationMs { get; set; }

    /// <summary>
    /// Processing method used
    /// </summary>
    public string? Method { get; set; }
}

/// <summary>
/// Document layout analysis result
/// </summary>
public class DocumentLayout
{
    /// <summary>
    /// Detected segments
    /// </summary>
    public List<DocumentSegment> Segments { get; set; } = new();

    /// <summary>
    /// Overall layout confidence
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// Detected layout type
    /// </summary>
    public LayoutType LayoutType { get; set; }

    /// <summary>
    /// Number of columns detected
    /// </summary>
    public int ColumnCount { get; set; }

    /// <summary>
    /// Whether document is mostly color or grayscale
    /// </summary>
    public bool IsColor { get; set; }

    /// <summary>
    /// Average saturation across all segments
    /// </summary>
    public double AverageSaturation { get; set; }
}

/// <summary>
/// Layout type classification
/// </summary>
public enum LayoutType
{
    Unknown,
    SingleColumn,       // Standard document
    MultiColumn,        // Newspaper, magazine
    Mixed,              // Text + images + charts
    TableBased,         // Primarily tables
    ImageHeavy,         // Mostly images with minimal text
    Technical           // Technical document with diagrams/code
}
