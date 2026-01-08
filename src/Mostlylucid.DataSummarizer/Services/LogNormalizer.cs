using DuckDB.NET.Data;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Normalizes various log formats to Parquet using DuckDB SQL with regex extraction.
/// Supports: Apache error logs, Apache access/combined logs, IIS W3C logs.
/// </summary>
internal static class LogNormalizer
{
    public enum LogFormat
    {
        Unknown,
        ApacheError,
        ApacheAccess,
        IisW3c
    }

    public static bool TryNormalizeToParquet(string path, out string parquetPath, out string format)
    {
        parquetPath = string.Empty;
        format = string.Empty;
        if (!File.Exists(path)) return false;

        var firstLines = File.ReadLines(path).Take(20).ToList();
        var detectedFormat = DetectFormat(firstLines);

        if (detectedFormat == LogFormat.Unknown)
            return false;

        format = detectedFormat.ToString().ToLowerInvariant();
        parquetPath = detectedFormat switch
        {
            LogFormat.ApacheError => ConvertApacheErrorToParquet(path),
            LogFormat.ApacheAccess => ConvertApacheAccessToParquet(path),
            LogFormat.IisW3c => ConvertIisToParquet(path, firstLines),
            _ => string.Empty
        };

        return !string.IsNullOrEmpty(parquetPath) && File.Exists(parquetPath);
    }

    public static LogFormat DetectFormat(IEnumerable<string> lines)
    {
        var lineList = lines.ToList();

        // IIS W3C: has #Fields: directive
        if (lineList.Any(l => l.StartsWith("#Fields:", StringComparison.OrdinalIgnoreCase)))
            return LogFormat.IisW3c;

        // Apache error log: [date] [level] pattern
        // Example: [Thu Jun 09 06:07:04 2005] [notice] LDAP: Built with OpenLDAP LDAP SDK
        if (lineList.Any(l => IsApacheErrorLine(l)))
            return LogFormat.ApacheError;

        // Apache access/combined: IP at start, quoted request, status code
        // Example: 192.168.1.1 - - [10/Oct/2000:13:55:36 -0700] "GET /index.html HTTP/1.0" 200 1043
        if (lineList.Any(l => IsApacheAccessLine(l)))
            return LogFormat.ApacheAccess;

        return LogFormat.Unknown;
    }

    private static bool IsApacheErrorLine(string line)
    {
        // Pattern: [Day Mon DD HH:MM:SS YYYY] [level] ...
        if (!line.StartsWith('[')) return false;
        var closeBracket = line.IndexOf(']');
        if (closeBracket < 10) return false;
        var afterFirst = line[(closeBracket + 1)..].TrimStart();
        return afterFirst.StartsWith('[') && afterFirst.Contains(']');
    }

