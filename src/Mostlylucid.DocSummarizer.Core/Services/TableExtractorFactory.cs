using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Core.Models;

namespace Mostlylucid.DocSummarizer.Core.Services;

/// <summary>
/// Factory for creating appropriate table extractors based on file type
/// </summary>
public class TableExtractorFactory : ITableExtractorFactory
{
    private readonly ILogger<TableExtractorFactory> _logger;
    private readonly List<ITableExtractor> _extractors;
    private Dictionary<ITableExtractor, bool>? _availabilityCache;

    public TableExtractorFactory(ILogger<TableExtractorFactory> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;

        // Register all available extractors
        _extractors = new List<ITableExtractor>
        {
            new PdfTableExtractor(loggerFactory.CreateLogger<PdfTableExtractor>()),
            new DocxTableExtractor(loggerFactory.CreateLogger<DocxTableExtractor>())
        };
    }

    /// <summary>
    /// Get appropriate extractor for a file
    /// </summary>
    public async Task<ITableExtractor?> GetExtractorForFileAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("File not found: {Path}", filePath);
            return null;
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        // Find extractor that supports this file type
        foreach (var extractor in _extractors)
        {
            if (extractor.SupportedExtensions.Contains(ext))
            {
                // Check if extractor is available
                if (await IsExtractorAvailableAsync(extractor, ct))
                {
                    _logger.LogDebug("Using {Extractor} for {File}", extractor.Name, Path.GetFileName(filePath));
                    return extractor;
                }
                else
                {
                    _logger.LogWarning("{Extractor} not available (missing dependencies)", extractor.Name);
                }
            }
        }

        _logger.LogWarning("No available extractor for file type: {Extension}", ext);
        return null;
    }

    /// <summary>
    /// Get all available extractors (with dependencies installed)
    /// </summary>
    public async Task<IReadOnlyList<ITableExtractor>> GetAvailableExtractorsAsync(CancellationToken ct = default)
    {
        var available = new List<ITableExtractor>();

        foreach (var extractor in _extractors)
        {
            if (await IsExtractorAvailableAsync(extractor, ct))
            {
                available.Add(extractor);
            }
        }

        return available;
    }

    /// <summary>
    /// Check if extractor is available (with caching)
    /// </summary>
    private async Task<bool> IsExtractorAvailableAsync(ITableExtractor extractor, CancellationToken ct)
    {
        _availabilityCache ??= new Dictionary<ITableExtractor, bool>();

        if (_availabilityCache.TryGetValue(extractor, out var cached))
        {
            return cached;
        }

        var available = await extractor.IsAvailableAsync(ct);
        _availabilityCache[extractor] = available;

        return available;
    }

    /// <summary>
    /// Clear availability cache (useful for testing)
    /// </summary>
    public void ClearCache()
    {
        _availabilityCache?.Clear();
    }
}

/// <summary>
/// Extension methods for table extraction
/// </summary>
public static class TableExtractorExtensions
{
    /// <summary>
    /// Extract tables from a file and export to CSV
    /// </summary>
    public static async Task<List<string>> ExtractAndExportToCsvAsync(
        this ITableExtractor extractor,
        string inputPath,
        string outputDirectory,
        TableExtractionOptions? options = null,
        CancellationToken ct = default)
    {
        var result = await extractor.ExtractTablesAsync(inputPath, options, ct);

        if (!result.Success || result.Tables.Count == 0)
        {
            return new List<string>();
        }

        Directory.CreateDirectory(outputDirectory);

        var csvPaths = new List<string>();
        var baseName = Path.GetFileNameWithoutExtension(inputPath);

        for (int i = 0; i < result.Tables.Count; i++)
        {
            var table = result.Tables[i];

            var csvFileName = result.Tables.Count == 1
                ? $"{baseName}_table.csv"
                : $"{baseName}_table_{i + 1}.csv";

            var csvPath = Path.Combine(outputDirectory, csvFileName);

            await File.WriteAllTextAsync(csvPath, table.ToCsv(), ct);
            csvPaths.Add(csvPath);
        }

        return csvPaths;
    }

    /// <summary>
    /// Extract tables from a file using factory
    /// </summary>
    public static async Task<TableExtractionResult?> ExtractTablesAsync(
        this ITableExtractorFactory factory,
        string filePath,
        TableExtractionOptions? options = null,
        CancellationToken ct = default)
    {
        var extractor = await factory.GetExtractorForFileAsync(filePath, ct);
        if (extractor == null)
        {
            return null;
        }

        return await extractor.ExtractTablesAsync(filePath, options, ct);
    }
}
