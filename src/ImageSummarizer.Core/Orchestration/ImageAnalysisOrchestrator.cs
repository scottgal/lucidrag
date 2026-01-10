using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using SixLabors.ImageSharp;

namespace Mostlylucid.DocSummarizer.Images.Orchestration;

/// <summary>
///     Orchestrates image analysis waves using the blackboard architecture.
///     Follows the BotDetection EphemeralDetectionOrchestrator pattern.
/// </summary>
/// <remarks>
///     **Architecture:**
///     ```
///     Image → Load → Waves (parallel/staged) → DetectionLedger → ImageAnalysisResult
///                          ↓
///                    Logging (coordination)
///                          ↓
///                    Learning System
///     ```
/// </remarks>
public sealed class ImageAnalysisOrchestrator
{
    private readonly IEnumerable<IContributingWave> _waves;
    private readonly ILogger<ImageAnalysisOrchestrator> _logger;
    private readonly ImageAnalysisOptions _options;

    public ImageAnalysisOrchestrator(
        IEnumerable<IContributingWave> waves,
        ILogger<ImageAnalysisOrchestrator> logger,
        ImageAnalysisOptions? options = null)
    {
        _waves = waves.Where(w => w.IsEnabled).OrderBy(w => w.Priority).ToList();
        _logger = logger;
        _options = options ?? new ImageAnalysisOptions();
    }

    /// <summary>
    ///     Analyze an image and return aggregated results.
    /// </summary>
    public async Task<ImageAnalysisResult> AnalyzeAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var stopwatch = Stopwatch.StartNew();
        var ledger = new ImageAnalysisLedger(sessionId, imagePath);

        _logger.LogDebug("Starting image analysis session {SessionId} for {Path}",
            sessionId, imagePath);

        // Load the image once for all waves
        Image? loadedImage = null;
        byte[]? imageBytes = null;
        string? mimeType = null;

        try
        {
            imageBytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
            loadedImage = Image.Load(imageBytes);
            mimeType = GetMimeType(imagePath);

            // Add identity signals
            ledger.RecordSignal(ImageSignalKeys.ImageWidth, loadedImage.Width);
            ledger.RecordSignal(ImageSignalKeys.ImageHeight, loadedImage.Height);
            ledger.RecordSignal(ImageSignalKeys.ImageSize, imageBytes.Length);
            ledger.RecordSignal(ImageSignalKeys.ImageFormat, mimeType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load image {Path}", imagePath);

            return new ImageAnalysisResult
            {
                SessionId = sessionId,
                ImagePath = imagePath,
                Success = false,
                Error = ex.Message,
                ProcessingTimeMs = stopwatch.ElapsedMilliseconds
            };
        }

        var completedWaves = new ConcurrentBag<string>();
        var failedWaves = new ConcurrentBag<string>();
        var allSignals = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Group waves by trigger requirements
            var wavesWithoutTriggers = _waves.Where(w => !w.TriggerConditions.Any()).ToList();
            var wavesWithTriggers = _waves.Where(w => w.TriggerConditions.Any()).ToList();

            // Run first-wave (no triggers) in parallel
            await RunWaveGroupAsync(
                wavesWithoutTriggers,
                imagePath,
                loadedImage,
                imageBytes,
                mimeType,
                sessionId,
                ledger,
                allSignals,
                completedWaves,
                failedWaves,
                stopwatch,
                cancellationToken);

            // Update system signals
            allSignals[ImageSignalKeys.CompletedWaves] = completedWaves.Count;

            // Run triggered waves in batches until no more can run
            var remainingWaves = new List<IContributingWave>(wavesWithTriggers);
            var maxIterations = 10; // Prevent infinite loops

            for (var iteration = 0; iteration < maxIterations && remainingWaves.Count > 0; iteration++)
            {
                // Find waves whose triggers are now satisfied
                var readyWaves = remainingWaves
                    .Where(w => AreTriggersSatisfied(w, allSignals))
                    .ToList();

                if (readyWaves.Count == 0)
                {
                    // No more waves can run - check for early exit or timeout
                    if (ledger.EarlyExit)
                    {
                        _logger.LogDebug("Early exit triggered in session {SessionId}", sessionId);
                        break;
                    }

                    // Wait a bit for signals if under budget
                    if (stopwatch.Elapsed < _options.TotalTimeout)
                    {
                        await Task.Delay(50, cancellationToken);
                        continue;
                    }

                    break;
                }

                // Remove ready waves from remaining
                foreach (var wave in readyWaves)
                    remainingWaves.Remove(wave);

                // Run ready waves in parallel
                await RunWaveGroupAsync(
                    readyWaves,
                    imagePath,
                    loadedImage,
                    imageBytes,
                    mimeType,
                    sessionId,
                    ledger,
                    allSignals,
                    completedWaves,
                    failedWaves,
                    stopwatch,
                    cancellationToken);

                // Update system signals
                allSignals[ImageSignalKeys.CompletedWaves] = completedWaves.Count;

                // Check for early exit
                if (ledger.EarlyExit)
                {
                    _logger.LogDebug("Early exit triggered in session {SessionId}", sessionId);
                    break;
                }

                // Check total timeout
                if (stopwatch.Elapsed >= _options.TotalTimeout)
                {
                    _logger.LogWarning("Session {SessionId} exceeded total timeout", sessionId);
                    break;
                }
            }

            // Log any waves that couldn't run due to unmet triggers
            foreach (var wave in remainingWaves)
            {
                _logger.LogDebug("Wave {Wave} skipped - triggers not satisfied", wave.Name);
            }
        }
        finally
        {
            loadedImage?.Dispose();
        }

