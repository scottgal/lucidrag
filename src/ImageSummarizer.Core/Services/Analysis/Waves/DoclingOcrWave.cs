using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Services;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Image OCR wave using Docling service for text extraction.
/// Docling provides advanced document understanding with layout analysis,
/// which can be beneficial for images containing structured text, tables, or forms.
///
/// Priority: 58 (runs before native OcrWave at 60)
/// When enabled, can either supplement or replace native OCR based on config.
///
/// Signals produced:
/// - ocr.docling.text: Full extracted text
/// - ocr.docling.confidence: Extraction confidence
/// - ocr.docling.available: Whether Docling service was available
/// </summary>
public sealed class DoclingOcrWave : IAnalysisWave, IAsyncDisposable
{
    private readonly DoclingConfig _config;
    private readonly ILogger<DoclingOcrWave>? _logger;
    private readonly Lazy<DoclingClient> _clientLazy;
    private bool? _isAvailable;
    private DateTime _lastAvailabilityCheck = DateTime.MinValue;
    private static readonly TimeSpan AvailabilityCacheDuration = TimeSpan.FromMinutes(5);

    public string Name => "DoclingOcrWave";
    public int Priority => 58; // Just before native OcrWave (60)
    public IReadOnlyList<string> Tags => [SignalTags.Content, "ocr", "text", "docling"];

    public DoclingOcrWave(
        IOptions<DocSummarizerConfig> config,
        ILogger<DoclingOcrWave>? logger = null)
    {
        _config = config.Value.Docling;
        _logger = logger;
        _clientLazy = new Lazy<DoclingClient>(() => new DoclingClient(_config));
    }

    private DoclingClient Client => _clientLazy.Value;

    /// <summary>
    /// Check if this wave should run.
    /// </summary>
    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        // Check master enable
        if (!_config.Enabled || !_config.Images.Enabled)
            return false;

        // Check if extension is supported
        var extension = Path.GetExtension(imagePath);
        if (!_config.Images.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return false;

        // Skip if routed to skip
        if (context.IsWaveSkippedByRouting(Name))
        {
            _logger?.LogDebug("DoclingOcrWave skipped by routing");
            return false;
        }

        // Check if service is available (cached)
        return IsServiceAvailable();
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();
        var startTime = DateTime.UtcNow;

        try
        {
            _logger?.LogDebug("Running Docling OCR on {ImagePath}", imagePath);

            // Convert image to markdown using Docling
            var markdown = await Client.ConvertAsync(imagePath, ct);
            var processingTime = DateTime.UtcNow - startTime;

            if (string.IsNullOrWhiteSpace(markdown))
            {
                _logger?.LogDebug("Docling returned empty text for {ImagePath}", imagePath);

                signals.Add(new Signal
                {
                    Key = "ocr.docling.available",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = [SignalTags.Metadata]
                });

                signals.Add(new Signal
                {
                    Key = "ocr.docling.text",
                    Value = "",
                    Confidence = 0.0,
                    Source = Name,
                    Tags = [SignalTags.Content, "ocr"]
                });

                return signals;
            }

            // Clean up markdown (remove headers/formatting for pure OCR text)
            var cleanText = CleanMarkdownToText(markdown);

            _logger?.LogInformation(
                "Docling OCR extracted {CharCount} chars from {ImagePath} in {TimeMs}ms",
                cleanText.Length,
                Path.GetFileName(imagePath),
                processingTime.TotalMilliseconds);

            // Add extracted text signal
            signals.Add(new Signal
            {
                Key = "ocr.docling.text",
                Value = cleanText,
                Confidence = 0.85, // Docling generally has good accuracy
                Source = Name,
                Tags = [SignalTags.Content, "ocr"],
                Metadata = new Dictionary<string, object>
                {
                    ["processingTimeMs"] = processingTime.TotalMilliseconds,
                    ["charCount"] = cleanText.Length,
                    ["wordCount"] = cleanText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length
                }
            });

            // If configured as primary OCR, also set the standard ocr.text signal
            if (_config.Images.UseAsPrimaryOcr)
            {
                signals.Add(new Signal
                {
                    Key = "ocr.text",
                    Value = cleanText,
                    Confidence = 0.85,
                    Source = Name,
                    Tags = [SignalTags.Content, "ocr"]
                });

                // Signal to skip native Tesseract OCR
                signals.Add(new Signal
                {
                    Key = "ocr.escalation.skip_tesseract",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = [SignalTags.Metadata]
                });
            }

            signals.Add(new Signal
            {
                Key = "ocr.docling.available",
                Value = true,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Metadata]
            });

            signals.Add(new Signal
            {
                Key = "ocr.docling.confidence",
                Value = 0.85,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Metadata]
            });
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "Docling service unavailable for OCR");
            _isAvailable = false;
            _lastAvailabilityCheck = DateTime.UtcNow;

            signals.Add(new Signal
            {
                Key = "ocr.docling.available",
                Value = false,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Metadata]
            });

            signals.Add(new Signal
            {
                Key = "ocr.docling.error",
                Value = ex.Message,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Metadata]
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "DoclingOcrWave failed for {ImagePath}", imagePath);

            signals.Add(new Signal
            {
                Key = "ocr.docling.error",
                Value = ex.Message,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Metadata]
            });
        }

        return signals;
    }

    /// <summary>
    /// Check if the Docling service is available (with caching).
    /// </summary>
    private bool IsServiceAvailable()
    {
        if (_isAvailable.HasValue && DateTime.UtcNow - _lastAvailabilityCheck < AvailabilityCacheDuration)
            return _isAvailable.Value;

        // Fire-and-forget availability check
        _ = CheckAvailabilityAsync();

        // Return cached value or optimistic true
        return _isAvailable ?? true;
    }

    private async Task CheckAvailabilityAsync()
    {
        try
        {
            _isAvailable = await Client.IsAvailableAsync();
            _lastAvailabilityCheck = DateTime.UtcNow;

            if (!_isAvailable.Value)
                _logger?.LogDebug("Docling service not available at {BaseUrl}", _config.BaseUrl);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to check Docling availability");
            _isAvailable = false;
            _lastAvailabilityCheck = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Clean markdown formatting to get plain text.
    /// </summary>
    private static string CleanMarkdownToText(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        var lines = markdown.Split('\n');
        var cleanLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Skip empty lines and HTML comments
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("<!--"))
                continue;

            // Remove markdown headers
            if (trimmed.StartsWith('#'))
            {
                var headerText = trimmed.TrimStart('#').Trim();
                if (!string.IsNullOrEmpty(headerText))
                    cleanLines.Add(headerText);
                continue;
            }

            // Remove markdown bold/italic markers
            var cleaned = trimmed
                .Replace("**", "")
                .Replace("__", "")
                .Replace("*", "")
                .Replace("_", "");

            if (!string.IsNullOrWhiteSpace(cleaned))
                cleanLines.Add(cleaned);
        }

        return string.Join(" ", cleanLines);
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientLazy.IsValueCreated)
        {
            Client.Dispose();
        }
        await ValueTask.CompletedTask;
    }
}
