using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.DocSummarizer.Images.Orchestration.FastPath;

/// <summary>
///     Ultra-fast image analyzer that checks signature cache FIRST.
///     If signature found → returns in &lt;1ms
///     If not found → runs minimal analysis, queues learning for background
///
///     Architecture:
///     ```
///     Request → FastPath Check → HIT? → Return cached result (instant)
///                    ↓
///                   MISS
///                    ↓
///     Minimal Analysis (fast lane only: identity + color + text detection)
///                    ↓
///     Return minimal result + Queue full analysis for background learning
///     ```
/// </summary>
public sealed class FastPathImageAnalyzer
{
    private readonly IImageSignatureCache _signatureCache;
    private readonly ImageAnalysisOrchestrator _orchestrator;
    private readonly IImageLearningCoordinator _learningCoordinator;
    private readonly ILogger<FastPathImageAnalyzer> _logger;
    private readonly FastPathOptions _options;

    public FastPathImageAnalyzer(
        IImageSignatureCache signatureCache,
        ImageAnalysisOrchestrator orchestrator,
        IImageLearningCoordinator learningCoordinator,
        ILogger<FastPathImageAnalyzer> logger,
        FastPathOptions? options = null)
    {
        _signatureCache = signatureCache;
        _orchestrator = orchestrator;
        _learningCoordinator = learningCoordinator;
        _logger = logger;
        _options = options ?? new FastPathOptions();
    }

    /// <summary>
    ///     Analyze image with fast-path optimization.
    ///     Returns cached result instantly if available.
    /// </summary>
    public async Task<FastPathResult> AnalyzeAsync(
        string imagePath,
        AnalysisMode mode = AnalysisMode.Auto,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        // Step 1: Compute signature key (fast - only reads first 64KB + perceptual hash)
        var signatureKey = await _signatureCache.ComputeSignatureKeyAsync(imagePath, cancellationToken);
        var combinedKey = signatureKey.CombinedKey;

        // Step 2a: Check exact content hash first (instant)
        var cached = _signatureCache.Get(combinedKey);

        // Step 2b: If no exact match, try perceptual hash similarity
        if (cached == null)
        {
            cached = _signatureCache.FindSimilar(signatureKey.PerceptualHash, _options.MaxHammingDistance);
            if (cached != null)
            {
                _logger.LogDebug(
                    "Fast-path HIT (perceptual): {Path} → similar image found",
                    Path.GetFileName(imagePath));
            }
        }

        if (cached != null)
        {
            stopwatch.Stop();

            _logger.LogDebug(
                "Fast-path HIT: {Path} → cached result in {Ms}ms (original took {OriginalMs}ms)",
                Path.GetFileName(imagePath), stopwatch.ElapsedMilliseconds, cached.OriginalProcessingTimeMs);

            return new FastPathResult
            {
                SignatureKey = combinedKey,
                IsCacheHit = true,
                ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                OriginalProcessingTimeMs = cached.OriginalProcessingTimeMs,
                Confidence = cached.Confidence,
                Caption = cached.Caption,
                OcrText = cached.OcrText,
                DominantColor = cached.DominantColor,
                ColorPalette = cached.ColorPalette,
                IsAnimated = cached.IsAnimated,
                Width = cached.Width,
                Height = cached.Height,
                ContentType = cached.ContentType,
                Signals = cached.Signals,
                ContributingWaves = cached.ContributingWaves,
                IsComplete = cached.IsComplete
            };
        }

        // Step 3: Cache miss - decide analysis strategy based on mode
        _logger.LogDebug("Fast-path MISS: {Key} - running {Mode} analysis", combinedKey, mode);

        FastPathResult result;

        switch (mode)
        {
            case AnalysisMode.FastOnly:
                // Run only fast-lane waves, return immediately
                result = await RunFastLaneOnlyAsync(imagePath, combinedKey, stopwatch, cancellationToken);
                // Queue full analysis for background learning
                _learningCoordinator.QueueFullAnalysis(imagePath, combinedKey, result);
                break;

            case AnalysisMode.Full:
                // Run full analysis synchronously
                result = await RunFullAnalysisAsync(imagePath, combinedKey, stopwatch, cancellationToken);
                break;

            case AnalysisMode.Auto:
            default:
                // Auto: run fast lane, if confident enough return immediately
                // Otherwise continue with more waves
                result = await RunAutoAnalysisAsync(imagePath, combinedKey, stopwatch, cancellationToken);
                break;
        }

        stopwatch.Stop();
        result = result with { ProcessingTimeMs = stopwatch.ElapsedMilliseconds };

        // Cache the result for next time (if confident enough)
        if (result.Confidence >= _options.MinCacheConfidence && result.IsComplete)
        {
            CacheResult(combinedKey, signatureKey.PerceptualHash, result);
        }

        _logger.LogDebug(
            "Fast-path analysis complete: {Key} - {Mode}, {Ms}ms, confidence={Confidence:F2}",
            combinedKey, mode, stopwatch.ElapsedMilliseconds, result.Confidence);

        return result;
    }

    private async Task<FastPathResult> RunFastLaneOnlyAsync(
        string imagePath,
        string signatureKey,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        // Use orchestrator with fast-lane-only options
        var fastOptions = new ImageAnalysisOptions
        {
            MaxParallelism = 4,
            TotalTimeout = TimeSpan.FromSeconds(5), // Fast timeout
            EnableEarlyExit = true
        };

        // TODO: Filter to fast-lane waves only
        var result = await _orchestrator.AnalyzeAsync(imagePath, ct);

        return new FastPathResult
        {
            SignatureKey = signatureKey,
            IsCacheHit = false,
            ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
            Confidence = result.Confidence,
            Caption = result.Caption,
            OcrText = result.OcrText,
            DominantColor = result.DominantColor,
            Signals = result.Signals,
            ContributingWaves = result.CompletedWaves,
            IsComplete = false, // Fast-lane is not complete
            EarlyExit = result.EarlyExit,
            EarlyExitReason = result.EarlyExitReason
        };
    }

