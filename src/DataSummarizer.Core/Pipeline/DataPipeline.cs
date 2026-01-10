using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Data.Services;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace Mostlylucid.DocSummarizer.Data.Pipeline;

/// <summary>
/// Pipeline implementation for structured data files (CSV, JSON, Excel, Parquet).
/// </summary>
public class DataPipeline : PipelineBase
{
    private readonly IDataProcessor _dataProcessor;
    private readonly ILogger<DataPipeline> _logger;

    public DataPipeline(IDataProcessor dataProcessor, ILogger<DataPipeline> logger)
    {
        _dataProcessor = dataProcessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public override string PipelineId => "data";

    /// <inheritdoc />
    public override string Name => "Data Pipeline";

    /// <inheritdoc />
    public override IReadOnlySet<string> SupportedExtensions => _dataProcessor.SupportedExtensions;

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<ContentChunk>> ProcessCoreAsync(
        string filePath,
        PipelineOptions options,
        IProgress<PipelineProgress>? progress,
        CancellationToken ct)
    {
        _logger.LogInformation("Processing data file: {FilePath}", filePath);

        progress?.Report(new PipelineProgress("Processing", "Reading data file", 10));

        var result = await _dataProcessor.ProcessAsync(filePath, ct);

        if (!result.Success)
        {
            throw new InvalidOperationException(result.Error ?? "Data processing failed");
        }

        progress?.Report(new PipelineProgress("Converting", "Converting to chunks", 80, result.Chunks.Count, result.Chunks.Count));

        // Convert DataChunk to ContentChunk
        var chunks = result.Chunks.Select((chunk, i) => new ContentChunk
        {
            Id = chunk.Id,
            Text = chunk.Text,
            ContentType = ContentType.StructuredData,
            SourcePath = filePath,
            Index = i,
            ContentHash = ComputeHash(chunk.Text),
            Metadata = new Dictionary<string, object?>
            {
                ["rowStart"] = chunk.RowStart,
                ["rowEnd"] = chunk.RowEnd,
                ["fileType"] = result.FileType,
                ["rowCount"] = result.RowCount,
                ["columnCount"] = result.ColumnCount,
                ["columnNames"] = result.ColumnNames
            }
        }).ToList();

        _logger.LogInformation("Processed {ChunkCount} chunks from data file", chunks.Count);

        return chunks;
    }
}
