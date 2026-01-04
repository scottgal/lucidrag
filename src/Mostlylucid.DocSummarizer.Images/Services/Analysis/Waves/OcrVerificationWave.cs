using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// OCR verification wave using vision LLM to validate and correct OCR results.
/// Provides concordance checking between Tesseract OCR and vision model.
///
/// This implements an offline escalation step where uncertain OCR results
/// are verified by a more powerful vision model (MiniCPM-V via Ollama).
///
/// References:
/// - MiniCPM-V: https://ollama.com/library/minicpm-v
/// - OCR verification patterns: Compare fast OCR with slow but accurate vision LLM
/// </summary>
public class OcrVerificationWave : IAnalysisWave
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OcrVerificationWave>? _logger;
    private readonly string _model;
    private readonly bool _enabled;
    private readonly double _confidenceThreshold;

    public string Name => "OcrVerificationWave";
    public int Priority => 55; // Lower priority than OcrWave (60) - runs after
    public IReadOnlyList<string> Tags => new[] { SignalTags.Content, "ocr", "verification" };

    public OcrVerificationWave(
        string ollamaBaseUrl = "http://localhost:11434",
        string model = "minicpm-v:8b",
        double confidenceThreshold = 0.7,
        bool enabled = true,
        ILogger<OcrVerificationWave>? logger = null)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(ollamaBaseUrl),
            Timeout = TimeSpan.FromMinutes(3) // OCR can be slow
        };
        _model = model;
        _confidenceThreshold = confidenceThreshold;
        _enabled = enabled;
        _logger = logger;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        if (!_enabled)
        {
            return signals;
        }

        // Check if Ollama is available
        var available = await CheckOllamaAvailableAsync(ct);
        if (!available)
        {
            _logger?.LogWarning("Ollama not available, skipping OCR verification");
            return signals;
        }

        // Get OCR results from context
        var ocrSignals = context.GetSignals("ocr.text_region").ToList();
        if (!ocrSignals.Any())
        {
            // No OCR results to verify
            return signals;
        }

        // Check if verification is needed (low confidence OCR results)
        var avgConfidence = ocrSignals.Average(s => s.Confidence);
        if (avgConfidence >= _confidenceThreshold)
        {
            signals.Add(new Signal
            {
                Key = "ocr.verification_skipped",
                Value = true,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr", "verification" },
                Metadata = new Dictionary<string, object>
                {
                    ["reason"] = "OCR confidence above threshold",
                    ["avg_confidence"] = avgConfidence,
                    ["threshold"] = _confidenceThreshold
                }
            });
            return signals;
        }

        try
        {
            _logger?.LogInformation("Verifying OCR with vision LLM for {ImagePath} (avg confidence: {Confidence:F2})",
                imagePath, avgConfidence);

            // Use vision LLM to extract text independently
            var visionText = await ExtractTextWithVisionLlmAsync(imagePath, ct);

            if (string.IsNullOrWhiteSpace(visionText))
            {
                signals.Add(new Signal
                {
                    Key = "ocr.verification_no_text",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "ocr", "verification" }
                });
                return signals;
            }

            // Get original OCR full text
            var ocrFullText = context.GetValue<string>("ocr.full_text") ?? string.Empty;

            // Calculate concordance (similarity between OCR and vision LLM)
            var concordance = CalculateConcordance(ocrFullText, visionText);

            signals.Add(new Signal
            {
                Key = "ocr.vision_llm_text",
                Value = visionText,
                Confidence = 0.95, // Vision LLM typically very accurate for text
                Source = Name,
                Tags = new List<string> { "ocr", SignalTags.Content, "verification" },
                Metadata = new Dictionary<string, object>
                {
                    ["model"] = _model,
                    ["original_ocr_confidence"] = avgConfidence
                }
            });

            signals.Add(new Signal
            {
                Key = "ocr.concordance",
                Value = concordance,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr", "verification", "quality" },
                Metadata = new Dictionary<string, object>
                {
                    ["ocr_text_length"] = ocrFullText.Length,
                    ["vision_text_length"] = visionText.Length,
                    ["interpretation"] = InterpretConcordance(concordance)
                }
            });

            // If concordance is low, flag for manual review
            if (concordance < 0.5)
            {
                signals.Add(new Signal
                {
                    Key = "ocr.discordance_detected",
                    Value = true,
                    Confidence = 0.9,
                    Source = Name,
                    Tags = new List<string> { "ocr", "verification", SignalTags.Quality },
                    Metadata = new Dictionary<string, object>
                    {
                        ["concordance"] = concordance,
                        ["requires_manual_review"] = true,
                        ["ocr_text_preview"] = Truncate(ocrFullText, 200),
                        ["vision_text_preview"] = Truncate(visionText, 200)
                    }
                });
            }

            // Suggest which text to trust
            var suggestedText = concordance < 0.5 && avgConfidence < 0.6
                ? visionText  // Use vision LLM if concordance is low and OCR confidence is low
                : ocrFullText; // Otherwise trust Tesseract

            signals.Add(new Signal
            {
                Key = "ocr.verified_text",
                Value = suggestedText,
                Confidence = Math.Max(avgConfidence, 0.95 * concordance), // Confidence based on concordance
                Source = Name,
                Tags = new List<string> { "ocr", SignalTags.Content, "verified" },
                Metadata = new Dictionary<string, object>
                {
                    ["source"] = suggestedText == visionText ? "vision_llm" : "tesseract",
                    ["concordance"] = concordance
                }
            });

            _logger?.LogInformation("OCR verification complete: concordance={Concordance:F2}, suggested_source={Source}",
                concordance, suggestedText == visionText ? "vision_llm" : "tesseract");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "OCR verification failed");

            signals.Add(new Signal
            {
                Key = "ocr.verification_error",
                Value = ex.Message,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "error" }
            });
        }

        return signals;
    }

    private async Task<bool> CheckOllamaAvailableAsync(CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/tags", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> ExtractTextWithVisionLlmAsync(string imagePath, CancellationToken ct)
    {
        try
        {
            // Read image and convert to base64
            var imageBytes = await File.ReadAllBytesAsync(imagePath, ct);
            var base64Image = Convert.ToBase64String(imageBytes);

            var request = new
            {
                model = _model,
                prompt = "Extract all visible text from this image. Return only the text content, preserving layout and structure. Be comprehensive and accurate.",
                images = new[] { base64Image },
                stream = false
            };

            var response = await _httpClient.PostAsJsonAsync("/api/generate", request, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Vision LLM text extraction failed: {Status}", response.StatusCode);
                return string.Empty;
            }

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(ct);
            return result?.Response?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to extract text with vision LLM");
            return string.Empty;
        }
    }

    /// <summary>
    /// Calculate concordance (similarity) between two text strings.
    /// Uses simple Jaccard similarity on word sets.
    /// </summary>
    private static double CalculateConcordance(string text1, string text2)
    {
        if (string.IsNullOrWhiteSpace(text1) && string.IsNullOrWhiteSpace(text2))
            return 1.0;

        if (string.IsNullOrWhiteSpace(text1) || string.IsNullOrWhiteSpace(text2))
            return 0.0;

        // Normalize and tokenize
        var words1 = text1.ToLowerInvariant()
            .Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();

        var words2 = text2.ToLowerInvariant()
            .Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();

        // Jaccard similarity
        var intersection = words1.Intersect(words2).Count();
        var union = words1.Union(words2).Count();

        return union > 0 ? (double)intersection / union : 0.0;
    }

    private static string InterpretConcordance(double concordance)
    {
        return concordance switch
        {
            >= 0.8 => "High agreement - results are consistent",
            >= 0.5 => "Moderate agreement - some differences detected",
            >= 0.3 => "Low agreement - significant differences",
            _ => "Very low agreement - results conflict"
        };
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text.Substring(0, maxLength) + "...";
    }

    private record OllamaGenerateResponse(
        [property: JsonPropertyName("response")] string Response,
        [property: JsonPropertyName("done")] bool Done);
}
