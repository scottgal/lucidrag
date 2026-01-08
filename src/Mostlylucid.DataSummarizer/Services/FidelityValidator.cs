using DuckDB.NET.Data;
using MathNet.Numerics.Statistics;
using Mostlylucid.DataSummarizer.Models;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Validates fidelity of synthetic data against the original profile.
/// Provides measurable metrics to validate "statistically identical" claims.
/// </summary>
public class FidelityValidator : IDisposable
{
    private DuckDBConnection? _connection;
    private readonly bool _verbose;

    public FidelityValidator(bool verbose = false)
    {
        _verbose = verbose;
    }

    /// <summary>
    /// Validate synthetic data against the original profile.
    /// </summary>
    public async Task<SynthesisFidelityReport> ValidateAsync(
        DataProfile originalProfile,
        string syntheticFilePath)
    {
        var report = new SynthesisFidelityReport
        {
            OriginalRowCount = originalProfile.RowCount
        };

        _connection = new DuckDBConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        // Profile the synthetic data
        var syntheticProfile = await ProfileSyntheticAsync(syntheticFilePath);
        report.SyntheticRowCount = (int)syntheticProfile.RowCount;

        // Compare each column
        foreach (var origCol in originalProfile.Columns)
        {
            var synthCol = syntheticProfile.Columns.FirstOrDefault(c => 
                c.Name.Equals(origCol.Name, StringComparison.OrdinalIgnoreCase));

            if (synthCol == null)
            {
                report.Warnings.Add($"Column '{origCol.Name}' missing in synthetic data");
                continue;
            }

            var colFidelity = ComputeColumnFidelity(origCol, synthCol);
            report.ColumnMetrics.Add(colFidelity);
        }

        // Compare correlations
        foreach (var origCorr in originalProfile.Correlations.Take(10))
        {
            var synthCorr = syntheticProfile.Correlations.FirstOrDefault(c =>
                (c.Column1 == origCorr.Column1 && c.Column2 == origCorr.Column2) ||
                (c.Column1 == origCorr.Column2 && c.Column2 == origCorr.Column1));

            var relFidelity = new RelationshipFidelity
            {
                Column1 = origCorr.Column1,
                Column2 = origCorr.Column2,
                Type = RelationshipType.NumericCorrelation,
                OriginalStrength = origCorr.Correlation,
                SyntheticStrength = synthCorr?.Correlation ?? 0
            };

            // Score based on how close the correlations are
            relFidelity.Score = 1.0 - Math.Min(1.0, Math.Abs(relFidelity.Delta) / 0.5);
            report.RelationshipMetrics.Add(relFidelity);
        }

        // Privacy compliance checks
        report.Privacy = CheckPrivacyCompliance(originalProfile, syntheticProfile);

        // Calculate overall score
        report.OverallScore = CalculateOverallScore(report);

        return report;
    }

    private async Task<DataProfile> ProfileSyntheticAsync(string filePath)
    {
        var profiler = new DuckDbProfiler(_verbose, new ProfileOptions { FastMode = true });
        var source = new DataSource { Source = filePath, Type = DataSourceType.Csv };
        return await profiler.ProfileAsync(source);
    }

    private ColumnFidelity ComputeColumnFidelity(ColumnProfile orig, ColumnProfile synth)
    {
        var fidelity = new ColumnFidelity
        {
            ColumnName = orig.Name,
            ColumnType = orig.InferredType
        };

        // Null rate comparison
        fidelity.NullRateDelta = Math.Abs(orig.NullPercent - synth.NullPercent);

        switch (orig.InferredType)
        {
            case ColumnType.Numeric:
                ComputeNumericFidelity(orig, synth, fidelity);
                break;

            case ColumnType.Categorical:
            case ColumnType.Boolean:
                ComputeCategoricalFidelity(orig, synth, fidelity);
                break;
        }

        // Calculate overall column score
        fidelity.Score = CalculateColumnScore(fidelity);

        return fidelity;
    }

