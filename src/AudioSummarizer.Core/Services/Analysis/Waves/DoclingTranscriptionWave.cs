using AudioSummarizer.Core.Models;
using AudioSummarizer.Core.Services.Analysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services;

namespace AudioSummarizer.Core.Services.Analysis.Waves;

/// <summary>
/// Audio transcription wave using Docling service.
/// Docling provides automatic speech recognition (ASR) capabilities
/// for WAV, MP3, and other audio formats.
///
/// Priority: 62 (runs slightly after standard TranscriptionWave at 60)
/// When configured as primary transcription, can replace or supplement WhisperX.
///
/// Signals produced:
/// - transcription.docling.text: Full transcript
/// - transcription.docling.confidence: Confidence score
/// - transcription.docling.available: Whether service was available
/// </summary>
public sealed class DoclingTranscriptionWave : IAudioWave, IAsyncDisposable
{
    private readonly DoclingConfig _config;
    private readonly ILogger<DoclingTranscriptionWave>? _logger;
    private readonly Lazy<DoclingClient> _clientLazy;
    private bool? _isAvailable;
    private DateTime _lastAvailabilityCheck = DateTime.MinValue;
    private static readonly TimeSpan AvailabilityCacheDuration = TimeSpan.FromMinutes(5);

    public string Name => "DoclingTranscriptionWave";
    public int Priority => 62; // Slightly after standard TranscriptionWave (60)

    public DoclingTranscriptionWave(
        IOptions<DocSummarizerConfig> config,
        ILogger<DoclingTranscriptionWave>? logger = null)
    {
        _config = config.Value.Docling;
        _logger = logger;
        _clientLazy = new Lazy<DoclingClient>(() => new DoclingClient(_config));
    }

    private DoclingClient Client => _clientLazy.Value;

    /// <summary>
    /// Check if this wave should run.
    /// </summary>
    public bool ShouldRun(string audioPath, AnalysisContext context)
    {
        // Check master enable
        if (!_config.Enabled || !_config.Audio.Enabled)
            return false;

        // Check if extension is supported
        var extension = Path.GetExtension(audioPath);
        if (!_config.Audio.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return false;

        // If configured as primary, only run if no transcript exists yet
        if (_config.Audio.UseAsPrimaryTranscription)
        {
            // Check if we already have a transcript from another wave
            if (context.HasSignal("transcription.text"))
            {
                _logger?.LogDebug("Skipping Docling transcription - transcript already exists");
                return false;
            }
        }

        // Check if service is available (cached)
        return IsServiceAvailable();
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string audioPath,
        AnalysisContext context,
        CancellationToken cancellationToken = default)
    {
        var signals = new List<Signal>();
        var startTime = DateTime.UtcNow;

        try
        {
            _logger?.LogDebug("Running Docling ASR on {AudioPath}", audioPath);

            // Convert audio to text using Docling
            var text = await Client.ConvertAsync(audioPath, cancellationToken);
            var processingTime = DateTime.UtcNow - startTime;

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger?.LogDebug("Docling returned empty transcript for {AudioPath}", audioPath);

                signals.Add(new Signal
                {
                    Name = "transcription.docling.available",
                    Value = true,
                    Type = SignalType.Metadata,
                    Source = Name
                });

                signals.Add(new Signal
                {
                    Name = "transcription.docling.text",
                    Value = "",
                    Confidence = 0.0,
                    Type = SignalType.Transcription,
                    Source = Name
                });

                return signals;
            }

            // Clean up the text
            var cleanText = CleanTranscriptText(text);

            _logger?.LogInformation(
                "Docling ASR transcribed {CharCount} chars from {AudioPath} in {TimeMs}ms",
                cleanText.Length,
                Path.GetFileName(audioPath),
                processingTime.TotalMilliseconds);

            // Add Docling-specific transcript signal
            signals.Add(new Signal
            {
                Name = "transcription.docling.text",
                Value = cleanText,
                Confidence = 0.80, // Docling ASR is generally reliable
                Type = SignalType.Transcription,
                Source = Name
            });

            signals.Add(new Signal
            {
                Name = "transcription.docling.processing_time_ms",
                Value = processingTime.TotalMilliseconds,
                Type = SignalType.Metadata,
                Source = Name
            });

            // If configured as primary transcription, also set the standard signal
            if (_config.Audio.UseAsPrimaryTranscription)
            {
                signals.Add(new Signal
                {
                    Name = "transcription.text",
                    Value = cleanText,
                    Confidence = 0.80,
                    Type = SignalType.Transcription,
                    Source = Name
                });

                signals.Add(new Signal
                {
                    Name = "transcription.provider",
                    Value = "docling",
                    Type = SignalType.Metadata,
                    Source = Name
                });
            }

            signals.Add(new Signal
            {
                Name = "transcription.docling.available",
                Value = true,
                Type = SignalType.Metadata,
                Source = Name
            });

            signals.Add(new Signal
            {
                Name = "transcription.docling.confidence",
                Value = 0.80,
                Confidence = 0.80,
                Type = SignalType.Metadata,
                Source = Name
            });
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "Docling service unavailable for transcription");
            _isAvailable = false;
            _lastAvailabilityCheck = DateTime.UtcNow;

            signals.Add(new Signal
            {
                Name = "transcription.docling.available",
                Value = false,
                Type = SignalType.Metadata,
                Source = Name
            });

            signals.Add(new Signal
            {
                Name = "transcription.docling.error",
                Value = ex.Message,
                Type = SignalType.Metadata,
                Source = Name
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "DoclingTranscriptionWave failed for {AudioPath}", audioPath);

            signals.Add(new Signal
            {
                Name = "transcription.docling.error",
                Value = ex.Message,
                Type = SignalType.Metadata,
                Source = Name
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
    /// Clean up transcript text (remove markdown formatting if present).
    /// </summary>
    private static string CleanTranscriptText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // Docling may return markdown-formatted text
        var lines = text.Split('\n');
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

            cleanLines.Add(trimmed);
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
