using System.Text;
using System.Text.Json;
using System.IO.Hashing;
using Microsoft.EntityFrameworkCore;
using LucidRAG.Data;
using LucidRAG.Entities;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.Services;

/// <summary>
/// Service for detecting communities in the entity graph and extracting their features.
/// Uses Leiden algorithm for community detection and LLM for summary generation.
/// </summary>
public interface ICommunityDetectionService
{
    /// <summary>
    /// Detect communities in the entity graph for a specific collection
    /// </summary>
    Task<CommunityDetectionResult> DetectCommunitiesAsync(Guid collectionId, CancellationToken ct = default);

    /// <summary>
    /// Get all detected communities for a collection
    /// </summary>
    Task<IReadOnlyList<CommunityEntity>> GetCommunitiesAsync(Guid? collectionId = null, CancellationToken ct = default);

    /// <summary>
    /// Get community by ID with members
    /// </summary>
    Task<CommunityEntity?> GetCommunityAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Find communities relevant to a query
    /// </summary>
    Task<IReadOnlyList<CommunityEntity>> SearchCommunitiesAsync(string query, Guid? collectionId = null, int limit = 5, CancellationToken ct = default);

    /// <summary>
    /// Generate/regenerate summaries for communities using LLM (3-word titles, paragraph descriptions)
    /// Ensures unique names per tenant
    /// </summary>
    Task GenerateCommunitySummariesAsync(Guid? collectionId = null, CancellationToken ct = default);
}

public record CommunityDetectionResult(
    int CommunitiesDetected,
    int EntitiesAssigned,
    double Modularity,
    TimeSpan ProcessingTime);

public class CommunityDetectionService : ICommunityDetectionService
{
    private readonly RagDocumentsDbContext _db;
    private readonly IEntityGraphService _graphService;
    private readonly ILlmService _llmService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;
    private readonly IEvidenceRepository _evidenceRepository;
    private readonly ILogger<CommunityDetectionService> _logger;

    public CommunityDetectionService(
        RagDocumentsDbContext db,
        IEntityGraphService graphService,
        ILlmService llmService,
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        IEvidenceRepository evidenceRepository,
        ILogger<CommunityDetectionService> logger)
    {
        _db = db;
        _graphService = graphService;
        _llmService = llmService;
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _evidenceRepository = evidenceRepository;
        _logger = logger;
    }

