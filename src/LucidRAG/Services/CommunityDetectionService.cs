using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LucidRAG.Data;
using LucidRAG.Entities;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.Services;

/// <summary>
/// Service for detecting communities in the entity graph and extracting their features.
/// Uses Louvain algorithm for community detection and LLM for summary generation.
/// </summary>
public interface ICommunityDetectionService
{
    /// <summary>
    /// Detect communities in the current entity graph
    /// </summary>
    Task<CommunityDetectionResult> DetectCommunitiesAsync(CancellationToken ct = default);

    /// <summary>
    /// Get all detected communities
    /// </summary>
    Task<IReadOnlyList<CommunityEntity>> GetCommunitiesAsync(CancellationToken ct = default);

    /// <summary>
    /// Get community by ID with members
    /// </summary>
    Task<CommunityEntity?> GetCommunityAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Find communities relevant to a query
    /// </summary>
    Task<IReadOnlyList<CommunityEntity>> SearchCommunitiesAsync(string query, int limit = 5, CancellationToken ct = default);

    /// <summary>
    /// Generate/regenerate summaries for communities using LLM
    /// </summary>
    Task GenerateCommunitySummariesAsync(CancellationToken ct = default);
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
    private readonly ILogger<CommunityDetectionService> _logger;

    public CommunityDetectionService(
        RagDocumentsDbContext db,
        IEntityGraphService graphService,
        ILlmService llmService,
        IEmbeddingService embeddingService,
        ILogger<CommunityDetectionService> logger)
    {
        _db = db;
        _graphService = graphService;
        _llmService = llmService;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task<CommunityDetectionResult> DetectCommunitiesAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("Starting community detection...");

        // Get graph data
        var graphData = await _graphService.GetGraphDataAsync(null, ct);
        if (graphData.Nodes.Count == 0)
        {
            _logger.LogWarning("No entities in graph, skipping community detection");
            return new CommunityDetectionResult(0, 0, 0, sw.Elapsed);
        }

        _logger.LogInformation("Running Louvain on {NodeCount} nodes, {EdgeCount} edges",
            graphData.Nodes.Count, graphData.Edges.Count);

        // Build adjacency structure for Louvain
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

        // Run Louvain algorithm
        var (communities, modularity) = RunLouvain(adjacency, graphData.Nodes.Count);

        _logger.LogInformation("Louvain found {CommunityCount} communities with modularity {Modularity:F3}",
            communities.Values.Distinct().Count(), modularity);

        // Clear existing communities
        _db.CommunityMemberships.RemoveRange(_db.CommunityMemberships);
        _db.Communities.RemoveRange(_db.Communities);
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
                Name = $"Community {idx + 1}: {string.Join(", ", topEntities)}",
                Algorithm = "louvain",
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
    /// Louvain community detection algorithm
    /// </summary>
    private (Dictionary<int, int> communities, double modularity) RunLouvain(
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

        // Iteratively optimize modularity
        bool improved;
        var maxIterations = 100;
        var iteration = 0;

        do
        {
            improved = false;
            iteration++;

            foreach (var node in Enumerable.Range(0, nodeCount).OrderBy(_ => Random.Shared.Next()))
            {
                if (!adjacency.ContainsKey(node)) continue;

                var currentCommunity = communities[node];

                // Calculate modularity gain for moving to each neighbor's community
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
                    improved = true;
                }
            }
        } while (improved && iteration < maxIterations);

        // Renumber communities to be contiguous
        var communityMap = communities.Values.Distinct().Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
        communities = communities.ToDictionary(kv => kv.Key, kv => communityMap[kv.Value]);

        // Calculate final modularity
        var modularity = CalculateModularity(communities, adjacency, nodeWeights, totalWeight);

        return (communities, modularity);
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

    public async Task<IReadOnlyList<CommunityEntity>> GetCommunitiesAsync(CancellationToken ct = default)
    {
        return await _db.Communities
            .Include(c => c.Members)
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
        string query, int limit = 5, CancellationToken ct = default)
    {
        // Embed query and find communities with similar embeddings
        var queryEmbedding = await _embeddingService.EmbedAsync(query, ct);

        var communities = await _db.Communities
            .Where(c => c.Embedding != null)
            .ToListAsync(ct);

        // Calculate cosine similarity
        var scored = communities
            .Select(c => (Community: c, Score: CosineSimilarity(queryEmbedding, c.Embedding!)))
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => x.Community)
            .ToList();

        return scored;
    }

    public async Task GenerateCommunitySummariesAsync(CancellationToken ct = default)
    {
        var communities = await _db.Communities
            .Include(c => c.Members)
                .ThenInclude(m => m.Entity)
            .ToListAsync(ct);

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
1. A concise name for this community (3-5 words, like ""Image Processing Techniques"" or ""Database Query Optimization"")
2. A one-sentence summary of what this community represents

Format your response as:
NAME: [community name]
SUMMARY: [one sentence description]";

                var response = await _llmService.GenerateAsync(prompt, new LlmOptions { Temperature = 0.3 }, ct);

                _logger.LogDebug("LLM response for community {Id}: {Response}", community.Id, response);

                // Parse response - handle multiple formats
                var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToArray();

                string? parsedName = null;
                string? parsedSummary = null;

                // Try structured format first (NAME: ..., SUMMARY: ...)
                var nameLine = lines.FirstOrDefault(l => l.StartsWith("NAME:", StringComparison.OrdinalIgnoreCase));
                var summaryLine = lines.FirstOrDefault(l => l.StartsWith("SUMMARY:", StringComparison.OrdinalIgnoreCase));

                if (nameLine != null)
                {
                    parsedName = CleanLlmText(nameLine.Substring(5));
                }
                if (summaryLine != null)
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

    private static string CleanLlmText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Remove markdown formatting
        var cleaned = text.Trim();

        // Remove leading/trailing asterisks, quotes, hashes, dashes
        while (cleaned.Length > 0 && "*#-_\"'`".Contains(cleaned[0]))
            cleaned = cleaned[1..];
        while (cleaned.Length > 0 && "*#-_\"'`".Contains(cleaned[^1]))
            cleaned = cleaned[..^1];

        // Remove common markdown patterns
        cleaned = cleaned.Replace("**", "").Replace("__", "").Trim();

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
