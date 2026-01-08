using DuckDB.NET.Data;
using Mostlylucid.DataSummarizer.Models;
using System.Text.RegularExpressions;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Intelligent data search that adapts strategy based on column types and statistics.
/// Supports full-text search, exact match, range queries, fuzzy matching, and natural language queries.
/// </summary>
public class DataSearcher : IDisposable
{
    private readonly bool _verbose;
    private DuckDBConnection? _conn;

    public DataSearcher(bool verbose = false)
    {
        _verbose = verbose;
    }

    /// <summary>
    /// Parse a natural language query and execute an intelligent search.
    /// Examples:
    /// - "show me ages of people named dave or david"
    /// - "find customers where balance > 1000"
    /// - "search for emails containing @gmail"
    /// - "people older than 30 in France"
    /// </summary>
    public async Task<SearchResult> NaturalSearchAsync(
        string sourcePath,
        string prompt,
        string? tableName = null,
        int limit = 100,
        DataProfile? profile = null)
    {
        var result = new SearchResult
        {
            SearchTerm = prompt,
            SourcePath = sourcePath,
            StartedAt = DateTime.UtcNow,
            IsNaturalLanguage = true
        };

        try
        {
            // Parse source
            var dataSource = DataSource.Parse(sourcePath, tableName);
            
            // Connect
            _conn = new DuckDBConnection("DataSource=:memory:");
            await _conn.OpenAsync();

            // Load extensions if needed
            foreach (var ext in dataSource.GetRequiredExtensions())
            {
                await ExecuteAsync($"INSTALL {ext}; LOAD {ext};");
            }

            // Attach if needed
            var attach = dataSource.GetAttachStatement();
            if (attach != null)
            {
                await ExecuteAsync(attach);
            }

            var readExpr = dataSource.GetReadExpression();

            // Profile if not provided
            if (profile == null)
            {
                if (_verbose) Console.WriteLine("[DataSearcher] Quick profiling for query understanding...");
                using var profiler = new DuckDbProfiler(_verbose, new ProfileOptions { FastMode = true });
                profile = await profiler.ProfileAsync(sourcePath, tableName);
            }

            // Parse natural language query
            var parsedQuery = ParseNaturalQuery(prompt, profile);
            result.ParsedQuery = parsedQuery;
            result.Strategies = parsedQuery.Conditions.Select(c => c.Description).ToList();

            if (_verbose)
            {
                Console.WriteLine($"[DataSearcher] Parsed query:");
                Console.WriteLine($"  Select: {string.Join(", ", parsedQuery.SelectColumns)}");
                Console.WriteLine($"  Conditions: {parsedQuery.Conditions.Count}");
                foreach (var cond in parsedQuery.Conditions)
                {
                    Console.WriteLine($"    - {cond.Description}");
                }
            }

            // Build SQL from parsed query
            var sql = BuildNaturalQuerySql(readExpr, parsedQuery, limit);
            result.Sql = sql;
            result.SearchedColumns = parsedQuery.Conditions.Select(c => c.ColumnName).Distinct().ToList();

            if (_verbose) Console.WriteLine($"[DataSearcher] SQL: {sql}");

            // Execute
            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync();

            var rows = new List<Dictionary<string, object?>>();
            var columnNames = Enumerable.Range(0, reader.FieldCount)
                .Select(i => reader.GetName(i))
                .ToList();

            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[columnNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }

            result.Rows = rows;
            result.MatchCount = rows.Count;
            result.Success = true;
            result.CompletedAt = DateTime.UtcNow;
            result.TotalMatches = rows.Count;
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
    /// Parse natural language query into structured components.
    /// </summary>
    private ParsedNaturalQuery ParseNaturalQuery(string prompt, DataProfile profile)
    {
        var query = new ParsedNaturalQuery { OriginalPrompt = prompt };
        var lower = prompt.ToLowerInvariant();
        
        // Build column name lookup (case-insensitive)
        var columnLookup = profile.Columns.ToDictionary(
            c => c.Name.ToLowerInvariant(), 
            c => c,
            StringComparer.OrdinalIgnoreCase);
        
        // Also build lookup by common variations
        var columnAliases = new Dictionary<string, ColumnProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in profile.Columns)
        {
            var name = col.Name.ToLowerInvariant();
            columnAliases[name] = col;
            // Add singular/plural variations
            if (name.EndsWith("s")) columnAliases[name.TrimEnd('s')] = col;
            else columnAliases[name + "s"] = col;
            // Add common abbreviations
            if (name == "firstname" || name == "first_name") columnAliases["name"] = col;
            if (name == "emailaddress" || name == "email_address") columnAliases["email"] = col;
        }

        // Detect SELECT columns: "show me the X of", "get X for", "what are the X"
        var selectPatterns = new[]
        {
            @"show\s+(?:me\s+)?(?:the\s+)?(\w+(?:\s+and\s+\w+)*)\s+(?:of|for|where)",
            @"get\s+(?:the\s+)?(\w+(?:\s+and\s+\w+)*)\s+(?:of|for|where)",
            @"what\s+(?:are\s+)?(?:the\s+)?(\w+(?:\s+and\s+\w+)*)\s+(?:of|for|where)",
            @"list\s+(?:the\s+)?(\w+(?:\s+and\s+\w+)*)\s+(?:of|for|where)",
            @"find\s+(?:the\s+)?(\w+(?:\s+and\s+\w+)*)\s+(?:of|for|where)"
        };

        foreach (var pattern in selectPatterns)
        {
            var match = Regex.Match(lower, pattern);
            if (match.Success)
            {
                var colNames = match.Groups[1].Value.Split(new[] { " and ", "," }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var colName in colNames)
                {
                    var trimmed = colName.Trim();
                    if (columnAliases.TryGetValue(trimmed, out var col))
                    {
                        query.SelectColumns.Add(col.Name);
                    }
                }
                break;
            }
        }

        // Parse conditions
        
        // Pattern: "named X" or "called X" or "name is X"
        var namedPattern = "(?:named|called|name\\s+(?:is|=|like))\\s+(\\w+(?:\\s+or\\s+\\w+)*)";
        var namedMatch = Regex.Match(lower, namedPattern);
        if (namedMatch.Success)
        {
            var nameCol = profile.Columns.FirstOrDefault(c => 
                c.Name.Contains("name", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains("firstname", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains("surname", StringComparison.OrdinalIgnoreCase));
            
            if (nameCol != null)
            {
                var values = namedMatch.Groups[1].Value.Split(new[] { " or " }, StringSplitOptions.RemoveEmptyEntries);
                query.Conditions.Add(new QueryCondition
                {
                    ColumnName = nameCol.Name,
                    Operator = values.Length > 1 ? "IN_LIKE" : "LIKE",
                    Values = values.Select(v => v.Trim()).ToList(),
                    Description = $"{nameCol.Name} matches: {string.Join(" or ", values)}"
                });
            }
        }

        // Pattern: "older than X" / "younger than X" / "age > X"
        var ageMatch = Regex.Match(lower, @"(?:older|greater|more)\s+than\s+(\d+)|age\s*[>]\s*(\d+)");
        if (ageMatch.Success)
        {
            var ageCol = profile.Columns.FirstOrDefault(c => 
                c.Name.Contains("age", StringComparison.OrdinalIgnoreCase));
            if (ageCol != null)
            {
                var value = ageMatch.Groups[1].Success ? ageMatch.Groups[1].Value : ageMatch.Groups[2].Value;
                query.Conditions.Add(new QueryCondition
                {
                    ColumnName = ageCol.Name,
                    Operator = ">",
                    Values = [value],
                    Description = $"{ageCol.Name} > {value}"
                });
            }
        }

        var youngerMatch = Regex.Match(lower, @"(?:younger|less|under)\s+than\s+(\d+)|age\s*[<]\s*(\d+)");
        if (youngerMatch.Success)
        {
            var ageCol = profile.Columns.FirstOrDefault(c => 
                c.Name.Contains("age", StringComparison.OrdinalIgnoreCase));
            if (ageCol != null)
            {
                var value = youngerMatch.Groups[1].Success ? youngerMatch.Groups[1].Value : youngerMatch.Groups[2].Value;
                query.Conditions.Add(new QueryCondition
                {
                    ColumnName = ageCol.Name,
                    Operator = "<",
                    Values = [value],
                    Description = $"{ageCol.Name} < {value}"
                });
            }
        }

        // Pattern: "in [location]" - for geography/country/city columns
        var inMatch = Regex.Match(lower, @"\bin\s+(\w+)(?:\s|$)");
        if (inMatch.Success)
        {
            var location = inMatch.Groups[1].Value;
            var geoCol = profile.Columns.FirstOrDefault(c =>
                c.Name.Contains("country", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains("city", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains("geography", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains("location", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains("region", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains("state", StringComparison.OrdinalIgnoreCase));
            
            if (geoCol != null)
            {
                query.Conditions.Add(new QueryCondition
                {
                    ColumnName = geoCol.Name,
                    Operator = "ILIKE",
                    Values = [location],
                    Description = $"{geoCol.Name} = {location}"
                });
            }
        }

        // Pattern: "where column > value" / "column = value"
        var wherePattern = "where\\s+(\\w+)\\s*(>|<|>=|<=|=|!=|<>|like|contains)\\s*['\"]?(\\w+)['\"]?";
        var whereMatch = Regex.Match(lower, wherePattern);
        if (whereMatch.Success)
        {
            var colName = whereMatch.Groups[1].Value;
            var op = whereMatch.Groups[2].Value.ToUpperInvariant();
            var value = whereMatch.Groups[3].Value;
            
            if (columnAliases.TryGetValue(colName, out var col))
            {
                if (op == "CONTAINS") op = "ILIKE";
                var condValue = op == "ILIKE" ? $"%{value}%" : value;
                query.Conditions.Add(new QueryCondition
                {
                    ColumnName = col.Name,
                    Operator = op,
                    Values = new List<string> { condValue },
                    Description = $"{col.Name} {op} {value}"
                });
            }
        }

        // Pattern: "containing X" / "with X" for text search
        var containsPattern = "(?:containing|contains|with)\\s+['\"]?([\\w@.]+)['\"]?";
        var containsMatch = Regex.Match(lower, containsPattern);
        if (containsMatch.Success)
        {
            var searchVal = containsMatch.Groups[1].Value;
            // Find best text column
            var textCol = profile.Columns.FirstOrDefault(c => 
                c.InferredType == ColumnType.Text || 
                (c.InferredType == ColumnType.Categorical && c.AvgLength > 10));
            
            if (textCol != null)
            {
                query.Conditions.Add(new QueryCondition
                {
                    ColumnName = textCol.Name,
                    Operator = "ILIKE",
                    Values = [$"%{searchVal}%"],
                    Description = $"{textCol.Name} contains '{searchVal}'"
                });
            }
        }

        // Pattern: "balance/amount/salary > X" for numeric columns
        var numericMatch = Regex.Match(lower, @"(\w+)\s*(>|<|>=|<=|=)\s*(\d+(?:\.\d+)?)");
        if (numericMatch.Success && query.Conditions.Count == 0)
        {
            var colName = numericMatch.Groups[1].Value;
            var op = numericMatch.Groups[2].Value;
            var value = numericMatch.Groups[3].Value;
            
            if (columnAliases.TryGetValue(colName, out var col) && col.InferredType == ColumnType.Numeric)
            {
                query.Conditions.Add(new QueryCondition
                {
                    ColumnName = col.Name,
                    Operator = op,
                    Values = [value],
                    Description = $"{col.Name} {op} {value}"
                });
            }
        }
        
        // Pattern: "column over/above/more than X" or "column under/below/less than X" for any numeric column
        var overMatch = Regex.Match(lower, @"(\w+)\s+(?:over|above|greater\s+than|more\s+than)\s+(\d+(?:\.\d+)?)");
        if (overMatch.Success)
        {
            var colName = overMatch.Groups[1].Value;
            var value = overMatch.Groups[2].Value;
            
            if (columnAliases.TryGetValue(colName, out var col) && col.InferredType == ColumnType.Numeric)
            {
                // Don't duplicate if already added by age pattern
                if (!query.Conditions.Any(c => c.ColumnName == col.Name && c.Operator == ">"))
                {
                    query.Conditions.Add(new QueryCondition
                    {
                        ColumnName = col.Name,
                        Operator = ">",
                        Values = [value],
                        Description = $"{col.Name} > {value}"
                    });
                }
            }
        }
        
        var underMatch = Regex.Match(lower, @"(\w+)\s+(?:under|below|less\s+than)\s+(\d+(?:\.\d+)?)");
        if (underMatch.Success)
        {
            var colName = underMatch.Groups[1].Value;
            var value = underMatch.Groups[2].Value;
            
            if (columnAliases.TryGetValue(colName, out var col) && col.InferredType == ColumnType.Numeric)
            {
                // Don't duplicate if already added by age pattern
                if (!query.Conditions.Any(c => c.ColumnName == col.Name && c.Operator == "<"))
                {
                    query.Conditions.Add(new QueryCondition
                    {
                        ColumnName = col.Name,
                        Operator = "<",
                        Values = [value],
                        Description = $"{col.Name} < {value}"
                    });
                }
            }
        }

        // If no select columns specified, include all
        if (query.SelectColumns.Count == 0)
        {
            query.SelectColumns.Add("*");
        }

        return query;
    }

    /// <summary>
    /// Build SQL from parsed natural language query.
    /// </summary>
    private string BuildNaturalQuerySql(string readExpr, ParsedNaturalQuery query, int limit)
    {
        var select = query.SelectColumns.Contains("*") 
            ? "*" 
            : string.Join(", ", query.SelectColumns.Select(c => $"\"{c}\""));

        if (query.Conditions.Count == 0)
        {
            return $"SELECT {select} FROM {readExpr} LIMIT {limit}";
        }

        var whereClauses = new List<string>();
        
        foreach (var cond in query.Conditions)
        {
            var col = $"\"{cond.ColumnName}\"";
            
            string clause = cond.Operator switch
            {
                "IN_LIKE" => $"({string.Join(" OR ", cond.Values.Select(v => $"{col}::TEXT ILIKE '%{v.Replace("'", "''")}%'"))})",
                "LIKE" => $"{col}::TEXT ILIKE '%{cond.Values[0].Replace("'", "''")}%'",
                "ILIKE" => $"{col}::TEXT ILIKE '{cond.Values[0].Replace("'", "''")}'",
                "=" when double.TryParse(cond.Values[0], out _) => $"{col} = {cond.Values[0]}",
                "=" => $"LOWER({col}::TEXT) = LOWER('{cond.Values[0].Replace("'", "''")}')",
                ">" or "<" or ">=" or "<=" => $"{col} {cond.Operator} {cond.Values[0]}",
                _ => $"{col} {cond.Operator} '{cond.Values[0].Replace("'", "''")}'"
            };
            
            whereClauses.Add(clause);
        }

        var where = string.Join(" AND ", whereClauses);
        return $"SELECT {select} FROM {readExpr} WHERE {where} LIMIT {limit}";
    }

    /// <summary>
    /// Search data with automatic strategy detection based on profile.
    /// </summary>
    public async Task<SearchResult> SearchAsync(
        string sourcePath,
        string searchTerm,
        string? tableName = null,
        string? columnName = null,
        int limit = 100,
        DataProfile? profile = null)
    {
        var result = new SearchResult
        {
            SearchTerm = searchTerm,
            SourcePath = sourcePath,
            StartedAt = DateTime.UtcNow
        };

        try
        {
            // Parse source
            var dataSource = DataSource.Parse(sourcePath, tableName);
            
            // Connect
            _conn = new DuckDBConnection("DataSource=:memory:");
            await _conn.OpenAsync();

            // Load extensions if needed
            foreach (var ext in dataSource.GetRequiredExtensions())
            {
                await ExecuteAsync($"INSTALL {ext}; LOAD {ext};");
            }

            // Attach if needed (for databases)
            var attach = dataSource.GetAttachStatement();
            if (attach != null)
            {
                await ExecuteAsync(attach);
            }

            var readExpr = dataSource.GetReadExpression();

            // Profile if not provided
            if (profile == null)
            {
                if (_verbose) Console.WriteLine("[DataSearcher] Quick profiling for search strategy...");
                using var profiler = new DuckDbProfiler(_verbose, new ProfileOptions { FastMode = true });
                profile = await profiler.ProfileAsync(sourcePath, tableName);
            }

            // Determine search strategy
            var strategies = DetermineSearchStrategies(profile, searchTerm, columnName);
            result.Strategies = strategies.Select(s => s.Description).ToList();

            if (_verbose)
            {
                Console.WriteLine($"[DataSearcher] Search term: '{searchTerm}'");
                Console.WriteLine($"[DataSearcher] Strategies: {string.Join(", ", result.Strategies)}");
            }

            // Build and execute search query
            var (sql, searchColumns) = BuildSearchQuery(readExpr, strategies, searchTerm, limit);
            result.Sql = sql;
            result.SearchedColumns = searchColumns;

            if (_verbose) Console.WriteLine($"[DataSearcher] SQL: {sql}");

            // Execute search
            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync();

            var rows = new List<Dictionary<string, object?>>();
            var columnNames = Enumerable.Range(0, reader.FieldCount)
                .Select(i => reader.GetName(i))
                .ToList();

            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[columnNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }

            result.Rows = rows;
            result.MatchCount = rows.Count;
            result.Success = true;
            result.CompletedAt = DateTime.UtcNow;

            // Get total count if we hit the limit
            if (rows.Count == limit)
            {
                var countSql = BuildCountQuery(readExpr, strategies, searchTerm);
                await using var countCmd = _conn.CreateCommand();
                countCmd.CommandText = countSql;
                var totalCount = await countCmd.ExecuteScalarAsync();
                result.TotalMatches = Convert.ToInt64(totalCount);
            }
            else
            {
                result.TotalMatches = rows.Count;
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
    /// Determine the best search strategies based on column types and search term.
    /// </summary>
    private List<SearchStrategy> DetermineSearchStrategies(DataProfile profile, string searchTerm, string? targetColumn)
    {
        var strategies = new List<SearchStrategy>();
        var columns = targetColumn != null 
            ? profile.Columns.Where(c => c.Name.Equals(targetColumn, StringComparison.OrdinalIgnoreCase)).ToList()
            : profile.Columns;

        // Detect search term type
        var isNumeric = double.TryParse(searchTerm, out var numericValue);
        var isDate = DateTime.TryParse(searchTerm, out var dateValue);
        var looksLikePattern = searchTerm.Contains('*') || searchTerm.Contains('%') || searchTerm.Contains('?');

        foreach (var col in columns)
        {
            // Skip ID columns unless specifically targeted
            if (targetColumn == null && (col.InferredType == ColumnType.Id || col.SemanticRole == SemanticRole.Identifier))
                continue;

            var strategy = new SearchStrategy { ColumnName = col.Name, ColumnType = col.InferredType };

            switch (col.InferredType)
            {
                case ColumnType.Text:
                    // Full-text search for text columns
                    if (col.AvgLength > 50)
                    {
                        strategy.Type = SearchType.FullText;
                        strategy.Description = $"{col.Name}: full-text search";
                    }
                    else
                    {
                        strategy.Type = looksLikePattern ? SearchType.Pattern : SearchType.Contains;
                        strategy.Description = $"{col.Name}: {(looksLikePattern ? "pattern" : "contains")} search";
                    }
                    strategies.Add(strategy);
                    break;

                case ColumnType.Categorical:
                    // Exact or fuzzy match for categorical
                    if (col.UniqueCount <= 50)
                    {
                        strategy.Type = SearchType.ExactIgnoreCase;
                        strategy.Description = $"{col.Name}: exact match (case-insensitive)";
                    }
                    else
                    {
                        strategy.Type = SearchType.Contains;
                        strategy.Description = $"{col.Name}: contains search";
                    }
                    strategies.Add(strategy);
                    break;

                case ColumnType.Numeric:
                    if (isNumeric)
                    {
                        // Check if it could be a range search
                        if (searchTerm.Contains('-') && !searchTerm.StartsWith("-"))
                        {
                            strategy.Type = SearchType.Range;
                            strategy.Description = $"{col.Name}: range search";
                        }
                        else
                        {
                            strategy.Type = SearchType.NumericEquals;
                            strategy.Description = $"{col.Name}: numeric equals";
                        }
                        strategies.Add(strategy);
                    }
                    break;

                case ColumnType.DateTime:
                    if (isDate || Regex.IsMatch(searchTerm, @"\d{4}[-/]\d{2}[-/]\d{2}"))
                    {
                        strategy.Type = SearchType.DateMatch;
                        strategy.Description = $"{col.Name}: date match";
                        strategies.Add(strategy);
                    }
                    break;

                case ColumnType.Boolean:
                    var boolTerms = new[] { "true", "false", "yes", "no", "1", "0", "y", "n" };
                    if (boolTerms.Contains(searchTerm.ToLowerInvariant()))
                    {
                        strategy.Type = SearchType.BooleanMatch;
                        strategy.Description = $"{col.Name}: boolean match";
                        strategies.Add(strategy);
                    }
                    break;
            }
        }

        // If no strategies found, fall back to text search on all text-like columns
        if (strategies.Count == 0 && targetColumn == null)
        {
            foreach (var col in profile.Columns.Where(c => 
                c.InferredType is ColumnType.Text or ColumnType.Categorical))
            {
                strategies.Add(new SearchStrategy
                {
                    ColumnName = col.Name,
                    ColumnType = col.InferredType,
                    Type = SearchType.Contains,
                    Description = $"{col.Name}: fallback contains search"
                });
            }
        }

        return strategies;
    }

    /// <summary>
    /// Build SQL query based on search strategies.
    /// </summary>
    private (string Sql, List<string> SearchedColumns) BuildSearchQuery(
        string readExpr, 
        List<SearchStrategy> strategies, 
        string searchTerm,
        int limit)
    {
        if (strategies.Count == 0)
        {
            return ($"SELECT * FROM {readExpr} LIMIT {limit}", new List<string>());
        }

        var conditions = new List<string>();
        var searchedColumns = new List<string>();
        var escapedTerm = searchTerm.Replace("'", "''");

        foreach (var strategy in strategies)
        {
            var col = $"\"{strategy.ColumnName}\"";
            searchedColumns.Add(strategy.ColumnName);

            var condition = strategy.Type switch
            {
                SearchType.FullText => $"{col}::TEXT ILIKE '%{escapedTerm}%'",
                SearchType.Contains => $"{col}::TEXT ILIKE '%{escapedTerm}%'",
                SearchType.ExactIgnoreCase => $"LOWER({col}::TEXT) = LOWER('{escapedTerm}')",
                SearchType.Pattern => $"{col}::TEXT ILIKE '{ConvertWildcards(escapedTerm)}'",
                SearchType.NumericEquals => $"{col} = {escapedTerm}",
                SearchType.Range => BuildRangeCondition(col, escapedTerm),
                SearchType.DateMatch => $"CAST({col} AS DATE) = '{escapedTerm}'",
                SearchType.BooleanMatch => BuildBooleanCondition(col, escapedTerm),
                _ => $"{col}::TEXT ILIKE '%{escapedTerm}%'"
            };

            conditions.Add($"({condition})");
        }

        var whereClause = string.Join(" OR ", conditions);
        var sql = $"SELECT * FROM {readExpr} WHERE {whereClause} LIMIT {limit}";

        return (sql, searchedColumns.Distinct().ToList());
    }

    /// <summary>
    /// Build count query for total matches.
    /// </summary>
    private string BuildCountQuery(string readExpr, List<SearchStrategy> strategies, string searchTerm)
    {
        var (selectSql, _) = BuildSearchQuery(readExpr, strategies, searchTerm, int.MaxValue);
        return selectSql.Replace("SELECT *", "SELECT COUNT(*)").Replace($" LIMIT {int.MaxValue}", "");
    }

    private string ConvertWildcards(string pattern)
    {
        return pattern.Replace("*", "%").Replace("?", "_");
    }

    private string BuildRangeCondition(string column, string term)
    {
        var parts = term.Split('-');
        if (parts.Length == 2 && double.TryParse(parts[0], out var min) && double.TryParse(parts[1], out var max))
        {
            return $"{column} BETWEEN {min} AND {max}";
        }
        return $"{column} = {term}";
    }

    private string BuildBooleanCondition(string column, string term)
    {
        var lower = term.ToLowerInvariant();
        var isTrue = lower is "true" or "yes" or "1" or "y";
        return isTrue ? $"({column} = TRUE OR {column} = 1)" : $"({column} = FALSE OR {column} = 0)";
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        _conn?.Dispose();
    }
}

/// <summary>
/// Search result with matched rows and metadata.
/// </summary>
public class SearchResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string SearchTerm { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string? Sql { get; set; }
    public List<string> Strategies { get; set; } = [];
    public List<string> SearchedColumns { get; set; } = [];
    public List<Dictionary<string, object?>> Rows { get; set; } = [];
    public int MatchCount { get; set; }
    public long TotalMatches { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan Duration => (CompletedAt ?? DateTime.UtcNow) - StartedAt;
    public bool IsNaturalLanguage { get; set; }
    public ParsedNaturalQuery? ParsedQuery { get; set; }
}

/// <summary>
/// Parsed natural language query structure.
/// </summary>
public class ParsedNaturalQuery
{
    public string OriginalPrompt { get; set; } = "";
    public List<string> SelectColumns { get; set; } = [];
    public List<QueryCondition> Conditions { get; set; } = [];
}

/// <summary>
/// A single condition in a query.
/// </summary>
public class QueryCondition
{
    public string ColumnName { get; set; } = "";
    public string Operator { get; set; } = "=";
    public List<string> Values { get; set; } = [];
    public string Description { get; set; } = "";
}

/// <summary>
/// Search strategy for a specific column.
/// </summary>
public class SearchStrategy
{
    public string ColumnName { get; set; } = "";
    public ColumnType ColumnType { get; set; }
    public SearchType Type { get; set; }
    public string Description { get; set; } = "";
}

/// <summary>
/// Types of search strategies.
/// </summary>
public enum SearchType
{
    FullText,
    Contains,
    ExactIgnoreCase,
    Pattern,
    NumericEquals,
    Range,
    DateMatch,
    BooleanMatch
}
