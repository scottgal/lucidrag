namespace Mostlylucid.GraphRag.Extraction;

/// <summary>
/// Interface for entity extraction strategies.
/// Allows swapping between heuristic, LLM, and hybrid approaches.
/// </summary>
public interface IEntityExtractor
{
    /// <summary>
    /// Extract entities and relationships from the indexed corpus.
    /// </summary>
    /// <param name="progress">Optional progress reporter</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Extraction statistics including entity/relationship counts and LLM call count</returns>
    Task<ExtractionResult> ExtractAsync(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);
}
