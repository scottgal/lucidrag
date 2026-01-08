using Mostlylucid.DataSummarizer.Models;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Data constraints/expectations system inspired by Great Expectations.
/// Define rules that data must satisfy and validate against profiles.
/// </summary>
public class ConstraintValidator
{
    private readonly bool _verbose;

    public ConstraintValidator(bool verbose = false)
    {
        _verbose = verbose;
    }

    /// <summary>
    /// Validate a profile against a set of constraints
    /// </summary>
    public ConstraintValidationResult Validate(DataProfile profile, ConstraintSuite suite)
    {
        var result = new ConstraintValidationResult
        {
            SuiteName = suite.Name,
            ProfileSource = profile.SourcePath,
            ValidatedAt = DateTime.UtcNow,
            Results = []
        };

        foreach (var constraint in suite.Constraints)
        {
            var constraintResult = ValidateConstraint(profile, constraint);
            result.Results.Add(constraintResult);
        }

        result.TotalConstraints = result.Results.Count;
        result.PassedConstraints = result.Results.Count(r => r.Passed);
        result.FailedConstraints = result.Results.Count(r => !r.Passed);
        result.PassRate = result.TotalConstraints > 0 
            ? (double)result.PassedConstraints / result.TotalConstraints 
            : 1.0;
        result.AllPassed = result.FailedConstraints == 0;

        return result;
    }

    /// <summary>
    /// Auto-generate constraints from an existing profile (learn from data)
    /// </summary>
    public ConstraintSuite GenerateFromProfile(DataProfile profile, ConstraintGenerationOptions? options = null)
    {
        options ??= new ConstraintGenerationOptions();
        var constraints = new List<DataConstraint>();

        // Table-level constraints
        if (options.IncludeRowCountConstraints)
        {
            constraints.Add(new DataConstraint
            {
                Type = ConstraintType.RowCountBetween,
                MinValue = Math.Max(1, (long)(profile.RowCount * 0.5)),
                MaxValue = (long)(profile.RowCount * 2.0),
                Description = $"Row count should be between {profile.RowCount * 0.5:N0} and {profile.RowCount * 2:N0}"
            });
        }

        if (options.IncludeSchemaConstraints)
        {
            constraints.Add(new DataConstraint
            {
                Type = ConstraintType.ColumnCountEquals,
                ExpectedValue = profile.ColumnCount,
                Description = $"Column count should be {profile.ColumnCount}"
            });

            // Expected columns
            constraints.Add(new DataConstraint
            {
                Type = ConstraintType.ColumnsExist,
                ExpectedColumns = profile.Columns.Select(c => c.Name).ToList(),
                Description = "All expected columns should exist"
            });
        }

        // Column-level constraints
        foreach (var col in profile.Columns)
        {
            // Null constraints
            if (options.IncludeNullConstraints)
            {
                if (col.NullPercent == 0)
                {
                    constraints.Add(new DataConstraint
                    {
                        Type = ConstraintType.ColumnNotNull,
                        ColumnName = col.Name,
                        Description = $"Column '{col.Name}' should not have null values"
                    });
                }
                else if (col.NullPercent < options.MaxAllowedNullPercent)
                {
                    constraints.Add(new DataConstraint
                    {
                        Type = ConstraintType.NullPercentBelow,
                        ColumnName = col.Name,
                        MaxValue = Math.Min(col.NullPercent * 1.5, options.MaxAllowedNullPercent),
                        Description = $"Column '{col.Name}' null % should be below {col.NullPercent * 1.5:F1}%"
                    });
                }
            }

            // Type constraints
            if (options.IncludeTypeConstraints)
            {
                constraints.Add(new DataConstraint
                {
                    Type = ConstraintType.ColumnTypeIs,
                    ColumnName = col.Name,
                    ExpectedType = col.InferredType,
                    Description = $"Column '{col.Name}' should be of type {col.InferredType}"
                });
            }

            // Numeric range constraints
            if (options.IncludeRangeConstraints && col.InferredType == ColumnType.Numeric)
            {
                if (col.Min.HasValue && col.Max.HasValue)
                {
                    var range = col.Max.Value - col.Min.Value;
                    constraints.Add(new DataConstraint
                    {
                        Type = ConstraintType.ValuesBetween,
                        ColumnName = col.Name,
                        MinValue = col.Min.Value - range * 0.1,
                        MaxValue = col.Max.Value + range * 0.1,
                        Description = $"Column '{col.Name}' values should be between {col.Min:F2} and {col.Max:F2} (with 10% tolerance)"
                    });
                }
            }

            // Uniqueness constraints
            if (options.IncludeUniquenessConstraints)
            {
                if (col.UniquePercent >= 99.9)
                {
                    constraints.Add(new DataConstraint
                    {
                        Type = ConstraintType.ColumnUnique,
                        ColumnName = col.Name,
                        Description = $"Column '{col.Name}' should have unique values"
                    });
                }
            }

            // Categorical constraints
            if (options.IncludeCategoricalConstraints && col.InferredType == ColumnType.Categorical)
            {
                if (col.TopValues?.Count > 0 && col.UniqueCount <= 50)
                {
                    constraints.Add(new DataConstraint
                    {
                        Type = ConstraintType.ValuesInSet,
                        ColumnName = col.Name,
                        AllowedValues = col.TopValues.Select(tv => tv.Value ?? "").Where(v => !string.IsNullOrEmpty(v)).ToList(),
                        Description = $"Column '{col.Name}' values should be in the known set"
                    });
                }
            }
        }

        return new ConstraintSuite
        {
            Name = $"Auto-generated from {Path.GetFileName(profile.SourcePath)}",
            Description = $"Generated at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} from profile with {profile.RowCount:N0} rows",
            GeneratedAt = DateTime.UtcNow,
            SourceProfile = profile.SourcePath,
            Constraints = constraints
        };
    }

