using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;

namespace Mostlylucid.DocSummarizer.Images.Services;

/// <summary>
/// Background service for offline salience evolution.
/// Runs the FULL analysis pipeline with all LLMs to assess quality and improve signal weights.
/// This enables continuous improvement without blocking the main analysis pipeline.
/// </summary>
public class SalienceEvolutionService : IDisposable
{
    private readonly SalienceLearner _learner;
    private readonly ILogger<SalienceEvolutionService>? _logger;
    private readonly Channel<EvolutionJob> _jobQueue;
    private readonly CancellationTokenSource _cts = new();
    private Task? _processingTask;
    private bool _disposed;

    // Optional: full pipeline for re-analysis
    private Func<string, CancellationToken, Task<DynamicImageProfile?>>? _fullPipelineAnalyzer;

    // Metrics
    private int _processedCount;
    private int _improvedCount;
    private int _errorCount;
    private int _reanalyzedCount;

    public SalienceEvolutionService(SalienceLearner learner, ILogger<SalienceEvolutionService>? logger = null)
    {
        _learner = learner;
        _logger = logger;
        _jobQueue = Channel.CreateBounded<EvolutionJob>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    /// <summary>
    /// Configure the full pipeline analyzer for re-analysis.
    /// This allows running complete analysis with all LLMs in background.
    /// </summary>
    public void SetFullPipelineAnalyzer(Func<string, CancellationToken, Task<DynamicImageProfile?>> analyzer)
    {
        _fullPipelineAnalyzer = analyzer;
    }

    /// <summary>
    /// Start the background processing task.
    /// </summary>
    public void Start()
    {
        if (_processingTask != null)
            return;

        _processingTask = Task.Run(ProcessQueueAsync);
        _logger?.LogInformation("Salience evolution service started");
    }

    /// <summary>
    /// Stop the background processing task.
    /// </summary>
    public async Task StopAsync()
    {
        _cts.Cancel();
        _jobQueue.Writer.Complete();

        if (_processingTask != null)
        {
            await _processingTask;
        }

        _logger?.LogInformation("Salience evolution service stopped. Processed: {Processed}, Improved: {Improved}, Errors: {Errors}",
            _processedCount, _improvedCount, _errorCount);
    }

    /// <summary>
    /// Queue an image for background assessment and learning.
    /// </summary>
    /// <param name="profile">The analyzed image profile</param>
    /// <param name="generatedCaption">Caption generated during main analysis</param>
    /// <param name="purpose">Output purpose (alttext, caption, verbose, etc.)</param>
    /// <param name="embeddings">Optional pre-computed embeddings</param>
    /// <param name="runFullAnalysis">If true, runs the full LLM pipeline for comprehensive learning</param>
    public void QueueForEvolution(
        DynamicImageProfile profile,
        string? generatedCaption,
        string purpose = "caption",
        ImageEmbeddingSet? embeddings = null,
        bool runFullAnalysis = false)
    {
        var job = new EvolutionJob
        {
            Profile = profile,
            GeneratedCaption = generatedCaption,
            Purpose = purpose,
            Embeddings = embeddings ?? ImageEmbeddingSet.FromProfile(profile),
            QueuedAt = DateTime.UtcNow,
            RunFullAnalysis = runFullAnalysis
        };

        // Non-blocking queue
        _jobQueue.Writer.TryWrite(job);
    }

    /// <summary>
    /// Queue for evolution with full pipeline re-analysis.
    /// Use this for comprehensive learning on images where quality matters.
    /// </summary>
    public void QueueForFullEvolution(
        DynamicImageProfile profile,
        string? generatedCaption,
        string purpose = "caption")
    {
        QueueForEvolution(profile, generatedCaption, purpose, null, runFullAnalysis: true);
    }

    /// <summary>
    /// Process the job queue in the background.
    /// </summary>
    private async Task ProcessQueueAsync()
    {
        await foreach (var job in _jobQueue.Reader.ReadAllAsync(_cts.Token))
        {
            try
            {
                await ProcessJobAsync(job);
                Interlocked.Increment(ref _processedCount);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref _errorCount);
                _logger?.LogWarning(ex, "Error processing evolution job for {ImagePath}", job.Profile?.ImagePath);
            }
        }
    }

