using Mostlylucid.DataSummarizer.Models;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Computes an overall anomaly score for a dataset based on data quality issues,
/// statistical anomalies, and structural patterns.
/// Score ranges from 0 (clean) to 1 (highly anomalous).
/// </summary>
public static class AnomalyScorer
{
    /// <summary>
    /// Compute overall anomaly score for a profile
    /// </summary>
    public static AnomalyScoreResult ComputeAnomalyScore(DataProfile profile)
    {
        var result = new AnomalyScoreResult
        {
            ProfileSource = profile.SourcePath,
            ComputedAt = DateTime.UtcNow
        };

        var componentScores = new List<(string Name, double Score, double Weight)>();

        // 1. Data Quality Score (based on alerts)
        var dataQualityScore = ComputeDataQualityScore(profile, result);
        componentScores.Add(("DataQuality", dataQualityScore, 0.25));

        // 2. Null Rate Score
        var nullScore = ComputeNullScore(profile, result);
        componentScores.Add(("NullRate", nullScore, 0.15));

        // 3. Outlier Score
        var outlierScore = ComputeOutlierScore(profile, result);
        componentScores.Add(("Outliers", outlierScore, 0.20));

        // 4. Distribution Anomaly Score
        var distributionScore = ComputeDistributionScore(profile, result);
        componentScores.Add(("Distribution", distributionScore, 0.15));

        // 5. Cardinality Anomaly Score
        var cardinalityScore = ComputeCardinalityScore(profile, result);
        componentScores.Add(("Cardinality", cardinalityScore, 0.10));

        // 6. Schema/Structure Score
        var schemaScore = ComputeSchemaScore(profile, result);
        componentScores.Add(("Schema", schemaScore, 0.15));

        // Compute weighted average
        var totalWeight = componentScores.Sum(c => c.Weight);
        result.OverallScore = componentScores.Sum(c => c.Score * c.Weight) / totalWeight;
        result.OverallScore = Math.Round(result.OverallScore, 4);

        // Add component breakdown
        foreach (var (name, score, weight) in componentScores)
        {
            result.Components.Add(new AnomalyComponent
            {
                Name = name,
                Score = Math.Round(score, 4),
                Weight = weight,
                WeightedScore = Math.Round(score * weight / totalWeight, 4)
            });
        }

        // Interpret overall score
        result.Interpretation = result.OverallScore switch
        {
            < 0.1 => "Excellent",
            < 0.2 => "Good",
            < 0.35 => "Fair",
            < 0.5 => "Concerning",
            < 0.7 => "Poor",
            _ => "Critical"
        };

        // Generate recommendations
        result.Recommendations = GenerateRecommendations(profile, result);

        return result;
    }

    private static double ComputeDataQualityScore(DataProfile profile, AnomalyScoreResult result)
    {
        if (profile.Alerts.Count == 0) return 0;

        // Weight by severity
        var errorCount = profile.Alerts.Count(a => a.Severity == AlertSeverity.Error);
        var warningCount = profile.Alerts.Count(a => a.Severity == AlertSeverity.Warning);
        var infoCount = profile.Alerts.Count(a => a.Severity == AlertSeverity.Info);

        // Normalize by column count (more columns = more potential alerts)
        var normalizedErrors = Math.Min(1.0, errorCount / Math.Max(1.0, profile.ColumnCount * 0.3));
        var normalizedWarnings = Math.Min(1.0, warningCount / Math.Max(1.0, profile.ColumnCount * 0.5));
        var normalizedInfo = Math.Min(1.0, infoCount / Math.Max(1.0, profile.ColumnCount));

        var score = normalizedErrors * 0.6 + normalizedWarnings * 0.3 + normalizedInfo * 0.1;

        result.Issues.Add(new AnomalyIssue
        {
            Category = "DataQuality",
            Description = $"{errorCount} errors, {warningCount} warnings, {infoCount} info alerts",
            Severity = errorCount > 0 ? "High" : warningCount > 0 ? "Medium" : "Low"
        });

        return Math.Min(1.0, score);
    }

    private static double ComputeNullScore(DataProfile profile, AnomalyScoreResult result)
    {
        if (profile.Columns.Count == 0) return 0;

        var avgNullPercent = profile.Columns.Average(c => c.NullPercent);
        var maxNullPercent = profile.Columns.Max(c => c.NullPercent);
        var highNullColumns = profile.Columns.Count(c => c.NullPercent > 20);

        // Score based on severity
        var avgScore = avgNullPercent / 100.0;
        var maxScore = maxNullPercent / 100.0;
        var countScore = (double)highNullColumns / profile.Columns.Count;

        var score = avgScore * 0.3 + maxScore * 0.4 + countScore * 0.3;

        if (highNullColumns > 0)
        {
            result.Issues.Add(new AnomalyIssue
            {
                Category = "NullRate",
                Description = $"{highNullColumns} columns with >20% null values (avg: {avgNullPercent:F1}%)",
                Severity = maxNullPercent > 50 ? "High" : "Medium",
                AffectedColumns = profile.Columns.Where(c => c.NullPercent > 20).Select(c => c.Name).ToList()
            });
        }

        return Math.Min(1.0, score);
    }