    private ConstraintResult ValidateConstraint(DataProfile profile, DataConstraint constraint)
    {
        var result = new ConstraintResult
        {
            Constraint = constraint,
            Passed = false
        };

        try
        {
            switch (constraint.Type)
            {
                case ConstraintType.RowCountBetween:
                    result.ActualValue = profile.RowCount;
                    result.Passed = profile.RowCount >= (constraint.MinValue ?? long.MinValue) &&
                                   profile.RowCount <= (constraint.MaxValue ?? long.MaxValue);
                    break;

                case ConstraintType.RowCountEquals:
                    result.ActualValue = profile.RowCount;
                    result.Passed = profile.RowCount == (long)(constraint.ExpectedValue ?? 0);
                    break;

                case ConstraintType.ColumnCountEquals:
                    result.ActualValue = profile.ColumnCount;
                    result.Passed = profile.ColumnCount == (int)(constraint.ExpectedValue ?? 0);
                    break;

                case ConstraintType.ColumnsExist:
                    var existingCols = profile.Columns.Select(c => c.Name).ToHashSet();
                    var missingCols = constraint.ExpectedColumns?.Where(c => !existingCols.Contains(c)).ToList() ?? [];
                    result.Passed = missingCols.Count == 0;
                    result.Details = missingCols.Count > 0 ? $"Missing columns: {string.Join(", ", missingCols)}" : null;
                    break;

                case ConstraintType.ColumnNotNull:
                    var notNullCol = profile.Columns.FirstOrDefault(c => c.Name == constraint.ColumnName);
                    if (notNullCol != null)
                    {
                        result.ActualValue = notNullCol.NullCount;
                        result.Passed = notNullCol.NullCount == 0;
                    }
                    break;

                case ConstraintType.NullPercentBelow:
                    var nullPctCol = profile.Columns.FirstOrDefault(c => c.Name == constraint.ColumnName);
                    if (nullPctCol != null)
                    {
                        result.ActualValue = nullPctCol.NullPercent;
                        result.Passed = nullPctCol.NullPercent <= (constraint.MaxValue ?? 100);
                    }
                    break;

                case ConstraintType.ColumnTypeIs:
                    var typeCol = profile.Columns.FirstOrDefault(c => c.Name == constraint.ColumnName);
                    if (typeCol != null)
                    {
                        result.ActualValue = typeCol.InferredType.ToString();
                        result.Passed = typeCol.InferredType == constraint.ExpectedType;
                    }
                    break;

                case ConstraintType.ValuesBetween:
                    var rangeCol = profile.Columns.FirstOrDefault(c => c.Name == constraint.ColumnName);
                    if (rangeCol != null && rangeCol.Min.HasValue && rangeCol.Max.HasValue)
                    {
                        result.ActualValue = $"[{rangeCol.Min:F2}, {rangeCol.Max:F2}]";
                        result.Passed = rangeCol.Min >= (constraint.MinValue ?? double.MinValue) &&
                                       rangeCol.Max <= (constraint.MaxValue ?? double.MaxValue);
                    }
                    break;

                case ConstraintType.ColumnUnique:
                    var uniqueCol = profile.Columns.FirstOrDefault(c => c.Name == constraint.ColumnName);
                    if (uniqueCol != null)
                    {
                        result.ActualValue = uniqueCol.UniquePercent;
                        result.Passed = uniqueCol.UniquePercent >= 99.9;
                    }
                    break;

                case ConstraintType.ValuesInSet:
                    var setCol = profile.Columns.FirstOrDefault(c => c.Name == constraint.ColumnName);
                    if (setCol != null && constraint.AllowedValues != null && setCol.TopValues != null)
                    {
                        var allowedSet = constraint.AllowedValues.ToHashSet();
                        var actualValues = setCol.TopValues.Select(tv => tv.Value ?? "").ToList();
                        var unknownValues = actualValues.Where(v => !string.IsNullOrEmpty(v) && !allowedSet.Contains(v)).ToList();
                        result.Passed = unknownValues.Count == 0;
                        result.Details = unknownValues.Count > 0 ? $"Unknown values: {string.Join(", ", unknownValues.Take(5))}" : null;
                    }
                    break;

                case ConstraintType.MeanBetween:
                    var meanCol = profile.Columns.FirstOrDefault(c => c.Name == constraint.ColumnName);
                    if (meanCol != null && meanCol.Mean.HasValue)
                    {
                        result.ActualValue = meanCol.Mean;
                        result.Passed = meanCol.Mean >= (constraint.MinValue ?? double.MinValue) &&
                                       meanCol.Mean <= (constraint.MaxValue ?? double.MaxValue);
                    }
                    break;

                case ConstraintType.StdDevBelow:
                    var stdCol = profile.Columns.FirstOrDefault(c => c.Name == constraint.ColumnName);
                    if (stdCol != null && stdCol.StdDev.HasValue)
                    {
                        result.ActualValue = stdCol.StdDev;
                        result.Passed = stdCol.StdDev <= (constraint.MaxValue ?? double.MaxValue);
                    }
                    break;

                default:
                    result.Details = $"Unknown constraint type: {constraint.Type}";
                    break;
            }
        }
        catch (Exception ex)
        {
            result.Passed = false;
            result.Details = $"Error validating constraint: {ex.Message}";
        }

        return result;
    }
}

