using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
/// Detects contradictions between signals using config-driven rules.
/// Implements priority-chain validation with rejection policies.
/// </summary>
public class ContradictionDetector
{
    private readonly ILogger<ContradictionDetector>? _logger;
    private readonly List<ContradictionRule> _rules;

    /// <summary>
    /// Built-in rules for common signal contradictions.
    /// </summary>
    public static readonly List<ContradictionRule> DefaultRules = new()
    {
        // OCR vs Vision LLM text detection
        new ContradictionRule
        {
            RuleId = "ocr_vs_vision_text",
            Description = "OCR found text but Vision LLM says no text present",
            SignalKeyA = "content.ocr_text",
            SignalKeyB = "vision.caption",
            Type = ContradictionType.Custom,
            Severity = ContradictionSeverity.Warning,
            Resolution = ResolutionStrategy.EscalateToLlm
        },

        // High text-likeliness but no OCR text
        new ContradictionRule
        {
            RuleId = "text_likeliness_vs_ocr",
            Description = "High text-likeliness score but OCR found no text",
            SignalKeyA = "content.text_likeliness",
            SignalKeyB = "content.ocr_text",
            Type = ContradictionType.MissingImplied,
            Threshold = 0.7, // text_likeliness > 0.7 should have OCR text
            Severity = ContradictionSeverity.Warning,
            Resolution = ResolutionStrategy.PreferHigherConfidence
        },

        // Grayscale flag vs dominant colors
        new ContradictionRule
        {
            RuleId = "grayscale_vs_colors",
            Description = "Image marked as grayscale but has colorful dominant colors",
            SignalKeyA = "color.is_grayscale",
            SignalKeyB = "color.mean_saturation",
            Type = ContradictionType.NumericDivergence,
            Threshold = 0.2, // saturation > 0.2 contradicts grayscale
            Severity = ContradictionSeverity.Info,
            Resolution = ResolutionStrategy.PreferHigherConfidence
        },

        // Screenshot type vs photo characteristics
        new ContradictionRule
        {
            RuleId = "screenshot_vs_photo_noise",
            Description = "Classified as screenshot but has photo-like noise patterns",
            SignalKeyA = "content.type",
            SignalKeyB = "quality.noise_level",
            Type = ContradictionType.Custom,
            Severity = ContradictionSeverity.Warning,
            Resolution = ResolutionStrategy.MarkConflicting
        },

        // Vision LLM confidence vs heuristic confidence
        new ContradictionRule
        {
            RuleId = "llm_vs_heuristic_type",
            Description = "Vision LLM type classification differs from heuristic classification",
            SignalKeyA = "content.type",
            SignalKeyB = "vision.detected_type",
            Type = ContradictionType.ValueConflict,
            Severity = ContradictionSeverity.Info,
            Resolution = ResolutionStrategy.PreferHigherConfidence
        },

        // CLIP embedding vs caption content
        new ContradictionRule
        {
            RuleId = "clip_vs_caption_mismatch",
            Description = "CLIP embedding suggests different content than vision caption",
            SignalKeyA = "vision.clip.embedding_hash",
            SignalKeyB = "vision.caption",
            Type = ContradictionType.Custom,
            Severity = ContradictionSeverity.Info,
            Resolution = ResolutionStrategy.MarkConflicting
        },

        // OCR quality vs OCR text length
        new ContradictionRule
        {
            RuleId = "ocr_quality_vs_content",
            Description = "OCR reports high confidence but text is garbled/very short",
            SignalKeyA = "ocr.confidence",
            SignalKeyB = "content.ocr_text",
            Type = ContradictionType.Custom,
            Severity = ContradictionSeverity.Warning,
            Resolution = ResolutionStrategy.EscalateToLlm
        },

        // Face detection vs image type
        new ContradictionRule
        {
            RuleId = "face_vs_icon",
            Description = "Face detected in image classified as Icon/Diagram",
            SignalKeyA = "faces.count",
            SignalKeyB = "content.type",
            Type = ContradictionType.Custom,
            Severity = ContradictionSeverity.Warning,
            Resolution = ResolutionStrategy.PreferHigherConfidence
        },

        // EXIF date vs format capability
        new ContradictionRule
        {
            RuleId = "exif_format_mismatch",
            Description = "EXIF data present but format doesn't typically support EXIF",
            SignalKeyA = "forensics.has_exif",
            SignalKeyB = "identity.format",
            Type = ContradictionType.Custom,
            Severity = ContradictionSeverity.Warning,
            Resolution = ResolutionStrategy.ManualReview
        },

        // Blurry image with high edge density
        new ContradictionRule
        {
            RuleId = "blur_vs_edges",
            Description = "Image classified as blurry but has high edge density",
            SignalKeyA = "quality.sharpness",
            SignalKeyB = "quality.edge_density",
            Type = ContradictionType.NumericDivergence,
            Threshold = 500, // If sharpness < 300 but edge_density > 0.3
            Severity = ContradictionSeverity.Info,
            Resolution = ResolutionStrategy.MarkConflicting
        }
    };

