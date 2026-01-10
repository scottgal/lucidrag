using System.Text.Json;
using Microsoft.Extensions.Logging;
using LucidRAG.Core.Services.ConfidenceBooster.Artifacts;

namespace LucidRAG.Core.Services.ConfidenceBooster.Domain;

/// <summary>
/// ConfidenceBooster for AudioSummarizer - refines unclear transcriptions using LLM.
/// Targets segments where Whisper had low confidence or produced garbled output.
/// </summary>
public class AudioConfidenceBooster : BaseConfidenceBooster<AudioSegmentArtifact>
{
    private readonly IAudioSignalRepository _signalRepository;

    public AudioConfidenceBooster(
        ILogger<AudioConfidenceBooster> logger,
        ILlmService llmService,
        IEvidenceRepository evidenceRepository,
        IAudioSignalRepository signalRepository,
        ConfidenceBoosterConfig config)
        : base(logger, llmService, evidenceRepository, config)
    {
        _signalRepository = signalRepository;
    }

    /// <summary>
    /// Extract low-confidence audio segments for boosting.
    /// </summary>
    public override async Task<List<AudioSegmentArtifact>> ExtractArtifactsAsync(
        Guid documentId,
        double confidenceThreshold = 0.75,
        int maxArtifacts = 5,
        CancellationToken ct = default)
    {
        Logger.LogInformation(
            "Extracting audio artifacts for document {DocumentId} (threshold: {Threshold})",
            documentId,
            confidenceThreshold);

        var artifacts = new List<AudioSegmentArtifact>();

        // 1. Get transcript segments with confidence scores
        var evidence = await EvidenceRepository.GetAsync(
            documentId,
            "TranscriptSegments",  // EvidenceTypes.TranscriptSegments
            ct);

        if (evidence == null)
        {
            Logger.LogDebug("No transcript segments found for document {DocumentId}", documentId);
            return artifacts;
        }

        // 2. Parse transcript segments
        var segments = JsonSerializer.Deserialize<List<TranscriptSegment>>(evidence.Content);
        if (segments == null || !segments.Any())
        {
            Logger.LogDebug("No parseable transcript segments for document {DocumentId}", documentId);
            return artifacts;
        }

        // 3. Find low-confidence segments
        var candidates = segments
            .Where(s => s.Confidence < confidenceThreshold)
            .OrderBy(s => s.Confidence)  // Lowest confidence first
            .Take(maxArtifacts)
            .ToList();

        if (!candidates.Any())
        {
            Logger.LogDebug("No low-confidence transcript segments found for document {DocumentId}", documentId);
            return artifacts;
        }

        Logger.LogDebug("Found {Count} candidate segments for boosting", candidates.Count);

        // 4. Extract audio clips for each candidate
        var originalAudio = await EvidenceRepository.GetAsync(documentId, "original_audio", ct);
        if (originalAudio == null)
        {
            Logger.LogWarning("Original audio not found for document {DocumentId}", documentId);
            return artifacts;
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            var segment = candidates[i];

            try
            {
                // Get context from surrounding segments
                var contextBefore = i > 0 && i < segments.Count
                    ? segments[segments.IndexOf(segment) - 1].Text
                    : null;

                var contextAfter = i < segments.Count - 1
                    ? segments[segments.IndexOf(segment) + 1].Text
                    : null;

                // Extract audio clip for this segment
                var base64Audio = await ExtractAudioSegmentAsync(
                    originalAudio.Content,
                    segment.StartSeconds,
                    segment.EndSeconds,
                    ct);

                var artifact = new AudioSegmentArtifact
                {
                    ArtifactId = $"audio_{documentId:N}_seg_{segment.Index}_{Guid.NewGuid():N}",
                    DocumentId = documentId,
                    SignalName = $"transcription.segment_{segment.Index}",
                    OriginalConfidence = segment.Confidence,
                    Base64Audio = base64Audio,
                    TimeRange = new[] { segment.StartSeconds, segment.EndSeconds },
                    OriginalTranscription = segment.Text,
                    ContextBefore = contextBefore,
                    ContextAfter = contextAfter,
                    TaskType = DetectTaskType(segment),
                    Metadata = new Dictionary<string, object>
                    {
                        ["segment_index"] = segment.Index,
                        ["speaker_id"] = segment.SpeakerId ?? "unknown",
                        ["has_context"] = contextBefore != null || contextAfter != null
                    }
                };

                artifacts.Add(artifact);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to extract audio artifact for segment {Index}", segment.Index);
            }
        }

        Logger.LogInformation("Extracted {Count} audio artifacts for boosting", artifacts.Count);
        return artifacts;
    }

