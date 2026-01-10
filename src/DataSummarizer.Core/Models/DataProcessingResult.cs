namespace Mostlylucid.DocSummarizer.Data.Models;

/// <summary>
/// Result of processing a data file.
/// </summary>
public record DataProcessingResult
{
    public required string FilePath { get; init; }
    public required string FileType { get; init; }
    public int RowCount { get; init; }
    public int ColumnCount { get; init; }
    public List<string> ColumnNames { get; init; } = [];
    public List<DataChunk> Chunks { get; init; } = [];
    public TimeSpan ProcessingTime { get; init; }
    public string? Error { get; init; }
    public bool Success => Error == null;
}

/// <summary>
/// A chunk of data for embedding and indexing.
/// </summary>
public record DataChunk
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public int RowStart { get; init; }
    public int RowEnd { get; init; }
    public Dictionary<string, object?> Metadata { get; init; } = [];
}

/// <summary>
/// Schema information for a data file.
/// </summary>
public record DataSchema
{
    public List<ColumnInfo> Columns { get; init; } = [];
    public int EstimatedRowCount { get; init; }
    public long FileSizeBytes { get; init; }
}

/// <summary>
/// Information about a column in the data.
/// </summary>
public record ColumnInfo
{
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public bool IsNullable { get; init; }
    public int? MaxLength { get; init; }
    public List<string> SampleValues { get; init; } = [];
}
