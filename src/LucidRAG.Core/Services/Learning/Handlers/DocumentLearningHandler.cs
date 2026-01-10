using Microsoft.Extensions.Logging;

namespace LucidRAG.Core.Services.Learning.Handlers;

/// <summary>
/// Learning handler for Document processing (DocSummarizer).
/// Reruns the full document processing stack (no early exit) to find better results.
/// </summary>
public class DocumentLearningHandler : ILearningHandler
{
    private readonly ILogger<DocumentLearningHandler> _logger;
    private readonly IDocumentProcessingService _processingService;
    private readonly IEntityRepository _entityRepository;
    private readonly IEvidenceRepository _evidenceRepository;

    public DocumentLearningHandler(
        ILogger<DocumentLearningHandler> logger,
        IDocumentProcessingService processingService,
        IEntityRepository entityRepository,
        IEvidenceRepository evidenceRepository)
    {
        _logger = logger;
        _processingService = processingService;
        _entityRepository = entityRepository;
        _evidenceRepository = evidenceRepository;
    }

    public bool CanHandle(string documentType)
    {
        return documentType.ToLowerInvariant() is "document" or "pdf" or "docx" or "html" or "markdown";
    }

    public async Task<string> GetDocumentHashAsync(LearningTask task, CancellationToken ct = default)
    {
        // Get document and return its content hash
        var doc = await _processingService.GetDocumentAsync(task.DocumentId, ct);
        if (doc == null)
            return string.Empty;

        // Return the content hash or compute from file metadata
        // This should be the same hash stored in DocumentEntity.ContentHash
        return doc.ContentHash ?? ComputeDocumentSignature(doc);
    }

