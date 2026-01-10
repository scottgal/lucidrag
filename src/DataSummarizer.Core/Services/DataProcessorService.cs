using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Data.Config;
using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services;

/// <summary>
/// Main data processor service that routes to format-specific processors.
/// </summary>
public class DataProcessorService : IDataProcessor
{
    private readonly DataProcessorOptions _options;
    private readonly ILogger<DataProcessorService> _logger;

    private static readonly HashSet<string> SupportedExtensionsSet = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".tsv", ".json", ".jsonl", ".xlsx", ".xls", ".parquet"
    };

    public DataProcessorService(
        IOptions<DataProcessorOptions> options,
        ILogger<DataProcessorService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public IReadOnlySet<string> SupportedExtensions => SupportedExtensionsSet;

    public bool IsSupported(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return SupportedExtensionsSet.Contains(ext);
    }

    public async Task<DataSchema> GetSchemaAsync(string filePath, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var fileInfo = new FileInfo(filePath);

        return ext switch
        {
            ".csv" or ".tsv" => await GetCsvSchemaAsync(filePath, ext == ".tsv" ? "\t" : _options.Csv.Delimiter, ct),
            ".json" => await GetJsonSchemaAsync(filePath, ct),
            ".jsonl" => await GetJsonLinesSchemaAsync(filePath, ct),
            ".xlsx" or ".xls" => await GetExcelSchemaAsync(filePath, ct),
            ".parquet" => await GetParquetSchemaAsync(filePath, ct),
            _ => throw new NotSupportedException($"File type {ext} is not supported")
        };
    }

    public async Task<DataProcessingResult> ProcessAsync(string filePath, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        try
        {
            _logger.LogInformation("Processing data file: {FilePath} ({Type})", filePath, ext);

            var rows = new List<IReadOnlyDictionary<string, object?>>();
            await foreach (var row in StreamRowsAsync(filePath, ct))
            {
                rows.Add(row);
            }

            var columnNames = rows.Count > 0 ? rows[0].Keys.ToList() : [];
            var chunks = CreateChunks(rows, columnNames, filePath);

            stopwatch.Stop();

            _logger.LogInformation("Processed {RowCount} rows into {ChunkCount} chunks from {FilePath}",
                rows.Count, chunks.Count, filePath);

            return new DataProcessingResult
            {
                FilePath = filePath,
                FileType = ext,
                RowCount = rows.Count,
                ColumnCount = columnNames.Count,
                ColumnNames = columnNames,
                Chunks = chunks,
                ProcessingTime = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing data file: {FilePath}", filePath);
            stopwatch.Stop();

            return new DataProcessingResult
            {
                FilePath = filePath,
                FileType = ext,
                Error = ex.Message,
                ProcessingTime = stopwatch.Elapsed
            };
        }
    }

    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamRowsAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        var enumerable = ext switch
        {
            ".csv" => StreamCsvAsync(filePath, _options.Csv.Delimiter, ct),
            ".tsv" => StreamCsvAsync(filePath, "\t", ct),
            ".json" => StreamJsonAsync(filePath, ct),
            ".jsonl" => StreamJsonLinesAsync(filePath, ct),
            ".xlsx" or ".xls" => StreamExcelAsync(filePath, ct),
            ".parquet" => StreamParquetAsync(filePath, ct),
            _ => throw new NotSupportedException($"File type {ext} is not supported")
        };

        await foreach (var row in enumerable)
        {
            yield return row;
        }
    }

    private List<DataChunk> CreateChunks(
        List<IReadOnlyDictionary<string, object?>> rows,
        List<string> columnNames,
        string filePath)
    {
        var chunks = new List<DataChunk>();
        var chunkSize = _options.ChunkSize;
        var overlap = _options.ChunkOverlap;

        for (int i = 0; i < rows.Count; i += (chunkSize - overlap))
        {
            var chunkRows = rows.Skip(i).Take(chunkSize).ToList();
            if (chunkRows.Count == 0) break;

            var text = FormatRowsAsText(chunkRows, columnNames);
            if (text.Length > _options.MaxChunkTextLength)
            {
                text = text[.._options.MaxChunkTextLength] + "...";
            }

            chunks.Add(new DataChunk
            {
                Id = $"{Path.GetFileName(filePath)}:rows:{i + 1}-{i + chunkRows.Count}",
                Text = text,
                RowStart = i + 1,
                RowEnd = i + chunkRows.Count,
                Metadata = new Dictionary<string, object?>
                {
                    ["source_file"] = Path.GetFileName(filePath),
                    ["row_start"] = i + 1,
                    ["row_end"] = i + chunkRows.Count,
                    ["row_count"] = chunkRows.Count
                }
            });
        }

        return chunks;
    }

    private static string FormatRowsAsText(
        List<IReadOnlyDictionary<string, object?>> rows,
        List<string> columnNames)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Columns: {string.Join(", ", columnNames)}");
        sb.AppendLine();

        foreach (var row in rows)
        {
            var values = columnNames.Select(c => row.TryGetValue(c, out var v) ? FormatValue(v) : "");
            sb.AppendLine(string.Join(" | ", values));
        }

        return sb.ToString();
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "",
            string s => s,
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss"),
            _ => value.ToString() ?? ""
        };
    }

    // Format-specific implementations

    private async Task<DataSchema> GetCsvSchemaAsync(string filePath, string delimiter, CancellationToken ct)
    {
        using var reader = new StreamReader(filePath);
        var firstLine = await reader.ReadLineAsync(ct);
        var columns = firstLine?.Split(delimiter) ?? [];

        var lineCount = 1;
        while (await reader.ReadLineAsync(ct) != null)
            lineCount++;

        return new DataSchema
        {
            Columns = columns.Select(c => new ColumnInfo
            {
                Name = c.Trim('"', ' '),
                DataType = "string",
                IsNullable = true
            }).ToList(),
            EstimatedRowCount = lineCount - (_options.Csv.HasHeaderRow ? 1 : 0),
            FileSizeBytes = new FileInfo(filePath).Length
        };
    }

    private async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamCsvAsync(
        string filePath,
        string delimiter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(filePath);
        var headerLine = await reader.ReadLineAsync(ct);
        if (headerLine == null) yield break;

        var headers = headerLine.Split(delimiter)
            .Select(h => h.Trim('"', ' '))
            .ToArray();

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            var values = ParseCsvLine(line, delimiter[0]);
            var row = new Dictionary<string, object?>();

            for (int i = 0; i < headers.Length && i < values.Count; i++)
            {
                row[headers[i]] = values[i];
            }

            yield return row;
        }
    }

    private static List<string> ParseCsvLine(string line, char delimiter)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == delimiter && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        values.Add(current.ToString());
        return values;
    }

    private async Task<DataSchema> GetJsonSchemaAsync(string filePath, CancellationToken ct)
    {
        // Simple schema detection - read first few records
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var row in StreamJsonAsync(filePath, ct).Take(10))
        {
            rows.Add(row);
        }

        var columns = rows.SelectMany(r => r.Keys).Distinct()
            .Select(k => new ColumnInfo { Name = k, DataType = "dynamic", IsNullable = true })
            .ToList();

        return new DataSchema
        {
            Columns = columns,
            EstimatedRowCount = -1, // Unknown without full scan
            FileSizeBytes = new FileInfo(filePath).Length
        };
    }

    private async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamJsonAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(filePath, ct);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var root = doc.RootElement;

        if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                if (element.ValueKind != System.Text.Json.JsonValueKind.Object) continue;

                var row = new Dictionary<string, object?>();
                foreach (var prop in element.EnumerateObject())
                {
                    row[prop.Name] = GetJsonValue(prop.Value);
                }
                yield return row;
            }
        }
        else if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var row = new Dictionary<string, object?>();
            foreach (var prop in root.EnumerateObject())
            {
                row[prop.Name] = GetJsonValue(prop.Value);
            }
            yield return row;
        }
    }

    private static object? GetJsonValue(System.Text.Json.JsonElement element)
    {
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => element.GetString(),
            System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    private async Task<DataSchema> GetJsonLinesSchemaAsync(string filePath, CancellationToken ct)
    {
        var lineCount = 0;
        var columns = new HashSet<string>();

        await foreach (var row in StreamJsonLinesAsync(filePath, ct).Take(100))
        {
            lineCount++;
            foreach (var key in row.Keys)
                columns.Add(key);
        }

        return new DataSchema
        {
            Columns = columns.Select(c => new ColumnInfo { Name = c, DataType = "dynamic", IsNullable = true }).ToList(),
            EstimatedRowCount = lineCount,
            FileSizeBytes = new FileInfo(filePath).Length
        };
    }

    private async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamJsonLinesAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(filePath);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var row = new Dictionary<string, object?>();

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                row[prop.Name] = GetJsonValue(prop.Value);
            }

            yield return row;
        }
    }

    private Task<DataSchema> GetExcelSchemaAsync(string filePath, CancellationToken ct)
    {
        // ClosedXML implementation
        using var workbook = new ClosedXML.Excel.XLWorkbook(filePath);
        var worksheet = _options.Excel.SheetName != null
            ? workbook.Worksheet(_options.Excel.SheetName)
            : workbook.Worksheets.First();

        var usedRange = worksheet.RangeUsed();
        if (usedRange == null)
        {
            return Task.FromResult(new DataSchema
            {
                FileSizeBytes = new FileInfo(filePath).Length
            });
        }

        var headerRow = usedRange.FirstRow();
        var columns = headerRow.Cells()
            .Select(c => new ColumnInfo
            {
                Name = c.GetString() ?? $"Column{c.Address.ColumnNumber}",
                DataType = "dynamic",
                IsNullable = true
            })
            .ToList();

        return Task.FromResult(new DataSchema
        {
            Columns = columns,
            EstimatedRowCount = usedRange.RowCount() - 1,
            FileSizeBytes = new FileInfo(filePath).Length
        });
    }

    private async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamExcelAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var workbook = new ClosedXML.Excel.XLWorkbook(filePath);
        var worksheet = _options.Excel.SheetName != null
            ? workbook.Worksheet(_options.Excel.SheetName)
            : workbook.Worksheets.First();

        var usedRange = worksheet.RangeUsed();
        if (usedRange == null) yield break;

        var rows = usedRange.RowsUsed().ToList();
        if (rows.Count == 0) yield break;

        var headers = rows[0].Cells()
            .Select(c => c.GetString() ?? $"Column{c.Address.ColumnNumber}")
            .ToList();

        foreach (var row in rows.Skip(_options.Excel.HasHeaderRow ? 1 : 0))
        {
            ct.ThrowIfCancellationRequested();
            var data = new Dictionary<string, object?>();
            var cells = row.Cells().ToList();

            for (int i = 0; i < headers.Count && i < cells.Count; i++)
            {
                data[headers[i]] = cells[i].Value.IsBlank ? null : cells[i].Value.ToString();
            }

            yield return data;
            await Task.Yield(); // Allow cancellation
        }
    }

    private async Task<DataSchema> GetParquetSchemaAsync(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        using var reader = await Parquet.ParquetReader.CreateAsync(stream, cancellationToken: ct);
        var schema = reader.Schema;

        var columns = schema.DataFields
            .Select(f => new ColumnInfo
            {
                Name = f.Name,
                DataType = f.ClrType?.Name ?? "unknown",
                IsNullable = f.IsNullable
            })
            .ToList();

        return new DataSchema
        {
            Columns = columns,
            EstimatedRowCount = (int)reader.Metadata.NumRows,
            FileSizeBytes = new FileInfo(filePath).Length
        };
    }

    private async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamParquetAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        using var reader = await Parquet.ParquetReader.CreateAsync(stream, cancellationToken: ct);
        var schema = reader.Schema;
        var dataFields = schema.DataFields.ToList();
        var fieldNames = dataFields.Select(f => f.Name).ToList();

        for (int rg = 0; rg < reader.RowGroupCount; rg++)
        {
            ct.ThrowIfCancellationRequested();
            using var rowGroupReader = reader.OpenRowGroupReader(rg);

            var columns = new Dictionary<string, Array>();
            foreach (var field in dataFields)
            {
                var column = await rowGroupReader.ReadColumnAsync(field, ct);
                columns[field.Name] = column.Data;
            }

            if (columns.Count == 0) continue;

            var rowCount = columns.Values.First().Length;
            for (int row = 0; row < rowCount; row++)
            {
                var data = new Dictionary<string, object?>();
                foreach (var name in fieldNames)
                {
                    data[name] = columns[name].GetValue(row);
                }
                yield return data;
            }
        }
    }
}
