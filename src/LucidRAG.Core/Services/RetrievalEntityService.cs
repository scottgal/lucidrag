using System.Text.Json;
using LucidRAG.Data;
using LucidRAG.Entities;
using Microsoft.EntityFrameworkCore;
using StyloFlow.Retrieval.Analysis;
using StyloFlow.Retrieval.Entities;
using StyloSignal = StyloFlow.Retrieval.Analysis.Signal;
using StyloContentMetadata = StyloFlow.Retrieval.Entities.ContentMetadata;
using ContentType = StyloFlow.Retrieval.Entities.ContentType;
using StyloEntityRelationship = StyloFlow.Retrieval.Entities.EntityRelationship;

namespace LucidRAG.Services;

/// <summary>
/// Unified service for storing and querying RetrievalEntities across all content types.
/// This is the central point for cross-modal storage - documents, images, audio, video, data.
/// </summary>
public interface IRetrievalEntityService
{
    /// <summary>
    /// Store a RetrievalEntity from any modality.
    /// </summary>
    Task<string> StoreAsync(RetrievalEntity entity, CancellationToken ct = default);

    /// <summary>
    /// Get a RetrievalEntity by ID.
    /// </summary>
    Task<RetrievalEntity?> GetByIdAsync(string entityId, CancellationToken ct = default);

    /// <summary>
    /// Get all entities for a collection.
    /// </summary>
    Task<IReadOnlyList<RetrievalEntity>> GetByCollectionAsync(Guid collectionId, ContentType[]? contentTypes = null, CancellationToken ct = default);

    /// <summary>
    /// Search entities by text content.
    /// </summary>
    Task<IReadOnlyList<RetrievalEntity>> SearchAsync(
        string query,
        ContentType[]? contentTypes = null,
        Guid? collectionId = null,
        int topK = 10,
        CancellationToken ct = default);

    /// <summary>
    /// Delete an entity.
    /// </summary>
    Task<bool> DeleteAsync(string entityId, CancellationToken ct = default);

    /// <summary>
    /// Get entities that need review.
    /// </summary>
    Task<IReadOnlyList<RetrievalEntity>> GetNeedsReviewAsync(Guid? collectionId = null, CancellationToken ct = default);

    /// <summary>
    /// Get entity counts by content type.
    /// </summary>
    Task<Dictionary<ContentType, int>> GetCountsByTypeAsync(Guid? collectionId = null, CancellationToken ct = default);

    /// <summary>
    /// Convert existing document to RetrievalEntity and store it.
    /// </summary>
    Task<RetrievalEntity> StoreDocumentAsync(
        DocumentEntity document,
        IReadOnlyList<Mostlylucid.DocSummarizer.Models.Segment> segments,
        IReadOnlyList<LucidRAG.Entities.ExtractedEntity>? entities = null,
        Mostlylucid.DocSummarizer.Models.DocumentSummary? summary = null,
        CancellationToken ct = default);

    /// <summary>
    /// Add an embedding to an entity.
    /// </summary>
    Task AddEmbeddingAsync(string entityId, string name, float[] vector, string? model = null, CancellationToken ct = default);
}

/// <summary>
/// Implementation of cross-modal retrieval storage with multi-vector support.
/// Uses PostgreSQL/SQLite for entity and embedding storage.
/// </summary>
public class RetrievalEntityService : IRetrievalEntityService
{
    private readonly RagDocumentsDbContext _dbContext;
    private readonly ILogger<RetrievalEntityService> _logger;

    public RetrievalEntityService(
        RagDocumentsDbContext dbContext,
        ILogger<RetrievalEntityService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<string> StoreAsync(RetrievalEntity entity, CancellationToken ct = default)
    {
        var record = ConvertToRecord(entity);

        var existing = await _dbContext.RetrievalEntities
            .FirstOrDefaultAsync(e => e.Id == record.Id, ct);

        if (existing != null)
        {
            _dbContext.Entry(existing).CurrentValues.SetValues(record);
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            await _dbContext.RetrievalEntities.AddAsync(record, ct);
        }

        await _dbContext.SaveChangesAsync(ct);

        // Store embeddings
        if (entity.Embedding != null)
        {
            await AddEmbeddingInternalAsync(record.Id, EmbeddingNames.Text, entity.Embedding, entity.EmbeddingModel, ct);
        }

        if (entity.AdditionalEmbeddings != null)
        {
            foreach (var (name, vector) in entity.AdditionalEmbeddings)
            {
                await AddEmbeddingInternalAsync(record.Id, name, vector, null, ct);
            }
        }

        _logger.LogInformation(
            "Stored {ContentType} entity {Id} in collection {Collection}",
            entity.ContentType, entity.Id, entity.Collection ?? "default");

        return entity.Id;
    }

    public async Task<RetrievalEntity?> GetByIdAsync(string entityId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(entityId, out var id))
            return null;

        var record = await _dbContext.RetrievalEntities
            .Include(e => e.Embeddings)
            .FirstOrDefaultAsync(e => e.Id == id, ct);

        return record != null ? ConvertToEntity(record) : null;
    }

