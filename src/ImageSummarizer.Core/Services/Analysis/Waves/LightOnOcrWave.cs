using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// LightOnOCR wave - State-of-the-art 1B-parameter OCR model from LightOn AI.
///
/// Features:
/// - 3.3× faster than Chandra OCR, 1.7× faster than OlmOCR
/// - Handles tables, receipts, forms, multi-column layouts, math notation
/// - Multilingual support (11 languages)
/// - End-to-end OCR without preprocessing pipelines
///
/// Pull model: ollama pull aipib/LightOnOCR-1B-1025
///
/// Priority: 52 (runs after DeepseekOcr at 53, before OlmOcr2 at 51)
/// When LightOnOcrAsPrimary=true, priority becomes 65 (runs before most OCR waves)
///
/// Signals produced:
/// - ocr.lightonocr.markdown: Extracted text in markdown format
/// - ocr.lightonocr.text: Plain text extraction
/// - ocr.lightonocr.duration_ms: Processing time for benchmarking
/// </summary>
public class LightOnOcrWave : IAnalysisWave
{
    private readonly OcrConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<LightOnOcrWave>? _logger;

    public string Name => "LightOnOcrWave";

    // Priority 52 by default (between DeepseekOcr=53 and OlmOcr2=51)
    // When configured as primary, use priority 65 (before most OCR waves)
    public int Priority => _config.LightOnOcrAsPrimary ? 65 : 52;

    public IReadOnlyList<string> Tags => new[] { SignalTags.Content, "ocr", "vlm", "lightonocr" };

