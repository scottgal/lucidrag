using Mostlylucid.DataSummarizer.Models;
using Mostlylucid.DataSummarizer.Services;

namespace Mostlylucid.DataSummarizer.Tests;

/// <summary>
/// Tests for the data constraint validation system
/// </summary>
public class ConstraintValidatorTests
{
    private readonly ConstraintValidator _validator = new();

    private static DataProfile CreateTestProfile()
    {
        return new DataProfile
        {
            SourcePath = "test.csv",
            RowCount = 1000,
            Columns =
            [
                new ColumnProfile
                {
                    Name = "id",
                    InferredType = ColumnType.Id,
                    Count = 1000,
                    NullCount = 0,
                    UniqueCount = 1000,
                    Min = 1,
                    Max = 1000,
                    Mean = 500.5,
                    StdDev = 288.7
                },
                new ColumnProfile
                {
                    Name = "name",
                    InferredType = ColumnType.Text,
                    Count = 1000,
                    NullCount = 50,
                    UniqueCount = 800
                },
                new ColumnProfile
                {
                    Name = "age",
                    InferredType = ColumnType.Numeric,
                    Count = 1000,
                    NullCount = 0,
                    UniqueCount = 80,
                    Min = 18,
                    Max = 99,
                    Mean = 45.5,
                    StdDev = 15.2
                },
                new ColumnProfile
                {
                    Name = "status",
                    InferredType = ColumnType.Categorical,
                    Count = 1000,
                    NullCount = 0,
                    UniqueCount = 3,
                    TopValues =
                    [
                        new ValueCount { Value = "active", Count = 600, Percent = 60 },
                        new ValueCount { Value = "inactive", Count = 300, Percent = 30 },
                        new ValueCount { Value = "pending", Count = 100, Percent = 10 }
                    ]
                }
            ]
        };
    }

    [Fact]
    public void Validate_RowCountBetween_Passes()
    {
        var profile = CreateTestProfile();
        var suite = new ConstraintSuite
        {
            Name = "Test",
            Constraints =
            [
                new DataConstraint
                {
                    Type = ConstraintType.RowCountBetween,
                    MinValue = 500,
                    MaxValue = 2000
                }
            ]
        };

        var result = _validator.Validate(profile, suite);

        Assert.True(result.AllPassed);
        Assert.Equal(1, result.PassedConstraints);
    }

    [Fact]
    public void Validate_RowCountBetween_Fails()
    {
        var profile = CreateTestProfile();
        var suite = new ConstraintSuite
        {
            Name = "Test",
            Constraints =
            [
                new DataConstraint
                {
                    Type = ConstraintType.RowCountBetween,
                    MinValue = 2000,
                    MaxValue = 3000
                }
            ]
        };

        var result = _validator.Validate(profile, suite);

        Assert.False(result.AllPassed);
        Assert.Equal(1, result.FailedConstraints);
    }

    [Fact]
    public void Validate_ColumnNotNull_Passes()
    {
        var profile = CreateTestProfile();
        var suite = new ConstraintSuite
        {
            Name = "Test",
            Constraints =
            [
                new DataConstraint
                {
                    Type = ConstraintType.ColumnNotNull,
                    ColumnName = "id"
                }
            ]
        };

        var result = _validator.Validate(profile, suite);

        Assert.True(result.AllPassed);
    }

    [Fact]
    public void Validate_ColumnNotNull_Fails()
    {
        var profile = CreateTestProfile();
        var suite = new ConstraintSuite
        {
            Name = "Test",
            Constraints =
            [
                new DataConstraint
                {
                    Type = ConstraintType.ColumnNotNull,
                    ColumnName = "name"  // This column has nulls
                }
            ]
        };

        var result = _validator.Validate(profile, suite);

        Assert.False(result.AllPassed);
    }

    [Fact]
    public void Validate_ColumnTypeIs_Passes()
    {
        var profile = CreateTestProfile();
        var suite = new ConstraintSuite
        {
            Name = "Test",
            Constraints =
            [
                new DataConstraint
                {
                    Type = ConstraintType.ColumnTypeIs,
                    ColumnName = "age",
                    ExpectedType = ColumnType.Numeric
                }
            ]
        };

        var result = _validator.Validate(profile, suite);

        Assert.True(result.AllPassed);
    }

