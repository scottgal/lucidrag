using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services;

/// <summary>
/// Interface for processing structured data files.
/// </summary>
public interface IDataProcessor
{
    /// <summary>
    /// Supported file extensions.
    /// </summary>
    IReadOnlySet<string> SupportedExtensions { get; }

    /// <summary>
    /// Check if a file type is supported.
    /// </summary>
    bool IsSupported(string filePath);

    /// <summary>
    /// Get schema information for a data file.
    /// </summary>
    Task<DataSchema> GetSchemaAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Process a data file and extract chunks for indexing.
    /// </summary>
    Task<DataProcessingResult> ProcessAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Stream rows from a data file.
    /// </summary>
    IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamRowsAsync(
        string filePath,
        CancellationToken ct = default);
}
