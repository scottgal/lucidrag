using DuckDB.NET.Data;
using Mostlylucid.DataSummarizer.Models;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Detects patterns in data using DuckDB SQL queries.
/// Patterns are used to guide LLM analysis and provide statistical insights.
/// </summary>
public class PatternDetector
{
    private readonly DuckDBConnection _connection;
    private readonly string _readExpr;
    private readonly bool _verbose;

    public PatternDetector(DuckDBConnection connection, string readExpr, bool verbose = false)
    {
        _connection = connection;
        _readExpr = readExpr;
        _verbose = verbose;
    }

    /// <summary>
    /// Detect all patterns for a column based on its type
    /// </summary>
    public async Task<ColumnProfile> EnrichWithPatternsAsync(ColumnProfile column, DataProfile profile)
    {
        try
        {
            switch (column.InferredType)
            {
                case ColumnType.Text:
                    column.TextPatterns = await DetectTextPatternsAsync(column.Name);
                    break;

                case ColumnType.Numeric:
                    column.Distribution = await ClassifyDistributionAsync(column);
                    column.Trend = await DetectTrendAsync(column, profile);
                    
                    // Detect periodicity if there's a date column for ordering
                    var dateColumn = profile.Columns
                        .FirstOrDefault(c => c.InferredType == ColumnType.DateTime);
                    if (dateColumn != null)
                    {
                        column.Periodicity = await AnalyzePeriodicityAsync(column.Name, dateColumn.Name);
                    }
                    break;

                case ColumnType.DateTime:
                    column.TimeSeries = await AnalyzeTimeSeriesAsync(column.Name, profile.RowCount);
                    break;
            }
        }
        catch (Exception ex)
        {
            if (_verbose) Console.WriteLine($"[Pattern] Error enriching {column.Name}: {ex.Message}");
        }

        return column;
    }

    /// <summary>
    /// Detect dataset-level patterns (relationships between columns)
    /// </summary>
    public async Task<List<DetectedPattern>> DetectDatasetPatternsAsync(DataProfile profile)
    {
        var patterns = new List<DetectedPattern>();

        // Detect potential foreign key relationships
        var fkPatterns = await DetectForeignKeyPatternsAsync(profile);
        patterns.AddRange(fkPatterns);

        // Detect monotonic sequences
        var monotonicPatterns = await DetectMonotonicPatternsAsync(profile);
        patterns.AddRange(monotonicPatterns);

        // Detect time-indexed data
        var timeSeriesPattern = DetectTimeSeriesPattern(profile);
        if (timeSeriesPattern != null)
            patterns.Add(timeSeriesPattern);

        return patterns;
    }

    #region Text Pattern Detection

    /// <summary>
    /// Detect text patterns using regex in DuckDB
    /// </summary>
    private async Task<List<TextPatternMatch>> DetectTextPatternsAsync(string column)
    {
        var patterns = new List<TextPatternMatch>();

        // Define regex patterns for common formats
        var regexPatterns = new Dictionary<TextPatternType, string>
        {
            // Email: simple pattern that catches most emails
            [TextPatternType.Email] = @"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$",
            
            // URL: http(s) URLs
            [TextPatternType.Url] = @"^https?://[^\s]+$",
            
            // UUID: standard format with hyphens
            [TextPatternType.Uuid] = @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            
            // Phone: various formats (US-centric but catches many)
            [TextPatternType.Phone] = @"^[\+]?[(]?[0-9]{1,3}[)]?[-\s\.]?[0-9]{3}[-\s\.]?[0-9]{4,6}$",
            
            // IP Address: IPv4
            [TextPatternType.IpAddress] = @"^(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$",
            
            // Credit Card: 13-19 digits with optional separators
            [TextPatternType.CreditCard] = @"^[0-9]{4}[-\s]?[0-9]{4}[-\s]?[0-9]{4}[-\s]?[0-9]{1,7}$",
            
            // Percentage: number with % sign
            [TextPatternType.Percentage] = @"^-?[0-9]+\.?[0-9]*\s*%$",
            
            // Currency: common formats
            [TextPatternType.Currency] = @"^[$\u00a3\u20ac\u00a5][0-9,]+\.?[0-9]*$"
        };

        var totalNonNull = await ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {_readExpr} WHERE \"{column}\" IS NOT NULL");

        if (totalNonNull == 0) return patterns;