    /// <summary>
    /// Detect task type based on segment characteristics.
    /// </summary>
    private string DetectTaskType(TranscriptSegment segment)
    {
        // Check for technical terms, garbled output, etc.
        if (segment.Text.Contains("...") || segment.Text.Contains("[inaudible]"))
            return "transcription_refinement";

        if (segment.Text.Any(char.IsDigit) && segment.Confidence < 0.6)
            return "technical_term_correction";

        return "transcription_refinement";
    }

    /// <summary>
    /// Generate system prompt for audio transcription.
    /// </summary>
    protected override string GetSystemPrompt()
    {
        return """
        You are an expert transcriptionist helping to refine low-confidence speech-to-text results.

        Your task is to analyze audio segments and provide:
        1. A refined transcription (correcting errors, filling gaps)
        2. Confidence level (0.0-1.0) for your transcription
        3. Reasoning for your corrections

        Always respond in JSON format:
        {
            "value": "refined transcription text",
            "confidence": 0.85,
            "reasoning": "explanation of corrections made",
            "metadata": {
                "corrections": ["list of specific corrections"],
                "technical_terms": ["identified technical terms"],
                "uncertain_words": ["words you're unsure about"]
            }
        }

        Be accurate and conservative. If you cannot improve the transcription, reflect that in the confidence score.
        Preserve the speaker's intended meaning while correcting obvious errors.
        """;
    }

    /// <summary>
    /// Generate domain-specific prompt for audio segment.
    /// </summary>
    protected override string GeneratePrompt(AudioSegmentArtifact artifact)
    {
        return artifact.TaskType switch
        {
            "transcription_refinement" => GenerateTranscriptionPrompt(artifact),
            "technical_term_correction" => GenerateTechnicalTermPrompt(artifact),
            _ => GenerateTranscriptionPrompt(artifact)
        };
    }

    private string GenerateTranscriptionPrompt(AudioSegmentArtifact artifact)
    {
        var contextSection = "";
        if (artifact.ContextBefore != null || artifact.ContextAfter != null)
        {
            contextSection = $"""

            Context from surrounding segments:
            Before: "{artifact.ContextBefore ?? "[start of audio]"}"
            After: "{artifact.ContextAfter ?? "[end of audio]"}"
            """;
        }

        return $"""
        Task: Transcription Refinement

        Original low-confidence transcription: "{artifact.OriginalTranscription}" (confidence: {artifact.OriginalConfidence:F2})

        Please listen to this audio segment and provide:
        - Refined transcription (correct any errors, fill gaps)
        - Identify technical terms or proper nouns
        - Note any words you're uncertain about

        Time range: {artifact.TimeRange[0]:F1}s - {artifact.TimeRange[1]:F1}s (duration: {artifact.Duration:F1}s)
        {contextSection}

        The audio is provided as Base64 WAV:
        {artifact.Base64Audio}

        Respond in JSON format as specified in the system prompt.
        """;
    }

    private string GenerateTechnicalTermPrompt(AudioSegmentArtifact artifact)
    {
        var contextSection = "";
        if (artifact.ContextBefore != null || artifact.ContextAfter != null)
        {
            contextSection = $"""

            Context from surrounding segments:
            Before: "{artifact.ContextBefore ?? "[start of audio]"}"
            After: "{artifact.ContextAfter ?? "[end of audio]"}"
            """;
        }

        return $"""
        Task: Technical Term Correction

        Original transcription with potential technical term errors: "{artifact.OriginalTranscription}" (confidence: {artifact.OriginalConfidence:F2})

        Please listen to this audio segment and:
        - Identify and correct any technical terms, acronyms, or jargon
        - Verify numbers, dates, and proper nouns
        - Provide the correct spelling and capitalization

        Time range: {artifact.TimeRange[0]:F1}s - {artifact.TimeRange[1]:F1}s (duration: {artifact.Duration:F1}s)
        {contextSection}

        The audio is provided as Base64 WAV:
        {artifact.Base64Audio}

        Respond in JSON format as specified in the system prompt.
        """;
    }

