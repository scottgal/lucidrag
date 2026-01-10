using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services.Onnx;
using Mostlylucid.GraphRag;
using Mostlylucid.GraphRag.Extraction;
using Mostlylucid.GraphRag.Services;
using Mostlylucid.GraphRag.Storage;
using LucidRAG.Config;
using LucidRAG.Data;
using LucidRAG.Entities;

namespace LucidRAG.Services;

/// <summary>
/// Service for extracting entities from documents and building the knowledge graph.
/// Delegates to Mostlylucid.GraphRag for sophisticated IDF-based extraction with BERT deduplication.
/// </summary>
public interface IEntityGraphService
{
    /// <summary>
    /// Extract entities from segments using GraphRag's heuristic extraction
    /// </summary>
    Task<EntityExtractionResult> ExtractAndStoreEntitiesAsync(
        Guid documentId,
        IReadOnlyList<Segment> segments,
        CancellationToken ct = default);

    /// <summary>
    /// Get graph data for visualization (D3.js format)
    /// </summary>
    Task<GraphData> GetGraphDataAsync(Guid? documentId = null, CancellationToken ct = default);

    /// <summary>
    /// Get entities related to a search query
    /// </summary>
    Task<IReadOnlyList<EntityInfo>> GetRelatedEntitiesAsync(string query, int limit = 10, CancellationToken ct = default);
}

public record EntityExtractionResult(
    int EntitiesExtracted,
    int RelationshipsCreated,
    TimeSpan ProcessingTime);

public record GraphData(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges);

public record GraphNode(string Id, string Label, string Type, int MentionCount);
public record GraphEdge(string Source, string Target, string Type, float Weight);
public record EntityInfo(string Name, string Type, string? Description, int MentionCount);

public class EntityGraphService : IEntityGraphService, IDisposable
{
    private readonly RagDocumentsDbContext _db;
    private readonly RagDocumentsConfig _config;
    private readonly ILogger<EntityGraphService> _logger;
    private readonly GraphRagDb _graphDb;
    private readonly EmbeddingService _embedder;
    private bool _initialized;

    public EntityGraphService(
        RagDocumentsDbContext db,
        IOptions<RagDocumentsConfig> config,
        ILogger<EntityGraphService> logger)
    {
        _db = db;
        _config = config.Value;
        _logger = logger;

        // Initialize GraphRag's DuckDB for entity graph storage
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        var graphDbPath = Path.Combine(dataDir, "entities.duckdb");

        _graphDb = new GraphRagDb(graphDbPath, 384);
        _embedder = new EmbeddingService();
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        await _graphDb.InitializeAsync();
        await _embedder.InitializeAsync();
        _initialized = true;
    }

