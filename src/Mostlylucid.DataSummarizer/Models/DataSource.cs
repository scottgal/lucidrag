namespace Mostlylucid.DataSummarizer.Models;

/// <summary>
/// Represents any data source DuckDB can connect to
/// </summary>
public class DataSource
{
    public DataSourceType Type { get; set; }
    public string Source { get; set; } = ""; // File path, URL, or connection string
    public string? Table { get; set; } // Table/sheet name for databases/Excel
    public string? Schema { get; set; } // Schema for databases
    public Dictionary<string, string> Options { get; set; } = new();
    
    /// <summary>
    /// For log files: the path to the converted Parquet file (temporary)
    /// </summary>
    public string? ConvertedParquetPath { get; set; }
    
    /// <summary>
    /// For log files: the detected format (apache-error, apache-access, iis-w3c)
    /// </summary>
    public string? DetectedLogFormat { get; set; }
    
    /// <summary>
    /// Get the DuckDB read expression for this source
    /// </summary>
    /// <summary>
    /// If true, ignore CSV parsing errors (malformed rows)
    /// </summary>
    public bool IgnoreErrors { get; set; }
    
    public string GetReadExpression()
    {
        var escaped = Source.Replace("'", "''").Replace("\\", "/");
        
        return Type switch
        {
            // Files - CSV with optional error tolerance
            DataSourceType.Csv when IgnoreErrors => $"read_csv_auto('{escaped}', ignore_errors=true)",
            DataSourceType.Csv => $"read_csv_auto('{escaped}')",
            DataSourceType.Parquet => $"read_parquet('{escaped}')",
            DataSourceType.Json => $"read_json_auto('{escaped}')",
            DataSourceType.Excel when Table != null => 
                $"st_read('{escaped}', layer = '{Table}')",
            DataSourceType.Excel => $"st_read('{escaped}')",
            DataSourceType.Avro => $"read_avro('{escaped}')",
            // Log files are pre-converted to Parquet
            DataSourceType.Log when !string.IsNullOrEmpty(ConvertedParquetPath) => 
                $"read_parquet('{ConvertedParquetPath.Replace("'", "''").Replace("\\", "/")}')",
            DataSourceType.Log => throw new InvalidOperationException(
                "Log file must be converted to Parquet first. Call EnsureLogConverted() before GetReadExpression()."),
            
            // Databases (after ATTACH)
            DataSourceType.Sqlite when Table != null => 
                $"sqlite_db.{Quote(Table)}",
            DataSourceType.Postgres when Schema != null && Table != null => 
                $"pg_db.{Quote(Schema)}.{Quote(Table)}",
            DataSourceType.Postgres when Table != null => 
                $"pg_db.public.{Quote(Table)}",
            DataSourceType.MySql when Table != null => 
                $"mysql_db.{Quote(Table)}",
            
            // Cloud - same as files but with different protocols
            DataSourceType.S3 => $"read_parquet('{escaped}')", // Auto-detect format
            DataSourceType.Azure => $"read_parquet('{escaped}')",
            DataSourceType.Gcs => $"read_parquet('{escaped}')",
            DataSourceType.Http => GetHttpReadExpression(escaped),
            
            // Lakehouse
            DataSourceType.Delta => $"delta_scan('{escaped}')",
            DataSourceType.Iceberg => $"iceberg_scan('{escaped}')",
            
            _ => throw new NotSupportedException($"Source type {Type} not supported")
        };
    }

    /// <summary>
    /// Get extensions needed for this source
    /// </summary>
    public IEnumerable<string> GetRequiredExtensions()
    {
        return Type switch
        {
            DataSourceType.Excel => ["spatial"], // st_read is from spatial
            DataSourceType.Avro => ["avro"],
            DataSourceType.Sqlite => ["sqlite"],
            DataSourceType.Postgres => ["postgres"],
            DataSourceType.MySql => ["mysql"],
            DataSourceType.S3 or DataSourceType.Azure or DataSourceType.Gcs or DataSourceType.Http 
                => ["httpfs"],
            DataSourceType.Delta => ["delta"],
            DataSourceType.Iceberg => ["iceberg"],
            _ => []
        };
    }

    /// <summary>
    /// Get ATTACH statement if needed
    /// </summary>
    public string? GetAttachStatement()
    {
        return Type switch
        {
            DataSourceType.Sqlite => $"ATTACH '{Source.Replace("'", "''")}' AS sqlite_db (TYPE sqlite)",
            DataSourceType.Postgres => $"ATTACH '{Source}' AS pg_db (TYPE postgres)",
            DataSourceType.MySql => $"ATTACH '{Source}' AS mysql_db (TYPE mysql)",
            _ => null
        };
    }

    private string GetHttpReadExpression(string url)
    {
        var lower = url.ToLowerInvariant();
        if (lower.EndsWith(".parquet")) return $"read_parquet('{url}')";
        if (lower.EndsWith(".json") || lower.EndsWith(".ndjson")) return $"read_json_auto('{url}')";
        return $"read_csv_auto('{url}')"; // Default to CSV
    }

    private static string Quote(string identifier) => $"\"{identifier}\"";

    /// <summary>
    /// For log files, ensures the file is converted to Parquet.
    /// Call this before GetReadExpression() for Log type sources.
    /// </summary>
    /// <returns>True if conversion succeeded or was not needed</returns>
    public bool EnsureLogConverted()
    {
        if (Type != DataSourceType.Log) return true;
        if (!string.IsNullOrEmpty(ConvertedParquetPath) && File.Exists(ConvertedParquetPath)) return true;
        
        // Use LogNormalizer to convert
        if (Services.LogNormalizer.TryNormalizeToParquet(Source, out var parquetPath, out var format))
        {
            ConvertedParquetPath = parquetPath;
            DetectedLogFormat = format;
            return true;
        }
        
        return false;
    }

    /// <summary>
    /// Parse a source string and detect the type
    /// </summary>
    public static DataSource Parse(string source, string? table = null)
    {
        var ds = new DataSource { Source = source, Table = table };
        
        // Cloud protocols
        if (source.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
        {
            ds.Type = DataSourceType.S3;
            ds.Type = DetectCloudFileType(source, DataSourceType.S3);
            return ds;
        }
        if (source.StartsWith("az://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("abfss://", StringComparison.OrdinalIgnoreCase))
        {
            ds.Type = DetectCloudFileType(source, DataSourceType.Azure);
            return ds;
        }
        if (source.StartsWith("gs://", StringComparison.OrdinalIgnoreCase))
        {
            ds.Type = DetectCloudFileType(source, DataSourceType.Gcs);
            return ds;
        }
        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            ds.Type = DataSourceType.Http;
            return ds;
        }

        // Database connection strings
        if (source.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("host=", StringComparison.OrdinalIgnoreCase) && 
            source.Contains("dbname=", StringComparison.OrdinalIgnoreCase))
        {
            ds.Type = DataSourceType.Postgres;
            return ds;
        }
        if (source.StartsWith("mysql://", StringComparison.OrdinalIgnoreCase) ||
            (source.Contains("host=", StringComparison.OrdinalIgnoreCase) && 
             source.Contains("database=", StringComparison.OrdinalIgnoreCase) &&
             !source.Contains("dbname=", StringComparison.OrdinalIgnoreCase)))
        {
            ds.Type = DataSourceType.MySql;
            return ds;
        }

        // Local files - detect by extension
        var ext = Path.GetExtension(source).ToLowerInvariant();
        ds.Type = ext switch
        {
            ".csv" or ".tsv" => DataSourceType.Csv,
            ".parquet" => DataSourceType.Parquet,
            ".json" or ".ndjson" or ".jsonl" => DataSourceType.Json,
            ".xlsx" or ".xls" => DataSourceType.Excel,
            ".avro" => DataSourceType.Avro,
            ".log" => DataSourceType.Log, // Apache/IIS log files
            ".sqlite" or ".db" or ".sqlite3" => DataSourceType.Sqlite,
            _ when Directory.Exists(source) && File.Exists(Path.Combine(source, "_delta_log")) 
                => DataSourceType.Delta,
            _ when Directory.Exists(source) && Directory.Exists(Path.Combine(source, "metadata"))
                => DataSourceType.Iceberg,
            _ => DataSourceType.Csv // Default fallback
        };

        return ds;
    }

    private static DataSourceType DetectCloudFileType(string url, DataSourceType cloudType)
    {
        var lower = url.ToLowerInvariant();
        if (lower.Contains("_delta_log") || lower.EndsWith("/delta") || lower.Contains("/delta/"))
            return DataSourceType.Delta;
        if (lower.Contains("/metadata/") || lower.EndsWith("/iceberg"))
            return DataSourceType.Iceberg;
        return cloudType; // Return base cloud type, will auto-detect format
    }
}

public enum DataSourceType
{
    // Files
    Csv,
    Parquet,
    Json,
    Excel,
    Avro,
    Log, // Apache/IIS log files (converted to Parquet on-the-fly)
    
    // Databases
    Sqlite,
    Postgres,
    MySql,
    
    // Cloud
    S3,
    Azure,
    Gcs,
    Http,
    
    // Lakehouse
    Delta,
    Iceberg
}

/// <summary>
/// Information about a table/dataset in a source
/// </summary>
public class TableInfo
{
    public string Name { get; set; } = "";
    public string? Schema { get; set; }
    public long? RowCount { get; set; }
    public List<ColumnInfo> Columns { get; set; } = [];
}

/// <summary>
/// Basic column info from DESCRIBE
/// </summary>
public class ColumnInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
}