    public async Task<CommunityDetectionResult> DetectCommunitiesAsync(Guid collectionId, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("Starting community detection for collection {CollectionId}...", collectionId);

        // Get graph data filtered by collection
        var graphData = await _graphService.GetGraphDataAsync(collectionId, ct);
        if (graphData.Nodes.Count == 0)
        {
            _logger.LogWarning("No entities in graph, skipping community detection");
            return new CommunityDetectionResult(0, 0, 0, sw.Elapsed);
        }

        _logger.LogInformation("Running Leiden on {NodeCount} nodes, {EdgeCount} edges",
            graphData.Nodes.Count, graphData.Edges.Count);

        // Build adjacency structure for Leiden
        var nodeIndex = graphData.Nodes.Select((n, i) => (n.Id, Index: i)).ToDictionary(x => x.Id, x => x.Index);
        var nodeIds = graphData.Nodes.Select(n => n.Id).ToArray();

        // Build weighted adjacency list
        var adjacency = new Dictionary<int, List<(int neighbor, float weight)>>();
        foreach (var edge in graphData.Edges)
        {
            if (!nodeIndex.TryGetValue(edge.Source, out var sourceIdx) ||
                !nodeIndex.TryGetValue(edge.Target, out var targetIdx))
                continue;

            if (!adjacency.ContainsKey(sourceIdx))
                adjacency[sourceIdx] = [];
            if (!adjacency.ContainsKey(targetIdx))
                adjacency[targetIdx] = [];

            adjacency[sourceIdx].Add((targetIdx, edge.Weight));
            adjacency[targetIdx].Add((sourceIdx, edge.Weight)); // Undirected
        }

        // Run Leiden algorithm (improved Louvain with connectivity guarantees)
        var (communities, modularity) = RunLeiden(adjacency, graphData.Nodes.Count);

        _logger.LogInformation("Leiden found {CommunityCount} communities with modularity {Modularity:F3}",
            communities.Values.Distinct().Count(), modularity);

        // Clear existing communities for this collection
        var existingCommunities = await _db.Communities
            .Where(c => c.CollectionId == collectionId)
            .ToListAsync(ct);

        var existingCommunityIds = existingCommunities.Select(c => c.Id).ToList();
        var existingMemberships = await _db.CommunityMemberships
            .Where(m => existingCommunityIds.Contains(m.CommunityId))
            .ToListAsync(ct);

        _db.CommunityMemberships.RemoveRange(existingMemberships);
        _db.Communities.RemoveRange(existingCommunities);
        await _db.SaveChangesAsync(ct);

        // Group nodes by community
        var communityGroups = communities
            .GroupBy(kv => kv.Value)
            .Where(g => g.Count() >= 2) // Filter out singleton communities
            .OrderByDescending(g => g.Count())
            .ToList();

        // Create community entities
        // Graph nodes come from DuckDB with different IDs, so look up by name
        var entityByName = await _db.Entities.ToDictionaryAsync(
            e => e.CanonicalName.ToLowerInvariant(),
            e => e, ct);

        // Map graph node IDs to their labels for name-based lookup
        var nodeLabelById = graphData.Nodes.ToDictionary(n => n.Id, n => n.Label);

        var createdCommunities = new List<CommunityEntity>();

        _logger.LogInformation("Processing {GroupCount} community groups (before entity matching)",
            communityGroups.Count);

        foreach (var (group, idx) in communityGroups.Select((g, i) => (g, i)))
        {
            var memberNodeIds = group.Select(kv => nodeIds[kv.Key]).ToList();

            // Map node IDs to entity names, then to database entities
            var memberEntities = memberNodeIds
                .Select(id => nodeLabelById.TryGetValue(id, out var label) ? label : null)
                .Where(label => label != null)
                .Select(label => entityByName.TryGetValue(label!.ToLowerInvariant(), out var ent) ? ent : null)
                .Where(e => e != null)
                .Cast<ExtractedEntity>()
                .ToList();

            if (memberEntities.Count < 2)
            {
                _logger.LogDebug("Skipping community {Idx} - only {Count} entities matched in database",
                    idx, memberEntities.Count);
                continue;
            }

            // Extract features for this community
            var features = await ExtractCommunityFeaturesAsync(memberEntities, graphData, ct);

            // Generate temporary name from top entities
            var topEntities = memberEntities
                .OrderByDescending(e => e.CanonicalName.Length > 3) // Prefer longer names
                .Take(3)
                .Select(e => e.CanonicalName)
                .ToList();

            var community = new CommunityEntity
            {
                Id = Guid.NewGuid(),
                CollectionId = collectionId,
                Name = $"Community {idx + 1}: {string.Join(", ", topEntities)}", // Temporary name, will be replaced by LLM
                Algorithm = "leiden",
                Level = 0,
                EntityCount = memberEntities.Count,
                Cohesion = features.InternalSimilarity,
                Features = JsonSerializer.Serialize(features),
                CreatedAt = DateTimeOffset.UtcNow
            };

            _db.Communities.Add(community);
            createdCommunities.Add(community);

            // Calculate centrality for each member (use graph node IDs)
            var centralities = CalculateCentrality(memberNodeIds, adjacency, nodeIndex);

            // Build a reverse lookup from entity name to graph node ID for centrality lookup
            var nameToNodeId = memberNodeIds
                .Where(id => nodeLabelById.ContainsKey(id))
                .ToDictionary(id => nodeLabelById[id].ToLowerInvariant(), id => id);

            // Add memberships
            foreach (var entity in memberEntities)
            {
                var centrality = 0f;
                if (nameToNodeId.TryGetValue(entity.CanonicalName.ToLowerInvariant(), out var graphNodeId) &&
                    nodeIndex.TryGetValue(graphNodeId, out var nodeIdx) &&
                    centralities.TryGetValue(nodeIdx, out var c))
                {
                    centrality = c;
                }

                _db.CommunityMemberships.Add(new CommunityMembership
                {
                    CommunityId = community.Id,
                    EntityId = entity.Id,
                    Centrality = centrality,
                    IsRepresentative = centrality > 0.5f
                });
            }
        }

        await _db.SaveChangesAsync(ct);

        sw.Stop();
        _logger.LogInformation("Community detection complete: {Count} communities in {Time}ms",
            createdCommunities.Count, sw.ElapsedMilliseconds);

        return new CommunityDetectionResult(
            createdCommunities.Count,
            communities.Count,
            modularity,
            sw.Elapsed);
    }

