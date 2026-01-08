using System.Text.RegularExpressions;
using MathNet.Numerics.Statistics;
using Mostlylucid.DataSummarizer.Models;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Profiles query results to extract statistics that enrich the data profile.
/// When the LLM executes SQL, this service analyzes the result set and stores
/// derived statistics for future queries.
/// </summary>
public class QueryResultProfiler
{
    private readonly bool _verbose;

    public QueryResultProfiler(bool verbose = false)
    {
        _verbose = verbose;
    }

    /// <summary>
    /// Profile a query result and create a cached entry with derived statistics.
    /// </summary>
    public CachedQueryResult ProfileQueryResult(
        string question,
        string sql,
        string summary,
        object? resultData,
        List<string> relatedColumns)
    {
        var cached = new CachedQueryResult
        {
            Question = question,
            NormalizedQuestion = NormalizeQuestion(question),
            Sql = sql,
            Summary = summary,
            RelatedColumns = relatedColumns,
            CachedAt = DateTime.UtcNow,
            FilterContext = ExtractFilterContext(sql)
        };

        // Extract and analyze result data
        if (resultData is { } data)
        {
            cached.ResultData = ConvertToDict(data);
            cached.DerivedStats = ComputeDerivedStats(data);
        }

        return cached;
    }

    /// <summary>
    /// Extract filter context from SQL WHERE clause.
    /// E.g., "WHERE Category = 'Electronics'" → "Category='Electronics'"
    /// </summary>
    private string? ExtractFilterContext(string sql)
    {
        var match = Regex.Match(sql, @"WHERE\s+(.+?)(?:\s+GROUP|\s+ORDER|\s+LIMIT|$)", 
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        
        if (!match.Success) return null;
        
        var whereClause = match.Groups[1].Value.Trim();
        
        // Normalize: remove extra whitespace, standardize quotes
        whereClause = Regex.Replace(whereClause, @"\s+", " ");
        
        return string.IsNullOrWhiteSpace(whereClause) ? null : whereClause;
    }

    /// <summary>
    /// Normalize question for matching similar queries.
    /// </summary>
    private string NormalizeQuestion(string question)
    {
        var normalized = question.ToLowerInvariant();
        
        // Remove punctuation
        normalized = Regex.Replace(normalized, @"[^\w\s]", " ");
        
        // Normalize whitespace
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        
        // Remove common filler words
        var fillers = new[] { "the", "a", "an", "is", "are", "what", "show", "me", "please", "can", "you" };
        foreach (var filler in fillers)
        {
            normalized = Regex.Replace(normalized, $@"\b{filler}\b", " ");
        }
        
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Compute derived statistics from query result.
    /// </summary>
    private QueryResultStats? ComputeDerivedStats(object data)
    {
        // Handle the anonymous type from ExecuteQueryAsync
        var dataType = data.GetType();
        var columnsProperty = dataType.GetProperty("columns");
        var rowsProperty = dataType.GetProperty("rows");
        
        if (columnsProperty == null || rowsProperty == null)
            return null;

        var columns = columnsProperty.GetValue(data) as List<string>;
        var rows = rowsProperty.GetValue(data) as List<Dictionary<string, object?>>;

        if (columns == null || rows == null || rows.Count == 0)
            return null;

        var stats = new QueryResultStats
        {
            RowCount = rows.Count
        };

        // Analyze each column
        foreach (var column in columns)
        {
            var values = rows
                .Select(r => r.TryGetValue(column, out var v) ? v : null)
                .Where(v => v != null)
                .ToList();

            if (values.Count == 0) continue;

            // Try to detect if numeric
            var numericValues = new List<double>();
            var stringValues = new List<string>();

            foreach (var val in values)
            {
                if (val is double d)
                    numericValues.Add(d);
                else if (val is int i)
                    numericValues.Add(i);
                else if (val is long l)
                    numericValues.Add(l);
                else if (val is decimal dec)
                    numericValues.Add((double)dec);
                else if (val is float f)
                    numericValues.Add(f);
                else if (double.TryParse(val?.ToString(), out var parsed))
                    numericValues.Add(parsed);
                else
                    stringValues.Add(val?.ToString() ?? "");
            }

            // If mostly numeric, compute numeric stats
            if (numericValues.Count > values.Count / 2 && numericValues.Count >= 2)
            {
                stats.NumericStats[column] = ComputeNumericStats(numericValues);
            }
            // If mostly string/categorical, compute distribution
            else if (stringValues.Count > 0)
            {
                var distribution = stringValues
                    .GroupBy(s => s)
                    .ToDictionary(g => g.Key, g => g.Count());
                
                // Only store if reasonable cardinality
                if (distribution.Count <= 50)
                {
                    stats.CategoryDistributions[column] = distribution;
                }
            }
        }

        // Detect patterns in numeric data
        if (stats.NumericStats.Count > 0)
        {
            stats.DetectedPatterns = DetectPatterns(stats);
        }

        return stats;
    }

    private NumericResultStats ComputeNumericStats(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var mean = values.Average();
        var stdDev = values.Count > 1 ? Statistics.StandardDeviation(values) : 0;
        
        // Quartiles
        var q25 = Statistics.Percentile(sorted, 25);
        var q75 = Statistics.Percentile(sorted, 75);
        var iqr = q75 - q25;
        
        // Count outliers using IQR method
        var lowerBound = q25 - 1.5 * iqr;
        var upperBound = q75 + 1.5 * iqr;
        var outlierCount = values.Count(v => v < lowerBound || v > upperBound);

        return new NumericResultStats
        {
            Min = sorted[0],
            Max = sorted[^1],
            Mean = Math.Round(mean, 4),
            Median = Statistics.Median(sorted),
            StdDev = Math.Round(stdDev, 4),
            Q25 = q25,
            Q75 = q75,
            OutlierCount = outlierCount
        };
    }

    private List<string>? DetectPatterns(QueryResultStats stats)
    {
        var patterns = new List<string>();

        foreach (var (column, numStats) in stats.NumericStats)
        {
            // High outlier percentage
            if (stats.RowCount > 0 && numStats.OutlierCount > stats.RowCount * 0.1)
            {
                patterns.Add($"{column}: {numStats.OutlierCount} outliers ({numStats.OutlierCount * 100.0 / stats.RowCount:F1}%)");
            }

            // Skewness indication (mean far from median)
            if (numStats.StdDev > 0)
            {
                var meanMedianDiff = Math.Abs(numStats.Mean - numStats.Median) / numStats.StdDev;
                if (meanMedianDiff > 0.5)
                {
                    var direction = numStats.Mean > numStats.Median ? "right" : "left";
                    patterns.Add($"{column}: {direction}-skewed distribution");
                }
            }

            // Low variance (nearly constant)
            if (numStats.Max - numStats.Min < numStats.Mean * 0.01 && numStats.Mean != 0)
            {
                patterns.Add($"{column}: nearly constant (range={numStats.Max - numStats.Min:F4})");
            }
        }

        return patterns.Count > 0 ? patterns : null;
    }

    private Dictionary<string, object> ConvertToDict(object data)
    {
        var result = new Dictionary<string, object>();
        
        var dataType = data.GetType();
        foreach (var prop in dataType.GetProperties())
        {
            var value = prop.GetValue(data);
            if (value != null)
            {
                result[prop.Name] = value;
            }
        }
        
        return result;
    }

    /// <summary>
    /// Check if a cached query can answer a new question.
    /// Uses normalized question similarity and filter context matching.
    /// </summary>
    public bool CanReuseCache(CachedQueryResult cached, string newQuestion, DataProfile profile)
    {
        var normalizedNew = NormalizeQuestion(newQuestion);
        
        // Exact match
        if (cached.NormalizedQuestion == normalizedNew)
            return true;

        // Check for filter context overlap
        // E.g., "average price for Electronics" can reuse cache with FilterContext="Category='Electronics'"
        if (!string.IsNullOrEmpty(cached.FilterContext))
        {
            var filterValues = ExtractFilterValues(cached.FilterContext);
            foreach (var (col, val) in filterValues)
            {
                if (newQuestion.Contains(val, StringComparison.OrdinalIgnoreCase))
                {
                    // Question mentions the same filter value
                    // Check if it's asking for something we have stats for
                    if (cached.DerivedStats?.NumericStats.Count > 0)
                    {
                        // Could potentially answer from cached stats
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private List<(string Column, string Value)> ExtractFilterValues(string filterContext)
    {
        var results = new List<(string, string)>();
        
        // Match patterns like "Column" = 'Value' or Column = 'Value'
        var matches = Regex.Matches(filterContext, 
            @"""?(\w+)""?\s*=\s*'([^']+)'",
            RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            results.Add((match.Groups[1].Value, match.Groups[2].Value));
        }

        return results;
    }

    /// <summary>
    /// Generate aggregate statistics from cached query for profile enrichment.
    /// </summary>
    public List<AggregateStatistic> ExtractAggregatesFromCache(CachedQueryResult cached)
    {
        var aggregates = new List<AggregateStatistic>();

        if (cached.DerivedStats == null || string.IsNullOrEmpty(cached.FilterContext))
            return aggregates;

        // Extract the group value from filter context
        var filterValues = ExtractFilterValues(cached.FilterContext);
        if (filterValues.Count == 0)
            return aggregates;

        var (groupByCol, groupValue) = filterValues[0];

        // Create aggregate entries for each numeric stat
        foreach (var (measureCol, stats) in cached.DerivedStats.NumericStats)
        {
            // AVG
            aggregates.Add(new AggregateStatistic
            {
                GroupByColumn = groupByCol,
                MeasureColumn = measureCol,
                AggregateFunction = "AVG",
                Results = new Dictionary<string, double> { [groupValue] = stats.Mean },
                Source = "LLM",
                ComputedAt = cached.CachedAt
            });

            // Also add MIN, MAX, MEDIAN if useful
            aggregates.Add(new AggregateStatistic
            {
                GroupByColumn = groupByCol,
                MeasureColumn = measureCol,
                AggregateFunction = "MIN",
                Results = new Dictionary<string, double> { [groupValue] = stats.Min },
                Source = "LLM",
                ComputedAt = cached.CachedAt
            });

            aggregates.Add(new AggregateStatistic
            {
                GroupByColumn = groupByCol,
                MeasureColumn = measureCol,
                AggregateFunction = "MAX",
                Results = new Dictionary<string, double> { [groupValue] = stats.Max },
                Source = "LLM",
                ComputedAt = cached.CachedAt
            });
        }

        return aggregates;
    }
}