        stopwatch.Stop();

        _logger.LogInformation(
            "Image analysis session {SessionId} completed: {Completed} waves, {Failed} failed, {Ms}ms",
            sessionId, completedWaves.Count, failedWaves.Count, stopwatch.ElapsedMilliseconds);

        return ledger.ToResult(stopwatch.ElapsedMilliseconds, completedWaves.ToHashSet(), failedWaves.ToHashSet());
    }

    private async Task RunWaveGroupAsync(
        IReadOnlyList<IContributingWave> waves,
        string imagePath,
        Image? loadedImage,
        byte[]? imageBytes,
        string? mimeType,
        string sessionId,
        ImageAnalysisLedger ledger,
        ConcurrentDictionary<string, object> allSignals,
        ConcurrentBag<string> completedWaves,
        ConcurrentBag<string> failedWaves,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (waves.Count == 0) return;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _options.MaxParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(waves, parallelOptions, async (wave, ct) =>
        {
            // Check routing skip
            if (allSignals.TryGetValue($"{ImageSignalKeys.RouteSkipPrefix}{wave.Name}", out var skipValue) &&
                skipValue is true)
            {
                _logger.LogDebug("Wave {Wave} skipped by routing", wave.Name);
                return;
            }

            // Build current state snapshot
            var state = new ImageBlackboardState
            {
                ImagePath = imagePath,
                Signals = allSignals.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                CurrentConfidence = ledger.Confidence,
                CompletedWaves = completedWaves.ToHashSet(),
                FailedWaves = failedWaves.ToHashSet(),
                Contributions = ledger.Contributions,
                SessionId = sessionId,
                Elapsed = stopwatch.Elapsed,
                LoadedImage = loadedImage,
                ImageBytes = imageBytes,
                MimeType = mimeType
            };

            try
            {
                // Run with timeout
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(wave.ExecutionTimeout);

                var waveStopwatch = Stopwatch.StartNew();

                var contributions = await wave.ContributeAsync(state, timeoutCts.Token);

                waveStopwatch.Stop();

                // Record contributions
                foreach (var contribution in contributions)
                {
                    var withTiming = contribution with { ProcessingTimeMs = waveStopwatch.ElapsedMilliseconds };
                    ledger.AddContribution(withTiming);

                    // Extract signals from contribution
                    foreach (var (key, value) in contribution.Signals)
                    {
                        allSignals[key] = value;
                    }
                }

                completedWaves.Add(wave.Name);

                _logger.LogDebug("Wave {Wave} completed with {Count} contributions in {Ms}ms",
                    wave.Name, contributions.Count, waveStopwatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Wave timed out
                _logger.LogWarning("Wave {Wave} timed out after {Timeout}",
                    wave.Name, wave.ExecutionTimeout);

                failedWaves.Add(wave.Name);
                ledger.RecordFailure(wave.Name, "Timeout");
            }
            catch (Exception ex) when (wave.IsOptional)
            {
                // Optional wave failed - log and continue
                _logger.LogWarning(ex, "Optional wave {Wave} failed", wave.Name);

                failedWaves.Add(wave.Name);
                ledger.RecordFailure(wave.Name, ex.Message);
            }
            catch (Exception ex)
            {
                // Required wave failed - log but continue (other waves may compensate)
                _logger.LogError(ex, "Required wave {Wave} failed", wave.Name);

                failedWaves.Add(wave.Name);
                ledger.RecordFailure(wave.Name, ex.Message);
            }
        });
    }

    private static bool AreTriggersSatisfied(IContributingWave wave, IReadOnlyDictionary<string, object> signals)
    {
        return wave.TriggerConditions.All(t => t.IsSatisfied(signals));
    }

    private static string GetMimeType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".tiff" or ".tif" => "image/tiff",
            _ => "application/octet-stream"
        };
    }
}

