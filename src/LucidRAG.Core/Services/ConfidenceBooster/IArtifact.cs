namespace LucidRAG.Core.Services.ConfidenceBooster;

/// <summary>
/// Base interface for bounded artifacts extracted from low-confidence signals.
/// Each artifact represents a specific region/segment/window that needs LLM clarification.
/// </summary>
public interface IArtifact
{
    /// <summary>
    /// Unique identifier for this artifact (e.g., "img_123_crop_5", "audio_456_segment_2").
    /// </summary>
    string ArtifactId { get; }

    /// <summary>
    /// The document ID this artifact belongs to.
    /// </summary>
    Guid DocumentId { get; }

    /// <summary>
    /// The signal name that triggered extraction (e.g., "object.classification.person", "transcription.segment_42").
    /// </summary>
    string SignalName { get; }

    /// <summary>
    /// Original confidence score that triggered extraction (should be &lt; threshold, e.g., 0.75).
    /// </summary>
    double OriginalConfidence { get; }

    /// <summary>
    /// Domain-specific artifact type (image, audio, text, data).
    /// </summary>
    string ArtifactType { get; }

    /// <summary>
    /// Metadata about the artifact (bounding box, time range, line numbers, etc.).
    /// </summary>
    Dictionary<string, object> Metadata { get; }
}
