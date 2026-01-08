using Mostlylucid.DataSummarizer.Models;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Profiles data segments and computes centroids for similarity/drift analysis.
/// Enables cohort analysis, A/B comparison, and time-window drift detection.
/// </summary>
public class SegmentProfiler
{
    private readonly bool _verbose;

    public SegmentProfiler(bool verbose = false)
    {
        _verbose = verbose;
    }

    /// <summary>
    /// Compute centroid (statistical center) from a profile.
    /// Centroid = normalized vector of [numeric means, categorical mode indices, null rates]
    /// </summary>
    public ProfileCentroid ComputeCentroid(DataProfile profile, string? segmentName = null)
    {
        var centroid = new ProfileCentroid
        {
            SourcePath = profile.SourcePath,
            SegmentName = segmentName,
            RowCount = profile.RowCount,
            ColumnCount = profile.ColumnCount,
            ComputedAt = DateTime.UtcNow
        };

        foreach (var col in profile.Columns)
        {
            var colCentroid = new ColumnCentroid
            {
                ColumnName = col.Name,
                ColumnType = col.InferredType,
                NullRate = col.NullPercent / 100.0,
                UniqueRate = col.UniquePercent / 100.0
            };

            switch (col.InferredType)
            {
                case ColumnType.Numeric:
                    colCentroid.NumericCenter = col.Mean;
                    colCentroid.NumericSpread = col.StdDev;
                    colCentroid.NumericMin = col.Min;
                    colCentroid.NumericMax = col.Max;
                    colCentroid.NumericMedian = col.Median;
                    colCentroid.Skewness = col.Skewness;
                    // Normalized value for distance calculations (z-score of mean relative to range)
                    if (col.Min.HasValue && col.Max.HasValue && col.Max != col.Min)
                    {
                        colCentroid.NormalizedCenter = (col.Mean - col.Min) / (col.Max - col.Min);
                    }
                    break;

                case ColumnType.Categorical:
                    if (col.TopValues?.Count > 0)
                    {
                        colCentroid.CategoricalMode = col.TopValues[0].Value;
                        colCentroid.CategoricalModeFrequency = col.TopValues[0].Percent / 100.0;
                        colCentroid.CategoricalDistribution = col.TopValues
                            .ToDictionary(v => v.Value, v => v.Percent / 100.0);
                    }
                    colCentroid.Cardinality = (int)col.UniqueCount;
                    colCentroid.Entropy = col.Entropy;
                    break;

                case ColumnType.DateTime:
                    if (col.MinDate.HasValue && col.MaxDate.HasValue)
                    {
                        colCentroid.DateRangeStart = col.MinDate;
                        colCentroid.DateRangeEnd = col.MaxDate;
                        colCentroid.DateSpanDays = col.DateSpanDays;
                        // Midpoint as center
                        var midTicks = (col.MinDate.Value.Ticks + col.MaxDate.Value.Ticks) / 2;
                        colCentroid.DateCenter = new DateTime(midTicks);
                    }
                    break;

                case ColumnType.Text:
                    colCentroid.TextAvgLength = col.AvgLength;
                    colCentroid.TextMaxLength = col.MaxLength;
                    break;
            }

            centroid.Columns.Add(colCentroid);
        }

        // Compute overall centroid vector (for distance calculations)
        centroid.Vector = ComputeCentroidVector(centroid);

        return centroid;
    }

