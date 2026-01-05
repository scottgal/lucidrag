using Microsoft.Extensions.Logging;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Batching;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;

namespace Mostlylucid.DocSummarizer.Images.Services.Coordinators;

/// <summary>
/// Evolution coordinator using mostlylucid.ephemeral atoms.
/// Leverages signal-driven processing, batching, and bounded concurrency.
/// </summary>
public class EphemeralEvolutionCoordinator : IAsyncDisposable
{
    private readonly SalienceLearner _learner;
    private readonly ILogger? _logger;
    private readonly SignalSink _signals;
    private readonly BatchingAtom<EvolutionWorkItem> _batchingAtom;
    private readonly EphemeralWorkCoordinator<EvolutionBatch> _workCoordinator;

    // Full pipeline analyzer (optional)
    private Func<string, CancellationToken, Task<DynamicImageProfile?>>? _fullAnalyzer;

    // Signals
    public const string SignalEvolutionQueued = "evolution.queued";
    public const string SignalEvolutionBatchReady = "evolution.batch.ready";
    public const string SignalEvolutionComplete = "evolution.complete";
    public const string SignalEvolutionError = "evolution.error";

    public EphemeralEvolutionCoordinator(
        SalienceLearner learner,
        ILogger? logger = null,
        int batchSize = 20,
        TimeSpan? batchTimeout = null)
    {
        _learner = learner;
        _logger = logger;

        // Create signal sink for coordination
        _signals = new SignalSink();

        // Create work coordinator with concurrency limit
        _workCoordinator = new EphemeralWorkCoordinator<EvolutionBatch>(
            body: ProcessBatchAsync,
            options: new EphemeralOptions { MaxConcurrency = 2, Signals = _signals });

        // Create batching atom - collects items and emits batches
        _batchingAtom = new BatchingAtom<EvolutionWorkItem>(
            onBatch: async (batch, ct) =>
            {
                _signals.Raise(SignalEvolutionBatchReady);
                await _workCoordinator.EnqueueAsync(new EvolutionBatch { Items = batch.ToList() });
            },
            maxBatchSize: batchSize,
            flushInterval: batchTimeout ?? TimeSpan.FromMinutes(5));

        // Subscribe to signals for logging
        _signals.Subscribe(signal =>
        {
            if (signal.Is(SignalEvolutionComplete))
            {
                _logger?.LogInformation("Evolution batch complete: {Signal}", signal.Signal);
            }
        });
    }

    /// <summary>
    /// Set full pipeline analyzer for comprehensive evolution.
    /// </summary>
    public void SetFullAnalyzer(Func<string, CancellationToken, Task<DynamicImageProfile?>> analyzer)
    {
        _fullAnalyzer = analyzer;
    }

    /// <summary>
    /// Queue an image for evolution learning.
    /// Items are batched automatically based on size/timeout.
    /// </summary>
    public void Queue(
        DynamicImageProfile profile,
        string? caption,
        string purpose = "caption",
        bool runFullAnalysis = false)
    {
        var item = new EvolutionWorkItem
        {
            Profile = profile,
            Caption = caption,
            Purpose = purpose,
            RunFullAnalysis = runFullAnalysis,
            QueuedAt = DateTime.UtcNow
        };

        _batchingAtom.Enqueue(item);
        _signals.Raise(SignalEvolutionQueued, key: profile.ImagePath);
    }

