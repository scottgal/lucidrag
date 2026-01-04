using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mostlylucid.DocSummarizer.Images.Services.Ocr.PostProcessing;

/// <summary>
/// Tier 3: Sentinel LLM correction for OCR text
/// Uses vision LLM to re-analyze the image and verify/correct OCR output
/// Most accurate but slowest tier - only used when Tier 1 & 2 uncertain
/// </summary>
public class SentinelLlmCorrector
{
    private readonly ImageConfig _config;
    private readonly ILogger<SentinelLlmCorrector>? _logger;
    private readonly HttpClient _httpClient;

    public SentinelLlmCorrector(
        IOptions<ImageConfig> config,
        ILogger<SentinelLlmCorrector>? logger = null,
        HttpClient? httpClient = null)
    {
        _config = config.Value;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Correct OCR text using vision LLM re-query
    /// </summary>
    public async Task<CorrectionResult> CorrectAsync(
        string ocrText,
        string imagePath,
        CancellationToken ct = default)
    {
        if (!_config.EnableVisionLlm)
        {
            _logger?.LogWarning("Vision LLM is disabled, skipping Sentinel LLM correction");
            return new CorrectionResult
            {
                OriginalText = ocrText,
                CorrectedText = ocrText,
                WasCorrected = false,
                Confidence = 0,
                Method = "skipped_disabled"
            };
        }

        try
        {
            // Convert image to base64
            var imageBase64 = await ConvertImageToBase64(imagePath, ct);

            // Ask vision LLM to verify and correct the OCR text
            var correctedText = await QueryVisionLlmForCorrectionAsync(ocrText, imageBase64, ct);

            if (string.IsNullOrWhiteSpace(correctedText) || correctedText == ocrText)
            {
                _logger?.LogInformation("Vision LLM did not suggest corrections for: {Text}", ocrText);
                return new CorrectionResult
                {
                    OriginalText = ocrText,
                    CorrectedText = ocrText,
                    WasCorrected = false,
                    Confidence = 0.5,
                    Method = "llm_no_change"
                };
            }

            // Calculate edit distance to measure how much changed
            var editDistance = LevenshteinDistance(ocrText, correctedText);
            var similarity = 1.0 - (editDistance / (double)Math.Max(ocrText.Length, correctedText.Length));

            _logger?.LogInformation(
                "Vision LLM corrected: '{Original}' → '{Corrected}' (similarity: {Similarity:F2})",
                ocrText, correctedText, similarity);

            return new CorrectionResult
            {
                OriginalText = ocrText,
                CorrectedText = correctedText,
                WasCorrected = true,
                Confidence = 0.9, // High confidence in LLM corrections
                Method = "sentinel_llm",
                EditDistance = editDistance,
                Similarity = similarity
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to correct text using Sentinel LLM");
            return new CorrectionResult
            {
                OriginalText = ocrText,
                CorrectedText = ocrText,
                WasCorrected = false,
                Confidence = 0,
                Method = "error",
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Query vision LLM to verify and correct OCR text
    /// </summary>
    private async Task<string?> QueryVisionLlmForCorrectionAsync(
        string ocrText,
        string imageBase64,
        CancellationToken ct)
    {
        var ollamaUrl = _config.OllamaBaseUrl ?? "http://localhost:11434";
        var model = _config.VisionLlmModel ?? "llava";

        var prompt = $@"I extracted this text from an image using OCR: ""{ocrText}""

Please look at the image and verify if the OCR is correct. If there are any errors (like 'Bf' instead of 'of', or 'rn' instead of 'm'), correct them.

IMPORTANT: Only output the corrected text, nothing else. If the OCR is already correct, output the same text.";

        var request = new
        {
            model = model,
            prompt = prompt,
            images = new[] { imageBase64 },
            stream = false,
            options = new
            {
                temperature = 0.1, // Low temperature for deterministic corrections
                top_p = 0.9
            }
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{ollamaUrl}/api/generate",
                request,
                ct);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

            if (result.TryGetProperty("response", out var responseText))
            {
                var correctedText = responseText.GetString()?.Trim() ?? ocrText;

                // Clean up response (remove quotes, extra whitespace)
                correctedText = correctedText.Trim('"', '\'', ' ', '\n', '\r');

                return correctedText;
            }

            _logger?.LogWarning("Vision LLM response missing 'response' property");
            return ocrText;
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogError(ex, "Failed to query Vision LLM for correction (is Ollama running?)");
            throw;
        }
    }

    /// <summary>
    /// Convert image to base64 for Ollama
    /// </summary>
    private async Task<string> ConvertImageToBase64(string imagePath, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(imagePath, ct);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Calculate Levenshtein distance between two strings
    /// </summary>
    private static int LevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s))
            return string.IsNullOrEmpty(t) ? 0 : t.Length;

        if (string.IsNullOrEmpty(t))
            return s.Length;

        int[,] d = new int[s.Length + 1, t.Length + 1];

        for (int i = 0; i <= s.Length; i++)
            d[i, 0] = i;

        for (int j = 0; j <= t.Length; j++)
            d[0, j] = j;

        for (int i = 1; i <= s.Length; i++)
        {
            for (int j = 1; j <= t.Length; j++)
            {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;

                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[s.Length, t.Length];
    }
}

/// <summary>
/// Result of Sentinel LLM correction
/// </summary>
public class CorrectionResult
{
    public required string OriginalText { get; set; }
    public required string CorrectedText { get; set; }
    public bool WasCorrected { get; set; }
    public double Confidence { get; set; }
    public required string Method { get; set; }
    public int? EditDistance { get; set; }
    public double? Similarity { get; set; }
    public string? Error { get; set; }
}