    private string ComputeDocumentSignature(DocumentEntity doc)
    {
        // Fallback: compute signature from file path + modified date
        var signature = $"{doc.FilePath}:{doc.ProcessedAt?.Ticks ?? 0}";
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(signature));
        return Convert.ToHexString(hashBytes);
    }

    public async Task<LearningResult> LearnAsync(LearningTask task, CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;
        var improvements = new List<string>();
        var metrics = new Dictionary<string, ComparisonMetric>();

        try
        {
            _logger.LogInformation("Starting learning for document {DocumentId}", task.DocumentId);

            // PHASE 1: Get current state (baseline)
            var currentEntities = await _entityRepository.GetEntitiesAsync(task.DocumentId, ct);
            var currentEntityCount = currentEntities.Count;

            var currentEvidence = await _evidenceRepository.GetAsync(task.DocumentId, "entity_extraction", ct);
            var currentConfidence = currentEvidence?.Metadata?
                .TryGetValue("average_confidence", out var conf) == true && conf is double c
                ? c
                : 0.0;

            _logger.LogInformation(
                "Baseline for document {DocumentId}: {EntityCount} entities, {Confidence:F2} avg confidence",
                task.DocumentId, currentEntityCount, currentConfidence);

            // PHASE 2: Reprocess with FULL stack (no early exit)
            var originalDoc = await _processingService.GetDocumentAsync(task.DocumentId, ct);
            if (originalDoc == null)
            {
                return new LearningResult
                {
                    DocumentId = task.DocumentId,
                    Success = false,
                    ProcessingTime = DateTime.UtcNow - startTime,
                    ImprovementsApplied = false,
                    ErrorMessage = "Document not found"
                };
            }

            // Reprocess with ALL extractors, no shortcuts
            var reprocessResult = await _processingService.ReprocessFullStackAsync(
                originalDoc.FilePath,
                new ProcessingOptions
                {
                    ExtractEntities = true,
                    GenerateEmbeddings = true,
                    ExtractRelationships = true,
                    RunAllExtractors = true,  // CRITICAL: No early exit
                    LearningMode = true  // Signal to run slower but better algorithms
                },
                ct);

            var newEntityCount = reprocessResult.Entities.Count;
            var newConfidence = reprocessResult.AverageConfidence;

            _logger.LogInformation(
                "Reprocessing complete: {NewEntityCount} entities, {NewConfidence:F2} avg confidence",
                newEntityCount, newConfidence);

            // PHASE 3: Compare results and decide whether to update
            var shouldUpdate = false;

            // Check for more entities
            if (newEntityCount > currentEntityCount)
            {
                shouldUpdate = true;
                var delta = newEntityCount - currentEntityCount;
                improvements.Add($"+{delta} entities ({currentEntityCount} → {newEntityCount})");

                metrics["entity_count"] = new ComparisonMetric
                {
                    Name = "Entity Count",
                    Before = currentEntityCount,
                    After = newEntityCount,
                    Improved = true,
                    Reason = $"Found {delta} additional entities"
                };
            }

            // Check for higher confidence
            if (newConfidence > currentConfidence + 0.05) // 5% threshold
            {
                shouldUpdate = true;
                improvements.Add($"confidence improved ({currentConfidence:F2} → {newConfidence:F2})");

                metrics["average_confidence"] = new ComparisonMetric
                {
                    Name = "Average Confidence",
                    Before = currentConfidence,
                    After = newConfidence,
                    Improved = true,
                    Reason = "Higher confidence scores from better extraction"
                };
            }

            // Check for better relationships
            var currentRelationships = await _entityRepository.GetRelationshipsAsync(task.DocumentId, ct);
            var newRelationships = reprocessResult.Relationships?.Count ?? 0;

            if (newRelationships > currentRelationships.Count)
            {
                shouldUpdate = true;
                var delta = newRelationships - currentRelationships.Count;
                improvements.Add($"+{delta} relationships");

                metrics["relationship_count"] = new ComparisonMetric
                {
                    Name = "Relationship Count",
                    Before = currentRelationships.Count,
                    After = newRelationships,
                    Improved = true,
                    Reason = $"Found {delta} additional relationships"
                };
            }

            // PHASE 4: Update if improvements found
            if (shouldUpdate)
            {
                _logger.LogInformation(
                    "Updating document {DocumentId} with improvements: {Improvements}",
                    task.DocumentId, string.Join(", ", improvements));

                // Update entities
                await _entityRepository.ReplaceEntitiesAsync(task.DocumentId, reprocessResult.Entities, ct);

                // Update evidence
                await _evidenceRepository.SaveAsync(new EvidenceArtifact
                {
                    Id = Guid.NewGuid(),
                    DocumentId = task.DocumentId,
                    Type = "entity_extraction_learned",
                    Content = System.Text.Json.JsonSerializer.Serialize(reprocessResult.Entities),
                    Metadata = new Dictionary<string, object>
                    {
                        ["entity_count"] = newEntityCount,
                        ["average_confidence"] = newConfidence,
                        ["learning_run"] = DateTime.UtcNow,
                        ["improvements"] = improvements
                    }
                }, ct);

                return new LearningResult
                {
                    DocumentId = task.DocumentId,
                    Success = true,
                    ProcessingTime = DateTime.UtcNow - startTime,
                    ImprovementsApplied = true,
                    Improvements = improvements,
                    Metrics = metrics
                };
            }
            else
            {
                _logger.LogInformation("No improvements found for document {DocumentId}", task.DocumentId);

                return new LearningResult
                {
                    DocumentId = task.DocumentId,
                    Success = true,
                    ProcessingTime = DateTime.UtcNow - startTime,
                    ImprovementsApplied = false
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Learning failed for document {DocumentId}", task.DocumentId);

            return new LearningResult
            {
                DocumentId = task.DocumentId,
                Success = false,
                ProcessingTime = DateTime.UtcNow - startTime,
                ImprovementsApplied = false,
                ErrorMessage = ex.Message
            };
        }
    }
}

/// <summary>
/// Document processing service interface (extended for learning).
/// </summary>
public interface IDocumentProcessingService
{
    Task<DocumentEntity?> GetDocumentAsync(Guid documentId, CancellationToken ct = default);

    Task<ReprocessResult> ReprocessFullStackAsync(
        string filePath,
        ProcessingOptions options,
        CancellationToken ct = default);
}

/// <summary>
/// Result of reprocessing a document.
/// </summary>
public class ReprocessResult
{
    public required List<ExtractedEntity> Entities { get; init; }
    public List<EntityRelationship>? Relationships { get; init; }
    public double AverageConfidence { get; init; }
    public TimeSpan ProcessingTime { get; init; }
}

/// <summary>
/// Processing options (extended for learning).
/// </summary>
public class ProcessingOptions
{
    public bool ExtractEntities { get; set; }
    public bool GenerateEmbeddings { get; set; }
    public bool ExtractRelationships { get; set; }

    /// <summary>Run ALL extractors, no early exit (for learning mode)</summary>
    public bool RunAllExtractors { get; set; }

    /// <summary>Learning mode - use slower but better algorithms</summary>
    public bool LearningMode { get; set; }
}

// Interfaces and models moved to ILearningRepositories.cs
