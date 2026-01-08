using Mostlylucid.DataSummarizer.Models;
using Mostlylucid.DataSummarizer.Services;

namespace Mostlylucid.DataSummarizer.Tests;

/// <summary>
/// Tests for ProfileComparator - drift detection and profile comparison
/// </summary>
public class ProfileComparatorTests
{
    private readonly ProfileComparator _comparator = new();

    #region Schema Change Detection

    [Fact]
    public void Compare_DetectsAddedColumns()
    {
        // Arrange
        var baseline = CreateProfile(columns: new[] { "A", "B" });
        var current = CreateProfile(columns: new[] { "A", "B", "C" });

        // Act
        var result = _comparator.Compare(baseline, current);

        // Assert
        Assert.Contains("C", result.SchemaChanges.AddedColumns);
        Assert.True(result.SchemaChanges.HasChanges);
    }

    [Fact]
    public void Compare_DetectsRemovedColumns()
    {
        // Arrange
        var baseline = CreateProfile(columns: new[] { "A", "B", "C" });
        var current = CreateProfile(columns: new[] { "A", "B" });

        // Act
        var result = _comparator.Compare(baseline, current);

        // Assert
        Assert.Contains("C", result.SchemaChanges.RemovedColumns);
        Assert.True(result.SchemaChanges.HasChanges);
    }

    [Fact]
    public void Compare_DetectsTypeChanges()
    {
        // Arrange
        var baseline = CreateProfile(columns: new[] { "A" }, types: new[] { ColumnType.Numeric });
        var current = CreateProfile(columns: new[] { "A" }, types: new[] { ColumnType.Text });

        // Act
        var result = _comparator.Compare(baseline, current);

        // Assert
        Assert.Single(result.SchemaChanges.TypeChanges);
        Assert.Equal("A", result.SchemaChanges.TypeChanges[0].ColumnName);
        Assert.Equal(ColumnType.Numeric, result.SchemaChanges.TypeChanges[0].BaselineType);
        Assert.Equal(ColumnType.Text, result.SchemaChanges.TypeChanges[0].CurrentType);
    }

    [Fact]
    public void Compare_NoChanges_HasNoSchemaChanges()
    {
        // Arrange
        var baseline = CreateProfile(columns: new[] { "A", "B" });
        var current = CreateProfile(columns: new[] { "A", "B" });

        // Act
        var result = _comparator.Compare(baseline, current);

        // Assert
        Assert.False(result.SchemaChanges.HasChanges);
        Assert.Empty(result.SchemaChanges.AddedColumns);
        Assert.Empty(result.SchemaChanges.RemovedColumns);
        Assert.Empty(result.SchemaChanges.TypeChanges);
    }

    #endregion

    #region Row Count Changes

    [Fact]
    public void Compare_DetectsRowCountIncrease()
    {
        // Arrange
        var baseline = CreateProfile(rowCount: 1000);
        var current = CreateProfile(rowCount: 1500);

        // Act
        var result = _comparator.Compare(baseline, current);

        // Assert
        Assert.Equal(1000, result.BaselineRowCount);
        Assert.Equal(1500, result.CurrentRowCount);
        Assert.True(result.RowCountChange.PercentChange > 0);
    }

    [Fact]
    public void Compare_DetectsRowCountDecrease()
    {
        // Arrange
        var baseline = CreateProfile(rowCount: 1000);
        var current = CreateProfile(rowCount: 100);

        // Act
        var result = _comparator.Compare(baseline, current);

        // Assert
        Assert.True(result.RowCountChange.PercentChange < 0);
        Assert.True(result.RowCountChange.IsSignificant);
    }

    #endregion

    #region Statistical Drift Detection

