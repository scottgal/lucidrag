using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;

namespace Mostlylucid.DocSummarizer.Images.Services.Storage;

/// <summary>
/// Interface for storing and retrieving image analysis signals.
/// Enables testing and swapping between different storage implementations.
/// </summary>
public interface ISignalDatabase
{
    /// <summary>
    /// Store a complete dynamic image profile with all signals.
    /// </summary>
    Task<long> StoreProfileAsync(
        DynamicImageProfile profile,
        string sha256,
        string? filePath = null,
        int width = 0,
        int height = 0,
        string? format = null,
        CancellationToken ct = default);

    /// <summary>
    /// Load all signals for an image by SHA256.
    /// </summary>
    Task<DynamicImageProfile?> LoadProfileAsync(string sha256, CancellationToken ct = default);

    /// <summary>
    /// Store user feedback for learning/improvement.
    /// </summary>
    Task StoreFeedbackAsync(
        string sha256,
        string feedbackType,
        string? originalValue,
        string? correctedValue,
        double? confidenceAdjustment = null,
        string? notes = null,
        long? signalId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get statistics about stored data.
    /// </summary>
    Task<DatabaseStatistics> GetStatisticsAsync(CancellationToken ct = default);

    // Discriminator scoring and effectiveness tracking

    /// <summary>
    /// Store a discriminator score to the immutable ledger
    /// </summary>
    Task StoreDiscriminatorScoreAsync(DiscriminatorScore score, CancellationToken ct = default);

    /// <summary>
    /// Get discriminator scores for an image
    /// </summary>
    Task<List<DiscriminatorScore>> GetDiscriminatorScoresAsync(
        string imageHash,
        int limit = 10,
        CancellationToken ct = default);

    /// <summary>
    /// Update discriminator effectiveness (with decay)
    /// </summary>
    Task UpdateDiscriminatorEffectivenessAsync(
        DiscriminatorEffectiveness effectiveness,
        CancellationToken ct = default);

    /// <summary>
    /// Get discriminator effectiveness for a specific signal/type/goal
    /// </summary>
    Task<DiscriminatorEffectiveness?> GetDiscriminatorEffectivenessAsync(
        string signalName,
        ImageType imageType,
        string goal,
        CancellationToken ct = default);

    /// <summary>
    /// Get all discriminator effectiveness records for a type/goal
    /// </summary>
    Task<List<DiscriminatorEffectiveness>> GetAllDiscriminatorEffectivenessAsync(
        ImageType imageType,
        string goal,
        CancellationToken ct = default);

    /// <summary>
    /// Retire a discriminator (mark as inactive due to low effectiveness)
    /// </summary>
    Task RetireDiscriminatorAsync(
        string signalName,
        ImageType imageType,
        string goal,
        CancellationToken ct = default);

    /// <summary>
    /// Get total count of discriminator scores
    /// </summary>
    Task<int> GetTotalScoreCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Get count of scores with feedback
    /// </summary>
    Task<int> GetTotalFeedbackCountAsync(CancellationToken ct = default);
}
