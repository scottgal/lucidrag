namespace LucidRAG.Core.Services.ConfidenceBooster;

/// <summary>
/// Result of LLM-based confidence boosting for a specific artifact.
/// </summary>
public class BoostResult
{
    /// <summary>
    /// The artifact that was boosted.
    /// </summary>
    public required IArtifact Artifact { get; init; }

    /// <summary>
    /// Whether the boost was successful.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// The boosted/refined value from the LLM (e.g., "person with backpack", "quantum computing", "2024-01-15").
    /// </summary>
    public string? BoostedValue { get; init; }

    /// <summary>
    /// New confidence score after LLM boost (e.g., 0.92).
    /// </summary>
    public double? BoostedConfidence { get; init; }

    /// <summary>
    /// LLM reasoning/explanation for the boost (for auditability).
    /// </summary>
    public string? Reasoning { get; init; }

    /// <summary>
    /// Error message if boost failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Tokens consumed by this boost request.
    /// </summary>
    public int TokensUsed { get; init; }

    /// <summary>
    /// Time taken for LLM inference (ms).
    /// </summary>
    public long InferenceTimeMs { get; init; }

    /// <summary>
    /// Additional metadata from the LLM response.
    /// </summary>
    public Dictionary<string, object>? AdditionalMetadata { get; init; }
}
