using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Services.Onnx;

namespace DoomSummarizer.Services;

/// <summary>
///     Self-assembling knowledge graph backed by DuckDB with HNSW vector search.
///     Accumulates entities, relationships, and provenance across runs.
///     Uses freshness-weighted scoring so recent information surfaces first.
///     Entity Profile Feature:
///     Each document gets a 384-dim entity profile embedding that encodes:
///     - Which entities appear (via their text embeddings)
///     - How distinctive each entity is (Entity IDF - rare entities matter more)
///     - How frequently each entity appears (TF in document)
///     - How confident the NER was (confidence weighting)
///     This enables O(log N) entity-based similarity search via HNSW.
/// </summary>
public class KnowledgeGraphService
{
    private readonly EntityProfileService? _entityProfileService;
    private readonly IEntityGraphStore _entityStore;
    private readonly IItemVectorStore _vectorStore;

    public KnowledgeGraphService(IItemVectorStore vectorStore, IEntityGraphStore entityStore)
    {
        _vectorStore = vectorStore;
        _entityStore = entityStore;
    }

    public KnowledgeGraphService(IItemVectorStore vectorStore, IEntityGraphStore entityStore,
        EntityProfileService entityProfileService)
    {
        _vectorStore = vectorStore;
        _entityStore = entityStore;
        _entityProfileService = entityProfileService;
    }

