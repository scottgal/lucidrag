using System.Diagnostics;
using System.Globalization;
using MathNet.Numerics.Statistics;
using Mostlylucid.DataSummarizer.Configuration;
using Mostlylucid.DataSummarizer.Models;
using Spectre.Console;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Statistical data profiler using DuckDB - no LLM required.
/// Streams data directly from files without loading into memory.
/// Supports all DuckDB data sources: files, databases, cloud, lakehouse.
/// </summary>
public class DuckDbProfiler : IDisposable
{
    private DuckDbConnectionManager? _db;
    private readonly bool _verbose;
    private readonly ProfileOptions _options;
    private readonly OnnxConfig? _onnxConfig;

    public DuckDbProfiler(bool verbose = false, ProfileOptions? options = null, OnnxConfig? onnxConfig = null)
    {
        _verbose = verbose;
        _options = options ?? new ProfileOptions();
        _onnxConfig = onnxConfig;
    }

    /// <summary>
    /// Profile any data source
    /// </summary>
    public async Task<DataProfile> ProfileAsync(DataSource source)
    {
        var stopwatch = Stopwatch.StartNew();
        
        // For log files, convert to Parquet first
        if (source.Type == DataSourceType.Log)
        {
            if (!source.EnsureLogConverted())
            {
                throw new InvalidOperationException(
                    $"Could not parse log file: {source.Source}. Ensure it's a valid Apache or IIS W3C log format.");
            }
            
            if (_verbose)
            {
                AnsiConsole.MarkupLine($"[dim]Detected log format: {source.DetectedLogFormat}[/]");
                AnsiConsole.MarkupLine($"[dim]Converted to: {source.ConvertedParquetPath}[/]");
            }
        }
        
        _db = new DuckDbConnectionManager(_verbose);
        await _db.ConnectAsync(source);

        // Apply ignore_errors option for CSV
        if (_options.IgnoreErrors)
        {
            source.IgnoreErrors = true;
        }
        
        var readExpr = source.GetReadExpression();
        
        // Try to profile, and if CSV parsing fails, retry with ignore_errors
        try
        {
            return await ProfileInternalAsync(source, readExpr, stopwatch);
        }
        catch (Exception ex) when (source.Type == DataSourceType.Csv && 
                                   !source.IgnoreErrors && 
                                   (ex.Message.Contains("Conversion Error") || 
                                    ex.Message.Contains("CSV Error")))
        {
            // Always show this warning - it's important for user to know data is being skipped
            AnsiConsole.MarkupLine("[yellow]Warning:[/] CSV contains malformed data. Retrying with error tolerance (some rows may be skipped).");
            if (_verbose)
            {
                // Extract the specific error for verbose mode
                var errorLine = ex.Message.Split('\n').FirstOrDefault(l => l.Contains("Original Line:") || l.Contains("Error when converting"));
                if (!string.IsNullOrEmpty(errorLine))
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape(errorLine.Trim())}[/]");
            }
            
            // Retry with ignore_errors
            source.IgnoreErrors = true;
            readExpr = source.GetReadExpression();
            var profile = await ProfileInternalAsync(source, readExpr, stopwatch);
            
            // Add an alert about the data quality issue
            profile.Alerts.Insert(0, new DataAlert
            {
                Severity = AlertSeverity.Warning,
                Type = AlertType.DataQuality,
                Message = "CSV contained malformed rows that were skipped during analysis. Use --ignore-errors to suppress this warning."
            });
            
            return profile;
        }
    }
    
    private void UpdateStatus(string status)
    {
        _options.OnStatusUpdate?.Invoke(status);
        if (_verbose) Console.WriteLine($"[Profile] {status}");
    }
    
    private async Task<DataProfile> ProfileInternalAsync(DataSource source, string readExpr, Stopwatch stopwatch)
    {
        
        var profile = new DataProfile
        {
            SourcePath = source.Source,
            SourceType = source.Type,
            SheetName = source.Table
        };

        // Get row count
        UpdateStatus("Counting rows...");
        profile.RowCount = await GetRowCountAsync(readExpr);
        
        UpdateStatus($"Analyzing {profile.RowCount:N0} rows...");

        // Get column profiles using SUMMARIZE
        var allColumns = await GetColumnProfilesAsync(readExpr, profile.RowCount);
        
        // Apply column filtering/selection
        profile.Columns = FilterAndPrioritizeColumns(allColumns);
        
        if (_verbose && profile.Columns.Count < allColumns.Count)
            Console.WriteLine($"[Profile] Analyzing {profile.Columns.Count} of {allColumns.Count} columns (use --columns to specify)");

        // For Initial depth, return early with minimal stats
        if (_options.Depth == ProfileDepth.Initial)
        {
            profile.ProfileTime = stopwatch.Elapsed;
            return profile;
        }

        // PARALLEL PHASE 1: Basic column enrichment in parallel
        UpdateStatus($"Enriching {profile.Columns.Count} columns (parallel)...");
        var enrichmentTasks = profile.Columns.Select(col => 
            EnrichColumnProfileAsync(readExpr, col, profile.RowCount)).ToList();
        await Task.WhenAll(enrichmentTasks);

        // Identify categorical and numeric columns for aggregate stats
        var categoricalCols = profile.Columns
            .Where(c => c.InferredType == ColumnType.Categorical && c.UniqueCount <= _options.MaxCategoryValues)
            .Take(_options.MaxAggregateCategories)
            .ToList();
        var numericCols = profile.Columns
            .Where(c => c.InferredType == ColumnType.Numeric && c.Mean.HasValue)
            .Take(_options.MaxAggregateMeasures)
            .ToList();

        // PARALLEL PHASE 2: Run expensive operations in parallel
        UpdateStatus("Computing advanced statistics (parallel)...");
        var parallelTasks = new List<Task>();
        
        // Task 1: Alerts detection (CPU-bound, fast)
        var alertsTask = Task.Run(() => DetectAlerts(profile));
        parallelTasks.Add(alertsTask);
        
        // Task 2: PII detection (skip in fast mode, include in Background)
        Task<(List<DataAlert> Alerts, List<PiiScanResult> Results)>? piiTask = null;
        if (!_options.FastMode || _options.Depth == ProfileDepth.Background)
        {
            piiTask = Task.Run(async () =>
            {
                var piiDetector = new PiiDetector(_verbose);
                
                // Enable ONNX classifier if config is available and enabled
                if (_onnxConfig?.Enabled == true)
                {
                    try
                    {
                        await piiDetector.EnableClassifierAsync(_onnxConfig);
                        if (_verbose) Console.WriteLine("[DuckDbProfiler] ONNX classifier enabled for PII detection");
                    }
                    catch (Exception ex)
                    {
                        if (_verbose) Console.WriteLine($"[DuckDbProfiler] Failed to enable ONNX classifier: {ex.Message}");
                    }
                }
                
                var piiResults = piiDetector.ScanProfile(profile);
                var alerts = piiResults.Count > 0 ? piiDetector.GeneratePiiAlerts(piiResults) : new List<DataAlert>();
                return (alerts, piiResults);
            });
            parallelTasks.Add(piiTask);
        }
        
        // Task 3: Correlations
        Task<List<ColumnCorrelation>>? correlationsTask = null;
        if (!_options.SkipCorrelations)
        {
            correlationsTask = GetCorrelationsAsync(readExpr, profile.Columns);
            parallelTasks.Add(correlationsTask);
        }
        
        // Task 4: Aggregate stats (per-category breakdowns) - only for Background depth
        Task<List<AggregateStatistic>>? aggregateTask = null;
        if (_options.Depth == ProfileDepth.Background && categoricalCols.Count > 0 && numericCols.Count > 0)
        {
            aggregateTask = ComputeAggregateStatsAsync(readExpr, categoricalCols, numericCols);
            parallelTasks.Add(aggregateTask);
        }
        
        // Task 5: Conditional tables for categorical relationships (synthesis quality)
        Task<List<ConditionalTable>>? conditionalTask = null;
        if (!_options.FastMode && categoricalCols.Count >= 2)
        {
            conditionalTask = ComputeConditionalTablesAsync(readExpr, profile.Columns);
            parallelTasks.Add(conditionalTask);
        }
        
        // Wait for all parallel tasks
        await Task.WhenAll(parallelTasks);
        
        // Collect results
        profile.Alerts = alertsTask.Result;
        if (piiTask != null)
        {
            var (piiAlerts, piiResults) = piiTask.Result;
            profile.Alerts.AddRange(piiAlerts);
            profile.PiiResults = piiResults;
        }
        if (correlationsTask != null)
        {
            profile.Correlations = correlationsTask.Result;
        }
        if (aggregateTask != null)
        {
            profile.AggregateStats = aggregateTask.Result;
            if (_verbose && profile.AggregateStats.Count > 0)
                Console.WriteLine($"[Profile] Computed {profile.AggregateStats.Count} aggregate statistics");
        }
        if (conditionalTask != null)
        {
            profile.ConditionalTables = conditionalTask.Result;
            if (_verbose && profile.ConditionalTables.Count > 0)
                Console.WriteLine($"[Profile] Found {profile.ConditionalTables.Count} categorical relationships for synthesis");
        }

        // Pattern detection (skip in fast mode, include in Background) - sequential due to state
        if (!_options.FastMode || _options.Depth == ProfileDepth.Background)
        {
            UpdateStatus("Detecting patterns...");
            var patternDetector = new PatternDetector(Connection, readExpr, _verbose);
            
            // Enrich each column with patterns
            foreach (var col in profile.Columns)
            {
                await patternDetector.EnrichWithPatternsAsync(col, profile);
            }
            
            // Detect dataset-level patterns
            profile.Patterns = await patternDetector.DetectDatasetPatternsAsync(profile);
        }

        if (!string.IsNullOrWhiteSpace(_options.TargetColumn))
        {
            UpdateStatus($"Analyzing target '{_options.TargetColumn}'...");
            var targetProfile = await AnalyzeTargetAsync(readExpr, profile, _options.TargetColumn!);
            if (targetProfile != null)
            {
                profile.Target = targetProfile;
                var targetColumn = profile.Columns.FirstOrDefault(c => c.Name.Equals(_options.TargetColumn, StringComparison.OrdinalIgnoreCase));
                if (targetColumn != null)
                {
                    targetColumn.SemanticRole = SemanticRole.Target;
                }
                AddTargetInsights(profile, targetProfile);
            }
        }
        
        // Generate descriptions for each column
        if (_options.IncludeDescriptions)
        {
            foreach (var col in profile.Columns)
            {
                col.Description = GenerateColumnDescription(col);
            }
        }
        
        // Generate statistical insights (no LLM) and merge with target insights
        var statisticalInsights = GenerateStatisticalInsights(profile);
        profile.Insights.AddRange(statisticalInsights);
        profile.Insights = profile.Insights.OrderByDescending(i => i.Score).ToList();

        profile.ProfileTime = stopwatch.Elapsed;
        return profile;
    }
    
    /// <summary>
    /// Filter columns based on options and prioritize interesting columns for wide tables
    /// </summary>
    private List<ColumnProfile> FilterAndPrioritizeColumns(List<ColumnProfile> allColumns)
    {
        var result = allColumns.ToList();
        
        // Apply explicit column filter
        if (_options.Columns?.Count > 0)
        {
            var requested = new HashSet<string>(_options.Columns, StringComparer.OrdinalIgnoreCase);
            result = result.Where(c => requested.Contains(c.Name)).ToList();
        }
        
        // Apply exclusions
        if (_options.ExcludeColumns?.Count > 0)
        {
            var excluded = new HashSet<string>(_options.ExcludeColumns, StringComparer.OrdinalIgnoreCase);
            result = result.Where(c => !excluded.Contains(c.Name)).ToList();
        }

        // Ensure target column is always included
        if (!string.IsNullOrWhiteSpace(_options.TargetColumn))
        {
            if (!result.Any(c => c.Name.Equals(_options.TargetColumn, StringComparison.OrdinalIgnoreCase)))
            {
                var targetCol = allColumns.FirstOrDefault(c => c.Name.Equals(_options.TargetColumn, StringComparison.OrdinalIgnoreCase));
                if (targetCol != null)
                {
                    result.Insert(0, targetCol);
                }
            }
        }
        
        // Calculate interest scores and limit if too many columns
        if (_options.MaxColumns > 0 && result.Count > _options.MaxColumns)
        {
            foreach (var col in result)
            {
                col.InterestScore = CalculateInterestScore(col);
            }
            
            // Keep top N most interesting + any date columns
            var dateColumns = result.Where(c => c.DuckDbType.Contains("DATE") || c.DuckDbType.Contains("TIME")).ToList();
            var otherColumns = result.Except(dateColumns)
                .OrderByDescending(c => c.InterestScore)
                .Take(_options.MaxColumns - dateColumns.Count)
                .ToList();
            
            result = dateColumns.Concat(otherColumns).ToList();
            
            if (_verbose)
            {
                Console.WriteLine($"[Profile] Selected columns by interest: {string.Join(", ", result.Take(10).Select(c => c.Name))}...");
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Score how "interesting" a column is for analysis
    /// Higher scores = more interesting (non-null, varied, not an ID)
    /// </summary>
    private static double CalculateInterestScore(ColumnProfile col)
    {
        double score = 0;
        
        // Penalize high nulls
        score -= col.NullPercent / 20.0;
        
        // Penalize constants
        if (col.UniqueCount == 1) score -= 10;
        
        // Reward moderate cardinality (not too low, not too high)
        if (col.UniquePercent > 1 && col.UniquePercent < 90) score += 2;
        
        // Penalize likely IDs
        var name = col.Name.ToLowerInvariant();
        if (name.EndsWith("id") || name.EndsWith("_id") || name == "id") score -= 5;
        if (col.UniquePercent > 95 && col.DuckDbType.Contains("INT")) score -= 3;
        
        // Reward numeric with variance
        if (col.StdDev.HasValue && col.StdDev > 0) score += 2;
        
        // Reward date columns
        if (col.DuckDbType.Contains("DATE") || col.DuckDbType.Contains("TIME")) score += 3;
        
        // Reward columns with meaningful names
        if (name.Contains("amount") || name.Contains("price") || name.Contains("total") ||
            name.Contains("count") || name.Contains("qty") || name.Contains("rate") ||
            name.Contains("score") || name.Contains("value"))
            score += 2;
        
        return score;
    }
    
    /// <summary>
    /// Generate a human-readable description of a column
    /// </summary>
    private static string GenerateColumnDescription(ColumnProfile col)
    {
        var parts = new List<string>();
        
        // Type description
        var typeDesc = col.InferredType switch
        {
            ColumnType.Numeric => "Numeric column",
            ColumnType.Categorical => $"Categorical with {col.UniqueCount} unique values",
            ColumnType.DateTime => "Date/time column",
            ColumnType.Text => "Free-text column",
            ColumnType.Boolean => "Boolean (true/false)",
            ColumnType.Id => "Identifier column",
            _ => "Column"
        };
        parts.Add(typeDesc);
        
        // Numeric details
        if (col.InferredType == ColumnType.Numeric && col.Mean.HasValue)
        {
            parts.Add($"range {col.Min:G4} to {col.Max:G4}");
            parts.Add($"mean {col.Mean:G4}");
            
            if (col.Distribution.HasValue && col.Distribution != DistributionType.Unknown)
            {
                var dist = col.Distribution switch
                {
                    DistributionType.Normal => "normally distributed",
                    DistributionType.Uniform => "uniformly distributed",
                    DistributionType.LeftSkewed => "left-skewed",
                    DistributionType.RightSkewed => "right-skewed",
                    DistributionType.Bimodal => "bimodal",
                    _ => ""
                };
                if (!string.IsNullOrEmpty(dist)) parts.Add(dist);
            }
        }
        
        // Categorical details
        if (col.InferredType == ColumnType.Categorical && col.TopValues?.Count > 0)
        {
            var top = string.Join(", ", col.TopValues.Take(3).Select(v => $"'{v.Value}'"));
            parts.Add($"top values: {top}");
        }
        
        // Date details
        if (col.InferredType == ColumnType.DateTime && col.MinDate.HasValue && col.MaxDate.HasValue)
        {
            parts.Add($"spans {col.MinDate:yyyy-MM-dd} to {col.MaxDate:yyyy-MM-dd}");
        }
        
        // Data quality notes
        if (col.NullPercent > 10)
            parts.Add($"{col.NullPercent:F0}% nulls");
        
        if (col.OutlierCount > 0)
        {
            var pct = col.OutlierCount * 100.0 / col.Count;
            if (pct > 1) parts.Add($"{pct:F1}% outliers");
        }
        
        return string.Join("; ", parts) + ".";
    }

    /// <summary>
    /// Profile a file (convenience overload)
    /// </summary>
    public async Task<DataProfile> ProfileAsync(string filePath, string? sheetName = null)
    {
        var source = DataSource.Parse(filePath, sheetName);
        return await ProfileAsync(source);
    }

    private async Task<long> GetRowCountAsync(string readExpr)
    {
        var sql = $"SELECT COUNT(*) FROM {readExpr}";
        return await ExecuteScalarAsync<long>(sql);
    }

    private async Task<List<ColumnProfile>> GetColumnProfilesAsync(string readExpr, long totalRows)
    {
        var profiles = new List<ColumnProfile>();
        
        // Use DuckDB's SUMMARIZE for basic stats
        var sql = $"SUMMARIZE SELECT * FROM {readExpr}";
        
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            var colName = reader.GetString(0);
            var colType = reader.GetString(1);
            
            var profile = new ColumnProfile
            {
                Name = colName,
                DuckDbType = colType,
                Count = totalRows
            };

            // Parse SUMMARIZE results
            // Columns: column_name, column_type, min, max, approx_unique, avg, std, q25, q50, q75, count, null_percentage
            if (!reader.IsDBNull(2)) profile.Min = TryGetDouble(reader, 2);
            if (!reader.IsDBNull(3)) profile.Max = TryGetDouble(reader, 3);
            if (!reader.IsDBNull(4))
            {
                profile.UniqueCount = Convert.ToInt64(reader.GetValue(4));
                profile.UniqueCount = Math.Min(profile.UniqueCount, totalRows);
            }
            if (!reader.IsDBNull(5)) profile.Mean = TryGetDouble(reader, 5);
            if (!reader.IsDBNull(6)) profile.StdDev = TryGetDouble(reader, 6);
            if (!reader.IsDBNull(7)) profile.Q25 = TryGetDouble(reader, 7);
            if (!reader.IsDBNull(8)) profile.Median = TryGetDouble(reader, 8);
            if (!reader.IsDBNull(9)) profile.Q75 = TryGetDouble(reader, 9);
            
            // Null percentage from SUMMARIZE
            if (!reader.IsDBNull(11))
            {
                var nullPct = TryGetDouble(reader, 11) ?? 0;
                profile.NullCount = (long)(totalRows * nullPct / 100.0);
            }

            profiles.Add(profile);
        }

        return profiles;
    }

    private async Task EnrichColumnProfileAsync(string readExpr, ColumnProfile col, long totalRows)
    {
        // Infer semantic type
        col.InferredType = InferColumnType(col);

        // Get top values for categorical columns
        const int topK = 10;
        if (col.InferredType == ColumnType.Categorical || col.UniqueCount <= 20)
        {
            col.TopValues = await GetTopValuesAsync(readExpr, col.Name, topK);
            col.TopK = topK;
            
            if (col.TopValues.Count > 0)
            {
                // Set mode (most frequent value)
                col.Mode = col.TopValues[0].Value;
                
                var topPct = col.TopValues[0].Percent;
                var expectedPct = 100.0 / col.UniqueCount;
                col.ImbalanceRatio = topPct / expectedPct;
                
                // Calculate entropy for categorical columns
                col.Entropy = CalculateEntropy(col.TopValues, totalRows);
                
                // Calculate "Other" bucket stats
                var topValuesCount = col.TopValues.Sum(v => v.Count);
                col.OtherCount = totalRows - topValuesCount;
                col.OtherPercent = totalRows > 0 ? (col.OtherCount * 100.0 / totalRows) : 0;
            }
            
            // Set generation policy based on column characteristics
            col.SynthesisPolicy = DetermineGenerationPolicy(col);
        }

        // Calculate stats for numeric columns
        if (col.InferredType == ColumnType.Numeric && col.StdDev > 0)
        {
            col.Skewness = await CalculateSkewnessAsync(readExpr, col.Name);
            col.Kurtosis = await CalculateKurtosisAsync(readExpr, col.Name);
            col.OutlierCount = await CountOutliersAsync(readExpr, col.Name, col.Q25, col.Q75);
            col.ZeroCount = await CountZerosAsync(readExpr, col.Name);
            
            // Build histogram for synthesis
            col.Histogram = await BuildHistogramAsync(readExpr, col.Name, col.Min, col.Max, 20);

            // MathNet robust stats on a sample
            var sample = await GetNumericSampleAsync(readExpr, col.Name, 5000);
            if (sample.Count >= 5)
            {
                var median = Statistics.Median(sample);
                col.Mad = Statistics.Median(sample.Select(v => Math.Abs(v - median)));
                // If DuckDB skewness failed, fill from MathNet
                if (!col.Skewness.HasValue && sample.Count >= 10)
                    col.Skewness = Statistics.Skewness(sample);
                // If DuckDB kurtosis failed, fill from MathNet
                if (!col.Kurtosis.HasValue && sample.Count >= 10)
                    col.Kurtosis = Statistics.Kurtosis(sample);
            }
            
            // Set generation policy for numeric columns
            col.SynthesisPolicy = DetermineGenerationPolicy(col);
        }
        else if (col.InferredType == ColumnType.Id)
        {
            col.SynthesisPolicy = new GenerationPolicy 
            { 
                Mode = GenerationMode.SequentialId, 
                Reason = "Identifier column",
                AutoClassified = true 
            };
        }

        // Date range for date columns
        if (col.InferredType == ColumnType.DateTime)
        {
            (col.MinDate, col.MaxDate) = await GetDateRangeAsync(readExpr, col.Name);
        }

        // Text stats
        if (col.InferredType == ColumnType.Text)
        {
            var textStats = await GetTextStatsExtendedAsync(readExpr, col.Name);
            col.AvgLength = textStats.AvgLength;
            col.MaxLength = textStats.MaxLength;
            col.MinLength = textStats.MinLength;
            col.EmptyStringCount = textStats.EmptyCount;
        }
    }
    
    /// <summary>
    /// Calculate Shannon entropy for categorical distribution
    /// </summary>
    private static double? CalculateEntropy(List<ValueCount> topValues, long totalRows)
    {
        if (topValues == null || topValues.Count == 0 || totalRows <= 0) return null;
        
        double entropy = 0;
        foreach (var val in topValues)
        {
            if (val.Percent > 0)
            {
                var p = val.Percent / 100.0; // Convert to probability
                entropy -= p * Math.Log2(p);
            }
        }
        
        return Math.Round(entropy, 4);
    }
    
    /// <summary>
    /// Build a histogram for numeric columns to enable accurate synthesis.
    /// </summary>
    private async Task<NumericHistogram?> BuildHistogramAsync(string readExpr, string colName, double? min, double? max, int numBins)
    {
        if (!min.HasValue || !max.HasValue || min >= max) return null;
        
        var binWidth = (max.Value - min.Value) / numBins;
        var histogram = new NumericHistogram { Type = HistogramType.EqualWidth };
        
        // Generate bin edges
        for (int i = 0; i <= numBins; i++)
        {
            histogram.BinEdges.Add(min.Value + i * binWidth);
        }
        
        // Count values in each bin using DuckDB
        try
        {
            // Use CASE expression to count values in each bin
            var caseClauses = new List<string>();
            for (int i = 0; i < numBins; i++)
            {
                var lower = histogram.BinEdges[i];
                var upper = histogram.BinEdges[i + 1];
                var condition = i == numBins - 1 
                    ? $"\"{colName}\" >= {lower} AND \"{colName}\" <= {upper}"  // Last bin is inclusive
                    : $"\"{colName}\" >= {lower} AND \"{colName}\" < {upper}";
                caseClauses.Add($"SUM(CASE WHEN {condition} THEN 1 ELSE 0 END) AS bin_{i}");
            }
            
            var sql = $"SELECT {string.Join(", ", caseClauses)} FROM {readExpr} WHERE \"{colName}\" IS NOT NULL";
            
            await using var cmd = Connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync();
            
            if (await reader.ReadAsync())
            {
                for (int i = 0; i < numBins; i++)
                {
                    histogram.BinCounts.Add(reader.IsDBNull(i) ? 0 : reader.GetInt64(i));
                }
            }
            
            return histogram;
        }
        catch
        {
            return null;
        }
    }
    
    /// <summary>
    /// Determine the generation policy for a column based on its characteristics.
    /// </summary>
    private static GenerationPolicy DetermineGenerationPolicy(ColumnProfile col)
    {
        var policy = new GenerationPolicy { AutoClassified = true };
        
        // Check for PII patterns in text patterns
        var hasPii = col.TextPatterns.Any(p => 
            p.PatternType == TextPatternType.Email ||
            p.PatternType == TextPatternType.Phone ||
            p.PatternType == TextPatternType.CreditCard ||
            p.PatternType == TextPatternType.IpAddress);
        
        // High uniqueness suggests identifier or PII
        var highUniqueness = col.CardinalityRatio > 0.9;
        
        // Column name heuristics for PII
        var nameLower = col.Name.ToLowerInvariant();
        var piiNamePatterns = new[] { "email", "phone", "ssn", "social", "credit", "card", "password", "secret", "token", "address", "ip" };
        var nameIsPii = piiNamePatterns.Any(p => nameLower.Contains(p));
        
        // Name columns (but not product names, file names, etc.)
        var isNameColumn = (nameLower.Contains("name") && !nameLower.Contains("product") && 
                           !nameLower.Contains("file") && !nameLower.Contains("column")) ||
                          nameLower == "firstname" || nameLower == "lastname" || nameLower == "fullname";
        
        if (hasPii || nameIsPii)
        {
            policy.Mode = GenerationMode.Mask;
            policy.Reason = "PII pattern detected";
            policy.SuppressTopValues = true;
            return policy;
        }
        
        if (isNameColumn && highUniqueness)
        {
            policy.Mode = GenerationMode.FakerPattern;
            policy.Reason = "Name column with high uniqueness";
            policy.SuppressTopValues = true;
            return policy;
        }
        
        if (col.InferredType == ColumnType.Id)
        {
            policy.Mode = GenerationMode.SequentialId;
            policy.Reason = "Identifier column";
            return policy;
        }
        
        if (highUniqueness && col.InferredType == ColumnType.Text)
        {
            policy.Mode = GenerationMode.Exclude;
            policy.Reason = "High cardinality text - likely unique identifier";
            policy.SuppressTopValues = true;
            return policy;
        }
        
        if (col.InferredType == ColumnType.Categorical && col.UniqueCount <= 50)
        {
            policy.Mode = GenerationMode.CopySafe;
            policy.Reason = "Low cardinality categorical - safe to copy distribution";
            policy.KAnonymityThreshold = 5; // Roll up categories with <5 occurrences
            return policy;
        }
        
        // Default: synthetic generation from stats
        policy.Mode = GenerationMode.Synthetic;
        policy.Reason = "Standard synthetic generation from profile statistics";
        return policy;
    }

    private ColumnType InferColumnType(ColumnProfile col)
    {
        var type = col.DuckDbType.ToUpperInvariant();
        
        // Check for ID patterns first
        if (IsLikelyId(col)) return ColumnType.Id;
        
        // Boolean
        if (type.Contains("BOOL")) return ColumnType.Boolean;
        
        // DateTime
        if (type.Contains("DATE") || type.Contains("TIME") || type.Contains("TIMESTAMP"))
            return ColumnType.DateTime;
        
        // Numeric
        if (type.Contains("INT") || type.Contains("FLOAT") || type.Contains("DOUBLE") || 
            type.Contains("DECIMAL") || type.Contains("NUMERIC") || type.Contains("BIGINT"))
        {
            // Low cardinality numeric might be categorical
            if (col.UniqueCount <= 10 && col.Count > 100)
                return ColumnType.Categorical;
            return ColumnType.Numeric;
        }
        
        // String types
        if (type.Contains("VARCHAR") || type.Contains("TEXT") || type.Contains("STRING"))
        {
            // Low cardinality = categorical
            if (col.UniquePercent < 5 || col.UniqueCount <= 50)
                return ColumnType.Categorical;
            return ColumnType.Text;
        }

        return ColumnType.Unknown;
    }

    private bool IsLikelyId(ColumnProfile col)
    {
        var name = col.Name.ToLowerInvariant();
        
        // Name patterns
        if (name.EndsWith("id") || name.EndsWith("_id") || name == "id" || 
            name.Contains("uuid") || name.Contains("guid"))
            return true;
        
        // High uniqueness for integer columns
        if (col.DuckDbType.Contains("INT") && col.UniquePercent > 95)
            return true;

        return false;
    }

    private async Task<List<ValueCount>> GetTopValuesAsync(string readExpr, string column, int limit)
    {
        var sql = $@"
            SELECT ""{column}"" as val, COUNT(*) as cnt
            FROM {readExpr}
            WHERE ""{column}"" IS NOT NULL
            GROUP BY ""{column}""
            ORDER BY cnt DESC
            LIMIT {limit}";
        
        var totalNonNull = await ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {readExpr} WHERE \"{column}\" IS NOT NULL");

        var results = new List<ValueCount>();
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            var count = reader.GetInt64(1);
            results.Add(new ValueCount
            {
                Value = reader.IsDBNull(0) ? "(null)" : reader.GetValue(0)?.ToString() ?? "",
                Count = count,
                Percent = totalNonNull > 0 ? count * 100.0 / totalNonNull : 0
            });
        }

        return results;
    }

    private async Task<List<double>> GetNumericSampleAsync(string readExpr, string column, int maxRows)
    {
        var sql = $@"SELECT ""{column}"" FROM {readExpr} WHERE ""{column}"" IS NOT NULL ORDER BY RANDOM() LIMIT {maxRows}";
        var values = new List<double>();
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(0))
            {
                if (double.TryParse(reader.GetValue(0)?.ToString(), out var v))
                    values.Add(v);
            }
        }
        return values;
    }

    private async Task<double?> CalculateSkewnessAsync(string readExpr, string column)
    {
        // Skewness = E[(X - μ)³] / σ³
        var sql = $@"
            SELECT 
                AVG(POW((""{column}"" - sub.mean) / sub.std, 3)) as skewness
            FROM {readExpr},
            (SELECT AVG(""{column}"") as mean, STDDEV(""{column}"") as std FROM {readExpr}) sub
            WHERE ""{column}"" IS NOT NULL AND sub.std > 0";
        
        try
        {
            return await ExecuteScalarAsync<double?>(sql);
        }
        catch
        {
            return null;
        }
    }

    private async Task<int> CountOutliersAsync(string readExpr, string column, double? q25, double? q75)
    {
        if (!q25.HasValue || !q75.HasValue) return 0;
        
        var iqr = q75.Value - q25.Value;
        var lower = q25.Value - 1.5 * iqr;
        var upper = q75.Value + 1.5 * iqr;
        
        var sql = $@"
            SELECT COUNT(*) FROM {readExpr}
            WHERE ""{column}"" < {lower} OR ""{column}"" > {upper}";
        
        return (int)await ExecuteScalarAsync<long>(sql);
    }

    private async Task<(DateTime?, DateTime?)> GetDateRangeAsync(string readExpr, string column)
    {
        var sql = $@"SELECT MIN(""{column}""), MAX(""{column}"") FROM {readExpr}";
        
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        
        if (await reader.ReadAsync())
        {
            DateTime? min = reader.IsDBNull(0) ? null : reader.GetDateTime(0);
            DateTime? max = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
            return (min, max);
        }
        return (null, null);
    }

    private async Task<(double?, int?)> GetTextStatsAsync(string readExpr, string column)
    {
        var sql = $@"
            SELECT AVG(LENGTH(""{column}"")), MAX(LENGTH(""{column}""))
            FROM {readExpr}
            WHERE ""{column}"" IS NOT NULL";
        
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        
        if (await reader.ReadAsync())
        {
            double? avg = reader.IsDBNull(0) ? null : TryGetDouble(reader, 0);
            int? max = reader.IsDBNull(1) ? null : reader.GetInt32(1);
            return (avg, max);
        }
        return (null, null);
    }
    
    private async Task<(double? AvgLength, int? MaxLength, int? MinLength, int EmptyCount)> GetTextStatsExtendedAsync(string readExpr, string column)
    {
        var sql = $@"
            SELECT 
                AVG(LENGTH(""{column}"")),
                MAX(LENGTH(""{column}"")),
                MIN(LENGTH(""{column}"")),
                SUM(CASE WHEN TRIM(""{column}"") = '' THEN 1 ELSE 0 END)
            FROM {readExpr}
            WHERE ""{column}"" IS NOT NULL";
        
        try
        {
            await using var cmd = Connection.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync();
            
            if (await reader.ReadAsync())
            {
                double? avg = reader.IsDBNull(0) ? null : TryGetDouble(reader, 0);
                int? max = reader.IsDBNull(1) ? null : reader.GetInt32(1);
                int? min = reader.IsDBNull(2) ? null : reader.GetInt32(2);
                int empty = reader.IsDBNull(3) ? 0 : (int)reader.GetInt64(3);
                return (avg, max, min, empty);
            }
        }
        catch
        {
            // Fall back to basic stats
            var basic = await GetTextStatsAsync(readExpr, column);
            return (basic.Item1, basic.Item2, null, 0);
        }
        return (null, null, null, 0);
    }
    
    private async Task<double?> CalculateKurtosisAsync(string readExpr, string column)
    {
        // Kurtosis = E[(X - μ)^4] / σ^4
        // Note: This is the raw kurtosis (not excess kurtosis). Normal distribution = 3.
        var sql = $@"
            SELECT 
                AVG(POW((""{column}"" - sub.mean) / sub.std, 4)) as kurtosis
            FROM {readExpr},
            (SELECT AVG(""{column}"") as mean, STDDEV(""{column}"") as std FROM {readExpr}) sub
            WHERE ""{column}"" IS NOT NULL AND sub.std > 0";
        
        try
        {
            return await ExecuteScalarAsync<double?>(sql);
        }
        catch
        {
            return null;
        }
    }
    
    private async Task<int> CountZerosAsync(string readExpr, string column)
    {
        var sql = $@"
            SELECT COUNT(*) FROM {readExpr}
            WHERE ""{column}"" = 0";
        
        try
        {
            return (int)await ExecuteScalarAsync<long>(sql);
        }
        catch
        {
            return 0;
        }
    }

    private async Task<List<ColumnCorrelation>> GetCorrelationsAsync(string readExpr, List<ColumnProfile> columns)
    {
        var numericCols = columns
            .Where(c => c.InferredType == ColumnType.Numeric)
            .Select(c => c.Name)
            .ToList();
        
        if (numericCols.Count < 2) return [];

        var correlations = new List<ColumnCorrelation>();
        
        // Limit pairs to avoid O(n²) explosion on wide tables
        var totalPairs = numericCols.Count * (numericCols.Count - 1) / 2;
        var maxPairs = _options.MaxCorrelationPairs;
        
        if (totalPairs > maxPairs && _verbose)
        {
            Console.WriteLine($"[Profile] Limiting correlation analysis: {maxPairs} of {totalPairs} possible pairs");
        }
        
        var pairsComputed = 0;
        
        // Calculate pairwise correlations
        for (int i = 0; i < numericCols.Count && pairsComputed < maxPairs; i++)
        {
            for (int j = i + 1; j < numericCols.Count && pairsComputed < maxPairs; j++)
            {
                var col1 = numericCols[i];
                var col2 = numericCols[j];
                
                var sql = $@"
                    SELECT CORR(""{col1}"", ""{col2}"") 
                    FROM {readExpr}
                    WHERE ""{col1}"" IS NOT NULL AND ""{col2}"" IS NOT NULL";
                
                try
                {
                    var corr = await ExecuteScalarAsync<double?>(sql);
                    pairsComputed++;
                    
                    if (corr.HasValue && Math.Abs(corr.Value) >= 0.3) // Only report meaningful correlations
                    {
                        correlations.Add(new ColumnCorrelation
                        {
                            Column1 = col1,
                            Column2 = col2,
                            Correlation = Math.Round(corr.Value, 3),
                            Metric = "pearson"
                        });
                    }
                }
                catch { /* Skip if correlation fails */ }
            }
        }

        return correlations.OrderByDescending(c => Math.Abs(c.Correlation)).ToList();
    }
    
    /// <summary>
    /// Compute conditional probability tables for categorical column pairs.
    /// Identifies strong parent-child relationships using Cramer's V and mutual information.
    /// </summary>
    private async Task<List<ConditionalTable>> ComputeConditionalTablesAsync(
        string readExpr, 
        List<ColumnProfile> columns)
    {
        var categoricalCols = columns
            .Where(c => c.InferredType == ColumnType.Categorical && 
                        c.UniqueCount >= 2 && 
                        c.UniqueCount <= 50) // Reasonable cardinality for conditionals
            .Select(c => c.Name)
            .ToList();
        
        if (categoricalCols.Count < 2) return [];
        
        var tables = new List<ConditionalTable>();
        var maxPairs = Math.Min(categoricalCols.Count * (categoricalCols.Count - 1), 20);
        var pairsComputed = 0;
        
        if (_verbose)
            Console.WriteLine($"[Profile] Computing conditional tables for {categoricalCols.Count} categorical columns");
        
        // Evaluate all pairs and find strong relationships
        for (int i = 0; i < categoricalCols.Count && pairsComputed < maxPairs; i++)
        {
            for (int j = 0; j < categoricalCols.Count && pairsComputed < maxPairs; j++)
            {
                if (i == j) continue;
                
                var parent = categoricalCols[i];
                var child = categoricalCols[j];
                
                try
                {
                    // Compute contingency table and Cramer's V
                    var cramersV = await ComputeCramersVAsync(readExpr, parent, child);
                    pairsComputed++;
                    
                    // Only keep strong relationships (Cramer's V > 0.2)
                    if (cramersV >= 0.2)
                    {
                        var conditionalDist = await ComputeConditionalDistributionAsync(readExpr, parent, child);
                        
                        if (conditionalDist.Count > 0)
                        {
                            tables.Add(new ConditionalTable
                            {
                                ParentColumn = parent,
                                ChildColumn = child,
                                CramersV = Math.Round(cramersV, 3),
                                Distributions = conditionalDist,
                                AutoDetected = true
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_verbose) Console.WriteLine($"[Profile] Failed to compute conditional for {parent}->{child}: {ex.Message}");
                }
            }
        }
        
        // Return sorted by strength of relationship
        return tables.OrderByDescending(t => t.CramersV).ToList();
    }
    
    /// <summary>
    /// Compute Cramer's V statistic for two categorical columns.
    /// V = sqrt(chi2 / (n * min(r-1, c-1))) where r,c are row/col counts
    /// </summary>
    private async Task<double> ComputeCramersVAsync(string readExpr, string col1, string col2)
    {
        // Get contingency table counts - cast to VARCHAR to handle mixed types
        var sql = $@"
            WITH contingency AS (
                SELECT 
                    CAST(""{col1}"" AS VARCHAR) as parent_val,
                    CAST(""{col2}"" AS VARCHAR) as child_val,
                    COUNT(*) as observed
                FROM {readExpr}
                WHERE ""{col1}"" IS NOT NULL AND ""{col2}"" IS NOT NULL
                GROUP BY CAST(""{col1}"" AS VARCHAR), CAST(""{col2}"" AS VARCHAR)
            ),
            marginals AS (
                SELECT 
                    (SELECT COUNT(*) FROM {readExpr} WHERE ""{col1}"" IS NOT NULL AND ""{col2}"" IS NOT NULL) as n,
                    (SELECT COUNT(DISTINCT CAST(""{col1}"" AS VARCHAR)) FROM {readExpr} WHERE ""{col1}"" IS NOT NULL) as r,
                    (SELECT COUNT(DISTINCT CAST(""{col2}"" AS VARCHAR)) FROM {readExpr} WHERE ""{col2}"" IS NOT NULL) as c
            ),
            parent_marginal AS (
                SELECT CAST(""{col1}"" AS VARCHAR) as val, COUNT(*) as cnt
                FROM {readExpr}
                WHERE ""{col1}"" IS NOT NULL AND ""{col2}"" IS NOT NULL
                GROUP BY CAST(""{col1}"" AS VARCHAR)
            ),
            child_marginal AS (
                SELECT CAST(""{col2}"" AS VARCHAR) as val, COUNT(*) as cnt
                FROM {readExpr}
                WHERE ""{col1}"" IS NOT NULL AND ""{col2}"" IS NOT NULL
                GROUP BY CAST(""{col2}"" AS VARCHAR)
            )
            SELECT 
                m.n, m.r, m.c,
                SUM(
                    POWER(c.observed - (pm.cnt * cm.cnt / m.n::DOUBLE), 2) / 
                    (pm.cnt * cm.cnt / m.n::DOUBLE)
                ) as chi2
            FROM contingency c
            JOIN parent_marginal pm ON c.parent_val = pm.val
            JOIN child_marginal cm ON c.child_val = cm.val
            CROSS JOIN marginals m
            GROUP BY m.n, m.r, m.c";
        
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var n = reader.GetDouble(0);
            var r = reader.GetDouble(1);
            var c = reader.GetDouble(2);
            var chi2 = reader.IsDBNull(3) ? 0 : reader.GetDouble(3);
            
            if (n > 0 && r > 1 && c > 1)
            {
                var minDim = Math.Min(r - 1, c - 1);
                var cramersV = Math.Sqrt(chi2 / (n * minDim));
                return Math.Min(cramersV, 1.0); // Cap at 1.0
            }
        }
        
        return 0;
    }
    
    /// <summary>
    /// Compute P(Child | Parent) conditional distribution.
    /// Returns: {parent_value: {child_value: probability}}
    /// </summary>
    private async Task<Dictionary<string, Dictionary<string, double>>> ComputeConditionalDistributionAsync(
        string readExpr, 
        string parent, 
        string child)
    {
        var sql = $@"
            WITH counts AS (
                SELECT 
                    CAST(""{parent}"" AS VARCHAR) as parent_val,
                    CAST(""{child}"" AS VARCHAR) as child_val,
                    COUNT(*) as cnt
                FROM {readExpr}
                WHERE ""{parent}"" IS NOT NULL AND ""{child}"" IS NOT NULL
                GROUP BY CAST(""{parent}"" AS VARCHAR), CAST(""{child}"" AS VARCHAR)
            ),
            parent_totals AS (
                SELECT parent_val, SUM(cnt) as total
                FROM counts
                GROUP BY parent_val
            )
            SELECT 
                c.parent_val,
                c.child_val,
                c.cnt::DOUBLE / pt.total as probability
            FROM counts c
            JOIN parent_totals pt ON c.parent_val = pt.parent_val
            ORDER BY c.parent_val, probability DESC";
        
        var result = new Dictionary<string, Dictionary<string, double>>();
        
        await using var cmd2 = Connection.CreateCommand();
        cmd2.CommandText = sql;
        await using var reader = await cmd2.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var parentVal = reader.IsDBNull(0) ? "(null)" : reader.GetString(0);
            var childVal = reader.IsDBNull(1) ? "(null)" : reader.GetString(1);
            var prob = reader.GetDouble(2);
            
            if (!result.ContainsKey(parentVal))
                result[parentVal] = new Dictionary<string, double>();
            
            // Only keep probabilities > 1% to avoid noise
            if (prob >= 0.01)
                result[parentVal][childVal] = Math.Round(prob, 4);
        }
        
        return result;
    }

    private List<DataAlert> DetectAlerts(DataProfile profile)
    {
        var alerts = new List<DataAlert>();

        foreach (var col in profile.Columns)
        {
            // High nulls
            if (col.NullPercent > 10)
            {
                alerts.Add(new DataAlert
                {
                    Severity = col.NullPercent > 50 ? AlertSeverity.Warning : AlertSeverity.Info,
                    Column = col.Name,
                    Type = AlertType.HighNulls,
                    Message = $"{col.NullPercent:F1}% null values"
                });
            }

            // Constant column
            if (col.UniqueCount == 1)
            {
                alerts.Add(new DataAlert
                {
                    Severity = AlertSeverity.Warning,
                    Column = col.Name,
                    Type = AlertType.Constant,
                    Message = "Column has only one unique value"
                });
            }

            // High cardinality (possible ID) — exclude numeric, ID, DateTime, and long Text columns
            // DateTime columns are often highly unique but are legitimate features
            // Text columns with long average length are usually free-text, not IDs
            var isLongText = col.InferredType == ColumnType.Text && col.AvgLength.HasValue && col.AvgLength > 50;
            if (col.InferredType != ColumnType.Numeric && 
                col.InferredType != ColumnType.Id &&
                col.InferredType != ColumnType.DateTime &&
                !isLongText &&
                col.UniquePercent > 95)
            {
                alerts.Add(new DataAlert
                {
                    Severity = AlertSeverity.Info,
                    Column = col.Name,
                    Type = AlertType.HighCardinality,
                    Message = $"{col.UniquePercent:F1}% unique - possibly an ID column"
                });
            }

            // High skewness
            if (col.Skewness.HasValue && Math.Abs(col.Skewness.Value) > 2)
            {
                alerts.Add(new DataAlert
                {
                    Severity = AlertSeverity.Info,
                    Column = col.Name,
                    Type = AlertType.HighSkewness,
                    Message = $"Skewness: {col.Skewness:F2} - distribution is highly skewed"
                });
            }

            // Outliers with IQR context
            if (col.OutlierCount > 0 && col.Q25.HasValue && col.Q75.HasValue)
            {
                var outlierPct = col.OutlierCount * 100.0 / col.Count;
                if (outlierPct > 1)
                {
                    var iqr = col.Q75.Value - col.Q25.Value;
                    var lowerBound = col.Q25.Value - 1.5 * iqr;
                    var upperBound = col.Q75.Value + 1.5 * iqr;
                    alerts.Add(new DataAlert
                    {
                        Severity = outlierPct > 5 ? AlertSeverity.Warning : AlertSeverity.Info,
                        Column = col.Name,
                        Type = AlertType.Outliers,
                        Message = $"{col.OutlierCount:N0} outliers ({outlierPct:F1}%) outside IQR bounds [{lowerBound:F1}, {upperBound:F1}]"
                    });
                }
            }

            // Imbalanced categorical
            if (col.ImbalanceRatio.HasValue && col.ImbalanceRatio > 5)
            {
                alerts.Add(new DataAlert
                {
                    Severity = AlertSeverity.Info,
                    Column = col.Name,
                    Type = AlertType.Imbalanced,
                    Message = $"Top value is {col.ImbalanceRatio:F1}x more common than expected"
                });
            }
            
            // Potential leakage: near-unique columns that aren't marked as ID, DateTime, or long free-text
            // DateTime columns are often unique but legitimate features (time series)
            // Long text columns (avg > 50 chars) are usually descriptions/comments, not features
            var isProbablyFreeText = col.InferredType == ColumnType.Text && col.AvgLength.HasValue && col.AvgLength > 50;
            if (col.InferredType != ColumnType.Id && 
                col.InferredType != ColumnType.DateTime && 
                !isProbablyFreeText &&
                col.UniquePercent > 90 && col.UniqueCount > 100)
            {
                // Provide context-aware explanation
                var explanation = col.InferredType == ColumnType.Numeric
                    ? "High-precision numeric columns with unique values may act as hidden identifiers or contain information derived from the target variable."
                    : "Near-unique text columns often indicate identifiers, timestamps, or data that wouldn't be available at prediction time.";
                
                alerts.Add(new DataAlert
                {
                    Severity = AlertSeverity.Warning,
                    Column = col.Name,
                    Type = AlertType.PotentialLeakage,
                    Message = $"⚠ Potential data leakage: {col.UniquePercent:F1}% unique ({col.UniqueCount:N0} values). " +
                              $"{explanation} " +
                              $"Data leakage occurs when a feature contains information that wouldn't be available during real predictions, " +
                              $"leading to overly optimistic model performance. Verify this column is a legitimate feature, or exclude it from modeling."
                });
            }
            
            // Ordinal masquerading as categorical (low cardinality integers)
            if (col.InferredType == ColumnType.Categorical && col.TopValues != null && col.TopValues.Count <= 10)
            {
                var allNumeric = col.TopValues.All(v => int.TryParse(v.Value, out _));
                if (allNumeric && col.TopValues.Count >= 3)
                {
                    alerts.Add(new DataAlert
                    {
                        Severity = AlertSeverity.Info,
                        Column = col.Name,
                        Type = AlertType.OrdinalAsCategory,
                        Message = $"ℹ Ordinal detected: {col.UniqueCount} integer levels - consider treating as ordered numeric"
                    });
                }
            }
            
            // Zero-inflated numeric columns
            if (col.InferredType == ColumnType.Numeric && col.TopValues != null)
            {
                var zeroValue = col.TopValues.FirstOrDefault(v => v.Value == "0" || v.Value == "0.0");
                if (zeroValue != null && zeroValue.Percent > 30)
                {
                    alerts.Add(new DataAlert
                    {
                        Severity = AlertSeverity.Info,
                        Column = col.Name,
                        Type = AlertType.ZeroInflated,
                        Message = $"ℹ Zero-inflated: {zeroValue.Percent:F1}% zeros - consider two-part model or segmentation"
                    });
                }
            }
        }
        
        // Target imbalance detection (if target column specified)
        if (profile.Target != null)
        {
            var minorityClass = profile.Target.ClassDistribution.OrderBy(kv => kv.Value).FirstOrDefault();
            var majorityClass = profile.Target.ClassDistribution.OrderByDescending(kv => kv.Value).FirstOrDefault();
            var minorityPct = minorityClass.Value * 100; // Convert ratio to percentage
            var majorityPct = majorityClass.Value * 100;
            
            if (minorityPct < 20)
            {
                var severity = minorityPct < 5 ? AlertSeverity.Warning : AlertSeverity.Info;
                alerts.Add(new DataAlert
                {
                    Severity = severity,
                    Column = profile.Target.ColumnName,
                    Type = AlertType.TargetImbalance,
                    Message = $"⚠ Target imbalance: {minorityClass.Key}={minorityPct:F1}% vs {majorityClass.Key}={majorityPct:F1}%"
                });
            }
        }

        return alerts;
    }

    private async Task<TargetProfile?> AnalyzeTargetAsync(string readExpr, DataProfile profile, string targetColumn)
    {
        var targetColumnProfile = profile.Columns.FirstOrDefault(c => c.Name.Equals(targetColumn, StringComparison.OrdinalIgnoreCase));
        if (targetColumnProfile == null)
            return null;

        targetColumnProfile.SemanticRole = SemanticRole.Target;
        var classValues = await GetTargetClassesAsync(readExpr, targetColumn);
        if (classValues.Count < 2)
            return null;

        var encoding = BuildTargetEncoding(targetColumn, classValues);
        if (!encoding.IsBinary)
            return null;

        var totalRows = classValues.Sum(c => c.Count);
        var totalRowsDouble = Math.Max(1, (double)totalRows);
        var targetProfile = new TargetProfile
        {
            ColumnName = targetColumn,
            IsBinary = true,
            ClassDistribution = classValues.ToDictionary(c => c.Value, c => c.Count / totalRowsDouble)
        };

        var positiveCount = classValues.First(c => c.Value == encoding.PositiveLabel).Count;
        var baseRate = positiveCount / totalRowsDouble;

        foreach (var col in profile.Columns.Where(c => !c.Name.Equals(targetColumn, StringComparison.OrdinalIgnoreCase)))
        {
            FeatureEffect? effect = col.InferredType switch
            {
                ColumnType.Numeric => await AnalyzeNumericEffectAsync(readExpr, col, targetColumn, encoding, totalRows),
                ColumnType.Categorical or ColumnType.Text or ColumnType.Boolean => await AnalyzeCategoricalEffectAsync(readExpr, col, targetColumn, encoding, totalRows, baseRate),
                ColumnType.DateTime => null, // Skip DateTime columns for target analysis (can't compute STDDEV on dates)
                _ => null
            };

            if (effect != null)
            {
                targetProfile.FeatureEffects.Add(effect);
            }
        }

        targetProfile.FeatureEffects = targetProfile.FeatureEffects
            .OrderByDescending(e => e.Magnitude * e.Support)
            .ToList();

        return targetProfile;
    }

    private void AddTargetInsights(DataProfile profile, TargetProfile targetProfile)
    {
        // Add cross-column teaser as top insight
        if (targetProfile.FeatureEffects.Count > 0)
        {
            var topEffects = targetProfile.FeatureEffects.Take(3).ToList();
            var teaserParts = topEffects.Select(e => 
            {
                if (e.Metric == "CohenD")
                    return $"{e.Feature} (d={e.Magnitude:F2})";
                return $"{e.Feature} (Δ{e.Magnitude:F1}%)";
            });
            
            var minorityClass = targetProfile.ClassDistribution.OrderBy(kv => kv.Value).FirstOrDefault();
            var minorityPct = minorityClass.Value * 100; // Convert ratio to percentage
            
            profile.Insights.Add(new DataInsight
            {
                Title = $"🎯 {targetProfile.ColumnName} Analysis Summary",
                Description = $"Target rate: {minorityPct:F1}%. Top drivers: {string.Join(", ", teaserParts)}. " +
                             $"See feature effects below for actionable segments.",
                Source = InsightSource.Statistical,
                RelatedColumns = new List<string> { targetProfile.ColumnName }.Concat(topEffects.Select(e => e.Feature)).ToList(),
                Score = 0.95, // High score to appear first
                ScoreBreakdown = new Dictionary<string, double>
                {
                    ["magnitude"] = 0.9,
                    ["support"] = 1.0,
                    ["novelty"] = 0.8
                }
            });
        }
        
        // Add individual feature effects
        foreach (var effect in targetProfile.FeatureEffects.Take(8))
        {
            var score = Math.Clamp(effect.Magnitude * 0.7 + effect.Support * 0.3, 0, 1);
            profile.Insights.Add(new DataInsight
            {
                Title = $"Target driver: {effect.Feature}",
                Description = effect.Summary,
                Source = InsightSource.Statistical,
                RelatedColumns = new List<string> { targetProfile.ColumnName, effect.Feature },
                Score = score,
                ScoreBreakdown = new Dictionary<string, double>
                {
                    ["magnitude"] = effect.Magnitude,
                    ["support"] = effect.Support
                }
            });
        }
    }

    private async Task<List<(string Value, long Count)>> GetTargetClassesAsync(string readExpr, string targetColumn)
    {
        var results = new List<(string Value, long Count)>();
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT CAST("{targetColumn}" AS VARCHAR) AS val, COUNT(*) cnt
            FROM {readExpr}
            WHERE "{targetColumn}" IS NOT NULL
            GROUP BY 1
            ORDER BY cnt DESC
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var value = reader.IsDBNull(0) ? "(null)" : reader.GetString(0);
            var count = reader.GetInt64(1);
            results.Add((value, count));
        }
        return results;
    }

    private TargetEncoding BuildTargetEncoding(string targetColumn, List<(string Value, long Count)> classes)
    {
        var encoding = new TargetEncoding { Column = targetColumn };
        if (classes.Count < 2)
            return encoding;

        var ordered = classes.OrderByDescending(c => c.Count).ToList();
        var preferred = ordered.FirstOrDefault(c => IsPositiveHint(c.Value));
        var positive = preferred.Value ?? ordered[0].Value;
        var negative = ordered.FirstOrDefault(c => !c.Value.Equals(positive, StringComparison.OrdinalIgnoreCase)).Value ?? ordered[0].Value;

        if (double.TryParse(positive, NumberStyles.Any, CultureInfo.InvariantCulture, out var posNumeric) &&
            double.TryParse(negative, NumberStyles.Any, CultureInfo.InvariantCulture, out var negNumeric))
        {
            if (negNumeric > posNumeric)
            {
                (positive, negative) = (negative, positive);
                (posNumeric, negNumeric) = (negNumeric, posNumeric);
            }
            var posLiteral = posNumeric.ToString(CultureInfo.InvariantCulture);
            encoding.Expression = $"CASE WHEN \"{targetColumn}\" = {posLiteral} THEN 1.0 ELSE 0.0 END";
            encoding.PositiveLabel = positive;
            encoding.NegativeLabel = negative;
            encoding.IsBinary = true;
            return encoding;
        }

        encoding.Expression = $"CASE WHEN \"{targetColumn}\" = '{EscapeSqlLiteral(positive)}' THEN 1.0 ELSE 0.0 END";
        encoding.PositiveLabel = positive;
        encoding.NegativeLabel = negative;
        encoding.IsBinary = true;
        return encoding;
    }

    private static bool IsPositiveHint(string value)
    {
        var hints = new[] { "1", "true", "yes", "y", "churn", "exited", "fraud", "late", "bad" };
        return hints.Any(h => string.Equals(h, value, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<FeatureEffect?> AnalyzeNumericEffectAsync(string readExpr, ColumnProfile column, string targetColumn, TargetEncoding encoding, long totalRows)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {encoding.Expression} AS flag,
                   COUNT(*) cnt,
                   AVG("{column.Name}") mean,
                   STDDEV("{column.Name}") stddev
            FROM {readExpr}
            WHERE "{column.Name}" IS NOT NULL AND "{targetColumn}" IS NOT NULL
            GROUP BY 1
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        var groups = new List<(double flag, long count, double mean, double std)>();
        while (await reader.ReadAsync())
        {
            var flag = TryGetDouble(reader, 0) ?? 0;
            var count = reader.GetInt64(1);
            var mean = TryGetDouble(reader, 2) ?? 0;
            var std = TryGetDouble(reader, 3) ?? 0;
            groups.Add((flag, count, mean, std));
        }
        if (groups.Count < 2) return null;

        var positive = groups.FirstOrDefault(g => g.flag >= 0.5);
        var negative = groups.FirstOrDefault(g => g.flag < 0.5);
        if (positive.count < 5 || negative.count < 5) return null;

        var meanDiff = positive.mean - negative.mean;
        var pooledStd = Math.Sqrt((((positive.count - 1) * Math.Pow(positive.std, 2)) + ((negative.count - 1) * Math.Pow(negative.std, 2))) /
                                   Math.Max(1, (positive.count + negative.count - 2)));
        var d = pooledStd > 0 ? meanDiff / pooledStd : 0;
        var support = (positive.count + negative.count) / (double)Math.Max(1, totalRows);

        return new FeatureEffect
        {
            Feature = column.Name,
            Metric = "cohens_d",
            Magnitude = Math.Abs(d),
            Support = support,
            Summary = $"Average {column.Name} is {positive.mean:F1} for {encoding.PositiveLabel} vs {negative.mean:F1} for {encoding.NegativeLabel} (Δ {meanDiff:F1})",
            Details = new Dictionary<string, double>
            {
                ["mean_positive"] = positive.mean,
                ["mean_negative"] = negative.mean,
                ["delta"] = meanDiff
            }
        };
    }

    private async Task<FeatureEffect?> AnalyzeCategoricalEffectAsync(string readExpr, ColumnProfile column, string targetColumn, TargetEncoding encoding, long totalRows, double baseRate)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT "{column.Name}" AS category,
                   COUNT(*) cnt,
                   AVG({encoding.Expression}) AS rate
            FROM {readExpr}
            WHERE "{column.Name}" IS NOT NULL AND "{targetColumn}" IS NOT NULL
            GROUP BY 1
            HAVING COUNT(*) >= 10
            ORDER BY cnt DESC
            LIMIT 50
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        double bestDelta = 0;
        string? bestCategory = null;
        double bestRate = 0;
        long total = 0;
        while (await reader.ReadAsync())
        {
            var category = reader.GetValue(0)?.ToString() ?? "(null)";
            var cnt = reader.GetInt64(1);
            var rate = TryGetDouble(reader, 2) ?? 0;
            var delta = Math.Abs(rate - baseRate);
            if (delta > bestDelta)
            {
                bestDelta = delta;
                bestCategory = category;
                bestRate = rate;
            }
            total += cnt;
        }

        if (bestCategory == null || total == 0)
            return null;

        return new FeatureEffect
        {
            Feature = column.Name,
            Metric = "rate_delta",
            Magnitude = bestDelta,
            Support = total / (double)Math.Max(1, totalRows),
            Summary = $"{column.Name} = {bestCategory} has {encoding.PositiveLabel} rate {bestRate:P1} vs baseline {baseRate:P1} (Δ {bestDelta:P1})",
            Details = new Dictionary<string, double>
            {
                ["category_rate"] = bestRate,
                ["baseline_rate"] = baseRate,
                ["delta"] = bestDelta
            }
        };
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''");

    private sealed class TargetEncoding
    {
        public string Column { get; set; } = string.Empty;
        public string Expression { get; set; } = "0";
        public string PositiveLabel { get; set; } = "1";
        public string NegativeLabel { get; set; } = "0";
        public bool IsBinary { get; set; }
    }

    private List<DataInsight> GenerateStatisticalInsights(DataProfile profile)
    {
        var insights = new List<DataInsight>();

        // Dataset overview
        var overview = new DataInsight
        {
            Title = "Dataset Overview",
            Description = $"Dataset contains {profile.RowCount:N0} rows and {profile.ColumnCount} columns. " +
                         $"{profile.Columns.Count(c => c.InferredType == ColumnType.Numeric)} numeric, " +
                         $"{profile.Columns.Count(c => c.InferredType == ColumnType.Categorical)} categorical, " +
                         $"{profile.Columns.Count(c => c.InferredType == ColumnType.DateTime)} date/time columns.",
            Source = InsightSource.Statistical
        };
        ApplyInsightScore(overview, magnitude: 0.2, support: 1, novelty: 0.2);
        insights.Add(overview);

        // Strong correlations
        var strongCorrs = profile.Correlations.Where(c => Math.Abs(c.Correlation) >= 0.7).ToList();
        if (strongCorrs.Any())
        {
            var corrDesc = string.Join("; ", strongCorrs.Select(c => 
                $"{c.Column1} ↔ {c.Column2} ({c.Correlation:F2})"));
            var corrInsight = new DataInsight
            {
                Title = "Strong Correlations Detected",
                Description = $"Found {strongCorrs.Count} strong correlations: {corrDesc}",
                Source = InsightSource.Statistical,
                RelatedColumns = strongCorrs.SelectMany(c => new[] { c.Column1, c.Column2 }).Distinct().ToList()
            };
            var avgMagnitude = strongCorrs.Average(c => Math.Abs(c.Correlation));
            ApplyInsightScore(corrInsight, magnitude: avgMagnitude, support: strongCorrs.Count / (double)Math.Max(1, profile.Columns.Count));
            insights.Add(corrInsight);
        }

        // Data quality summary
        var criticalAlerts = profile.Alerts.Count(a => a.Severity == AlertSeverity.Error);
        var warningAlerts = profile.Alerts.Count(a => a.Severity == AlertSeverity.Warning);
        
        if (criticalAlerts > 0 || warningAlerts > 0)
        {
            var dqInsight = new DataInsight
            {
                Title = "Data Quality Issues",
                Description = $"Found {criticalAlerts} critical and {warningAlerts} warning-level data quality issues. " +
                             "Review alerts for details.",
                Source = InsightSource.Statistical
            };
            var magnitude = Math.Min(1, (criticalAlerts * 1.0 + warningAlerts * 0.5) / Math.Max(1, profile.Columns.Count));
            ApplyInsightScore(dqInsight, magnitude: magnitude, support: 1, novelty: 0.6);
            insights.Add(dqInsight);
        }
        
        // Modeling hints based on data characteristics
        AddModelingHints(profile, insights);

        // Numeric column with interesting stats
        var numericCols = profile.Columns.Where(c => c.InferredType == ColumnType.Numeric).ToList();
        foreach (var col in numericCols.Take(3)) // Top 3
        {
            if (col.Mean.HasValue && col.Median.HasValue)
            {
                var diff = Math.Abs(col.Mean.Value - col.Median.Value);
                var range = (col.Max ?? 0) - (col.Min ?? 0);
                
                if (range > 0 && diff / range > 0.1) // Mean-median diff > 10% of range
                {
                    var direction = col.Mean > col.Median ? "right" : "left";
                    var distributionInsight = new DataInsight
                    {
                        Title = $"{col.Name} Distribution",
                        Description = $"Mean ({col.Mean:F2}) differs significantly from median ({col.Median:F2}), " +
                                     $"suggesting a {direction}-skewed distribution.",
                        Source = InsightSource.Statistical,
                        RelatedColumns = [col.Name]
                    };
                    var magnitude = Math.Min(1, diff / Math.Max(1e-6, range));
                    var support = col.Count / (double)Math.Max(1, profile.RowCount);
                    ApplyInsightScore(distributionInsight, magnitude, support);
                    insights.Add(distributionInsight);
                }
            }
        }

        // Add pattern-based insights
        AddPatternInsights(profile, insights);

        return insights;
    }

    private static void ApplyInsightScore(DataInsight insight, double magnitude, double support = 1, double novelty = 0.5)
    {
        magnitude = Math.Clamp(magnitude, 0, 1);
        support = Math.Clamp(support, 0, 1);
        novelty = Math.Clamp(novelty, 0, 1);
        insight.Score = magnitude * 0.6 + support * 0.3 + novelty * 0.1;
        insight.ScoreBreakdown["magnitude"] = magnitude;
        insight.ScoreBreakdown["support"] = support;
        insight.ScoreBreakdown["novelty"] = novelty;
    }
    
    private static void AddModelingHints(DataProfile profile, List<DataInsight> insights)
    {
        var hints = new List<string>();
        
        // Binary classification suitability
        if (profile.Target != null && profile.Target.IsBinary)
        {
            var numericFeatures = profile.Columns.Count(c => c.InferredType == ColumnType.Numeric && c.SemanticRole != SemanticRole.Identifier);
            var catFeatures = profile.Columns.Count(c => c.InferredType == ColumnType.Categorical && c.SemanticRole != SemanticRole.Target);
            
            if (numericFeatures >= 3 && profile.RowCount >= 1000)
            {
                hints.Add("ℹ Good candidate for logistic regression or gradient boosting");
            }
            
            var minorityPct = profile.Target.ClassDistribution.Values.Min() * 100; // Convert ratio to percentage
            if (minorityPct < 10)
            {
                hints.Add("⚠ Severe imbalance - consider SMOTE, class weights, or precision/recall metrics");
            }
            else if (minorityPct < 20)
            {
                hints.Add("⚠ Moderate imbalance - use stratified splits and F1 score");
            }
        }
        
        // High cardinality categorical warning
        var highCardCats = profile.Columns
            .Where(c => c.InferredType == ColumnType.Categorical && c.UniqueCount > 50 && c.UniquePercent < 90)
            .ToList();
        if (highCardCats.Any())
        {
            hints.Add($"ℹ High-cardinality categoricals ({string.Join(", ", highCardCats.Select(c => c.Name))}) - consider target encoding or embeddings");
        }
        
        // ID columns that might leak
        var idCols = profile.Columns.Where(c => c.InferredType == ColumnType.Id || c.SemanticRole == SemanticRole.Identifier).ToList();
        if (idCols.Any())
        {
            hints.Add($"⚠ Exclude ID columns from features: {string.Join(", ", idCols.Select(c => c.Name))}");
        }
        
        // Zero-inflated columns
        var zeroInflated = profile.Alerts.Where(a => a.Type == AlertType.ZeroInflated).Select(a => a.Column).ToList();
        if (zeroInflated.Any())
        {
            hints.Add($"ℹ Zero-inflated columns ({string.Join(", ", zeroInflated)}) - consider log transform or two-part model");
        }
        
        if (hints.Any())
        {
            var modelHint = new DataInsight
            {
                Title = "💡 Modeling Recommendations",
                Description = string.Join(" | ", hints),
                Source = InsightSource.Statistical,
                Score = 0.7
            };
            insights.Add(modelHint);
        }
    }

    private static void AddPatternInsights(DataProfile profile, List<DataInsight> insights)
    {
        // Time series insights
        var dateCol = profile.Columns.FirstOrDefault(c => c.TimeSeries != null);
        if (dateCol?.TimeSeries != null)
        {
            var ts = dateCol.TimeSeries;
            var desc = $"Data is a {ts.Granularity.ToString().ToLower()} time series spanning {dateCol.MinDate:yyyy-MM-dd} to {dateCol.MaxDate:yyyy-MM-dd}.";
            
            if (!ts.IsContiguous)
                desc += $" {ts.GapCount} time gaps detected ({ts.GapPercent:F1}% missing).";
            
            if (ts.HasSeasonality)
                desc += $" Potential seasonality detected with period {ts.SeasonalPeriod}.";

            var tsInsight = new DataInsight
            {
                Title = "Time Series Characteristics",
                Description = desc,
                Source = InsightSource.Statistical,
                RelatedColumns = [dateCol.Name]
            };
            ApplyInsightScore(tsInsight, magnitude: ts.HasSeasonality ? 0.6 : 0.3, support: 1, novelty: 0.5);
            insights.Add(tsInsight);
        }

        // Text pattern insights
        foreach (var col in profile.Columns.Where(c => c.TextPatterns.Count > 0))
        {
            var topPattern = col.TextPatterns.First();
            var textInsight = new DataInsight
            {
                Title = $"Text Pattern in '{col.Name}'",
                Description = $"{topPattern.MatchPercent:F0}% of values match {topPattern.PatternType} pattern " +
                             $"({topPattern.MatchCount:N0} matches).",
                Source = InsightSource.Statistical,
                RelatedColumns = [col.Name]
            };
            ApplyInsightScore(textInsight, magnitude: topPattern.MatchPercent / 100.0, support: col.Count / (double)Math.Max(1, profile.RowCount), novelty: 0.4);
            insights.Add(textInsight);
        }

        // Distribution insights
        foreach (var col in profile.Columns.Where(c => c.Distribution.HasValue && c.Distribution != DistributionType.Unknown))
        {
            var distDesc = col.Distribution switch
            {
                DistributionType.Normal => "follows a normal (bell curve) distribution",
                DistributionType.Uniform => "is uniformly distributed across its range",
                DistributionType.LeftSkewed => "is left-skewed (tail extends left)",
                DistributionType.RightSkewed => "is right-skewed (tail extends right)",
                DistributionType.Bimodal => "appears bimodal (two peaks)",
                DistributionType.PowerLaw => "follows a power law distribution",
                DistributionType.Exponential => "follows an exponential distribution",
                _ => "has an unknown distribution"
            };

            var distInsight = new DataInsight
            {
                Title = $"'{col.Name}' Distribution",
                Description = $"Column {distDesc}.",
                Source = InsightSource.Statistical,
                RelatedColumns = [col.Name]
            };
            ApplyInsightScore(distInsight, magnitude: 0.4, support: col.Count / (double)Math.Max(1, profile.RowCount), novelty: 0.5);
            insights.Add(distInsight);
        }

        // Trend insights
        foreach (var col in profile.Columns.Where(c => c.Trend != null && c.Trend.Direction != TrendDirection.None))
        {
            var trend = col.Trend!;
            var direction = trend.Direction == TrendDirection.Increasing ? "increasing" : "decreasing";
            var dateRef = trend.RelatedDateColumn != null ? $" over '{trend.RelatedDateColumn}'" : " by row order";
            
            insights.Add(new DataInsight
            {
                Title = $"Trend in '{col.Name}'",
                Description = $"Values are {direction}{dateRef} (R²={trend.RSquared:F2}).",
                Source = InsightSource.Statistical,
                RelatedColumns = trend.RelatedDateColumn != null 
                    ? [col.Name, trend.RelatedDateColumn] 
                    : [col.Name]
            });
        }

        // Dataset-level pattern insights
        foreach (var pattern in profile.Patterns)
        {
            insights.Add(new DataInsight
            {
                Title = $"{pattern.Type} Pattern",
                Description = pattern.Description,
                Source = InsightSource.Statistical,
                RelatedColumns = pattern.RelatedColumns
            });
        }
    }

    #region Helpers

    private DuckDB.NET.Data.DuckDBConnection Connection => _db?.Connection 
        ?? throw new InvalidOperationException("Not connected");

    private async Task ExecuteAsync(string sql)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ExecuteScalarAsync<T>(string sql)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        
        if (result == null || result == DBNull.Value)
            return default!;
        
        return (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private static double? TryGetDouble(System.Data.Common.DbDataReader reader, int ordinal)
    {
        try
        {
            var val = reader.GetValue(ordinal);
            if (val == null || val == DBNull.Value) return null;
            return Convert.ToDouble(val);
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Aggregate Statistics
    
    /// <summary>
    /// Compute per-category aggregate statistics in parallel.
    /// E.g., average price per category, sum of sales per region, etc.
    /// </summary>
    private async Task<List<AggregateStatistic>> ComputeAggregateStatsAsync(
        string readExpr, 
        List<ColumnProfile> categoricalCols, 
        List<ColumnProfile> numericCols)
    {
        var results = new List<AggregateStatistic>();
        var tasks = new List<Task<AggregateStatistic?>>();
        
        // For each categorical column, compute aggregates for each numeric column
        foreach (var catCol in categoricalCols)
        {
            foreach (var numCol in numericCols)
            {
                // Compute AVG, SUM, COUNT for each combination
                tasks.Add(ComputeSingleAggregateAsync(readExpr, catCol.Name, numCol.Name, "AVG"));
                
                // Also compute SUM for amount/total/price columns
                var nameLower = numCol.Name.ToLowerInvariant();
                if (nameLower.Contains("amount") || nameLower.Contains("total") || 
                    nameLower.Contains("price") || nameLower.Contains("sales") ||
                    nameLower.Contains("revenue") || nameLower.Contains("cost"))
                {
                    tasks.Add(ComputeSingleAggregateAsync(readExpr, catCol.Name, numCol.Name, "SUM"));
                }
            }
            
            // Also compute COUNT per category
            tasks.Add(ComputeCountAggregateAsync(readExpr, catCol.Name));
        }
        
        // Wait for all tasks
        var completedTasks = await Task.WhenAll(tasks);
        
        // Collect non-null results
        foreach (var result in completedTasks)
        {
            if (result != null)
            {
                results.Add(result);
            }
        }
        
        return results;
    }
    
    private async Task<AggregateStatistic?> ComputeSingleAggregateAsync(
        string readExpr, 
        string groupByCol, 
        string measureCol, 
        string aggFunc)
    {
        try
        {
            var sql = $@"
                SELECT ""{groupByCol}"" AS grp, {aggFunc}(""{measureCol}"") AS val
                FROM {readExpr}
                WHERE ""{groupByCol}"" IS NOT NULL AND ""{measureCol}"" IS NOT NULL
                GROUP BY ""{groupByCol}""
                ORDER BY val DESC
                LIMIT 50";
            
            var results = new Dictionary<string, double>();
            await using var cmd = Connection.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                var group = reader.IsDBNull(0) ? "(null)" : reader.GetValue(0)?.ToString() ?? "";
                var value = reader.IsDBNull(1) ? 0.0 : Convert.ToDouble(reader.GetValue(1));
                results[group] = Math.Round(value, 4);
            }
            
            if (results.Count == 0)
                return null;
            
            return new AggregateStatistic
            {
                GroupByColumn = groupByCol,
                MeasureColumn = measureCol,
                AggregateFunction = aggFunc,
                Results = results,
                ComputedAt = DateTime.UtcNow,
                Source = "Profiler"
            };
        }
        catch
        {
            return null;
        }
    }
    
    private async Task<AggregateStatistic?> ComputeCountAggregateAsync(string readExpr, string groupByCol)
    {
        try
        {
            var sql = $@"
                SELECT ""{groupByCol}"" AS grp, COUNT(*) AS cnt
                FROM {readExpr}
                WHERE ""{groupByCol}"" IS NOT NULL
                GROUP BY ""{groupByCol}""
                ORDER BY cnt DESC
                LIMIT 50";
            
            var results = new Dictionary<string, double>();
            await using var cmd = Connection.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                var group = reader.IsDBNull(0) ? "(null)" : reader.GetValue(0)?.ToString() ?? "";
                var count = reader.GetInt64(1);
                results[group] = count;
            }
            
            if (results.Count == 0)
                return null;
            
            return new AggregateStatistic
            {
                GroupByColumn = groupByCol,
                MeasureColumn = "*",
                AggregateFunction = "COUNT",
                Results = results,
                ComputedAt = DateTime.UtcNow,
                Source = "Profiler"
            };
        }
        catch
        {
            return null;
        }
    }
    
    #endregion

    public void Dispose()
    {
        _db?.Dispose();
        _db = null;
    }
}
