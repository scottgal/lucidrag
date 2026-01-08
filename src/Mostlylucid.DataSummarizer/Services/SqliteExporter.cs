using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using Mostlylucid.DataSummarizer.Models;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Exports data to SQLite with intelligent schema and index creation based on profile stats.
/// Uses DuckDB's SQLite extension to perform the export.
/// </summary>
public class SqliteExporter
{
    private readonly bool _verbose;

    public SqliteExporter(bool verbose = false)
    {
        _verbose = verbose;
    }

    /// <summary>
    /// Export data from any supported source to a SQLite database.
    /// Creates table with appropriate types and indexes based on profile analysis.
    /// </summary>
    public async Task<SqliteExportResult> ExportAsync(
        string sourcePath,
        string sqlitePath,
        string? tableName = null,
        DataProfile? profile = null,
        bool createIndexes = true,
        bool overwrite = false)
    {
        var result = new SqliteExportResult
        {
            SourcePath = sourcePath,
            SqlitePath = sqlitePath,
            StartedAt = DateTime.UtcNow
        };

        try
        {
            // Derive table name from file if not specified
            tableName ??= SanitizeTableName(Path.GetFileNameWithoutExtension(sourcePath));
            result.TableName = tableName;

            // Handle existing file
            if (File.Exists(sqlitePath))
            {
                if (overwrite)
                {
                    File.Delete(sqlitePath);
                    if (_verbose) Console.WriteLine($"[SqliteExporter] Deleted existing: {sqlitePath}");
                }
                else
                {
                    result.Success = false;
                    result.Error = $"File already exists: {sqlitePath}. Use --overwrite to replace.";
                    return result;
                }
            }

            // Parse source and get read expression
            var dataSource = DataSource.Parse(sourcePath);
            var readExpr = dataSource.GetReadExpression();

            // Profile the data if not provided
            if (profile == null)
            {
                if (_verbose) Console.WriteLine("[SqliteExporter] Profiling source data...");
                using var profiler = new DuckDbProfiler(_verbose, new ProfileOptions { FastMode = true });
                profile = await profiler.ProfileAsync(sourcePath);
            }

            result.RowCount = profile.RowCount;
            result.ColumnCount = profile.ColumnCount;

            // Create SQLite database using DuckDB
            await using var conn = new DuckDBConnection("DataSource=:memory:");
            await conn.OpenAsync();

            // Install and load SQLite extension
            await ExecuteAsync(conn, "INSTALL sqlite; LOAD sqlite;");

            // Load required extensions for source
            foreach (var ext in dataSource.GetRequiredExtensions())
            {
                await ExecuteAsync(conn, $"INSTALL {ext}; LOAD {ext};");
            }

            // Attach source if needed (for database sources)
            var attachStmt = dataSource.GetAttachStatement();
            if (attachStmt != null)
            {
                await ExecuteAsync(conn, attachStmt);
            }

            // Create SQLite database and export
            await ExecuteAsync(conn, $"ATTACH '{sqlitePath.Replace("'", "''")}' AS sqlite_db (TYPE sqlite)");

            // Build CREATE TABLE with optimized types
            var createSql = BuildCreateTableSql(tableName, profile);
            if (_verbose) Console.WriteLine($"[SqliteExporter] Creating table:\n{createSql}");
            await ExecuteAsync(conn, createSql);

            // Insert data
            var insertSql = $"INSERT INTO sqlite_db.\"{tableName}\" SELECT * FROM {readExpr}";
            if (_verbose) Console.WriteLine($"[SqliteExporter] Inserting data...");
            await ExecuteAsync(conn, insertSql);

            // Detach from DuckDB first
            await ExecuteAsync(conn, "DETACH sqlite_db");
            
            // Create indexes using native SQLite (DuckDB's sqlite extension doesn't support CREATE INDEX)
            if (createIndexes)
            {
                var indexes = SuggestIndexes(tableName, profile);
                result.IndexesCreated = new List<string>();
                
                await using var sqliteConn = new SqliteConnection($"Data Source={sqlitePath}");
                await sqliteConn.OpenAsync();
                
                foreach (var (indexName, columnName, reason) in indexes)
                {
                    try
                    {
                        if (_verbose) Console.WriteLine($"[SqliteExporter] Creating index: {indexName}");
                        
                        await using var cmd = sqliteConn.CreateCommand();
                        cmd.CommandText = $"CREATE INDEX \"{indexName}\" ON \"{tableName}\" (\"{columnName}\")";
                        await cmd.ExecuteNonQueryAsync();
                        
                        result.IndexesCreated.Add(indexName);
                    }
                    catch (Exception ex)
                    {
                        if (_verbose) Console.WriteLine($"[SqliteExporter] Failed to create index {indexName}: {ex.Message}");
                    }
                }
            }

            result.Success = true;
            result.CompletedAt = DateTime.UtcNow;

            if (_verbose)
            {
                Console.WriteLine($"[SqliteExporter] Export complete: {result.RowCount:N0} rows, {result.ColumnCount} columns");
                if (result.IndexesCreated?.Count > 0)
                    Console.WriteLine($"[SqliteExporter] Created {result.IndexesCreated.Count} indexes");
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            result.CompletedAt = DateTime.UtcNow;
        }

        return result;
    }

    /// <summary>
    /// Build CREATE TABLE SQL with SQLite-appropriate types based on profile
    /// </summary>
    private string BuildCreateTableSql(string tableName, DataProfile profile)
    {
        var columns = new List<string>();
        
        foreach (var col in profile.Columns)
        {
            var sqliteType = MapToSqliteType(col);
            var nullable = col.NullPercent > 0 ? "" : " NOT NULL";
            columns.Add($"    \"{col.Name}\" {sqliteType}{nullable}");
        }

        return $"CREATE TABLE sqlite_db.\"{tableName}\" (\n{string.Join(",\n", columns)}\n)";
    }

    /// <summary>
    /// Map column profile to appropriate SQLite type
    /// </summary>
    private string MapToSqliteType(ColumnProfile col)
    {
        // Check semantic role first
        if (col.SemanticRole == SemanticRole.Identifier || col.InferredType == ColumnType.Id)
        {
            // Could be INTEGER or TEXT depending on content
            if (col.Min.HasValue && col.Max.HasValue && col.Min >= 0)
                return "INTEGER";
            return "TEXT";
        }

        return col.InferredType switch
        {
            ColumnType.Numeric when IsInteger(col) => "INTEGER",
            ColumnType.Numeric => "REAL",
            ColumnType.Boolean => "INTEGER", // SQLite uses 0/1 for bool
            ColumnType.DateTime => "TEXT", // Store as ISO8601 text
            ColumnType.Categorical when col.UniqueCount <= 2 && IsBooleanLike(col) => "INTEGER",
            _ => "TEXT"
        };
    }

    /// <summary>
    /// Check if numeric column is integer (no decimal places)
    /// </summary>
    private bool IsInteger(ColumnProfile col)
    {
        if (!col.Min.HasValue || !col.Max.HasValue) return false;
        
        // Check if min/max are whole numbers
        return col.Min.Value == Math.Floor(col.Min.Value) && 
               col.Max.Value == Math.Floor(col.Max.Value) &&
               (col.Mean == null || col.Mean.Value == Math.Floor(col.Mean.Value));
    }

    /// <summary>
    /// Check if categorical column is boolean-like (0/1, true/false, yes/no)
    /// </summary>
    private bool IsBooleanLike(ColumnProfile col)
    {
        if (col.TopValues == null || col.TopValues.Count != 2) return false;
        
        var values = col.TopValues.Select(v => v.Value?.ToLowerInvariant()).ToHashSet();
        return values.SetEquals(new[] { "0", "1" }) ||
               values.SetEquals(new[] { "true", "false" }) ||
               values.SetEquals(new[] { "yes", "no" }) ||
               values.SetEquals(new[] { "y", "n" });
    }

    /// <summary>
    /// Suggest indexes based on profile statistics
    /// </summary>
    private List<(string IndexName, string ColumnName, string Reason)> SuggestIndexes(string tableName, DataProfile profile)
    {
        var indexes = new List<(string IndexName, string ColumnName, string Reason)>();

        foreach (var col in profile.Columns)
        {
            var shouldIndex = false;
            var reason = "";

            // Index ID columns (often used in JOINs)
            if (col.SemanticRole == SemanticRole.Identifier || col.InferredType == ColumnType.Id)
            {
                shouldIndex = true;
                reason = "identifier";
            }
            // Index low-cardinality categorical columns (good for filtering)
            else if (col.InferredType == ColumnType.Categorical && 
                     col.UniqueCount >= 2 && 
                     col.UniqueCount <= 100 &&
                     col.UniquePercent < 50)
            {
                shouldIndex = true;
                reason = "low-cardinality categorical";
            }
            // Index date columns (common for range queries)
            else if (col.InferredType == ColumnType.DateTime)
            {
                shouldIndex = true;
                reason = "datetime";
            }
            // Index foreign key-like columns (names ending in _id, Id, etc.)
            else if (col.Name.EndsWith("_id", StringComparison.OrdinalIgnoreCase) ||
                     col.Name.EndsWith("Id", StringComparison.Ordinal) ||
                     col.Name.EndsWith("_key", StringComparison.OrdinalIgnoreCase))
            {
                shouldIndex = true;
                reason = "foreign key pattern";
            }
            // Index boolean-like columns (useful for filtering active/inactive)
            else if (col.InferredType == ColumnType.Boolean ||
                     (col.UniqueCount == 2 && IsBooleanLike(col)))
            {
                shouldIndex = true;
                reason = "boolean filter";
            }

        if (shouldIndex)
                {
                    var indexName = $"idx_{tableName}_{SanitizeIdentifier(col.Name)}";
                    indexes.Add((indexName, col.Name, reason));
                    
                    if (_verbose) Console.WriteLine($"[SqliteExporter] Will index '{col.Name}' ({reason})");
                }
        }

        return indexes;
    }

    /// <summary>
    /// Sanitize string for use as table name
    /// </summary>
    private string SanitizeTableName(string name)
    {
        // Remove invalid characters, replace spaces with underscores
        var sanitized = new string(name
            .Replace(' ', '_')
            .Replace('-', '_')
            .Where(c => char.IsLetterOrDigit(c) || c == '_')
            .ToArray());
        
        // Ensure doesn't start with number
        if (sanitized.Length > 0 && char.IsDigit(sanitized[0]))
            sanitized = "_" + sanitized;
        
        return string.IsNullOrEmpty(sanitized) ? "data" : sanitized;
    }

    /// <summary>
    /// Sanitize string for use as identifier (index name)
    /// </summary>
    private string SanitizeIdentifier(string name)
    {
        return new string(name
            .Replace(' ', '_')
            .Where(c => char.IsLetterOrDigit(c) || c == '_')
            .ToArray())
            .ToLowerInvariant();
    }

    private async Task ExecuteAsync(DuckDBConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Result of SQLite export operation
/// </summary>
public class SqliteExportResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string SourcePath { get; set; } = "";
    public string SqlitePath { get; set; } = "";
    public string TableName { get; set; } = "";
    public long RowCount { get; set; }
    public int ColumnCount { get; set; }
    public List<string>? IndexesCreated { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan Duration => (CompletedAt ?? DateTime.UtcNow) - StartedAt;
}