    /// <summary>
    ///     Index item embeddings into DuckDB for HNSW similarity search.
    /// </summary>
    public async Task IndexItemEmbeddingsAsync(
        List<ContentItem> items,
        CancellationToken ct = default)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Embedding == null) continue;

            await _vectorStore.UpsertItemEmbeddingAsync(
                item.Id, item.Title, item.Source, item.Url, item.Embedding);
        }
    }

    /// <summary>
    ///     Ingest NER entities from analyzed articles into the knowledge graph.
    ///     Builds entity nodes, article→entity mentions, co-occurrence edges, and entity profiles.
    ///     Also stores entity text embeddings for efficient profile computation.
    /// </summary>
    public async Task IngestEntitiesAsync(
        List<(ContentItem item, List<NerEntity> entities)> articleEntities,
        CancellationToken ct = default)
    {
        // Collect all unique entity names for batch embedding
        var allEntityNames = articleEntities
            .SelectMany(ae => ae.entities)
            .Select(e => e.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Batch compute entity text embeddings (for storage and profile computation)
        var entityTextEmbeddings = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        if (_entityProfileService != null && allEntityNames.Count > 0)
            foreach (var name in allEntityNames)
            {
                // EntityProfileService has access to EmbeddingService
                // For now, embeddings will be computed on-demand during profile computation
                // and cached in the entities table for future use
            }

        // First pass: upsert all entities and mentions
        var itemEntityData =
            new List<(string itemId, List<(string entityId, string name, string type, float confidence, int mentions)>
                entities)>();
        var newEntityIds = new HashSet<string>(); // Track new entities for embedding storage

        foreach (var (item, entities) in articleEntities)
        {
            ct.ThrowIfCancellationRequested();

            // Ensure the item is in the vector store (for article provenance lookups)
            if (item.Embedding != null)
                await _vectorStore.UpsertItemEmbeddingAsync(
                    item.Id, item.Title, item.Source, item.Url, item.Embedding);

            // Deduplicate entities within this article (case-insensitive)
            var deduped = entities
                .GroupBy(e => e.Text.ToLowerInvariant())
                .Select(g => g.MaxBy(e => e.Confidence)!)
                .ToList();

            // Upsert each entity and record the mention
            var entityIds = new List<string>();
            var entityData = new List<(string entityId, string name, string type, float confidence, int mentions)>();

            foreach (var entity in deduped)
            {
                var entityId = GenerateEntityId(entity.Text, entity.Type);
                entityIds.Add(entityId);
                newEntityIds.Add(entityId);

                await _entityStore.UpsertEntityAsync(entityId, entity.Text, entity.Type, entity.Confidence);
                await _entityStore.UpsertEntityMentionAsync(entityId, item.Id, entity.Confidence,
                    TruncateContext(item.Title, 200));

                // Track entity data for profile computation (includes type for weighting)
                entityData.Add((entityId, entity.Text, entity.Type, (float)entity.Confidence, 1));
            }

            // Build co-occurrence relationships (entities in the same article)
            for (var i = 0; i < entityIds.Count; i++)
            for (var j = i + 1; j < entityIds.Count; j++)
                await _entityStore.UpsertRelationshipAsync(entityIds[i], entityIds[j]);

            if (entityData.Count > 0) itemEntityData.Add((item.Id, entityData));
        }

        // Second pass: compute and store entity embeddings + profiles
        if (_entityProfileService != null && itemEntityData.Count > 0)
            await ComputeAndStoreEntityProfilesAsync(itemEntityData, ct);
    }

    /// <summary>
    ///     Compute and store entity profiles for items that have entity mentions.
    ///     Uses TF × IDF × confidence × type weighting with:
    ///     - Saturating TF: 1 + log(mentions) to prevent boilerplate dominance
    ///     - Smoothed IDF: log((N+1)/(df+1)) + 1 for stable extremes
    ///     - Confidence floor: clamp(0.2, 1.0) so low-confidence entities don't vanish
    ///     - Type weights: ORG/PER get slight boost over LOC/MISC
    ///     Also caches entity text embeddings for future profile computations.
    /// </summary>
    private async Task ComputeAndStoreEntityProfilesAsync(
        List<(string itemId, List<(string entityId, string name, string type, float confidence, int mentions)> entities
            )> itemEntityData,
        CancellationToken ct)
    {
        // Get global entity document counts for IDF computation
        var entityDocCounts = await _entityStore.GetEntityDocCountsAsync();
        var totalDocs = await _entityStore.GetTotalDocsWithEntitiesAsync();
        if (totalDocs == 0) totalDocs = itemEntityData.Count; // Fallback for first batch

        // Collect all entity IDs for batch embedding fetch
        var allEntityIds = itemEntityData
            .SelectMany(d => d.entities.Select(e => e.entityId))
            .Distinct()
            .ToList();

        // Fetch cached entity embeddings to avoid re-embedding
        var entityEmbeddings = await _entityStore.GetEntityEmbeddingsAsync(allEntityIds);

        // Track newly computed embeddings for caching
        var newlyComputedEmbeddings = new Dictionary<string, float[]>();

        foreach (var (itemId, entityList) in itemEntityData)
        {
            ct.ThrowIfCancellationRequested();

            var (profile, topEntities) = await _entityProfileService!.ComputeProfileWithExplainAsync(
                entityList, entityDocCounts, totalDocs, entityEmbeddings, newlyComputedEmbeddings);

            if (profile.Length > 0) await _entityStore.UpsertItemEntityProfileAsync(itemId, profile);

            // Add newly computed embeddings to cache for subsequent items in this batch
            foreach (var (entityId, embedding) in newlyComputedEmbeddings) entityEmbeddings[entityId] = embedding;
        }

        // Store all newly computed entity embeddings back to the database
        if (newlyComputedEmbeddings.Count > 0)
            await _entityStore.UpdateEntityEmbeddingsBatchAsync(newlyComputedEmbeddings);
    }

    /// <summary>
    ///     Ingest linked page entities with lower confidence (one hop away).
    /// </summary>
    public async Task IngestLinkedPageEntitiesAsync(
        ContentItem parentItem,
        List<NerEntity> linkedEntities,
        string linkedUrl,
        CancellationToken ct = default)
    {
        var deduped = linkedEntities
            .GroupBy(e => e.Text.ToLowerInvariant())
            .Select(g => g.MaxBy(e => e.Confidence)!)
            .ToList();

        foreach (var entity in deduped)
        {
            ct.ThrowIfCancellationRequested();
            var entityId = GenerateEntityId(entity.Text, entity.Type);

            var adjustedConfidence = entity.Confidence * 0.7;
            await _entityStore.UpsertEntityAsync(entityId, entity.Text, entity.Type, adjustedConfidence);
            await _entityStore.UpsertEntityMentionAsync(entityId, parentItem.Id, adjustedConfidence,
                $"[linked: {TruncateContext(linkedUrl, 100)}]");
        }
    }

    /// <summary>
    ///     Find items similar to a query using HNSW vector search.
    /// </summary>
    public async Task<List<(string itemId, string title, string? url, float similarity)>> FindSimilarAsync(
        float[] queryEmbedding, int topK = 10, float minSimilarity = 0.5f)
    {
        return await _vectorStore.FindSimilarItemsAsync(queryEmbedding, topK, minSimilarity);
    }

    /// <summary>
    ///     Find related items using entity profile HNSW similarity.
    ///     Computes an aggregate entity profile from the given item IDs, then finds similar items.
    /// </summary>
    public async Task<List<(string itemId, string title, float similarity)>> FindRelatedByEntityProfileAsync(
        List<string> itemIds, int topK = 5, float minSimilarity = 0.3f)
    {
        if (_entityProfileService == null || itemIds.Count == 0)
            return [];

        // Get entity profiles for the seed items
        var profiles = await _entityStore.GetEntityProfilesAsync(itemIds);
        if (profiles.Count == 0)
            return [];

        // Compute aggregate profile
        var profileList = profiles.Values.ToList();
        var aggregateProfile = _entityProfileService.ComputeAggregateProfile(profileList);
        if (aggregateProfile.Length == 0)
            return [];

        // Find similar items via HNSW
        var results = await _entityStore.FindRelatedByEntityProfileAsync(
            aggregateProfile, topK + itemIds.Count, minSimilarity);

        // Exclude the seed items from results
        var seedSet = new HashSet<string>(itemIds, StringComparer.OrdinalIgnoreCase);
        return results
            .Where(r => !seedSet.Contains(r.itemId))
            .Take(topK)
            .ToList();
    }

    /// <summary>
    ///     Check if the vector store has any entity profiles computed.
    /// </summary>
    public async Task<bool> HasEntityProfilesAsync()
    {
        return await _entityStore.HasEntityProfilesAsync();
    }

    /// <summary>
    ///     Get entity document counts for IDF computation.
    /// </summary>
    public async Task<Dictionary<string, int>> GetEntityDocCountsAsync()
    {
        return await _entityStore.GetEntityDocCountsAsync();
    }

    /// <summary>
    ///     Get total number of documents with entity mentions.
    /// </summary>
    public async Task<int> GetTotalDocsWithEntitiesAsync()
    {
        return await _entityStore.GetTotalDocsWithEntitiesAsync();
    }

    /// <summary>
    ///     Backfill entity profiles for existing KB items that have entity mentions but no profile.
    ///     Returns the number of items processed.
    /// </summary>
    public async Task<int> BackfillEntityProfilesAsync(int batchSize = 50, CancellationToken ct = default)
    {
        if (_entityProfileService == null)
            throw new InvalidOperationException("EntityProfileService is required for backfill");

        var totalProcessed = 0;

        // Get global stats for IDF computation
        var entityDocCounts = await _entityStore.GetEntityDocCountsAsync();
        var totalDocs = await _entityStore.GetTotalDocsWithEntitiesAsync();
        if (totalDocs == 0) return 0;

        // Fetch cached entity embeddings
        var allEntityIds = entityDocCounts.Keys.ToList();
        var entityEmbeddings = await _entityStore.GetEntityEmbeddingsAsync(allEntityIds);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Get items without entity profiles
            var itemIds = await _entityStore.GetItemsWithoutEntityProfilesAsync(batchSize);
            if (itemIds.Count == 0) break;

            // Get all entities for these items
            var itemEntities = await _entityStore.GetEntitiesForItemsAsync(itemIds);

            // Group by item_id
            var byItem = itemEntities
                .GroupBy(e => e.itemId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var itemId in itemIds)
            {
                ct.ThrowIfCancellationRequested();

                if (!byItem.TryGetValue(itemId, out var entities) || entities.Count == 0)
                    continue;

                // Convert to profile service format
                var entityList = entities
                    .Select(e => (e.entityId, e.name, e.type, e.confidence, e.mentions))
                    .ToList();

                var (profile, _) = await _entityProfileService.ComputeProfileWithExplainAsync(
                    entityList, entityDocCounts, totalDocs, entityEmbeddings);

                if (profile.Length > 0)
                {
                    await _entityStore.UpsertItemEntityProfileAsync(itemId, profile);
                    totalProcessed++;
                }
            }
        }

        return totalProcessed;
    }

    /// <summary>
    ///     Display a mini knowledge graph as plain text output.
    /// </summary>
    public async Task DisplayGraphAsync(int topN = 15, int? daysBack = null)
    {
        var (entityCount, relCount, mentionCount, itemCount) = await _entityStore.GetStatsAsync();

        if (entityCount == 0)
        {
            Debug.WriteLine("Knowledge graph empty. Entities are extracted on each run.");
            return;
        }

        var entities = await _entityStore.GetTopEntitiesAsync(topN, daysBack: daysBack);

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine(
            $"Knowledge Graph ({entityCount} entities, {relCount} edges, {mentionCount} mentions, {itemCount} items)");
        sb.AppendLine(new string('=', 80));

        // Group entities by type
        var byType = entities
            .OrderByDescending(e => e.FreshnessScore)
            .GroupBy(e => e.Type)
            .OrderByDescending(g => g.Sum(e => e.MentionCount));

        foreach (var typeGroup in byType)
        {
            var typeLabel = typeGroup.Key switch
            {
                "PER" => "People",
                "ORG" => "Organizations",
                "LOC" => "Locations",
                _ => typeGroup.Key
            };

            sb.AppendLine($"  [{typeLabel}]");

            foreach (var entity in typeGroup.OrderByDescending(e => e.FreshnessScore).Take(5))
            {
                sb.AppendLine($"    {entity.Name} ({entity.MentionCount} mentions, {entity.ArticleCount} articles)");

                // Show relationships (deduplicated by related entity name)
                var relationships = await _entityStore.GetRelationshipsAsync(entity.Id);
                if (relationships.Count > 0)
                {
                    sb.AppendLine("      Related:");
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var rel in relationships.OrderByDescending(r => r.Weight).Take(6))
                    {
                        var otherName = rel.SourceId == entity.Id ? rel.TargetName : rel.SourceName;
                        if (!seen.Add(otherName)) continue; // skip duplicate names
                        sb.AppendLine($"        {otherName} (weight: {rel.Weight:F0})");
                        if (seen.Count >= 4) break;
                    }
                }

                // Show article provenance (deduplicated by title)
                var articles = await _entityStore.GetArticlesForEntityAsync(entity.Id);
                var uniqueArticles = articles
                    .GroupBy(a => a.title.ToLowerInvariant().Trim())
                    .Select(g => g.First())
                    .ToList();
                if (uniqueArticles.Count > 0)
                {
                    sb.AppendLine("      Articles:");
                    foreach (var (_, title, url, _) in uniqueArticles.Take(3))
                    {
                        var truncTitle = title.Length > 60 ? title[..57] + "..." : title;
                        sb.AppendLine($"        {truncTitle}");
                    }
                }
            }
        }

        Debug.WriteLine(sb.ToString());
    }

    internal static string GenerateEntityId(string name, string type)
    {
        var normalized = name.Trim().ToLowerInvariant();
        var input = $"{type}:{normalized}";
        var hash = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(input))[..8]).ToLowerInvariant();
        return $"{type.ToLowerInvariant()}_{hash}";
    }

    private static string TruncateContext(string text, int maxLen)
    {
        return text.Length > maxLen ? text[..maxLen] + "..." : text;
    }
}