    private static double ComputeOutlierScore(DataProfile profile, AnomalyScoreResult result)
    {
        var numericCols = profile.Columns.Where(c => c.InferredType == ColumnType.Numeric).ToList();
        if (numericCols.Count == 0) return 0;

        var outlierRatios = numericCols
            .Where(c => c.OutlierCount > 0)
            .Select(c => (Column: c, Ratio: (double)c.OutlierCount / profile.RowCount))
            .ToList();

        if (outlierRatios.Count == 0) return 0;

        var avgOutlierRatio = outlierRatios.Average(o => o.Ratio);
        var maxOutlierRatio = outlierRatios.Max(o => o.Ratio);
        var highOutlierCols = outlierRatios.Count(o => o.Ratio > 0.05);

        // Score based on outlier severity
        // >10% outliers is very concerning, >5% is concerning, >1% is notable
        var avgScore = Math.Min(1.0, avgOutlierRatio * 10);
        var maxScore = Math.Min(1.0, maxOutlierRatio * 5);

        var score = avgScore * 0.4 + maxScore * 0.4 + (double)highOutlierCols / numericCols.Count * 0.2;

        if (highOutlierCols > 0)
        {
            var worstCols = outlierRatios.OrderByDescending(o => o.Ratio).Take(3).ToList();
            result.Issues.Add(new AnomalyIssue
            {
                Category = "Outliers",
                Description = $"{highOutlierCols} columns with >5% outliers. Worst: {string.Join(", ", worstCols.Select(o => $"{o.Column.Name} ({o.Ratio:P1})"))}",
                Severity = maxOutlierRatio > 0.1 ? "High" : "Medium",
                AffectedColumns = worstCols.Select(o => o.Column.Name).ToList()
            });
        }

        return Math.Min(1.0, score);
    }

    private static double ComputeDistributionScore(DataProfile profile, AnomalyScoreResult result)
    {
        var numericCols = profile.Columns.Where(c => c.InferredType == ColumnType.Numeric && c.Skewness.HasValue).ToList();
        if (numericCols.Count == 0) return 0;

        // High skewness indicates potential data issues
        var highSkewCols = numericCols.Where(c => Math.Abs(c.Skewness ?? 0) > 2).ToList();
        var extremeSkewCols = numericCols.Where(c => Math.Abs(c.Skewness ?? 0) > 5).ToList();

        // High kurtosis indicates heavy tails
        var highKurtosisCols = numericCols.Where(c => Math.Abs(c.Kurtosis ?? 0) > 7).ToList();

        var skewScore = (double)highSkewCols.Count / numericCols.Count;
        var extremeSkewScore = (double)extremeSkewCols.Count / numericCols.Count;
        var kurtosisScore = (double)highKurtosisCols.Count / numericCols.Count;

        var score = skewScore * 0.3 + extremeSkewScore * 0.4 + kurtosisScore * 0.3;

        if (highSkewCols.Count > 0)
        {
            result.Issues.Add(new AnomalyIssue
            {
                Category = "Distribution",
                Description = $"{highSkewCols.Count} columns are highly skewed (|skewness| > 2)",
                Severity = extremeSkewCols.Count > 0 ? "Medium" : "Low",
                AffectedColumns = highSkewCols.Select(c => c.Name).ToList()
            });
        }

        return Math.Min(1.0, score);
    }

    private static double ComputeCardinalityScore(DataProfile profile, AnomalyScoreResult result)
    {
        if (profile.Columns.Count == 0) return 0;

        // Constant columns (0 information)
        var constantCols = profile.Columns.Where(c => c.UniqueCount <= 1).ToList();
        
        // Near-unique columns that might be IDs incorrectly used as features
        var nearUniqueCols = profile.Columns
            .Where(c => c.UniquePercent > 95 && c.InferredType != ColumnType.Id && c.SemanticRole != SemanticRole.Identifier)
            .ToList();
        
        // High-cardinality categoricals
        var highCardCat = profile.Columns
            .Where(c => c.InferredType == ColumnType.Categorical && c.UniquePercent > 50)
            .ToList();

        var constantScore = (double)constantCols.Count / profile.Columns.Count;
        var nearUniqueScore = (double)nearUniqueCols.Count / profile.Columns.Count;
        var highCardScore = (double)highCardCat.Count / Math.Max(1, profile.Columns.Count(c => c.InferredType == ColumnType.Categorical));

        var score = constantScore * 0.5 + nearUniqueScore * 0.3 + highCardScore * 0.2;

        if (constantCols.Count > 0)
        {
            result.Issues.Add(new AnomalyIssue
            {
                Category = "Cardinality",
                Description = $"{constantCols.Count} constant columns (provide no information)",
                Severity = "Medium",
                AffectedColumns = constantCols.Select(c => c.Name).ToList()
            });
        }

        if (nearUniqueCols.Count > 0)
        {
            result.Issues.Add(new AnomalyIssue
            {
                Category = "Cardinality",
                Description = $"{nearUniqueCols.Count} near-unique columns (may be IDs)",
                Severity = "Low",
                AffectedColumns = nearUniqueCols.Select(c => c.Name).ToList()
            });
        }

        return Math.Min(1.0, score);
    }