    /// <summary>
    /// Compute distance between two centroids (0 = identical, 1 = maximally different)
    /// </summary>
    public double ComputeDistance(ProfileCentroid a, ProfileCentroid b)
    {
        // Match columns by name
        var aByName = a.Columns.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
        var bByName = b.Columns.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
        
        var commonColumns = aByName.Keys.Intersect(bByName.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        
        if (commonColumns.Count == 0)
            return 1.0; // No common columns = maximally different

        var distances = new List<double>();

        foreach (var colName in commonColumns)
        {
            var colA = aByName[colName];
            var colB = bByName[colName];
            
            var colDistance = ComputeColumnDistance(colA, colB);
            distances.Add(colDistance);
        }

        // Penalize for missing columns
        var missingPenalty = 1.0 - (double)commonColumns.Count / Math.Max(a.Columns.Count, b.Columns.Count);
        
        var avgDistance = distances.Average();
        return Math.Min(1.0, avgDistance + missingPenalty * 0.2);
    }

    /// <summary>
    /// Compute distance between two column centroids
    /// </summary>
    private double ComputeColumnDistance(ColumnCentroid a, ColumnCentroid b)
    {
        var components = new List<double>();

        // Null rate difference
        components.Add(Math.Abs(a.NullRate - b.NullRate));

        // Type-specific distance
        if (a.ColumnType == ColumnType.Numeric && b.ColumnType == ColumnType.Numeric)
        {
            // Normalized center difference
            if (a.NormalizedCenter.HasValue && b.NormalizedCenter.HasValue)
            {
                components.Add(Math.Abs(a.NormalizedCenter.Value - b.NormalizedCenter.Value));
            }
            
            // Spread ratio (coefficient of variation comparison)
            if (a.NumericCenter.HasValue && b.NumericCenter.HasValue && 
                a.NumericSpread.HasValue && b.NumericSpread.HasValue &&
                a.NumericCenter != 0 && b.NumericCenter != 0)
            {
                var cvA = Math.Abs(a.NumericSpread.Value / a.NumericCenter.Value);
                var cvB = Math.Abs(b.NumericSpread.Value / b.NumericCenter.Value);
                var cvDiff = Math.Abs(cvA - cvB) / Math.Max(cvA + cvB, 0.001);
                components.Add(Math.Min(1.0, cvDiff));
            }

            // Skewness difference (distribution shape)
            if (a.Skewness.HasValue && b.Skewness.HasValue)
            {
                var skewDiff = Math.Abs(a.Skewness.Value - b.Skewness.Value) / 4.0; // Normalize by typical range
                components.Add(Math.Min(1.0, skewDiff));
            }
        }
        else if (a.ColumnType == ColumnType.Categorical && b.ColumnType == ColumnType.Categorical)
        {
            // Mode frequency difference
            components.Add(Math.Abs((a.CategoricalModeFrequency ?? 0) - (b.CategoricalModeFrequency ?? 0)));
            
            // Distribution divergence (simplified Jensen-Shannon)
            if (a.CategoricalDistribution != null && b.CategoricalDistribution != null)
            {
                var jsd = ComputeJensenShannonDivergence(a.CategoricalDistribution, b.CategoricalDistribution);
                components.Add(jsd);
            }
            
            // Cardinality ratio
            if (a.Cardinality > 0 && b.Cardinality > 0)
            {
                var cardRatio = Math.Min(a.Cardinality, b.Cardinality) / (double)Math.Max(a.Cardinality, b.Cardinality);
                components.Add(1.0 - cardRatio);
            }
        }
        else if (a.ColumnType == ColumnType.DateTime && b.ColumnType == ColumnType.DateTime)
        {
            // Date span overlap
            if (a.DateRangeStart.HasValue && a.DateRangeEnd.HasValue &&
                b.DateRangeStart.HasValue && b.DateRangeEnd.HasValue)
            {
                var overlap = ComputeDateOverlap(
                    a.DateRangeStart.Value, a.DateRangeEnd.Value,
                    b.DateRangeStart.Value, b.DateRangeEnd.Value);
                components.Add(1.0 - overlap);
            }
        }

        // Unique rate difference
        components.Add(Math.Abs(a.UniqueRate - b.UniqueRate) * 0.5);

        return components.Count > 0 ? components.Average() : 0.5;
    }

    /// <summary>
    /// Compute Jensen-Shannon divergence between two categorical distributions
    /// </summary>
    private double ComputeJensenShannonDivergence(
        Dictionary<string, double> p, 
        Dictionary<string, double> q)
    {
        var allKeys = p.Keys.Union(q.Keys).ToList();
        const double epsilon = 0.0001;

        // Create probability vectors
        var pVec = allKeys.Select(k => p.GetValueOrDefault(k, epsilon)).ToArray();
        var qVec = allKeys.Select(k => q.GetValueOrDefault(k, epsilon)).ToArray();

        // Normalize
        var pSum = pVec.Sum();
        var qSum = qVec.Sum();
        for (int i = 0; i < pVec.Length; i++)
        {
            pVec[i] /= pSum;
            qVec[i] /= qSum;
        }

        // M = (P + Q) / 2
        var m = pVec.Zip(qVec, (a, b) => (a + b) / 2).ToArray();

        // JSD = (KL(P||M) + KL(Q||M)) / 2
        double klPM = 0, klQM = 0;
        for (int i = 0; i < m.Length; i++)
        {
            if (pVec[i] > 0 && m[i] > 0)
                klPM += pVec[i] * Math.Log(pVec[i] / m[i]);
            if (qVec[i] > 0 && m[i] > 0)
                klQM += qVec[i] * Math.Log(qVec[i] / m[i]);
        }

        var jsd = (klPM + klQM) / 2;
        // Normalize to 0-1 range (JSD is bounded by log(2) ≈ 0.693)
        return Math.Min(1.0, jsd / Math.Log(2));
    }

    /// <summary>
    /// Compute overlap ratio between two date ranges
    /// </summary>
    private double ComputeDateOverlap(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd)
    {
        var overlapStart = aStart > bStart ? aStart : bStart;
        var overlapEnd = aEnd < bEnd ? aEnd : bEnd;

        if (overlapStart >= overlapEnd)
            return 0; // No overlap

        var overlapDays = (overlapEnd - overlapStart).TotalDays;
        var totalDays = Math.Max((aEnd - aStart).TotalDays, (bEnd - bStart).TotalDays);

        return totalDays > 0 ? overlapDays / totalDays : 0;
    }

    /// <summary>
    /// Compute a flat vector representation of the centroid for ML/clustering
    /// </summary>
    private double[] ComputeCentroidVector(ProfileCentroid centroid)
    {
        var vector = new List<double>();

        // Global features
        vector.Add(Math.Log10(centroid.RowCount + 1)); // Log-scaled row count
        vector.Add(centroid.ColumnCount / 100.0); // Normalized column count

        // Per-column features (sorted by name for consistency)
        foreach (var col in centroid.Columns.OrderBy(c => c.ColumnName))
        {
            vector.Add(col.NullRate);
            vector.Add(col.UniqueRate);
            
            // Type indicator (one-hot style)
            vector.Add(col.ColumnType == ColumnType.Numeric ? 1.0 : 0.0);
            vector.Add(col.ColumnType == ColumnType.Categorical ? 1.0 : 0.0);
            vector.Add(col.ColumnType == ColumnType.DateTime ? 1.0 : 0.0);
            
            // Numeric features
            vector.Add(col.NormalizedCenter ?? 0.5);
            vector.Add(Math.Min(1.0, (col.Skewness ?? 0) / 3.0 + 0.5)); // Normalized skewness
            
            // Categorical features
            vector.Add(col.CategoricalModeFrequency ?? 0);
            vector.Add(Math.Min(1.0, (col.Entropy ?? 0) / 5.0)); // Normalized entropy
        }

        return vector.ToArray();
    }

    /// <summary>
    /// Find the most similar stored profile to a given centroid
    /// </summary>
    public (StoredProfileInfo? Profile, double Similarity) FindMostSimilar(
        ProfileCentroid centroid, 
        ProfileStore store,
        double minSimilarity = 0.5)
    {
        var allProfiles = store.ListAll(100);
        StoredProfileInfo? bestMatch = null;
        double bestSimilarity = 0;

        foreach (var info in allProfiles)
        {
            var storedProfile = store.LoadProfile(info.Id);
            if (storedProfile == null) continue;

            var storedCentroid = ComputeCentroid(storedProfile);
            var distance = ComputeDistance(centroid, storedCentroid);
            var similarity = 1.0 - distance;

            if (similarity > bestSimilarity && similarity >= minSimilarity)
            {
                bestSimilarity = similarity;
                bestMatch = info;
            }
        }

        return (bestMatch, bestSimilarity);
    }

    /// <summary>
    /// Compare two profiles and return detailed segment comparison
    /// </summary>
    public SegmentComparison CompareSegments(
        DataProfile segmentA, 
        DataProfile segmentB,
        string? nameA = null,
        string? nameB = null)
    {
        var centroidA = ComputeCentroid(segmentA, nameA ?? "Segment A");
        var centroidB = ComputeCentroid(segmentB, nameB ?? "Segment B");
        var distance = ComputeDistance(centroidA, centroidB);

        var comparison = new SegmentComparison
        {
            SegmentAName = centroidA.SegmentName ?? "A",
            SegmentBName = centroidB.SegmentName ?? "B",
            SegmentARowCount = segmentA.RowCount,
            SegmentBRowCount = segmentB.RowCount,
            OverallDistance = distance,
            Similarity = 1.0 - distance,
            ComparedAt = DateTime.UtcNow
        };

        // Per-column comparison
        var aByName = centroidA.Columns.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
        var bByName = centroidB.Columns.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
        var commonColumns = aByName.Keys.Intersect(bByName.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (var colName in commonColumns)
        {
            var colA = aByName[colName];
            var colB = bByName[colName];
            var colDistance = ComputeColumnDistance(colA, colB);

            comparison.ColumnComparisons.Add(new ColumnComparison
            {
                ColumnName = colName,
                ColumnType = colA.ColumnType,
                Distance = colDistance,
                
                // Numeric comparisons
                MeanA = colA.NumericCenter,
                MeanB = colB.NumericCenter,
                MeanDelta = colA.NumericCenter.HasValue && colB.NumericCenter.HasValue 
                    ? colB.NumericCenter - colA.NumericCenter : null,
                
                // Categorical comparisons
                ModeA = colA.CategoricalMode,
                ModeB = colB.CategoricalMode,
                ModeFrequencyA = colA.CategoricalModeFrequency,
                ModeFrequencyB = colB.CategoricalModeFrequency,
                
                // Null rate comparison
                NullRateA = colA.NullRate,
                NullRateB = colB.NullRate,
                NullRateDelta = colB.NullRate - colA.NullRate
            });
        }

        // Sort by distance (most different first)
        comparison.ColumnComparisons = comparison.ColumnComparisons
            .OrderByDescending(c => c.Distance)
            .ToList();

        // Generate insights
        comparison.Insights = GenerateComparisonInsights(comparison);

        return comparison;
    }

    private List<string> GenerateComparisonInsights(SegmentComparison comparison)
    {
        var insights = new List<string>();

        // Size difference
        var sizeDiff = (double)(comparison.SegmentBRowCount - comparison.SegmentARowCount) / comparison.SegmentARowCount * 100;
        if (Math.Abs(sizeDiff) > 20)
        {
            insights.Add($"Segment sizes differ by {sizeDiff:+0.0;-0.0}% ({comparison.SegmentARowCount:N0} vs {comparison.SegmentBRowCount:N0} rows)");
        }

        // Most different columns
        var topDiffs = comparison.ColumnComparisons.Take(3).Where(c => c.Distance > 0.3).ToList();
        foreach (var col in topDiffs)
        {
            if (col.ColumnType == ColumnType.Numeric && col.MeanDelta.HasValue)
            {
                var pctChange = col.MeanA.HasValue && col.MeanA != 0 
                    ? col.MeanDelta.Value / col.MeanA.Value * 100 
                    : 0;
                insights.Add($"'{col.ColumnName}' mean shifted by {pctChange:+0.0;-0.0}% ({col.MeanA:F2} → {col.MeanB:F2})");
            }
            else if (col.ColumnType == ColumnType.Categorical && col.ModeA != col.ModeB)
            {
                insights.Add($"'{col.ColumnName}' mode changed from '{col.ModeA}' to '{col.ModeB}'");
            }
        }

        // Null rate changes
        var nullChanges = comparison.ColumnComparisons
            .Where(c => Math.Abs(c.NullRateDelta) > 0.1)
            .OrderByDescending(c => Math.Abs(c.NullRateDelta))
            .Take(2);
        foreach (var col in nullChanges)
        {
            insights.Add($"'{col.ColumnName}' null rate changed by {col.NullRateDelta * 100:+0.0;-0.0}pp");
        }

        // Overall assessment
        if (comparison.Similarity >= 0.9)
            insights.Insert(0, "Segments are highly similar (>90% match)");
        else if (comparison.Similarity >= 0.7)
            insights.Insert(0, "Segments show moderate differences");
        else if (comparison.Similarity >= 0.5)
            insights.Insert(0, "Segments have significant differences");
        else
            insights.Insert(0, "Segments are substantially different (<50% similarity)");

        return insights;
    }
}

#region Models

/// <summary>
/// Statistical centroid of a data profile - the "center" of the data
/// </summary>
public class ProfileCentroid
{
    public string SourcePath { get; set; } = "";
    public string? SegmentName { get; set; }
    public long RowCount { get; set; }
    public int ColumnCount { get; set; }
    public DateTime ComputedAt { get; set; }
    public List<ColumnCentroid> Columns { get; set; } = [];
    
    /// <summary>
    /// Flat vector representation for ML/clustering
    /// </summary>
    public double[] Vector { get; set; } = [];
}

/// <summary>
/// Centroid data for a single column
/// </summary>
public class ColumnCentroid
{
    public string ColumnName { get; set; } = "";
    public ColumnType ColumnType { get; set; }
    public double NullRate { get; set; }
    public double UniqueRate { get; set; }
    
    // Numeric columns
    public double? NumericCenter { get; set; } // Mean
    public double? NumericSpread { get; set; } // StdDev
    public double? NumericMin { get; set; }
    public double? NumericMax { get; set; }
    public double? NumericMedian { get; set; }
    public double? Skewness { get; set; }
    public double? NormalizedCenter { get; set; } // 0-1 scaled
    
    // Categorical columns
    public string? CategoricalMode { get; set; }
    public double? CategoricalModeFrequency { get; set; }
    public Dictionary<string, double>? CategoricalDistribution { get; set; }
    public int Cardinality { get; set; }
    public double? Entropy { get; set; }
    
    // DateTime columns
    public DateTime? DateRangeStart { get; set; }
    public DateTime? DateRangeEnd { get; set; }
    public DateTime? DateCenter { get; set; }
    public int? DateSpanDays { get; set; }
    
    // Text columns
    public double? TextAvgLength { get; set; }
    public int? TextMaxLength { get; set; }
}

/// <summary>
/// Result of comparing two data segments
/// </summary>
public class SegmentComparison
{
    public string SegmentAName { get; set; } = "";
    public string SegmentBName { get; set; } = "";
    public long SegmentARowCount { get; set; }
    public long SegmentBRowCount { get; set; }
    
    /// <summary>
    /// Overall distance between segments (0 = identical, 1 = maximally different)
    /// </summary>
    public double OverallDistance { get; set; }
    
    /// <summary>
    /// Overall similarity (1 - distance)
    /// </summary>
    public double Similarity { get; set; }
    
    public DateTime ComparedAt { get; set; }
    public List<ColumnComparison> ColumnComparisons { get; set; } = [];
    public List<string> Insights { get; set; } = [];
}

/// <summary>
/// Comparison of a single column between two segments
/// </summary>
public class ColumnComparison
{
    public string ColumnName { get; set; } = "";
    public ColumnType ColumnType { get; set; }
    public double Distance { get; set; }
    
    // Numeric
    public double? MeanA { get; set; }
    public double? MeanB { get; set; }
    public double? MeanDelta { get; set; }
    
    // Categorical
    public string? ModeA { get; set; }
    public string? ModeB { get; set; }
    public double? ModeFrequencyA { get; set; }
    public double? ModeFrequencyB { get; set; }
    
    // Null rates
    public double NullRateA { get; set; }
    public double NullRateB { get; set; }
    public double NullRateDelta { get; set; }
}

#endregion