    private static bool IsApacheAccessLine(string line)
    {
        // Must have: IP at start, quoted section, and a 3-digit status
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) return false;
        var parts = line.Split(' ');
        if (parts.Length < 7) return false;
        // First part should look like IP
        if (!parts[0].Contains('.') && !parts[0].Contains(':')) return false;
        // Should have quoted request
        return line.Contains('"') && System.Text.RegularExpressions.Regex.IsMatch(line, @"""\s+\d{3}\s");
    }

    private static string ConvertApacheErrorToParquet(string path)
    {
        var parquet = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(path) + "_error.parquet");
        var escapedPath = path.Replace("'", "''").Replace("\\", "/");
        var escapedParquet = parquet.Replace("'", "''").Replace("\\", "/");

        // Apache error log format:
        // [Thu Jun 09 06:07:04 2005] [notice] message
        // [Thu Jun 09 07:11:21 2005] [error] [client 204.100.200.22] Directory index forbidden
        var sql = $$"""
            COPY (
              WITH raw AS (
                SELECT column0 AS line FROM read_csv('{{escapedPath}}', 
                  header=false, 
                  columns={'column0': 'VARCHAR'},
                  quote='',
                  escape='',
                  delim=E'\x01'
                )
                WHERE line IS NOT NULL AND length(trim(line)) > 0
              )
              SELECT
                regexp_extract(line, '^\[([^\]]+)\]', 1) AS timestamp_raw,
                regexp_extract(line, '^\[[^\]]+\]\s+\[(\w+)\]', 1) AS level,
                regexp_extract(line, '\[client\s+([^\]]+)\]', 1) AS client_ip,
                CASE 
                  WHEN line LIKE '%[client %' 
                  THEN regexp_extract(line, '\[client\s+[^\]]+\]\s+(.*)', 1)
                  ELSE regexp_extract(line, '^\[[^\]]+\]\s+\[\w+\]\s+(.*)', 1)
                END AS message,
                try_strptime(regexp_extract(line, '^\[([^\]]+)\]', 1), '%a %b %d %H:%M:%S %Y') AS timestamp
              FROM raw
              WHERE regexp_extract(line, '^\[([^\]]+)\]', 1) != ''
            ) TO '{{escapedParquet}}' (FORMAT PARQUET);
            """;

        ExecuteDuckDbSql(sql);
        return parquet;
    }

    private static string ConvertApacheAccessToParquet(string path)
    {
        var parquet = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(path) + "_access.parquet");
        var escapedPath = path.Replace("'", "''").Replace("\\", "/");
        var escapedParquet = parquet.Replace("'", "''").Replace("\\", "/");

        // Apache combined log format:
        // 127.0.0.1 - frank [10/Oct/2000:13:55:36 -0700] "GET /apache_pb.gif HTTP/1.0" 200 2326 "http://..." "Mozilla/..."
        var sql = $$"""
            COPY (
              WITH raw AS (
                SELECT column0 AS line FROM read_csv('{{escapedPath}}', 
                  header=false, 
                  columns={'column0': 'VARCHAR'},
                  quote='',
                  escape='',
                  delim=E'\x01'
                )
                WHERE line IS NOT NULL AND length(trim(line)) > 0 AND line NOT LIKE '#%'
              )
              SELECT
                regexp_extract(line, '^(\S+)', 1) AS client_ip,
                regexp_extract(line, '^\S+\s+(\S+)', 1) AS ident,
                regexp_extract(line, '^\S+\s+\S+\s+(\S+)', 1) AS user,
                regexp_extract(line, '\[([^\]]+)\]', 1) AS timestamp_raw,
                regexp_extract(line, '"(\w+)\s+', 1) AS method,
                regexp_extract(line, '"\w+\s+(\S+)', 1) AS url,
                regexp_extract(line, '"\w+\s+\S+\s+(\S+)"', 1) AS protocol,
                TRY_CAST(regexp_extract(line, '"\s+(\d+)', 1) AS INTEGER) AS status,
                TRY_CAST(NULLIF(regexp_extract(line, '"\s+\d+\s+(\S+)', 1), '-') AS BIGINT) AS bytes,
                regexp_extract(line, '"\s+\d+\s+\S+\s+"([^"]*)"', 1) AS referer,
                regexp_extract(line, '"\s+\d+\s+\S+\s+"[^"]*"\s+"([^"]*)"', 1) AS user_agent,
                try_strptime(regexp_extract(line, '\[([^\]]+)\]', 1), '%d/%b/%Y:%H:%M:%S %z') AS timestamp
              FROM raw
              WHERE regexp_extract(line, '^(\S+)', 1) != ''
            ) TO '{{escapedParquet}}' (FORMAT PARQUET);
            """;

        ExecuteDuckDbSql(sql);
        return parquet;
    }

    private static string ConvertIisToParquet(string path, List<string> firstLines)
    {
        var parquet = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(path) + "_iis.parquet");
        var escapedPath = path.Replace("'", "''").Replace("\\", "/");
        var escapedParquet = parquet.Replace("'", "''").Replace("\\", "/");

        // Find #Fields: line to get column names
        var fieldsLine = firstLines.FirstOrDefault(l => l.StartsWith("#Fields:", StringComparison.OrdinalIgnoreCase));
        if (fieldsLine is null)
        {
            // Try reading more of the file
            fieldsLine = File.ReadLines(path)
                .Take(100)
                .FirstOrDefault(l => l.StartsWith("#Fields:", StringComparison.OrdinalIgnoreCase));
        }

        if (fieldsLine is null) return string.Empty;

        var fields = fieldsLine[8..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        // Sanitize field names for SQL (replace hyphens with underscores, handle parentheses)
        var sanitizedFields = fields.Select(f => SanitizeColumnName(f)).ToList();
        var columnDefs = string.Join(", ", sanitizedFields.Select(f => $"\"{f}\" VARCHAR"));

        // Count comment lines to skip
        var skipLines = File.ReadLines(path).TakeWhile(l => l.StartsWith('#')).Count();

        var sql = $$"""
            COPY (
              SELECT * FROM read_csv('{{escapedPath}}',
                header=false,
                skip={{skipLines}},
                columns={{{columnDefs}}},
                delim=' ',
                quote='"',
                ignore_errors=true
              )
              WHERE "{{sanitizedFields[0]}}" NOT LIKE '#%'
            ) TO '{{escapedParquet}}' (FORMAT PARQUET);
            """;

        ExecuteDuckDbSql(sql);
        return parquet;
    }

    private static string SanitizeColumnName(string name)
    {
        // Replace common problematic characters
        return name
            .Replace("-", "_")
            .Replace("(", "_")
            .Replace(")", "")
            .Replace(".", "_");
    }

    private static void ExecuteDuckDbSql(string sql)
    {
        using var conn = new DuckDBConnection("DataSource=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