    public async Task<IReadOnlyList<RetrievalEntity>> GetByCollectionAsync(
        Guid collectionId,
        ContentType[]? contentTypes = null,
        CancellationToken ct = default)
    {
        var query = _dbContext.RetrievalEntities
            .Include(e => e.Embeddings)
            .Where(e => e.CollectionId == collectionId);

        if (contentTypes != null && contentTypes.Length > 0)
        {
            var typeStrings = contentTypes.Select(t => t.ToString()).ToArray();
            query = query.Where(e => typeStrings.Contains(e.ContentType));
        }

        var records = await query.OrderByDescending(e => e.CreatedAt).ToListAsync(ct);
        return records.Select(ConvertToEntity).ToList();
    }

    public async Task<IReadOnlyList<RetrievalEntity>> SearchAsync(
        string query,
        ContentType[]? contentTypes = null,
        Guid? collectionId = null,
        int topK = 10,
        CancellationToken ct = default)
    {
        // Simple text search using database LIKE - for full semantic search,
        // integrate with vector store or use pgvector/sqlite-vss
        var dbQuery = _dbContext.RetrievalEntities
            .Include(e => e.Embeddings)
            .AsQueryable();

        if (collectionId.HasValue)
            dbQuery = dbQuery.Where(e => e.CollectionId == collectionId);

        if (contentTypes != null && contentTypes.Length > 0)
        {
            var typeStrings = contentTypes.Select(t => t.ToString()).ToArray();
            dbQuery = dbQuery.Where(e => typeStrings.Contains(e.ContentType));
        }

        // Text search on title, summary, and content
        var searchTerms = query.ToLower();
        dbQuery = dbQuery.Where(e =>
            (e.Title != null && e.Title.ToLower().Contains(searchTerms)) ||
            (e.Summary != null && e.Summary.ToLower().Contains(searchTerms)) ||
            (e.TextContent != null && e.TextContent.ToLower().Contains(searchTerms)));

        var records = await dbQuery
            .OrderByDescending(e => e.QualityScore)
            .Take(topK)
            .ToListAsync(ct);

        return records.Select(ConvertToEntity).ToList();
    }

    public async Task<bool> DeleteAsync(string entityId, CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(entityId, out var id))
                return false;

            var record = await _dbContext.RetrievalEntities.FindAsync([id], ct);
            if (record == null)
                return false;

