namespace LucidRAG.Core.Services.ConfidenceBooster;

/// <summary>
/// Generic interface for confidence boosting across different artifact types.
/// Implementations provide domain-specific artifact extraction and prompt generation.
/// </summary>
/// <typeparam name="TArtifact">The specific artifact type (ImageCropArtifact, AudioSegmentArtifact, etc.)</typeparam>
public interface IConfidenceBooster<TArtifact> where TArtifact : IArtifact
{
    /// <summary>
    /// Scan a processed document for low-confidence signals that need boosting.
    /// </summary>
    /// <param name="documentId">The document to scan.</param>
    /// <param name="confidenceThreshold">Signals below this threshold are candidates for boosting (default: 0.75).</param>
    /// <param name="maxArtifacts">Maximum artifacts to extract (cost control, default: 5).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of extracted artifacts ready for LLM boosting.</returns>
    Task<List<TArtifact>> ExtractArtifactsAsync(
        Guid documentId,
        double confidenceThreshold = 0.75,
        int maxArtifacts = 5,
        CancellationToken ct = default);

    /// <summary>
    /// Boost confidence for a batch of artifacts using LLM inference.
    /// </summary>
    /// <param name="artifacts">Artifacts to boost.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Boost results for each artifact.</returns>
    Task<List<BoostResult>> BoostBatchAsync(
        IEnumerable<TArtifact> artifacts,
        CancellationToken ct = default);

    /// <summary>
    /// Update the signal ledger with boosted values.
    /// </summary>
    /// <param name="documentId">The document to update.</param>
    /// <param name="results">Boost results to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateSignalLedgerAsync(
        Guid documentId,
        IEnumerable<BoostResult> results,
        CancellationToken ct = default);
}
