using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Contradiction Detection Wave - Runs after all other waves to detect signal conflicts.
/// Uses config-driven rules to identify contradictions between different analysis sources.
/// Priority: 5 (runs last, after all content/quality/forensic waves)
/// </summary>
public class ContradictionWave : IAnalysisWave
{
    private readonly ContradictionDetector _detector;
    private readonly ImageConfig _config;
    private readonly ILogger<ContradictionWave>? _logger;

    public string Name => "ContradictionWave";
    public int Priority => 5; // Lowest priority - runs after all other waves
    public IReadOnlyList<string> Tags => new[] { SignalTags.Quality, "validation", "contradiction" };

    public ContradictionWave(
        ContradictionDetector detector,
        IOptions<ImageConfig> config,
        ILogger<ContradictionWave>? logger = null)
    {
        _detector = detector;
        _config = config.Value;
        _logger = logger;
    }

    public Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        // Skip if contradiction detection is disabled
        if (!_config.Contradiction.Enabled)
        {
            signals.Add(new Signal
            {
                Key = "validation.contradiction.disabled",
                Value = true,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "validation", "config" }
            });
            return Task.FromResult<IEnumerable<Signal>>(signals);
        }

        try
        {
            var contradictions = _detector.DetectContradictions(context).ToList();

            // Summary signal
            signals.Add(new Signal
            {
                Key = "validation.contradiction.count",
                Value = contradictions.Count,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "validation", "contradiction", "summary" },
                Metadata = new Dictionary<string, object>
                {
                    ["has_contradictions"] = contradictions.Count > 0,
                    ["rules_checked"] = _detector.GetRules().Count(r => r.Enabled)
                }
            });

            if (contradictions.Count == 0)
            {
                signals.Add(new Signal
                {
                    Key = "validation.contradiction.status",
                    Value = "clean",
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "validation", "status" }
                });

                _logger?.LogDebug("No contradictions detected in {ImagePath}", imagePath);
                return Task.FromResult<IEnumerable<Signal>>(signals);
            }

            // Signal for each contradiction
            foreach (var contradiction in contradictions)
            {
                var key = $"validation.contradiction.{contradiction.Rule.RuleId}";

                signals.Add(new Signal
                {
                    Key = key,
                    Value = contradiction.Explanation,
                    Confidence = CalculateContradictionConfidence(contradiction),
                    Source = Name,
                    Tags = new List<string>
                    {
                        "validation",
                        "contradiction",
                        contradiction.EffectiveSeverity.ToString().ToLowerInvariant()
                    },
                    Metadata = new Dictionary<string, object>
                    {
                        ["rule_id"] = contradiction.Rule.RuleId,
                        ["severity"] = contradiction.EffectiveSeverity.ToString(),
                        ["resolution"] = contradiction.Rule.Resolution.ToString(),
                        ["signal_a_key"] = contradiction.SignalA.Key,
                        ["signal_a_value"] = contradiction.SignalA.Value?.ToString() ?? "null",
                        ["signal_a_confidence"] = contradiction.SignalA.Confidence,
                        ["signal_b_key"] = contradiction.SignalB?.Key ?? "missing",
                        ["signal_b_value"] = contradiction.SignalB?.Value?.ToString() ?? "null",
                        ["signal_b_confidence"] = contradiction.SignalB?.Confidence ?? 0,
                        ["detected_at"] = contradiction.DetectedAt.ToString("O")
                    }
                });

                // Log based on severity
                LogContradiction(contradiction, imagePath);
            }

            // Overall status signal
            var worstSeverity = contradictions.Max(c => c.EffectiveSeverity);
            signals.Add(new Signal
            {
                Key = "validation.contradiction.status",
                Value = worstSeverity switch
                {
                    ContradictionSeverity.Critical => "critical",
                    ContradictionSeverity.Error => "error",
                    ContradictionSeverity.Warning => "warning",
                    _ => "info"
                },
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "validation", "status" },
                Metadata = new Dictionary<string, object>
                {
                    ["worst_severity"] = worstSeverity.ToString(),
                    ["needs_review"] = worstSeverity >= ContradictionSeverity.Warning,
                    ["should_escalate"] = contradictions.Any(c =>
                        c.Rule.Resolution == ResolutionStrategy.EscalateToLlm),
                    ["needs_manual_review"] = contradictions.Any(c =>
                        c.Rule.Resolution == ResolutionStrategy.ManualReview)
                }
            });

            // Check rejection policy
            if (_config.Contradiction.RejectOnCritical &&
                worstSeverity == ContradictionSeverity.Critical)
            {
                signals.Add(new Signal
                {
                    Key = "validation.contradiction.rejected",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "validation", "rejection", "critical" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["reason"] = "Critical contradiction detected",
                        ["contradictions"] = contradictions
                            .Where(c => c.EffectiveSeverity == ContradictionSeverity.Critical)
                            .Select(c => c.Rule.RuleId)
                            .ToList()
                    }
                });

                _logger?.LogError(
                    "Image {ImagePath} REJECTED due to critical contradictions: {Contradictions}",
                    imagePath,
                    string.Join(", ", contradictions
                        .Where(c => c.EffectiveSeverity == ContradictionSeverity.Critical)
                        .Select(c => c.Rule.RuleId)));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Contradiction detection failed for {ImagePath}", imagePath);
            signals.Add(new Signal
            {
                Key = "validation.contradiction.error",
                Value = ex.Message,
                Confidence = 0,
                Source = Name,
                Tags = new List<string> { "validation", "error" }
            });
        }

        return Task.FromResult<IEnumerable<Signal>>(signals);
    }

    private double CalculateContradictionConfidence(ContradictionResult contradiction)
    {
        // Base confidence on the signals involved
        var confidenceA = contradiction.SignalA.Confidence;
        var confidenceB = contradiction.SignalB?.Confidence ?? 0.5;

        // Higher confidence signals = more confident contradiction detection
        return Math.Min(confidenceA, confidenceB);
    }

    private void LogContradiction(ContradictionResult contradiction, string imagePath)
    {
        var message = $"[{contradiction.Rule.RuleId}] {contradiction.Explanation}";

        switch (contradiction.EffectiveSeverity)
        {
            case ContradictionSeverity.Critical:
                _logger?.LogError("CRITICAL contradiction in {ImagePath}: {Message}", imagePath, message);
                break;
            case ContradictionSeverity.Error:
                _logger?.LogError("Contradiction in {ImagePath}: {Message}", imagePath, message);
                break;
            case ContradictionSeverity.Warning:
                _logger?.LogWarning("Contradiction in {ImagePath}: {Message}", imagePath, message);
                break;
            default:
                _logger?.LogInformation("Contradiction in {ImagePath}: {Message}", imagePath, message);
                break;
        }
    }
}