    public ContradictionDetector(
        IEnumerable<ContradictionRule>? customRules = null,
        ILogger<ContradictionDetector>? logger = null)
    {
        _logger = logger;
        _rules = new List<ContradictionRule>(DefaultRules);

        if (customRules != null)
        {
            _rules.AddRange(customRules);
        }
    }

    /// <summary>
    /// Add a custom contradiction rule.
    /// </summary>
    public void AddRule(ContradictionRule rule)
    {
        _rules.Add(rule);
    }

    /// <summary>
    /// Remove a rule by ID.
    /// </summary>
    public bool RemoveRule(string ruleId)
    {
        return _rules.RemoveAll(r => r.RuleId == ruleId) > 0;
    }

    /// <summary>
    /// Check for contradictions in an analysis context.
    /// </summary>
    public IEnumerable<ContradictionResult> DetectContradictions(AnalysisContext context)
    {
        var results = new List<ContradictionResult>();

        foreach (var rule in _rules.Where(r => r.Enabled))
        {
            try
            {
                var contradiction = CheckRule(rule, context);
                if (contradiction != null)
                {
                    results.Add(contradiction);
                    _logger?.LogWarning(
                        "Contradiction detected: {RuleId} - {Description}",
                        rule.RuleId, contradiction.Explanation);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error checking rule {RuleId}", rule.RuleId);
            }
        }

        return results;
    }

    /// <summary>
    /// Check for contradictions in a DynamicImageProfile.
    /// </summary>
    public IEnumerable<ContradictionResult> DetectContradictions(DynamicImageProfile profile)
    {
        // Build context from profile signals
        var context = new AnalysisContext();
        foreach (var signal in profile.GetAllSignals())
        {
            context.AddSignal(signal);
        }
        return DetectContradictions(context);
    }

    private ContradictionResult? CheckRule(ContradictionRule rule, AnalysisContext context)
    {
        var signalA = context.GetBestSignal(rule.SignalKeyA);
        var signalB = context.GetBestSignal(rule.SignalKeyB);

        // Skip if signals don't meet minimum confidence
        if (signalA != null && signalA.Confidence < rule.MinConfidenceThreshold)
            return null;
        if (signalB != null && signalB.Confidence < rule.MinConfidenceThreshold)
            return null;

        return rule.Type switch
        {
            ContradictionType.ValueConflict => CheckValueConflict(rule, signalA, signalB),
            ContradictionType.NumericDivergence => CheckNumericDivergence(rule, signalA, signalB),
            ContradictionType.BooleanOpposite => CheckBooleanOpposite(rule, signalA, signalB),
            ContradictionType.MutuallyExclusive => CheckMutuallyExclusive(rule, signalA, signalB),
            ContradictionType.MissingImplied => CheckMissingImplied(rule, signalA, signalB, context),
            ContradictionType.Custom => CheckCustomRule(rule, signalA, signalB, context),
            _ => null
        };
    }

    private ContradictionResult? CheckValueConflict(
        ContradictionRule rule, Signal? signalA, Signal? signalB)
    {
        if (signalA?.Value == null || signalB?.Value == null)
            return null;

        // Check if values are in contradictory sets
        if (rule.ExpectedValuesA != null && rule.ContradictoryValuesB != null)
        {
            var valueAStr = signalA.Value.ToString();
            var valueBStr = signalB.Value.ToString();

            if (rule.ExpectedValuesA.Any(v => v.ToString() == valueAStr) &&
                rule.ContradictoryValuesB.Any(v => v.ToString() == valueBStr))
            {
                return CreateResult(rule, signalA, signalB,
                    $"Value '{valueAStr}' for {rule.SignalKeyA} conflicts with '{valueBStr}' for {rule.SignalKeyB}");
            }
        }

        // Direct value comparison for same-key signals
        if (rule.SignalKeyA == rule.SignalKeyB && !Equals(signalA.Value, signalB.Value))
        {
            return CreateResult(rule, signalA, signalB,
                $"Multiple signals for {rule.SignalKeyA} have conflicting values: '{signalA.Value}' vs '{signalB.Value}'");
        }

        return null;
    }

    private ContradictionResult? CheckNumericDivergence(
        ContradictionRule rule, Signal? signalA, Signal? signalB)
    {
        if (signalA?.Value == null || signalB?.Value == null || rule.Threshold == null)
            return null;

        if (!TryGetNumericValue(signalA.Value, out var numA) ||
            !TryGetNumericValue(signalB.Value, out var numB))
            return null;

        // Special case for grayscale vs saturation check
        if (rule.RuleId == "grayscale_vs_colors")
        {
            if (signalA.Value is bool isGrayscale && isGrayscale && numB > rule.Threshold)
            {
                return CreateResult(rule, signalA, signalB,
                    $"Image marked as grayscale but saturation is {numB:F2} (> {rule.Threshold})");
            }
            return null;
        }

        // Special case for blur vs edges
        if (rule.RuleId == "blur_vs_edges")
        {
            if (numA < 300 && numB > 0.3)
            {
                return CreateResult(rule, signalA, signalB,
                    $"Low sharpness ({numA:F0}) but high edge density ({numB:F2})");
            }
            return null;
        }

        var difference = Math.Abs(numA - numB);
        if (difference > rule.Threshold)
        {
            return CreateResult(rule, signalA, signalB,
                $"Numeric divergence: {rule.SignalKeyA}={numA:F2} vs {rule.SignalKeyB}={numB:F2} (diff={difference:F2}, threshold={rule.Threshold})");
        }

        return null;
    }

    private ContradictionResult? CheckBooleanOpposite(
        ContradictionRule rule, Signal? signalA, Signal? signalB)
    {
        if (signalA?.Value == null || signalB?.Value == null)
            return null;

        if (signalA.Value is bool boolA && signalB.Value is bool boolB)
        {
            if (boolA != boolB)
            {
                return CreateResult(rule, signalA, signalB,
                    $"Boolean contradiction: {rule.SignalKeyA}={boolA} vs {rule.SignalKeyB}={boolB}");
            }
        }

        return null;
    }

    private ContradictionResult? CheckMutuallyExclusive(
        ContradictionRule rule, Signal? signalA, Signal? signalB)
    {
        if (signalA?.Value == null || signalB?.Value == null)
            return null;

        // Both values must be from mutually exclusive sets
        if (rule.ExpectedValuesA != null && rule.ContradictoryValuesB != null)
        {
            var valueAStr = signalA.Value.ToString();
            var valueBStr = signalB.Value.ToString();

            if (rule.ExpectedValuesA.Any(v => v.ToString() == valueAStr) &&
                rule.ContradictoryValuesB.Any(v => v.ToString() == valueBStr))
            {
                return CreateResult(rule, signalA, signalB,
                    $"Mutually exclusive values: {rule.SignalKeyA}='{valueAStr}' cannot coexist with {rule.SignalKeyB}='{valueBStr}'");
            }
        }

        return null;
    }

    private ContradictionResult? CheckMissingImplied(
        ContradictionRule rule, Signal? signalA, Signal? signalB, AnalysisContext context)
    {
        if (signalA?.Value == null)
            return null;

        // Check if signalA implies signalB should exist
        if (rule.RuleId == "text_likeliness_vs_ocr")
        {
            if (TryGetNumericValue(signalA.Value, out var textLikeliness))
            {
                if (textLikeliness > (rule.Threshold ?? 0.7))
                {
                    // High text likeliness - OCR text should exist
                    if (signalB?.Value == null ||
                        (signalB.Value is string text && string.IsNullOrWhiteSpace(text)))
                    {
                        return CreateResult(rule, signalA, null,
                            $"High text likeliness ({textLikeliness:F2}) but no OCR text found");
                    }
                }
            }
        }

        return null;
    }

    private ContradictionResult? CheckCustomRule(
        ContradictionRule rule, Signal? signalA, Signal? signalB, AnalysisContext context)
    {
        return rule.RuleId switch
        {
            "ocr_vs_vision_text" => CheckOcrVsVisionText(rule, signalA, signalB),
            "ocr_quality_vs_content" => CheckOcrQualityVsContent(rule, signalA, signalB),
            "face_vs_icon" => CheckFaceVsIcon(rule, signalA, signalB),
            "exif_format_mismatch" => CheckExifFormatMismatch(rule, signalA, signalB),
            "screenshot_vs_photo_noise" => CheckScreenshotVsPhotoNoise(rule, signalA, signalB),
            _ => null
        };
    }

    private ContradictionResult? CheckOcrVsVisionText(
        ContradictionRule rule, Signal? ocrSignal, Signal? visionSignal)
    {
        if (ocrSignal?.Value == null || visionSignal?.Value == null)
            return null;

        var ocrText = ocrSignal.Value.ToString() ?? "";
        var caption = visionSignal.Value.ToString()?.ToLowerInvariant() ?? "";

        // OCR found significant text
        if (ocrText.Length > 20)
        {
            // Vision LLM says no text
            if (caption.Contains("no text") || caption.Contains("without text") ||
                caption.Contains("doesn't contain") && caption.Contains("text"))
            {
                return CreateResult(rule, ocrSignal, visionSignal,
                    $"OCR found {ocrText.Length} chars of text but Vision LLM says no text present");
            }
        }

        return null;
    }

    private ContradictionResult? CheckOcrQualityVsContent(
        ContradictionRule rule, Signal? confidenceSignal, Signal? textSignal)
    {
        if (confidenceSignal?.Value == null)
            return null;

        if (!TryGetNumericValue(confidenceSignal.Value, out var confidence))
            return null;

        // High OCR confidence
        if (confidence > 0.8)
        {
            var text = textSignal?.Value?.ToString() ?? "";
            // But text is very short or looks garbled
            if (text.Length < 5 || IsGarbledText(text))
            {
                return CreateResult(rule, confidenceSignal, textSignal,
                    $"High OCR confidence ({confidence:F2}) but text appears garbled or too short: '{text.Substring(0, Math.Min(50, text.Length))}'");
            }
        }

        return null;
    }

    private ContradictionResult? CheckFaceVsIcon(
        ContradictionRule rule, Signal? faceCountSignal, Signal? typeSignal)
    {
        if (faceCountSignal?.Value == null || typeSignal?.Value == null)
            return null;

        if (!TryGetNumericValue(faceCountSignal.Value, out var faceCount))
            return null;

        var imageType = typeSignal.Value.ToString()?.ToLowerInvariant() ?? "";

        if (faceCount > 0 && (imageType == "icon" || imageType == "diagram"))
        {
            return CreateResult(rule, faceCountSignal, typeSignal,
                $"Detected {faceCount} face(s) in image classified as '{imageType}'");
        }

        return null;
    }

    private ContradictionResult? CheckExifFormatMismatch(
        ContradictionRule rule, Signal? exifSignal, Signal? formatSignal)
    {
        if (exifSignal?.Value == null || formatSignal?.Value == null)
            return null;

        if (exifSignal.Value is bool hasExif && hasExif)
        {
            var format = formatSignal.Value.ToString()?.ToUpperInvariant() ?? "";
            // PNG and GIF don't typically have EXIF
            if (format == "PNG" || format == "GIF" || format == "BMP")
            {
                return CreateResult(rule, exifSignal, formatSignal,
                    $"EXIF data found in {format} image (unusual - may indicate format conversion or tampering)");
            }
        }

        return null;
    }

    private ContradictionResult? CheckScreenshotVsPhotoNoise(
        ContradictionRule rule, Signal? typeSignal, Signal? noiseSignal)
    {
        if (typeSignal?.Value == null || noiseSignal?.Value == null)
            return null;

        var imageType = typeSignal.Value.ToString()?.ToLowerInvariant() ?? "";
        if (imageType != "screenshot")
            return null;

        if (!TryGetNumericValue(noiseSignal.Value, out var noiseLevel))
            return null;

        // Screenshots should have very low noise (clean digital capture)
        if (noiseLevel > 0.1)
        {
            return CreateResult(rule, typeSignal, noiseSignal,
                $"Image classified as screenshot but has photo-like noise level ({noiseLevel:F2})");
        }

        return null;
    }

    private static bool IsGarbledText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        // Check for common garbled text patterns
        var consecutiveConsonants = 0;
        var consecutiveSpecial = 0;
        var specialCharCount = 0;

        foreach (var c in text)
        {
            if (!char.IsLetter(c) && !char.IsWhiteSpace(c) && !char.IsDigit(c))
            {
                specialCharCount++;
                consecutiveSpecial++;
                if (consecutiveSpecial > 3) return true;
            }
            else
            {
                consecutiveSpecial = 0;
            }

            if (char.IsLetter(c) && !"aeiouAEIOU".Contains(c))
            {
                consecutiveConsonants++;
                if (consecutiveConsonants > 5) return true;
            }
            else if (char.IsLetter(c))
            {
                consecutiveConsonants = 0;
            }
        }

        // More than 30% special characters is likely garbled
        return text.Length > 0 && (double)specialCharCount / text.Length > 0.3;
    }