    public LightOnOcrWave(
        IOptions<ImageConfig> imageConfig,
        ILogger<LightOnOcrWave>? logger = null)
    {
        _config = imageConfig.Value.Ocr;
        _logger = logger;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_config.LightOnOcrBaseUrl),
            Timeout = TimeSpan.FromSeconds(_config.LightOnOcrTimeoutSeconds)
        };

        if (!string.IsNullOrWhiteSpace(_config.LightOnOcrApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _config.LightOnOcrApiKey);
        }
    }

    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        if (!_config.EnableLightOnOcr)
            return false;

        // Force run if benchmark mode with ForceRunAllSystems
        if (_config.Benchmark.Enabled && _config.Benchmark.ForceRunAllSystems)
            return true;

        if (context.IsWaveSkippedByRouting(Name))
            return false;

        // Don't run if we already have LightOnOCR results
        if (context.HasSignal("ocr.lightonocr.text") || context.HasSignal("ocr.lightonocr.markdown"))
            return false;

        // If configured as primary, always run (unless skipped above)
        if (_config.LightOnOcrAsPrimary)
            return true;

        // Otherwise, run as escalation when OCR quality is poor
        var ocrGarbled = context.GetValue<bool>("ocr.quality.is_garbled");
        var needsCorrection = context.GetValue<bool>("ocr.quality.correction_needed") ||
                              context.GetValue<bool>("ocr.quality.llm_escalation_recommended");
        var noTextDetected = context.GetValue<bool>("ocr.quality.no_text_detected");
        var textLikeliness = context.GetValue<double>("content.text_likeliness");

        var existingText = context.GetValue<string>("ocr.full_text")
                           ?? context.GetValue<string>("ocr.voting.consensus_text")
                           ?? context.GetValue<string>("ocr.ml.fused_text")
                           ?? context.GetValue<string>("florence2.ocr_text");

        if (ocrGarbled || needsCorrection)
            return true;

        if (noTextDetected && textLikeliness > 0.3)
            return true;

        return textLikeliness > 0.3 && string.IsNullOrWhiteSpace(existingText);
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();
        var sw = Stopwatch.StartNew();

        // Use preprocessed image if available (from OcrPreprocessingWave)
        var effectivePath = context.GetCached<string>("preprocessing.enhanced_image_path") ?? imagePath;

        try
        {
            _logger?.LogDebug("Running LightOnOCR on {ImagePath}", effectivePath);

            var markdown = await ExtractMarkdownAsync(effectivePath, ct);
            if (string.IsNullOrWhiteSpace(markdown))
            {
                _logger?.LogDebug("LightOnOCR returned empty result for {ImagePath}", imagePath);
                return signals;
            }

            var cleanedMarkdown = StripCodeFences(markdown);
            var plainText = StripMarkdown(cleanedMarkdown);
            var extractedText = _config.LightOnOcrPreferMarkdown
                ? cleanedMarkdown
                : plainText;

            // LightOnOCR-specific signals
            signals.Add(new Signal
            {
                Key = "ocr.lightonocr.markdown",
                Value = cleanedMarkdown,
                Confidence = 0.94, // LightOnOCR has excellent accuracy
                Source = Name,
                Tags = new List<string> { "ocr", "markdown", "lightonocr" },
                Metadata = new Dictionary<string, object>
                {
                    ["model"] = _config.LightOnOcrModelName,
                    ["output_format"] = "markdown",
                    ["length"] = cleanedMarkdown.Length
                }
            });

            signals.Add(new Signal
            {
                Key = "ocr.lightonocr.text",
                Value = plainText,
                Confidence = 0.90,
                Source = Name,
                Tags = new List<string> { "ocr", "text", "lightonocr" },
                Metadata = new Dictionary<string, object>
                {
                    ["model"] = _config.LightOnOcrModelName,
                    ["text_length"] = plainText.Length
                }
            });

            // Only emit generic OCR signals if NOT in benchmark mode
            if (!(_config.Benchmark.Enabled && _config.Benchmark.ForceRunAllSystems))
            {
                signals.Add(new Signal
                {
                    Key = "ocr.markdown",
                    Value = cleanedMarkdown,
                    Confidence = 0.94,
                    Source = Name,
                    Tags = new List<string> { "ocr", "markdown" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["model"] = _config.LightOnOcrModelName
                    }
                });

                signals.Add(new Signal
                {
                    Key = "ocr.text",
                    Value = plainText,
                    Confidence = 0.90,
                    Source = Name,
                    Tags = new List<string> { "ocr", "text" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["model"] = _config.LightOnOcrModelName
                    }
                });

                signals.Add(new Signal
                {
                    Key = "ocr.full_text",
                    Value = plainText,
                    Confidence = 0.90,
                    Source = Name,
                    Tags = new List<string> { "ocr", SignalTags.Content }
                });
            }

            // Update content signals if not already set
            var existingContent = context.GetValue<string>("content.extracted_text");
            if (string.IsNullOrWhiteSpace(existingContent))
            {
                signals.Add(new Signal
                {
                    Key = "content.extracted_text",
                    Value = extractedText,
                    Confidence = 0.88,
                    Source = Name,
                    Tags = new List<string> { SignalTags.Content, "text" }
                });
            }

            if (!string.IsNullOrWhiteSpace(cleanedMarkdown))
            {
                signals.Add(new Signal
                {
                    Key = "content.extracted_markdown",
                    Value = cleanedMarkdown,
                    Confidence = 0.92,
                    Source = Name,
                    Tags = new List<string> { SignalTags.Content, "markdown" }
                });
            }

            // Timing signal for benchmarking
            sw.Stop();
            signals.Add(new Signal
            {
                Key = "ocr.lightonocr.duration_ms",
                Value = sw.ElapsedMilliseconds,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr", "timing", "benchmark" }
            });

            _logger?.LogInformation(
                "LightOnOCR extracted {TextLength} chars from {ImagePath} in {Duration}ms",
                plainText.Length, Path.GetFileName(imagePath), sw.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "LightOnOCR service unavailable at {BaseUrl}", _config.LightOnOcrBaseUrl);
            signals.Add(new Signal
            {
                Key = "ocr.lightonocr.error",
                Value = $"Service unavailable: {ex.Message}",
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr", "error" }
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "LightOnOCR failed for {Path}", imagePath);
            signals.Add(new Signal
            {
                Key = "ocr.lightonocr.error",
                Value = ex.Message,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr", "error" }
            });
        }

        return signals;
    }

    private async Task<string> ExtractMarkdownAsync(string imagePath, CancellationToken ct)
    {
        var imageBytes = await File.ReadAllBytesAsync(imagePath, ct);
        var base64Image = Convert.ToBase64String(imageBytes);

        // LightOnOCR prompt - the model is specifically trained for OCR
        // so a simple prompt works well
        var prompt = _config.LightOnOcrPreferMarkdown
            ? "Extract all visible text from this image. Return clean Markdown preserving layout, tables, headings, and lists. No explanations."
            : "Extract all visible text from this image. Return plain text only.";

        // Use Ollama native /api/chat format with images array
        var request = new
        {
            model = _config.LightOnOcrModelName,
            stream = false,
            options = new { temperature = 0.0 },
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = prompt,
                    images = new[] { base64Image }
                }
            }
        };

        var response = await _httpClient.PostAsJsonAsync("/api/chat", request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(ct);
            _logger?.LogWarning("LightOnOCR API error: {Status} {Body}", response.StatusCode, errorContent);
            return string.Empty;
        }

        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(ct);
        return result?.Message?.Content?.Trim() ?? string.Empty;
    }

    // Ollama native API response format
    private record OllamaChatResponse(
        [property: JsonPropertyName("message")] OllamaMessage? Message);

    private record OllamaMessage(
        [property: JsonPropertyName("content")] string Content);

    private static string StripCodeFences(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        var trimmed = input.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var lines = trimmed.Split('\n');
        if (lines.Length < 2)
            return trimmed;

        return string.Join('\n', lines.Skip(1).Reverse().Skip(1).Reverse()).Trim();
    }

    private static string StripMarkdown(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        var text = markdown;
        text = Regex.Replace(text, @"```[\s\S]*?```", " ", RegexOptions.Multiline);
        text = Regex.Replace(text, @"`([^`]*)`", "$1");
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]+\)", " ");
        text = Regex.Replace(text, @"\[[^\]]*\]\([^)]+\)", "$1");
        text = Regex.Replace(text, @"^\s{0,3}#{1,6}\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*[-*+]\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*\d+\.\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*\|", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\|\s*$", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*:?-{3,}:?\s*$", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\s{2,}", " ");
        return text.Trim();
    }
}
