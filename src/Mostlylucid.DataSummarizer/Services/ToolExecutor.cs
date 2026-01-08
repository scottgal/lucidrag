using DuckDB.NET.Data;
using MathNet.Numerics.Statistics;
using Mostlylucid.DataSummarizer.Models;
using System.Text;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Executes analytics tools invoked by the LLM.
/// Provides capabilities beyond SQL: segmentation, anomaly detection, statistical tests, etc.
/// </summary>
public class ToolExecutor
{
    private readonly DuckDBConnection _connection;
    private readonly DataProfile _profile;
    private readonly string _readExpr;
    private readonly bool _verbose;

    public ToolExecutor(DuckDBConnection connection, DataProfile profile, string readExpr, bool verbose = false)
    {
        _connection = connection;
        _profile = profile;
        _readExpr = readExpr;
        _verbose = verbose;
    }

    /// <summary>
    /// Execute a tool invocation and return the result.
    /// </summary>
    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation)
    {
        if (_verbose) Console.WriteLine($"[Tool] Executing {invocation.ToolId}");

        try
        {
            return invocation.ToolId.ToLowerInvariant() switch
            {
                "segment_audience" => await ExecuteSegmentationAsync(invocation),
                "detect_anomalies" => await ExecuteAnomalyDetectionAsync(invocation),
                "compare_groups" => await ExecuteGroupComparisonAsync(invocation),
                "correlation_analysis" => await ExecuteCorrelationAnalysisAsync(invocation),
                "data_quality" => ExecuteDataQualityReport(invocation),
                "statistical_test" => await ExecuteStatisticalTestAsync(invocation),
                "feature_importance" => await ExecuteFeatureImportanceAsync(invocation),
                "decompose_timeseries" => await ExecuteTimeSeriesDecomposeAsync(invocation),
                _ => new ToolResult
                {
                    ToolId = invocation.ToolId,
                    Success = false,
                    Error = $"Unknown tool: {invocation.ToolId}"
                }
            };
        }
        catch (Exception ex)
        {
            if (_verbose) Console.WriteLine($"[Tool] Error executing {invocation.ToolId}: {ex.Message}");
            return new ToolResult
            {
                ToolId = invocation.ToolId,
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// K-Means style clustering using DuckDB aggregations.
    /// Uses iterative refinement with SQL to avoid loading all data into memory.
    /// </summary>
    private async Task<ToolResult> ExecuteSegmentationAsync(ToolInvocation invocation)
    {
        var numSegments = GetParam<int>(invocation, "num_segments", 4);
        numSegments = Math.Clamp(numSegments, 2, 10);

        var featuresParam = GetParam<string>(invocation, "features", "auto");
        
        // Get numeric columns for clustering
        var numericCols = _profile.Columns
            .Where(c => c.InferredType == ColumnType.Numeric && c.StdDev > 0)
            .Select(c => c.Name)
            .ToList();

        if (featuresParam != "auto")
        {
            var requested = featuresParam.Split(',').Select(s => s.Trim()).ToList();
            numericCols = numericCols.Where(c => requested.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        if (numericCols.Count < 2)
        {
            return new ToolResult
            {
                ToolId = "segment_audience",
                Success = false,
                Error = "Need at least 2 numeric columns with variance for segmentation"
            };
        }

        // Limit to first 5 numeric columns for performance
        numericCols = numericCols.Take(5).ToList();

        // Use percentile-based segmentation (more robust than k-means without iteration)
        // Create segments based on composite score
        var colList = string.Join(", ", numericCols.Select(c => $"\"{c}\""));
        
        // Normalize each column and create composite score using NTILE
        var normalizedCols = numericCols.Select((c, i) => 
            $"((\"{c}\" - (SELECT MIN(\"{c}\") FROM {_readExpr})) / " +
            $"NULLIF((SELECT MAX(\"{c}\") FROM {_readExpr}) - (SELECT MIN(\"{c}\") FROM {_readExpr}), 0))").ToList();

        var compositeExpr = string.Join(" + ", normalizedCols);
        
        // Get segment statistics
        var sql = $@"
            WITH scored AS (
                SELECT *, ({compositeExpr}) / {numericCols.Count} AS composite_score
                FROM {_readExpr}
                WHERE {string.Join(" AND ", numericCols.Select(c => $"\"{c}\" IS NOT NULL"))}
            ),
            segmented AS (
                SELECT *, NTILE({numSegments}) OVER (ORDER BY composite_score) AS segment
                FROM scored
            )
            SELECT 
                segment,
                COUNT(*) AS count,
                {string.Join(",\n                ", numericCols.Select(c => $"AVG(\"{c}\") AS avg_{c.Replace(" ", "_")}"))}
            FROM segmented
            GROUP BY segment
            ORDER BY segment";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;

        var segments = new List<Dictionary<string, object>>();
        using var reader = await cmd.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            var segment = new Dictionary<string, object>
            {
                ["segment"] = reader.GetInt32(0),
                ["count"] = reader.GetInt64(1)
            };

            for (int i = 0; i < numericCols.Count; i++)
            {
                var val = reader.IsDBNull(i + 2) ? 0 : reader.GetDouble(i + 2);
                segment[$"avg_{numericCols[i]}"] = Math.Round(val, 2);
            }

            segments.Add(segment);
        }

        // Generate summary
        var sb = new StringBuilder();
        sb.AppendLine($"**Audience Segmentation into {numSegments} Groups**");
        sb.AppendLine($"Features used: {string.Join(", ", numericCols)}");
        sb.AppendLine();

        foreach (var seg in segments)
        {
            var segNum = seg["segment"];
            var count = seg["count"];
            sb.AppendLine($"**Segment {segNum}** ({count} records):");
            
            foreach (var col in numericCols)
            {
                var key = $"avg_{col}";
                if (seg.TryGetValue(key, out var val))
                {
                    sb.AppendLine($"  - Avg {col}: {val}");
                }
            }
            sb.AppendLine();
        }

        return new ToolResult
        {
            ToolId = "segment_audience",
            Success = true,
            Summary = sb.ToString(),
            Data = new Dictionary<string, object>
            {
                ["segments"] = segments,
                ["features"] = numericCols,
                ["num_segments"] = numSegments
            },
            RelatedColumns = numericCols,
            VisualizationType = "table"
        };
    }

    /// <summary>
    /// Anomaly detection using IQR or Z-score method.
    /// </summary>
    private async Task<ToolResult> ExecuteAnomalyDetectionAsync(ToolInvocation invocation)
    {
        var method = GetParam<string>(invocation, "method", "zscore");
        var threshold = GetParam<double>(invocation, "threshold", 3.0);

        var numericCols = _profile.Columns
            .Where(c => c.InferredType == ColumnType.Numeric && c.StdDev > 0)
            .Take(5) // Limit for performance
            .ToList();

        if (numericCols.Count == 0)
        {
            return new ToolResult
            {
                ToolId = "detect_anomalies",
                Success = false,
                Error = "No numeric columns with variance found for anomaly detection"
            };
        }

        var anomalies = new List<Dictionary<string, object>>();
        var sb = new StringBuilder();
        sb.AppendLine($"**Anomaly Detection ({method.ToUpper()} method, threshold={threshold})**");
        sb.AppendLine();

        foreach (var col in numericCols)
        {
            string whereClause;
            var mean = col.Mean ?? 0;
            var stdDev = col.StdDev ?? 1;
            var median = col.Median ?? mean;

            if (method.ToLowerInvariant() == "iqr")
            {
                // IQR method: outliers outside 1.5*IQR
                var q1 = col.Q25 ?? (median - stdDev);
                var q3 = col.Q75 ?? (median + stdDev);
                var iqr = q3 - q1;
                var lower = q1 - threshold * iqr;
                var upper = q3 + threshold * iqr;
                whereClause = $"\"{col.Name}\" < {lower} OR \"{col.Name}\" > {upper}";
            }
            else // zscore
            {
                var zLower = mean - threshold * stdDev;
                var zUpper = mean + threshold * stdDev;
                whereClause = $"\"{col.Name}\" < {zLower} OR \"{col.Name}\" > {zUpper}";
            }

            var sql = $@"
                SELECT COUNT(*) as outlier_count,
                       MIN(""{col.Name}"") as min_outlier,
                       MAX(""{col.Name}"") as max_outlier
                FROM {_readExpr}
                WHERE {whereClause}";

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                var count = reader.GetInt64(0);
                if (count > 0)
                {
                    var minOutlier = reader.IsDBNull(1) ? 0 : reader.GetDouble(1);
                    var maxOutlier = reader.IsDBNull(2) ? 0 : reader.GetDouble(2);

                    var pct = _profile.RowCount > 0 ? (count * 100.0 / _profile.RowCount) : 0;
                    
                    anomalies.Add(new Dictionary<string, object>
                    {
                        ["column"] = col.Name,
                        ["outlier_count"] = count,
                        ["percentage"] = Math.Round(pct, 2),
                        ["min_outlier"] = Math.Round(minOutlier, 2),
                        ["max_outlier"] = Math.Round(maxOutlier, 2),
                        ["normal_mean"] = Math.Round(mean, 2),
                        ["normal_std"] = Math.Round(stdDev, 2)
                    });

                    sb.AppendLine($"**{col.Name}**: {count} anomalies ({pct:F1}%)");
                    sb.AppendLine($"  - Range: {minOutlier:F2} to {maxOutlier:F2}");
                    sb.AppendLine($"  - Normal: mean={mean:F2}, std={stdDev:F2}");
                    sb.AppendLine();
                }
            }
        }

        if (anomalies.Count == 0)
        {
            sb.AppendLine("No significant anomalies detected at the specified threshold.");
        }

        return new ToolResult
        {
            ToolId = "detect_anomalies",
            Success = true,
            Summary = sb.ToString(),
            Data = new Dictionary<string, object>
            {
                ["anomalies"] = anomalies,
                ["method"] = method,
                ["threshold"] = threshold
            },
            RelatedColumns = numericCols.Select(c => c.Name).ToList(),
            VisualizationType = "table"
        };
    }

    /// <summary>
    /// Compare statistics between groups.
    /// </summary>
    private async Task<ToolResult> ExecuteGroupComparisonAsync(ToolInvocation invocation)
    {
        var groupByCol = GetParam<string>(invocation, "group_by", "");
        var metricsParam = GetParam<string>(invocation, "metrics", "all");

        // Find grouping column
        var groupCol = _profile.Columns.FirstOrDefault(c =>
            c.Name.Equals(groupByCol, StringComparison.OrdinalIgnoreCase) ||
            c.InferredType == ColumnType.Categorical);

        if (groupCol == null)
        {
            return new ToolResult
            {
                ToolId = "compare_groups",
                Success = false,
                Error = $"No categorical column found for grouping. Available: {string.Join(", ", _profile.Columns.Where(c => c.InferredType == ColumnType.Categorical).Select(c => c.Name))}"
            };
        }

        var numericCols = _profile.Columns
            .Where(c => c.InferredType == ColumnType.Numeric)
            .Select(c => c.Name)
            .ToList();

        if (metricsParam != "all")
        {
            var requested = metricsParam.Split(',').Select(s => s.Trim()).ToList();
            numericCols = numericCols.Where(c => requested.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        numericCols = numericCols.Take(5).ToList(); // Limit for readability

        if (numericCols.Count == 0)
        {
            return new ToolResult
            {
                ToolId = "compare_groups",
                Success = false,
                Error = "No numeric columns found for comparison"
            };
        }

        var metrics = string.Join(",\n            ", numericCols.SelectMany(c => new[]
        {
            $"AVG(\"{c}\") AS avg_{c.Replace(" ", "_")}",
            $"MIN(\"{c}\") AS min_{c.Replace(" ", "_")}",
            $"MAX(\"{c}\") AS max_{c.Replace(" ", "_")}"
        }));

        var sql = $@"
            SELECT ""{groupCol.Name}"" AS group_value, COUNT(*) AS count,
            {metrics}
            FROM {_readExpr}
            WHERE ""{groupCol.Name}"" IS NOT NULL
            GROUP BY ""{groupCol.Name}""
            ORDER BY COUNT(*) DESC
            LIMIT 10";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;

        var groups = new List<Dictionary<string, object>>();
        using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var group = new Dictionary<string, object>
            {
                ["group"] = reader.GetValue(0)?.ToString() ?? "(null)",
                ["count"] = reader.GetInt64(1)
            };

            int colIndex = 2;
            foreach (var numCol in numericCols)
            {
                group[$"avg_{numCol}"] = reader.IsDBNull(colIndex) ? null : Math.Round(reader.GetDouble(colIndex), 2);
                group[$"min_{numCol}"] = reader.IsDBNull(colIndex + 1) ? null : Math.Round(reader.GetDouble(colIndex + 1), 2);
                group[$"max_{numCol}"] = reader.IsDBNull(colIndex + 2) ? null : Math.Round(reader.GetDouble(colIndex + 2), 2);
                colIndex += 3;
            }

            groups.Add(group);
        }

        // Generate summary
        var sb = new StringBuilder();
        sb.AppendLine($"**Group Comparison by {groupCol.Name}**");
        sb.AppendLine($"Comparing: {string.Join(", ", numericCols)}");
        sb.AppendLine();

        foreach (var group in groups)
        {
            sb.AppendLine($"**{group["group"]}** (n={group["count"]}):");
            foreach (var numCol in numericCols)
            {
                var avg = group[$"avg_{numCol}"];
                sb.AppendLine($"  - {numCol}: avg={avg}");
            }
            sb.AppendLine();
        }

        return new ToolResult
        {
            ToolId = "compare_groups",
            Success = true,
            Summary = sb.ToString(),
            Data = new Dictionary<string, object>
            {
                ["groups"] = groups,
                ["group_by"] = groupCol.Name,
                ["metrics"] = numericCols
            },
            RelatedColumns = [groupCol.Name, .. numericCols],
            VisualizationType = "table"
        };
    }

    /// <summary>
    /// Correlation analysis using pre-computed profile data.
    /// </summary>
    private async Task<ToolResult> ExecuteCorrelationAnalysisAsync(ToolInvocation invocation)
    {
        var minCorr = GetParam<double>(invocation, "min_correlation", 0.3);

        // Use pre-computed correlations from profile if available
        if (_profile.Correlations != null && _profile.Correlations.Count > 0)
        {
            var significantCorrs = _profile.Correlations
                .Where(c => Math.Abs(c.Correlation) >= minCorr)
                .OrderByDescending(c => Math.Abs(c.Correlation))
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"**Correlation Analysis** (threshold: {minCorr})");
            sb.AppendLine();

            if (significantCorrs.Count == 0)
            {
                sb.AppendLine($"No correlations above {minCorr} threshold found.");
            }
            else
            {
                foreach (var corr in significantCorrs.Take(10))
                {
                    var strength = Math.Abs(corr.Correlation) switch
                    {
                        >= 0.8 => "Very Strong",
                        >= 0.6 => "Strong",
                        >= 0.4 => "Moderate",
                        _ => "Weak"
                    };
                    var direction = corr.Correlation > 0 ? "positive" : "negative";
                    sb.AppendLine($"- **{corr.Column1}** vs **{corr.Column2}**: {corr.Correlation:F3} ({strength} {direction})");
                }
            }

            return new ToolResult
            {
                ToolId = "correlation_analysis",
                Success = true,
                Summary = sb.ToString(),
                Data = new Dictionary<string, object>
                {
                    ["correlations"] = significantCorrs.Select(c => new Dictionary<string, object>
                    {
                        ["column1"] = c.Column1,
                        ["column2"] = c.Column2,
                        ["correlation"] = c.Correlation
                    }).ToList(),
                    ["min_threshold"] = minCorr
                },
                RelatedColumns = significantCorrs.SelectMany(c => new[] { c.Column1, c.Column2 }).Distinct().ToList(),
                VisualizationType = "heatmap"
            };
        }

        // Fall back to computing correlations on the fly
        return new ToolResult
        {
            ToolId = "correlation_analysis",
            Success = false,
            Error = "Correlation data not available in profile. Run with --profile-depth=deep first."
        };
    }

    /// <summary>
    /// Generate data quality report from profile.
    /// </summary>
    private ToolResult ExecuteDataQualityReport(ToolInvocation invocation)
    {
        var sb = new StringBuilder();
        sb.AppendLine("**Data Quality Report**");
        sb.AppendLine();

        var issues = new List<Dictionary<string, object>>();
        var overallScore = 100.0;

        // Analyze each column
        foreach (var col in _profile.Columns)
        {
            var colIssues = new List<string>();

            // Missing values
            if (col.NullCount > 0)
            {
                var nullPct = (col.NullCount * 100.0) / _profile.RowCount;
                if (nullPct > 50)
                {
                    colIssues.Add($"High missing rate ({nullPct:F1}%)");
                    overallScore -= 5;
                }
                else if (nullPct > 10)
                {
                    colIssues.Add($"Missing values ({nullPct:F1}%)");
                    overallScore -= 2;
                }
            }

            // Cardinality issues
            if (col.InferredType == ColumnType.Categorical && col.UniqueCount > _profile.RowCount * 0.9)
            {
                colIssues.Add("High cardinality - may be identifier not category");
                overallScore -= 1;
            }

            if (col.InferredType == ColumnType.Numeric && col.UniqueCount == 1)
            {
                colIssues.Add("Constant value - no variance");
                overallScore -= 1;
            }

            // Outliers (OutlierCount is count, not percentage)
            var outlierPct = _profile.RowCount > 0 ? (col.OutlierCount * 100.0 / _profile.RowCount) : 0;
            if (outlierPct > 10)
            {
                colIssues.Add($"High outlier rate ({outlierPct:F1}%)");
                overallScore -= 2;
            }

            if (colIssues.Count > 0)
            {
                issues.Add(new Dictionary<string, object>
                {
                    ["column"] = col.Name,
                    ["issues"] = colIssues
                });
            }
        }

        overallScore = Math.Max(0, overallScore);

        sb.AppendLine($"**Overall Quality Score: {overallScore:F0}/100**");
        sb.AppendLine($"- Total rows: {_profile.RowCount:N0}");
        sb.AppendLine($"- Total columns: {_profile.Columns.Count}");
        sb.AppendLine();

        if (issues.Count == 0)
        {
            sb.AppendLine("No significant data quality issues detected.");
        }
        else
        {
            sb.AppendLine("**Issues Found:**");
            foreach (var issue in issues)
            {
                sb.AppendLine($"\n**{issue["column"]}**:");
                foreach (var i in (List<string>)issue["issues"])
                {
                    sb.AppendLine($"  - {i}");
                }
            }
        }

        return new ToolResult
        {
            ToolId = "data_quality",
            Success = true,
            Summary = sb.ToString(),
            Data = new Dictionary<string, object>
            {
                ["score"] = overallScore,
                ["issues"] = issues,
                ["row_count"] = _profile.RowCount,
                ["column_count"] = _profile.Columns.Count
            },
            RelatedColumns = issues.Select(i => i["column"].ToString()!).ToList(),
            VisualizationType = "report"
        };
    }

    /// <summary>
    /// Execute statistical significance tests.
    /// </summary>
    private async Task<ToolResult> ExecuteStatisticalTestAsync(ToolInvocation invocation)
    {
        var testType = GetParam<string>(invocation, "test_type", "ttest");
        var column1 = GetParam<string>(invocation, "column1", "");
        var column2 = GetParam<string>(invocation, "column2", "");

        if (string.IsNullOrEmpty(column1))
        {
            return new ToolResult
            {
                ToolId = "statistical_test",
                Success = false,
                Error = "column1 parameter is required"
            };
        }

        var sb = new StringBuilder();

        switch (testType.ToLowerInvariant())
        {
            case "correlation":
                return await ExecuteCorrelationTestAsync(column1, column2, sb);
            
            case "ttest":
                return await ExecuteTTestAsync(column1, column2, sb);
            
            default:
                return new ToolResult
                {
                    ToolId = "statistical_test",
                    Success = false,
                    Error = $"Unsupported test type: {testType}. Supported: correlation, ttest"
                };
        }
    }

    private async Task<ToolResult> ExecuteCorrelationTestAsync(string col1, string col2, StringBuilder sb)
    {
        if (string.IsNullOrEmpty(col2))
        {
            return new ToolResult
            {
                ToolId = "statistical_test",
                Success = false,
                Error = "column2 parameter required for correlation test"
            };
        }

        // Calculate correlation using DuckDB
        var sql = $@"
            SELECT CORR(""{col1}"", ""{col2}"") as correlation,
                   COUNT(*) as n
            FROM {_readExpr}
            WHERE ""{col1}"" IS NOT NULL AND ""{col2}"" IS NOT NULL";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = await cmd.ExecuteReaderAsync();

        if (await reader.ReadAsync())
        {
            var corr = reader.IsDBNull(0) ? 0 : reader.GetDouble(0);
            var n = reader.GetInt64(1);

            // Calculate t-statistic for correlation significance
            // t = r * sqrt(n-2) / sqrt(1-r^2)
            var tStat = Math.Abs(corr) < 0.9999
                ? corr * Math.Sqrt(n - 2) / Math.Sqrt(1 - corr * corr)
                : double.PositiveInfinity;

            // Approximate p-value (two-tailed)
            // For n > 30, t-distribution approximates normal
            var pValue = n > 30 ? 2 * (1 - NormalCDF(Math.Abs(tStat))) : -1;

            var significant = pValue >= 0 && pValue < 0.05;

            sb.AppendLine($"**Correlation Test: {col1} vs {col2}**");
            sb.AppendLine();
            sb.AppendLine($"- Correlation coefficient: {corr:F4}");
            sb.AppendLine($"- Sample size: {n}");
            sb.AppendLine($"- t-statistic: {tStat:F4}");
            if (pValue >= 0)
            {
                sb.AppendLine($"- p-value: {pValue:F4}");
                sb.AppendLine($"- Significant at 0.05 level: {(significant ? "Yes" : "No")}");
            }

            return new ToolResult
            {
                ToolId = "statistical_test",
                Success = true,
                Summary = sb.ToString(),
                Data = new Dictionary<string, object>
                {
                    ["test_type"] = "correlation",
                    ["column1"] = col1,
                    ["column2"] = col2,
                    ["correlation"] = corr,
                    ["t_statistic"] = tStat,
                    ["p_value"] = pValue,
                    ["n"] = n,
                    ["significant"] = significant
                },
                RelatedColumns = [col1, col2]
            };
        }

        return new ToolResult
        {
            ToolId = "statistical_test",
            Success = false,
            Error = "Failed to compute correlation"
        };
    }

    private async Task<ToolResult> ExecuteTTestAsync(string numericCol, string groupCol, StringBuilder sb)
    {
        // Two-sample t-test comparing numeric column across two groups
        if (string.IsNullOrEmpty(groupCol))
        {
            // One-sample t-test against 0
            var col = _profile.Columns.FirstOrDefault(c => 
                c.Name.Equals(numericCol, StringComparison.OrdinalIgnoreCase));
            
            if (col == null || !col.Mean.HasValue)
            {
                return new ToolResult
                {
                    ToolId = "statistical_test",
                    Success = false,
                    Error = $"Column {numericCol} not found or has no statistics"
                };
            }

            var mean = col.Mean.Value;
            var stdDev = col.StdDev ?? 1;
            var n = _profile.RowCount - col.NullCount;
            var se = stdDev / Math.Sqrt(n);
            var tStat = mean / se;
            
            sb.AppendLine($"**One-Sample T-Test: {numericCol}**");
            sb.AppendLine($"- Testing if mean differs from 0");
            sb.AppendLine($"- Mean: {mean:F4}");
            sb.AppendLine($"- Std Dev: {stdDev:F4}");
            sb.AppendLine($"- n: {n}");
            sb.AppendLine($"- t-statistic: {tStat:F4}");

            return new ToolResult
            {
                ToolId = "statistical_test",
                Success = true,
                Summary = sb.ToString(),
                Data = new Dictionary<string, object>
                {
                    ["test_type"] = "one_sample_ttest",
                    ["column"] = numericCol,
                    ["mean"] = mean,
                    ["t_statistic"] = tStat,
                    ["n"] = n
                },
                RelatedColumns = [numericCol]
            };
        }

        // Two-sample t-test
        var sql = $@"
            SELECT ""{groupCol}"", 
                   AVG(""{numericCol}"") as mean,
                   STDDEV(""{numericCol}"") as std,
                   COUNT(*) as n
            FROM {_readExpr}
            WHERE ""{numericCol}"" IS NOT NULL AND ""{groupCol}"" IS NOT NULL
            GROUP BY ""{groupCol}""
            ORDER BY COUNT(*) DESC
            LIMIT 2";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;

        var groups = new List<(string name, double mean, double std, long n)>();
        using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            groups.Add((
                reader.GetValue(0)?.ToString() ?? "(null)",
                reader.IsDBNull(1) ? 0 : reader.GetDouble(1),
                reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                reader.GetInt64(3)
            ));
        }

        if (groups.Count < 2)
        {
            return new ToolResult
            {
                ToolId = "statistical_test",
                Success = false,
                Error = "Need at least 2 groups for two-sample t-test"
            };
        }

        var g1 = groups[0];
        var g2 = groups[1];

        // Welch's t-test (unequal variances)
        var se1 = g1.std * g1.std / g1.n;
        var se2 = g2.std * g2.std / g2.n;
        var tStat2 = (g1.mean - g2.mean) / Math.Sqrt(se1 + se2);

        sb.AppendLine($"**Two-Sample T-Test: {numericCol} by {groupCol}**");
        sb.AppendLine();
        sb.AppendLine($"Group **{g1.name}**: mean={g1.mean:F4}, std={g1.std:F4}, n={g1.n}");
        sb.AppendLine($"Group **{g2.name}**: mean={g2.mean:F4}, std={g2.std:F4}, n={g2.n}");
        sb.AppendLine();
        sb.AppendLine($"- Mean difference: {g1.mean - g2.mean:F4}");
        sb.AppendLine($"- t-statistic: {tStat2:F4}");

        return new ToolResult
        {
            ToolId = "statistical_test",
            Success = true,
            Summary = sb.ToString(),
            Data = new Dictionary<string, object>
            {
                ["test_type"] = "two_sample_ttest",
                ["numeric_column"] = numericCol,
                ["group_column"] = groupCol,
                ["groups"] = groups.Select(g => new Dictionary<string, object>
                {
                    ["name"] = g.name,
                    ["mean"] = g.mean,
                    ["std"] = g.std,
                    ["n"] = g.n
                }).ToList(),
                ["mean_difference"] = g1.mean - g2.mean,
                ["t_statistic"] = tStat2
            },
            RelatedColumns = [numericCol, groupCol]
        };
    }

    /// <summary>
    /// Feature importance based on correlation with target.
    /// </summary>
    private async Task<ToolResult> ExecuteFeatureImportanceAsync(ToolInvocation invocation)
    {
        var method = GetParam<string>(invocation, "method", "correlation");

        if (_profile.Target == null)
        {
            return new ToolResult
            {
                ToolId = "feature_importance",
                Success = false,
                Error = "No target column specified. Use --target=<column> when profiling."
            };
        }

        var targetCol = _profile.Target.ColumnName;
        var numericCols = _profile.Columns
            .Where(c => c.InferredType == ColumnType.Numeric && c.Name != targetCol)
            .Select(c => c.Name)
            .Take(10)
            .ToList();

        if (numericCols.Count == 0)
        {
            return new ToolResult
            {
                ToolId = "feature_importance",
                Success = false,
                Error = "No numeric feature columns found"
            };
        }

        var featureCorrelations = new List<(string Column, double Correlation)>();

        foreach (var colName in numericCols)
        {
            var sql = $@"
                SELECT CORR(""{colName}"", ""{targetCol}"") as correlation
                FROM {_readExpr}
                WHERE ""{colName}"" IS NOT NULL AND ""{targetCol}"" IS NOT NULL";

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync();

            if (await reader.ReadAsync() && !reader.IsDBNull(0))
            {
                featureCorrelations.Add((colName, reader.GetDouble(0)));
            }
        }

        // Sort by absolute correlation
        featureCorrelations = featureCorrelations.OrderByDescending(c => Math.Abs(c.Correlation)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"**Feature Importance for Target: {targetCol}**");
        sb.AppendLine($"Method: {method}");
        sb.AppendLine();

        foreach (var (colName, corr) in featureCorrelations)
        {
            var bar = new string('|', (int)(Math.Abs(corr) * 20));
            var direction = corr > 0 ? "+" : "-";
            sb.AppendLine($"- {colName}: {corr:F3} {direction} {bar}");
        }

        return new ToolResult
        {
            ToolId = "feature_importance",
            Success = true,
            Summary = sb.ToString(),
            Data = new Dictionary<string, object>
            {
                ["target"] = targetCol,
                ["method"] = method,
                ["importance"] = featureCorrelations.Select(c => new Dictionary<string, object>
                {
                    ["column"] = c.Column,
                    ["importance"] = Math.Abs(c.Correlation),
                    ["correlation"] = c.Correlation
                }).ToList()
            },
            RelatedColumns = [targetCol, .. featureCorrelations.Select(c => c.Column)],
            VisualizationType = "bar_chart"
        };
    }

    /// <summary>
    /// Time series decomposition - trend extraction.
    /// </summary>
    private async Task<ToolResult> ExecuteTimeSeriesDecomposeAsync(ToolInvocation invocation)
    {
        var dateColParam = GetParam<string>(invocation, "date_column", "auto");
        var valueColParam = GetParam<string>(invocation, "value_column", "auto");

        // Find date column
        var dateCol = dateColParam == "auto"
            ? _profile.Columns.FirstOrDefault(c => c.InferredType == ColumnType.DateTime)?.Name
            : dateColParam;

        if (string.IsNullOrEmpty(dateCol))
        {
            return new ToolResult
            {
                ToolId = "decompose_timeseries",
                Success = false,
                Error = "No date column found. Specify with date_column parameter."
            };
        }

        // Find value column
        var valueCol = valueColParam == "auto"
            ? _profile.Columns.FirstOrDefault(c => c.InferredType == ColumnType.Numeric)?.Name
            : valueColParam;

        if (string.IsNullOrEmpty(valueCol))
        {
            return new ToolResult
            {
                ToolId = "decompose_timeseries",
                Success = false,
                Error = "No numeric column found for time series analysis."
            };
        }

        // Get monthly aggregations for trend
        var sql = $@"
            SELECT DATE_TRUNC('month', ""{dateCol}"") as period,
                   AVG(""{valueCol}"") as value,
                   COUNT(*) as count
            FROM {_readExpr}
            WHERE ""{dateCol}"" IS NOT NULL AND ""{valueCol}"" IS NOT NULL
            GROUP BY DATE_TRUNC('month', ""{dateCol}"")
            ORDER BY period
            LIMIT 60";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;

        var periods = new List<Dictionary<string, object>>();
        using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            periods.Add(new Dictionary<string, object>
            {
                ["period"] = reader.GetValue(0)?.ToString() ?? "",
                ["value"] = reader.IsDBNull(1) ? 0 : Math.Round(reader.GetDouble(1), 2),
                ["count"] = reader.GetInt64(2)
            });
        }

        if (periods.Count < 3)
        {
            return new ToolResult
            {
                ToolId = "decompose_timeseries",
                Success = false,
                Error = "Not enough data points for time series analysis (need at least 3 periods)"
            };
        }

        // Calculate simple trend (linear regression slope)
        var values = periods.Select(p => (double)p["value"]).ToArray();
        var n = values.Length;
        var xMean = (n - 1) / 2.0;
        var yMean = values.Average();
        
        double sumXY = 0, sumXX = 0;
        for (int i = 0; i < n; i++)
        {
            sumXY += (i - xMean) * (values[i] - yMean);
            sumXX += (i - xMean) * (i - xMean);
        }
        var slope = sumXX != 0 ? sumXY / sumXX : 0;
        var trendDirection = slope > 0 ? "increasing" : slope < 0 ? "decreasing" : "flat";

        var sb = new StringBuilder();
        sb.AppendLine($"**Time Series Analysis: {valueCol} over {dateCol}**");
        sb.AppendLine();
        sb.AppendLine($"- Periods analyzed: {n}");
        sb.AppendLine($"- Overall trend: **{trendDirection}** (slope: {slope:F4}/period)");
        sb.AppendLine($"- Average value: {yMean:F2}");
        sb.AppendLine($"- Range: {values.Min():F2} to {values.Max():F2}");

        return new ToolResult
        {
            ToolId = "decompose_timeseries",
            Success = true,
            Summary = sb.ToString(),
            Data = new Dictionary<string, object>
            {
                ["date_column"] = dateCol,
                ["value_column"] = valueCol,
                ["periods"] = periods,
                ["trend_slope"] = slope,
                ["trend_direction"] = trendDirection,
                ["mean"] = yMean
            },
            RelatedColumns = [dateCol, valueCol],
            VisualizationType = "line_chart"
        };
    }

    // Helper: Normal CDF approximation
    private static double NormalCDF(double x)
    {
        // Approximation using error function
        var t = 1.0 / (1.0 + 0.2316419 * Math.Abs(x));
        var d = 0.3989423 * Math.Exp(-x * x / 2.0);
        var p = d * t * (0.3193815 + t * (-0.3565638 + t * (1.781478 + t * (-1.821256 + t * 1.330274))));
        return x > 0 ? 1.0 - p : p;
    }

    // Helper: Get typed parameter
    private T GetParam<T>(ToolInvocation invocation, string name, T defaultValue)
    {
        if (!invocation.Parameters.TryGetValue(name, out var value))
            return defaultValue;

        try
        {
            if (typeof(T) == typeof(int))
                return (T)(object)Convert.ToInt32(value);
            if (typeof(T) == typeof(double))
                return (T)(object)Convert.ToDouble(value);
            if (typeof(T) == typeof(string))
                return (T)(object)value.ToString()!;
            return (T)value;
        }
        catch
        {
            return defaultValue;
        }
    }
}