    private static bool TryGetNumericValue(object value, out double result)
    {
        result = 0;

        if (value is double d) { result = d; return true; }
        if (value is float f) { result = f; return true; }
        if (value is int i) { result = i; return true; }
        if (value is long l) { result = l; return true; }
        if (value is decimal dec) { result = (double)dec; return true; }

        return double.TryParse(value.ToString(), out result);
    }

    private static ContradictionResult CreateResult(
        ContradictionRule rule, Signal signalA, Signal? signalB, string explanation)
    {
        return new ContradictionResult
        {
            Rule = rule,
            SignalA = signalA,
            SignalB = signalB,
            Explanation = explanation,
            EffectiveSeverity = rule.Severity,
            RecommendedResolution = GetResolutionDescription(rule.Resolution)
        };
    }

    private static string GetResolutionDescription(ResolutionStrategy strategy)
    {
        return strategy switch
        {
            ResolutionStrategy.PreferHigherConfidence => "Use the signal with higher confidence score",
            ResolutionStrategy.PreferMostRecent => "Use the most recent signal",
            ResolutionStrategy.MarkConflicting => "Mark both signals as conflicting for manual review",
            ResolutionStrategy.RemoveBoth => "Remove both signals as neither is trusted",
            ResolutionStrategy.EscalateToLlm => "Escalate to Vision LLM for resolution",
            ResolutionStrategy.ManualReview => "Flag for manual review",
            _ => "Unknown resolution strategy"
        };
    }

    /// <summary>
    /// Get all registered rules.
    /// </summary>
    public IReadOnlyList<ContradictionRule> GetRules() => _rules.AsReadOnly();
}
