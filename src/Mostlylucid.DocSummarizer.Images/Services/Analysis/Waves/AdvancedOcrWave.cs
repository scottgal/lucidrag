using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Ocr;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.FrameStabilization;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.PostProcessing;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.Preprocessing;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.Voting;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Diagnostics;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Advanced OCR wave for multi-frame GIF/WebP processing.
/// Implements temporal processing, stabilization, voting, and post-correction.
/// Only activates for animated images when UseAdvancedPipeline is enabled.
///
/// Priority: 59 (runs after OcrWave at 60, can enhance or replace simple OCR)
/// </summary>
public class AdvancedOcrWave : IAnalysisWave
{
    private readonly IOcrEngine _ocrEngine;
    private readonly OcrConfig _config;
    private readonly ILogger<AdvancedOcrWave>? _logger;

    public string Name => "AdvancedOcrWave";
    public int Priority => 59; // Runs after simple OcrWave
    public IReadOnlyList<string> Tags => new[] { SignalTags.Content, "ocr", "advanced" };

    /// <summary>
    /// Check if advanced OCR should run. Respects auto-routing.
    /// </summary>
    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        // Skip if auto-routing says to skip this wave
        if (context.IsWaveSkippedByRouting(Name))
            return false;

        // Skip if advanced pipeline is disabled
        if (!_config.UseAdvancedPipeline)
            return false;