        bool foundKnownPattern = false;
        foreach (var (patternType, regex) in regexPatterns)
        {
            try
            {
                var sql = $@"
                    SELECT COUNT(*) FROM {_readExpr}
                    WHERE ""{column}"" IS NOT NULL 
                    AND regexp_matches(""{column}""::VARCHAR, '{regex}')";

                var matchCount = await ExecuteScalarAsync<long>(sql);
                
                if (matchCount > 0)
                {
                    var matchPercent = matchCount * 100.0 / totalNonNull;
                    
                    // Only report if significant portion matches (>10%)
                    if (matchPercent >= 10)
                    {
                        patterns.Add(new TextPatternMatch
                        {
                            PatternType = patternType,
                            MatchCount = (int)matchCount,
                            MatchPercent = Math.Round(matchPercent, 1)
                        });
                        foundKnownPattern = true;
                    }
                }
            }
            catch
            {
                // Skip patterns that fail (regex might not be compatible)
            }
        }
        
        // If no known pattern found and column has consistent structure, try to detect novel patterns
        if (!foundKnownPattern && totalNonNull >= 10)
        {
            var novelPattern = await DetectNovelPatternAsync(column, totalNonNull);
            if (novelPattern != null)
            {
                patterns.Add(novelPattern);
            }
        }