    private void ComputeNumericFidelity(ColumnProfile orig, ColumnProfile synth, ColumnFidelity fidelity)
    {
        // Mean delta (normalized by std)
        if (orig.Mean.HasValue && synth.Mean.HasValue && orig.StdDev.HasValue && orig.StdDev.Value > 0)
        {
            fidelity.MeanDelta = Math.Abs(orig.Mean.Value - synth.Mean.Value) / orig.StdDev.Value;
        }

        // Std dev delta (relative)
        if (orig.StdDev.HasValue && synth.StdDev.HasValue && orig.StdDev.Value > 0)
        {
            fidelity.StdDevDelta = Math.Abs(orig.StdDev.Value - synth.StdDev.Value) / orig.StdDev.Value;
        }

        // Quantile deltas
        if (orig.Q25.HasValue && synth.Q25.HasValue && orig.Iqr.HasValue && orig.Iqr.Value > 0)
        {
            fidelity.Q25Delta = Math.Abs(orig.Q25.Value - synth.Q25.Value) / orig.Iqr.Value;
        }

        if (orig.Median.HasValue && synth.Median.HasValue && orig.Iqr.HasValue && orig.Iqr.Value > 0)
        {
            fidelity.MedianDelta = Math.Abs(orig.Median.Value - synth.Median.Value) / orig.Iqr.Value;
        }

        if (orig.Q75.HasValue && synth.Q75.HasValue && orig.Iqr.HasValue && orig.Iqr.Value > 0)
        {
            fidelity.Q75Delta = Math.Abs(orig.Q75.Value - synth.Q75.Value) / orig.Iqr.Value;
        }

        // Kolmogorov-Smirnov statistic would require the actual data
        // For now, approximate from quantile differences
        if (fidelity.Q25Delta.HasValue && fidelity.MedianDelta.HasValue && fidelity.Q75Delta.HasValue)
        {
            fidelity.KsStatistic = (fidelity.Q25Delta + fidelity.MedianDelta + fidelity.Q75Delta) / 3.0;
        }
    }

    private void ComputeCategoricalFidelity(ColumnProfile orig, ColumnProfile synth, ColumnFidelity fidelity)
    {
        if (orig.TopValues == null || synth.TopValues == null) return;

        // Top-K overlap
        var origSet = orig.TopValues.Select(v => v.Value).ToHashSet();
        var synthSet = synth.TopValues.Select(v => v.Value).ToHashSet();
        var overlap = origSet.Intersect(synthSet).Count();
        fidelity.TopKOverlap = origSet.Count > 0 ? (double)overlap / origSet.Count : 1.0;

        // PSI (Population Stability Index)
        fidelity.Psi = CalculatePsi(orig.TopValues, synth.TopValues);

        // Jensen-Shannon divergence
        fidelity.JsDivergence = CalculateJsDivergence(orig.TopValues, synth.TopValues);

        // Category frequency deltas
        fidelity.CategoryFrequencyDeltas = new Dictionary<string, double>();
        foreach (var origVal in orig.TopValues.Take(5))
        {
            var synthVal = synth.TopValues.FirstOrDefault(v => v.Value == origVal.Value);
            var synthPct = synthVal?.Percent ?? 0;
            fidelity.CategoryFrequencyDeltas[origVal.Value] = Math.Abs(origVal.Percent - synthPct);
        }
    }

    /// <summary>
    /// Calculate Population Stability Index (PSI).
    /// Used for drift detection. Lower is better.
    /// PSI < 0.1: No significant change
    /// PSI 0.1-0.25: Moderate change
    /// PSI > 0.25: Significant change
    /// </summary>
    private double CalculatePsi(List<ValueCount> original, List<ValueCount> synthetic)
    {
        double psi = 0;
        var epsilon = 0.001; // Avoid log(0)

        // Build lookup for synthetic values
        var synthLookup = synthetic.ToDictionary(v => v.Value, v => v.Percent / 100.0);

        foreach (var orig in original)
        {
            var origP = Math.Max(epsilon, orig.Percent / 100.0);
            var synthP = synthLookup.TryGetValue(orig.Value, out var p) ? Math.Max(epsilon, p) : epsilon;

            psi += (synthP - origP) * Math.Log(synthP / origP);
        }

        return Math.Abs(psi);
    }

