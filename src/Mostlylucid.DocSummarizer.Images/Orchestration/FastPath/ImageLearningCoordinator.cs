using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.DocSummarizer.Images.Orchestration.FastPath;

/// <summary>
///     Background learning coordinator for image analysis.
///     Queues learning tasks for async processing OUTSIDE the request path.
///
///     Keyed by signal type for parallel processing:
///     - "signature.update" - Update signature cache with refined results
///     - "ocr.quality" - Train OCR quality model on spellcheck results
///     - "caption.quality" - Train caption quality model
///     - "pattern.extraction" - Extract patterns from confident results
/// </summary>
public interface IImageLearningCoordinator
{
    /// <summary>
    ///     Queue full analysis for an image (after fast-path returned).
    /// </summary>
    void QueueFullAnalysis(string imagePath, string signatureKey, FastPathResult fastResult);

    /// <summary>
    ///     Queue refinement of an early-exit result.
    /// </summary>
    void QueueRefinement(string imagePath, string signatureKey, FastPathResult result);

    /// <summary>
    ///     Queue signature update from completed analysis.
    /// </summary>
    void QueueSignatureUpdate(string signatureKey, CachedImageSignature signature);

    /// <summary>
    ///     Queue OCR quality learning.
    /// </summary>
    void QueueOcrQualityLearning(OcrQualityFeatures features, bool isHighQuality);

    /// <summary>
    ///     Get statistics.
    /// </summary>
    LearningStats GetStats();
}

/// <summary>
///     OCR quality features for learning.
/// </summary>
public sealed record OcrQualityFeatures
{
    public int WordCount { get; init; }
    public int CharacterCount { get; init; }
    public double SpellcheckScore { get; init; }
    public double AverageWordLength { get; init; }
    public double DigitRatio { get; init; }
    public double UppercaseRatio { get; init; }
    public double WhitespaceRatio { get; init; }
    public int LineCount { get; init; }
    public double AverageConfidence { get; init; }
    public string? DetectedLanguage { get; init; }
}

/// <summary>
///     Learning statistics.
/// </summary>
public sealed record LearningStats
{
    public long TotalQueued { get; init; }
    public long TotalProcessed { get; init; }
    public long TotalFailed { get; init; }
    public int QueueDepth { get; init; }
    public double AverageProcessingTimeMs { get; init; }
}

/// <summary>
///     Background service that processes learning tasks.
/// </summary>
public sealed class ImageLearningCoordinator : BackgroundService, IImageLearningCoordinator
{
    private readonly Channel<LearningTask> _channel;
    private readonly IImageSignatureCache _signatureCache;
    private readonly ImageAnalysisOrchestrator _orchestrator;
    private readonly ILogger<ImageLearningCoordinator> _logger;

    private long _totalQueued;
    private long _totalProcessed;
    private long _totalFailed;
    private double _avgProcessingTimeMs;