    /// <summary>
    /// Process a batch of evolution items.
    /// </summary>
    private async Task ProcessBatchAsync(EvolutionBatch batch, CancellationToken ct)
    {
        var result = new EvolutionBatchResult();

        foreach (var item in batch.Items)
        {
            try
            {
                var itemResult = await ProcessItemAsync(item, ct);
                result.ProcessedCount++;

                if (itemResult.Success)
                {
                    result.ImprovedCount++;
                    result.TotalQuality += itemResult.QualityScore;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.ErrorCount++;
                _signals.Raise(SignalEvolutionError, key: item.Profile?.ImagePath);
                _logger?.LogWarning(ex, "Evolution failed for {Path}", item.Profile?.ImagePath);
            }
        }

        result.AverageQuality = result.ProcessedCount > 0 ? result.TotalQuality / result.ProcessedCount : 0;
        _signals.Raise(SignalEvolutionComplete, key: $"{result.ProcessedCount}:{result.ImprovedCount}");
    }

    /// <summary>
    /// Process a single evolution item.
    /// </summary>
    private async Task<EvolutionItemResult> ProcessItemAsync(EvolutionWorkItem item, CancellationToken ct)
    {
        if (item.Profile == null)
            return new EvolutionItemResult { Success = false };

        var profile = item.Profile;
        var caption = item.Caption;

        // Run full analysis if requested
        if (item.RunFullAnalysis && _fullAnalyzer != null && !string.IsNullOrEmpty(profile.ImagePath))
        {
            var fullProfile = await _fullAnalyzer(profile.ImagePath, ct);
            if (fullProfile != null)
            {
                profile = fullProfile;
                caption = fullProfile.GetValue<string>("vision.caption");
            }
        }

        // Assess and learn
        var quality = AssessQuality(profile, caption, item.Purpose);
        var signals = IdentifyUsefulSignals(profile, caption, quality);

        var embeddings = ImageEmbeddingSet.FromProfile(profile);
        _learner.RecordFeedbackWithEmbeddings(profile, embeddings, signals);

        return new EvolutionItemResult
        {
            Success = true,
            QualityScore = quality,
            SignalsLearned = signals.Count
        };
    }

    private double AssessQuality(DynamicImageProfile profile, string? caption, string purpose)
    {
        if (string.IsNullOrWhiteSpace(caption)) return 0.2;

        var score = 0.5;
        var optimalLength = purpose switch { "alttext" => 125, "verbose" => 500, _ => 200 };
        score += Math.Min(0.2, (double)caption.Length / optimalLength * 0.2);

        if (caption.Contains("based on", StringComparison.OrdinalIgnoreCase))
            score -= 0.15;

        var subjects = profile.GetValue<string>("vision.subjects");
        if (!string.IsNullOrEmpty(subjects) &&
            subjects.Split(',').Any(s => caption.Contains(s.Trim(), StringComparison.OrdinalIgnoreCase)))
            score += 0.15;

        return Math.Clamp(score, 0, 1);
    }

    private Dictionary<string, double> IdentifyUsefulSignals(
        DynamicImageProfile profile, string? caption, double quality)
    {
        var signals = new Dictionary<string, double>
        {
            ["subjects"] = 0.8,
            ["entities"] = 0.7,
            ["scene"] = 0.5,
            ["motion"] = profile.GetValue<bool>("identity.is_animated") ? 0.9 : 0.1,
            ["text"] = !string.IsNullOrEmpty(profile.GetValue<string>("text.extracted")) ? 0.7 : 0.2,
            ["colors"] = 0.3
        };

        var factor = 0.7 + quality * 0.6;
        return signals.ToDictionary(k => k.Key, k => Math.Clamp(k.Value * factor, 0, 1));
    }

    /// <summary>
    /// Get signal sink for external subscription.
    /// </summary>
    public SignalSink Signals => _signals;

    public async ValueTask DisposeAsync()
    {
        await _batchingAtom.DisposeAsync();
        await _workCoordinator.DisposeAsync();
    }
}

/// <summary>
/// Work item for evolution processing.
/// </summary>
public record EvolutionWorkItem
{
    public DynamicImageProfile? Profile { get; init; }
    public string? Caption { get; init; }
    public string Purpose { get; init; } = "caption";
    public bool RunFullAnalysis { get; init; }
    public DateTime QueuedAt { get; init; }
}

/// <summary>
/// Batch of evolution items for processing.
/// </summary>
public record EvolutionBatch
{
    public List<EvolutionWorkItem> Items { get; init; } = new();
}

/// <summary>
/// Result from processing a single item.
/// </summary>
public record EvolutionItemResult
{
    public bool Success { get; init; }
    public double QualityScore { get; init; }
    public int SignalsLearned { get; init; }
}

/// <summary>
/// Result from processing a batch.
/// </summary>
public record EvolutionBatchResult
{
    public int ProcessedCount { get; set; }
    public int ImprovedCount { get; set; }
    public int ErrorCount { get; set; }
    public double TotalQuality { get; set; }
    public double AverageQuality { get; set; }
}