        return patterns.OrderByDescending(p => p.MatchPercent).ToList();
    }

    /// <summary>
    /// Detect novel patterns by analyzing character class structure of column values.
    /// Returns a pattern if >70% of values share a similar structure.
    /// </summary>
    private async Task<TextPatternMatch?> DetectNovelPatternAsync(string column, long totalNonNull)
    {
        try
        {
            // Get sample values and their character class patterns
            // Character class pattern: A=alpha, N=number, S=special, W=whitespace
            var sampleSql = $@"
                WITH samples AS (
                    SELECT DISTINCT ""{column}""::VARCHAR as val
                    FROM {_readExpr}
                    WHERE ""{column}"" IS NOT NULL 
                      AND LENGTH(""{column}""::VARCHAR) BETWEEN 2 AND 100
                    LIMIT 200
                ),
                char_patterns AS (
                    SELECT val,
                           regexp_replace(
                               regexp_replace(
                                   regexp_replace(
                                       regexp_replace(val, '[a-zA-Z]+', 'A', 'g'),
                                       '[0-9]+', 'N', 'g'),
                                   '[\s]+', 'W', 'g'),
                               '[^ANW]+', 'S', 'g') as pattern
                    FROM samples
                )
                SELECT pattern, COUNT(*) as cnt, MIN(val) as example
                FROM char_patterns
                GROUP BY pattern
                ORDER BY cnt DESC
                LIMIT 5";

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sampleSql;
            using var reader = await cmd.ExecuteReaderAsync();

            var patternGroups = new List<(string Pattern, long Count, string Example)>();
            while (await reader.ReadAsync())
            {
                var pattern = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var count = reader.GetInt64(1);
                var example = reader.IsDBNull(2) ? "" : reader.GetString(2);
                patternGroups.Add((pattern, count, example));
            }

            if (patternGroups.Count == 0) return null;

            // Check if dominant pattern covers >70% of samples
            var topPattern = patternGroups[0];
            var totalSampled = patternGroups.Sum(p => p.Count);
            var dominance = (double)topPattern.Count / totalSampled;

            if (dominance < 0.7) return null;

            // Get more examples for this pattern
            var examples = await GetPatternExamplesAsync(column, 5);
            
            // Generate a regex from the character class pattern
            var inferredRegex = CharPatternToRegex(topPattern.Pattern);

            return new TextPatternMatch
            {
                PatternType = TextPatternType.Novel,
                MatchCount = (int)(totalNonNull * dominance),
                MatchPercent = Math.Round(dominance * 100, 1),
                DetectedRegex = inferredRegex,
                Examples = examples,
                Description = $"Consistent format detected: {DescribeCharPattern(topPattern.Pattern)}"
            };
        }
        catch (Exception ex)
        {
            if (_verbose) Console.WriteLine($"[Pattern] Novel detection failed for {column}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get example values from a column
    /// </summary>
    private async Task<List<string>> GetPatternExamplesAsync(string column, int count)
    {
        var examples = new List<string>();
        try
        {
            var sql = $@"
                SELECT DISTINCT ""{column}""::VARCHAR as val
                FROM {_readExpr}
                WHERE ""{column}"" IS NOT NULL
                LIMIT {count}";

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0))
                {
                    examples.Add(reader.GetString(0));
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        return examples;
    }

    /// <summary>
    /// Convert character class pattern (ANSW) to a basic regex
    /// </summary>
    private static string CharPatternToRegex(string charPattern)
    {
        var regex = new System.Text.StringBuilder("^");
        
        foreach (var c in charPattern)
        {
            regex.Append(c switch
            {
                'A' => "[a-zA-Z]+",
                'N' => "[0-9]+",
                'S' => "[^a-zA-Z0-9\\s]+",
                'W' => "\\s+",
                _ => "."
            });
        }
        
        regex.Append('$');
        return regex.ToString();
    }

    /// <summary>
    /// Generate human-readable description of character class pattern
    /// </summary>
    private static string DescribeCharPattern(string charPattern)
    {
        var parts = new List<string>();
        
        foreach (var c in charPattern)
        {
            var desc = c switch
            {
                'A' => "letters",
                'N' => "numbers",
                'S' => "symbols",
                'W' => "space",
                _ => "?"
            };
            
            if (parts.Count == 0 || parts[^1] != desc)
            {
                parts.Add(desc);
            }
        }
        
        return string.Join(" + ", parts);
    }

    #endregion

    #region Distribution Classification

    /// <summary>
    /// Classify the distribution type of a numeric column
    /// </summary>
    private async Task<DistributionType> ClassifyDistributionAsync(ColumnProfile column)
    {
        if (!column.Mean.HasValue || !column.StdDev.HasValue || column.StdDev == 0)
            return DistributionType.Unknown;

        // Get distribution characteristics
        var skewness = column.Skewness ?? 0;
        var kurtosis = await CalculateKurtosisAsync(column.Name);

        // Coefficient of variation (relative spread)
        var cv = column.StdDev.Value / Math.Abs(column.Mean.Value);

        // IQR ratio: how much of the data is in the middle 50%
        double? iqrRatio = null;
        if (column.Q25.HasValue && column.Q75.HasValue && column.Min.HasValue && column.Max.HasValue)
        {
            var range = column.Max.Value - column.Min.Value;
            if (range > 0)
                iqrRatio = (column.Q75.Value - column.Q25.Value) / range;
        }

        // Classification logic
        // Normal: skewness near 0, kurtosis near 3 (excess kurtosis near 0)
        if (Math.Abs(skewness) < 0.5 && kurtosis.HasValue && Math.Abs(kurtosis.Value - 3) < 1)
        {
            return DistributionType.Normal;
        }

        // Uniform: very low kurtosis, IQR is about half the range
        if (kurtosis.HasValue && kurtosis < 2 && iqrRatio.HasValue && iqrRatio > 0.4 && iqrRatio < 0.6)
        {
            return DistributionType.Uniform;
        }

        // Skewed distributions
        if (skewness > 1)
            return DistributionType.RightSkewed;
        if (skewness < -1)
            return DistributionType.LeftSkewed;

        // Exponential: right-skewed with high kurtosis
        if (skewness > 0.5 && kurtosis.HasValue && kurtosis > 6)
        {
            return DistributionType.Exponential;
        }

        // Power law: very high skewness and kurtosis
        if (skewness > 2 && kurtosis.HasValue && kurtosis > 10)
        {
            return DistributionType.PowerLaw;
        }

        // Bimodal detection: check if there are two peaks
        var isBimodal = await DetectBimodalityAsync(column.Name);
        if (isBimodal)
            return DistributionType.Bimodal;

        return DistributionType.Unknown;
    }

    private async Task<double?> CalculateKurtosisAsync(string column)
    {
        // Kurtosis = E[(X - mu)^4] / sigma^4
        var sql = $@"
            SELECT 
                AVG(POW((""{column}"" - sub.mean) / sub.std, 4)) as kurtosis
            FROM {_readExpr},
            (SELECT AVG(""{column}"") as mean, STDDEV(""{column}"") as std FROM {_readExpr}) sub
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

    private async Task<bool> DetectBimodalityAsync(string column)
    {
        // Use histogram to detect two peaks
        // Create 10 bins and check for two local maxima
        var sql = $@"
            WITH binned AS (
                SELECT 
                    width_bucket(""{column}"", 
                        (SELECT MIN(""{column}"") FROM {_readExpr}),
                        (SELECT MAX(""{column}"") FROM {_readExpr}),
                        10) as bin,
                    COUNT(*) as cnt
                FROM {_readExpr}
                WHERE ""{column}"" IS NOT NULL
                GROUP BY 1
            ),
            with_neighbors AS (
                SELECT 
                    bin,
                    cnt,
                    LAG(cnt) OVER (ORDER BY bin) as prev_cnt,
                    LEAD(cnt) OVER (ORDER BY bin) as next_cnt
                FROM binned
            )
            SELECT COUNT(*) as peaks
            FROM with_neighbors
            WHERE cnt > COALESCE(prev_cnt, 0) AND cnt > COALESCE(next_cnt, 0)";

        try
        {
            var peaks = await ExecuteScalarAsync<long>(sql);
            return peaks >= 2;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Trend Detection

    /// <summary>
    /// Detect trends in numeric columns relative to date columns
    /// </summary>
    private async Task<TrendInfo?> DetectTrendAsync(ColumnProfile numericColumn, DataProfile profile)
    {
        // Find a date column to correlate with
        var dateColumn = profile.Columns
            .FirstOrDefault(c => c.InferredType == ColumnType.DateTime);

        if (dateColumn == null)
        {
            // Try to detect trend using row order
            return await DetectTrendByRowOrderAsync(numericColumn.Name);
        }

        return await DetectTrendByDateAsync(numericColumn.Name, dateColumn.Name);
    }

    private async Task<TrendInfo?> DetectTrendByDateAsync(string valueColumn, string dateColumn)
    {
        // Calculate simple linear regression: value vs days since start
        var sql = $@"
            WITH numbered AS (
                SELECT 
                    ""{valueColumn}"" as y,
                    DATE_DIFF('day', MIN(""{dateColumn}"") OVER(), ""{dateColumn}"") as x
                FROM {_readExpr}
                WHERE ""{valueColumn}"" IS NOT NULL AND ""{dateColumn}"" IS NOT NULL
            ),
            stats AS (
                SELECT 
                    COUNT(*) as n,
                    AVG(x) as x_mean,
                    AVG(y) as y_mean,
                    SUM((x - (SELECT AVG(x) FROM numbered)) * (y - (SELECT AVG(y) FROM numbered))) as numerator,
                    SUM(POWER(x - (SELECT AVG(x) FROM numbered), 2)) as denominator,
                    SUM(POWER(y - (SELECT AVG(y) FROM numbered), 2)) as ss_total
                FROM numbered
            )
            SELECT 
                CASE WHEN denominator > 0 THEN numerator / denominator ELSE 0 END as slope,
                CASE WHEN ss_total > 0 THEN 1 - (SUM(POWER(y - (y_mean + (numerator/NULLIF(denominator,0)) * (x - x_mean)), 2)) / ss_total) ELSE 0 END as r_squared
            FROM numbered, stats
            GROUP BY slope, y_mean, x_mean, ss_total, denominator, numerator";

        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                var slope = reader.IsDBNull(0) ? 0 : reader.GetDouble(0);
                var rSquared = reader.IsDBNull(1) ? 0 : Math.Max(0, Math.Min(1, reader.GetDouble(1)));

                // Only report if there's a meaningful correlation (R^2 > 0.3)
                if (rSquared > 0.3 || Math.Abs(slope) > 0.001)
                {
                    return new TrendInfo
                    {
                        Direction = slope > 0.001 ? TrendDirection.Increasing 
                                  : slope < -0.001 ? TrendDirection.Decreasing 
                                  : TrendDirection.None,
                        Slope = Math.Round(slope, 4),
                        RSquared = Math.Round(rSquared, 3),
                        RelatedDateColumn = dateColumn
                    };
                }
            }
        }
        catch (Exception ex)
        {
            if (_verbose) Console.WriteLine($"[Pattern] Trend detection failed: {ex.Message}");
        }

        return null;
    }

    private async Task<TrendInfo?> DetectTrendByRowOrderAsync(string column)
    {
        // Use row number as X axis (assumes data is ordered)
        var sql = $@"
            WITH numbered AS (
                SELECT 
                    ""{column}"" as y,
                    ROW_NUMBER() OVER() as x
                FROM {_readExpr}
                WHERE ""{column}"" IS NOT NULL
                LIMIT 10000
            ),
            stats AS (
                SELECT 
                    AVG(x) as x_mean,
                    AVG(y) as y_mean,
                    SUM((x - (SELECT AVG(x) FROM numbered)) * (y - (SELECT AVG(y) FROM numbered))) as cov_xy,
                    SUM(POWER(x - (SELECT AVG(x) FROM numbered), 2)) as var_x,
                    SUM(POWER(y - (SELECT AVG(y) FROM numbered), 2)) as var_y
                FROM numbered
            )
            SELECT 
                CASE WHEN var_x > 0 THEN cov_xy / var_x ELSE 0 END as slope,
                CASE WHEN var_x > 0 AND var_y > 0 THEN POWER(cov_xy, 2) / (var_x * var_y) ELSE 0 END as r_squared
            FROM stats";

        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                var slope = reader.IsDBNull(0) ? 0 : reader.GetDouble(0);
                var rSquared = reader.IsDBNull(1) ? 0 : Math.Max(0, Math.Min(1, reader.GetDouble(1)));

                if (rSquared > 0.5) // Only report strong trends without date context
                {
                    return new TrendInfo
                    {
                        Direction = slope > 0 ? TrendDirection.Increasing 
                                  : slope < 0 ? TrendDirection.Decreasing 
                                  : TrendDirection.None,
                        Slope = Math.Round(slope, 4),
                        RSquared = Math.Round(rSquared, 3),
                        RelatedDateColumn = null
                    };
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return null;
    }

    #endregion

    #region Time Series Analysis

    /// <summary>
    /// Analyze time series characteristics of a date column
    /// </summary>
    private async Task<TimeSeriesInfo?> AnalyzeTimeSeriesAsync(string column, long totalRows)
    {
        try
        {
            var info = new TimeSeriesInfo { DateColumn = column };

            // Detect granularity by looking at most common time difference
            info.Granularity = await DetectGranularityAsync(column);

            // Count gaps
            var gapInfo = await CountGapsAsync(column, info.Granularity);
            info.GapCount = gapInfo.gapCount;
            info.GapPercent = totalRows > 0 ? Math.Round(gapInfo.gapCount * 100.0 / totalRows, 1) : 0;
            info.IsContiguous = info.GapPercent < 5;

            // Detect seasonality (simplified - check for weekly/monthly patterns)
            info.HasSeasonality = await DetectSeasonalityAsync(column);
            if (info.HasSeasonality)
            {
                info.SeasonalPeriod = info.Granularity switch
                {
                    TimeGranularity.Daily => 7,    // Weekly
                    TimeGranularity.Weekly => 52,  // Yearly
                    TimeGranularity.Monthly => 12, // Yearly
                    _ => null
                };
            }

            return info;
        }
        catch
        {
            return null;
        }
    }

    private async Task<TimeGranularity> DetectGranularityAsync(string column)
    {
        // Calculate most common time difference
        var sql = $@"
            WITH diffs AS (
                SELECT 
                    DATE_DIFF('second', LAG(""{column}"") OVER (ORDER BY ""{column}""), ""{column}"") as diff_seconds
                FROM {_readExpr}
                WHERE ""{column}"" IS NOT NULL
                ORDER BY ""{column}""
                LIMIT 1000
            )
            SELECT 
                PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY diff_seconds) as median_diff
            FROM diffs
            WHERE diff_seconds IS NOT NULL AND diff_seconds > 0";

        try
        {
            var medianDiffSeconds = await ExecuteScalarAsync<double?>(sql);
            
            if (!medianDiffSeconds.HasValue || medianDiffSeconds <= 0)
                return TimeGranularity.Unknown;

            var seconds = medianDiffSeconds.Value;

            return seconds switch
            {
                < 120 => TimeGranularity.Minute,
                < 7200 => TimeGranularity.Hourly,
                < 172800 => TimeGranularity.Daily,      // < 2 days
                < 864000 => TimeGranularity.Weekly,     // < 10 days
                < 5184000 => TimeGranularity.Monthly,   // < 60 days
                < 15552000 => TimeGranularity.Quarterly, // < 180 days
                _ => TimeGranularity.Yearly
            };
        }
        catch
        {
            return TimeGranularity.Unknown;
        }
    }

    private async Task<(int gapCount, int expectedCount)> CountGapsAsync(string column, TimeGranularity granularity)
    {
        // Count missing time periods based on granularity
        var datePart = granularity switch
        {
            TimeGranularity.Minute => "minute",
            TimeGranularity.Hourly => "hour",
            TimeGranularity.Daily => "day",
            TimeGranularity.Weekly => "week",
            TimeGranularity.Monthly => "month",
            TimeGranularity.Quarterly => "quarter",
            TimeGranularity.Yearly => "year",
            _ => "day"
        };

        var sql = $@"
            WITH bounds AS (
                SELECT MIN(""{column}"") as min_date, MAX(""{column}"") as max_date
                FROM {_readExpr}
                WHERE ""{column}"" IS NOT NULL
            ),
            actual_periods AS (
                SELECT DISTINCT DATE_TRUNC('{datePart}', ""{column}"") as period
                FROM {_readExpr}
                WHERE ""{column}"" IS NOT NULL
            )
            SELECT 
                DATE_DIFF('{datePart}', min_date, max_date) + 1 as expected,
                (SELECT COUNT(*) FROM actual_periods) as actual
            FROM bounds";

        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                var expected = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                var actual = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
                return (Math.Max(0, expected - actual), expected);
            }
        }
        catch
        {
            // Ignore
        }

        return (0, 0);
    }

    private async Task<bool> DetectSeasonalityAsync(string column)
    {
        // Simplified seasonality detection: check if day-of-week matters for daily data
        var sql = $@"
            WITH daily_counts AS (
                SELECT 
                    DAYOFWEEK(""{column}"") as dow,
                    COUNT(*) as cnt
                FROM {_readExpr}
                WHERE ""{column}"" IS NOT NULL
                GROUP BY 1
            )
            SELECT STDDEV(cnt) / AVG(cnt) as cv
            FROM daily_counts
            HAVING COUNT(*) >= 5";

        try
        {
            var cv = await ExecuteScalarAsync<double?>(sql);
            // High coefficient of variation suggests seasonality
            return cv.HasValue && cv > 0.3;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Detect dominant period in a numeric column using autocorrelation.
    /// Uses DuckDB window functions for efficient lag computation.
    /// Returns (hasPeriodicity, dominantPeriod, confidence)
    /// </summary>
    public async Task<(bool HasPeriodicity, int? DominantPeriod, double Confidence)> DetectPeriodicityAsync(
        string numericColumn, 
        string? orderByColumn = null,
        int maxLag = 50,
        int sampleSize = 1000)
    {
        try
        {
            // Compute autocorrelation at various lags using DuckDB
            // ACF(k) = Σ((x_t - μ)(x_{t-k} - μ)) / Σ(x_t - μ)²
            var orderClause = string.IsNullOrEmpty(orderByColumn) 
                ? $"\"{numericColumn}\"" 
                : $"\"{orderByColumn}\"";
            
            var sql = $@"
                WITH sampled AS (
                    SELECT 
                        ""{numericColumn}"" as val,
                        ROW_NUMBER() OVER (ORDER BY {orderClause}) as rn
                    FROM {_readExpr}
                    WHERE ""{numericColumn}"" IS NOT NULL
                    ORDER BY {orderClause}
                    LIMIT {sampleSize}
                ),
                stats AS (
                    SELECT AVG(val) as mean, STDDEV(val) as std FROM sampled
                ),
                normalized AS (
                    SELECT rn, (val - stats.mean) as centered
                    FROM sampled, stats
                ),
                variance AS (
                    SELECT SUM(centered * centered) as total_var FROM normalized
                ),
                -- Compute autocorrelation for lags 1 to {maxLag}
                acf AS (
                    SELECT 
                        lag_val,
                        SUM(n1.centered * n2.centered) / variance.total_var as acf
                    FROM (SELECT UNNEST(RANGE(1, {maxLag + 1})) as lag_val) lags
                    CROSS JOIN variance
                    JOIN normalized n1 ON true
                    JOIN normalized n2 ON n2.rn = n1.rn + lags.lag_val
                    GROUP BY lag_val, variance.total_var
                    ORDER BY lag_val
                )
                SELECT lag_val, acf
                FROM acf
                WHERE acf IS NOT NULL
                ORDER BY lag_val";

            var acfValues = new List<(int Lag, double Acf)>();
            
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                var lag = reader.GetInt32(0);
                var acf = reader.GetDouble(1);
                acfValues.Add((lag, acf));
            }

            if (acfValues.Count < 3)
                return (false, null, 0);

            // Find peaks in ACF (local maxima that are significant)
            var peaks = FindAcfPeaks(acfValues);
            
            if (peaks.Count == 0)
                return (false, null, 0);

            // The first significant peak indicates the dominant period
            var dominantPeak = peaks.First();
            
            // Confidence is the ACF value at the peak (0-1 range, higher = stronger periodicity)
            var confidence = Math.Max(0, Math.Min(1, dominantPeak.Acf));
            
            // Only report if confidence is reasonable
            if (confidence < 0.2)
                return (false, null, 0);

            return (true, dominantPeak.Lag, confidence);
        }
        catch (Exception ex)
        {
            if (_verbose) Console.WriteLine($"[Pattern] Periodicity detection failed: {ex.Message}");
            return (false, null, 0);
        }
    }

    /// <summary>
    /// Find peaks in autocorrelation function (local maxima above threshold)
    /// </summary>
    private List<(int Lag, double Acf)> FindAcfPeaks(List<(int Lag, double Acf)> acfValues, double threshold = 0.2)
    {
        var peaks = new List<(int Lag, double Acf)>();
        
        for (int i = 1; i < acfValues.Count - 1; i++)
        {
            var prev = acfValues[i - 1].Acf;
            var curr = acfValues[i].Acf;
            var next = acfValues[i + 1].Acf;
            
            // Local maximum above threshold
            if (curr > prev && curr > next && curr > threshold)
            {
                peaks.Add(acfValues[i]);
            }
        }
        
        return peaks.OrderByDescending(p => p.Acf).ToList();
    }

    /// <summary>
    /// Quick periodicity check for numeric time series - uses sampled autocorrelation
    /// </summary>
    public async Task<PeriodicityInfo?> AnalyzePeriodicityAsync(string numericColumn, string? dateColumn = null)
    {
        var (hasPeriodicity, period, confidence) = await DetectPeriodicityAsync(
            numericColumn, 
            dateColumn,
            maxLag: 60,  // Check up to 60 lags (e.g., 60 days, weeks, etc.)
            sampleSize: 500);

        if (!hasPeriodicity || !period.HasValue)
            return null;

        return new PeriodicityInfo
        {
            HasPeriodicity = true,
            DominantPeriod = period.Value,
            Confidence = confidence,
            SuggestedInterpretation = InterpretPeriod(period.Value)
        };
    }

    private string InterpretPeriod(int period) => period switch
    {
        7 => "Weekly cycle (7 periods)",
        12 => "Monthly cycle (12 periods) - possibly yearly pattern in monthly data",
        14 => "Bi-weekly cycle (14 periods)",
        24 => "Daily cycle (24 periods) - possibly hourly data",
        30 or 31 => "Monthly cycle (~30 periods)",
        52 => "Yearly cycle (52 periods) - possibly weekly data",
        365 or 366 => "Yearly cycle (365 periods) - possibly daily data",
        _ when period <= 3 => $"Short cycle ({period} periods)",
        _ when period <= 10 => $"Short-term cycle ({period} periods)",
        _ => $"Cycle detected ({period} periods)"
    };

    #endregion

    #region Relationship Detection

    /// <summary>
    /// Detect potential foreign key relationships
    /// </summary>
    private async Task<List<DetectedPattern>> DetectForeignKeyPatternsAsync(DataProfile profile)
    {
        var patterns = new List<DetectedPattern>();

        // Look for ID columns and their potential references
        var idColumns = profile.Columns
            .Where(c => c.InferredType == ColumnType.Id || 
                       c.Name.ToLowerInvariant().EndsWith("_id") ||
                       c.Name.ToLowerInvariant().EndsWith("id"))
            .ToList();

        var categoricalColumns = profile.Columns
            .Where(c => c.InferredType == ColumnType.Categorical && 
                       c.UniqueCount > 1 && c.UniqueCount <= 1000)
            .ToList();

        // Check if categorical column values are subset of another column
        foreach (var catCol in categoricalColumns)
        {
            foreach (var idCol in idColumns)
            {
                if (catCol.Name == idCol.Name) continue;

                try
                {
                    var overlap = await CalculateValueOverlapAsync(catCol.Name, idCol.Name);
                    
                    if (overlap > 0.9) // 90%+ overlap suggests FK relationship
                    {
                        patterns.Add(new DetectedPattern
                        {
                            Type = PatternType.ForeignKey,
                            Description = $"'{catCol.Name}' values are likely references to '{idCol.Name}' ({overlap:P0} overlap)",
                            RelatedColumns = [catCol.Name, idCol.Name],
                            Confidence = overlap,
                            Details = new Dictionary<string, object>
                            {
                                ["source_column"] = catCol.Name,
                                ["target_column"] = idCol.Name,
                                ["overlap_percent"] = overlap * 100
                            }
                        });
                    }
                }
                catch
                {
                    // Skip if comparison fails
                }
            }
        }

        return patterns;
    }

    private async Task<double> CalculateValueOverlapAsync(string col1, string col2)
    {
        var sql = $@"
            WITH col1_vals AS (
                SELECT DISTINCT ""{col1}""::VARCHAR as val FROM {_readExpr} WHERE ""{col1}"" IS NOT NULL
            ),
            col2_vals AS (
                SELECT DISTINCT ""{col2}""::VARCHAR as val FROM {_readExpr} WHERE ""{col2}"" IS NOT NULL
            )
            SELECT 
                (SELECT COUNT(*) FROM col1_vals WHERE val IN (SELECT val FROM col2_vals))::DOUBLE / 
                NULLIF((SELECT COUNT(*) FROM col1_vals), 0) as overlap";

        return await ExecuteScalarAsync<double>(sql);
    }

    /// <summary>
    /// Detect monotonic (strictly increasing/decreasing) columns
    /// </summary>
    private async Task<List<DetectedPattern>> DetectMonotonicPatternsAsync(DataProfile profile)
    {
        var patterns = new List<DetectedPattern>();

        foreach (var col in profile.Columns.Where(c => c.InferredType == ColumnType.Numeric || c.InferredType == ColumnType.Id))
        {
            try
            {
                var sql = $@"
                    WITH numbered AS (
                        SELECT 
                            ""{col.Name}"" as val,
                            LAG(""{col.Name}"") OVER (ORDER BY ROWID) as prev_val
                        FROM {_readExpr}
                        WHERE ""{col.Name}"" IS NOT NULL
                        LIMIT 10000
                    )
                    SELECT 
                        SUM(CASE WHEN val > prev_val THEN 1 ELSE 0 END) as increasing,
                        SUM(CASE WHEN val < prev_val THEN 1 ELSE 0 END) as decreasing,
                        COUNT(*) as total
                    FROM numbered
                    WHERE prev_val IS NOT NULL";

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                using var reader = await cmd.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    var increasing = reader.GetInt64(0);
                    var decreasing = reader.GetInt64(1);
                    var total = reader.GetInt64(2);

                    if (total > 0)
                    {
                        var incRatio = (double)increasing / total;
                        var decRatio = (double)decreasing / total;

                        if (incRatio > 0.95)
                        {
                            patterns.Add(new DetectedPattern
                            {
                                Type = PatternType.Monotonic,
                                Description = $"'{col.Name}' is monotonically increasing ({incRatio:P0} of transitions)",
                                RelatedColumns = [col.Name],
                                Confidence = incRatio,
                                Details = new Dictionary<string, object>
                                {
                                    ["direction"] = "increasing",
                                    ["ratio"] = incRatio
                                }
                            });
                        }
                        else if (decRatio > 0.95)
                        {
                            patterns.Add(new DetectedPattern
                            {
                                Type = PatternType.Monotonic,
                                Description = $"'{col.Name}' is monotonically decreasing ({decRatio:P0} of transitions)",
                                RelatedColumns = [col.Name],
                                Confidence = decRatio,
                                Details = new Dictionary<string, object>
                                {
                                    ["direction"] = "decreasing",
                                    ["ratio"] = decRatio
                                }
                            });
                        }
                    }
                }
            }
            catch
            {
                // Skip if detection fails
            }
        }

        return patterns;
    }

    private DetectedPattern? DetectTimeSeriesPattern(DataProfile profile)
    {
        var dateCol = profile.Columns.FirstOrDefault(c => c.InferredType == ColumnType.DateTime);
        if (dateCol?.TimeSeries == null) return null;

        var ts = dateCol.TimeSeries;
        
        return new DetectedPattern
        {
            Type = PatternType.TimeSeries,
            Description = $"Data appears to be a {ts.Granularity} time series indexed by '{dateCol.Name}'" +
                         (ts.HasSeasonality ? $" with potential seasonality (period: {ts.SeasonalPeriod})" : "") +
                         (ts.IsContiguous ? " (contiguous)" : $" ({ts.GapCount} gaps detected)"),
            RelatedColumns = [dateCol.Name],
            Confidence = ts.IsContiguous ? 0.9 : 0.7,
            Details = new Dictionary<string, object>
            {
                ["granularity"] = ts.Granularity.ToString(),
                ["gap_count"] = ts.GapCount,
                ["has_seasonality"] = ts.HasSeasonality,
                ["is_contiguous"] = ts.IsContiguous
            }
        };
    }

    #endregion

    #region Helpers

    private async Task<T> ExecuteScalarAsync<T>(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();

        if (result == null || result == DBNull.Value)
            return default!;

        return (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    #endregion
}