        return true;
    }

    public AdvancedOcrWave(
        IOcrEngine ocrEngine,
        IOptions<ImageConfig> imageConfig,
        ILogger<AdvancedOcrWave>? logger = null)
    {
        _ocrEngine = ocrEngine;
        _config = imageConfig.Value.Ocr;
        _logger = logger;

        // Apply quality mode presets on construction
        _config.ApplyQualityModePresets();
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        // Check if advanced pipeline is enabled
        if (!_config.UseAdvancedPipeline)
        {
            signals.Add(new Signal
            {
                Key = "ocr.advanced.enabled",
                Value = false,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "config" }
            });
            return signals;
        }

        // Check if this is an animated image
        if (!IsAnimatedImage(imagePath))
        {
            signals.Add(new Signal
            {
                Key = "ocr.advanced.skipped",
                Value = true,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr" },
                Metadata = new Dictionary<string, object>
                {
                    ["reason"] = "Not an animated image (GIF/WebP)"
                }
            });
            return signals;
        }

        // Check text-likeliness threshold (use 0 or negative threshold to disable check)
        var textLikeliness = context.GetValue<double>("content.text_likeliness");
        var threshold = _config.TextDetectionConfidenceThreshold >= 0
            ? _config.TextDetectionConfidenceThreshold
            : 0.3;

        // Only skip if threshold is positive AND text-likeliness is below it
        if (threshold > 0 && textLikeliness < threshold)
        {
            signals.Add(new Signal
            {
                Key = "ocr.advanced.skipped",
                Value = true,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr" },
                Metadata = new Dictionary<string, object>
                {
                    ["reason"] = "Low text-likeliness score",
                    ["text_likeliness"] = textLikeliness,
                    ["threshold"] = threshold
                }
            });
            return signals;
        }

        var sw = Stopwatch.StartNew();

        try
        {
            // Phase 1: Extract frames
            var frameSignals = await ExtractFramesPhaseAsync(imagePath, context, ct);
            signals.AddRange(frameSignals);

            var frames = context.GetCached<List<Image<Rgba32>>>("ocr.frames");
            if (frames == null || frames.Count == 0)
            {
                signals.Add(CreateErrorSignal("Frame extraction failed or produced no frames"));
                return signals;
            }

            // Phase 2: Stabilization (if enabled and multiple frames)
            if (_config.EnableStabilization && frames.Count > 1)
            {
                var stabilizationSignals = await StabilizationPhaseAsync(frames, context, ct);
                signals.AddRange(stabilizationSignals);
            }

            // Phase 3: Temporal Median (if enabled and multiple frames)
            Image<Rgba32>? medianComposite = null;
            if (_config.EnableTemporalMedian && frames.Count > 1)
            {
                var medianSignals = await TemporalMedianPhaseAsync(frames, context, ct);
                signals.AddRange(medianSignals);
                medianComposite = context.GetCached<Image<Rgba32>>("ocr.temporal_median");
            }

            // Phase 4: OCR on temporal median composite (or first frame)
            var ocrImage = medianComposite ?? frames[0];
            var (ocrSignals, primaryConfidence) = await OcrPhaseAsync(ocrImage, "temporal_median", context, ct);
            signals.AddRange(ocrSignals);

            // Early exit check
            if (primaryConfidence >= _config.ConfidenceThresholdForEarlyExit)
            {
                signals.Add(new Signal
                {
                    Key = "ocr.advanced.early_exit",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "ocr", "optimization" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["primary_confidence"] = primaryConfidence,
                        ["threshold"] = _config.ConfidenceThresholdForEarlyExit,
                        ["phases_skipped"] = "temporal_voting, post_correction"
                    }
                });

                sw.Stop();
                signals.Add(CreatePerformanceSignal(sw.ElapsedMilliseconds, "early_exit"));

                _logger?.LogInformation(
                    "Advanced OCR early exit: confidence {Confidence:F3} >= threshold {Threshold:F3}",
                    primaryConfidence, _config.ConfidenceThresholdForEarlyExit);

                return signals;
            }

            // Phase 5: Temporal Voting (if enabled and multiple frames)
            if (_config.EnableTemporalVoting && frames.Count > 1)
            {
                var votingSignals = await TemporalVotingPhaseAsync(frames, context, ct);
                signals.AddRange(votingSignals);
            }

            // Phase 6: Post-Correction (if enabled)
            if (_config.EnablePostCorrection)
            {
                var correctionSignals = await PostCorrectionPhaseAsync(context, ct);
                signals.AddRange(correctionSignals);
            }

            sw.Stop();
            signals.Add(CreatePerformanceSignal(sw.ElapsedMilliseconds, "complete"));

            _logger?.LogInformation(
                "Advanced OCR completed for {ImagePath}: {FrameCount} frames, {Duration}ms, quality mode: {QualityMode}",
                Path.GetFileName(imagePath), frames.Count, sw.ElapsedMilliseconds, _config.QualityMode);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Advanced OCR failed for {ImagePath}", imagePath);
            signals.Add(CreateErrorSignal(ex.Message));
        }

        return signals;
    }

    private async Task<List<Signal>> ExtractFramesPhaseAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct)
    {
        var signals = new List<Signal>();
        var sw = Stopwatch.StartNew();

        var frames = new List<Image<Rgba32>>();

        // Check if MlOcrWave already detected which frames have text changes
        var mlTextChangedIndices = context.GetCached<List<int>>("ocr.ml.text_changed_indices");

        using (var image = await Image.LoadAsync<Rgba32>(imagePath, ct))
        {
            var frameCount = image.Frames.Count;

            // Get OpenCV per-frame text regions if available (from MlOcrWave)
            var perFrameRegions = context.GetCached<Dictionary<int, List<Dictionary<string, int>>>>("ocr.opencv.per_frame_regions");

            // If MlOcrWave already identified text-changed frames, use those
            if (mlTextChangedIndices != null && mlTextChangedIndices.Count > 0)
            {
                _logger?.LogDebug("Using {Count} MlOcrWave text-changed frame indices", mlTextChangedIndices.Count);

                foreach (var idx in mlTextChangedIndices)
                {
                    if (idx >= 0 && idx < frameCount)
                    {
                        var frame = image.Frames.CloneFrame(idx);
                        frames.Add(frame);
                    }
                }

                signals.Add(new Signal
                {
                    Key = "ocr.frames.ml_filtered",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "ocr", "preprocessing", "opencv" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["total_frames"] = frameCount,
                        ["text_changed_frames"] = frames.Count,
                        ["method"] = "MlOcrWave_text_change_detection"
                    }
                });

                // Further deduplicate using actual text content comparison
                // Use text similarity threshold (default 0.85) - lower than SSIM to catch text variations
                var textThreshold = _config.TextSimilarityDeduplicationThreshold > 0
                    ? _config.TextSimilarityDeduplicationThreshold
                    : 0.85;

                if (frames.Count > 1 && textThreshold > 0)
                {
                    var beforeTextDedup = frames.Count;
                    frames = await DeduplicateFramesUsingTextContentAsync(
                        frames, perFrameRegions, textThreshold, ct);

                    if (frames.Count < beforeTextDedup)
                    {
                        signals.Add(new Signal
                        {
                            Key = "ocr.frames.text_content_dedup",
                            Value = true,
                            Confidence = 1.0,
                            Source = Name,
                            Tags = new List<string> { "ocr", "preprocessing", "smart_dedup" },
                            Metadata = new Dictionary<string, object>
                            {
                                ["before_dedup"] = beforeTextDedup,
                                ["after_dedup"] = frames.Count,
                                ["removed"] = beforeTextDedup - frames.Count,
                                ["method"] = "text_content_similarity"
                            }
                        });
                    }
                }
            }
            else
            {
                // Fallback: Extract all frames
                for (int i = 0; i < frameCount; i++)
                {
                    var frame = image.Frames.CloneFrame(i);
                    frames.Add(frame);
                }

                // Smart deduplication: prefer text-content comparison if we have OpenCV regions
                // Use lower text threshold (0.85) to catch text variations like "mean" vs "means"
                var textThreshold = _config.TextSimilarityDeduplicationThreshold > 0
                    ? _config.TextSimilarityDeduplicationThreshold
                    : 0.85;

                if (frameCount > 1 && (_config.SsimDeduplicationThreshold > 0 || textThreshold > 0))
                {
                    var originalCount = frames.Count;

                    if (perFrameRegions != null && perFrameRegions.Count > 0)
                    {
                        // Use smart text-content deduplication
                        frames = await DeduplicateFramesUsingTextContentAsync(
                            frames, perFrameRegions, textThreshold, ct);

                        signals.Add(new Signal
                        {
                            Key = "ocr.frames.deduplicated",
                            Value = true,
                            Confidence = 1.0,
                            Source = Name,
                            Tags = new List<string> { "ocr", "preprocessing", "smart_dedup" },
                            Metadata = new Dictionary<string, object>
                            {
                                ["original_count"] = originalCount,
                                ["deduplicated_count"] = frames.Count,
                                ["removed"] = originalCount - frames.Count,
                                ["threshold"] = _config.SsimDeduplicationThreshold,
                                ["method"] = "text_content_similarity"
                            }
                        });
                    }
                    else
                    {
                        // Fallback to SSIM deduplication
                        frames = DeduplicateFramesSsim(frames, _config.SsimDeduplicationThreshold);

                        signals.Add(new Signal
                        {
                            Key = "ocr.frames.deduplicated",
                            Value = true,
                            Confidence = 1.0,
                            Source = Name,
                            Tags = new List<string> { "ocr", "preprocessing" },
                            Metadata = new Dictionary<string, object>
                            {
                                ["original_count"] = originalCount,
                                ["deduplicated_count"] = frames.Count,
                                ["removed"] = originalCount - frames.Count,
                                ["threshold"] = _config.SsimDeduplicationThreshold,
                                ["method"] = "ssim_pixel_similarity"
                            }
                        });
                    }
                }
            }
        }

        // Cache frames for other phases
        context.SetCached("ocr.frames", frames);

        sw.Stop();

        signals.Add(new Signal
        {
            Key = "ocr.frames.extracted",
            Value = frames.Count,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "ocr", "preprocessing" },
            Metadata = new Dictionary<string, object>
            {
                ["frame_count"] = frames.Count,
                ["duration_ms"] = sw.ElapsedMilliseconds
            }
        });

        return signals;
    }

    private async Task<List<Signal>> StabilizationPhaseAsync(
        List<Image<Rgba32>> frames,
        AnalysisContext context,
        CancellationToken ct)
    {
        var signals = new List<Signal>();
        var sw = Stopwatch.StartNew();

        var stabilizer = new FrameStabilizer(
            maxFeatures: 500,
            confidenceThreshold: _config.StabilizationConfidenceThreshold,
            logger: _logger as ILogger<FrameStabilizer>);

        var result = await Task.Run(() => stabilizer.StabilizeFrames(frames), ct);

        // Cache stabilized frames
        context.SetCached("ocr.frames.stabilized", result.StabilizedFrames);

        sw.Stop();

        signals.Add(new Signal
        {
            Key = "ocr.stabilization.confidence",
            Value = result.AverageConfidence,
            Confidence = result.AverageConfidence,
            Source = Name,
            Tags = new List<string> { "ocr", "preprocessing" },
            Metadata = new Dictionary<string, object>
            {
                ["stabilized_count"] = result.StabilizedFrames.Count,
                ["failed_count"] = result.FailedFrameIndices.Count,
                ["duration_ms"] = sw.ElapsedMilliseconds
            }
        });

        signals.Add(new Signal
        {
            Key = "ocr.stabilization.success",
            Value = result.FailedFrameIndices.Count == 0,
            Confidence = result.FailedFrameIndices.Count == 0 ? 1.0 : 1.0 - (result.FailedFrameIndices.Count / (double)frames.Count),
            Source = Name,
            Tags = new List<string> { "ocr", "preprocessing" }
        });

        // Use stabilized frames for subsequent processing
        frames.Clear();
        frames.AddRange(result.StabilizedFrames);

        return signals;
    }

    private async Task<List<Signal>> TemporalMedianPhaseAsync(
        List<Image<Rgba32>> frames,
        AnalysisContext context,
        CancellationToken ct)
    {
        var signals = new List<Signal>();
        var sw = Stopwatch.StartNew();

        var filter = new TemporalMedianFilter(
            verbose: false,
            logger: _logger as ILogger<TemporalMedianFilter>);
        var medianComposite = await Task.Run(() => filter.ComputeTemporalMedian(frames), ct);

        // Cache median composite
        context.SetCached("ocr.temporal_median", medianComposite);

        sw.Stop();

        signals.Add(new Signal
        {
            Key = "ocr.temporal_median.computed",
            Value = true,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "ocr", "preprocessing" },
            Metadata = new Dictionary<string, object>
            {
                ["frame_count"] = frames.Count,
                ["duration_ms"] = sw.ElapsedMilliseconds
            }
        });

        return signals;
    }

    private async Task<(List<Signal> Signals, double Confidence)> OcrPhaseAsync(
        Image<Rgba32> image,
        string phase,
        AnalysisContext context,
        CancellationToken ct)
    {
        var signals = new List<Signal>();
        var sw = Stopwatch.StartNew();

        // Save image temporarily for OCR engine
        var tempPath = Path.Combine(Path.GetTempPath(), $"ocr_{Guid.NewGuid()}.png");
        try
        {
            await image.SaveAsPngAsync(tempPath, ct);

            var ocrRegions = await Task.Run(() => _ocrEngine.ExtractTextWithCoordinates(tempPath), ct);

            sw.Stop();

            var fullText = string.Join("\n", ocrRegions.Select(r => r.Text));
            var avgConfidence = ocrRegions.Any()
                ? ocrRegions.Average(r => r.Confidence)
                : 0.0;

            signals.Add(new Signal
            {
                Key = $"ocr.{phase}.full_text",
                Value = fullText,
                Confidence = avgConfidence,
                Source = Name,
                Tags = new List<string> { "ocr", SignalTags.Content },
                Metadata = new Dictionary<string, object>
                {
                    ["region_count"] = ocrRegions.Count,
                    ["character_count"] = fullText.Length,
                    ["duration_ms"] = sw.ElapsedMilliseconds
                }
            });

            signals.Add(new Signal
            {
                Key = $"ocr.{phase}.regions",
                Value = ocrRegions,
                Confidence = avgConfidence,
                Source = Name,
                Tags = new List<string> { "ocr", SignalTags.Content },
                Metadata = new Dictionary<string, object>
                {
                    ["count"] = ocrRegions.Count
                }
            });

            // Cache OCR result for voting phase
            context.SetCached($"ocr.{phase}.result", ocrRegions);

            return (signals, avgConfidence);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async Task<List<Signal>> TemporalVotingPhaseAsync(
        List<Image<Rgba32>> frames,
        AnalysisContext context,
        CancellationToken ct)
    {
        var signals = new List<Signal>();
        var sw = Stopwatch.StartNew();

        // Select frames for voting
        var maxFrames = _config.MaxFramesForVoting;
        var framesToVote = SelectFramesForVoting(frames, maxFrames);

        // OCR each frame in parallel
        var ocrTasks = framesToVote.Select(async (frame, index) =>
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"vote_{Guid.NewGuid()}.png");
            try
            {
                await frame.SaveAsPngAsync(tempPath, ct);
                var regions = await Task.Run(() => _ocrEngine.ExtractTextWithCoordinates(tempPath), ct);
                return (Index: index, Regions: regions);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        });

        var frameOcrResults = await Task.WhenAll(ocrTasks);

        // Convert to voting format
        var frameResults = frameOcrResults.Select(r => new FrameOcrResult
        {
            FrameIndex = r.Index,
            TextRegions = r.Regions
        }).ToList();

        // Perform voting
        var votingEngine = new TemporalVotingEngine(
            confidenceWeighting: true,
            iouThreshold: 0.5,
            logger: _logger as ILogger<TemporalVotingEngine>);

        var votingResult = votingEngine.PerformVoting(frameResults);

        sw.Stop();

        signals.Add(new Signal
        {
            Key = "ocr.voting.consensus_text",
            Value = votingResult.ConsensusText,
            Confidence = votingResult.Confidence,
            Source = Name,
            Tags = new List<string> { "ocr", SignalTags.Content },
            Metadata = new Dictionary<string, object>
            {
                ["frames_voted"] = framesToVote.Count,
                ["regions_merged"] = votingResult.TextRegions.Count,
                ["agreement_score"] = votingResult.AgreementScore,
                ["duration_ms"] = sw.ElapsedMilliseconds
            }
        });

        signals.Add(new Signal
        {
            Key = "ocr.voting.agreement_score",
            Value = votingResult.AgreementScore,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "ocr", "statistics" }
        });

        // Cache voting result for post-correction
        context.SetCached("ocr.voting.result", votingResult);

        return signals;
    }

    private async Task<List<Signal>> PostCorrectionPhaseAsync(
        AnalysisContext context,
        CancellationToken ct)
    {
        var signals = new List<Signal>();
        var sw = Stopwatch.StartNew();

        // Get text to correct (prefer voting result, fallback to median OCR)
        var textToCorrect = context.GetCached<VotingResult>("ocr.voting.result")?.ConsensusText
            ?? context.GetValue<string>("ocr.temporal_median.full_text")
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(textToCorrect))
        {
            return signals;
        }

        // Load dictionary if configured
        HashSet<string>? dictionary = null;
        if (!string.IsNullOrEmpty(_config.DictionaryPath) && File.Exists(_config.DictionaryPath))
        {
            dictionary = await OcrPostProcessor.LoadDictionaryAsync(_config.DictionaryPath);
        }

        var postProcessor = new OcrPostProcessor(
            dictionary,
            useDictionary: dictionary != null,
            logger: _logger as ILogger<OcrPostProcessor>);

        var (correctedText, correctionsApplied) = postProcessor.CorrectText(textToCorrect);

        sw.Stop();

        signals.Add(new Signal
        {
            Key = "ocr.corrected.text",
            Value = correctedText,
            Confidence = 0.95, // High confidence for corrected text
            Source = Name,
            Tags = new List<string> { "ocr", SignalTags.Content },
            Metadata = new Dictionary<string, object>
            {
                ["corrections_applied"] = correctionsApplied,
                ["original_text"] = textToCorrect,
                ["duration_ms"] = sw.ElapsedMilliseconds
            }
        });

        signals.Add(new Signal
        {
            Key = "ocr.corrections.count",
            Value = correctionsApplied,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "ocr", "statistics" }
        });

        return signals;
    }

    // Helper methods

    private bool IsAnimatedImage(string imagePath)
    {
        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        return ext == ".gif" || ext == ".webp";
    }

    private List<Image<Rgba32>> DeduplicateFramesSsim(List<Image<Rgba32>> frames, double threshold)
    {
        if (frames.Count == 0) return frames;

        var deduplicated = new List<Image<Rgba32>> { frames[0] };

        for (int i = 1; i < frames.Count; i++)
        {
            var currentFrame = frames[i];
            var isDuplicate = false;

            // Compare with last deduplicated frame (most likely to be similar)
            var lastFrame = deduplicated[^1];

            // Calculate structural similarity using simplified SSIM approach
            var similarity = CalculateFrameSimilarity(lastFrame, currentFrame);

            if (similarity < threshold)
            {
                // Frame is sufficiently different, keep it
                deduplicated.Add(currentFrame);
            }
            else
            {
                isDuplicate = true;
            }

            _logger?.LogTrace("Frame {Index}: similarity to previous = {Similarity:F3}, duplicate = {IsDuplicate}",
                i, similarity, isDuplicate);
        }

        _logger?.LogDebug("Deduplicated {Original} frames to {Final} unique frames (threshold: {Threshold})",
            frames.Count, deduplicated.Count, threshold);

        return deduplicated;
    }

    /// <summary>
    /// Smart deduplication using actual OCR text content.
    /// Compares the extracted text from each frame - if text is identical, frame is a duplicate.
    /// This is the most accurate method for OCR purposes.
    /// Falls back to perceptual hash comparison if OCR is unavailable.
    /// </summary>
    private async Task<List<Image<Rgba32>>> DeduplicateFramesUsingTextContentAsync(
        List<Image<Rgba32>> frames,
        Dictionary<int, List<Dictionary<string, int>>>? perFrameRegions,
        double threshold,
        CancellationToken ct)
    {
        if (frames.Count == 0) return frames;

        var deduplicated = new List<Image<Rgba32>>();
        var deduplicatedIndices = new List<int>();
        var seenTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? lastText = null;

        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];

            // Try to get OCR text for this frame
            string? frameText = null;

            // If we have text regions, OCR just those areas (faster + more accurate)
            if (perFrameRegions != null && perFrameRegions.TryGetValue(i, out var regions) && regions.Count > 0)
            {
                frameText = await ExtractTextFromRegionsAsync(frame, regions, ct);
            }
            else
            {
                // OCR the full frame (or subtitle area)
                frameText = await ExtractTextFromFrameSubtitleAreaAsync(frame, ct);
            }

            // Normalize text for comparison
            var normalizedText = NormalizeTextForComparison(frameText);

            // First frame always included
            if (deduplicated.Count == 0)
            {
                deduplicated.Add(frame);
                deduplicatedIndices.Add(i);
                if (!string.IsNullOrEmpty(normalizedText))
                    seenTexts.Add(normalizedText);
                lastText = normalizedText;
                continue;
            }

            // Check if we've seen this exact text before
            if (!string.IsNullOrEmpty(normalizedText) && seenTexts.Contains(normalizedText))
            {
                _logger?.LogTrace("Frame {Index}: duplicate text '{Text}', skipping", i,
                    normalizedText.Length > 30 ? normalizedText[..30] + "..." : normalizedText);
                continue;
            }

            // Check text similarity with previous frame
            var textSimilarity = CalculateTextSimilarity(lastText, normalizedText);

            if (textSimilarity < threshold)
            {
                // Text is different enough - keep this frame
                deduplicated.Add(frame);
                deduplicatedIndices.Add(i);
                if (!string.IsNullOrEmpty(normalizedText))
                    seenTexts.Add(normalizedText);
                lastText = normalizedText;

                _logger?.LogTrace("Frame {Index}: text similarity = {Similarity:F3}, keeping ('{Text}')",
                    i, textSimilarity, normalizedText?.Length > 30 ? normalizedText[..30] + "..." : normalizedText ?? "");
            }
            else
            {
                _logger?.LogTrace("Frame {Index}: text similarity = {Similarity:F3}, duplicate", i, textSimilarity);
            }
        }

        _logger?.LogDebug(
            "Text-content dedup: {Original} → {Final} frames, {UniqueTexts} unique texts (indices: {Indices})",
            frames.Count, deduplicated.Count, seenTexts.Count, string.Join(",", deduplicatedIndices));

        return deduplicated;
    }

    /// <summary>
    /// Extracts text from specific regions of a frame using Tesseract.
    /// </summary>
    private async Task<string?> ExtractTextFromRegionsAsync(
        Image<Rgba32> frame,
        List<Dictionary<string, int>> regions,
        CancellationToken ct)
    {
        var (x, y, w, h) = MergeRegionBounds(regions, frame.Width, frame.Height);

        // Crop to text region
        var cropRect = new Rectangle(x, y, w, h);
        using var cropped = frame.Clone(ctx => ctx.Crop(cropRect));

        // Save to temp and OCR
        var tempPath = Path.Combine(Path.GetTempPath(), $"dedup_region_{Guid.NewGuid()}.png");
        try
        {
            await cropped.SaveAsPngAsync(tempPath, ct);
            var ocrResult = await Task.Run(() => _ocrEngine.ExtractTextWithCoordinates(tempPath), ct);
            return string.Join(" ", ocrResult.Select(r => r.Text));
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Extracts text from the subtitle area (bottom 30%) of a frame.
    /// </summary>
    private async Task<string?> ExtractTextFromFrameSubtitleAreaAsync(Image<Rgba32> frame, CancellationToken ct)
    {
        // Crop to subtitle area (bottom 30%)
        var subtitleY = (int)(frame.Height * 0.70);
        var subtitleH = frame.Height - subtitleY;

        var cropRect = new Rectangle(0, subtitleY, frame.Width, subtitleH);
        using var cropped = frame.Clone(ctx => ctx.Crop(cropRect));

        var tempPath = Path.Combine(Path.GetTempPath(), $"dedup_subtitle_{Guid.NewGuid()}.png");
        try
        {
            await cropped.SaveAsPngAsync(tempPath, ct);
            var ocrResult = await Task.Run(() => _ocrEngine.ExtractTextWithCoordinates(tempPath), ct);
            return string.Join(" ", ocrResult.Select(r => r.Text));
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Normalizes text for comparison - strips whitespace, lowercases, removes punctuation.
    /// </summary>
    private static string? NormalizeTextForComparison(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Remove extra whitespace, lowercase, strip common OCR artifacts
        var normalized = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ")
            .Trim()
            .ToLowerInvariant();

        // Remove non-alphanumeric chars for fuzzy comparison
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"[^a-z0-9\s]", "");

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    /// <summary>
    /// Calculates similarity between two text strings using Levenshtein distance.
    /// Returns 0.0 (completely different) to 1.0 (identical).
    /// </summary>
    private static double CalculateTextSimilarity(string? text1, string? text2)
    {
        // Both empty = identical
        if (string.IsNullOrEmpty(text1) && string.IsNullOrEmpty(text2))
            return 1.0;

        // One empty = different
        if (string.IsNullOrEmpty(text1) || string.IsNullOrEmpty(text2))
            return 0.0;

        // Exact match
        if (text1 == text2)
            return 1.0;

        // Calculate Levenshtein distance
        var distance = LevenshteinDistance(text1, text2);
        var maxLen = Math.Max(text1.Length, text2.Length);

        return 1.0 - ((double)distance / maxLen);
    }

    /// <summary>
    /// Computes Levenshtein (edit) distance between two strings.
    /// </summary>
    private static int LevenshteinDistance(string s, string t)
    {
        var n = s.Length;
        var m = t.Length;

        if (n == 0) return m;
        if (m == 0) return n;

        var d = new int[n + 1, m + 1];

        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }

    /// <summary>
    /// Fallback deduplication using OpenCV-detected text regions (pixel-based).
    /// Only compares the areas where text was detected, ignoring background changes.
    /// </summary>
    private List<Image<Rgba32>> DeduplicateFramesUsingTextRegions(
        List<Image<Rgba32>> frames,
        Dictionary<int, List<Dictionary<string, int>>> perFrameRegions,
        double threshold)
    {
        if (frames.Count == 0) return frames;
        if (perFrameRegions.Count == 0) return DeduplicateFramesSsim(frames, threshold);

        var deduplicated = new List<Image<Rgba32>>();
        var deduplicatedIndices = new List<int>();
        byte[]? lastTextRegionHash = null;

        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];

            // Get text regions for this frame
            if (!perFrameRegions.TryGetValue(i, out var regions) || regions.Count == 0)
            {
                // No text regions detected - compare using subtitle area fallback
                if (deduplicated.Count == 0)
                {
                    deduplicated.Add(frame);
                    deduplicatedIndices.Add(i);
                    continue;
                }

                // Compare subtitle region only (bottom 25%)
                var subtitleSimilarity = CalculateSubtitleRegionSimilarity(deduplicated[^1], frame);
                if (subtitleSimilarity < threshold)
                {
                    deduplicated.Add(frame);
                    deduplicatedIndices.Add(i);
                }
                continue;
            }

            // Extract and hash the text region content
            var currentHash = ComputeTextRegionHash(frame, regions);

            if (lastTextRegionHash == null)
            {
                deduplicated.Add(frame);
                deduplicatedIndices.Add(i);
                lastTextRegionHash = currentHash;
                continue;
            }

            // Compare text region hashes
            var hashSimilarity = CompareHashes(lastTextRegionHash, currentHash);

            if (hashSimilarity < threshold)
            {
                // Text regions are different enough - keep this frame
                deduplicated.Add(frame);
                deduplicatedIndices.Add(i);
                lastTextRegionHash = currentHash;

                _logger?.LogTrace("Frame {Index}: text region hash similarity = {Similarity:F3}, keeping", i, hashSimilarity);
            }
            else
            {
                _logger?.LogTrace("Frame {Index}: text region hash similarity = {Similarity:F3}, duplicate", i, hashSimilarity);
            }
        }

        _logger?.LogDebug(
            "Smart text-region dedup: {Original} → {Final} frames (indices: {Indices})",
            frames.Count, deduplicated.Count, string.Join(",", deduplicatedIndices));

        return deduplicated;
    }

    /// <summary>
    /// Computes a perceptual hash of the text region content.
    /// Uses luminance values in the text area to detect changes.
    /// </summary>
    private byte[] ComputeTextRegionHash(Image<Rgba32> frame, List<Dictionary<string, int>> regions)
    {
        // Merge regions into single bounding box
        var (x, y, w, h) = MergeRegionBounds(regions, frame.Width, frame.Height);

        // Sample at fixed grid points for consistent comparison
        const int gridSize = 16;
        var hash = new byte[gridSize * gridSize];

        var stepX = Math.Max(1, w / gridSize);
        var stepY = Math.Max(1, h / gridSize);

        var idx = 0;
        for (int gy = 0; gy < gridSize; gy++)
        {
            for (int gx = 0; gx < gridSize; gx++)
            {
                var px = Math.Min(x + gx * stepX, frame.Width - 1);
                var py = Math.Min(y + gy * stepY, frame.Height - 1);

                var pixel = frame[px, py];
                // Store luminance as hash byte
                var luminance = (byte)(0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B);
                hash[idx++] = luminance;
            }
        }

        return hash;
    }

    /// <summary>
    /// Compares two perceptual hashes and returns similarity (0=different, 1=identical).
    /// </summary>
    private double CompareHashes(byte[] hash1, byte[] hash2)
    {
        if (hash1.Length != hash2.Length) return 0;

        var totalDiff = 0.0;
        for (int i = 0; i < hash1.Length; i++)
        {
            totalDiff += Math.Abs(hash1[i] - hash2[i]);
        }

        // Normalize to 0-1 range (max diff per byte is 255)
        var avgDiff = totalDiff / hash1.Length;
        return 1.0 - (avgDiff / 255.0);
    }

    /// <summary>
    /// Merges multiple region bounds into a single bounding box with padding.
    /// </summary>
    private (int X, int Y, int Width, int Height) MergeRegionBounds(
        List<Dictionary<string, int>> regions,
        int imageWidth,
        int imageHeight)
    {
        if (regions.Count == 0)
            return (0, (int)(imageHeight * 0.75), imageWidth, imageHeight / 4); // Default to subtitle area

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = 0;
        var maxY = 0;

        foreach (var region in regions)
        {
            var rx = region.GetValueOrDefault("x", 0);
            var ry = region.GetValueOrDefault("y", 0);
            var rw = region.GetValueOrDefault("width", 0);
            var rh = region.GetValueOrDefault("height", 0);

            minX = Math.Min(minX, rx);
            minY = Math.Min(minY, ry);
            maxX = Math.Max(maxX, rx + rw);
            maxY = Math.Max(maxY, ry + rh);
        }

        // Add small padding
        var pad = 5;
        minX = Math.Max(0, minX - pad);
        minY = Math.Max(0, minY - pad);
        maxX = Math.Min(imageWidth, maxX + pad);
        maxY = Math.Min(imageHeight, maxY + pad);

        return (minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Calculates similarity of subtitle regions only (bottom 25% of frame).
    /// Used as fallback when no OpenCV text regions are available.
    /// </summary>
    private double CalculateSubtitleRegionSimilarity(Image<Rgba32> frame1, Image<Rgba32> frame2)
    {
        if (frame1.Width != frame2.Width || frame1.Height != frame2.Height)
            return 0.0;

        var subtitleStart = (int)(frame1.Height * 0.75);
        const int sampleStep = 2;

        double totalDiff = 0;
        int sampleCount = 0;

        for (int y = subtitleStart; y < frame1.Height; y += sampleStep)
        {
            for (int x = 0; x < frame1.Width; x += sampleStep)
            {
                var p1 = frame1[x, y];
                var p2 = frame2[x, y];

                var lum1 = 0.299 * p1.R + 0.587 * p1.G + 0.114 * p1.B;
                var lum2 = 0.299 * p2.R + 0.587 * p2.G + 0.114 * p2.B;

                totalDiff += Math.Abs(lum1 - lum2);
                sampleCount++;
            }
        }

        if (sampleCount == 0) return 1.0;

        var avgDiff = totalDiff / sampleCount;
        return 1.0 - (avgDiff / 255.0);
    }

    /// <summary>
    /// Calculate structural similarity between two frames
    /// Returns 0.0 (completely different) to 1.0 (identical)
    /// Uses a subtitle-aware metric that weights:
    /// 1. Bottom region (where subtitles appear) more heavily
    /// 2. High-brightness pixels (subtitle text is typically white/yellow) more heavily
    /// </summary>
    private double CalculateFrameSimilarity(Image<Rgba32> frame1, Image<Rgba32> frame2)
    {
        // Ensure frames are same size
        if (frame1.Width != frame2.Width || frame1.Height != frame2.Height)
            return 0.0;

        // Sample pixels in a grid pattern for performance
        const int sampleStep = 4; // Sample every 4th pixel

        // Subtitle region starts at 75% of frame height (bottom 25%)
        var subtitleRegionStart = (int)(frame1.Height * 0.75);

        // Brightness threshold for "likely text" pixels (white/yellow subtitles are bright)
        const double textBrightnessThreshold = 200.0; // Out of 255

        double mainDiff = 0;
        int mainSampleCount = 0;
        double subtitleDiff = 0;
        int subtitleSampleCount = 0;
        double textColorDiff = 0; // Track changes in bright/text-colored pixels specifically
        int textColorSampleCount = 0;

        for (int y = 0; y < frame1.Height; y += sampleStep)
        {
            for (int x = 0; x < frame1.Width; x += sampleStep)
            {
                var p1 = frame1[x, y];
                var p2 = frame2[x, y];

                // Calculate luminance (brightness)
                var lum1 = 0.299 * p1.R + 0.587 * p1.G + 0.114 * p1.B;
                var lum2 = 0.299 * p2.R + 0.587 * p2.G + 0.114 * p2.B;

                var diff = Math.Abs(lum1 - lum2);

                // Check if either pixel is bright (likely text color)
                var isBright1 = lum1 > textBrightnessThreshold;
                var isBright2 = lum2 > textBrightnessThreshold;

                // Track subtitle region separately (bottom 25%)
                if (y >= subtitleRegionStart)
                {
                    // In subtitle region, weight bright pixel changes even more
                    if (isBright1 || isBright2)
                    {
                        // Text appearing or disappearing - weight 3x
                        textColorDiff += diff * 3.0;
                        textColorSampleCount++;
                    }
                    subtitleDiff += diff;
                    subtitleSampleCount++;
                }
                else
                {
                    mainDiff += diff;
                    mainSampleCount++;
                }
            }
        }

        // Calculate separate similarities
        var mainAvgDiff = mainSampleCount > 0 ? mainDiff / mainSampleCount : 0;
        var subtitleAvgDiff = subtitleSampleCount > 0 ? subtitleDiff / subtitleSampleCount : 0;
        var textColorAvgDiff = textColorSampleCount > 0 ? textColorDiff / textColorSampleCount : 0;

        var mainSimilarity = 1.0 - (mainAvgDiff / 255.0);
        var subtitleSimilarity = 1.0 - (subtitleAvgDiff / 255.0);
        var textColorSimilarity = 1.0 - Math.Min(textColorAvgDiff / 255.0, 1.0);

        // Combined weighting:
        // - 30% main content (background)
        // - 40% subtitle region (location-based)
        // - 30% bright text pixels (color-based, most sensitive to subtitle changes)
        var combinedSimilarity = (mainSimilarity * 0.3) + (subtitleSimilarity * 0.4) + (textColorSimilarity * 0.3);

        _logger?.LogTrace("Frame similarity: main={Main:F3}, subtitle={Subtitle:F3}, text={Text:F3}, combined={Combined:F3}",
            mainSimilarity, subtitleSimilarity, textColorSimilarity, combinedSimilarity);

        return Math.Clamp(combinedSimilarity, 0.0, 1.0);
    }

    private List<Image<Rgba32>> SelectFramesForVoting(List<Image<Rgba32>> frames, int maxFrames)
    {
        if (frames.Count <= maxFrames)
        {
            return frames;
        }

        // Evenly sample frames
        var step = frames.Count / (double)maxFrames;
        var selected = new List<Image<Rgba32>>();

        for (int i = 0; i < maxFrames; i++)
        {
            var index = (int)(i * step);
            selected.Add(frames[index]);
        }

        return selected;
    }

    private Signal CreateErrorSignal(string message)
    {
        return new Signal
        {
            Key = "ocr.advanced.error",
            Value = message,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "error" }
        };
    }

    private Signal CreatePerformanceSignal(long durationMs, string status)
    {
        return new Signal
        {
            Key = "ocr.advanced.performance",
            Value = new
            {
                DurationMs = durationMs,
                Status = status,
                QualityMode = _config.QualityMode.ToString()
            },
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "performance", "ocr" },
            Metadata = new Dictionary<string, object>
            {
                ["duration_ms"] = durationMs,
                ["quality_mode"] = _config.QualityMode.ToString()
            }
        };
    }
}