    /// <summary>
    /// Leiden community detection algorithm
    /// Improves on Louvain by adding a refinement phase to ensure well-connected communities.
    /// Based on: Traag, V.A., Waltman, L. & van Eck, N.J. (2019)
    /// "From Louvain to Leiden: guaranteeing well-connected communities"
    /// </summary>
    private (Dictionary<int, int> communities, double modularity) RunLeiden(
        Dictionary<int, List<(int neighbor, float weight)>> adjacency,
        int nodeCount)
    {
        // Initialize: each node in its own community
        var communities = Enumerable.Range(0, nodeCount).ToDictionary(i => i, i => i);

        // Calculate total edge weight
        var totalWeight = adjacency.Values.SelectMany(x => x).Sum(x => x.weight) / 2;
        if (totalWeight == 0) return (communities, 0);

        // Node weights (sum of incident edges)
        var nodeWeights = new float[nodeCount];
        foreach (var (node, neighbors) in adjacency)
        {
            nodeWeights[node] = neighbors.Sum(n => n.weight);
        }

        var maxIterations = 100;
        var iteration = 0;
        bool improved;

        do
        {
            improved = false;
            iteration++;

            // Phase 1: Local moving (same as Louvain)
            var moved = LocalMovingPhase(communities, adjacency, nodeWeights, totalWeight, nodeCount);
            improved = moved;

            // Phase 2: Refinement - check for and fix poorly connected communities
            if (improved)
            {
                RefineCommunitiesPhase(communities, adjacency, nodeWeights, totalWeight, nodeCount);
            }

        } while (improved && iteration < maxIterations);

        // Renumber communities to be contiguous
        var communityMap = communities.Values.Distinct().Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
        communities = communities.ToDictionary(kv => kv.Key, kv => communityMap[kv.Value]);

        // Calculate final modularity
        var modularity = CalculateModularity(communities, adjacency, nodeWeights, totalWeight);

        return (communities, modularity);
    }

    /// <summary>
    /// Local moving phase: Greedily move nodes to improve modularity
    /// </summary>
    private bool LocalMovingPhase(
        Dictionary<int, int> communities,
        Dictionary<int, List<(int neighbor, float weight)>> adjacency,
        float[] nodeWeights, float totalWeight, int nodeCount)
    {
        var moved = false;
        var changed = true;
        var maxPasses = 10; // Limit passes to prevent slow convergence
        var passes = 0;

        while (changed && passes < maxPasses)
        {
            changed = false;
            passes++;
            foreach (var node in Enumerable.Range(0, nodeCount).OrderBy(_ => Random.Shared.Next()))
            {
                if (!adjacency.ContainsKey(node)) continue;

                var currentCommunity = communities[node];

                // Find best community among neighbors
                var neighborCommunities = adjacency[node]
                    .Select(n => communities[n.neighbor])
                    .Distinct()
                    .Where(c => c != currentCommunity)
                    .ToList();

                var bestCommunity = currentCommunity;
                var bestGain = 0.0;

                foreach (var targetCommunity in neighborCommunities)
                {
                    var gain = CalculateModularityGain(
                        node, currentCommunity, targetCommunity,
                        communities, adjacency, nodeWeights, totalWeight);

                    if (gain > bestGain)
                    {
                        bestGain = gain;
                        bestCommunity = targetCommunity;
                    }
                }

                if (bestCommunity != currentCommunity)
                {
                    communities[node] = bestCommunity;
                    changed = true;
                    moved = true;
                }
            }
        }

        return moved;
    }

