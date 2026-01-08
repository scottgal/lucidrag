using DuckDB.NET.Data;
using Mostlylucid.DataSummarizer.Models;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Manages DuckDB connections with extension loading and source attachment.
/// Supports files, databases, cloud storage, and lakehouse formats.
/// </summary>
public class DuckDbConnectionManager : IDisposable
{
    private DuckDBConnection? _connection;
    private readonly HashSet<string> _loadedExtensions = new();
    private readonly bool _verbose;

    public DuckDbConnectionManager(bool verbose = false)
    {
        _verbose = verbose;
    }

    public DuckDBConnection Connection => _connection 
        ?? throw new InvalidOperationException("Connection not initialized. Call ConnectAsync first.");

    /// <summary>
    /// Initialize connection and prepare for the given data source
    /// </summary>
    public async Task ConnectAsync(DataSource source)
    {
        _connection = new DuckDBConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        // Load required extensions
        foreach (var ext in source.GetRequiredExtensions())
        {
            await LoadExtensionAsync(ext);
        }

        // Attach database if needed
        var attachStmt = source.GetAttachStatement();
        if (attachStmt != null)
        {
            if (_verbose) Console.WriteLine($"[DuckDB] {attachStmt}");
            await ExecuteAsync(attachStmt);
        }
    }

    /// <summary>
    /// Load an extension if not already loaded
    /// </summary>
    public async Task LoadExtensionAsync(string extension)
    {
        if (_loadedExtensions.Contains(extension)) return;

        try
        {
            if (_verbose) Console.WriteLine($"[DuckDB] Loading extension: {extension}");
            
            // Install and load
            await ExecuteAsync($"INSTALL {extension}");
            await ExecuteAsync($"LOAD {extension}");
            
            _loadedExtensions.Add(extension);
        }
        catch (Exception ex)
        {
            if (_verbose) Console.WriteLine($"[DuckDB] Extension {extension} failed: {ex.Message}");
            // Some extensions auto-load, so failure might be OK
        }
    }

    /// <summary>
    /// List tables in an attached database
    /// </summary>
    public async Task<List<TableInfo>> ListTablesAsync(DataSource source)
    {
        var tables = new List<TableInfo>();
        
        string sql = source.Type switch
        {
            DataSourceType.Sqlite => "SELECT name FROM sqlite_db.sqlite_master WHERE type='table'",
            DataSourceType.Postgres => @"
                SELECT table_schema, table_name 
                FROM pg_db.information_schema.tables 
                WHERE table_schema NOT IN ('pg_catalog', 'information_schema')",
            DataSourceType.MySql => "SHOW TABLES FROM mysql_db",
            _ => throw new NotSupportedException($"ListTables not supported for {source.Type}")
        };

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var info = new TableInfo();
            
            if (source.Type == DataSourceType.Postgres && reader.FieldCount >= 2)
            {
                info.Schema = reader.GetString(0);
                info.Name = reader.GetString(1);
            }
            else
            {
                info.Name = reader.GetString(0);
            }
            
            tables.Add(info);
        }

        return tables;
    }

    /// <summary>
    /// Get column information for a source
    /// </summary>
    public async Task<List<Models.ColumnInfo>> DescribeAsync(DataSource source)
    {
        var columns = new List<Models.ColumnInfo>();
        var readExpr = source.GetReadExpression();
        
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = $"DESCRIBE SELECT * FROM {readExpr}";
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            columns.Add(new Models.ColumnInfo
            {
                Name = reader.GetString(0),
                Type = reader.GetString(1)
            });
        }

        return columns;
    }

    /// <summary>
    /// Get row count for a source
    /// </summary>
    public async Task<long> CountAsync(DataSource source)
    {
        var readExpr = source.GetReadExpression();
        return await ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {readExpr}");
    }

    /// <summary>
    /// Run SUMMARIZE on the source
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> SummarizeAsync(DataSource source)
    {
        var readExpr = source.GetReadExpression();
        var results = new List<Dictionary<string, object?>>();
        
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = $"SUMMARIZE SELECT * FROM {readExpr}";
        await using var reader = await cmd.ExecuteReaderAsync();

        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(i => reader.GetName(i))
            .ToList();

        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            results.Add(row);
        }

        return results;
    }

    /// <summary>
    /// Execute a query and return results
    /// </summary>
    public async Task<QueryResult> QueryAsync(string sql)
    {
        var result = new QueryResult { Sql = sql };
        
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync();

            // Get columns
            for (int i = 0; i < reader.FieldCount; i++)
            {
                result.Columns.Add(reader.GetName(i));
            }

            // Get rows (limit to 1000)
            while (await reader.ReadAsync() && result.Rows.Count < 1000)
            {
                var row = new List<object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row.Add(reader.IsDBNull(i) ? null : reader.GetValue(i));
                }
                result.Rows.Add(row);
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Validate SQL without executing
    /// </summary>
    public async Task<string?> ValidateSqlAsync(string sql)
    {
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = $"EXPLAIN {sql}";
            await cmd.ExecuteNonQueryAsync();
            return null; // Valid
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Get sample rows
    /// </summary>
    public async Task<List<Dictionary<string, string>>> GetSampleRowsAsync(DataSource source, int limit = 5)
    {
        var readExpr = source.GetReadExpression();
        var results = new List<Dictionary<string, string>>();
        
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {readExpr} LIMIT {limit}";
        await using var reader = await cmd.ExecuteReaderAsync();

        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(i => reader.GetName(i))
            .ToList();

        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, string>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                row[columns[i]] = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i)?.ToString() ?? "";
            }
            results.Add(row);
        }

        return results;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ExecuteScalarAsync<T>(string sql)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        
        if (result == null || result == DBNull.Value)
            return default!;
        
        return (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}

/// <summary>
/// Query execution result
/// </summary>
public class QueryResult
{
    public bool Success { get; set; }
    public string Sql { get; set; } = "";
    public string? Error { get; set; }
    public List<string> Columns { get; set; } = [];
    public List<List<object?>> Rows { get; set; } = [];
    public TimeSpan ExecutionTime { get; set; }
}