    private async Task<FastPathResult> RunFullAnalysisAsync(
        string imagePath,
        string signatureKey,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var result = await _orchestrator.AnalyzeAsync(imagePath, ct);

        return ConvertToFastPathResult(signatureKey, result, stopwatch.ElapsedMilliseconds);
    }

    private async Task<FastPathResult> RunAutoAnalysisAsync(
        string imagePath,
        string combinedKey,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        // Run full analysis - the orchestrator handles early exit
        var result = await _orchestrator.AnalyzeAsync(imagePath, ct);

        var fastResult = ConvertToFastPathResult(combinedKey, result, stopwatch.ElapsedMilliseconds);

        // If we got early exit (high confidence), queue background refinement
        if (result.EarlyExit && fastResult.Confidence >= _options.MinCacheConfidence)
        {
            _learningCoordinator.QueueRefinement(imagePath, combinedKey, fastResult);
        }

        return fastResult;
    }

    private FastPathResult ConvertToFastPathResult(
        string signatureKey,
        ImageAnalysisResult result,
        long processingTimeMs)
    {
        return new FastPathResult
        {
            SignatureKey = signatureKey,
            IsCacheHit = false,
            ProcessingTimeMs = processingTimeMs,
            Confidence = result.Confidence,
            Caption = result.Caption,
            OcrText = result.OcrText,
            DominantColor = result.DominantColor,
            Signals = result.Signals,
            ContributingWaves = result.CompletedWaves,
            IsComplete = !result.EarlyExit,
            EarlyExit = result.EarlyExit,
            EarlyExitReason = result.EarlyExitReason,
            Width = result.GetSignal<int>(ImageSignalKeys.ImageWidth),
            Height = result.GetSignal<int>(ImageSignalKeys.ImageHeight),
            IsAnimated = result.GetSignal<bool>(ImageSignalKeys.IsAnimated),
            ColorPalette = result.GetSignal<IReadOnlyList<string>>(ImageSignalKeys.ColorPalette)
        };
    }

    private void CacheResult(string signatureKey, string perceptualHash, FastPathResult result)
    {
        // Store perceptual hash in signals for indexing
        var signals = new Dictionary<string, object>(result.Signals)
        {
            ["_perceptual_hash"] = perceptualHash
        };

        var cached = new CachedImageSignature
        {
            SignatureKey = signatureKey,
            Confidence = result.Confidence,
            Caption = result.Caption,
            OcrText = result.OcrText,
            DominantColor = result.DominantColor,
            ColorPalette = result.ColorPalette,
            IsAnimated = result.IsAnimated,
            Width = result.Width,
            Height = result.Height,
            ContentType = result.ContentType,
            Signals = signals,
            ContributingWaves = result.ContributingWaves,
            IsComplete = result.IsComplete,
            OriginalProcessingTimeMs = result.ProcessingTimeMs,
            LastAccessedAt = DateTimeOffset.UtcNow
        };

        _signatureCache.Set(signatureKey, cached);
    }
}

/// <summary>
///     Analysis mode for fast-path analyzer.
/// </summary>
public enum AnalysisMode
{
    /// <summary>
    ///     Automatically choose: fast lane first, extend if needed.
    /// </summary>
    Auto,

    /// <summary>
    ///     Fast lane only - return immediately, queue learning.
    /// </summary>
    FastOnly,

    /// <summary>
    ///     Full analysis - run all waves synchronously.
    /// </summary>
    Full
}

/// <summary>
///     Result from fast-path analysis.
/// </summary>
public sealed record FastPathResult
{
    public required string SignatureKey { get; init; }
    public required bool IsCacheHit { get; init; }
    public long ProcessingTimeMs { get; init; }
    public long OriginalProcessingTimeMs { get; init; }
    public double Confidence { get; init; }

    // Core results
    public string? Caption { get; init; }
    public string? OcrText { get; init; }
    public string? DominantColor { get; init; }
    public IReadOnlyList<string>? ColorPalette { get; init; }

    // Image metadata
    public bool IsAnimated { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? ContentType { get; init; }

    // Full signals
    public IReadOnlyDictionary<string, object> Signals { get; init; } =
        new Dictionary<string, object>();

    public IReadOnlySet<string> ContributingWaves { get; init; } = new HashSet<string>();

    // Completion status
    public bool IsComplete { get; init; }
    public bool EarlyExit { get; init; }
    public string? EarlyExitReason { get; init; }
}

/// <summary>
///     Options for fast-path analyzer.
/// </summary>
public sealed class FastPathOptions
{
    /// <summary>
    ///     Minimum confidence to cache results.
    /// </summary>
    public double MinCacheConfidence { get; init; } = 0.7;

    /// <summary>
    ///     Timeout for fast-lane analysis.
    /// </summary>
    public TimeSpan FastLaneTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Maximum signatures to cache.
    /// </summary>
    public int MaxCacheSize { get; init; } = 10000;

    /// <summary>
    ///     How long to keep cached signatures.
    /// </summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    ///     Maximum hamming distance for perceptual hash similarity.
    ///     Lower = stricter matching (5 is good for resized/recompressed variants).
    /// </summary>
    public int MaxHammingDistance { get; init; } = 5;
}