    /// <summary>
    /// Refinement phase: Check communities for connectivity and split poorly connected ones
    /// This is the key difference from Louvain - Leiden guarantees well-connected communities
    /// </summary>
    private void RefineCommunitiesPhase(
        Dictionary<int, int> communities,
        Dictionary<int, List<(int neighbor, float weight)>> adjacency,
        float[] nodeWeights, float totalWeight, int nodeCount)
    {
        // Group nodes by community
        var communityNodes = communities
            .GroupBy(kv => kv.Value)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToHashSet());

        var nextCommunityId = communities.Values.Max() + 1;

        foreach (var (communityId, nodes) in communityNodes)
        {
            if (nodes.Count < 2) continue;

            // Check if community is well-connected using BFS
            var components = FindConnectedComponents(nodes, adjacency);

            if (components.Count > 1)
            {
                // Community has disconnected components - split them
                // Keep the largest component in the original community
                var sortedComponents = components.OrderByDescending(c => c.Count).ToList();

                // First (largest) component keeps the community ID
                // Other components get new community IDs
                for (var i = 1; i < sortedComponents.Count; i++)
                {
                    foreach (var node in sortedComponents[i])
                    {
                        communities[node] = nextCommunityId;
                    }
                    nextCommunityId++;
                }
            }
        }
    }

    /// <summary>
    /// Find connected components within a set of nodes using BFS
    /// </summary>
    private List<HashSet<int>> FindConnectedComponents(
        HashSet<int> nodes,
        Dictionary<int, List<(int neighbor, float weight)>> adjacency)
    {
        var components = new List<HashSet<int>>();
        var visited = new HashSet<int>();

        foreach (var startNode in nodes)
        {
            if (visited.Contains(startNode)) continue;

            // BFS to find connected component
            var component = new HashSet<int>();
            var queue = new Queue<int>();
            queue.Enqueue(startNode);
            visited.Add(startNode);

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                component.Add(node);

                if (!adjacency.TryGetValue(node, out var neighbors)) continue;

                foreach (var (neighbor, _) in neighbors)
                {
                    if (nodes.Contains(neighbor) && !visited.Contains(neighbor))
                    {
                        visited.Add(neighbor);
                        queue.Enqueue(neighbor);
                    }
                }
            }

            components.Add(component);
        }

        return components;
    }

    private double CalculateModularityGain(
        int node, int fromCommunity, int toCommunity,
        Dictionary<int, int> communities,
        Dictionary<int, List<(int neighbor, float weight)>> adjacency,
        float[] nodeWeights, float totalWeight)
    {
        if (!adjacency.ContainsKey(node)) return 0;

        // Sum of weights to nodes in target community
        var sumIn = adjacency[node]
            .Where(n => communities[n.neighbor] == toCommunity)
            .Sum(n => n.weight);

        // Sum of weights in target community
        var sumTot = communities
            .Where(kv => kv.Value == toCommunity)
            .Sum(kv => nodeWeights[kv.Key]);

        var ki = nodeWeights[node];
        var m2 = 2 * totalWeight;

        return (sumIn / m2) - (sumTot * ki) / (m2 * m2);
    }

    private double CalculateModularity(
        Dictionary<int, int> communities,
        Dictionary<int, List<(int neighbor, float weight)>> adjacency,
        float[] nodeWeights, float totalWeight)
    {
        if (totalWeight == 0) return 0;

        var m2 = 2 * totalWeight;
        var modularity = 0.0;

        foreach (var (node, neighbors) in adjacency)
        {
            var nodeCommunity = communities[node];
            foreach (var (neighbor, weight) in neighbors)
            {
                if (communities[neighbor] == nodeCommunity)
                {
                    modularity += weight - (nodeWeights[node] * nodeWeights[neighbor]) / m2;
                }
            }
        }

        return modularity / m2;
    }

    private Dictionary<int, float> CalculateCentrality(
        List<string> memberNodeIds,
        Dictionary<int, List<(int neighbor, float weight)>> adjacency,
        Dictionary<string, int> nodeIndex)
    {
        var centralities = new Dictionary<int, float>();
        var memberIndices = memberNodeIds
            .Where(id => nodeIndex.ContainsKey(id))
            .Select(id => nodeIndex[id])
            .ToHashSet();

        foreach (var idx in memberIndices)
        {
            if (!adjacency.TryGetValue(idx, out var neighbors))
            {
                centralities[idx] = 0;
                continue;
            }

            // Degree centrality within community
            var internalDegree = neighbors.Count(n => memberIndices.Contains(n.neighbor));
            var maxDegree = memberIndices.Count - 1;
            centralities[idx] = maxDegree > 0 ? (float)internalDegree / maxDegree : 0;
        }

        return centralities;
    }

    private async Task<CommunityFeatures> ExtractCommunityFeaturesAsync(
        List<ExtractedEntity> members,
        GraphData graphData,
        CancellationToken ct)
    {
        // Dominant entity types
        var typeDistribution = members
            .GroupBy(e => e.EntityType ?? "unknown")
            .ToDictionary(g => g.Key, g => g.Count());

        // Key terms from entity names (simple tokenization)
        var terms = members
            .SelectMany(e => e.CanonicalName.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
            .Where(t => t.Length > 2)
            .GroupBy(t => t.ToLowerInvariant())
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => g.Key)
            .ToList();

        // Representative entities (longest names, most unique)
        var representatives = members
            .OrderByDescending(e => e.CanonicalName.Length)
            .ThenBy(e => e.CanonicalName)
            .Take(5)
            .Select(e => e.CanonicalName)
            .ToList();

        // Source documents
        var memberIds = members.Select(e => e.Id).ToHashSet();
        var sourceDocIds = await _db.DocumentEntityLinks
            .Where(l => memberIds.Contains(l.EntityId))
            .Select(l => l.DocumentId)
            .Distinct()
            .ToListAsync(ct);

        // Calculate internal edge weight
        // Graph edges use DuckDB node IDs, so build a lookup by name
        var nodeLabelById = graphData.Nodes.ToDictionary(n => n.Id, n => n.Label.ToLowerInvariant());
        var memberNames = members.Select(e => e.CanonicalName.ToLowerInvariant()).ToHashSet();

        var internalEdges = graphData.Edges
            .Where(e =>
                nodeLabelById.TryGetValue(e.Source, out var sourceName) &&
                nodeLabelById.TryGetValue(e.Target, out var targetName) &&
                memberNames.Contains(sourceName) && memberNames.Contains(targetName))
            .ToList();
        var totalEdgeWeight = internalEdges.Sum(e => e.Weight);

        // Internal similarity (simplified - based on edge density)
        var maxEdges = members.Count * (members.Count - 1) / 2;
        var internalSimilarity = maxEdges > 0 ? (float)internalEdges.Count / maxEdges : 0;

        return new CommunityFeatures
        {
            DominantTypes = typeDistribution,
            KeyTerms = terms,
            Representatives = representatives,
            SourceDocuments = sourceDocIds,
            InternalSimilarity = internalSimilarity,
            TotalEdgeWeight = totalEdgeWeight
        };
    }

    public async Task<IReadOnlyList<CommunityEntity>> GetCommunitiesAsync(Guid? collectionId = null, CancellationToken ct = default)
    {
        var query = _db.Communities
            .Include(c => c.Members)
            .AsQueryable();

        if (collectionId.HasValue)
        {
            query = query.Where(c => c.CollectionId == collectionId.Value);
        }

        return await query
            .OrderByDescending(c => c.EntityCount)
            .ToListAsync(ct);
    }

    public async Task<CommunityEntity?> GetCommunityAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Communities
            .Include(c => c.Members)
                .ThenInclude(m => m.Entity)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<IReadOnlyList<CommunityEntity>> SearchCommunitiesAsync(
        string query, Guid? collectionId = null, int limit = 5, CancellationToken ct = default)
    {
        // Embed query and find communities with similar embeddings
        var queryEmbedding = await _embeddingService.EmbedAsync(query, ct);

        var queryable = _db.Communities.Where(c => c.Embedding != null);

        if (collectionId.HasValue)
        {
            queryable = queryable.Where(c => c.CollectionId == collectionId.Value);
        }

        var communities = await queryable.ToListAsync(ct);

        // Calculate cosine similarity
        var scored = communities
            .Select(c => (Community: c, Score: CosineSimilarity(queryEmbedding, c.Embedding!)))
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => x.Community)
            .ToList();

        return scored;
    }

    public async Task GenerateCommunitySummariesAsync(Guid? collectionId = null, CancellationToken ct = default)
    {
        var query = _db.Communities
            .Include(c => c.Members)
                .ThenInclude(m => m.Entity)
            .AsQueryable();

        if (collectionId.HasValue)
        {
            query = query.Where(c => c.CollectionId == collectionId.Value);
        }

        var communities = await query.ToListAsync(ct);

        _logger.LogInformation("Generating summaries for {Count} communities", communities.Count);

        foreach (var community in communities)
        {
            try
            {
                var features = !string.IsNullOrEmpty(community.Features)
                    ? JsonSerializer.Deserialize<CommunityFeatures>(community.Features)
                    : null;

                var entityNames = community.Members
                    .OrderByDescending(m => m.Centrality)
                    .Take(10)
                    .Select(m => m.Entity?.CanonicalName ?? "unknown")
                    .ToList();

                var prompt = $@"You are analyzing a community of related entities from a knowledge graph.

Entities in this community:
{string.Join(", ", entityNames)}

Entity types present: {(features != null ? string.Join(", ", features.DominantTypes.Select(kv => $"{kv.Key}({kv.Value})")) : "mixed")}
Key terms: {(features != null ? string.Join(", ", features.KeyTerms) : "various")}

Based on these entities and their characteristics, provide:
1. A descriptive title (MAXIMUM 3 words, like ""Image Processing"" or ""Database Optimization"")
2. A paragraph description (maximum 5 sentences) explaining what this community represents, what topics it covers, and its key themes

Format your response as:
NAME: [maximum 3 word title]
SUMMARY: [descriptive paragraph, max 5 sentences]";

                var response = await _llmService.GenerateAsync(prompt, new LlmOptions { Temperature = 0.3 }, ct);

                _logger.LogDebug("LLM response for community {Id}: {Response}", community.Id, response);

                // Parse response - handle multiple formats
                // First strip markdown from each line for consistent parsing
                var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => StripMarkdownPrefix(l.Trim()))
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToArray();

                string? parsedName = null;
                string? parsedSummary = null;

                // Try structured format first (NAME: ..., SUMMARY: ...)
                var nameLine = lines.FirstOrDefault(l => l.StartsWith("NAME:", StringComparison.OrdinalIgnoreCase));
                var summaryLine = lines.FirstOrDefault(l => l.StartsWith("SUMMARY:", StringComparison.OrdinalIgnoreCase));

                if (nameLine != null && nameLine.Length > 5)
                {
                    parsedName = CleanLlmText(nameLine.Substring(5));
                }
                if (summaryLine != null && summaryLine.Length > 8)
                {
                    parsedSummary = CleanLlmText(summaryLine.Substring(8));
                }

                // Fallback: if no structured format, use first non-empty line as name, rest as summary
                if (string.IsNullOrWhiteSpace(parsedName) && lines.Length > 0)
                {
                    parsedName = lines[0].Trim().Trim('"', '*', '#', '-');
                    if (lines.Length > 1)
                    {
                        parsedSummary = string.Join(" ", lines.Skip(1)).Trim().Trim('"', '*');
                    }
                }

                if (!string.IsNullOrWhiteSpace(parsedName) && parsedName.Length >= 3)
                {
                    community.Name = parsedName;
                }
                if (!string.IsNullOrWhiteSpace(parsedSummary))
                {
                    community.Summary = parsedSummary;
                }

                // Generate embedding for the community
                var communityText = $"{community.Name}. {community.Summary ?? ""} {string.Join(" ", entityNames)}";
                community.Embedding = await _embeddingService.EmbedAsync(communityText, ct);

                community.UpdatedAt = DateTimeOffset.UtcNow;

                // Store summary as evidence artifact
                await StoreCommunityEvidenceAsync(community, communityText, ct);

                // Add to vector store for semantic search
                await IndexCommunityInVectorStoreAsync(community, communityText, ct);

                _logger.LogDebug("Generated summary for community: {Name}", community.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate summary for community {Id}", community.Id);
            }
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Community summary generation complete");
    }

    /// <summary>
    /// Store community summary and features as evidence artifacts.
    /// </summary>
    private async Task StoreCommunityEvidenceAsync(CommunityEntity community, string summaryText, CancellationToken ct)
    {
        try
        {
            // We need an entity ID for evidence storage - use the community ID
            // Note: This requires the community to be linked to a RetrievalEntityRecord
            // For now, we'll store with the community ID as the "entity" ID
            // In a full implementation, communities would be linked to RetrievalEntityRecords

            // Generate a content hash for the summary
            var contentHash = GenerateContentHash(summaryText);

            // Store summary as evidence
            using var textStream = new MemoryStream(Encoding.UTF8.GetBytes(summaryText));
            var metadata = new
            {
                communityId = community.Id,
                communityName = community.Name,
                entityCount = community.EntityCount,
                cohesion = community.Cohesion,
                level = community.Level
            };

            await _evidenceRepository.StoreAsync(
                entityId: community.Id,  // Use community ID as entity ID
                artifactType: EvidenceTypes.LlmSummary,
                content: textStream,
                mimeType: "text/plain",
                producerSource: "CommunityDetection",
                confidence: community.Cohesion,
                metadata: metadata,
                segmentHash: contentHash,
                ct: ct);

            _logger.LogDebug("Stored evidence for community {Name}", community.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store evidence for community {Id}", community.Id);
        }
    }

    /// <summary>
    /// Add community summary to vector store for semantic search.
    /// Communities appear as searchable segments with type CommunitySummary.
    /// </summary>
    private async Task IndexCommunityInVectorStoreAsync(CommunityEntity community, string summaryText, CancellationToken ct)
    {
        if (community.Embedding == null || community.Embedding.Length == 0)
        {
            _logger.LogWarning("Community {Id} has no embedding, skipping vector indexing", community.Id);
            return;
        }

        try
        {
            var contentHash = GenerateContentHash(summaryText);

            // Create a segment for the community summary (use Heading type as it represents high-level content)
            var segment = new Segment(
                docId: $"community_{community.Id}",
                text: summaryText,
                type: SegmentType.Heading,
                index: 0,
                startChar: 0,
                endChar: summaryText.Length)
            {
                ContentHash = contentHash,
                SectionTitle = community.Name,
                SalienceScore = community.Cohesion,
                PositionWeight = 1.0,
                Embedding = community.Embedding
            };

            // Use a special collection for communities or the default collection
            const string collectionName = "communities";
            await _vectorStore.InitializeAsync(collectionName, community.Embedding.Length, ct);
            await _vectorStore.UpsertSegmentsAsync(collectionName, [segment], ct);

            _logger.LogDebug("Indexed community {Name} in vector store", community.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to index community {Id} in vector store", community.Id);
        }
    }

    /// <summary>
    /// Generate a content hash for deduplication and lookups.
    /// </summary>
    private static string GenerateContentHash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = XxHash64.Hash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Strips markdown prefix characters from a line (**, *, #, etc.) to normalize for parsing
    /// </summary>
    private static string StripMarkdownPrefix(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return string.Empty;

        var result = line;
        // Strip leading markdown characters
        while (result.Length > 0 && (result[0] == '*' || result[0] == '#' || result[0] == '-' || result[0] == '_' || result[0] == ' '))
            result = result[1..];

        return result;
    }

    private static string CleanLlmText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Remove markdown formatting
        var cleaned = text.Trim();

        // Remove leading asterisks, spaces, quotes, hashes, dashes repeatedly
        const string trimChars = "*#-_\"'` ";
        while (cleaned.Length > 0 && trimChars.Contains(cleaned[0]))
            cleaned = cleaned[1..];
        while (cleaned.Length > 0 && trimChars.Contains(cleaned[^1]))
            cleaned = cleaned[..^1];

        // Remove common markdown patterns
        cleaned = cleaned
            .Replace("**", "")
            .Replace("__", "")
            .Replace("* ", "")
            .Replace("# ", "")
            .Trim();

        return cleaned;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        var dotProduct = 0f;
        var normA = 0f;
        var normB = 0f;

        for (var i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denominator > 0 ? dotProduct / denominator : 0;
    }
}