    /// <summary>
    /// Process a single evolution job - optionally re-analyze with full pipeline, assess, evaluate, and learn.
    /// </summary>
    private async Task ProcessJobAsync(EvolutionJob job)
    {
        if (job.Profile == null)
            return;

        var profile = job.Profile;
        var caption = job.GeneratedCaption;
        DynamicImageProfile? fullAnalysisProfile = null;

        // Step 0: If full pipeline is available, run comprehensive re-analysis
        if (_fullPipelineAnalyzer != null && !string.IsNullOrEmpty(job.Profile.ImagePath) && job.RunFullAnalysis)
        {
            try
            {
                _logger?.LogDebug("Running full pipeline re-analysis for: {ImagePath}", job.Profile.ImagePath);
                fullAnalysisProfile = await _fullPipelineAnalyzer(job.Profile.ImagePath, _cts.Token);

                if (fullAnalysisProfile != null)
                {
                    Interlocked.Increment(ref _reanalyzedCount);

                    // Use the full analysis for learning if successful
                    profile = fullAnalysisProfile;
                    caption = fullAnalysisProfile.GetValue<string>("vision.caption");

                    // Compare original vs full analysis to measure improvement potential
                    var originalQuality = AssessOutputQuality(job.Profile, job.GeneratedCaption, job.Purpose);
                    var fullQuality = AssessOutputQuality(fullAnalysisProfile, caption, job.Purpose);

                    if (fullQuality > originalQuality)
                    {
                        _logger?.LogInformation(
                            "Full analysis improved quality for {ImagePath}: {Original:F2} -> {Full:F2}",
                            job.Profile.ImagePath, originalQuality, fullQuality);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Full pipeline re-analysis failed for {ImagePath}, using original analysis",
                    job.Profile.ImagePath);
            }
        }

        // Step 1: Assess output quality using heuristics
        var qualityScore = AssessOutputQuality(profile, caption, job.Purpose);

        // Step 2: Determine which signals were most useful based on what's in the caption
        var usefulSignals = IdentifyUsefulSignals(profile, caption);

        // Step 3: If we have full analysis, compare signal coverage
        if (fullAnalysisProfile != null)
        {
            usefulSignals = CompareAndLearnSignals(job.Profile, fullAnalysisProfile, usefulSignals);
        }

        // Step 4: Adjust weights based on quality
        var adjustedSignals = AdjustWeightsByQuality(usefulSignals, qualityScore);

        // Step 5: Record feedback to the learner
        var embeddings = job.Embeddings ?? ImageEmbeddingSet.FromProfile(profile);
        if (embeddings != null)
        {
            _learner.RecordFeedbackWithEmbeddings(profile, embeddings, adjustedSignals);
        }
        else
        {
            _learner.RecordFeedback(profile, adjustedSignals);
        }

        // Step 6: Track improvements
        if (qualityScore >= 0.7)
        {
            Interlocked.Increment(ref _improvedCount);
        }

        _logger?.LogDebug("Evolution processed: {ImagePath}, Quality: {Quality:F2}, Signals: {SignalCount}, FullAnalysis: {FullAnalysis}",
            profile.ImagePath, qualityScore, adjustedSignals.Count, fullAnalysisProfile != null);
    }

    /// <summary>
    /// Compare original and full analysis to learn which signals made the difference.
    /// </summary>
    private Dictionary<string, double> CompareAndLearnSignals(
        DynamicImageProfile original,
        DynamicImageProfile fullAnalysis,
        Dictionary<string, double> baseSignals)
    {
        var result = new Dictionary<string, double>(baseSignals);

        // Check what signals the full analysis added or improved
        var signalCategories = new[]
        {
            ("subjects", "vision.subjects"),
            ("entities", "content.entities"),
            ("scene", "vision.scene"),
            ("motion", "motion.description"),
            ("text", "text.extracted"),
            ("caption", "vision.caption")
        };

        foreach (var (category, key) in signalCategories)
        {
            var originalValue = original.GetBestSignal(key);
            var fullValue = fullAnalysis.GetBestSignal(key);

            // If full analysis has higher confidence or better value, boost this signal
            if (fullValue != null && (originalValue == null || fullValue.Confidence > originalValue.Confidence))
            {
                var boost = fullValue.Confidence - (originalValue?.Confidence ?? 0);
                if (result.TryGetValue(category, out var current))
                {
                    result[category] = Math.Min(1.0, current + boost * 0.3);
                }
                else
                {
                    result[category] = boost;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Assess the quality of generated output using heuristics.
    /// Returns a score from 0 (poor) to 1 (excellent).
    /// </summary>
    private double AssessOutputQuality(DynamicImageProfile profile, string? caption, string purpose)
    {
        if (string.IsNullOrWhiteSpace(caption))
            return 0.1;

        var score = 0.5; // Base score

        // Length appropriateness
        var optimalLength = purpose switch
        {
            "alttext" => 125,
            "caption" => 200,
            "verbose" => 500,
            _ => 200
        };

        var lengthRatio = Math.Min(1.0, (double)caption.Length / optimalLength);
        score += lengthRatio * 0.15;

        // Check for prompt leakage (bad sign)
        var leakagePatterns = new[]
        {
            "based on the", "i can see", "the image shows", "this appears to be",
            "looking at", "from what i can"
        };

        var hasLeakage = leakagePatterns.Any(p => caption.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (hasLeakage)
        {
            score -= 0.2;
        }

        // Check for specificity (good sign) - mentions entities, subjects, actions
        var subjects = profile.GetValue<string>("vision.subjects");
        var entities = profile.GetValue<List<string>>("content.entities");

        if (!string.IsNullOrEmpty(subjects))
        {
            // Check if caption mentions any subjects
            var subjectWords = subjects.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var mentionedSubjects = subjectWords.Count(s => caption.Contains(s, StringComparison.OrdinalIgnoreCase));
            score += Math.Min(0.2, mentionedSubjects * 0.05);
        }

        // Check for action verbs (good for animated content)
        var isAnimated = profile.GetValue<bool>("identity.is_animated");
        if (isAnimated)
        {
            var actionWords = new[] { "moving", "walking", "running", "jumping", "waving", "dancing", "falling" };
            var hasAction = actionWords.Any(a => caption.Contains(a, StringComparison.OrdinalIgnoreCase));
            if (hasAction)
            {
                score += 0.1;
            }
        }

        // Penalty for generic/vague descriptions
        var vaguePhrases = new[] { "various", "some kind of", "appears to", "seems to", "might be" };
        var vagueCount = vaguePhrases.Count(p => caption.Contains(p, StringComparison.OrdinalIgnoreCase));
        score -= vagueCount * 0.05;

        return Math.Clamp(score, 0, 1);
    }

    /// <summary>
    /// Identify which signals from the profile appear to have been useful
    /// based on what content appears in the generated caption.
    /// </summary>
    private Dictionary<string, double> IdentifyUsefulSignals(DynamicImageProfile profile, string? caption)
    {
        var signals = new Dictionary<string, double>();

        if (string.IsNullOrWhiteSpace(caption))
        {
            // No caption = use defaults
            return GetDefaultSignalWeights();
        }

        var captionLower = caption.ToLowerInvariant();

        // Check subjects
        var subjects = profile.GetValue<string>("vision.subjects");
        if (!string.IsNullOrEmpty(subjects) &&
            subjects.Split(',').Any(s => captionLower.Contains(s.Trim().ToLowerInvariant())))
        {
            signals["subjects"] = 1.0;
        }
        else
        {
            signals["subjects"] = 0.3;
        }

        // Check entities
        var entities = profile.GetValue<List<string>>("content.entities");
        if (entities != null && entities.Any(e => captionLower.Contains(e.ToLowerInvariant())))
        {
            signals["entities"] = 1.0;
        }
        else
        {
            signals["entities"] = 0.3;
        }

        // Check scene/setting
        var scene = profile.GetValue<string>("vision.scene");
        if (!string.IsNullOrEmpty(scene) && captionLower.Contains(scene.ToLowerInvariant()))
        {
            signals["scene"] = 0.9;
        }
        else
        {
            signals["scene"] = 0.3;
        }

        // Check motion (for animated)
        var motion = profile.GetValue<string>("motion.description");
        var isAnimated = profile.GetValue<bool>("identity.is_animated");
        if (isAnimated)
        {
            var motionWords = new[] { "animation", "movement", "motion", "animated", "moving", "loop" };
            if (motionWords.Any(m => captionLower.Contains(m)))
            {
                signals["motion"] = 1.0;
            }
            else
            {
                signals["motion"] = 0.5; // Should be mentioned for animated
            }
        }
        else
        {
            signals["motion"] = 0.0;
        }

        // Check text (OCR)
        var ocrText = profile.GetValue<string>("text.extracted");
        if (!string.IsNullOrEmpty(ocrText))
        {
            var ocrWords = ocrText.Split(' ').Take(5); // First 5 words
            if (ocrWords.Any(w => w.Length > 3 && captionLower.Contains(w.ToLowerInvariant())))
            {
                signals["text"] = 0.9;
            }
            else
            {
                signals["text"] = 0.4;
            }
        }
        else
        {
            signals["text"] = 0.1;
        }

        // Colors - usually low weight unless very distinctive
        signals["colors"] = 0.2;

        // Quality - rarely useful for descriptions
        signals["quality"] = 0.1;

        // Identity - metadata, rarely useful
        signals["identity"] = 0.1;

        return signals;
    }

    /// <summary>
    /// Adjust signal weights based on overall quality score.
    /// High quality = boost weights, low quality = dampen weights.
    /// </summary>
    private Dictionary<string, double> AdjustWeightsByQuality(Dictionary<string, double> signals, double qualityScore)
    {
        // Adjustment factor based on quality
        // Quality 0.5 = no change, quality 1.0 = boost by 20%, quality 0.0 = reduce by 30%
        var factor = 0.7 + (qualityScore * 0.5);

        return signals.ToDictionary(
            kvp => kvp.Key,
            kvp => Math.Clamp(kvp.Value * factor, 0, 1)
        );
    }

    /// <summary>
    /// Default signal weights when no caption available.
    /// </summary>
    private static Dictionary<string, double> GetDefaultSignalWeights()
    {
        return new Dictionary<string, double>
        {
            ["subjects"] = 0.9,
            ["entities"] = 0.8,
            ["scene"] = 0.6,
            ["motion"] = 0.7,
            ["text"] = 0.5,
            ["colors"] = 0.3,
            ["quality"] = 0.2,
            ["identity"] = 0.2
        };
    }

    /// <summary>
    /// Get current evolution statistics.
    /// </summary>
    public EvolutionStatistics GetStatistics()
    {
        return new EvolutionStatistics
        {
            ProcessedCount = _processedCount,
            ImprovedCount = _improvedCount,
            ErrorCount = _errorCount,
            ReanalyzedCount = _reanalyzedCount,
            QueuedCount = _jobQueue.Reader.Count,
            LearnerStats = _learner.GetStatistics()
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _cts.Cancel();
        _cts.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Job for the evolution queue.
/// </summary>
public record EvolutionJob
{
    public DynamicImageProfile? Profile { get; init; }
    public string? GeneratedCaption { get; init; }
    public string Purpose { get; init; } = "caption";
    public ImageEmbeddingSet? Embeddings { get; init; }
    public DateTime QueuedAt { get; init; }
    public bool RunFullAnalysis { get; init; }
}

/// <summary>
/// Statistics about the evolution service.
/// </summary>
public record EvolutionStatistics
{
    public int ProcessedCount { get; init; }
    public int ImprovedCount { get; init; }
    public int ErrorCount { get; init; }
    public int ReanalyzedCount { get; init; }
    public int QueuedCount { get; init; }
    public LearnerStatistics? LearnerStats { get; init; }
}