    private static double ComputeSchemaScore(DataProfile profile, AnomalyScoreResult result)
    {
        var score = 0.0;

        // Mixed types within columns (inferred from type detection confidence)
        // This is a proxy - if we have high cardinality text columns they might be mixed
        var suspiciousTextCols = profile.Columns
            .Where(c => c.InferredType == ColumnType.Text && c.UniquePercent < 50)
            .ToList();

        // Very wide datasets
        if (profile.ColumnCount > 100)
        {
            score += 0.2;
            result.Issues.Add(new AnomalyIssue
            {
                Category = "Schema",
                Description = $"Wide dataset ({profile.ColumnCount} columns) - may have redundant features",
                Severity = profile.ColumnCount > 500 ? "Medium" : "Low"
            });
        }

        // Very few rows relative to columns
        if (profile.RowCount > 0 && profile.ColumnCount / (double)profile.RowCount > 0.1)
        {
            score += 0.3;
            result.Issues.Add(new AnomalyIssue
            {
                Category = "Schema",
                Description = $"High column-to-row ratio ({profile.ColumnCount} cols / {profile.RowCount} rows) - potential overfitting risk",
                Severity = "Medium"
            });
        }

        // Mostly ID/text columns (limited numeric analysis)
        var numericRatio = (double)profile.Columns.Count(c => c.InferredType == ColumnType.Numeric) / profile.Columns.Count;
        if (numericRatio < 0.1 && profile.Columns.Count > 5)
        {
            score += 0.1;
        }

        return Math.Min(1.0, score);
    }

    private static List<string> GenerateRecommendations(DataProfile profile, AnomalyScoreResult result)
    {
        var recs = new List<string>();

        // Based on score severity
        if (result.OverallScore >= 0.5)
        {
            recs.Add("Data quality requires immediate attention before modeling");
        }

        // Specific recommendations based on issues
        var nullIssues = result.Issues.Where(i => i.Category == "NullRate" && i.Severity != "Low").ToList();
        if (nullIssues.Count > 0)
        {
            var affectedCols = nullIssues.SelectMany(i => i.AffectedColumns).Distinct().Take(5);
            recs.Add($"Address high null rates in: {string.Join(", ", affectedCols)}");
        }

        var outlierIssues = result.Issues.Where(i => i.Category == "Outliers" && i.Severity == "High").ToList();
        if (outlierIssues.Count > 0)
        {
            recs.Add("Investigate outliers - consider capping, winsorization, or separate modeling");
        }

        var cardinalityIssues = result.Issues.Where(i => i.Category == "Cardinality").ToList();
        if (cardinalityIssues.Any(i => i.Description.Contains("constant")))
        {
            recs.Add("Remove constant columns before modeling");
        }

        var distributionIssues = result.Issues.Where(i => i.Category == "Distribution").ToList();
        if (distributionIssues.Count > 0)
        {
            recs.Add("Consider transformations (log, Box-Cox) for highly skewed columns");
        }

        if (recs.Count == 0)
        {
            recs.Add("Data quality is acceptable for analysis");
        }

        return recs;
    }
}

#region Models

/// <summary>
/// Result of computing anomaly score for a profile
/// </summary>
public class AnomalyScoreResult
{
    public string ProfileSource { get; set; } = "";
    public DateTime ComputedAt { get; set; }
    
    /// <summary>
    /// Overall anomaly score (0-1, higher = more anomalous)
    /// </summary>
    public double OverallScore { get; set; }
    
    /// <summary>
    /// Human-readable interpretation
    /// </summary>
    public string Interpretation { get; set; } = "";
    
    /// <summary>
    /// Breakdown by component
    /// </summary>
    public List<AnomalyComponent> Components { get; set; } = [];
    
    /// <summary>
    /// Specific issues found
    /// </summary>
    public List<AnomalyIssue> Issues { get; set; } = [];
    
    /// <summary>
    /// Actionable recommendations
    /// </summary>
    public List<string> Recommendations { get; set; } = [];
}

/// <summary>
/// Component of the anomaly score
/// </summary>
public class AnomalyComponent
{
    public string Name { get; set; } = "";
    public double Score { get; set; }
    public double Weight { get; set; }
    public double WeightedScore { get; set; }
}

/// <summary>
/// Specific anomaly issue found
/// </summary>
public class AnomalyIssue
{
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public string Severity { get; set; } = "Low";
    public List<string> AffectedColumns { get; set; } = [];
}

#endregion