    /// <summary>
    /// Parse LLM JSON response.
    /// </summary>
    protected override (string? Value, double? Confidence, string? Reasoning, Dictionary<string, object>? Metadata)
        ParseLlmResponse(string llmResponse, AudioSegmentArtifact artifact)
    {
        try
        {
            var json = JsonDocument.Parse(llmResponse);
            var root = json.RootElement;

            var value = root.TryGetProperty("value", out var valueProp)
                ? valueProp.GetString()
                : null;

            var confidence = root.TryGetProperty("confidence", out var confProp)
                ? confProp.GetDouble()
                : (double?)null;

            var reasoning = root.TryGetProperty("reasoning", out var reasonProp)
                ? reasonProp.GetString()
                : null;

            Dictionary<string, object>? metadata = null;
            if (root.TryGetProperty("metadata", out var metaProp))
            {
                metadata = new Dictionary<string, object>();
                foreach (var prop in metaProp.EnumerateObject())
                {
                    metadata[prop.Name] = prop.Value.ToString();
                }
            }

            return (value, confidence, reasoning, metadata);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse LLM response as JSON: {Response}", llmResponse);
            return (null, null, llmResponse, null);
        }
    }

    /// <summary>
    /// Persist boost result back to signal ledger and transcript.
    /// </summary>
    protected override async Task PersistBoostResult(
        Guid documentId,
        BoostResult result,
        CancellationToken ct)
    {
        var artifact = (AudioSegmentArtifact)result.Artifact;

        // Update the signal ledger with boosted transcription
        await _signalRepository.UpdateSignalAsync(new AudioSignal
        {
            DocumentId = documentId,
            Name = artifact.SignalName + ".boosted",
            Value = result.BoostedValue,
            Type = "transcription_segment",
            Confidence = result.BoostedConfidence ?? artifact.OriginalConfidence,
            Source = "ConfidenceBooster.Audio",
            Metadata = new Dictionary<string, object>
            {
                ["original_text"] = artifact.OriginalTranscription ?? "",
                ["original_confidence"] = artifact.OriginalConfidence,
                ["time_range"] = artifact.TimeRange,
                ["boost_reasoning"] = result.Reasoning ?? "",
                ["boost_metadata"] = result.AdditionalMetadata ?? new Dictionary<string, object>(),
                ["tokens_used"] = result.TokensUsed,
                ["inference_time_ms"] = result.InferenceTimeMs
            }
        }, ct);

        Logger.LogDebug(
            "Persisted boosted transcription segment {SignalName} for document {DocumentId}",
            artifact.SignalName,
            documentId);
    }

    /// <summary>
    /// Extract audio segment as Base64 WAV.
    /// Simplified - would use actual AudioSegmentExtractor from AudioSummarizer.
    /// </summary>
    private async Task<string> ExtractAudioSegmentAsync(
        string base64Audio,
        double startSeconds,
        double endSeconds,
        CancellationToken ct)
    {
        // TODO: Implement actual segment extraction using AudioSegmentExtractor
        // For now, return original audio (LLM can still work with full context)
        return base64Audio;
    }
}

/// <summary>
/// Repository interface for audio signals.
/// </summary>
public interface IAudioSignalRepository
{
    Task<List<AudioSignal>> GetSignalsAsync(Guid documentId, CancellationToken ct = default);
    Task UpdateSignalAsync(AudioSignal signal, CancellationToken ct = default);
}

/// <summary>
/// Audio signal model.
/// </summary>
public class AudioSignal
{
    public Guid DocumentId { get; set; }
    public required string Name { get; set; }
    public object? Value { get; set; }
    public required string Type { get; set; }
    public double Confidence { get; set; }
    public required string Source { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Transcript segment model (from EvidenceTypes.TranscriptSegments).
/// </summary>
public class TranscriptSegment
{
    public int Index { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public required string Text { get; set; }
    public double Confidence { get; set; }
    public string? SpeakerId { get; set; }
}