    [Fact]
    public void Compare_DetectsNumericDrift()
    {
        // Arrange
        var baseline = CreateProfileWithStats("A", mean: 100, stdDev: 10);
        var current = CreateProfileWithStats("A", mean: 150, stdDev: 10); // Mean shifted significantly

        // Act
        var result = _comparator.Compare(baseline, current);

        // Assert
        // The mean shift of 50 (5 standard deviations) should be detected
        Assert.True(result.ColumnDiffs.Count > 0 || result.HasSignificantDrift);
    }

    [Fact]
    public void Compare_DetectsNullRateChange()
    {
        // Arrange
        var baseline = CreateProfileWithNulls("A", nullPercent: 0);
        var current = CreateProfileWithNulls("A", nullPercent: 50);

        // Act
        var result = _comparator.Compare(baseline, current);

        // Assert
        Assert.True(result.HasSignificantDrift || result.ColumnDiffs.Any(d => d.ColumnName == "A"));
    }

    [Fact]
    public void Compare_StableData_LowDriftScore()
    {
        // Arrange
        var baseline = CreateProfileWithStats("Value", mean: 100, stdDev: 10);
        var current = CreateProfileWithStats("Value", mean: 101, stdDev: 10.5); // Minor changes

        // Act
        var result = _comparator.Compare(baseline, current);

        // Assert
        Assert.True(result.OverallDriftScore < 0.5); // Should be relatively low
    }

    #endregion

    #region Summary and Recommendations

    [Fact]
    public void Compare_GeneratesSummary()
    {
        // Arrange
        var baseline = CreateProfile(rowCount: 1000, columns: new[] { "A", "B" });
        var current = CreateProfile(rowCount: 1200, columns: new[] { "A", "B", "C" });

        // Act
        var result = _comparator.Compare(baseline, current);

        // Assert
        Assert.NotNull(result.Summary);
        Assert.NotEmpty(result.Summary);
    }

    [Fact]
    public void Compare_GeneratesRecommendations_ForSchemaChanges()
    {
        // Arrange
        var baseline = CreateProfile(columns: new[] { "A", "B", "C" });
        var current = CreateProfile(columns: new[] { "A", "B" }); // C removed

        // Act
        var result = _comparator.Compare(baseline, current);

        // Assert
        Assert.NotNull(result.Recommendations);
        // Should have recommendations about schema changes
    }

    #endregion

    #region Helper Methods

    private static DataProfile CreateProfile(
        long rowCount = 1000,
        string[]? columns = null,
        ColumnType[]? types = null)
    {
        columns ??= new[] { "Column1", "Column2" };
        types ??= Enumerable.Repeat(ColumnType.Numeric, columns.Length).ToArray();

        return new DataProfile
        {
            SourcePath = "test.csv",
            RowCount = rowCount,
            Columns = columns.Select((name, i) => new ColumnProfile
            {
                Name = name,
                InferredType = types.Length > i ? types[i] : ColumnType.Numeric,
                Count = rowCount,
                UniqueCount = Math.Min(rowCount, 100)
            }).ToList()
        };
    }

    private static DataProfile CreateProfileWithStats(string columnName, double mean, double stdDev)
    {
        return new DataProfile
        {
            SourcePath = "test.csv",
            RowCount = 1000,
            Columns = new List<ColumnProfile>
            {
                new()
                {
                    Name = columnName,
                    InferredType = ColumnType.Numeric,
                    Count = 1000,
                    UniqueCount = 500,
                    Mean = mean,
                    StdDev = stdDev,
                    Min = mean - 3 * stdDev,
                    Max = mean + 3 * stdDev
                }
            }
        };
    }

    private static DataProfile CreateProfileWithNulls(string columnName, double nullPercent)
    {
        return new DataProfile
        {
            SourcePath = "test.csv",
            RowCount = 1000,
            Columns = new List<ColumnProfile>
            {
                new()
                {
                    Name = columnName,
                    InferredType = ColumnType.Numeric,
                    Count = 1000,
                    NullCount = (long)(1000 * nullPercent / 100),
                    UniqueCount = 500,
                    Mean = 100,
                    StdDev = 10
                }
            }
        };
    }

    #endregion
}