    /// <summary>
    /// Calculate Jensen-Shannon divergence (0-1, symmetric measure of distribution difference).
    /// </summary>
    private double CalculateJsDivergence(List<ValueCount> original, List<ValueCount> synthetic)
    {
        var epsilon = 0.001;
        var allValues = original.Select(v => v.Value)
            .Union(synthetic.Select(v => v.Value))
            .ToList();

        var origLookup = original.ToDictionary(v => v.Value, v => Math.Max(epsilon, v.Percent / 100.0));
        var synthLookup = synthetic.ToDictionary(v => v.Value, v => Math.Max(epsilon, v.Percent / 100.0));

        // Normalize to ensure they sum to 1
        var origSum = allValues.Sum(v => origLookup.GetValueOrDefault(v, epsilon));
        var synthSum = allValues.Sum(v => synthLookup.GetValueOrDefault(v, epsilon));

        double klPM = 0, klQM = 0;

        foreach (var val in allValues)
        {
            var p = origLookup.GetValueOrDefault(val, epsilon) / origSum;
            var q = synthLookup.GetValueOrDefault(val, epsilon) / synthSum;
            var m = (p + q) / 2;

            if (p > 0 && m > 0) klPM += p * Math.Log(p / m);
            if (q > 0 && m > 0) klQM += q * Math.Log(q / m);
        }

        return (klPM + klQM) / 2;
    }

    private double CalculateColumnScore(ColumnFidelity fidelity)
    {
        var penalties = new List<double>();

        // Null rate penalty
        penalties.Add(Math.Min(1, fidelity.NullRateDelta / 10.0));

        // Numeric penalties
        if (fidelity.MeanDelta.HasValue)
            penalties.Add(Math.Min(1, fidelity.MeanDelta.Value));
        if (fidelity.StdDevDelta.HasValue)
            penalties.Add(Math.Min(1, fidelity.StdDevDelta.Value));
        if (fidelity.KsStatistic.HasValue)
            penalties.Add(Math.Min(1, fidelity.KsStatistic.Value * 2));

        // Categorical penalties
        if (fidelity.Psi.HasValue)
            penalties.Add(Math.Min(1, fidelity.Psi.Value * 4)); // PSI > 0.25 is bad
        if (fidelity.TopKOverlap.HasValue)
            penalties.Add(1.0 - fidelity.TopKOverlap.Value);
        if (fidelity.JsDivergence.HasValue)
            penalties.Add(Math.Min(1, fidelity.JsDivergence.Value * 2));

        if (penalties.Count == 0) return 1.0;

        // Average penalty, convert to score
        var avgPenalty = penalties.Average();
        return Math.Max(0, 1.0 - avgPenalty);
    }

    private PrivacyCompliance CheckPrivacyCompliance(DataProfile original, DataProfile synthetic)
    {
        var compliance = new PrivacyCompliance();

        // Check if k-anonymity was applied
        foreach (var col in original.Columns)
        {
            if (col.SynthesisPolicy?.KAnonymityThreshold > 0)
            {
                compliance.KAnonymityEnforced = true;
                compliance.KAnonymityThreshold = col.SynthesisPolicy.KAnonymityThreshold ?? 5;
            }

            // Count suppressed categories
            if (col.TopValues != null)
            {
                var threshold = col.SynthesisPolicy?.KAnonymityThreshold ?? 5;
                compliance.SuppressedCategories += col.TopValues.Count(v => v.Count < threshold);
            }

            // Track redacted columns
            if (col.SynthesisPolicy?.SuppressTopValues == true)
            {
                compliance.RedactedColumns.Add(col.Name);
            }
        }

        // Check for potential re-identification
        // Warning if synthetic data has high-cardinality categorical columns
        foreach (var synthCol in synthetic.Columns)
        {
            if (synthCol.InferredType == ColumnType.Categorical && synthCol.CardinalityRatio > 0.5)
            {
                compliance.Warnings.Add(
                    $"Column '{synthCol.Name}' has high cardinality ({synthCol.UniqueCount} unique values) - potential re-identification risk");
            }
        }

        compliance.PassesUniquenessCheck = compliance.Warnings.Count == 0;

        return compliance;
    }

    private double CalculateOverallScore(SynthesisFidelityReport report)
    {
        var scores = new List<double>();

        // Column-level scores (weighted by importance)
        foreach (var col in report.ColumnMetrics)
        {
            scores.Add(col.Score);
        }

        // Relationship scores
        foreach (var rel in report.RelationshipMetrics)
        {
            scores.Add(rel.Score);
        }

        // Privacy compliance bonus/penalty
        var privacyScore = report.Privacy.PassesUniquenessCheck ? 1.0 : 0.8;
        scores.Add(privacyScore);

        if (scores.Count == 0) return 100;

        return Math.Round(scores.Average() * 100, 1);
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