    [Fact]
    public void Validate_ColumnTypeIs_Fails()
    {
        var profile = CreateTestProfile();
        var suite = new ConstraintSuite
        {
            Name = "Test",
            Constraints =
            [
                new DataConstraint
                {
                    Type = ConstraintType.ColumnTypeIs,
                    ColumnName = "age",
                    ExpectedType = ColumnType.Text  // Wrong type
                }
            ]
        };

        var result = _validator.Validate(profile, suite);

        Assert.False(result.AllPassed);
    }

    [Fact]
    public void Validate_ValuesBetween_Passes()
    {
        var profile = CreateTestProfile();
        var suite = new ConstraintSuite
        {
            Name = "Test",
            Constraints =
            [
                new DataConstraint
                {
                    Type = ConstraintType.ValuesBetween,
                    ColumnName = "age",
                    MinValue = 0,
                    MaxValue = 150
                }
            ]
        };

        var result = _validator.Validate(profile, suite);

        Assert.True(result.AllPassed);
    }

    [Fact]
    public void Validate_ValuesBetween_Fails()
    {
        var profile = CreateTestProfile();
        var suite = new ConstraintSuite
        {
            Name = "Test",
            Constraints =
            [
                new DataConstraint
                {
                    Type = ConstraintType.ValuesBetween,
                    ColumnName = "age",
                    MinValue = 20,  // Min is 18, so this fails
                    MaxValue = 80   // Max is 99, so this also fails
                }
            ]
        };

        var result = _validator.Validate(profile, suite);

        Assert.False(result.AllPassed);
    }

    [Fact]
    public void Validate_ColumnsExist_Passes()
    {
        var profile = CreateTestProfile();
        var suite = new ConstraintSuite
        {
            Name = "Test",
            Constraints =
            [
                new DataConstraint
                {
                    Type = ConstraintType.ColumnsExist,
                    ExpectedColumns = ["id", "name", "age"]
                }
            ]
        };

        var result = _validator.Validate(profile, suite);

        Assert.True(result.AllPassed);
    }

    [Fact]
    public void Validate_ColumnsExist_Fails()
    {
        var profile = CreateTestProfile();
        var suite = new ConstraintSuite
        {
            Name = "Test",
            Constraints =
            [
                new DataConstraint
                {
                    Type = ConstraintType.ColumnsExist,
                    ExpectedColumns = ["id", "name", "missing_column"]
                }
            ]
        };

        var result = _validator.Validate(profile, suite);

        Assert.False(result.AllPassed);
        Assert.Contains("missing_column", result.Results[0].Details);
    }

    [Fact]
    public void Validate_ColumnUnique_Passes()
    {
        var profile = CreateTestProfile();
        var suite = new ConstraintSuite
        {
            Name = "Test",
            Constraints =
            [
                new DataConstraint
                {
                    Type = ConstraintType.ColumnUnique,
                    ColumnName = "id"  // 100% unique
                }
            ]
        };

        var result = _validator.Validate(profile, suite);

        Assert.True(result.AllPassed);
    }

    [Fact]
    public void Validate_ColumnUnique_Fails()
    {
        var profile = CreateTestProfile();
        var suite = new ConstraintSuite
        {
            Name = "Test",
            Constraints =
            [
                new DataConstraint
                {
                    Type = ConstraintType.ColumnUnique,
                    ColumnName = "status"  // Only 3 unique values
                }
            ]
        };

        var result = _validator.Validate(profile, suite);

        Assert.False(result.AllPassed);
    }

    [Fact]
    public void Validate_ValuesInSet_Passes()
    {
        var profile = CreateTestProfile();
        var suite = new ConstraintSuite
        {
            Name = "Test",
            Constraints =
            [
                new DataConstraint
                {
                    Type = ConstraintType.ValuesInSet,
                    ColumnName = "status",
                    AllowedValues = ["active", "inactive", "pending", "archived"]
                }
            ]
        };

        var result = _validator.Validate(profile, suite);

        Assert.True(result.AllPassed);
    }