    public ImageLearningCoordinator(
        IImageSignatureCache signatureCache,
        ImageAnalysisOrchestrator orchestrator,
        ILogger<ImageLearningCoordinator> logger)
    {
        _signatureCache = signatureCache;
        _orchestrator = orchestrator;
        _logger = logger;

        // Bounded channel to prevent memory issues
        _channel = Channel.CreateBounded<LearningTask>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public void QueueFullAnalysis(string imagePath, string signatureKey, FastPathResult fastResult)
    {
        var task = new LearningTask
        {
            Type = LearningTaskType.FullAnalysis,
            ImagePath = imagePath,
            SignatureKey = signatureKey,
            FastResult = fastResult
        };

        TryEnqueue(task);
    }

    public void QueueRefinement(string imagePath, string signatureKey, FastPathResult result)
    {
        var task = new LearningTask
        {
            Type = LearningTaskType.Refinement,
            ImagePath = imagePath,
            SignatureKey = signatureKey,
            FastResult = result
        };

        TryEnqueue(task);
    }

    public void QueueSignatureUpdate(string signatureKey, CachedImageSignature signature)
    {
        var task = new LearningTask
        {
            Type = LearningTaskType.SignatureUpdate,
            SignatureKey = signatureKey,
            Signature = signature
        };

        TryEnqueue(task);
    }

    public void QueueOcrQualityLearning(OcrQualityFeatures features, bool isHighQuality)
    {
        var task = new LearningTask
        {
            Type = LearningTaskType.OcrQuality,
            OcrFeatures = features,
            IsHighQuality = isHighQuality
        };

        TryEnqueue(task);
    }

    public LearningStats GetStats()
    {
        return new LearningStats
        {
            TotalQueued = _totalQueued,
            TotalProcessed = _totalProcessed,
            TotalFailed = _totalFailed,
            QueueDepth = _channel.Reader.Count,
            AverageProcessingTimeMs = _avgProcessingTimeMs
        };
    }

    private void TryEnqueue(LearningTask task)
    {
        if (_channel.Writer.TryWrite(task))
        {
            Interlocked.Increment(ref _totalQueued);
        }
        else
        {
            _logger.LogWarning("Learning queue full, dropping task: {Type}", task.Type);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Image learning coordinator started");

        await foreach (var task in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                await ProcessTaskAsync(task, stoppingToken);

                sw.Stop();
                Interlocked.Increment(ref _totalProcessed);

                // Update rolling average
                var total = _totalProcessed;
                _avgProcessingTimeMs = (_avgProcessingTimeMs * (total - 1) + sw.ElapsedMilliseconds) / total;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing learning task: {Type}", task.Type);
                Interlocked.Increment(ref _totalFailed);
            }
        }

        _logger.LogInformation("Image learning coordinator stopped");
    }

    private async Task ProcessTaskAsync(LearningTask task, CancellationToken ct)
    {
        switch (task.Type)
        {
            case LearningTaskType.FullAnalysis:
                await ProcessFullAnalysisAsync(task, ct);
                break;

            case LearningTaskType.Refinement:
                await ProcessRefinementAsync(task, ct);
                break;

            case LearningTaskType.SignatureUpdate:
                ProcessSignatureUpdate(task);
                break;

            case LearningTaskType.OcrQuality:
                ProcessOcrQualityLearning(task);
                break;
        }
    }

    private async Task ProcessFullAnalysisAsync(LearningTask task, CancellationToken ct)
    {
        if (task.ImagePath == null || task.SignatureKey == null)
            return;

        // Check if file still exists
        if (!File.Exists(task.ImagePath))
        {
            _logger.LogDebug("Image no longer exists, skipping full analysis: {Path}", task.ImagePath);
            return;
        }

        _logger.LogDebug("Running background full analysis for: {Path}", task.ImagePath);

        var result = await _orchestrator.AnalyzeAsync(task.ImagePath, ct);

        // Cache the full result
        var signature = new CachedImageSignature
        {
            SignatureKey = task.SignatureKey,
            Confidence = result.Confidence,
            Caption = result.Caption,
            OcrText = result.OcrText,
            DominantColor = result.DominantColor,
            Signals = result.Signals,
            ContributingWaves = result.CompletedWaves,
            IsComplete = true,
            OriginalProcessingTimeMs = result.ProcessingTimeMs,
            Width = result.GetSignal<int>(ImageSignalKeys.ImageWidth),
            Height = result.GetSignal<int>(ImageSignalKeys.ImageHeight),
            IsAnimated = result.GetSignal<bool>(ImageSignalKeys.IsAnimated)
        };

        _signatureCache.Set(task.SignatureKey, signature);

        _logger.LogDebug("Background analysis complete: {Path} ({Ms}ms)",
            Path.GetFileName(task.ImagePath), result.ProcessingTimeMs);
    }

    private async Task ProcessRefinementAsync(LearningTask task, CancellationToken ct)
    {
        // For early-exit results, run the remaining waves to improve confidence
        // This is a low-priority operation that improves future cache hits

        if (task.ImagePath == null || task.SignatureKey == null || task.FastResult == null)
            return;

        // Only refine if we have a partial result
        if (task.FastResult.IsComplete)
            return;

        _logger.LogDebug("Refining early-exit result for: {Path}", task.ImagePath);

        // Run full analysis
        var result = await _orchestrator.AnalyzeAsync(task.ImagePath, ct);

        // Update cache with refined result
        var signature = new CachedImageSignature
        {
            SignatureKey = task.SignatureKey,
            Confidence = Math.Max(result.Confidence, task.FastResult.Confidence),
            Caption = result.Caption ?? task.FastResult.Caption,
            OcrText = result.OcrText ?? task.FastResult.OcrText,
            DominantColor = result.DominantColor ?? task.FastResult.DominantColor,
            Signals = result.Signals,
            ContributingWaves = result.CompletedWaves,
            IsComplete = true,
            OriginalProcessingTimeMs = result.ProcessingTimeMs,
            Width = result.GetSignal<int>(ImageSignalKeys.ImageWidth),
            Height = result.GetSignal<int>(ImageSignalKeys.ImageHeight),
            IsAnimated = result.GetSignal<bool>(ImageSignalKeys.IsAnimated)
        };

        _signatureCache.Set(task.SignatureKey, signature);
    }

    private void ProcessSignatureUpdate(LearningTask task)
    {
        if (task.SignatureKey == null || task.Signature == null)
            return;

        // Update existing signature with new data
        var existing = _signatureCache.Get(task.SignatureKey);
        if (existing != null)
        {
            // Merge - take higher confidence values
            var merged = existing with
            {
                Confidence = Math.Max(existing.Confidence, task.Signature.Confidence),
                Support = existing.Support + 1,
                Caption = task.Signature.Caption ?? existing.Caption,
                OcrText = task.Signature.OcrText ?? existing.OcrText,
                IsComplete = existing.IsComplete || task.Signature.IsComplete
            };

            _signatureCache.Set(task.SignatureKey, merged);
        }
        else
        {
            _signatureCache.Set(task.SignatureKey, task.Signature);
        }
    }

    private void ProcessOcrQualityLearning(LearningTask task)
    {
        // TODO: Train OCR quality model
        // For now, just log for future implementation
        if (task.OcrFeatures != null)
        {
            _logger.LogDebug(
                "OCR quality learning: words={Words}, spellcheck={Spell:F2}, quality={Quality}",
                task.OcrFeatures.WordCount,
                task.OcrFeatures.SpellcheckScore,
                task.IsHighQuality);
        }
    }

    private sealed class LearningTask
    {
        public LearningTaskType Type { get; init; }
        public string? ImagePath { get; init; }
        public string? SignatureKey { get; init; }
        public FastPathResult? FastResult { get; init; }
        public CachedImageSignature? Signature { get; init; }
        public OcrQualityFeatures? OcrFeatures { get; init; }
        public bool IsHighQuality { get; init; }
    }

    private enum LearningTaskType
    {
        FullAnalysis,
        Refinement,
        SignatureUpdate,
        OcrQuality
    }
}
