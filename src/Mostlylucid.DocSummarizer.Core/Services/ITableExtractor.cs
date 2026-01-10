using Mostlylucid.DocSummarizer.Core.Models;

namespace Mostlylucid.DocSummarizer.Core.Services;

/// <summary>
/// Interface for extracting tables from documents
/// </summary>
public interface ITableExtractor
{
    /// <summary>
    /// Supported file extensions (e.g., [".pdf", ".docx"])
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Extractor name (e.g., "PdfPlumber", "PythonDocx")
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Whether this extractor is available (dependencies installed)
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Extract tables from a document
    /// </summary>
    /// <param name="filePath">Path to document</param>
    /// <param name="options">Extraction options</param>
    /// <param name="ct">Cancellation token</param>
    Task<TableExtractionResult> ExtractTablesAsync(
        string filePath,
        TableExtractionOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Check if file is supported by this extractor
    /// </summary>
    bool SupportsFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return SupportedExtensions.Contains(ext);
    }
}

/// <summary>
/// Options for table extraction
/// </summary>
public class TableExtractionOptions
{
    /// <summary>
    /// Pages to extract from (null = all pages)
    /// </summary>
    public List<int>? Pages { get; init; }

    /// <summary>
    /// Minimum table rows to extract (filter small tables)
    /// </summary>
    public int MinRows { get; init; } = 2;

    /// <summary>
    /// Minimum table columns
    /// </summary>
    public int MinColumns { get; init; } = 2;

    /// <summary>
    /// Whether to include table borders/formatting
    /// </summary>
    public bool IncludeFormatting { get; init; } = false;

    /// <summary>
    /// Whether to attempt OCR on image-based tables
    /// </summary>
    public bool EnableOcr { get; init; } = false;

    /// <summary>
    /// Table detection strategy (e.g., "lines", "text", "hybrid")
    /// </summary>
    public string? DetectionStrategy { get; init; }

    /// <summary>
    /// Additional extractor-specific options
    /// </summary>
    public Dictionary<string, object>? CustomOptions { get; init; }
}

/// <summary>
/// Factory for creating table extractors
/// </summary>
public interface ITableExtractorFactory
{
    /// <summary>
    /// Get extractor for a file
    /// </summary>
    Task<ITableExtractor?> GetExtractorForFileAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Get all available extractors
    /// </summary>
    Task<IReadOnlyList<ITableExtractor>> GetAvailableExtractorsAsync(CancellationToken ct = default);
}
