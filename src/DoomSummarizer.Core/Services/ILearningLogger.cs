using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
///     Logs sentinel LLM corrections when the embedding classifier disagrees.
///     Implementations capture disagreement data for future exemplar learning.
/// </summary>
public interface ILearningLogger
{
    /// <summary>
    ///     Log a disagreement between the embedding classifier and sentinel LLM.
    ///     Called after a successful sentinel parse when any classification dimension differs.
    /// </summary>
    /// <param name="query">The original user query text.</param>
    /// <param name="embedding">The query embedding (384d float array), or null if unavailable.</param>
    /// <param name="embeddingResult">The embedding classifier's deterministic result.</param>
    /// <param name="sentinelResult">The sentinel LLM's structured intent result.</param>
    Task LogDisagreementAsync(string query, float[]? embedding,
        QueryClassification embeddingResult, SentinelIntent sentinelResult);
}