/// <summary>
///     Options for image analysis orchestration.
/// </summary>
public sealed class ImageAnalysisOptions
{
    /// <summary>
    ///     Maximum parallelism for wave execution.
    /// </summary>
    public int MaxParallelism { get; init; } = 4;

    /// <summary>
    ///     Total timeout for the entire analysis session.
    /// </summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Whether to enable early exit when confident results are achieved.
    /// </summary>
    public bool EnableEarlyExit { get; init; } = true;
}

/// <summary>
///     Image-specific detection ledger.
/// </summary>
public sealed class ImageAnalysisLedger : DetectionLedger
{
    private readonly ConcurrentDictionary<string, object> _signals = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);

    public ImageAnalysisLedger(string sessionId, string imagePath)
        : base(sessionId, ComputeFingerprint(imagePath))
    {
        ImagePath = imagePath;
    }

    public string ImagePath { get; }

    /// <summary>
    ///     All signals recorded.
    /// </summary>
    public IReadOnlyDictionary<string, object> AllSignals => _signals;

    /// <summary>
    ///     Record a signal value.
    /// </summary>
    public void RecordSignal(string key, object value)
    {
        _signals[key] = value;
        Record(key, value, 0.5, "orchestrator");
    }

    /// <summary>
    ///     Record a wave failure.
    /// </summary>
    public void RecordFailure(string waveName, string reason)
    {
        _failures[waveName] = reason;
        base.RecordFailure(waveName);
    }

    /// <summary>
    ///     Convert to final result.
    /// </summary>
    public ImageAnalysisResult ToResult(
        long processingTimeMs,
        IReadOnlySet<string> completedWaves,
        IReadOnlySet<string> failedWaves)
    {
        return new ImageAnalysisResult
        {
            SessionId = RequestId,
            ImagePath = ImagePath,
            Success = true,
            ProcessingTimeMs = processingTimeMs,
            Contributions = Contributions,
            Signals = _signals.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            CompletedWaves = completedWaves,
            FailedWaves = failedWaves,
            FailureReasons = _failures.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Confidence = Confidence,
            EarlyExit = EarlyExit,
            EarlyExitReason = EarlyExitContribution?.Reason
        };
    }

    private static string ComputeFingerprint(string imagePath)
    {
        // Simple fingerprint based on path - could be enhanced with content hash
        return Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(imagePath)))[..16];
    }
}

/// <summary>
///     Result of image analysis.
/// </summary>
public sealed class ImageAnalysisResult
{
    public required string SessionId { get; init; }
    public required string ImagePath { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public long ProcessingTimeMs { get; init; }
    public IReadOnlyList<DetectionContribution> Contributions { get; init; } = Array.Empty<DetectionContribution>();
    public IReadOnlyDictionary<string, object> Signals { get; init; } = new Dictionary<string, object>();
    public IReadOnlySet<string> CompletedWaves { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> FailedWaves { get; init; } = new HashSet<string>();
    public IReadOnlyDictionary<string, string> FailureReasons { get; init; } = new Dictionary<string, string>();
    public double Confidence { get; init; }
    public bool EarlyExit { get; init; }
    public string? EarlyExitReason { get; init; }

    /// <summary>
    ///     Get a typed signal value.
    /// </summary>
    public T? GetSignal<T>(string key)
    {
        return Signals.TryGetValue(key, out var value) && value is T typed ? typed : default;
    }

    /// <summary>
    ///     Get the caption (from vision waves).
    /// </summary>
    public string? Caption => GetSignal<string>(ImageSignalKeys.Caption);

    /// <summary>
    ///     Get extracted text (from OCR waves).
    /// </summary>
    public string? OcrText => GetSignal<string>(ImageSignalKeys.OcrText);

    /// <summary>
    ///     Get dominant color (from color wave).
    /// </summary>
    public string? DominantColor => GetSignal<string>(ImageSignalKeys.ColorDominant);
}

/// <summary>
///     Well-known signal types for the SignalSink.
/// </summary>
public static class Signal
{
    public const string ImageAnalysisStarted = "image.analysis.started";
    public const string ImageAnalysisCompleted = "image.analysis.completed";
    public const string ImageAnalysisFailed = "image.analysis.failed";
    public const string WaveStarted = "wave.started";
    public const string WaveCompleted = "wave.completed";
    public const string WaveFailed = "wave.failed";
    public const string EscalationTriggered = "escalation.triggered";
}
