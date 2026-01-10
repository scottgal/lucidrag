using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Services.Vision;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.DocSummarizer.Images.Orchestration.Contributors;

/// <summary>
///     Florence-2 ONNX captioning and OCR contributor.
///     Can trigger early exit when OCR/caption quality is high enough.
///     This prevents unnecessary VisionLLM calls that might produce worse results.
/// </summary>
public sealed class Florence2Contributor : ConfiguredWaveBase
{
    private readonly Florence2CaptionService? _florence2Service;
    private readonly ILogger<Florence2Contributor> _logger;

    public Florence2Contributor(
        IWaveConfigProvider configProvider,
        ILogger<Florence2Contributor> logger,
        Florence2CaptionService? florence2Service = null)
        : base(configProvider)
    {
        _logger = logger;
        _florence2Service = florence2Service;
    }

    public override string Name => "Florence2Wave";

    // Trigger after identity wave has run
    public override IReadOnlyList<TriggerCondition> TriggerConditions => new[]
    {
        Triggers.WhenSignalExists(ImageSignalKeys.ImageWidth)
    };

    // Config-driven parameters from YAML
    private int MinCaptionLength => GetParam("min_caption_length", 20);
    private double MinOcrConfidence => GetParam("min_ocr_confidence", 0.75);
    private int EarlyExitCaptionLength => GetParam("early_exit_caption_length", 50);
    private int EarlyExitOcrWordCount => GetParam("early_exit_ocr_word_count", 10);
    private bool EscalateOnAnimated => GetParam("escalate_on_animated", true);
    private double ComplexityThreshold => GetParam("complexity_threshold", 0.7);

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        ImageBlackboardState state,
        CancellationToken cancellationToken = default)
    {
        // Check if Florence-2 is available
        if (_florence2Service == null || !await _florence2Service.IsAvailableAsync(cancellationToken))
        {
            _logger.LogDebug("Florence-2 not available, skipping");
            return Single(Info("florence2", "Florence-2 not available",
                new Dictionary<string, object> { ["florence2.available"] = false }));
        }

        var contributions = new List<DetectionContribution>();
        var signals = new Dictionary<string, object>
        {
            ["florence2.available"] = true
        };

        try
        {
            // Check if we should skip for animated images
            if (state.IsAnimated && EscalateOnAnimated)
            {
                _logger.LogDebug("Animated image detected, deferring to VisionLLM");
                signals["florence2.should_escalate"] = true;
                signals["florence2.skip_reason"] = "animated";
                return Single(Info("florence2", "Deferring animated to VisionLLM", signals));
            }

            // Run Florence-2 captioning and OCR
            var result = await _florence2Service.GetCaptionAsync(state.ImagePath, ct: cancellationToken);

            if (result == null)
            {
                signals["florence2.error"] = "No result returned";
                return Single(LowConfidenceContribution("florence2", "Florence-2 returned no result", signals));
            }

            // Record results
            signals["florence2.caption"] = result.Caption ?? "";
            signals["florence2.ocr_text"] = result.OcrText ?? "";
            signals["florence2.duration_ms"] = result.DurationMs;

            // Copy to standard signal keys
            if (!string.IsNullOrWhiteSpace(result.Caption))
            {
                signals[ImageSignalKeys.Caption] = result.Caption;
            }

            if (!string.IsNullOrWhiteSpace(result.OcrText))
            {
                signals[ImageSignalKeys.OcrText] = result.OcrText;
                signals[ImageSignalKeys.OcrWordCount] = CountWords(result.OcrText);
            }

            // Determine quality and whether to early exit or escalate
            var (quality, shouldEarlyExit, shouldEscalate, reason) = EvaluateQuality(result, state);

            signals["florence2.quality"] = quality;
            signals["florence2.should_escalate"] = shouldEscalate;

            if (shouldEarlyExit && CanTriggerEarlyExit)
            {
                _logger.LogInformation(
                    "Florence-2 triggering early exit: quality={Quality}, reason={Reason}",
                    quality, reason);

                // Create early exit contribution - prevents VisionLLM from running
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "florence2",
                    ConfidenceDelta = 0, // No confidence impact, just signals
                    Weight = WeightBase * WeightVerified,
                    Salience = 1.0,
                    Reason = $"Early exit: {reason}",
                    TriggerEarlyExit = true,
                    EarlyExitVerdict = "Florence2Confident",
                    Signals = signals.ToImmutableSignals()
                });

                return contributions;
            }

            // Normal contribution
            var contributionType = quality switch
            {
                > 0.8 => HighConfidenceContribution("florence2", reason, signals),
                > 0.6 => MediumConfidenceContribution("florence2", reason, signals),
                _ => LowConfidenceContribution("florence2", reason, signals)
            };

            contributions.Add(contributionType);

            // If we should escalate, add a signal so VisionLLM knows to run
            if (shouldEscalate)
            {
                _logger.LogDebug("Florence-2 recommending escalation to VisionLLM: {Reason}", reason);
            }

            return contributions;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Florence-2 processing failed");
            signals["florence2.error"] = ex.Message;
            return Single(LowConfidenceContribution("florence2", $"Error: {ex.Message}", signals));
        }
    }

    private (double quality, bool earlyExit, bool escalate, string reason) EvaluateQuality(
        Florence2CaptionResult result,
        ImageBlackboardState state)
    {
        var captionLength = result.Caption?.Length ?? 0;
        var ocrText = result.OcrText ?? "";
        var ocrWordCount = CountWords(ocrText);
        var hasCaption = captionLength >= MinCaptionLength;
        var hasOcr = ocrWordCount > 0;

        // Check for text-heavy image (OCR is primary output)
        if (state.HasSignal(ImageSignalKeys.TextDetected) && state.GetSignal<bool>(ImageSignalKeys.TextDetected))
        {
            // For text images, OCR quality determines everything
            if (ocrWordCount >= EarlyExitOcrWordCount)
            {
                // Good OCR result - early exit, don't need VisionLLM
                return (0.9, true, false, $"High-quality OCR: {ocrWordCount} words extracted");
            }

            if (ocrWordCount > 0 && ocrWordCount < 3)
            {
                // Sparse OCR - might need VisionLLM for interpretation
                return (0.5, false, true, "Sparse OCR, escalating for interpretation");
            }

            if (ocrWordCount == 0)
            {
                // No OCR on text image - definitely escalate
                return (0.3, false, true, "No OCR text extracted from text image");
            }
        }

        // For non-text images, caption quality matters more
        if (captionLength >= EarlyExitCaptionLength && hasCaption)
        {
            // Detailed caption - early exit
            return (0.85, true, false, $"Detailed caption: {captionLength} chars");
        }

        if (hasCaption && !hasOcr)
        {
            // Decent caption, no text - good enough
            return (0.75, false, false, "Caption adequate, no text in image");
        }

        if (!hasCaption)
        {
            // No usable caption - escalate
            return (0.4, false, true, "Caption too short or missing");
        }

        // Default - moderate quality, no early exit
        return (0.65, false, false, "Moderate quality results");
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return text.Split(new[] { ' ', '\t', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries).Length;
    }
}

/// <summary>
///     Extension to convert dictionary to immutable signals.
/// </summary>
internal static class DictionaryExtensions
{
    public static IReadOnlyDictionary<string, object> ToImmutableSignals(this Dictionary<string, object> dict)
    {
        return dict.ToImmutableDictionary();
    }

    private static System.Collections.Immutable.ImmutableDictionary<TKey, TValue> ToImmutableDictionary<TKey, TValue>(
        this Dictionary<TKey, TValue> dict) where TKey : notnull
    {
        return System.Collections.Immutable.ImmutableDictionary.CreateRange(dict);
    }
}
