namespace Mostlylucid.DataSummarizer.Models;

/// <summary>
/// Fidelity report comparing synthetic data to original profile.
/// Provides measurable validation that synthetic data matches statistical properties.
/// </summary>
public class SynthesisFidelityReport
{
    /// <summary>Overall fidelity score (0-100)</summary>
    public double OverallScore { get; set; }
    
    /// <summary>Per-column fidelity metrics</summary>
    public List<ColumnFidelity> ColumnMetrics { get; set; } = [];
    
    /// <summary>Cross-column relationship fidelity</summary>
    public List<RelationshipFidelity> RelationshipMetrics { get; set; } = [];
    
    /// <summary>Privacy compliance checks</summary>
    public PrivacyCompliance Privacy { get; set; } = new();
    
    /// <summary>Warnings about potential issues</summary>
    public List<string> Warnings { get; set; } = [];
    
    /// <summary>When this report was generated</summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>Number of rows in synthetic data</summary>
    public int SyntheticRowCount { get; set; }
    
    /// <summary>Number of rows in original data</summary>
    public long OriginalRowCount { get; set; }
}

/// <summary>
/// Fidelity metrics for a single column
/// </summary>
public class ColumnFidelity
{
    public string ColumnName { get; set; } = "";
    public ColumnType ColumnType { get; set; }
    
    /// <summary>Overall column fidelity (0-1)</summary>
    public double Score { get; set; }
    
    /// <summary>Difference in null rate (absolute)</summary>
    public double NullRateDelta { get; set; }
    
    // Numeric columns
    /// <summary>Kolmogorov-Smirnov statistic (0-1, lower is better)</summary>
    public double? KsStatistic { get; set; }
    
    /// <summary>Mean difference (relative to original std)</summary>
    public double? MeanDelta { get; set; }
    
    /// <summary>Std dev difference (relative)</summary>
    public double? StdDevDelta { get; set; }
    
    /// <summary>Quantile differences at 25th, 50th, 75th percentiles</summary>
    public double? Q25Delta { get; set; }
    public double? MedianDelta { get; set; }
    public double? Q75Delta { get; set; }
    
    // Categorical columns
    /// <summary>Population Stability Index (PSI) - drift metric (lower is better)</summary>
    public double? Psi { get; set; }
    
    /// <summary>Top-K category overlap percentage</summary>
    public double? TopKOverlap { get; set; }
    
    /// <summary>Jensen-Shannon divergence (0-1, lower is better)</summary>
    public double? JsDivergence { get; set; }
    
    /// <summary>Frequency deltas for top categories</summary>
    public Dictionary<string, double>? CategoryFrequencyDeltas { get; set; }
}

/// <summary>
/// Fidelity metrics for column relationships
/// </summary>
public class RelationshipFidelity
{
    public string Column1 { get; set; } = "";
    public string Column2 { get; set; } = "";
    public RelationshipType Type { get; set; }
    
    /// <summary>Relationship fidelity score (0-1)</summary>
    public double Score { get; set; }
    
    /// <summary>Original correlation/association strength</summary>
    public double OriginalStrength { get; set; }
    
    /// <summary>Synthetic correlation/association strength</summary>
    public double SyntheticStrength { get; set; }
    
    /// <summary>Difference in strength</summary>
    public double Delta => Math.Abs(OriginalStrength - SyntheticStrength);
}

public enum RelationshipType
{
    NumericCorrelation,
    CategoricalAssociation,
    ConditionalDistribution
}

/// <summary>
/// Privacy compliance checks for synthetic data
/// </summary>
public class PrivacyCompliance
{
    /// <summary>Whether k-anonymity threshold was enforced</summary>
    public bool KAnonymityEnforced { get; set; }
    
    /// <summary>The k-anonymity threshold used</summary>
    public int KAnonymityThreshold { get; set; }
    
    /// <summary>Number of rare categories that were suppressed</summary>
    public int SuppressedCategories { get; set; }
    
    /// <summary>PII columns that were redacted</summary>
    public List<string> RedactedColumns { get; set; } = [];
    
    /// <summary>Whether tail clipping was applied</summary>
    public bool TailsClipped { get; set; }
    
    /// <summary>Unique combination check: any combinations that might re-identify?</summary>
    public bool PassesUniquenessCheck { get; set; }
    
    /// <summary>Warnings about potential privacy risks</summary>
    public List<string> Warnings { get; set; } = [];
}
