using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.PostProcessing;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// OCR Quality Assessment Wave - 3-tier correction pipeline
/// Tier 1: Dictionary + Heuristics (language-agnostic OCR patterns)
/// Tier 2: ML Context Check (n-gram language model, perplexity scoring)
/// Tier 3: Sentinel LLM (vision re-query for verification)
/// Priority: 58 (runs after AdvancedOcrWave at 59)
/// </summary>
public class OcrQualityWave : IAnalysisWave
{
    private readonly OcrConfig _config;
    private readonly ILogger<OcrQualityWave>? _logger;
    private readonly SpellChecker? _spellChecker;
    private readonly MlContextChecker? _mlContextChecker;
    private readonly SentinelLlmCorrector? _sentinelLlmCorrector;

    public string Name => "OcrQualityWave";
    public int Priority => 58; // Runs after all OCR waves
    public IReadOnlyList<string> Tags => new[] { SignalTags.Content, "ocr", "quality" };

    public OcrQualityWave(
        IOptions<ImageConfig> imageConfig,
        ILogger<OcrQualityWave>? logger = null,
        MlContextChecker? mlContextChecker = null,
        SentinelLlmCorrector? sentinelLlmCorrector = null)
    {
        _config = imageConfig.Value.Ocr;
        _logger = logger;
        _mlContextChecker = mlContextChecker;
        _sentinelLlmCorrector = sentinelLlmCorrector;

        // Initialize spell checker if enabled (Tier 1)
        if (_config.EnableSpellChecking)
        {
            // Dictionary path is optional - SpellChecker will use AppData if not specified
            var dictionaryPath = !string.IsNullOrEmpty(_config.DictionaryPath)
                ? _config.DictionaryPath
                : imageConfig.Value.ModelsDirectory != null
                    ? Path.Combine(imageConfig.Value.ModelsDirectory, "dictionaries")
                    : null;
            _spellChecker = new SpellChecker(dictionaryPath, logger as ILogger<SpellChecker>);
        }
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        // Only run if spell checking is enabled
        if (!_config.EnableSpellChecking || _spellChecker == null)
        {
            signals.Add(new Signal
            {
                Key = "ocr.quality.spell_check_disabled",
                Value = true,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr", "quality" }
            });
            return signals;
        }

        // Get the best OCR text from previous waves
        string? ocrText = null;
        string? ocrSource = null;

        if (context.HasSignal("ocr.corrected.text"))
        {
            ocrText = context.GetValue<string>("ocr.corrected.text");
            ocrSource = "ocr.corrected.text";
        }
        else if (context.HasSignal("ocr.voting.consensus_text"))
        {
            ocrText = context.GetValue<string>("ocr.voting.consensus_text");
            ocrSource = "ocr.voting.consensus_text";
        }
        else if (context.HasSignal("ocr.temporal_median.full_text"))
        {
            ocrText = context.GetValue<string>("ocr.temporal_median.full_text");
            ocrSource = "ocr.temporal_median.full_text";
        }
        else if (context.HasSignal("ocr.full_text"))
        {
            ocrText = context.GetValue<string>("ocr.full_text");
            ocrSource = "ocr.full_text";
        }

        // No OCR text found - distinguish between "skipped" vs "ran and found nothing"
        if (string.IsNullOrWhiteSpace(ocrText))
        {
            // Check if OCR was actually skipped (not just empty result)
            var ocrSkipped = context.GetValue<bool>("ocr.skipped");
            var ocrEnabled = context.HasSignal("ocr.enabled")
                ? context.GetValue<bool>("ocr.enabled")
                : true; // Default to enabled if not specified

            if (ocrSkipped || !ocrEnabled)
            {
                // OCR was skipped - don't assert "no text", just note it wasn't evaluated
                signals.Add(new Signal
                {
                    Key = "ocr.quality.not_evaluated",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "ocr", "quality" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["reason"] = ocrSkipped ? "ocr_skipped" : "ocr_disabled"
                    }
                });
            }
            else
            {
                // OCR ran but found no text
                signals.Add(new Signal
                {
                    Key = "ocr.quality.no_text_detected",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "ocr", "quality" }
                });
            }
            return signals;
        }

        try
        {
            // Load dictionary for configured language
            var dictionaryLoaded = await _spellChecker.LoadDictionaryAsync(_config.SpellCheckLanguage, ct);

            if (!dictionaryLoaded)
            {
                _logger?.LogWarning("Dictionary not available for {Language}, spell checking disabled", _config.SpellCheckLanguage);
                signals.Add(new Signal
                {
                    Key = "ocr.quality.dictionary_unavailable",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "ocr", "quality" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["language"] = _config.SpellCheckLanguage
                    }
                });
                return signals;
            }

            // Tier 1: Check text quality with dictionary + heuristics
            var spellResult = _spellChecker.CheckTextQuality(ocrText, _config.SpellCheckLanguage);

            // Tier 2: ML Context Check (if escalation recommended)
            string? tier2CorrectedText = null;
            double tier2Perplexity = 0;
            if (spellResult.RecommendLlmEscalation && _mlContextChecker != null)
            {
                _logger?.LogInformation("Tier 1 recommended escalation, running Tier 2: ML Context Check");

                // Initialize ML models if not already done
                var mlInitialized = await _mlContextChecker.InitializeAsync(ct);

                if (mlInitialized)
                {
                    var (isValid, perplexity, suggestions, failureReasons) = _mlContextChecker.CheckContext(ocrText);
                    tier2Perplexity = perplexity;

                    _logger?.LogInformation(
                        "ML Context Check: perplexity={Perplexity:F2}, valid={IsValid}, suggestions={Count}, reasons={Reasons}",
                        perplexity, isValid, suggestions.Count, string.Join(", ", failureReasons));

                    // If ML found contextual issues and has suggestions, apply them
                    if (!isValid && suggestions.Any())
                    {
                        tier2CorrectedText = ApplyContextualSuggestions(ocrText, suggestions);
                        _logger?.LogInformation("ML Context suggested correction: '{Original}' → '{Corrected}'",
                            ocrText, tier2CorrectedText);

                        signals.Add(new Signal
                        {
                            Key = "ocr.quality.ml_context_check",
                            Value = new
                            {
                                Perplexity = perplexity,
                                IsValid = isValid,
                                SuggestionCount = suggestions.Count,
                                OriginalText = ocrText,
                                CorrectedText = tier2CorrectedText,
                                FailureReasons = failureReasons
                            },
                            Confidence = 0.8,
                            Source = Name,
                            Tags = new List<string> { "ocr", "quality", "ml", "tier2" },
                            Metadata = new Dictionary<string, object>
                            {
                                ["perplexity"] = perplexity,
                                ["suggestions_count"] = suggestions.Count,
                                ["failure_reasons"] = failureReasons,
                                ["escalation_reason"] = failureReasons.Any()
                                    ? $"Escalated to Tier 2 ML because: {string.Join(", ", failureReasons)}"
                                    : "No escalation needed"
                            }
                        });
                    }
                }
            }

            // Tier 3: Sentinel LLM Correction (if still uncertain or garbled)
            string? finalCorrectedText = tier2CorrectedText ?? ocrText;
            bool tier3Applied = false;

            // Only escalate to Tier 3 if:
            // 1. Tier 1 identified as garbled (< 50% correct)
            // 2. OR Tier 2 made corrections (text was changed)
            // 3. Don't escalate if Tier 2 truly validated text as clean (low perplexity, not neutral)
            // Note: perplexity=50.0 is neutral (unknown bigrams) - don't trust it as validation
            bool tier2TrulyValidated = tier2CorrectedText == null
                                    && tier2Perplexity < 60
                                    && tier2Perplexity > 0
                                    && Math.Abs(tier2Perplexity - 50.0) > 0.1; // Exclude neutral score
            bool needsTier3 = (spellResult.IsGarbled || tier2CorrectedText != null) && !tier2TrulyValidated;

            if (needsTier3 && _sentinelLlmCorrector != null)
            {
                _logger?.LogInformation("Running Tier 3: Sentinel LLM Correction");

                var correctionResult = await _sentinelLlmCorrector.CorrectAsync(
                    tier2CorrectedText ?? ocrText,
                    imagePath,
                    ct);

                if (correctionResult.WasCorrected)
                {
                    finalCorrectedText = correctionResult.CorrectedText;
                    tier3Applied = true;

                    _logger?.LogInformation(
                        "Sentinel LLM corrected: '{Original}' → '{Corrected}' (confidence: {Confidence:F2})",
                        correctionResult.OriginalText, correctionResult.CorrectedText, correctionResult.Confidence);

                    signals.Add(new Signal
                    {
                        Key = "ocr.corrected.text",
                        Value = correctionResult.CorrectedText,
                        Confidence = correctionResult.Confidence,
                        Source = Name,
                        Tags = new List<string> { "ocr", "corrected", "llm", "tier3" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["original_text"] = correctionResult.OriginalText,
                            ["method"] = correctionResult.Method,
                            ["edit_distance"] = correctionResult.EditDistance ?? 0,
                            ["similarity"] = correctionResult.Similarity ?? 0
                        }
                    });

                    // Update context with corrected text for downstream waves
                    context.SetCached("ocr.final_corrected_text", correctionResult.CorrectedText);
                }
            }

            // Emit final corrected text signal (prioritized over voting/temporal median in ledger)
            // This ensures corrected text from Tier 2 or Tier 3 is used
            if (finalCorrectedText != ocrText)
            {
                signals.Add(new Signal
                {
                    Key = "ocr.final.corrected_text",
                    Value = finalCorrectedText,
                    Confidence = tier3Applied ? 0.9 : (tier2CorrectedText != null ? 0.8 : 0.0),
                    Source = Name,
                    Tags = new List<string> { "ocr", "corrected", tier3Applied ? "tier3" : tier2CorrectedText != null ? "tier2" : "tier1" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["original_text"] = ocrText,
                        ["tier2_applied"] = tier2CorrectedText != null,
                        ["tier3_applied"] = tier3Applied
                    }
                });
            }

            // Log correction pipeline summary
            var pipelineSummary = new Dictionary<string, object>
            {
                ["tier1_checked"] = true,
                ["tier1_score"] = spellResult.CorrectWordsRatio,
                ["tier2_applied"] = tier2CorrectedText != null,
                ["tier2_perplexity"] = tier2Perplexity,
                ["tier3_applied"] = tier3Applied,
                ["final_text"] = finalCorrectedText
            };

            signals.Add(new Signal
            {
                Key = "ocr.quality.correction_pipeline",
                Value = pipelineSummary,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr", "quality", "pipeline_summary" },
                Metadata = pipelineSummary
            });

            // Emit quality signals
            signals.Add(new Signal
            {
                Key = "ocr.quality.spell_check_score",
                Value = spellResult.CorrectWordsRatio,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr", "quality" },
                Metadata = new Dictionary<string, object>
                {
                    ["total_words"] = spellResult.TotalWords,
                    ["correct_words"] = spellResult.CorrectWords,
                    ["misspelled_count"] = spellResult.MisspelledWords.Count,
                    ["language"] = spellResult.Language,
                    ["source_signal"] = ocrSource ?? "unknown"
                }
            });

            signals.Add(new Signal
            {
                Key = "ocr.quality.is_garbled",
                Value = spellResult.IsGarbled,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr", "quality" },
                Metadata = new Dictionary<string, object>
                {
                    ["threshold"] = _config.SpellCheckQualityThreshold,
                    ["actual_score"] = spellResult.CorrectWordsRatio
                }
            });

            // If quality is low, suggest LLM correction (signal for sentinel)
            if (spellResult.IsGarbled)
            {
                _logger?.LogWarning(
                    "OCR text appears garbled (quality: {Quality:F2}), LLM correction recommended",
                    spellResult.CorrectWordsRatio);

                signals.Add(new Signal
                {
                    Key = "ocr.quality.correction_needed",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "ocr", "quality", "action_required" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["quality_score"] = spellResult.CorrectWordsRatio,
                        ["text_length"] = ocrText.Length,
                        ["misspelled_words"] = spellResult.MisspelledWords.Take(10).ToList(), // First 10 for diagnostics
                        ["correction_method"] = "llm_sentinel",
                        ["reason"] = "garbled"
                    }
                });

                // Cache the garbled text for sentinel to access
                context.SetCached("ocr.garbled_text", ocrText);
                context.SetCached("ocr.spell_check_result", spellResult);
            }
            else if (spellResult.RecommendLlmEscalation)
            {
                _logger?.LogInformation(
                    "OCR text has moderate quality (score: {Quality:F2}), LLM escalation recommended for context-aware correction",
                    spellResult.CorrectWordsRatio);

                signals.Add(new Signal
                {
                    Key = "ocr.quality.llm_escalation_recommended",
                    Value = true,
                    Confidence = 0.7, // Moderate confidence - not garbled but uncertain
                    Source = Name,
                    Tags = new List<string> { "ocr", "quality", "escalation_recommended" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["quality_score"] = spellResult.CorrectWordsRatio,
                        ["text_length"] = ocrText.Length,
                        ["misspelled_words"] = spellResult.MisspelledWords,
                        ["correction_method"] = "ml_or_llm",
                        ["reason"] = "uncertain_quality_or_context_needed"
                    }
                });

                // Cache for potential ML/LLM correction
                context.SetCached("ocr.uncertain_text", ocrText);
                context.SetCached("ocr.spell_check_result", spellResult);
            }
            else
            {
                _logger?.LogInformation(
                    "OCR text quality good (score: {Quality:F2}, {Correct}/{Total} words)",
                    spellResult.CorrectWordsRatio,
                    spellResult.CorrectWords,
                    spellResult.TotalWords);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during spell checking");
            signals.Add(new Signal
            {
                Key = "ocr.quality.error",
                Value = ex.Message,
                Confidence = 0,
                Source = Name,
                Tags = new List<string> { "ocr", "quality", "error" }
            });
        }

        return signals;
    }

    /// <summary>
    /// Apply ML contextual suggestions to correct OCR text
    /// </summary>
    private string ApplyContextualSuggestions(string originalText, List<ContextSuggestion> suggestions)
    {
        var words = originalText.Split(' ');
        var correctedWords = new string[words.Length];
        Array.Copy(words, correctedWords, words.Length);

        // Apply suggestions in order of word index
        foreach (var suggestion in suggestions.OrderBy(s => s.WordIndex))
        {
            if (suggestion.WordIndex < correctedWords.Length && suggestion.Alternatives.Any())
            {
                // Take the first (best) alternative
                correctedWords[suggestion.WordIndex] = suggestion.Alternatives.First();
            }
        }

        return string.Join(" ", correctedWords);
    }
}