    public async Task<EntityExtractionResult> ExtractAndStoreEntitiesAsync(
        Guid documentId,
        IReadOnlyList<Segment> segments,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("Extracting entities from {Count} segments for document {DocumentId}",
            segments.Count, documentId);

        // Convert Segments to GraphRag ChunkResults
        var docIdStr = documentId.ToString("N");
        var chunks = segments.Select((s, i) => new ChunkResult(
            $"{docIdStr}_{i}",
            docIdStr,
            s.Text,
            i,
            0
        )).ToList();

        // Store document reference in GraphRag
        await _graphDb.UpsertDocumentAsync(docIdStr, $"doc:{documentId}", "", "");

        // Delete any existing chunks for this document (re-indexing)
        await _graphDb.DeleteDocumentChunksAsync(docIdStr);

        // Store chunks with embeddings
        foreach (var chunk in chunks)
        {
            var embedding = await _embedder.EmbedAsync(chunk.Text, ct);
            await _graphDb.InsertChunkAsync(chunk.Id, chunk.DocumentId, chunk.ChunkIndex, chunk.Text, embedding,
                chunk.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        }

        // Run entity extraction using configured mode
        var extractionMode = _config.ExtractionMode;
        _logger.LogInformation("Running entity extraction with mode: {Mode}", extractionMode);

        var extractor = new EntityExtractor(_graphDb, _embedder, null, extractionMode);
        var result = await extractor.ExtractAsync(null, ct);

        // Sync extracted entities to PostgreSQL for relational queries
        await SyncEntitiesToPostgresAsync(documentId, ct);

        sw.Stop();
        _logger.LogInformation(
            "Entity extraction complete: {Entities} entities, {Relationships} relationships in {Time}ms",
            result.EntitiesExtracted, result.RelationshipsExtracted, sw.ElapsedMilliseconds);

        return new EntityExtractionResult(
            result.EntitiesExtracted,
            result.RelationshipsExtracted,
            sw.Elapsed);
    }

    private async Task SyncEntitiesToPostgresAsync(Guid documentId, CancellationToken ct)
    {
        // Get entities from GraphRag DuckDB
        var graphEntities = await _graphDb.GetAllEntitiesAsync();
        var graphRelationships = await _graphDb.GetAllRelationshipsAsync();

        foreach (var ge in graphEntities)
        {
            // Check if entity exists in PostgreSQL
            var existing = await _db.Entities
                .FirstOrDefaultAsync(e => e.CanonicalName.ToLower() == ge.Name.ToLower(), ct);

            Guid entityId;
            if (existing != null)
            {
                entityId = existing.Id;
            }
            else
            {
                var entity = new ExtractedEntity
                {
                    Id = Guid.NewGuid(),
                    CanonicalName = ge.Name,
                    EntityType = ge.Type,
                    Description = ge.Description,
                    Aliases = []
                };
                _db.Entities.Add(entity);
                entityId = entity.Id;
            }

            // Create document-entity link
            var existingLink = await _db.DocumentEntityLinks
                .FirstOrDefaultAsync(l => l.DocumentId == documentId && l.EntityId == entityId, ct);

            if (existingLink == null)
            {
                _db.DocumentEntityLinks.Add(new DocumentEntityLink
                {
                    DocumentId = documentId,
                    EntityId = entityId,
                    MentionCount = ge.MentionCount,
                    SegmentIds = []
                });
            }
            else
            {
                existingLink.MentionCount = ge.MentionCount;
            }
        }

        // Sync relationships
        var entityLookup = await _db.Entities
            .ToDictionaryAsync(e => e.CanonicalName.ToLower(), e => e.Id, ct);

        foreach (var gr in graphRelationships)
        {
            if (!entityLookup.TryGetValue(gr.SourceName.ToLower(), out var sourceId) ||
                !entityLookup.TryGetValue(gr.TargetName.ToLower(), out var targetId))
                continue;

            var existing = await _db.EntityRelationships
                .FirstOrDefaultAsync(r =>
                    r.SourceEntityId == sourceId && r.TargetEntityId == targetId &&
                    r.RelationshipType == gr.RelationshipType, ct);

            if (existing == null)
            {
                _db.EntityRelationships.Add(new EntityRelationship
                {
                    Id = Guid.NewGuid(),
                    SourceEntityId = sourceId,
                    TargetEntityId = targetId,
                    RelationshipType = gr.RelationshipType,
                    Strength = gr.Weight,
                    SourceDocuments = [documentId]
                });
            }
            else
            {
                existing.Strength = Math.Max(existing.Strength, gr.Weight);
                if (!existing.SourceDocuments.Contains(documentId))
                    existing.SourceDocuments = [..existing.SourceDocuments, documentId];
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<GraphData> GetGraphDataAsync(Guid? documentId = null, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();

        // Get data from GraphRag DuckDB (faster for graph queries)
        var entities = await _graphDb.GetAllEntitiesAsync();
        var relationships = await _graphDb.GetAllRelationshipsAsync();

        var nodes = entities
            .Select(e => new GraphNode(e.Id, e.Name, e.Type, e.MentionCount))
            .ToList();

        var edges = relationships
            .Select(r => new GraphEdge(r.SourceEntityId, r.TargetEntityId, r.RelationshipType, r.Weight))
            .ToList();

        return new GraphData(nodes, edges);
    }

    public async Task<IReadOnlyList<EntityInfo>> GetRelatedEntitiesAsync(
        string query,
        int limit = 10,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync();

        // Embed query and search for similar entities
        var queryEmbedding = await _embedder.EmbedAsync(query, ct);
        var chunks = await _graphDb.SearchChunksAsync(queryEmbedding, limit * 2);

        // Get entities mentioned in matching chunks
        var entitySet = new Dictionary<string, EntityResult>();
        foreach (var chunk in chunks)
        {
            var chunkEntities = await _graphDb.GetEntitiesInChunkAsync(chunk.Id);
            foreach (var e in chunkEntities)
            {
                if (!entitySet.ContainsKey(e.Id))
                    entitySet[e.Id] = e;
            }
        }

        return entitySet.Values
            .OrderByDescending(e => e.MentionCount)
            .Take(limit)
            .Select(e => new EntityInfo(e.Name, e.Type, e.Description, e.MentionCount))
            .ToList();
    }

    public void Dispose()
    {
        _graphDb.Dispose();
        _embedder.Dispose();
    }
}