    [Fact]
    public void Validate_ValuesInSet_Fails()
    {
        var profile = CreateTestProfile();
        var suite = new ConstraintSuite
        {
            Name = "Test",
            Constraints =
            [
                new DataConstraint
                {
                    Type = ConstraintType.ValuesInSet,
                    ColumnName = "status",
                    AllowedValues = ["active", "inactive"]  // Missing "pending"
                }
            ]
        };

        var result = _validator.Validate(profile, suite);

        Assert.False(result.AllPassed);
    }

    [Fact]
    public void Validate_MultipleConstraints_PartialPass()
    {
        var profile = CreateTestProfile();
        var suite = new ConstraintSuite
        {
            Name = "Test",
            Constraints =
            [
                new DataConstraint { Type = ConstraintType.RowCountBetween, MinValue = 500, MaxValue = 2000 },
                new DataConstraint { Type = ConstraintType.ColumnNotNull, ColumnName = "id" },
                new DataConstraint { Type = ConstraintType.ColumnNotNull, ColumnName = "name" }  // Fails - has nulls
            ]
        };

        var result = _validator.Validate(profile, suite);

        Assert.False(result.AllPassed);
        Assert.Equal(3, result.TotalConstraints);
        Assert.Equal(2, result.PassedConstraints);
        Assert.Equal(1, result.FailedConstraints);
    }

    [Fact]
    public void GenerateFromProfile_CreatesReasonableConstraints()
    {
        var profile = CreateTestProfile();
        
        var suite = _validator.GenerateFromProfile(profile);

        Assert.NotNull(suite);
        Assert.True(suite.Constraints.Count > 0);
        
        // Should have row count constraint
        Assert.Contains(suite.Constraints, c => c.Type == ConstraintType.RowCountBetween);
        
        // Should have column count constraint
        Assert.Contains(suite.Constraints, c => c.Type == ConstraintType.ColumnCountEquals);
        
        // Should have not-null constraint for columns with 0 nulls
        Assert.Contains(suite.Constraints, c => c.Type == ConstraintType.ColumnNotNull && c.ColumnName == "id");
        
        // Should have type constraints
        Assert.Contains(suite.Constraints, c => c.Type == ConstraintType.ColumnTypeIs);
    }

    [Fact]
    public void GenerateFromProfile_GeneratedConstraints_PassOnSameProfile()
    {
        var profile = CreateTestProfile();
        
        var suite = _validator.GenerateFromProfile(profile);
        var result = _validator.Validate(profile, suite);

        // All auto-generated constraints should pass against the profile they were generated from
        Assert.True(result.AllPassed, 
            $"Generated constraints should pass on source profile. Failures: {string.Join(", ", result.GetFailures().Select(f => f.Constraint.Description))}");
    }

    [Fact]
    public void Validate_NullPercentBelow_Passes()
    {
        var profile = CreateTestProfile();
        var suite = new ConstraintSuite
        {
            Name = "Test",
            Constraints =
            [
                new DataConstraint
                {
                    Type = ConstraintType.NullPercentBelow,
                    ColumnName = "name",
                    MaxValue = 10  // 5% nulls, should pass
                }
            ]
        };

        var result = _validator.Validate(profile, suite);

        Assert.True(result.AllPassed);
    }

    [Fact]
    public void Validate_NullPercentBelow_Fails()
    {
        var profile = CreateTestProfile();
        var suite = new ConstraintSuite
        {
            Name = "Test",
            Constraints =
            [
                new DataConstraint
                {
                    Type = ConstraintType.NullPercentBelow,
                    ColumnName = "name",
                    MaxValue = 2  // 5% nulls, should fail
                }
            ]
        };

        var result = _validator.Validate(profile, suite);

        Assert.False(result.AllPassed);
    }

    [Fact]
    public void GetFailures_ReturnsOnlyFailedConstraints()
    {
        var profile = CreateTestProfile();
        var suite = new ConstraintSuite
        {
            Name = "Test",
            Constraints =
            [
                new DataConstraint { Type = ConstraintType.ColumnNotNull, ColumnName = "id" },
                new DataConstraint { Type = ConstraintType.ColumnNotNull, ColumnName = "name" }
            ]
        };

        var result = _validator.Validate(profile, suite);
        var failures = result.GetFailures().ToList();

        Assert.Single(failures);
        Assert.Equal("name", failures[0].Constraint.ColumnName);
    }
}