#region Constraint Models

/// <summary>
/// A suite of data constraints
/// </summary>
public class ConstraintSuite
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTime GeneratedAt { get; set; }
    public string? SourceProfile { get; set; }
    public List<DataConstraint> Constraints { get; set; } = [];
}

/// <summary>
/// A single data constraint/expectation
/// </summary>
public class DataConstraint
{
    public ConstraintType Type { get; set; }
    public string? ColumnName { get; set; }
    public string Description { get; set; } = "";
    
    // Value constraints
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }
    public object? ExpectedValue { get; set; }
    public ColumnType? ExpectedType { get; set; }
    
    // Set constraints
    public List<string>? AllowedValues { get; set; }
    public List<string>? ExpectedColumns { get; set; }
}

/// <summary>
/// Types of constraints supported
/// </summary>
public enum ConstraintType
{
    // Table-level
    RowCountBetween,
    RowCountEquals,
    ColumnCountEquals,
    ColumnsExist,
    
    // Column null constraints
    ColumnNotNull,
    NullPercentBelow,
    
    // Column type constraints
    ColumnTypeIs,
    
    // Column value constraints
    ValuesBetween,
    ValuesInSet,
    ColumnUnique,
    
    // Statistical constraints
    MeanBetween,
    StdDevBelow,
    MedianBetween,
    
    // Custom
    Custom
}

/// <summary>
/// Options for auto-generating constraints
/// </summary>
public class ConstraintGenerationOptions
{
    public bool IncludeRowCountConstraints { get; set; } = true;
    public bool IncludeSchemaConstraints { get; set; } = true;
    public bool IncludeNullConstraints { get; set; } = true;
    public bool IncludeTypeConstraints { get; set; } = true;
    public bool IncludeRangeConstraints { get; set; } = true;
    public bool IncludeUniquenessConstraints { get; set; } = true;
    public bool IncludeCategoricalConstraints { get; set; } = true;
    public double MaxAllowedNullPercent { get; set; } = 50.0;
}

/// <summary>
/// Result of validating a constraint suite
/// </summary>
public class ConstraintValidationResult
{
    public string SuiteName { get; set; } = "";
    public string? ProfileSource { get; set; }
    public DateTime ValidatedAt { get; set; }
    public bool AllPassed { get; set; }
    public int TotalConstraints { get; set; }
    public int PassedConstraints { get; set; }
    public int FailedConstraints { get; set; }
    public double PassRate { get; set; }
    public List<ConstraintResult> Results { get; set; } = [];
    
    /// <summary>
    /// Get failed constraints only
    /// </summary>
    public IEnumerable<ConstraintResult> GetFailures() => Results.Where(r => !r.Passed);
}

/// <summary>
/// Result of validating a single constraint
/// </summary>
public class ConstraintResult
{
    public DataConstraint Constraint { get; set; } = new();
    public bool Passed { get; set; }
    public object? ActualValue { get; set; }
    public string? Details { get; set; }
}

#endregion
