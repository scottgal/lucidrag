using Xunit;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;

namespace Mostlylucid.DocSummarizer.Images.Tests.Models.Dynamic;

public class SignalAggregatorTests
{
    [Fact]
    public void Aggregate_WithHighestConfidence_ShouldReturnMostConfidentValue()
    {
        // Arrange
        var signals = new List<Signal>
        {
            new Signal { Key = "test", Value = "low", Confidence = 0.5, Source = "A" },
            new Signal { Key = "test", Value = "high", Confidence = 0.9, Source = "B" },
            new Signal { Key = "test", Value = "medium", Confidence = 0.7, Source = "C" }
        };

        // Act
        var result = SignalAggregator.Aggregate(signals, AggregationStrategy.HighestConfidence);

        // Assert
        Assert.Equal("high", result);
    }

    [Fact]
    public void Aggregate_WithMostRecent_ShouldReturnNewestValue()
    {
        // Arrange
        var baseTime = DateTime.UtcNow;
        var signals = new List<Signal>
        {
            new Signal { Key = "test", Value = "old", Confidence = 1.0, Source = "A", Timestamp = baseTime.AddMinutes(-2) },
            new Signal { Key = "test", Value = "new", Confidence = 1.0, Source = "B", Timestamp = baseTime },
            new Signal { Key = "test", Value = "middle", Confidence = 1.0, Source = "C", Timestamp = baseTime.AddMinutes(-1) }
        };

        // Act
        var result = SignalAggregator.Aggregate(signals, AggregationStrategy.MostRecent);

        // Assert
        Assert.Equal("new", result);
    }

    [Fact]
    public void Aggregate_WithWeightedAverage_ForNumeric_ShouldReturnWeightedMean()
    {
        // Arrange
        var signals = new List<Signal>
        {
            new Signal { Key = "test", Value = 10.0, Confidence = 0.8, Source = "A" },
            new Signal { Key = "test", Value = 20.0, Confidence = 0.6, Source = "B" },
            new Signal { Key = "test", Value = 30.0, Confidence = 0.4, Source = "C" }
        };

        // Act
        var result = SignalAggregator.Aggregate(signals, AggregationStrategy.WeightedAverage);

        // Assert
        Assert.IsType<double>(result);
        var weightedAvg = (double)result!;

        // Expected: (10*0.8 + 20*0.6 + 30*0.4) / (0.8 + 0.6 + 0.4) = 32/1.8 = 17.78
        Assert.True(Math.Abs(weightedAvg - 17.78) < 0.1);
    }

    [Fact]
    public void Aggregate_WithWeightedAverage_ForNonNumeric_ShouldReturnNull()
    {
        // Arrange
        var signals = new List<Signal>
        {
            new Signal { Key = "test", Value = "text1", Confidence = 0.5, Source = "A" },
            new Signal { Key = "test", Value = "text2", Confidence = 0.9, Source = "B" }
        };

        // Act
        var result = SignalAggregator.Aggregate(signals, AggregationStrategy.WeightedAverage);

        // Assert - WeightedAverage only works for numeric values
        Assert.Null(result);
    }

    [Fact]
    public void Aggregate_WithMajorityVote_ShouldReturnMostCommonValue()
    {
        // Arrange
        var signals = new List<Signal>
        {
            new Signal { Key = "test", Value = "A", Confidence = 1.0, Source = "S1" },
            new Signal { Key = "test", Value = "A", Confidence = 1.0, Source = "S2" },
            new Signal { Key = "test", Value = "B", Confidence = 1.0, Source = "S3" }
        };

        // Act
        var result = SignalAggregator.Aggregate(signals, AggregationStrategy.MajorityVote);

        // Assert
        Assert.Equal("A", result);
    }

    [Fact]
    public void Aggregate_WithCollect_ShouldReturnAllValues()
    {
        // Arrange
        var signals = new List<Signal>
        {
            new Signal { Key = "test", Value = "A", Confidence = 1.0, Source = "S1" },
            new Signal { Key = "test", Value = "B", Confidence = 1.0, Source = "S2" },
            new Signal { Key = "test", Value = "C", Confidence = 1.0, Source = "S3" }
        };

        // Act
        var result = SignalAggregator.Aggregate(signals, AggregationStrategy.Collect);

        // Assert
        Assert.IsType<List<object>>(result);
        var list = (List<object>)result!;
        Assert.Equal(3, list.Count);
        Assert.Contains("A", list);
        Assert.Contains("B", list);
        Assert.Contains("C", list);
    }

    [Fact]
    public void Aggregate_WithEmptySignals_ShouldReturnNull()
    {
        // Arrange
        var signals = new List<Signal>();

        // Act
        var result = SignalAggregator.Aggregate(signals, AggregationStrategy.HighestConfidence);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void MergeSignals_WithHighestConfidenceStrategy_ShouldMergeCorrectly()
    {
        // Arrange
        var signals = new List<Signal>
        {
            new Signal { Key = "test", Value = "low", Confidence = 0.3, Source = "A" },
            new Signal { Key = "test", Value = "high", Confidence = 0.9, Source = "B" }
        };

        // Act
        var result = SignalAggregator.MergeSignals(signals, "merged.test", "MergeTest", AggregationStrategy.HighestConfidence);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("high", result.Value);
        Assert.Equal(0.6, result.Confidence); // Average of 0.3 and 0.9
    }

    [Fact]
    public void ResolveConflict_WithTrustNewerData_ShouldReturnNewest()
    {
        // Arrange
        var baseTime = DateTime.UtcNow;
        var signals = new List<Signal>
        {
            new Signal { Key = "test", Value = "old", Confidence = 1.0, Source = "A", Timestamp = baseTime.AddMinutes(-1) },
            new Signal { Key = "test", Value = "new", Confidence = 1.0, Source = "B", Timestamp = baseTime }
        };

        // Act
        var result = SignalAggregator.ResolveConflict(signals, ConflictResolutionRule.TrustNewerData);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("new", result.Value);
    }

    [Fact]
    public void ResolveConflict_WithTrustHighestConfidence_ShouldReturnMostConfident()
    {
        // Arrange
        var signals = new List<Signal>
        {
            new Signal { Key = "test", Value = "A", Confidence = 0.8, Source = "S1" },
            new Signal { Key = "test", Value = "B", Confidence = 0.7, Source = "S2" }
        };

        // Act
        var result = SignalAggregator.ResolveConflict(signals, ConflictResolutionRule.TrustHighestConfidence);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("A", result.Value);
    }
}
