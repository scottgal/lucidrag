using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Services.Storage;
using System.Collections.Concurrent;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Tracks signal effectiveness with decay-based learning
/// Maintains immutable ledger of discriminator scores and learns which signals
/// reliably separate good from bad results
/// </summary>
public class SignalEffectivenessTracker
{
    private readonly ILogger<SignalEffectivenessTracker> _logger;
    private readonly ISignalDatabase _database;

    // In-memory cache of effectiveness weights (periodically synced with database)
    private readonly ConcurrentDictionary<string, DiscriminatorEffectiveness> _effectivenessCache = new();

    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public SignalEffectivenessTracker(
        ILogger<SignalEffectivenessTracker> logger,
        ISignalDatabase database)
    {
        _logger = logger;
        _database = database;
    }

    /// <summary>
    /// Record a discriminator score to the immutable ledger
    /// </summary>
    public async Task RecordScoreAsync(DiscriminatorScore score, CancellationToken ct = default)
    {
        await _database.StoreDiscriminatorScoreAsync(score, ct);

        _logger.LogDebug(
            "Recorded discriminator score {Id} for {ImageHash} (Overall: {Score:F3}, Accepted: {Accepted})",
            score.Id,
            score.ImageHash,
            score.OverallScore,
            score.Accepted?.ToString() ?? "PENDING");
    }

