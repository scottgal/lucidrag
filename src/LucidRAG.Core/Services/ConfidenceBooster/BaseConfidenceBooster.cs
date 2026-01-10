using Microsoft.Extensions.Logging;

namespace LucidRAG.Core.Services.ConfidenceBooster;

/// <summary>
/// Base implementation of confidence boosting with common LLM orchestration logic.
/// Domain-specific implementations override artifact extraction and prompt generation.
/// </summary>
public abstract class BaseConfidenceBooster<TArtifact> : IConfidenceBooster<TArtifact>
    where TArtifact : IArtifact
{
    protected readonly ILogger Logger;
    protected readonly ILlmService LlmService;
    protected readonly IEvidenceRepository EvidenceRepository;
    protected readonly ConfidenceBoosterConfig Config;

    protected BaseConfidenceBooster(
        ILogger logger,
        ILlmService llmService,
        IEvidenceRepository evidenceRepository,
        ConfidenceBoosterConfig config)
    {
        Logger = logger;
        LlmService = llmService;
        EvidenceRepository = evidenceRepository;
        Config = config;
    }

    /// <summary>
    /// Scan for low-confidence signals and extract artifacts.
    /// Implemented by domain-specific boosters.
    /// </summary>
    public abstract Task<List<TArtifact>> ExtractArtifactsAsync(
        Guid documentId,
        double confidenceThreshold = 0.75,
        int maxArtifacts = 5,
        CancellationToken ct = default);

    /// <summary>
    /// Boost a batch of artifacts using LLM inference.
    /// Common implementation - uses domain-specific prompt generation.
    /// </summary>
    public async Task<List<BoostResult>> BoostBatchAsync(
        IEnumerable<TArtifact> artifacts,
        CancellationToken ct = default)
    {
        var results = new List<BoostResult>();
        var artifactList = artifacts.ToList();

        if (!artifactList.Any())
        {
            Logger.LogDebug("No artifacts to boost");
            return results;
        }

        Logger.LogInformation("Boosting {Count} artifacts with LLM", artifactList.Count);

        foreach (var artifact in artifactList)
        {
            try
            {
                var result = await BoostSingleAsync(artifact, ct);
                results.Add(result);

                // Rate limiting / cost control
                if (Config.DelayBetweenRequestsMs > 0)
                {
                    await Task.Delay(Config.DelayBetweenRequestsMs, ct);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to boost artifact {ArtifactId}", artifact.ArtifactId);
                results.Add(new BoostResult
                {
                    Artifact = artifact,
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        Logger.LogInformation(
            "Boost batch complete: {SuccessCount}/{TotalCount} successful, {TokensUsed} tokens",
            results.Count(r => r.Success),
            results.Count,
            results.Sum(r => r.TokensUsed));

        return results;
    }

    /// <summary>
    /// Boost a single artifact with LLM.
    /// </summary>
    protected async Task<BoostResult> BoostSingleAsync(
        TArtifact artifact,
        CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;

        try
        {
            // Generate domain-specific prompt
            var prompt = GeneratePrompt(artifact);

            Logger.LogDebug(
                "Boosting {ArtifactType} artifact {ArtifactId} (signal: {SignalName}, confidence: {Confidence:F2})",
                artifact.ArtifactType,
                artifact.ArtifactId,
                artifact.SignalName,
                artifact.OriginalConfidence);

            // Call LLM with structured output
            var llmResponse = await LlmService.InvokeAsync(new LlmRequest
            {
                SystemPrompt = GetSystemPrompt(),
                UserPrompt = prompt,
                Temperature = Config.Temperature,
                MaxTokens = Config.MaxTokensPerRequest,
                ResponseFormat = "json"  // Request structured JSON response
            }, ct);

            var inferenceTime = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;

            // Parse LLM response
            var parsed = ParseLlmResponse(llmResponse.Content, artifact);

            return new BoostResult
            {
                Artifact = artifact,
                Success = true,
                BoostedValue = parsed.Value,
                BoostedConfidence = parsed.Confidence,
                Reasoning = parsed.Reasoning,
                TokensUsed = llmResponse.TokensUsed,
                InferenceTimeMs = inferenceTime,
                AdditionalMetadata = parsed.Metadata
            };
        }
        catch (Exception ex)
        {
            var inferenceTime = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
            Logger.LogError(ex, "LLM boost failed for artifact {ArtifactId}", artifact.ArtifactId);

            return new BoostResult
            {
                Artifact = artifact,
                Success = false,
                ErrorMessage = ex.Message,
                InferenceTimeMs = inferenceTime
            };
        }
    }

    /// <summary>
    /// Update signal ledger with boosted values.
    /// </summary>
    public async Task UpdateSignalLedgerAsync(
        Guid documentId,
        IEnumerable<BoostResult> results,
        CancellationToken ct = default)
    {
        var successfulBoosts = results.Where(r => r.Success).ToList();

        if (!successfulBoosts.Any())
        {
            Logger.LogDebug("No successful boosts to persist for document {DocumentId}", documentId);
            return;
        }

        Logger.LogInformation(
            "Updating signal ledger for document {DocumentId} with {Count} boosted signals",
            documentId,
            successfulBoosts.Count);

        foreach (var result in successfulBoosts)
        {
            try
            {
                await PersistBoostResult(documentId, result, ct);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex,
                    "Failed to persist boost result for artifact {ArtifactId}",
                    result.Artifact.ArtifactId);
            }
        }

        Logger.LogInformation("Signal ledger updated successfully");
    }

    /// <summary>
    /// Generate system prompt for LLM (domain-specific).
    /// </summary>
    protected abstract string GetSystemPrompt();

    /// <summary>
    /// Generate domain-specific prompt for artifact.
    /// </summary>
    protected abstract string GeneratePrompt(TArtifact artifact);

    /// <summary>
    /// Parse LLM response into structured boost data.
    /// </summary>
    protected abstract (string? Value, double? Confidence, string? Reasoning, Dictionary<string, object>? Metadata)
        ParseLlmResponse(string llmResponse, TArtifact artifact);

    /// <summary>
    /// Persist boost result to signal ledger.
    /// </summary>
    protected abstract Task PersistBoostResult(
        Guid documentId,
        BoostResult result,
        CancellationToken ct);
}

/// <summary>
/// Configuration for ConfidenceBooster service.
/// </summary>
public class ConfidenceBoosterConfig
{
    /// <summary>
    /// Confidence threshold for extracting artifacts (default: 0.75).
    /// </summary>
    public double ConfidenceThreshold { get; set; } = 0.75;

    /// <summary>
    /// Maximum artifacts to boost per document (cost control, default: 5).
    /// </summary>
    public int MaxArtifactsPerDocument { get; set; } = 5;

    /// <summary>
    /// LLM temperature for boosting (default: 0.1 for consistency).
    /// </summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>
    /// Maximum tokens per LLM request (default: 500).
    /// </summary>
    public int MaxTokensPerRequest { get; set; } = 500;

    /// <summary>
    /// Delay between LLM requests in milliseconds (rate limiting, default: 0).
    /// </summary>
    public int DelayBetweenRequestsMs { get; set; } = 0;

    /// <summary>
    /// Enable confidence boosting (default: false - opt-in).
    /// </summary>
    public bool Enabled { get; set; } = false;
}

/// <summary>
/// LLM service interface for confidence boosting.
/// </summary>
public interface ILlmService
{
    Task<LlmResponse> InvokeAsync(LlmRequest request, CancellationToken ct = default);
}

/// <summary>
/// LLM request model.
/// </summary>
public class LlmRequest
{
    public required string SystemPrompt { get; init; }
    public required string UserPrompt { get; init; }
    public double Temperature { get; init; } = 0.1;
    public int MaxTokens { get; init; } = 500;
    public string? ResponseFormat { get; init; }
}

/// <summary>
/// LLM response model.
/// </summary>
public class LlmResponse
{
    public required string Content { get; init; }
    public int TokensUsed { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Evidence repository interface for storing artifacts and results.
/// </summary>
public interface IEvidenceRepository
{
    Task<EvidenceArtifact?> GetAsync(Guid documentId, string evidenceType, CancellationToken ct = default);
    Task SaveAsync(EvidenceArtifact evidence, CancellationToken ct = default);
}

/// <summary>
/// Evidence artifact model (reuse existing if available).
/// </summary>
public class EvidenceArtifact
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public required string Type { get; set; }
    public required string Content { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}
