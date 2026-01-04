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

        using (var image = await Image.LoadAsync<Rgba32>(imagePath, ct))
        {
            var frameCount = image.Frames.Count;

            // Extract all frames
            for (int i = 0; i < frameCount; i++)
            {
                var frame = image.Frames.CloneFrame(i);
                frames.Add(frame);
            }

            // SSIM deduplication if enabled
            if (frameCount > 1 && _config.SsimDeduplicationThreshold > 0)
            {
                var originalCount = frames.Count;
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
                        ["threshold"] = _config.SsimDeduplicationThreshold
                    }
                });
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
    /// Calculate structural similarity between two frames
    /// Returns 0.0 (completely different) to 1.0 (identical)
    /// Uses a simplified SSIM-like metric based on luminance and structure
    /// </summary>
    private double CalculateFrameSimilarity(Image<Rgba32> frame1, Image<Rgba32> frame2)
    {
        // Ensure frames are same size
        if (frame1.Width != frame2.Width || frame1.Height != frame2.Height)
            return 0.0;

        // Sample pixels in a grid pattern for performance
        const int sampleStep = 4; // Sample every 4th pixel
        long totalDiff = 0;
        int sampleCount = 0;

        for (int y = 0; y < frame1.Height; y += sampleStep)
        {
            for (int x = 0; x < frame1.Width; x += sampleStep)
            {
                var p1 = frame1[x, y];
                var p2 = frame2[x, y];

                // Calculate luminance difference (YUV Y component)
                var lum1 = 0.299 * p1.R + 0.587 * p1.G + 0.114 * p1.B;
                var lum2 = 0.299 * p2.R + 0.587 * p2.G + 0.114 * p2.B;

                var diff = Math.Abs(lum1 - lum2);
                totalDiff += (long)diff;
                sampleCount++;
            }
        }

        // Average difference per pixel (0-255 range)
        var avgDiff = totalDiff / (double)sampleCount;

        // Convert to similarity score (0.0 = different, 1.0 = identical)
        // Normalize by max possible difference (255)
        var similarity = 1.0 - (avgDiff / 255.0);

        return Math.Clamp(similarity, 0.0, 1.0);
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