    /// <summary>
    /// Update discriminator effectiveness weights based on feedback
    /// </summary>
    public async Task UpdateEffectivenessAsync(DiscriminatorScore score, CancellationToken ct = default)
    {
        if (score.Accepted == null)
        {
            _logger.LogDebug("Skipping effectiveness update for score {Id} (no feedback)", score.Id);
            return;
        }

        await _cacheLock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;

            foreach (var (signalName, contribution) in score.SignalContributions)
            {
                var key = GetEffectivenessKey(signalName, score.ImageType, score.Goal);

                // Load or create effectiveness record
                var effectiveness = _effectivenessCache.GetOrAdd(key, _ =>
                    LoadOrCreateEffectiveness(signalName, score.ImageType, score.Goal));

                // Apply time-based decay before update
                var decayedWeight = effectiveness.GetDecayedWeight(now);

                // Update counts based on agreement
                var agreed = DidSignalAgreeWithOutcome(contribution, score.Accepted.Value, score.OverallScore);

                var updatedEffectiveness = effectiveness with
                {
                    Weight = CalculateNewWeight(decayedWeight, agreed, effectiveness.EvaluationCount),
                    EvaluationCount = effectiveness.EvaluationCount + 1,
                    AgreementCount = effectiveness.AgreementCount + (agreed ? 1 : 0),
                    DisagreementCount = effectiveness.DisagreementCount + (agreed ? 0 : 1),
                    LastEvaluated = now
                };

                _effectivenessCache[key] = updatedEffectiveness;

                // Persist to database
                await _database.UpdateDiscriminatorEffectivenessAsync(updatedEffectiveness, ct);

                _logger.LogDebug(
                    "Updated effectiveness for {SignalName}/{ImageType}/{Goal}: " +
                    "Weight {OldWeight:F3} → {NewWeight:F3}, Agreement: {AgreementRate:P0} ({AgreementCount}/{EvalCount})",
                    signalName,
                    score.ImageType,
                    score.Goal,
                    decayedWeight,
                    updatedEffectiveness.Weight,
                    updatedEffectiveness.AgreementRate,
                    updatedEffectiveness.AgreementCount,
                    updatedEffectiveness.EvaluationCount);

                // Retire discriminator if weight too low
                if (updatedEffectiveness.ShouldRetire(now))
                {
                    _logger.LogWarning(
                        "Discriminator {SignalName}/{ImageType}/{Goal} retired due to low weight ({Weight:F3})",
                        signalName,
                        score.ImageType,
                        score.Goal,
                        updatedEffectiveness.GetDecayedWeight(now));

                    _effectivenessCache.TryRemove(key, out _);
                    await _database.RetireDiscriminatorAsync(signalName, score.ImageType, score.Goal, ct);
                }
            }
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Get effectiveness weight for a specific signal/type/goal combination
    /// </summary>
    public async Task<double> GetEffectivenessWeightAsync(
        string signalName,
        ImageType imageType,
        string goal,
        CancellationToken ct = default)
    {
        var key = GetEffectivenessKey(signalName, imageType, goal);

        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_effectivenessCache.TryGetValue(key, out var effectiveness))
            {
                return effectiveness.GetDecayedWeight(DateTimeOffset.UtcNow);
            }

            // Load from database
            var loaded = await _database.GetDiscriminatorEffectivenessAsync(signalName, imageType, goal, ct);
            if (loaded != null)
            {
                _effectivenessCache[key] = loaded;
                return loaded.GetDecayedWeight(DateTimeOffset.UtcNow);
            }

            return 1.0; // Neutral prior for new discriminators
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Get all effectiveness records for a specific image type and goal
    /// Useful for understanding which signals are most valuable
    /// </summary>
    public async Task<List<DiscriminatorEffectiveness>> GetTopDiscriminatorsAsync(
        ImageType imageType,
        string goal,
        int limit = 10,
        CancellationToken ct = default)
    {
        var allEffectiveness = await _database.GetAllDiscriminatorEffectivenessAsync(imageType, goal, ct);

        var now = DateTimeOffset.UtcNow;

        return allEffectiveness
            .OrderByDescending(e => e.GetDecayedWeight(now))
            .ThenByDescending(e => e.AgreementRate)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Get prior scores for an image (for novelty comparison)
    /// </summary>
    public async Task<List<DiscriminatorScore>> GetPriorScoresAsync(
        string imageHash,
        int limit = 5,
        CancellationToken ct = default)
    {
        return await _database.GetDiscriminatorScoresAsync(imageHash, limit, ct);
    }

    /// <summary>
    /// Prune discriminators with weights below threshold across all types/goals
    /// Should be run periodically (e.g., daily) to clean up ineffective discriminators
    /// </summary>
    public async Task PruneIneffectiveDiscriminatorsAsync(
        double threshold = 0.1,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var pruneCount = 0;

        await _cacheLock.WaitAsync(ct);
        try
        {
            var toPrune = _effectivenessCache
                .Where(kvp => kvp.Value.ShouldRetire(now, threshold))
                .ToList();

            foreach (var (key, effectiveness) in toPrune)
            {
                _effectivenessCache.TryRemove(key, out _);
                await _database.RetireDiscriminatorAsync(
                    effectiveness.SignalName,
                    effectiveness.ImageType,
                    effectiveness.Goal,
                    ct);
                pruneCount++;
            }
        }
        finally
        {
            _cacheLock.Release();
        }

        if (pruneCount > 0)
        {
            _logger.LogInformation("Pruned {PruneCount} ineffective discriminators", pruneCount);
        }
    }

    /// <summary>
    /// Get statistics about discriminator learning progress
    /// </summary>
    public async Task<DiscriminatorStats> GetStatsAsync(CancellationToken ct = default)
    {
        var totalScores = await _database.GetTotalScoreCountAsync(ct);
        var totalWithFeedback = await _database.GetTotalFeedbackCountAsync(ct);

        await _cacheLock.WaitAsync(ct);
        try
        {
            var activeDiscriminators = _effectivenessCache.Count;
            var avgWeight = _effectivenessCache.Values.Average(e => e.Weight);
            var avgAgreementRate = _effectivenessCache.Values.Average(e => e.AgreementRate);

            return new DiscriminatorStats
            {
                TotalScores = totalScores,
                TotalWithFeedback = totalWithFeedback,
                ActiveDiscriminators = activeDiscriminators,
                AverageWeight = avgWeight,
                AverageAgreementRate = avgAgreementRate
            };
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    // Private helpers

    private string GetEffectivenessKey(string signalName, ImageType imageType, string goal)
    {
        return $"{signalName}|{imageType}|{goal}";
    }

    private DiscriminatorEffectiveness LoadOrCreateEffectiveness(
        string signalName,
        ImageType imageType,
        string goal)
    {
        // Try to load from database (will be null for new discriminators)
        var loaded = _database.GetDiscriminatorEffectivenessAsync(signalName, imageType, goal, CancellationToken.None)
            .GetAwaiter().GetResult();

        if (loaded != null)
            return loaded;

        // Create new with neutral prior
        return new DiscriminatorEffectiveness
        {
            SignalName = signalName,
            ImageType = imageType,
            Goal = goal,
            Weight = 1.0, // Start at full weight
            EvaluationCount = 0,
            AgreementCount = 0,
            DisagreementCount = 0,
            LastEvaluated = DateTimeOffset.UtcNow,
            DecayRate = 0.95 // 5% decay per day
        };
    }

    /// <summary>
    /// Determine if a signal "agreed" with the outcome
    /// Agreement = signal's contribution aligned with acceptance/rejection
    /// </summary>
    private bool DidSignalAgreeWithOutcome(SignalContribution contribution, bool accepted, double overallScore)
    {
        // High-strength contributions to good results = agreement
        // Low-strength contributions to bad results = agreement
        // High-strength contributions to bad results = disagreement
        // Low-strength contributions to good results = disagreement

        var threshold = 0.5;

        if (accepted && overallScore > threshold)
        {
            // Good result: high-strength signals agreed
            return contribution.Strength > threshold;
        }
        else if (!accepted && overallScore <= threshold)
        {
            // Bad result: low-strength signals agreed (correctly indicated poor quality)
            return contribution.Strength <= threshold;
        }
        else if (accepted && overallScore <= threshold)
        {
            // Edge case: accepted despite low score
            // Likely user correction - trust user, so low-strength = disagreement
            return contribution.Strength <= threshold;
        }
        else // !accepted && overallScore > threshold
        {
            // Edge case: rejected despite high score
            // User found issue not captured by signals - high strength = disagreement
            return contribution.Strength <= threshold;
        }
    }

    /// <summary>
    /// Calculate new weight using exponential moving average with decay
    /// </summary>
    private double CalculateNewWeight(double currentWeight, bool agreed, int priorEvaluations)
    {
        // Learning rate decreases as evaluations increase (stabilizes over time)
        var learningRate = 1.0 / Math.Max(1, Math.Sqrt(priorEvaluations + 1));

        // Update weight based on agreement
        var delta = agreed ? learningRate : -learningRate;

        // New weight with bounds [0.0, 2.0] (can exceed 1.0 for highly effective signals)
        var newWeight = Math.Clamp(currentWeight + delta, 0.0, 2.0);

        return newWeight;
    }
}

/// <summary>
/// Statistics about discriminator learning
/// </summary>
public record DiscriminatorStats
{
    public int TotalScores { get; init; }
    public int TotalWithFeedback { get; init; }
    public int ActiveDiscriminators { get; init; }
    public double AverageWeight { get; init; }
    public double AverageAgreementRate { get; init; }
}
