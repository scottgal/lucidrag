namespace Mostlylucid.DocSummarizer.Images.Models.Dynamic;

/// <summary>
/// Aggregates multiple signals for the same key using various strategies.
/// </summary>
public static class SignalAggregator
{
    /// <summary>
    /// Aggregate signals using the specified strategy.
    /// </summary>
    public static object? Aggregate(IEnumerable<Signal> signals, AggregationStrategy strategy)
    {
        var signalsList = signals.ToList();
        if (!signalsList.Any()) return null;
        if (signalsList.Count == 1) return signalsList[0].Value;

        return strategy switch
        {
            AggregationStrategy.HighestConfidence => AggregateByHighestConfidence(signalsList),
            AggregationStrategy.MostRecent => AggregateByMostRecent(signalsList),
            AggregationStrategy.WeightedAverage => AggregateByWeightedAverage(signalsList),
            AggregationStrategy.MajorityVote => AggregateByMajorityVote(signalsList),
            AggregationStrategy.Collect => AggregateByCollect(signalsList),
            _ => AggregateByHighestConfidence(signalsList)
        };
    }

    /// <summary>
    /// Return value from signal with highest confidence.
    /// </summary>
    private static object? AggregateByHighestConfidence(List<Signal> signals)
    {
        return signals.OrderByDescending(s => s.Confidence).First().Value;
    }

    /// <summary>
    /// Return value from most recent signal.
    /// </summary>
    private static object? AggregateByMostRecent(List<Signal> signals)
    {
        return signals.OrderByDescending(s => s.Timestamp).First().Value;
    }

    /// <summary>
    /// Calculate weighted average of numeric values.
    /// Returns null if values are not numeric.
    /// </summary>
    private static object? AggregateByWeightedAverage(List<Signal> signals)
    {
        var numericSignals = signals
            .Where(s => IsNumeric(s.Value))
            .ToList();

        if (!numericSignals.Any()) return null;

        var totalWeight = numericSignals.Sum(s => s.Confidence);
        if (totalWeight == 0) return null;

        var weightedSum = numericSignals.Sum(s =>
        {
            var value = Convert.ToDouble(s.Value);
            return value * s.Confidence;
        });

        return weightedSum / totalWeight;
    }

    /// <summary>
    /// Return most common value, weighted by confidence.
    /// </summary>
    private static object? AggregateByMajorityVote(List<Signal> signals)
    {
        var groups = signals
            .GroupBy(s => s.Value?.ToString() ?? "null")
            .Select(g => new
            {
                Value = g.First().Value,
                TotalConfidence = g.Sum(s => s.Confidence)
            })
            .OrderByDescending(x => x.TotalConfidence)
            .FirstOrDefault();

        return groups?.Value;
    }

    /// <summary>
    /// Collect all values into a list.
    /// </summary>
    private static object AggregateByCollect(List<Signal> signals)
    {
        return signals.Select(s => s.Value).ToList();
    }

    /// <summary>
    /// Check if a value is numeric.
    /// </summary>
    private static bool IsNumeric(object? value)
    {
        return value is int or long or float or double or decimal or short or byte;
    }

    /// <summary>
    /// Custom aggregation with user-defined function.
    /// </summary>
    public static object? AggregateCustom(
        IEnumerable<Signal> signals,
        Func<IEnumerable<Signal>, object?> aggregator)
    {
        return aggregator(signals);
    }

    /// <summary>
    /// Merge signals from multiple sources with conflict resolution.
    /// </summary>
    public static Signal MergeSignals(
        IEnumerable<Signal> signals,
        string resultKey,
        string resultSource,
        AggregationStrategy strategy = AggregationStrategy.HighestConfidence)
    {
        var signalsList = signals.ToList();
        if (!signalsList.Any())
        {
            throw new ArgumentException("Cannot merge empty signal collection", nameof(signals));
        }

        var aggregatedValue = Aggregate(signalsList, strategy);
        var avgConfidence = signalsList.Average(s => s.Confidence);

        return new Signal
        {
            Key = resultKey,
            Value = aggregatedValue,
            Confidence = avgConfidence,
            Source = resultSource,
            Timestamp = DateTime.UtcNow,
            Metadata = new Dictionary<string, object>
            {
                ["merged_from"] = signalsList.Select(s => s.Source).ToList(),
                ["merge_strategy"] = strategy.ToString(),
                ["original_count"] = signalsList.Count
            }
        };
    }

    /// <summary>
    /// Resolve conflicts between signals by applying domain-specific rules.
    /// </summary>
    public static Signal ResolveConflict(
        IEnumerable<Signal> conflictingSignals,
        ConflictResolutionRule rule)
    {
        var signals = conflictingSignals.ToList();

        return rule switch
        {
            ConflictResolutionRule.TrustHigherPriority => signals.OrderByDescending(s =>
            {
                // Assume metadata contains priority
                if (s.Metadata?.TryGetValue("priority", out var priority) == true)
                {
                    return Convert.ToInt32(priority);
                }
                return 0;
            }).First(),

            ConflictResolutionRule.TrustNewerData => signals.OrderByDescending(s => s.Timestamp).First(),

            ConflictResolutionRule.AverageNumeric => MergeSignals(signals, signals.First().Key, "ConflictResolver", AggregationStrategy.WeightedAverage),

            ConflictResolutionRule.ConsensusVoting => MergeSignals(signals, signals.First().Key, "ConflictResolver", AggregationStrategy.MajorityVote),

            _ => signals.OrderByDescending(s => s.Confidence).First()
        };
    }
}

/// <summary>
/// Rules for resolving conflicts between signals.
/// </summary>
public enum ConflictResolutionRule
{
    /// <summary>
    /// Trust signal from higher priority source.
    /// </summary>
    TrustHigherPriority,

    /// <summary>
    /// Trust more recent signal.
    /// </summary>
    TrustNewerData,

    /// <summary>
    /// Average numeric values.
    /// </summary>
    AverageNumeric,

    /// <summary>
    /// Use consensus/voting.
    /// </summary>
    ConsensusVoting,

    /// <summary>
    /// Trust signal with highest confidence.
    /// </summary>
    TrustHighestConfidence
}