            _dbContext.RetrievalEntities.Remove(record);
            await _dbContext.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete entity {EntityId}", entityId);
            return false;
        }
    }

    public async Task<IReadOnlyList<RetrievalEntity>> GetNeedsReviewAsync(Guid? collectionId = null, CancellationToken ct = default)
    {
        var query = _dbContext.RetrievalEntities
            .Include(e => e.Embeddings)
            .Where(e => e.NeedsReview);

        if (collectionId.HasValue)
            query = query.Where(e => e.CollectionId == collectionId);

        var records = await query.OrderByDescending(e => e.CreatedAt).ToListAsync(ct);
        return records.Select(ConvertToEntity).ToList();
    }

    public async Task<Dictionary<ContentType, int>> GetCountsByTypeAsync(Guid? collectionId = null, CancellationToken ct = default)
    {
        var query = _dbContext.RetrievalEntities.AsQueryable();

        if (collectionId.HasValue)
            query = query.Where(e => e.CollectionId == collectionId);

        var counts = await query
            .GroupBy(e => e.ContentType)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return counts.ToDictionary(
            x => Enum.TryParse<ContentType>(x.Type, out var type) ? type : ContentType.Unknown,
            x => x.Count);
    }

    public async Task<RetrievalEntity> StoreDocumentAsync(
        DocumentEntity document,
        IReadOnlyList<Mostlylucid.DocSummarizer.Models.Segment> segments,
        IReadOnlyList<LucidRAG.Entities.ExtractedEntity>? entities = null,
        Mostlylucid.DocSummarizer.Models.DocumentSummary? summary = null,
        CancellationToken ct = default)
    {
        var builder = new EntityBuilder()
            .WithId(document.Id.ToString("N"))
            .WithContentType(ContentType.Document)
            .WithSource(document.FilePath ?? document.SourceUrl ?? document.Name)
            .WithContentHash(document.ContentHash)
            .WithTitle(document.OriginalFilename ?? document.Name);

        if (document.CollectionId.HasValue)
            builder.WithCollection(document.CollectionId.Value.ToString("N"));

        // Build text content from segments
        var textContent = string.Join("\n\n", segments.Select(s => s.Text));
        builder.WithTextContent(textContent);

        // Use centroid of all embeddings
        var embeddingsWithVectors = segments.Where(s => s.Embedding != null).Select(s => s.Embedding!).ToList();
        if (embeddingsWithVectors.Count > 0)
        {
            var centroid = ComputeCentroid(embeddingsWithVectors);
            if (centroid != null)
                builder.WithEmbedding(centroid, "all-MiniLM-L6-v2");
        }

        // Add summary - prefer executive summary from LLM synthesis, fall back to top salient segment
        if (!string.IsNullOrEmpty(summary?.ExecutiveSummary))
        {
            // Truncate to 4000 chars for database storage
            var execSummary = summary.ExecutiveSummary.Length > 4000
                ? summary.ExecutiveSummary[..4000] + "..."
                : summary.ExecutiveSummary;
            builder.WithSummary(execSummary);
        }
        else
        {
            // Fallback: use highest salience segment
            var topSegment = segments.OrderByDescending(s => s.SalienceScore).FirstOrDefault();
            if (topSegment != null)
                builder.WithSummary(topSegment.Text.Length > 500 ? topSegment.Text[..500] + "..." : topSegment.Text);
        }

        // Add topic summaries to signals if available
        if (summary?.TopicSummaries?.Count > 0)
        {
            var topicsJson = System.Text.Json.JsonSerializer.Serialize(summary.TopicSummaries.Select(t => new { t.Topic, t.Summary }));
            builder.WithSignal(new StyloSignal { Key = "document.topics", Value = topicsJson, Source = "BertRAG" });
        }

        // Add open questions to signals if available
        if (summary?.OpenQuestions?.Count > 0)
        {
            var questionsJson = System.Text.Json.JsonSerializer.Serialize(summary.OpenQuestions);
            builder.WithSignal(new StyloSignal { Key = "document.open_questions", Value = questionsJson, Source = "BertRAG" });
        }

        // Add signals
        builder.WithSignal(new StyloSignal { Key = "document.segment_count", Value = segments.Count, Source = "LucidRAG" });
        builder.WithSignal(new StyloSignal { Key = "document.file_size", Value = document.FileSizeBytes, Source = "LucidRAG" });
        builder.WithSignal(new StyloSignal { Key = "document.mime_type", Value = document.MimeType, Source = "LucidRAG" });
        builder.WithSignal(new StyloSignal { Key = "document.status", Value = document.Status.ToString(), Source = "LucidRAG" });

        // Add entities if available
        if (entities != null)
        {
            foreach (var entity in entities)
            {
                builder.WithEntity(new StyloFlow.Retrieval.Entities.ExtractedEntity
                {
                    Id = entity.Id.ToString("N"),
                    Name = entity.CanonicalName,
                    Type = entity.EntityType,
                    Description = entity.Description,
                    Confidence = 1.0,
                    Source = "GraphRAG"
                });
            }
        }

        // Build metadata
        var metadata = new StyloContentMetadata
        {
            WordCount = textContent.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
            MimeType = document.MimeType,
            FileSizeBytes = document.FileSizeBytes
        };
        builder.WithMetadata(metadata);

        // Check if needs review
        if (document.Status == DocumentStatus.Failed)
            builder.NeedsReview(document.StatusMessage ?? "Processing failed");

        builder.WithTag("document");
        if (!string.IsNullOrEmpty(document.MimeType))
            builder.WithTag(document.MimeType.Split('/').Last());

        var retrievalEntity = builder.Build();
        await StoreAsync(retrievalEntity, ct);

        return retrievalEntity;
    }

    public async Task AddEmbeddingAsync(string entityId, string name, float[] vector, string? model = null, CancellationToken ct = default)
    {
        if (!Guid.TryParse(entityId, out var id))
            throw new ArgumentException("Invalid entity ID", nameof(entityId));

        await AddEmbeddingInternalAsync(id, name, vector, model, ct);
    }

    private async Task AddEmbeddingInternalAsync(Guid entityId, string name, float[] vector, string? model, CancellationToken ct)
    {
        var existing = await _dbContext.EntityEmbeddings
            .FirstOrDefaultAsync(e => e.EntityId == entityId && e.Name == name, ct);

        var vectorJson = JsonSerializer.Serialize(vector);

        if (existing != null)
        {
            existing.Vector = vectorJson;
            existing.VectorBinary = VectorToBytes(vector);
            existing.Model = model;
            existing.Dimension = vector.Length;
        }
        else
        {
            await _dbContext.EntityEmbeddings.AddAsync(new EntityEmbedding
            {
                Id = Guid.NewGuid(),
                EntityId = entityId,
                Name = name,
                Model = model,
                Dimension = vector.Length,
                Vector = vectorJson,
                VectorBinary = VectorToBytes(vector)
            }, ct);
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    private static RetrievalEntityRecord ConvertToRecord(RetrievalEntity entity)
    {
        return new RetrievalEntityRecord
        {
            Id = Guid.TryParse(entity.Id, out var id) ? id : Guid.NewGuid(),
            ContentType = entity.ContentType.ToString(),
            Source = entity.Source,
            ContentHash = entity.ContentHash,
            CollectionId = !string.IsNullOrEmpty(entity.Collection) && Guid.TryParse(entity.Collection, out var cid) ? cid : null,
            Title = entity.Title,
            Summary = entity.Summary,
            TextContent = entity.TextContent,
            EmbeddingModel = entity.EmbeddingModel,
            QualityScore = entity.QualityScore,
            ContentConfidence = entity.ContentConfidence,
            NeedsReview = entity.NeedsReview,
            ReviewReason = entity.ReviewReason,
            Tags = entity.Tags != null ? JsonSerializer.Serialize(entity.Tags) : null,
            Metadata = entity.Metadata != null ? JsonSerializer.Serialize(entity.Metadata) : null,
            CustomMetadata = entity.CustomMetadata != null ? JsonSerializer.Serialize(entity.CustomMetadata) : null,
            Signals = entity.Signals != null ? JsonSerializer.Serialize(entity.Signals) : null,
            ExtractedEntities = entity.Entities != null ? JsonSerializer.Serialize(entity.Entities) : null,
            Relationships = entity.Relationships != null ? JsonSerializer.Serialize(entity.Relationships) : null,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
    }

    private static RetrievalEntity ConvertToEntity(RetrievalEntityRecord record)
    {
        var embeddings = new Dictionary<string, float[]>();
        float[]? primaryEmbedding = null;
        string? embeddingModel = record.EmbeddingModel;

        foreach (var emb in record.Embeddings)
        {
            var vector = emb.VectorBinary != null
                ? BytesToVector(emb.VectorBinary)
                : (!string.IsNullOrEmpty(emb.Vector) ? JsonSerializer.Deserialize<float[]>(emb.Vector) : null);

            if (vector != null)
            {
                if (emb.Name == EmbeddingNames.Text)
                {
                    primaryEmbedding = vector;
                    embeddingModel = emb.Model;
                }
                else
                {
                    embeddings[emb.Name] = vector;
                }
            }
        }

        return new RetrievalEntity
        {
            Id = record.Id.ToString("N"),
            ContentType = Enum.TryParse<ContentType>(record.ContentType, out var ct) ? ct : ContentType.Unknown,
            Source = record.Source,
            ContentHash = record.ContentHash,
            Collection = record.CollectionId?.ToString("N"),
            Title = record.Title,
            Summary = record.Summary,
            TextContent = record.TextContent,
            Embedding = primaryEmbedding,
            EmbeddingModel = embeddingModel,
            AdditionalEmbeddings = embeddings.Count > 0 ? embeddings : null,
            QualityScore = record.QualityScore,
            ContentConfidence = record.ContentConfidence,
            NeedsReview = record.NeedsReview,
            ReviewReason = record.ReviewReason,
            Tags = !string.IsNullOrEmpty(record.Tags) ? JsonSerializer.Deserialize<List<string>>(record.Tags) : null,
            Metadata = !string.IsNullOrEmpty(record.Metadata) ? JsonSerializer.Deserialize<StyloContentMetadata>(record.Metadata) : null,
            CustomMetadata = !string.IsNullOrEmpty(record.CustomMetadata) ? JsonSerializer.Deserialize<Dictionary<string, object>>(record.CustomMetadata) : null,
            Signals = !string.IsNullOrEmpty(record.Signals) ? JsonSerializer.Deserialize<List<StyloSignal>>(record.Signals) : null,
            Entities = !string.IsNullOrEmpty(record.ExtractedEntities) ? JsonSerializer.Deserialize<List<StyloFlow.Retrieval.Entities.ExtractedEntity>>(record.ExtractedEntities) : null,
            Relationships = !string.IsNullOrEmpty(record.Relationships) ? JsonSerializer.Deserialize<List<StyloEntityRelationship>>(record.Relationships) : null,
            CreatedAt = record.CreatedAt.UtcDateTime,
            UpdatedAt = record.UpdatedAt.UtcDateTime
        };
    }

    private static float[]? ComputeCentroid(IReadOnlyList<float[]> embeddings)
    {
        if (embeddings.Count == 0) return null;

        var dimension = embeddings[0].Length;
        var centroid = new float[dimension];

        foreach (var embedding in embeddings)
        {
            for (int i = 0; i < dimension; i++)
            {
                centroid[i] += embedding[i];
            }
        }

        for (int i = 0; i < dimension; i++)
        {
            centroid[i] /= embeddings.Count;
        }

        return centroid;
    }

    private static byte[] VectorToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BytesToVector(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }
}
