using Mostlylucid.DataSummarizer.Models;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Compares two data profiles to detect drift, schema changes, and statistical differences.
/// Useful for monitoring data quality over time or comparing training vs. production data.
/// </summary>
public class ProfileComparator
{
    /// <summary>
    /// Default threshold for considering a change significant
    /// </summary>
    public double SignificanceThreshold { get; set; } = 0.1;

    /// <summary>
    /// Compare two profiles and generate a comprehensive drift report
    /// </summary>
    public ProfileDiffResult Compare(DataProfile baseline, DataProfile current, ProfileDiffOptions? options = null)
    {
        options ??= new ProfileDiffOptions();
        
        var result = new ProfileDiffResult
        {
            BaselineSource = baseline.SourcePath,
            CurrentSource = current.SourcePath,
            BaselineRowCount = baseline.RowCount,
            CurrentRowCount = current.RowCount,
            ComparedAt = DateTime.UtcNow
        };

        // Schema comparison
        result.SchemaChanges = CompareSchemas(baseline, current);
        
        // Row count change
        result.RowCountChange = CalculateRowCountChange(baseline.RowCount, current.RowCount);
        
        // Column-level statistical comparison
        var baselineColumns = baseline.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var currentColumns = current.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        
        foreach (var colName in baselineColumns.Keys.Intersect(currentColumns.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var baseCol = baselineColumns[colName];
            var currCol = currentColumns[colName];
            
            var colDiff = CompareColumns(baseCol, currCol, options);
            if (colDiff.HasSignificantChanges(options.SignificanceThreshold ?? SignificanceThreshold))
            {
                result.ColumnDiffs.Add(colDiff);
            }
        }
        
        // Calculate overall drift score
        result.OverallDriftScore = CalculateOverallDriftScore(result, options);
        result.HasSignificantDrift = result.OverallDriftScore > (options.DriftThreshold ?? 0.2);
        
        // Generate summary
        result.Summary = GenerateSummary(result);
        result.Recommendations = GenerateRecommendations(result);

        return result;
    }

    private SchemaChanges CompareSchemas(DataProfile baseline, DataProfile current)
    {
        var baselineNames = baseline.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentNames = current.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        
        var added = currentNames.Except(baselineNames, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = baselineNames.Except(currentNames, StringComparer.OrdinalIgnoreCase).ToList();
        
        // Type changes
        var typeChanges = new List<TypeChange>();
        var baselineByName = baseline.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var currentByName = current.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        
        foreach (var name in baselineNames.Intersect(currentNames, StringComparer.OrdinalIgnoreCase))
        {
            var baseType = baselineByName[name].InferredType;
            var currType = currentByName[name].InferredType;
            if (baseType != currType)
            {
                typeChanges.Add(new TypeChange
                {
                    ColumnName = name,
                    BaselineType = baseType,
                    CurrentType = currType
                });
            }
        }

        return new SchemaChanges
        {
            AddedColumns = added,
            RemovedColumns = removed,
            TypeChanges = typeChanges,
            HasChanges = added.Count > 0 || removed.Count > 0 || typeChanges.Count > 0
        };
    }

    private RowCountChange CalculateRowCountChange(long baseline, long current)
    {
        var change = current - baseline;
        var percentChange = baseline > 0 ? (double)change / baseline * 100 : (current > 0 ? 100 : 0);
        
        return new RowCountChange
        {
            Baseline = baseline,
            Current = current,
            AbsoluteChange = change,
            PercentChange = percentChange,
            IsSignificant = Math.Abs(percentChange) > 10 // More than 10% change
        };
    }

    private ColumnDiff CompareColumns(ColumnProfile baseline, ColumnProfile current, ProfileDiffOptions options)
    {
        var diff = new ColumnDiff
        {
            ColumnName = baseline.Name,
            ColumnType = baseline.InferredType
        };

        // Null percentage change
        diff.NullPercentChange = new MetricChange
        {
            MetricName = "NullPercent",
            Baseline = baseline.NullPercent,
            Current = current.NullPercent,
            AbsoluteChange = current.NullPercent - baseline.NullPercent,
            PercentChange = baseline.NullPercent > 0 
                ? (current.NullPercent - baseline.NullPercent) / baseline.NullPercent * 100 
                : (current.NullPercent > 0 ? 100 : 0)
        };

        // Unique percent change
        diff.UniquePercentChange = new MetricChange
        {
            MetricName = "UniquePercent",
            Baseline = baseline.UniquePercent,
            Current = current.UniquePercent,
            AbsoluteChange = current.UniquePercent - baseline.UniquePercent,
            PercentChange = baseline.UniquePercent > 0 
                ? (current.UniquePercent - baseline.UniquePercent) / baseline.UniquePercent * 100 
                : (current.UniquePercent > 0 ? 100 : 0)
        };

        // Numeric stats comparison
        if (baseline.InferredType == ColumnType.Numeric && baseline.Mean.HasValue && current.Mean.HasValue)
        {
            diff.NumericChanges = CompareNumericStats(baseline, current);
            
            // Calculate PSI for numeric columns (approximate using mean/stddev shift)
            if (baseline.StdDev.HasValue && current.StdDev.HasValue && baseline.StdDev > 0)
            {
                diff.Psi = CalculateApproximatePsi(baseline, current);
            }
            
            // Also compute KS distance (quantile-based approximation)
            var ksDistance = DistanceMetrics.ApproximateKolmogorovSmirnov(baseline, current);
            diff.KsDistance = ksDistance;
        }

        // Categorical distribution comparison
        if (baseline.InferredType == ColumnType.Categorical && 
            baseline.TopValues?.Count > 0 && current.TopValues?.Count > 0)
        {
            diff.CategoricalChanges = CompareCategoricalDistribution(baseline.TopValues, current.TopValues);
            diff.Psi = CalculateCategoricalPsi(baseline.TopValues, current.TopValues);
            
            // Also compute JS divergence
            var pDist = baseline.TopValues.ToDictionary(v => v.Value, v => v.Percent / 100.0);
            var qDist = current.TopValues.ToDictionary(v => v.Value, v => v.Percent / 100.0);
            diff.JsDivergence = DistanceMetrics.JensenShannonDivergence(pDist, qDist);
        }

        // Date range changes
        if (baseline.InferredType == ColumnType.DateTime)
        {
            diff.DateRangeChanges = CompareDateRanges(baseline, current);
        }

        return diff;
    }

    private NumericChanges CompareNumericStats(ColumnProfile baseline, ColumnProfile current)
    {
        var changes = new NumericChanges();
        
        if (baseline.Mean.HasValue && current.Mean.HasValue)
        {
            changes.MeanChange = CreateMetricChange("Mean", baseline.Mean.Value, current.Mean.Value);
        }
        
        if (baseline.Median.HasValue && current.Median.HasValue)
        {
            changes.MedianChange = CreateMetricChange("Median", baseline.Median.Value, current.Median.Value);
        }
        
        if (baseline.StdDev.HasValue && current.StdDev.HasValue)
        {
            changes.StdDevChange = CreateMetricChange("StdDev", baseline.StdDev.Value, current.StdDev.Value);
        }
        
        if (baseline.Min.HasValue && current.Min.HasValue)
        {
            changes.MinChange = CreateMetricChange("Min", baseline.Min.Value, current.Min.Value);
        }
        
        if (baseline.Max.HasValue && current.Max.HasValue)
        {
            changes.MaxChange = CreateMetricChange("Max", baseline.Max.Value, current.Max.Value);
        }
        
        if (baseline.Skewness.HasValue && current.Skewness.HasValue)
        {
            changes.SkewnessChange = CreateMetricChange("Skewness", baseline.Skewness.Value, current.Skewness.Value);
        }

        return changes;
    }

    private MetricChange CreateMetricChange(string name, double baseline, double current)
    {
        return new MetricChange
        {
            MetricName = name,
            Baseline = baseline,
            Current = current,
            AbsoluteChange = current - baseline,
            PercentChange = baseline != 0 ? (current - baseline) / Math.Abs(baseline) * 100 : (current != 0 ? 100 : 0)
        };
    }

    /// <summary>
    /// Approximate PSI using normalized mean shift
    /// True PSI requires binned distributions; this is a practical approximation
    /// </summary>
    private double CalculateApproximatePsi(ColumnProfile baseline, ColumnProfile current)
    {
        if (!baseline.Mean.HasValue || !current.Mean.HasValue ||
            !baseline.StdDev.HasValue || !current.StdDev.HasValue ||
            baseline.StdDev.Value <= 0)
        {
            return 0;
        }

        // Standardized mean shift (Cohen's d style)
        var pooledStd = Math.Sqrt((baseline.StdDev.Value * baseline.StdDev.Value + 
                                   current.StdDev.Value * current.StdDev.Value) / 2);
        
        if (pooledStd <= 0) return 0;
        
        var meanShift = Math.Abs(current.Mean.Value - baseline.Mean.Value) / pooledStd;
        
        // Variance ratio (F-ratio style)
        var varianceRatio = Math.Max(current.StdDev.Value, baseline.StdDev.Value) / 
                           Math.Max(0.001, Math.Min(current.StdDev.Value, baseline.StdDev.Value));
        
        // Combine into PSI-like score (0-1 scale, >0.2 is significant)
        var psi = (meanShift * 0.1) + (Math.Log(varianceRatio) * 0.1);
        return Math.Min(1.0, Math.Max(0, psi));
    }

    /// <summary>
    /// Calculate PSI for categorical distributions
    /// PSI = Σ (actual% - expected%) * ln(actual% / expected%)
    /// </summary>
    private double CalculateCategoricalPsi(List<ValueCount> baseline, List<ValueCount> current)
    {
        var baselineDict = baseline.ToDictionary(v => v.Value, v => v.Percent / 100.0);
        var currentDict = current.ToDictionary(v => v.Value, v => v.Percent / 100.0);
        
        var allKeys = baselineDict.Keys.Union(currentDict.Keys).ToList();
        
        double psi = 0;
        const double epsilon = 0.0001; // Avoid log(0)
        
        foreach (var key in allKeys)
        {
            var baseProb = baselineDict.GetValueOrDefault(key, epsilon);
            var currProb = currentDict.GetValueOrDefault(key, epsilon);
            
            // Ensure neither is zero
            baseProb = Math.Max(baseProb, epsilon);
            currProb = Math.Max(currProb, epsilon);
            
            psi += (currProb - baseProb) * Math.Log(currProb / baseProb);
        }
        
        return Math.Abs(psi);
    }

    private CategoricalChanges CompareCategoricalDistribution(List<ValueCount> baseline, List<ValueCount> current)
    {
        var changes = new CategoricalChanges();
        
        var baselineDict = baseline.ToDictionary(v => v.Value, v => v);
        var currentDict = current.ToDictionary(v => v.Value, v => v);
        
        var allKeys = baselineDict.Keys.Union(currentDict.Keys).ToList();
        
        foreach (var key in allKeys)
        {
            var basePercent = baselineDict.TryGetValue(key, out var bv) ? bv.Percent : 0;
            var currPercent = currentDict.TryGetValue(key, out var cv) ? cv.Percent : 0;
            var change = currPercent - basePercent;
            
            if (Math.Abs(change) >= 5) // At least 5 percentage points change
            {
                changes.ValueChanges.Add(new CategoryValueChange
                {
                    Value = key,
                    BaselinePercent = basePercent,
                    CurrentPercent = currPercent,
                    Change = change,
                    IsNew = !baselineDict.ContainsKey(key),
                    IsRemoved = !currentDict.ContainsKey(key)
                });
            }
        }
        
        // Sort by absolute change magnitude
        changes.ValueChanges = changes.ValueChanges.OrderByDescending(c => Math.Abs(c.Change)).ToList();
        
        return changes;
    }

    private DateRangeChanges? CompareDateRanges(ColumnProfile baseline, ColumnProfile current)
    {
        if (!baseline.MinDate.HasValue || !baseline.MaxDate.HasValue ||
            !current.MinDate.HasValue || !current.MaxDate.HasValue)
        {
            return null;
        }

        return new DateRangeChanges
        {
            BaselineMinDate = baseline.MinDate.Value,
            BaselineMaxDate = baseline.MaxDate.Value,
            CurrentMinDate = current.MinDate.Value,
            CurrentMaxDate = current.MaxDate.Value,
            MinDateShiftDays = (int)(current.MinDate.Value - baseline.MinDate.Value).TotalDays,
            MaxDateShiftDays = (int)(current.MaxDate.Value - baseline.MaxDate.Value).TotalDays,
            BaselineSpanDays = baseline.DateSpanDays ?? 0,
            CurrentSpanDays = current.DateSpanDays ?? 0
        };
    }

    private double CalculateOverallDriftScore(ProfileDiffResult result, ProfileDiffOptions options)
    {
        var scores = new List<double>();
        
        // Schema changes weight heavily
        if (result.SchemaChanges.HasChanges)
        {
            scores.Add(0.5 * (result.SchemaChanges.RemovedColumns.Count > 0 ? 1.0 : 0.3));
        }
        
        // Row count change
        if (result.RowCountChange.IsSignificant)
        {
            scores.Add(Math.Min(1.0, Math.Abs(result.RowCountChange.PercentChange) / 100.0) * 0.3);
        }
        
        // Column-level PSI scores
        foreach (var colDiff in result.ColumnDiffs)
        {
            if (colDiff.Psi.HasValue)
            {
                scores.Add(colDiff.Psi.Value);
            }
        }
        
        if (scores.Count == 0) return 0;
        
        // Return weighted average, capped at 1.0
        return Math.Min(1.0, scores.Average() + (result.SchemaChanges.RemovedColumns.Count * 0.1));
    }

    private string GenerateSummary(ProfileDiffResult result)
    {
        var parts = new List<string>();
        
        // Row count
        if (result.RowCountChange.AbsoluteChange != 0)
        {
            var direction = result.RowCountChange.AbsoluteChange > 0 ? "increased" : "decreased";
            parts.Add($"Row count {direction} by {Math.Abs(result.RowCountChange.PercentChange):F1}% ({result.BaselineRowCount:N0} → {result.CurrentRowCount:N0})");
        }
        else
        {
            parts.Add($"Row count unchanged at {result.CurrentRowCount:N0}");
        }
        
        // Schema changes
        if (result.SchemaChanges.HasChanges)
        {
            if (result.SchemaChanges.AddedColumns.Count > 0)
                parts.Add($"{result.SchemaChanges.AddedColumns.Count} column(s) added");
            if (result.SchemaChanges.RemovedColumns.Count > 0)
                parts.Add($"{result.SchemaChanges.RemovedColumns.Count} column(s) removed");
            if (result.SchemaChanges.TypeChanges.Count > 0)
                parts.Add($"{result.SchemaChanges.TypeChanges.Count} column type(s) changed");
        }
        
        // Statistical drift
        var highPsiCols = result.ColumnDiffs.Where(c => c.Psi >= 0.2).ToList();
        if (highPsiCols.Count > 0)
        {
            parts.Add($"{highPsiCols.Count} column(s) with significant distribution drift");
        }
        
        return string.Join(". ", parts) + ".";
    }

    private List<string> GenerateRecommendations(ProfileDiffResult result)
    {
        var recs = new List<string>();
        
        if (result.SchemaChanges.RemovedColumns.Count > 0)
        {
            recs.Add($"CRITICAL: {result.SchemaChanges.RemovedColumns.Count} column(s) removed - verify downstream dependencies");
        }
        
        if (result.SchemaChanges.TypeChanges.Count > 0)
        {
            recs.Add($"WARNING: Column type changes detected - review data pipeline transformations");
        }
        
        if (Math.Abs(result.RowCountChange.PercentChange) > 50)
        {
            recs.Add($"Large row count change ({result.RowCountChange.PercentChange:F1}%) - investigate data source");
        }
        
        var driftedCols = result.ColumnDiffs.Where(c => c.Psi >= 0.25).ToList();
        foreach (var col in driftedCols.Take(5))
        {
            recs.Add($"Column '{col.ColumnName}' has high drift (PSI={col.Psi:F3}) - investigate distribution change");
        }
        
        var nullIncreaseCols = result.ColumnDiffs
            .Where(c => c.NullPercentChange?.AbsoluteChange > 10)
            .ToList();
        foreach (var col in nullIncreaseCols.Take(3))
        {
            recs.Add($"Column '{col.ColumnName}' null rate increased by {col.NullPercentChange!.AbsoluteChange:F1}pp - check data quality");
        }
        
        if (recs.Count == 0)
        {
            recs.Add("No significant drift detected - data appears stable");
        }
        
        return recs;
    }
}

#region Models

/// <summary>
/// Options for profile comparison
/// </summary>
public class ProfileDiffOptions
{
    /// <summary>
    /// Threshold for considering a metric change significant (0-1)
    /// </summary>
    public double? SignificanceThreshold { get; set; }
    
    /// <summary>
    /// Overall drift score threshold for flagging significant drift
    /// </summary>
    public double? DriftThreshold { get; set; }
    
    /// <summary>
    /// Include detailed numeric statistics comparison
    /// </summary>
    public bool IncludeDetailedStats { get; set; } = true;
    
    /// <summary>
    /// Columns to exclude from comparison
    /// </summary>
    public List<string>? ExcludeColumns { get; set; }
}

/// <summary>
/// Complete result of comparing two profiles
/// </summary>
public class ProfileDiffResult
{
    public string BaselineSource { get; set; } = "";
    public string CurrentSource { get; set; } = "";
    public long BaselineRowCount { get; set; }
    public long CurrentRowCount { get; set; }
    public DateTime ComparedAt { get; set; }
    
    /// <summary>
    /// Overall drift score (0-1, higher = more drift)
    /// </summary>
    public double OverallDriftScore { get; set; }
    
    /// <summary>
    /// Whether drift exceeds threshold
    /// </summary>
    public bool HasSignificantDrift { get; set; }
    
    public SchemaChanges SchemaChanges { get; set; } = new();
    public RowCountChange RowCountChange { get; set; } = new();
    public List<ColumnDiff> ColumnDiffs { get; set; } = [];
    
    public string Summary { get; set; } = "";
    public List<string> Recommendations { get; set; } = [];
}

/// <summary>
/// Schema-level changes between profiles
/// </summary>
public class SchemaChanges
{
    public List<string> AddedColumns { get; set; } = [];
    public List<string> RemovedColumns { get; set; } = [];
    public List<TypeChange> TypeChanges { get; set; } = [];
    public bool HasChanges { get; set; }
}

/// <summary>
/// Column type change
/// </summary>
public class TypeChange
{
    public string ColumnName { get; set; } = "";
    public ColumnType BaselineType { get; set; }
    public ColumnType CurrentType { get; set; }
}

/// <summary>
/// Row count comparison
/// </summary>
public class RowCountChange
{
    public long Baseline { get; set; }
    public long Current { get; set; }
    public long AbsoluteChange { get; set; }
    public double PercentChange { get; set; }
    public bool IsSignificant { get; set; }
}

/// <summary>
/// Differences for a single column
/// </summary>
public class ColumnDiff
{
    public string ColumnName { get; set; } = "";
    public ColumnType ColumnType { get; set; }
    
    /// <summary>
    /// Population Stability Index (0-1, >0.1 moderate, >0.25 significant drift)
    /// </summary>
    public double? Psi { get; set; }
    
    /// <summary>
    /// Kolmogorov-Smirnov distance (quantile-based approximation, 0-1)
    /// For numeric columns, measures distributional shift
    /// </summary>
    public double? KsDistance { get; set; }
    
    /// <summary>
    /// Jensen-Shannon divergence (0-1, symmetric measure for categorical distributions)
    /// </summary>
    public double? JsDivergence { get; set; }
    
    public MetricChange? NullPercentChange { get; set; }
    public MetricChange? UniquePercentChange { get; set; }
    public NumericChanges? NumericChanges { get; set; }
    public CategoricalChanges? CategoricalChanges { get; set; }
    public DateRangeChanges? DateRangeChanges { get; set; }
    
    /// <summary>
    /// Check if column has any changes above threshold
    /// </summary>
    public bool HasSignificantChanges(double threshold)
    {
        if (Psi >= threshold) return true;
        if (Math.Abs(NullPercentChange?.AbsoluteChange ?? 0) > threshold * 100) return true;
        if (NumericChanges?.HasSignificantChanges(threshold) == true) return true;
        if (CategoricalChanges?.ValueChanges.Count > 0) return true;
        return false;
    }
}

/// <summary>
/// Change in a single metric
/// </summary>
public class MetricChange
{
    public string MetricName { get; set; } = "";
    public double Baseline { get; set; }
    public double Current { get; set; }
    public double AbsoluteChange { get; set; }
    public double PercentChange { get; set; }
}

/// <summary>
/// Changes in numeric statistics
/// </summary>
public class NumericChanges
{
    public MetricChange? MeanChange { get; set; }
    public MetricChange? MedianChange { get; set; }
    public MetricChange? StdDevChange { get; set; }
    public MetricChange? MinChange { get; set; }
    public MetricChange? MaxChange { get; set; }
    public MetricChange? SkewnessChange { get; set; }
    
    public bool HasSignificantChanges(double threshold)
    {
        var percentThreshold = threshold * 100;
        return Math.Abs(MeanChange?.PercentChange ?? 0) > percentThreshold ||
               Math.Abs(StdDevChange?.PercentChange ?? 0) > percentThreshold * 2 ||
               Math.Abs(SkewnessChange?.AbsoluteChange ?? 0) > 0.5;
    }
}

/// <summary>
/// Changes in categorical distribution
/// </summary>
public class CategoricalChanges
{
    public List<CategoryValueChange> ValueChanges { get; set; } = [];
}

/// <summary>
/// Change in a single category value
/// </summary>
public class CategoryValueChange
{
    public string Value { get; set; } = "";
    public double BaselinePercent { get; set; }
    public double CurrentPercent { get; set; }
    public double Change { get; set; }
    public bool IsNew { get; set; }
    public bool IsRemoved { get; set; }
}

/// <summary>
/// Changes in date ranges
/// </summary>
public class DateRangeChanges
{
    public DateTime BaselineMinDate { get; set; }
    public DateTime BaselineMaxDate { get; set; }
    public DateTime CurrentMinDate { get; set; }
    public DateTime CurrentMaxDate { get; set; }
    public int MinDateShiftDays { get; set; }
    public int MaxDateShiftDays { get; set; }
    public int BaselineSpanDays { get; set; }
    public int CurrentSpanDays { get; set; }
}

#endregion

#region Advanced Distance Metrics

public static class DistanceMetrics
{
    /// <summary>
    /// Approximate KS statistic using quantiles (cheap Wasserstein-ish)
    /// This is much faster than full KS test and works with profile data (no raw samples needed)
    /// </summary>
    public static double ApproximateKolmogorovSmirnov(ColumnProfile baseline, ColumnProfile current)
    {
        if (!baseline.Q25.HasValue || !baseline.Median.HasValue || !baseline.Q75.HasValue ||
            !current.Q25.HasValue || !current.Median.HasValue || !current.Q75.HasValue)
        {
            return 0;
        }

        // Compare CDF at quartile points (cheap approximation)
        var q25Diff = Math.Abs(current.Q25.Value - baseline.Q25.Value);
        var medDiff = Math.Abs(current.Median.Value - baseline.Median.Value);
        var q75Diff = Math.Abs(current.Q75.Value - baseline.Q75.Value);

        // Normalize by IQR to make scale-independent
        var baselineIQR = baseline.Q75.Value - baseline.Q25.Value;
        var currentIQR = current.Q75.Value - current.Q25.Value;
        var avgIQR = (baselineIQR + currentIQR) / 2.0;

        if (avgIQR <= 0) return 0;

        // Max difference across quantiles (KS-like)
        var maxDiff = Math.Max(Math.Max(q25Diff, medDiff), q75Diff) / avgIQR;

        // Also check IQR shift itself
        var iqrShift = Math.Abs(currentIQR - baselineIQR) / Math.Max(0.001, baselineIQR);

        return Math.Min(1.0, (maxDiff + iqrShift) / 2.0);
    }

    /// <summary>
    /// Jensen-Shannon divergence for categorical distributions
    /// Symmetric, bounded [0,1], and handles missing categories gracefully
    /// </summary>
    public static double JensenShannonDivergence(Dictionary<string, double> p, Dictionary<string, double> q)
    {
        if (p.Count == 0 && q.Count == 0) return 0;

        var allKeys = p.Keys.Union(q.Keys).ToHashSet();
        const double epsilon = 1e-10; // Avoid log(0)

        // Normalize to ensure they sum to 1
        var pSum = p.Values.Sum();
        var qSum = q.Values.Sum();
        if (pSum <= 0 || qSum <= 0) return 0;

        var pNorm = p.ToDictionary(kv => kv.Key, kv => kv.Value / pSum);
        var qNorm = q.ToDictionary(kv => kv.Key, kv => kv.Value / qSum);

        // Compute M = (P + Q) / 2
        var m = new Dictionary<string, double>();
        foreach (var key in allKeys)
        {
            var pVal = pNorm.GetValueOrDefault(key, epsilon);
            var qVal = qNorm.GetValueOrDefault(key, epsilon);
            m[key] = (pVal + qVal) / 2.0;
        }

        // JS = (KL(P||M) + KL(Q||M)) / 2
        double klPM = 0;
        double klQM = 0;

        foreach (var key in allKeys)
        {
            var pVal = pNorm.GetValueOrDefault(key, epsilon);
            var qVal = qNorm.GetValueOrDefault(key, epsilon);
            var mVal = m[key];

            klPM += pVal * Math.Log(pVal / mVal);
            klQM += qVal * Math.Log(qVal / mVal);
        }

        var js = (klPM + klQM) / 2.0;

        // Normalize to [0,1] (JS divergence max is ln(2))
        return Math.Min(1.0, js / Math.Log(2));
    }

    /// <summary>
    /// Compute weighted drift score across all columns
    /// Uses KS for numeric, JS for categorical, simple deltas for others
    /// </summary>
    public static double ComputeWeightedDrift(
        Dictionary<string, ColumnProfile> baseline,
        Dictionary<string, ColumnProfile> current,
        HashSet<string>? excludeColumns = null)
    {
        excludeColumns ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var scores = new List<(double distance, double weight)>();

        foreach (var (colName, baseCol) in baseline)
        {
            if (excludeColumns.Contains(colName)) continue;
            if (!current.TryGetValue(colName, out var currCol)) continue;

            double distance = 0;
            double weight = 1.0;

            // Higher weight for non-ID columns
            if (baseCol.InferredType == ColumnType.Id)
            {
                weight = 0.1;
            }

            switch (baseCol.InferredType)
            {
                case ColumnType.Numeric:
                    distance = ApproximateKolmogorovSmirnov(baseCol, currCol);
                    break;

                case ColumnType.Categorical:
                    if (baseCol.TopValues?.Count > 0 && currCol.TopValues?.Count > 0)
                    {
                        var pDist = baseCol.TopValues.ToDictionary(v => v.Value, v => v.Percent / 100.0);
                        var qDist = currCol.TopValues.ToDictionary(v => v.Value, v => v.Percent / 100.0);
                        distance = JensenShannonDivergence(pDist, qDist);
                    }
                    break;

                default:
                    // For other types, use null% and unique% deltas
                    var nullDelta = Math.Abs(currCol.NullPercent - baseCol.NullPercent) / 100.0;
                    var uniqueDelta = Math.Abs(currCol.UniquePercent - baseCol.UniquePercent) / 100.0;
                    distance = (nullDelta + uniqueDelta) / 2.0;
                    break;
            }

            scores.Add((distance, weight));
        }

        if (scores.Count == 0) return 0;

        var weightedSum = scores.Sum(s => s.distance * s.weight);
        var totalWeight = scores.Sum(s => s.weight);

        return totalWeight > 0 ? weightedSum / totalWeight : 0;
    }
}

#endregion
